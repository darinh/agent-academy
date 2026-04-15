using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles UPDATE_TASK — updates task status, blocker, or note.
/// At least one optional argument is required.
/// </summary>
public sealed class UpdateTaskHandler : ICommandHandler
{
    public string CommandName => "UPDATE_TASK";

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(TaskStatus.Active),
        nameof(TaskStatus.Blocked),
        nameof(TaskStatus.AwaitingValidation),
        nameof(TaskStatus.InReview),
        nameof(TaskStatus.Queued),
    };

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId
            || string.IsNullOrWhiteSpace(taskId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: taskId"
            };
        }

        var hasStatus = command.Args.TryGetValue("status", out var statusObj) && statusObj is string statusStr
            && !string.IsNullOrWhiteSpace(statusStr);
        var hasBlocker = command.Args.TryGetValue("blocker", out var blockerObj) && blockerObj is string blockerStr
            && !string.IsNullOrWhiteSpace(blockerStr);
        var hasNote = command.Args.TryGetValue("note", out var noteObj) && noteObj is string noteStr
            && !string.IsNullOrWhiteSpace(noteStr);
        var hasPriority = command.Args.TryGetValue("priority", out var priorityObj) && priorityObj is string priorityStr
            && !string.IsNullOrWhiteSpace(priorityStr);

        if (!hasStatus && !hasBlocker && !hasNote && !hasPriority)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "At least one of status, blocker, note, or priority is required"
            };
        }

        var taskOrchestration = context.Services.GetRequiredService<TaskOrchestrationService>();
        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();

        try
        {
            // Verify task exists first
            _ = await taskQueries.GetTaskAsync(taskId)
                ?? throw new InvalidOperationException($"Task '{taskId}' not found");

            var actions = new List<string>();

            // Handle blocker — sets Blocked status and posts the blocker message
            if (hasBlocker)
            {
                if (hasStatus)
                {
                    return command with
                    {
                        Status = CommandStatus.Error,
                        ErrorCode = CommandErrorCode.Validation,
                        Error = "Cannot specify both 'blocker' and 'status' — blocker implies Blocked status"
                    };
                }

                await taskQueries.UpdateTaskStatusAsync(taskId, TaskStatus.Blocked);
                await taskOrchestration.PostTaskNoteAsync(taskId,
                    $"🚫 Blocked by {context.AgentName}: {(string)blockerObj!}");
                actions.Add($"status → Blocked (blocker: {(string)blockerObj!})");
            }
            else if (hasStatus)
            {
                var statusValue = (string)statusObj!;
                if (!AllowedStatuses.Contains(statusValue))
                {
                    return command with
                    {
                        Status = CommandStatus.Error,
                        ErrorCode = CommandErrorCode.Validation,
                        Error = $"Invalid status '{statusValue}'. Allowed: {string.Join(", ", AllowedStatuses)}"
                    };
                }

                if (!Enum.TryParse<TaskStatus>(statusValue, ignoreCase: true, out var parsed))
                {
                    return command with
                    {
                        Status = CommandStatus.Error,
                        ErrorCode = CommandErrorCode.Validation,
                        Error = $"Unknown status: {statusValue}"
                    };
                }

                await taskQueries.UpdateTaskStatusAsync(taskId, parsed);
                actions.Add($"status → {parsed}");
            }

            // Handle note — post as system message to the task's room
            if (hasNote)
            {
                await taskOrchestration.PostTaskNoteAsync(taskId,
                    $"📝 Note from {context.AgentName}: {(string)noteObj!}");
                actions.Add("note posted");
            }

            // Handle priority change
            if (hasPriority)
            {
                var pStr = (string)priorityObj!;
                if (!Enum.TryParse<TaskPriority>(pStr, ignoreCase: true, out var parsedPriority)
                    || !Enum.IsDefined(parsedPriority))
                {
                    return command with
                    {
                        Status = CommandStatus.Error,
                        ErrorCode = CommandErrorCode.Validation,
                        Error = $"Invalid priority '{pStr}'. Allowed: Critical, High, Medium, Low"
                    };
                }
                await taskQueries.UpdateTaskPriorityAsync(taskId, parsedPriority);
                actions.Add($"priority → {parsedPriority}");
            }

            // Fetch final state
            var finalTask = await taskQueries.GetTaskAsync(taskId)
                ?? throw new InvalidOperationException($"Task '{taskId}' not found after update");

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = finalTask.Id,
                    ["title"] = finalTask.Title,
                    ["status"] = finalTask.Status.ToString(),
                    ["priority"] = finalTask.Priority.ToString(),
                    ["actions"] = string.Join("; ", actions),
                    ["message"] = $"Task '{finalTask.Title}' updated: {string.Join("; ", actions)}"
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = ex.Message
            };
        }
    }
}
