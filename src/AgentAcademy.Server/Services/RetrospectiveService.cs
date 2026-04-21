using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Runs an automated post-task retrospective for the assigned agent after
/// a task is merged. The agent reflects on the task, stores key learnings
/// as memories via REMEMBER commands, and produces a summary saved as a
/// Retrospective comment on the task.
///
/// Designed to be called fire-and-forget from MergeTaskHandler. Creates
/// its own DI scopes (singleton lifetime, like BreakoutCompletionService).
/// </summary>
public sealed class RetrospectiveService : Contracts.IRetrospectiveService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentExecutor _executor;
    private readonly CommandPipeline _commandPipeline;
    private readonly ILearningDigestService _digestService;
    private readonly ILogger<RetrospectiveService> _logger;

    public RetrospectiveService(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
        IAgentExecutor executor,
        CommandPipeline commandPipeline,
        ILearningDigestService digestService,
        ILogger<RetrospectiveService> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _executor = executor;
        _commandPipeline = commandPipeline;
        _digestService = digestService;
        _logger = logger;
    }

    /// <summary>
    /// Runs a retrospective for the completed task. Safe to call fire-and-forget.
    /// </summary>
    public async Task RunRetrospectiveAsync(string taskId, string? assignedAgentId)
    {
        if (string.IsNullOrWhiteSpace(assignedAgentId))
        {
            _logger.LogDebug("Skipping retrospective for task {TaskId}: no assigned agent", taskId);
            return;
        }

        try
        {
            // Idempotency: skip if a retrospective comment already exists
            if (await HasExistingRetrospectiveAsync(taskId))
            {
                _logger.LogDebug("Retrospective already exists for task {TaskId}, skipping", taskId);
                return;
            }

            var agent = _catalog.Agents.FirstOrDefault(a => a.Id == assignedAgentId);
            if (agent is null)
            {
                _logger.LogWarning("Retrospective skipped: agent {AgentId} not found in catalog", assignedAgentId);
                return;
            }

            // Gather task context
            var context = await GatherRetrospectiveContextAsync(taskId);
            if (context is null)
            {
                _logger.LogWarning("Retrospective skipped: task {TaskId} not found", taskId);
                return;
            }

            // Build prompt
            var prompt = PromptBuilder.BuildRetrospectivePrompt(agent, context);

            // Create a restricted agent clone: only REMEMBER commands allowed
            var retroAgent = agent with
            {
                Permissions = new CommandPermissionSet(
                    Allowed: new List<string> { "REMEMBER" },
                    Denied: new List<string>()),
                EnabledTools = new List<string>()
            };

            // Run the agent with a synthetic retrospective session
            var retroRoomId = $"retrospective:{taskId}";
            _logger.LogInformation(
                "Running retrospective for task {TaskId} with agent {AgentName}",
                taskId, agent.Name);

            bool sessionStarted = false;
            try
            {
                sessionStarted = true;
                var response = await _executor.RunAsync(retroAgent, prompt, retroRoomId);

                if (string.IsNullOrWhiteSpace(response))
                {
                    _logger.LogWarning("Retrospective for task {TaskId}: agent returned empty response", taskId);
                    return;
                }

                // Process REMEMBER commands from the response
                string remainingText;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var pipelineResult = await _commandPipeline.ProcessResponseAsync(
                        agent.Id, response, retroRoomId, retroAgent, scope.ServiceProvider);
                    remainingText = pipelineResult.RemainingText;
                }

                // Save the retrospective summary (commands stripped) as a task comment
                var summary = remainingText.Trim();
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    await SaveRetrospectiveCommentAsync(taskId, agent.Id, agent.Name, summary);
                }

                // Publish activity event
                using (var scope = _scopeFactory.CreateScope())
                {
                    var activity = scope.ServiceProvider.GetRequiredService<IActivityPublisher>();
                    var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
                    var taskEntity = await db.Tasks.FindAsync(taskId);

                    activity.Publish(
                        ActivityEventType.TaskRetrospectiveCompleted,
                        taskEntity?.RoomId,
                        agent.Id,
                        taskId,
                        $"{agent.Name} completed retrospective for task: {taskEntity?.Title ?? taskId}");

                    await db.SaveChangesAsync();
                }

                _logger.LogInformation(
                    "Retrospective completed for task {TaskId} by {AgentName}",
                    taskId, agent.Name);

                // Trigger learning digest check (fire-and-forget, threshold-gated)
                _ = Task.Run(async () =>
                {
                    try { await _digestService.TryGenerateDigestAsync(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Digest trigger after retrospective failed"); }
                });
            }
            finally
            {
                // Always clean up the synthetic session to prevent leaks
                if (sessionStarted)
                {
                    try { await _executor.InvalidateSessionAsync(agent.Id, retroRoomId); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to invalidate retrospective session"); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retrospective failed for task {TaskId}", taskId);
        }
    }

    private async Task<bool> HasExistingRetrospectiveAsync(string taskId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        return await db.TaskComments
            .AnyAsync(c => c.TaskId == taskId && c.CommentType == nameof(TaskCommentType.Retrospective));
    }

    internal async Task<RetrospectiveContext?> GatherRetrospectiveContextAsync(string taskId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var task = await db.Tasks.FindAsync(taskId);
        if (task is null) return null;

        var comments = await db.TaskComments
            .Where(c => c.TaskId == taskId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        // Gather review messages from the task's room
        List<MessageEntity> reviewMessages = new();
        if (!string.IsNullOrEmpty(task.RoomId))
        {
            reviewMessages = await db.Messages
                .Where(m => m.RoomId == task.RoomId && m.Kind == nameof(MessageKind.Review))
                .OrderByDescending(m => m.SentAt)
                .Take(20)
                .ToListAsync();
            reviewMessages.Reverse(); // chronological order for prompt readability
        }

        var cycleTime = task.CompletedAt.HasValue && task.StartedAt.HasValue
            ? task.CompletedAt.Value - task.StartedAt.Value
            : (TimeSpan?)null;

        return new RetrospectiveContext(
            TaskId: task.Id,
            Title: task.Title,
            Description: task.Description,
            SuccessCriteria: task.SuccessCriteria,
            TaskType: task.Type,
            Status: task.Status,
            ReviewRounds: task.ReviewRounds,
            CommitCount: task.CommitCount,
            CycleTime: cycleTime,
            CreatedAt: task.CreatedAt,
            CompletedAt: task.CompletedAt,
            Comments: comments.Select(c => new RetrospectiveComment(
                c.AgentName, c.CommentType, c.Content)).ToList(),
            ReviewMessages: reviewMessages.Select(m => new RetrospectiveComment(
                m.SenderName, "Review", m.Content)).ToList()
        );
    }

    private async Task SaveRetrospectiveCommentAsync(
        string taskId, string agentId, string agentName, string content)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();

        await taskLifecycle.AddTaskCommentAsync(
            taskId, agentId, agentName, TaskCommentType.Retrospective, content);
    }
}

/// <summary>
/// Context gathered for a retrospective prompt.
/// </summary>
public record RetrospectiveContext(
    string TaskId,
    string Title,
    string Description,
    string? SuccessCriteria,
    string? TaskType,
    string? Status,
    int ReviewRounds,
    int CommitCount,
    TimeSpan? CycleTime,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    List<RetrospectiveComment> Comments,
    List<RetrospectiveComment> ReviewMessages
);

public record RetrospectiveComment(string Author, string Type, string Content);
