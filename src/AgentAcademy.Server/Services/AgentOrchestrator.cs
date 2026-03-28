using System.Text.RegularExpressions;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Drives the multi-agent conversation lifecycle: queue-based message
/// processing, conversation rounds, breakout room workflows, and review
/// cycles. Ported from v1 TypeScript CollaborationOrchestrator.
/// </summary>
public sealed class AgentOrchestrator
{
    /// <summary>Timeout for each agent turn in the main conversation room.</summary>
    private static readonly TimeSpan McTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Timeout for agent work in a breakout room.</summary>
    private static readonly TimeSpan BreakoutTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Maximum iterations an agent gets inside a breakout room.</summary>
    private const int MaxBreakoutRounds = 5;

    /// <summary>Extra rounds granted after a reviewer requests fixes.</summary>
    private const int MaxFixRounds = 2;

    /// <summary>Cap on the number of agents that can be tagged in one round.</summary>
    private const int MaxTaggedAgents = 6;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentExecutor _executor;
    private readonly ActivityBroadcaster _activityBus;
    private readonly ILogger<AgentOrchestrator> _logger;

    private readonly Queue<string> _queue = new();
    private readonly object _lock = new();
    private bool _processing;
    private volatile bool _stopped;

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory,
        IAgentExecutor executor,
        ActivityBroadcaster activityBus,
        ILogger<AgentOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _executor = executor;
        _activityBus = activityBus;
        _logger = logger;
    }

    /// <summary>Signals the orchestrator to stop processing.</summary>
    public void Stop() => _stopped = true;

    // ── PUBLIC ENTRY POINT ──────────────────────────────────────

    /// <summary>
    /// Enqueues a room for processing after a human message arrives.
    /// Processing is serialized — only one room is handled at a time.
    /// </summary>
    public void HandleHumanMessage(string roomId)
    {
        lock (_lock) { _queue.Enqueue(roomId); }
        _ = ProcessQueueAsync();
    }

    // ── QUEUE ───────────────────────────────────────────────────

    private async Task ProcessQueueAsync()
    {
        lock (_lock)
        {
            if (_processing) return;
            _processing = true;
        }

        try
        {
            while (!_stopped)
            {
                string? roomId;
                lock (_lock)
                {
                    if (!_queue.TryDequeue(out roomId))
                    {
                        // Atomically clear processing flag while still holding the lock.
                        // Any concurrent HandleHumanMessage that enqueued after the last
                        // dequeue will see _processing == false and start a new loop.
                        _processing = false;
                        return;
                    }
                }

                try
                {
                    await RunConversationRoundAsync(roomId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Orchestrator failed for room {RoomId}", roomId);
                }
            }
        }
        finally
        {
            lock (_lock) { _processing = false; }
        }
    }

    // ── CONVERSATION ROUND (MC room) ────────────────────────────

    private async Task RunConversationRoundAsync(string roomId)
    {
        using var scope = _scopeFactory.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var room = await runtime.GetRoomAsync(roomId);
        if (room is null) return;

        _logger.LogInformation("Conversation round for room {RoomId}", roomId);

        var planner = FindPlanner(runtime);
        var agentsToRun = new List<AgentDefinition>();

        // Step 1 — Run the planner first
        if (planner is not null)
        {
            await runtime.PublishThinkingAsync(planner, roomId);
            var plannerResponse = "";
            try
            {
                var freshRoom = await runtime.GetRoomAsync(roomId) ?? room;
                var prompt = BuildConversationPrompt(planner, freshRoom)
                    + "\n\nIMPORTANT: You are the lead planner. After your response, mention other agents "
                    + "by name if they should respond (e.g., '@Archimedes should review').\n"
                    + "If work needs to be done independently, use TASK ASSIGNMENT blocks to assign it:\n"
                    + "TASK ASSIGNMENT:\nAgent: @AgentName\nTitle: ...\nDescription: ...\nAcceptance Criteria:\n- ...\n";
                plannerResponse = await RunAgentWithTimeoutAsync(planner, prompt, roomId, McTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Planner failed");
            }
            finally
            {
                await runtime.PublishFinishedAsync(planner, roomId);
            }

            if (!string.IsNullOrWhiteSpace(plannerResponse) && !IsPassResponse(plannerResponse))
            {
                await PostAgentMessageAsync(runtime, planner, roomId, plannerResponse);

                // Collect @-mentioned agents for the next step
                foreach (var a in ParseTaggedAgents(runtime, plannerResponse))
                {
                    if (a.Id != planner.Id) agentsToRun.Add(a);
                }

                // Detect and handle task assignments
                foreach (var assignment in ParseTaskAssignments(plannerResponse))
                {
                    await HandleTaskAssignmentAsync(runtime, roomId, assignment);
                }
            }
        }

        // Step 2 — Fall back to idle agents if nobody was tagged
        if (agentsToRun.Count == 0)
        {
            agentsToRun.AddRange(
                (await GetIdleAgentsInRoomAsync(runtime, roomId))
                    .Where(a => a.Id != planner?.Id)
                    .Take(3));
        }

        // Step 3 — Run agents sequentially so each sees the previous response
        foreach (var agent in agentsToRun)
        {
            if (_stopped) break;

            var currentRoom = await runtime.GetRoomAsync(roomId);
            if (currentRoom is null) break;

            // Skip agents that are already working in a breakout room
            var location = await runtime.GetAgentLocationAsync(agent.Id);
            if (location?.State == AgentState.Working) continue;

            await runtime.PublishThinkingAsync(agent, roomId);
            var response = "";
            try
            {
                var prompt = BuildConversationPrompt(agent, currentRoom);
                response = await RunAgentWithTimeoutAsync(agent, prompt, roomId, McTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent {AgentName} failed", agent.Name);
            }
            finally
            {
                await runtime.PublishFinishedAsync(agent, roomId);
            }

            if (!string.IsNullOrWhiteSpace(response) && !IsPassResponse(response))
            {
                await PostAgentMessageAsync(runtime, agent, roomId, response);

                foreach (var assignment in ParseTaskAssignments(response))
                {
                    await HandleTaskAssignmentAsync(runtime, roomId, assignment);
                }
            }
        }

        _logger.LogInformation("Conversation round finished for room {RoomId}", roomId);
    }

    // ── TASK ASSIGNMENT PARSING ─────────────────────────────────

    /// <summary>
    /// Parses TASK ASSIGNMENT: blocks from an agent's response text.
    /// </summary>
    internal static List<ParsedTaskAssignment> ParseTaskAssignments(string content)
    {
        var assignments = new List<ParsedTaskAssignment>();

        var blocks = Regex.Split(content, @"TASK ASSIGNMENT:", RegexOptions.IgnoreCase);
        foreach (var block in blocks.Skip(1))
        {
            var agentMatch = Regex.Match(block, @"Agent:\s*@?(\S+)", RegexOptions.IgnoreCase);
            var titleMatch = Regex.Match(block, @"Title:\s*(.+)", RegexOptions.IgnoreCase);
            var descMatch = Regex.Match(block, @"Description:\s*([\s\S]*?)(?=Acceptance Criteria:|TASK ASSIGNMENT:|$)", RegexOptions.IgnoreCase);
            var criteriaMatch = Regex.Match(block, @"Acceptance Criteria:\s*([\s\S]*?)(?=TASK ASSIGNMENT:|$)", RegexOptions.IgnoreCase);

            if (!agentMatch.Success || !titleMatch.Success) continue;

            var criteria = new List<string>();
            if (criteriaMatch.Success)
            {
                foreach (var line in criteriaMatch.Groups[1].Value.Split('\n'))
                {
                    var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                    if (!string.IsNullOrEmpty(trimmed)) criteria.Add(trimmed);
                }
            }

            assignments.Add(new ParsedTaskAssignment(
                Agent: agentMatch.Groups[1].Value.Trim(),
                Title: titleMatch.Groups[1].Value.Trim(),
                Description: descMatch.Success ? descMatch.Groups[1].Value.Trim() : titleMatch.Groups[1].Value.Trim(),
                Criteria: criteria));
        }

        return assignments;
    }

    private async Task HandleTaskAssignmentAsync(
        WorkspaceRuntime runtime, string roomId, ParsedTaskAssignment assignment)
    {
        var allAgents = runtime.GetConfiguredAgents();
        var agent = allAgents.FirstOrDefault(a =>
            a.Name.Equals(assignment.Agent, StringComparison.OrdinalIgnoreCase) ||
            a.Id.Equals(assignment.Agent, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            _logger.LogWarning("Task assignment references unknown agent: {Agent}", assignment.Agent);
            return;
        }

        var brName = $"BR: {assignment.Title}";
        var br = await runtime.CreateBreakoutRoomAsync(roomId, agent.Id, brName);

        var descriptionWithCriteria = assignment.Description
            + (assignment.Criteria.Count > 0
                ? "\n\nAcceptance Criteria:\n" + string.Join("\n", assignment.Criteria.Select(c => $"- {c}"))
                : "");

        await runtime.CreateTaskItemAsync(
            assignment.Title, descriptionWithCriteria,
            agent.Id, roomId, br.Id);

        await runtime.PostSystemStatusAsync(roomId,
            $"📋 {agent.Name} has been assigned \"{assignment.Title}\" and is heading to breakout room \"{brName}\".");

        // Fire-and-forget — breakout work runs asynchronously
        _ = Task.Run(async () =>
        {
            try { await RunBreakoutLoopAsync(br.Id, agent.Id); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Breakout loop failed for {AgentName} in {BreakoutId}", agent.Name, br.Id);
            }
        });
    }

    // ── BREAKOUT ROOM ───────────────────────────────────────────

    private async Task RunBreakoutLoopAsync(string breakoutRoomId, string agentId)
    {
        using var scope = _scopeFactory.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var agent = runtime.GetConfiguredAgents().FirstOrDefault(a => a.Id == agentId);
        if (agent is null) return;

        var br = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
        if (br is null) return;

        _logger.LogInformation("Starting breakout loop for {AgentName} in {BreakoutName}", agent.Name, br.Name);

        var tasks = await runtime.GetBreakoutTaskItemsAsync(breakoutRoomId);
        await runtime.PostBreakoutMessageAsync(
            breakoutRoomId, "system", "LocalAgentHost", "System",
            BuildTaskBrief(agent, tasks));

        for (var round = 1; round <= MaxBreakoutRounds; round++)
        {
            if (_stopped) break;

            var currentBr = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
            if (currentBr is null || currentBr.Status != RoomStatus.Active) break;

            _logger.LogInformation("Breakout round {Round}/{Max} for {AgentName}",
                round, MaxBreakoutRounds, agent.Name);

            var response = "";
            try
            {
                var prompt = BuildBreakoutPrompt(agent, currentBr, round);
                response = await RunAgentWithTimeoutAsync(agent, prompt, breakoutRoomId, BreakoutTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Breakout agent {AgentName} failed in round {Round}", agent.Name, round);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                await runtime.PostBreakoutMessageAsync(
                    breakoutRoomId, agent.Id, agent.Name, agent.Role, response);
            }

            var report = ParseWorkReport(response);
            if (report is not null && Regex.IsMatch(report.Status, @"complete", RegexOptions.IgnoreCase))
            {
                var latestTasks = await runtime.GetBreakoutTaskItemsAsync(breakoutRoomId);
                foreach (var task in latestTasks)
                {
                    try { await runtime.UpdateTaskItemStatusAsync(task.Id, TaskItemStatus.Done, report.Evidence); }
                    catch { /* ok */ }
                }
                await HandleBreakoutCompleteAsync(runtime, breakoutRoomId, br.ParentRoomId);
                return;
            }
        }

        // Max rounds reached — present whatever exists
        _logger.LogInformation("Breakout max rounds reached for {AgentName}", agent.Name);
        await HandleBreakoutCompleteAsync(runtime, breakoutRoomId, br.ParentRoomId);
    }

    // ── BREAKOUT COMPLETION / REVIEW ────────────────────────────

    private async Task HandleBreakoutCompleteAsync(
        WorkspaceRuntime runtime, string breakoutRoomId, string parentRoomId)
    {
        var br = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
        if (br is null) return;

        var agent = runtime.GetConfiguredAgents().FirstOrDefault(a => a.Id == br.AssignedAgentId);
        if (agent is null) return;

        _logger.LogInformation("Breakout complete: {AgentName} returning from {BreakoutName}", agent.Name, br.Name);

        // Move agent to MC in "presenting" state
        await runtime.MoveAgentAsync(agent.Id, parentRoomId, AgentState.Presenting);
        await runtime.PostSystemStatusAsync(parentRoomId,
            $"🎯 {agent.Name} has completed work in \"{br.Name}\" and is presenting results.");

        // Post the last agent message (work report) into the MC room
        var agentMessages = br.RecentMessages.Where(m => m.SenderId == agent.Id).ToList();
        var lastMessage = agentMessages.LastOrDefault();
        if (lastMessage is not null)
        {
            try
            {
                await runtime.PostMessageAsync(new PostMessageRequest(
                    RoomId: parentRoomId,
                    SenderId: agent.Id,
                    Content: lastMessage.Content,
                    Kind: MessageKind.Response));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post work report for {AgentName}", agent.Name);
            }
        }

        // Ask the reviewer to evaluate
        var verdict = await RunReviewCycleAsync(runtime, parentRoomId, agent, lastMessage?.Content ?? "");

        // Check for approval — anchored at start to avoid false positives from
        // chatty verdicts like "APPROVED - no fixes needed"
        var isApproved = verdict is null ||
            Regex.IsMatch(verdict.Verdict, @"^\s*APPROVED", RegexOptions.IgnoreCase);

        if (!isApproved)
        {
            await HandleReviewRejectionAsync(runtime, breakoutRoomId, parentRoomId, agent, br);
        }
        else
        {
            await FinalizeBreakoutAsync(runtime, breakoutRoomId);
        }
    }

    private async Task<ParsedReviewVerdict?> RunReviewCycleAsync(
        WorkspaceRuntime runtime, string parentRoomId,
        AgentDefinition presentingAgent, string workReport)
    {
        var reviewer = FindReviewer(runtime);
        if (reviewer is null) return null;

        await runtime.PublishThinkingAsync(reviewer, parentRoomId);
        var reviewResponse = "";
        try
        {
            var room = await runtime.GetRoomAsync(parentRoomId);
            if (room is null) return null;
            var prompt = BuildReviewPrompt(reviewer, presentingAgent.Name, workReport);
            reviewResponse = await RunAgentWithTimeoutAsync(reviewer, prompt, parentRoomId, McTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reviewer failed");
            return null;
        }
        finally
        {
            await runtime.PublishFinishedAsync(reviewer, parentRoomId);
        }

        if (string.IsNullOrWhiteSpace(reviewResponse) || IsPassResponse(reviewResponse))
            return null;

        try
        {
            await runtime.PostMessageAsync(new PostMessageRequest(
                RoomId: parentRoomId,
                SenderId: reviewer.Id,
                Content: reviewResponse,
                Kind: MessageKind.Review));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post review");
        }

        return ParseReviewVerdict(reviewResponse);
    }

    private async Task HandleReviewRejectionAsync(
        WorkspaceRuntime runtime, string breakoutRoomId, string parentRoomId,
        AgentDefinition agent, BreakoutRoom br)
    {
        await runtime.PostSystemStatusAsync(parentRoomId,
            $"🔄 {agent.Name} is returning to \"{br.Name}\" to address review feedback.");
        await runtime.MoveAgentAsync(agent.Id, parentRoomId, AgentState.Working, breakoutRoomId);

        // Post review feedback into the breakout room
        var room = await runtime.GetRoomAsync(parentRoomId);
        var reviewMessage = room?.RecentMessages
            .Where(m => m.Kind == MessageKind.Review)
            .LastOrDefault();

        if (reviewMessage is not null)
        {
            await runtime.PostBreakoutMessageAsync(
                breakoutRoomId, "system", "LocalAgentHost", "System",
                $"Review feedback:\n{reviewMessage.Content}\n\nPlease address the findings and produce an updated WORK REPORT.");
        }

        // Grant fix rounds
        for (var round = 1; round <= MaxFixRounds; round++)
        {
            if (_stopped) break;
            var updatedBr = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
            if (updatedBr is null || updatedBr.Status != RoomStatus.Active) break;

            var response = "";
            try
            {
                response = await RunAgentWithTimeoutAsync(
                    agent, BuildBreakoutPrompt(agent, updatedBr, round),
                    breakoutRoomId, BreakoutTimeout);
            }
            catch { continue; }

            if (!string.IsNullOrWhiteSpace(response))
            {
                await runtime.PostBreakoutMessageAsync(
                    breakoutRoomId, agent.Id, agent.Name, agent.Role, response);
            }

            var report = ParseWorkReport(response);
            if (report is not null && Regex.IsMatch(report.Status, @"complete", RegexOptions.IgnoreCase))
            {
                var latestTasks = await runtime.GetBreakoutTaskItemsAsync(breakoutRoomId);
                foreach (var task in latestTasks)
                {
                    try { await runtime.UpdateTaskItemStatusAsync(task.Id, TaskItemStatus.Done, report.Evidence); }
                    catch { /* ok */ }
                }
                break;
            }
        }

        await FinalizeBreakoutAsync(runtime, breakoutRoomId);
    }

    private async Task FinalizeBreakoutAsync(WorkspaceRuntime runtime, string breakoutRoomId)
    {
        try
        {
            await runtime.CloseBreakoutRoomAsync(breakoutRoomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close breakout room {BreakoutId}", breakoutRoomId);
        }
    }

    // ── PARSING ─────────────────────────────────────────────────

    /// <summary>
    /// Parses a WORK REPORT: block from agent response text.
    /// Returns null if no report block is found.
    /// </summary>
    internal static ParsedWorkReport? ParseWorkReport(string content)
    {
        var match = Regex.Match(content, @"WORK REPORT:([\s\S]*?)(?=$|\nTASK ASSIGNMENT:)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var block = match.Groups[1].Value;
        var statusMatch = Regex.Match(block, @"Status:\s*(.+)", RegexOptions.IgnoreCase);
        var filesMatch = Regex.Match(block, @"Files?:\s*([\s\S]*?)(?=Evidence:|$)", RegexOptions.IgnoreCase);
        var evidenceMatch = Regex.Match(block, @"Evidence:\s*([\s\S]*?)$", RegexOptions.IgnoreCase);

        var files = new List<string>();
        if (filesMatch.Success)
        {
            foreach (var line in filesMatch.Groups[1].Value.Split('\n'))
            {
                var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                if (!string.IsNullOrEmpty(trimmed)) files.Add(trimmed);
            }
        }

        return new ParsedWorkReport(
            Status: statusMatch.Success ? statusMatch.Groups[1].Value.Trim() : "unknown",
            Files: files,
            Evidence: evidenceMatch.Success ? evidenceMatch.Groups[1].Value.Trim() : "");
    }

    /// <summary>
    /// Parses a REVIEW: block from reviewer response text.
    /// Returns null if no review block is found.
    /// </summary>
    internal static ParsedReviewVerdict? ParseReviewVerdict(string content)
    {
        var match = Regex.Match(content, @"REVIEW:([\s\S]*?)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var block = match.Groups[1].Value;
        var verdictMatch = Regex.Match(block, @"(?:Verdict|Status|Decision):\s*(.+)", RegexOptions.IgnoreCase);
        var findingsMatch = Regex.Match(block, @"(?:Findings?|Issues?|Comments?):\s*([\s\S]*?)$", RegexOptions.IgnoreCase);

        var findings = new List<string>();
        if (findingsMatch.Success)
        {
            foreach (var line in findingsMatch.Groups[1].Value.Split('\n'))
            {
                var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                if (!string.IsNullOrEmpty(trimmed)) findings.Add(trimmed);
            }
        }

        return new ParsedReviewVerdict(
            Verdict: verdictMatch.Success
                ? verdictMatch.Groups[1].Value.Trim()
                : block.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "",
            Findings: findings);
    }

    // ── AGENT HELPERS ───────────────────────────────────────────

    private static AgentDefinition? FindPlanner(WorkspaceRuntime runtime) =>
        runtime.GetConfiguredAgents().FirstOrDefault(a => a.Role == "Planner");

    private static AgentDefinition? FindReviewer(WorkspaceRuntime runtime) =>
        runtime.GetConfiguredAgents().FirstOrDefault(a => a.Role == "Reviewer");

    private static async Task<List<AgentDefinition>> GetIdleAgentsInRoomAsync(
        WorkspaceRuntime runtime, string roomId)
    {
        var result = new List<AgentDefinition>();
        foreach (var agent in runtime.GetConfiguredAgents())
        {
            var loc = await runtime.GetAgentLocationAsync(agent.Id);
            if (loc is not null &&
                loc.RoomId == roomId &&
                (loc.State == AgentState.Idle ||
                 loc.State == AgentState.InRoom ||
                 loc.State == AgentState.Presenting))
            {
                result.Add(agent);
            }
        }
        return result;
    }

    private List<AgentDefinition> ParseTaggedAgents(WorkspaceRuntime runtime, string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return [];

        var allAgents = runtime.GetConfiguredAgents();
        var tagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AgentDefinition>();

        foreach (var agent in allAgents)
        {
            if (tagged.Contains(agent.Id)) continue;

            var namePattern = $@"@?{Regex.Escape(agent.Name)}\b";
            var idPattern = $@"@?{Regex.Escape(agent.Id)}\b";

            if (Regex.IsMatch(response, namePattern, RegexOptions.IgnoreCase) ||
                Regex.IsMatch(response, idPattern, RegexOptions.IgnoreCase))
            {
                tagged.Add(agent.Id);
                result.Add(agent);
            }
        }

        return result.Take(MaxTaggedAgents).ToList();
    }

    private async Task<string> RunAgentWithTimeoutAsync(
        AgentDefinition agent, string prompt, string roomId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _executor.RunAsync(agent, prompt, roomId, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Agent {AgentName} timed out after {Seconds}s",
                agent.Name, timeout.TotalSeconds);
            return "";
        }
    }

    // ── PROMPT BUILDERS ─────────────────────────────────────────

    private static string BuildConversationPrompt(AgentDefinition agent, RoomSnapshot room)
    {
        var lines = new List<string> { agent.StartupPrompt, "" };
        lines.Add("=== CURRENT ROOM CONTEXT ===");
        lines.Add($"Room: {room.Name}");

        if (room.ActiveTask is not null)
        {
            lines.Add("");
            lines.Add("=== TASK ===");
            lines.Add($"Title: {room.ActiveTask.Title}");
            lines.Add($"Description: {room.ActiveTask.Description}");
            if (!string.IsNullOrEmpty(room.ActiveTask.SuccessCriteria))
                lines.Add($"Success criteria: {room.ActiveTask.SuccessCriteria}");
        }

        var specContext = LoadSpecContext();
        if (specContext is not null)
        {
            lines.Add("");
            lines.Add("=== PROJECT SPECIFICATION ===");
            lines.Add("The project maintains a living spec in specs/. Relevant sections:");
            lines.Add(specContext);
        }

        if (room.RecentMessages.Count > 0)
        {
            lines.Add("");
            lines.Add("=== RECENT CONVERSATION ===");
            foreach (var msg in room.RecentMessages.TakeLast(20))
            {
                lines.Add($"[{msg.SenderName} ({msg.SenderRole ?? msg.SenderKind.ToString()})]: {msg.Content}");
            }
        }

        lines.Add("");
        lines.Add("=== YOUR TURN ===");
        lines.Add($"You are {agent.Name} ({agent.Role}).");
        lines.Add("Respond naturally to the conversation. Be concise and actionable.");
        lines.Add("If you have nothing meaningful to contribute, reply with exactly: PASS");

        return string.Join("\n", lines);
    }

    private static string BuildBreakoutPrompt(AgentDefinition agent, BreakoutRoom br, int round)
    {
        var lines = new List<string> { agent.StartupPrompt, "" };
        lines.Add($"=== BREAKOUT ROOM: {br.Name} ===");
        lines.Add($"Round: {round}/{MaxBreakoutRounds}");

        if (br.Tasks.Count > 0)
        {
            lines.Add("");
            lines.Add("=== ASSIGNED TASKS ===");
            foreach (var task in br.Tasks)
            {
                lines.Add($"Task: {task.Title}");
                lines.Add($"Description: {task.Description}");
                lines.Add($"Status: {task.Status}");
                lines.Add("");
            }
        }

        if (br.RecentMessages.Count > 0)
        {
            lines.Add("=== WORK LOG ===");
            foreach (var msg in br.RecentMessages.TakeLast(10))
            {
                lines.Add($"[{msg.SenderName}]: {msg.Content}");
            }
        }

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

    private static string BuildReviewPrompt(AgentDefinition reviewer, string agentName, string workReport)
    {
        var lines = new List<string> { reviewer.StartupPrompt, "" };
        lines.Add("=== REVIEW REQUEST ===");
        lines.Add($"{agentName} has completed work and is presenting their results.");
        lines.Add("");
        lines.Add("=== WORK REPORT ===");
        lines.Add(workReport);

        var specContext = LoadSpecContext();
        if (specContext is not null)
        {
            lines.Add("");
            lines.Add("=== SPEC SECTIONS (verify accuracy against delivered work) ===");
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

    // ── MESSAGE POSTING ─────────────────────────────────────────

    private async Task PostAgentMessageAsync(
        WorkspaceRuntime runtime, AgentDefinition agent, string roomId, string content)
    {
        try
        {
            await runtime.PostMessageAsync(new PostMessageRequest(
                RoomId: roomId,
                SenderId: agent.Id,
                Content: content,
                Kind: InferMessageKind(agent.Role)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post message for {AgentId}", agent.Id);
        }
    }

    /// <summary>
    /// Maps an agent's role to the appropriate <see cref="MessageKind"/>.
    /// </summary>
    internal static MessageKind InferMessageKind(string role) => role switch
    {
        "Planner" => MessageKind.Coordination,
        "Architect" => MessageKind.Decision,
        "SoftwareEngineer" => MessageKind.Response,
        "Reviewer" => MessageKind.Review,
        "Validator" => MessageKind.Validation,
        "TechnicalWriter" => MessageKind.SpecChangeProposal,
        _ => MessageKind.Response,
    };

    /// <summary>
    /// Detects "pass" responses — short responses that indicate the agent
    /// has nothing meaningful to contribute.
    /// </summary>
    internal static bool IsPassResponse(string response)
    {
        var trimmed = response.Trim();
        return trimmed.Length < 30 &&
               (Regex.IsMatch(trimmed, @"PASS", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^N/A$", RegexOptions.IgnoreCase) ||
                trimmed == "No comment." ||
                trimmed == "Nothing to add.");
    }

    /// <summary>
    /// Loads a summary of the project specification from the specs/ directory.
    /// Returns a condensed index of spec sections, or null if no specs exist.
    /// </summary>
    internal static string? LoadSpecContext()
    {
        var specsDir = Path.Combine(Directory.GetCurrentDirectory(), "specs");
        if (!Directory.Exists(specsDir)) return null;

        try
        {
            var sections = new List<string>();

            foreach (var dir in Directory.GetDirectories(specsDir))
            {
                var dirName = Path.GetFileName(dir);
                var specFile = Path.Combine(dir, "spec.md");
                if (!File.Exists(specFile)) continue;

                var content = File.ReadAllText(specFile);
                var headingMatch = Regex.Match(content, @"^#\s+(.+)", RegexOptions.Multiline);
                var heading = headingMatch.Success ? headingMatch.Groups[1].Value : dirName;

                var purposeMatch = Regex.Match(content, @"## Purpose\s*\n([\s\S]*?)(?=\n##|\n$)");
                var summary = purposeMatch.Success
                    ? purposeMatch.Groups[1].Value.Trim().Split('\n')[0]
                    : "";

                sections.Add($"- specs/{dirName}/spec.md: {heading}" +
                    (string.IsNullOrEmpty(summary) ? "" : $" — {summary}"));
            }

            return sections.Count == 0 ? null : string.Join("\n", sections);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildTaskBrief(AgentDefinition agent, List<TaskItem> tasks)
    {
        var lines = new List<string>
        {
            $"Task Brief for {agent.Name}",
            new('=', 40)
        };
        foreach (var task in tasks)
        {
            lines.Add($"\nTask: {task.Title}");
            lines.Add($"Description: {task.Description}");
        }
        return string.Join("\n", lines);
    }
}

// ── Data Transfer Records ───────────────────────────────────────

/// <summary>A parsed TASK ASSIGNMENT: block.</summary>
internal record ParsedTaskAssignment(
    string Agent,
    string Title,
    string Description,
    List<string> Criteria);

/// <summary>A parsed WORK REPORT: block.</summary>
internal record ParsedWorkReport(
    string Status,
    List<string> Files,
    string Evidence);

/// <summary>A parsed REVIEW: verdict block.</summary>
internal record ParsedReviewVerdict(
    string Verdict,
    List<string> Findings);
