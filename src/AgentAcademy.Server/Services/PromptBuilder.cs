using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Pure-function prompt construction for agent conversations, breakout rooms,
/// and review cycles. Extracted from AgentOrchestrator to enable independent testing.
/// </summary>
internal static class PromptBuilder
{
    /// <summary>
    /// Builds the prompt for a main-room conversation round.
    /// Does NOT include agent.StartupPrompt — that's sent as session priming
    /// in CopilotExecutor.GetOrCreateSessionEntryAsync.
    /// </summary>
    internal static string BuildConversationPrompt(
        AgentDefinition agent, RoomSnapshot room, string? specContext,
        List<TaskItem>? activeTaskItems = null,
        List<AgentMemory>? memories = null,
        List<MessageEntity>? directMessages = null,
        string? sessionSummary = null,
        string? sprintPreamble = null,
        string? specVersion = null)
    {
        var lines = new List<string> { PromptSanitizer.BoundaryInstruction, "" };

        if (!string.IsNullOrEmpty(sprintPreamble))
        {
            lines.Add(sprintPreamble);
        }

        if (!string.IsNullOrEmpty(sessionSummary))
        {
            lines.Add("=== PREVIOUS CONVERSATION SUMMARY ===");
            lines.Add(PromptSanitizer.WrapBlock(sessionSummary));
            lines.Add("");
        }

        AppendMemories(lines, memories, agent.Id);

        lines.Add("=== CURRENT ROOM CONTEXT ===");
        lines.Add($"Room: {PromptSanitizer.SanitizeMetadata(room.Name)}");

        if (room.ActiveTask is not null)
        {
            lines.Add("");
            lines.Add("=== TASK ===");
            lines.Add(PromptSanitizer.WrapBlock(
                $"Title: {room.ActiveTask.Title}\n" +
                $"Description: {room.ActiveTask.Description}" +
                (string.IsNullOrEmpty(room.ActiveTask.SuccessCriteria)
                    ? ""
                    : $"\nSuccess criteria: {room.ActiveTask.SuccessCriteria}")));
        }

        if (activeTaskItems is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("=== IN-FLIGHT WORK ITEMS ===");
            lines.Add(PromptSanitizer.ContentMarkerOpen);
            foreach (var item in activeTaskItems)
            {
                var workspace = item.BreakoutRoomId is not null ? " [in workspace]" : "";
                lines.Add($"- [{item.Status}] \"{PromptSanitizer.SanitizeMetadata(item.Title)}\" → assigned to {PromptSanitizer.SanitizeMetadata(item.AssignedTo)}{workspace}");
            }
            lines.Add(PromptSanitizer.ContentMarkerClose);
        }

        if (specContext is not null)
        {
            var versionTag = specVersion is not null ? $" (v{specVersion})" : "";
            lines.Add("");
            lines.Add($"=== PROJECT SPECIFICATION{versionTag} ===");
            lines.Add("The project maintains a living spec in specs/. Relevant sections:");
            lines.Add(specContext);
        }

        if (room.RecentMessages.Count > 0)
        {
            lines.Add("");
            lines.Add("=== RECENT CONVERSATION ===");
            lines.Add(PromptSanitizer.ContentMarkerOpen);
            foreach (var msg in room.RecentMessages.TakeLast(20))
            {
                lines.Add($"[{PromptSanitizer.SanitizeMetadata(msg.SenderName)} ({PromptSanitizer.SanitizeMetadata(msg.SenderRole ?? msg.SenderKind.ToString())})]: {PromptSanitizer.EscapeMarkers(msg.Content)}");
            }
            lines.Add(PromptSanitizer.ContentMarkerClose);
        }

        AppendDirectMessages(lines, directMessages, agent.Id);

        lines.Add("");
        lines.Add("=== YOUR TURN ===");
        lines.Add($"You are {agent.Name} ({agent.Role}).");
        lines.Add("Respond naturally to the conversation. Be concise and actionable.");
        lines.Add("If you have nothing meaningful to contribute, reply with exactly: PASS");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds the prompt for a breakout room work round.
    /// Does NOT include agent.StartupPrompt — sent as session priming.
    /// </summary>
    internal static string BuildBreakoutPrompt(
        AgentDefinition agent, BreakoutRoom br, int round,
        List<AgentMemory>? memories = null,
        List<MessageEntity>? directMessages = null,
        string? sessionSummary = null,
        string? specContext = null,
        string? specVersion = null)
    {
        var lines = new List<string> { PromptSanitizer.BoundaryInstruction, "" };

        if (!string.IsNullOrEmpty(sessionSummary))
        {
            lines.Add("=== PREVIOUS WORK SUMMARY ===");
            lines.Add(PromptSanitizer.WrapBlock(sessionSummary));
            lines.Add("");
        }

        AppendMemories(lines, memories, agent.Id);

        lines.Add($"=== BREAKOUT ROOM: {PromptSanitizer.SanitizeMetadata(br.Name)} ===");
        lines.Add($"Round: {round}");

        if (specContext is not null)
        {
            var versionTag = specVersion is not null ? $" (v{specVersion})" : "";
            lines.Add("");
            lines.Add($"=== PROJECT SPECIFICATIONS{versionTag} ===");
            lines.Add("Sections marked with ★ are linked to your current task.");
            lines.Add(specContext);
        }

        if (br.Tasks.Count > 0)
        {
            lines.Add("");
            lines.Add("=== ASSIGNED TASKS ===");
            lines.Add(PromptSanitizer.ContentMarkerOpen);
            foreach (var task in br.Tasks)
            {
                lines.Add($"Task: {PromptSanitizer.EscapeMarkers(task.Title)}");
                lines.Add($"Description: {PromptSanitizer.EscapeMarkers(task.Description)}");
                lines.Add($"Status: {task.Status}");
                lines.Add("");
            }
            lines.Add(PromptSanitizer.ContentMarkerClose);
        }

        if (br.RecentMessages.Count > 0)
        {
            lines.Add("=== WORK LOG ===");
            lines.Add(PromptSanitizer.ContentMarkerOpen);
            foreach (var msg in br.RecentMessages.TakeLast(10))
            {
                lines.Add($"[{PromptSanitizer.SanitizeMetadata(msg.SenderName)}]: {PromptSanitizer.EscapeMarkers(msg.Content)}");
            }
            lines.Add(PromptSanitizer.ContentMarkerClose);
        }

        AppendDirectMessages(lines, directMessages, agent.Id);

        lines.Add("");
        lines.Add("=== YOUR TURN ===");
        lines.Add($"You are {agent.Name} ({agent.Role}).");
        lines.Add("Do the work described in your tasks. Create files, write code, execute commands.");
        lines.Add("When your work is complete, include a WORK REPORT block:");
        lines.Add("WORK REPORT:");
        lines.Add("Status: COMPLETE");
        lines.Add("Files: [list of created/modified files]");
        lines.Add("Evidence: [description of what was done and how it meets criteria]");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds the prompt for a review round. Unlike conversation/breakout prompts,
    /// this DOES include the reviewer's StartupPrompt.
    /// </summary>
    internal static string BuildReviewPrompt(
        AgentDefinition reviewer, string agentName, string workReport, string? specContext, string? specVersion = null)
    {
        var lines = new List<string> { reviewer.StartupPrompt, "", PromptSanitizer.BoundaryInstruction, "" };
        lines.Add("=== REVIEW REQUEST ===");
        lines.Add($"{PromptSanitizer.SanitizeMetadata(agentName)} has completed work and is presenting their results.");
        lines.Add("");
        lines.Add("=== WORK REPORT ===");
        lines.Add(PromptSanitizer.WrapBlock(workReport));

        if (specContext is not null)
        {
            var versionTag = specVersion is not null ? $" (v{specVersion})" : "";
            lines.Add("");
            lines.Add($"=== SPEC SECTIONS{versionTag} (verify accuracy against delivered work) ===");
            lines.Add(specContext);
        }

        lines.Add("");
        lines.Add("=== YOUR TURN ===");
        lines.Add($"You are {reviewer.Name} ({reviewer.Role}).");
        lines.Add("Review the work report and provide your assessment.");
        lines.Add("End your review with a REVIEW: block:");
        lines.Add("REVIEW:");
        lines.Add("Verdict: APPROVED | NEEDS FIX");
        lines.Add("Findings:");
        lines.Add("- [list any issues or commendations]");
        if (specContext is not null)
        {
            lines.Add("Spec Accuracy:");
            lines.Add("- [PASS/FAIL] Do spec updates match the delivered implementation?");
            lines.Add("- [list any spec-code discrepancies found]");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a plan document from a parsed task assignment.
    /// Falls back to the title when description is blank.
    /// </summary>
    internal static string BuildAssignmentPlanContent(ParsedTaskAssignment assignment)
    {
        var lines = new List<string>
        {
            $"# {assignment.Title}",
            "",
            "## Objective",
            string.IsNullOrWhiteSpace(assignment.Description)
                ? assignment.Title
                : assignment.Description.Trim()
        };

        if (assignment.Criteria.Count > 0)
        {
            lines.Add("");
            lines.Add("## Acceptance Criteria");
            lines.AddRange(assignment.Criteria.Select(c => $"- {c}"));
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a brief summary of tasks for an agent, optionally including the branch name.
    /// </summary>
    internal static string BuildTaskBrief(AgentDefinition agent, List<TaskItem> tasks, string? taskBranch = null)
    {
        var lines = new List<string>
        {
            $"Task Brief for {agent.Name}",
            new('=', 40)
        };
        if (taskBranch != null)
            lines.Add($"Branch: {taskBranch}");
        foreach (var task in tasks)
        {
            lines.Add($"\nTask: {task.Title}");
            lines.Add($"Description: {task.Description}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// A memory is stale if it hasn't been accessed in 30+ days and has no explicit TTL.
    /// </summary>
    internal static bool IsMemoryStale(AgentMemory m)
    {
        if (m.ExpiresAt.HasValue) return false;
        var lastActivity = m.LastAccessedAt ?? m.UpdatedAt ?? m.CreatedAt;
        return (DateTime.UtcNow - lastActivity).TotalDays >= 30;
    }

    // ── Shared Helpers ──────────────────────────────────────────

    /// <summary>
    /// Appends memory sections (own + shared) to a prompt's line list.
    /// Shared memories authored by the current agent go under YOUR MEMORIES.
    /// </summary>
    private static void AppendMemories(List<string> lines, List<AgentMemory>? memories, string agentId)
    {
        if (memories is not { Count: > 0 }) return;

        var ownMemories = memories.Where(m => m.Category != "shared" || m.AgentId == agentId).ToList();
        var sharedMemories = memories.Where(m => m.Category == "shared" && m.AgentId != agentId).ToList();

        if (ownMemories.Count > 0)
        {
            lines.Add("=== YOUR MEMORIES ===");
            lines.Add(PromptSanitizer.ContentMarkerOpen);
            foreach (var m in ownMemories)
            {
                var staleTag = IsMemoryStale(m) ? " ⚠️STALE" : "";
                var ttlTag = m.ExpiresAt.HasValue ? $" [expires {m.ExpiresAt.Value:yyyy-MM-dd}]" : "";
                lines.Add($"[{PromptSanitizer.SanitizeMetadata(m.Category)}] {PromptSanitizer.SanitizeMetadata(m.Key)}: {PromptSanitizer.EscapeMarkers(m.Value)}{staleTag}{ttlTag}");
            }
            lines.Add(PromptSanitizer.ContentMarkerClose);
            lines.Add("");
        }

        if (sharedMemories.Count > 0)
        {
            lines.Add("=== SHARED KNOWLEDGE ===");
            lines.Add(PromptSanitizer.ContentMarkerOpen);
            foreach (var m in sharedMemories)
            {
                var staleTag = IsMemoryStale(m) ? " ⚠️STALE" : "";
                lines.Add($"[shared] {PromptSanitizer.SanitizeMetadata(m.Key)}: {PromptSanitizer.EscapeMarkers(m.Value)} (from: {PromptSanitizer.SanitizeMetadata(m.AgentId)}){staleTag}");
            }
            lines.Add(PromptSanitizer.ContentMarkerClose);
            lines.Add("");
        }
    }

    /// <summary>
    /// Appends a direct messages section to a prompt's line list.
    /// </summary>
    private static void AppendDirectMessages(List<string> lines, List<MessageEntity>? directMessages, string agentId)
    {
        if (directMessages is not { Count: > 0 }) return;

        lines.Add("");
        lines.Add("=== DIRECT MESSAGES ===");
        lines.Add("These are private messages only you can see. Reply via DM command if needed.");
        lines.Add(PromptSanitizer.ContentMarkerOpen);
        foreach (var dm in directMessages)
        {
            var direction = dm.SenderId == agentId
                ? $"[DM to {PromptSanitizer.SanitizeMetadata(dm.RecipientId)}]"
                : $"[DM from {PromptSanitizer.SanitizeMetadata(dm.SenderName)}]";
            lines.Add($"{direction}: {PromptSanitizer.EscapeMarkers(dm.Content)}");
        }
        lines.Add(PromptSanitizer.ContentMarkerClose);
    }

    /// <summary>
    /// Builds the prompt for a post-task retrospective. The agent reflects on
    /// the completed task, identifies learnings, and stores them via REMEMBER.
    /// </summary>
    internal static string BuildRetrospectivePrompt(
        AgentDefinition agent, RetrospectiveContext context)
    {
        var lines = new List<string>
        {
            PromptSanitizer.BoundaryInstruction,
            "",
            "=== POST-TASK RETROSPECTIVE ===",
            "",
            $"You ({agent.Name}, {agent.Role}) have just completed a task. Take a moment to reflect on the work and capture learnings that will help you and the team in future tasks.",
            "",
            "=== COMPLETED TASK ===",
            $"Title: {PromptSanitizer.SanitizeMetadata(context.Title)}",
            $"Type: {context.TaskType ?? "Unknown"}",
            $"Description: {PromptSanitizer.EscapeMarkers(context.Description)}"
        };

        if (!string.IsNullOrEmpty(context.SuccessCriteria))
            lines.Add($"Success Criteria: {PromptSanitizer.EscapeMarkers(context.SuccessCriteria)}");

        lines.Add("");
        lines.Add("=== TASK METRICS ===");
        lines.Add($"Review rounds: {context.ReviewRounds}");
        lines.Add($"Commit count: {context.CommitCount}");

        if (context.CycleTime.HasValue)
        {
            var ct = context.CycleTime.Value;
            var cycleStr = ct.TotalHours >= 1
                ? $"{ct.TotalHours:F1} hours"
                : $"{ct.TotalMinutes:F0} minutes";
            lines.Add($"Cycle time: {cycleStr}");
        }

        // Include review feedback if any
        if (context.ReviewMessages.Count > 0)
        {
            lines.Add("");
            lines.Add("=== REVIEW FEEDBACK ===");
            lines.Add(PromptSanitizer.ContentMarkerOpen);
            foreach (var msg in context.ReviewMessages.TakeLast(5))
            {
                lines.Add($"[{PromptSanitizer.SanitizeMetadata(msg.Author)}]: {PromptSanitizer.EscapeMarkers(msg.Content)}");
            }
            lines.Add(PromptSanitizer.ContentMarkerClose);
        }

        // Include task comments (findings, evidence, blockers)
        var relevantComments = context.Comments
            .Where(c => c.Type is "Finding" or "Evidence" or "Blocker")
            .ToList();
        if (relevantComments.Count > 0)
        {
            lines.Add("");
            lines.Add("=== NOTABLE COMMENTS ===");
            lines.Add(PromptSanitizer.ContentMarkerOpen);
            foreach (var comment in relevantComments.TakeLast(10))
            {
                lines.Add($"[{comment.Type}] {PromptSanitizer.SanitizeMetadata(comment.Author)}: {PromptSanitizer.EscapeMarkers(comment.Content)}");
            }
            lines.Add(PromptSanitizer.ContentMarkerClose);
        }

        lines.Add("");
        lines.Add("=== INSTRUCTIONS ===");
        lines.Add("Reflect on this task and produce two outputs:");
        lines.Add("");
        lines.Add("1. **REMEMBER commands** (2-5) to store the most valuable learnings. Use these exact categories:");
        lines.Add("   - `lesson` — general lessons from the experience");
        lines.Add("   - `pattern` — code or design patterns discovered in the codebase");
        lines.Add("   - `gotcha` — surprising behavior or non-obvious constraints");
        lines.Add("   - `incident` — mistakes to avoid repeating");
        lines.Add("   - `decision` — architectural decisions with rationale");
        lines.Add("");
        lines.Add("   Format each REMEMBER exactly like this:");
        lines.Add("   ```");
        lines.Add("   REMEMBER:");
        lines.Add("     category: lesson");
        lines.Add("     key: descriptive-kebab-case-key");
        lines.Add("     value: Concise description of the learning. Include enough context to be useful without the original task.");
        lines.Add("   ```");
        lines.Add("");
        lines.Add("2. **Retrospective summary** — a brief (3-5 sentence) summary covering:");
        lines.Add("   - What went well (patterns to repeat)");
        lines.Add("   - What was challenging (to anticipate next time)");
        lines.Add("   - Key insight for the team");
        lines.Add("");
        if (context.ReviewRounds > 1)
        {
            lines.Add($"NOTE: This task required {context.ReviewRounds} review rounds. Pay special attention to what caused review iterations — this is high-value learning.");
            lines.Add("");
        }
        lines.Add("Focus on learnings specific to THIS task and THIS codebase. Avoid generic advice. Only REMEMBER things you discovered that would not be obvious to a new agent working on a similar task.");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Builds the prompt for a learning digest. The planner reviews
    /// retrospective summaries and identifies cross-cutting patterns
    /// to store as shared memories.
    /// </summary>
    internal static string BuildDigestPrompt(
        AgentDefinition agent, List<DigestRetrospective> retrospectives)
    {
        var lines = new List<string>
        {
            PromptSanitizer.BoundaryInstruction,
            "",
            "=== LEARNING DIGEST ===",
            "",
            $"You ({agent.Name}, {agent.Role}) are reviewing retrospective summaries from your team's recently completed tasks. Your goal: identify cross-cutting patterns and synthesize them into shared knowledge that benefits all agents.",
            "",
            $"=== RETROSPECTIVE SUMMARIES ({retrospectives.Count} tasks) ==="
        };

        lines.Add(PromptSanitizer.ContentMarkerOpen);
        foreach (var retro in retrospectives)
        {
            lines.Add($"--- Task: {PromptSanitizer.SanitizeMetadata(retro.TaskTitle)} (by {PromptSanitizer.SanitizeMetadata(retro.AgentName)}, {retro.CreatedAt:yyyy-MM-dd}) ---");
            lines.Add(PromptSanitizer.EscapeMarkers(retro.Content));
            lines.Add("");
        }
        lines.Add(PromptSanitizer.ContentMarkerClose);

        lines.Add("");
        lines.Add("=== INSTRUCTIONS ===");
        lines.Add("Analyze the retrospectives above and produce two outputs:");
        lines.Add("");
        lines.Add("1. **REMEMBER commands** (3-8) to store cross-cutting learnings as **shared** knowledge. These will be visible to ALL agents on the team.");
        lines.Add("");
        lines.Add("   Rules:");
        lines.Add("   - EVERY REMEMBER must use `category: shared` — no exceptions");
        lines.Add("   - Focus on patterns that appear across MULTIPLE retrospectives");
        lines.Add("   - Merge redundant agent-specific learnings into unified shared knowledge");
        lines.Add("   - Identify contradictions between agents' learnings and resolve them");
        lines.Add("   - Skip one-off observations that only apply to a single task");
        lines.Add("");
        lines.Add("   Format each REMEMBER exactly like this:");
        lines.Add("   ```");
        lines.Add("   REMEMBER:");
        lines.Add("     category: shared");
        lines.Add("     key: descriptive-kebab-case-key");
        lines.Add("     value: Concise, actionable learning. Include enough context to be useful without the original tasks.");
        lines.Add("   ```");
        lines.Add("");
        lines.Add("2. **Digest summary** — a brief (3-5 sentence) overview covering:");
        lines.Add("   - Top recurring themes across the retrospectives");
        lines.Add("   - Any contradictions or tensions discovered");
        lines.Add("   - Recommended process improvements for the team");
        lines.Add("");
        lines.Add("Focus on actionable, specific insights. Avoid restating what individual retrospectives already said. Your value is synthesis — connecting dots across tasks that individual agents can't see.");

        return string.Join('\n', lines);
    }
}
