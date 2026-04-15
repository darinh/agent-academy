using System.Text.Json;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Pure mapping functions that convert EF Core entities to shared DTOs.
/// Stateless and dependency-free — safe to call from any service layer.
///
/// Extracted from <c>TaskQueryService</c> to break the cross-service
/// static coupling where TaskLifecycleService, TaskEvidenceService, and
/// others were calling <c>TaskQueryService.BuildTaskSnapshot()</c>.
/// </summary>
public static class TaskSnapshotFactory
{
    /// <summary>
    /// Maps a <see cref="TaskEntity"/> to a <see cref="TaskSnapshot"/> DTO.
    /// JSON-stored list fields are deserialized; enum-stored-as-string fields
    /// are parsed with safe defaults.
    /// </summary>
    public static TaskSnapshot BuildTaskSnapshot(
        TaskEntity entity,
        int commentCount = 0,
        List<string>? dependsOnIds = null,
        List<string>? blockingIds = null)
    {
        return new TaskSnapshot(
            Id: entity.Id,
            Title: entity.Title,
            Description: entity.Description,
            SuccessCriteria: entity.SuccessCriteria,
            Status: Enum.Parse<TaskStatus>(entity.Status),
            Type: Enum.TryParse<TaskType>(entity.Type, out var tt) ? tt : TaskType.Feature,
            CurrentPhase: Enum.Parse<CollaborationPhase>(entity.CurrentPhase),
            CurrentPlan: entity.CurrentPlan,
            ValidationStatus: Enum.Parse<WorkstreamStatus>(entity.ValidationStatus),
            ValidationSummary: entity.ValidationSummary,
            ImplementationStatus: Enum.Parse<WorkstreamStatus>(entity.ImplementationStatus),
            ImplementationSummary: entity.ImplementationSummary,
            PreferredRoles: JsonSerializer.Deserialize<List<string>>(entity.PreferredRoles) ?? [],
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            Size: string.IsNullOrEmpty(entity.Size) ? null : Enum.Parse<TaskSize>(entity.Size),
            StartedAt: entity.StartedAt,
            CompletedAt: entity.CompletedAt,
            AssignedAgentId: entity.AssignedAgentId,
            AssignedAgentName: entity.AssignedAgentName,
            UsedFleet: entity.UsedFleet,
            FleetModels: JsonSerializer.Deserialize<List<string>>(entity.FleetModels) ?? [],
            BranchName: entity.BranchName,
            PullRequestUrl: entity.PullRequestUrl,
            PullRequestNumber: entity.PullRequestNumber,
            PullRequestStatus: string.IsNullOrEmpty(entity.PullRequestStatus)
                ? null
                : Enum.Parse<PullRequestStatus>(entity.PullRequestStatus),
            ReviewerAgentId: entity.ReviewerAgentId,
            ReviewRounds: entity.ReviewRounds,
            TestsCreated: JsonSerializer.Deserialize<List<string>>(entity.TestsCreated) ?? [],
            CommitCount: entity.CommitCount,
            MergeCommitSha: entity.MergeCommitSha,
            CommentCount: commentCount,
            WorkspacePath: entity.WorkspacePath,
            SprintId: entity.SprintId,
            DependsOnTaskIds: dependsOnIds,
            BlockingTaskIds: blockingIds,
            Priority: Enum.IsDefined(typeof(TaskPriority), entity.Priority)
                ? (TaskPriority)entity.Priority
                : TaskPriority.Medium
        );
    }

    /// <summary>
    /// Maps a <see cref="TaskCommentEntity"/> to a <see cref="TaskComment"/> DTO.
    /// </summary>
    public static TaskComment BuildTaskComment(TaskCommentEntity entity)
    {
        return new TaskComment(
            Id: entity.Id,
            TaskId: entity.TaskId,
            AgentId: entity.AgentId,
            AgentName: entity.AgentName,
            CommentType: Enum.TryParse<TaskCommentType>(entity.CommentType, out var ct)
                ? ct
                : TaskCommentType.Comment,
            Content: entity.Content,
            CreatedAt: entity.CreatedAt
        );
    }

    /// <summary>
    /// Maps a <see cref="TaskEvidenceEntity"/> to a <see cref="TaskEvidence"/> DTO.
    /// </summary>
    public static TaskEvidence BuildTaskEvidence(TaskEvidenceEntity entity)
    {
        return new TaskEvidence(
            Id: entity.Id,
            TaskId: entity.TaskId,
            Phase: Enum.TryParse<EvidencePhase>(entity.Phase, out var p) ? p : EvidencePhase.After,
            CheckName: entity.CheckName,
            Tool: entity.Tool,
            Command: entity.Command,
            ExitCode: entity.ExitCode,
            OutputSnippet: entity.OutputSnippet,
            Passed: entity.Passed,
            AgentId: entity.AgentId,
            AgentName: entity.AgentName,
            CreatedAt: entity.CreatedAt
        );
    }

    /// <summary>
    /// Maps a <see cref="SpecTaskLinkEntity"/> to a <see cref="SpecTaskLink"/> DTO.
    /// </summary>
    public static SpecTaskLink BuildSpecTaskLink(SpecTaskLinkEntity entity)
    {
        return new SpecTaskLink(
            Id: entity.Id,
            TaskId: entity.TaskId,
            SpecSectionId: entity.SpecSectionId,
            LinkType: Enum.TryParse<SpecLinkType>(entity.LinkType, out var lt)
                ? lt
                : SpecLinkType.Implements,
            LinkedByAgentId: entity.LinkedByAgentId,
            LinkedByAgentName: entity.LinkedByAgentName,
            Note: entity.Note,
            CreatedAt: entity.CreatedAt
        );
    }
}
