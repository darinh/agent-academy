using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Review workflow: approve, request changes, and reject tasks.
/// </summary>
public sealed partial class TaskLifecycleService
{
    private const int MaxReviewRounds = 5;

    /// <summary>
    /// Approves a task after review. Records the reviewer and increments review rounds.
    /// </summary>
    public async Task<TaskSnapshot> ApproveTaskAsync(string taskId, string reviewerAgentId, string? findings = null)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var currentStatus = entity.Status;
        if (currentStatus != nameof(Shared.Models.TaskStatus.InReview) &&
            currentStatus != nameof(Shared.Models.TaskStatus.AwaitingValidation))
            throw new InvalidOperationException(
                $"Task '{taskId}' is in '{currentStatus}' state — must be InReview or AwaitingValidation to approve");

        var now = DateTime.UtcNow;
        entity.Status = nameof(Shared.Models.TaskStatus.Approved);
        entity.ReviewerAgentId = reviewerAgentId;
        entity.ReviewRounds++;
        entity.UpdatedAt = now;

        var reviewerName = _catalog.Agents.FirstOrDefault(a => a.Id == reviewerAgentId)?.Name ?? reviewerAgentId;

        if (!string.IsNullOrWhiteSpace(findings) && !string.IsNullOrEmpty(entity.RoomId))
        {
            var msgEntity = CreateMessageEntity(entity.RoomId, MessageKind.Review,
                $"✅ **Approved** by {reviewerName}\n\n{findings}", null, now);
            _db.Messages.Add(msgEntity);
        }

        Publish(ActivityEventType.TaskApproved, entity.RoomId, reviewerAgentId, taskId,
            $"{reviewerName} approved task: {Truncate(entity.Title, 80)}");

        await _db.SaveChangesAsync();
        return TaskQueryService.BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Requests changes on a task after review. Records the reviewer and increments review rounds.
    /// </summary>
    public async Task<TaskSnapshot> RequestChangesAsync(string taskId, string reviewerAgentId, string findings)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var currentStatus = entity.Status;
        if (currentStatus != nameof(Shared.Models.TaskStatus.InReview) &&
            currentStatus != nameof(Shared.Models.TaskStatus.AwaitingValidation))
            throw new InvalidOperationException(
                $"Task '{taskId}' is in '{currentStatus}' state — must be InReview or AwaitingValidation to request changes");

        if (entity.ReviewRounds >= MaxReviewRounds)
            throw new InvalidOperationException(
                $"Task '{taskId}' has reached the maximum of {MaxReviewRounds} review rounds. Consider cancelling the task or breaking it into smaller pieces.");

        var now = DateTime.UtcNow;
        entity.Status = nameof(Shared.Models.TaskStatus.ChangesRequested);
        entity.ReviewerAgentId = reviewerAgentId;
        entity.ReviewRounds++;
        entity.UpdatedAt = now;

        var reviewerName = _catalog.Agents.FirstOrDefault(a => a.Id == reviewerAgentId)?.Name ?? reviewerAgentId;

        if (!string.IsNullOrEmpty(entity.RoomId))
        {
            var msgEntity = CreateMessageEntity(entity.RoomId, MessageKind.Review,
                $"🔄 **Changes Requested** by {reviewerName}\n\n{findings}", null, now);
            _db.Messages.Add(msgEntity);
        }

        Publish(ActivityEventType.TaskChangesRequested, entity.RoomId, reviewerAgentId, taskId,
            $"{reviewerName} requested changes on task: {Truncate(entity.Title, 80)}");

        await _db.SaveChangesAsync();
        return TaskQueryService.BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Rejects a task (from Approved or Completed state). Updates status, adds review message,
    /// publishes activity. Does NOT save — the caller owns the unit of work so room/breakout
    /// reopen can be committed atomically with the rejection.
    /// </summary>
    public async Task<RejectTaskResult> RejectTaskCoreAsync(
        string taskId, string reviewerAgentId, string reason, string? revertCommitSha = null)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var currentStatus = entity.Status;
        if (currentStatus != nameof(Shared.Models.TaskStatus.Approved) &&
            currentStatus != nameof(Shared.Models.TaskStatus.Completed))
            throw new InvalidOperationException(
                $"Task '{taskId}' is in '{currentStatus}' state — must be Approved or Completed to reject");

        if (entity.ReviewRounds >= MaxReviewRounds)
            throw new InvalidOperationException(
                $"Task '{taskId}' has reached the maximum of {MaxReviewRounds} review rounds. Consider cancelling the task or breaking it into smaller pieces.");

        var now = DateTime.UtcNow;
        var wasCompleted = currentStatus == nameof(Shared.Models.TaskStatus.Completed);

        entity.Status = nameof(Shared.Models.TaskStatus.ChangesRequested);
        entity.ReviewerAgentId = reviewerAgentId;
        entity.ReviewRounds++;
        entity.UpdatedAt = now;

        if (wasCompleted)
        {
            entity.MergeCommitSha = null;
            entity.CompletedAt = null;
        }

        var reviewerName = _catalog.Agents.FirstOrDefault(a => a.Id == reviewerAgentId)?.Name ?? reviewerAgentId;

        var statusNote = revertCommitSha is not null ? " (merge reverted)" : "";
        if (!string.IsNullOrEmpty(entity.RoomId))
        {
            var msgEntity = CreateMessageEntity(entity.RoomId, MessageKind.Review,
                $"❌ **Rejected** by {reviewerName}{statusNote}\n\n{reason}", null, now);
            _db.Messages.Add(msgEntity);
        }

        Publish(ActivityEventType.TaskRejected, entity.RoomId, reviewerAgentId, taskId,
            $"{reviewerName} rejected task: {Truncate(entity.Title, 80)}");

        // NOTE: Does NOT call SaveChangesAsync — the caller (TaskOrchestrationService.RejectTaskAsync)
        // performs room/breakout reopen and saves everything in one atomic commit.

        return new RejectTaskResult(
            Snapshot: TaskQueryService.BuildTaskSnapshot(entity),
            RoomId: entity.RoomId,
            TaskId: taskId,
            ReviewerName: reviewerName);
    }
}
