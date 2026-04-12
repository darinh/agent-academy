using System.ComponentModel;
using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Wrapper that captures agent identity for task-write tool functions.
/// Methods have proper default parameter values so <see cref="AIFunctionFactory"/>
/// treats nullable parameters as optional.
/// </summary>
internal sealed class TaskWriteToolWrapper
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly string _agentId;
    private readonly string _agentName;

    internal TaskWriteToolWrapper(
        IServiceScopeFactory scopeFactory, ILogger logger,
        string agentId, string agentName)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _agentId = agentId;
        _agentName = agentName;
    }

    private static readonly HashSet<string> AllowedTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(Shared.Models.TaskStatus.Active),
        nameof(Shared.Models.TaskStatus.Blocked),
        nameof(Shared.Models.TaskStatus.AwaitingValidation),
        nameof(Shared.Models.TaskStatus.InReview),
        nameof(Shared.Models.TaskStatus.Queued),
    };

    [Description("Create a new task in the workspace.")]
    internal async Task<string> CreateTaskAsync(
        [Description("Task title")] string title,
        [Description("Detailed description of the task")] string description,
        [Description("Success criteria — what must be true for the task to be considered done")] string successCriteria,
        [Description("Preferred agent roles (e.g., SoftwareEngineer, Reviewer)")] string[]? preferredRoles = null,
        [Description("Task type: Feature, Bug, Refactor, Documentation, Test (default: Feature)")] string? type = null)
    {
        _logger.LogDebug("Tool call: create_task by {AgentId} (title={Title})", _agentId, title);

        if (string.IsNullOrWhiteSpace(title))
            return "Error: title is required.";
        if (string.IsNullOrWhiteSpace(description))
            return "Error: description is required.";
        if (string.IsNullOrWhiteSpace(successCriteria))
            return "Error: successCriteria is required.";

        var taskType = TaskType.Feature;
        if (!string.IsNullOrWhiteSpace(type) &&
            !Enum.TryParse<TaskType>(type, ignoreCase: true, out taskType))
        {
            return $"Error: Invalid task type '{type}'. Valid: Feature, Bug, Refactor, Documentation, Test";
        }

        var roles = preferredRoles?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList()
            ?? new List<string>();

        using var scope = _scopeFactory.CreateScope();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<TaskOrchestrationService>();

        try
        {
            var request = new TaskAssignmentRequest(
                Title: title,
                Description: description,
                SuccessCriteria: successCriteria,
                RoomId: null,
                PreferredRoles: roles,
                Type: taskType
            );

            var result = await taskOrchestration.CreateTaskAsync(request);
            return $"Task created successfully.\n" +
                   $"- ID: {result.Task.Id}\n" +
                   $"- Title: {result.Task.Title}\n" +
                   $"- Status: {result.Task.Status}\n" +
                   $"- Room: {result.Room.Name} (ID: {result.Room.Id})\n" +
                   $"- Type: {taskType}";
        }
        catch (Exception ex)
        {
            return $"Error creating task: {ex.Message}";
        }
    }

    [Description("Update a task's status, report a blocker, or post a note.")]
    internal async Task<string> UpdateTaskStatusAsync(
        [Description("ID of the task to update")] string taskId,
        [Description("New status: Active, Blocked, AwaitingValidation, InReview, Queued")] string? status = null,
        [Description("Blocker description (implies Blocked status — cannot be combined with status)")] string? blocker = null,
        [Description("Note to post on the task")] string? note = null)
    {
        _logger.LogDebug("Tool call: update_task_status by {AgentId} (taskId={TaskId})", _agentId, taskId);

        if (string.IsNullOrWhiteSpace(taskId))
            return "Error: taskId is required.";

        var hasStatus = !string.IsNullOrWhiteSpace(status);
        var hasBlocker = !string.IsNullOrWhiteSpace(blocker);
        var hasNote = !string.IsNullOrWhiteSpace(note);

        if (!hasStatus && !hasBlocker && !hasNote)
            return "Error: At least one of status, blocker, or note is required.";

        if (hasBlocker && hasStatus)
            return "Error: Cannot specify both 'blocker' and 'status' — blocker implies Blocked status.";

        if (hasStatus && !AllowedTaskStatuses.Contains(status!))
            return $"Error: Invalid status '{status}'. Allowed: {string.Join(", ", AllowedTaskStatuses.Order())}";

        using var scope = _scopeFactory.CreateScope();
        var taskQueries = scope.ServiceProvider.GetRequiredService<TaskQueryService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<TaskOrchestrationService>();

        try
        {
            var task = await taskQueries.GetTaskAsync(taskId);
            if (task is null)
                return $"Error: Task '{taskId}' not found.";

            var actions = new List<string>();

            if (hasBlocker)
            {
                await taskQueries.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Blocked);
                await taskOrchestration.PostTaskNoteAsync(taskId,
                    $"🚫 Blocked by {_agentName}: {blocker}");
                actions.Add($"status → Blocked (blocker: {blocker})");
            }
            else if (hasStatus)
            {
                var parsed = Enum.Parse<Shared.Models.TaskStatus>(status!, ignoreCase: true);
                await taskQueries.UpdateTaskStatusAsync(taskId, parsed);
                actions.Add($"status → {parsed}");
            }

            if (hasNote)
            {
                await taskOrchestration.PostTaskNoteAsync(taskId,
                    $"📝 Note from {_agentName}: {note}");
                actions.Add("note posted");
            }

            var finalTask = await taskQueries.GetTaskAsync(taskId);
            var title = finalTask?.Title ?? task.Title;
            return $"Task '{title}' updated: {string.Join("; ", actions)}\n" +
                   $"- ID: {taskId}\n" +
                   $"- Status: {finalTask?.Status.ToString() ?? task.Status.ToString()}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Add a comment, finding, evidence, or blocker to a task.")]
    internal async Task<string> AddTaskCommentAsync(
        [Description("ID of the task to comment on")] string taskId,
        [Description("Comment content")] string content,
        [Description("Comment type: Comment, Finding, Evidence, Blocker (default: Comment)")] string? commentType = null)
    {
        _logger.LogDebug("Tool call: add_task_comment by {AgentId} (taskId={TaskId})", _agentId, taskId);

        if (string.IsNullOrWhiteSpace(taskId))
            return "Error: taskId is required.";
        if (string.IsNullOrWhiteSpace(content))
            return "Error: content is required.";

        var parsedType = TaskCommentType.Comment;
        if (!string.IsNullOrWhiteSpace(commentType) &&
            !Enum.TryParse<TaskCommentType>(commentType, ignoreCase: true, out parsedType))
        {
            return $"Error: Invalid comment type '{commentType}'. Valid: Comment, Finding, Evidence, Blocker";
        }

        using var scope = _scopeFactory.CreateScope();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<TaskLifecycleService>();

        try
        {
            var comment = await taskLifecycle.AddTaskCommentAsync(
                taskId, _agentId, _agentName, parsedType, content);
            return $"Comment added to task '{taskId}'.\n" +
                   $"- Type: {parsedType}\n" +
                   $"- ID: {comment.Id}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
