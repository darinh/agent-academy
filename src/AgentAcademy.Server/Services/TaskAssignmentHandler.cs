using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles the full task assignment flow: permission gating, breakout room
/// creation, git branch/worktree setup, task entity creation, and cleanup
/// on failure. Extracted from AgentOrchestrator to isolate the assignment
/// concern that attracted frequent fix commits.
///
/// Concurrency assumption: callers must ensure that only one assignment
/// is processed for a given agent at a time. The orchestrator's serialized
/// queue processing provides this guarantee today.
/// </summary>
public sealed class TaskAssignmentHandler : ITaskAssignmentHandler
{
    private readonly IAgentCatalog _catalog;
    private readonly GitService _gitService;
    private readonly WorktreeService _worktreeService;
    private readonly BreakoutLifecycleService _breakoutLifecycle;
    private readonly ILogger<TaskAssignmentHandler> _logger;

    public TaskAssignmentHandler(
        IAgentCatalog catalog,
        GitService gitService,
        WorktreeService worktreeService,
        BreakoutLifecycleService breakoutLifecycle,
        ILogger<TaskAssignmentHandler> logger)
    {
        _catalog = catalog;
        _gitService = gitService;
        _worktreeService = worktreeService;
        _breakoutLifecycle = breakoutLifecycle;
        _logger = logger;
    }

    /// <summary>
    /// Gates and processes a task assignment from an agent response.
    /// Planners may create any task type; non-planners may only file bugs
    /// (other types are converted into a proposal posted to the room).
    /// </summary>
    public async Task ProcessAssignmentAsync(
        IServiceScope scope, AgentDefinition requestedBy, string roomId,
        ParsedTaskAssignment assignment)
    {
        var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
        if (!await TryGateAssignmentAsync(messageService, requestedBy, roomId, assignment))
            return;

        await HandleAssignmentAsync(scope, roomId, assignment);
    }

    // ── GATING ──────────────────────────────────────────────────

    /// <summary>
    /// Checks whether an agent is allowed to create a task of the given type.
    /// Non-planners can only create Bug tasks. Other types are converted into a
    /// proposal message posted to the room. Returns true if the assignment should proceed.
    /// </summary>
    private async Task<bool> TryGateAssignmentAsync(
        MessageService messageService, AgentDefinition agent, string roomId,
        ParsedTaskAssignment assignment)
    {
        if (string.Equals(agent.Role, "Planner", StringComparison.OrdinalIgnoreCase))
            return true;

        if (assignment.Type == TaskType.Bug)
            return true;

        _logger.LogInformation(
            "Agent {AgentName} ({Role}) proposed task '{Title}' — only planners can create non-bug tasks",
            agent.Name, agent.Role, assignment.Title);

        await messageService.PostSystemStatusAsync(roomId,
            $"💡 **Task proposal from {agent.Name}**: \"{assignment.Title}\"\n" +
            $"{assignment.Description}\n\n" +
            $"_Only planners can create tasks. Aristotle, please review and assign if appropriate._");

        return false;
    }

    // ── ASSIGNMENT FLOW ─────────────────────────────────────────

    private async Task HandleAssignmentAsync(
        IServiceScope scope, string roomId, ParsedTaskAssignment assignment)
    {
        var agentLocationService = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
        var breakoutRoomService = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskItemService = scope.ServiceProvider.GetRequiredService<ITaskItemService>();
        var taskQueryService = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var planService = scope.ServiceProvider.GetRequiredService<PlanService>();

        var agent = _catalog.Agents.FirstOrDefault(a =>
            a.Name.Equals(assignment.Agent, StringComparison.OrdinalIgnoreCase) ||
            a.Id.Equals(assignment.Agent, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            _logger.LogWarning("Task assignment references unknown agent: {Agent}", assignment.Agent);
            return;
        }

        // Prevent concurrent breakout rooms for the same agent
        var location = await agentLocationService.GetAgentLocationAsync(agent.Id);
        if (location?.State == AgentState.Working)
        {
            _logger.LogWarning(
                "Agent {AgentName} is already in breakout room {BreakoutId} — skipping assignment '{Title}'",
                agent.Name, location.BreakoutRoomId, assignment.Title);
            await messageService.PostSystemStatusAsync(roomId,
                $"⚠️ {agent.Name} is already working on a task. Assignment \"{assignment.Title}\" will wait until they finish.");
            return;
        }

        var descriptionWithCriteria = assignment.Description
            + (assignment.Criteria.Count > 0
                ? "\n\nAcceptance Criteria:\n" + string.Join("\n", assignment.Criteria.Select(c => $"- {c}"))
                : "");

        var brName = $"BR: {assignment.Title}";
        var br = await breakoutRoomService.CreateBreakoutRoomAsync(roomId, agent.Id, brName);

        string? taskBranch = null;
        string? taskId = null;
        TaskItem? taskItem = null;
        string? worktreePath = null;
        try
        {
            taskBranch = await _gitService.CreateTaskBranchAsync(assignment.Title);

            if (!await _gitService.BranchExistsAsync(taskBranch))
                throw new InvalidOperationException($"Branch '{taskBranch}' was not created");

            await _gitService.ReturnToDevelopAsync(taskBranch);

            // Create a worktree for isolated work when a workspace is available
            var workspacePath = await roomService.GetActiveWorkspacePathAsync();
            if (workspacePath is not null && taskBranch is not null)
            {
                try
                {
                    var worktree = await _worktreeService.CreateWorktreeAsync(taskBranch);
                    worktreePath = worktree.Path;
                    _logger.LogInformation(
                        "Created worktree for task branch {Branch} at {Path}",
                        taskBranch, worktree.Path);
                }
                catch (Exception wtEx)
                {
                    _logger.LogWarning(wtEx,
                        "Failed to create worktree for {Branch} — agent will work on shared checkout",
                        taskBranch);
                }
            }

            taskItem = await taskItemService.CreateTaskItemAsync(
                assignment.Title, descriptionWithCriteria,
                agent.Id, roomId, br.Id);

            taskId = await breakoutRoomService.EnsureTaskForBreakoutAsync(
                br.Id, assignment.Title, descriptionWithCriteria, agent.Id, roomId,
                PromptBuilder.BuildAssignmentPlanContent(assignment), taskBranch);

            var task = await taskQueryService.GetTaskAsync(taskId);
            var planContent = !string.IsNullOrWhiteSpace(task?.CurrentPlan)
                ? task.CurrentPlan
                : PromptBuilder.BuildAssignmentPlanContent(assignment);
            await planService.SetPlanAsync(br.Id, planContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create task branch for {Title} — cleaning up", assignment.Title);
            await CleanupFailedAssignmentAsync(
                br.Id, taskId, taskItem, taskBranch, worktreePath, roomId, assignment.Title,
                breakoutRoomService, messageService, taskQueryService, taskItemService);
            return;
        }

        await messageService.PostSystemStatusAsync(roomId,
            $"📋 {agent.Name} has been assigned \"{assignment.Title}\" and is heading to breakout room \"{brName}\" on branch `{taskBranch}`.");

        _ = Task.Run(() => _breakoutLifecycle.RunBreakoutLifecycleAsync(
            br.Id, agent.Id, roomId, agent, taskBranch, worktreePath));
    }

    // ── CLEANUP ─────────────────────────────────────────────────

    /// <summary>
    /// Best-effort cleanup when the assignment flow fails partway through.
    /// Each step is independent — one failure must not prevent the others.
    /// </summary>
    private async Task CleanupFailedAssignmentAsync(
        string breakoutRoomId, string? taskId, TaskItem? taskItem,
        string? taskBranch, string? worktreePath, string roomId, string title,
        IBreakoutRoomService breakoutRoomService, MessageService messageService,
        ITaskQueryService taskQueryService, ITaskItemService taskItemService)
    {
        try
        {
            await breakoutRoomService.CloseBreakoutRoomAsync(breakoutRoomId, BreakoutRoomCloseReason.Cancelled);
        }
        catch (Exception closeEx)
        {
            _logger.LogWarning(closeEx, "Failed to close breakout room {BreakoutId}", breakoutRoomId);
        }

        if (taskId is not null)
        {
            try
            {
                await taskQueryService.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Cancelled);
            }
            catch (Exception cancelEx)
            {
                _logger.LogWarning(cancelEx, "Failed to cancel orphaned task {TaskId}", taskId);
            }
        }

        if (taskItem is not null)
        {
            try
            {
                await taskItemService.UpdateTaskItemStatusAsync(taskItem.Id, Shared.Models.TaskItemStatus.Rejected);
            }
            catch (Exception itemEx)
            {
                _logger.LogWarning(itemEx, "Failed to cancel orphaned task item {TaskItemId}", taskItem.Id);
            }
        }

        if (taskBranch is not null)
        {
            if (worktreePath is not null)
            {
                try
                {
                    await _worktreeService.RemoveWorktreeAsync(taskBranch);
                }
                catch (Exception wtEx)
                {
                    _logger.LogWarning(wtEx, "Failed to remove worktree for orphaned branch {Branch}", taskBranch);
                }
            }

            try
            {
                await _gitService.ReturnToDevelopAsync(taskBranch);
            }
            catch { /* best-effort — may already be on develop */ }

            try
            {
                await _gitService.DeleteBranchAsync(taskBranch);
            }
            catch (Exception branchEx)
            {
                _logger.LogWarning(branchEx, "Failed to delete orphaned branch {Branch}", taskBranch);
            }
        }

        try
        {
            await messageService.PostSystemStatusAsync(roomId,
                $"⚠️ Failed to set up branch for \"{title}\". Breakout cancelled.");
        }
        catch { /* best-effort notification */ }
    }
}
