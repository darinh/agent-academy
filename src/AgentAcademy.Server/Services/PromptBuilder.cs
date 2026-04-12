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
}
