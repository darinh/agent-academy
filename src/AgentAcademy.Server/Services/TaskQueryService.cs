using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles task queries and low-coupling task mutations (no room/messaging/activity side-effects).
/// </summary>
public sealed class TaskQueryService : ITaskQueryService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<TaskQueryService> _logger;
    private readonly IAgentCatalog _catalog;
    private readonly TaskDependencyService _dependencies;

    public TaskQueryService(
        AgentAcademyDbContext db,
        ILogger<TaskQueryService> logger,
        IAgentCatalog catalog,
        TaskDependencyService dependencies)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _dependencies = dependencies;
    }

    // ── Task Queries ────────────────────────────────────────────

    /// <summary>
    /// Returns all tasks, optionally filtered by sprint. Scoped to active workspace.
    /// </summary>
    public async Task<List<TaskSnapshot>> GetTasksAsync(string? sprintId = null)
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();
        var query = _db.Tasks.AsQueryable();

        if (activeWorkspace is not null)
        {
            query = query.Where(t => t.WorkspacePath == activeWorkspace
                || (t.WorkspacePath == null && t.RoomId != null
                    && _db.Rooms.Any(r => r.Id == t.RoomId && r.WorkspacePath == activeWorkspace)));
        }

        if (sprintId is not null)
        {
            query = query.Where(t => t.SprintId == sprintId);
        }

        var entities = await query.OrderBy(t => t.Priority).ThenByDescending(t => t.CreatedAt).ToListAsync();
        var taskIds = entities.Select(e => e.Id).ToList();
        var depMap = await _dependencies.GetBatchDependencyIdsAsync(taskIds);
        return entities.Select(e =>
        {
            depMap.TryGetValue(e.Id, out var deps);
            return TaskSnapshotFactory.BuildTaskSnapshot(e, dependsOnIds: deps.DependsOn, blockingIds: deps.Blocking);
        }).ToList();
    }

    /// <summary>
    /// Returns a specific task by ID, or null if not found.
    /// </summary>
    public async Task<TaskSnapshot?> GetTaskAsync(string taskId)
    {
        var entity = await _db.Tasks.FindAsync(taskId);
        if (entity is null) return null;
        var commentCount = await _db.TaskComments.CountAsync(c => c.TaskId == taskId);
        var depMap = await _dependencies.GetBatchDependencyIdsAsync([taskId]);
        depMap.TryGetValue(taskId, out var deps);
        return TaskSnapshotFactory.BuildTaskSnapshot(entity, commentCount, deps.DependsOn, deps.Blocking);
    }

    /// <summary>
    /// Finds a task by title. Returns the first non-cancelled match, or null.
    /// </summary>
    public async Task<TaskSnapshot?> FindTaskByTitleAsync(string title)
    {
        var entity = await _db.Tasks
            .Where(t => t.Title == title && t.Status != nameof(Shared.Models.TaskStatus.Cancelled))
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();
        if (entity is null) return null;
        var commentCount = await _db.TaskComments.CountAsync(c => c.TaskId == entity.Id);
        return TaskSnapshotFactory.BuildTaskSnapshot(entity, commentCount);
    }

    /// <summary>
    /// Returns tasks that are pending review (InReview or AwaitingValidation).
    /// </summary>
    public async Task<List<TaskSnapshot>> GetReviewQueueAsync()
    {
        var reviewStatuses = new[]
        {
            nameof(Shared.Models.TaskStatus.InReview),
            nameof(Shared.Models.TaskStatus.AwaitingValidation)
        };

        var entities = await _db.Tasks
            .Where(t => reviewStatuses.Contains(t.Status))
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        return entities.Select(e => TaskSnapshotFactory.BuildTaskSnapshot(e)).ToList();
    }

    /// <summary>
    /// Returns task IDs that have open (non-terminal) pull requests for polling.
    /// </summary>
    public async Task<List<(string TaskId, int PrNumber)>> GetTasksWithActivePrsAsync()
    {
        var terminalStatuses = new[]
        {
            nameof(PullRequestStatus.Merged),
            nameof(PullRequestStatus.Closed)
        };

        return await _db.Tasks
            .Where(t => t.PullRequestNumber != null
                && t.PullRequestStatus != null
                && !terminalStatuses.Contains(t.PullRequestStatus))
            .Select(t => new { t.Id, PrNumber = t.PullRequestNumber!.Value })
            .AsAsyncEnumerable()
            .Select(t => (t.Id, t.PrNumber))
            .ToListAsync();
    }

    /// <summary>
    /// Gets all comments for a task, ordered by creation time.
    /// </summary>
    public async Task<List<TaskComment>> GetTaskCommentsAsync(string taskId)
    {
        _ = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var comments = await _db.TaskComments
            .Where(c => c.TaskId == taskId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        return comments.Select(TaskSnapshotFactory.BuildTaskComment).ToList();
    }

    /// <summary>
    /// Gets the count of comments for a task.
    /// </summary>
    public async Task<int> GetTaskCommentCountAsync(string taskId)
    {
        return await _db.TaskComments.CountAsync(c => c.TaskId == taskId);
    }

    /// <summary>
    /// Gets all evidence for a task, optionally filtered by phase.
    /// </summary>
    public async Task<List<TaskEvidence>> GetTaskEvidenceAsync(string taskId, EvidencePhase? phase = null)
    {
        _ = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var query = _db.TaskEvidence
            .Where(e => e.TaskId == taskId);

        if (phase.HasValue)
            query = query.Where(e => e.Phase == phase.Value.ToString());

        var entities = await query.OrderBy(e => e.CreatedAt).ToListAsync();
        return entities.Select(TaskSnapshotFactory.BuildTaskEvidence).ToList();
    }

    /// <summary>
    /// Gets all spec links for a task.
    /// </summary>
    public async Task<List<SpecTaskLink>> GetSpecLinksForTaskAsync(string taskId)
    {
        _ = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var links = await _db.SpecTaskLinks
            .Where(l => l.TaskId == taskId)
            .OrderBy(l => l.SpecSectionId)
            .ToListAsync();

        return links.Select(TaskSnapshotFactory.BuildSpecTaskLink).ToList();
    }

    /// <summary>
    /// Gets all tasks linked to a spec section.
    /// </summary>
    public async Task<List<SpecTaskLink>> GetTasksForSpecAsync(string specSectionId)
    {
        var links = await _db.SpecTaskLinks
            .Where(l => l.SpecSectionId == specSectionId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

        return links.Select(TaskSnapshotFactory.BuildSpecTaskLink).ToList();
    }

    /// <summary>
    /// Gets tasks that have no spec links.
    /// </summary>
    public async Task<List<TaskSnapshot>> GetUnlinkedTasksAsync()
    {
        var linkedTaskIds = await _db.SpecTaskLinks
            .Select(l => l.TaskId)
            .Distinct()
            .ToListAsync();

        var unlinkedTasks = await _db.Tasks
            .Where(t => !linkedTaskIds.Contains(t.Id)
                && t.Status != "Completed" && t.Status != "Cancelled")
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        var snapshots = new List<TaskSnapshot>();
        foreach (var entity in unlinkedTasks)
        {
            var commentCount = await _db.TaskComments.CountAsync(c => c.TaskId == entity.Id);
            snapshots.Add(TaskSnapshotFactory.BuildTaskSnapshot(entity, commentCount));
        }
        return snapshots;
    }

    // ── Task Mutations (no side-effects) ────────────────────────

    /// <summary>
    /// Assigns an agent to a task. Validates the agent exists in the catalog.
    /// </summary>
    public async Task<TaskSnapshot> AssignTaskAsync(string taskId, string agentId, string agentName)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == agentId);
        if (agent is not null)
        {
            entity.AssignedAgentId = agent.Id;
            entity.AssignedAgentName = agent.Name;
        }
        else
        {
            entity.AssignedAgentId = agentId;
            entity.AssignedAgentName = agentName;
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return TaskSnapshotFactory.BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Updates a task's status. Automatically sets StartedAt/CompletedAt as appropriate.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskStatusAsync(string taskId, Shared.Models.TaskStatus status)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        // Block activation of tasks with unmet dependencies
        if (status == Shared.Models.TaskStatus.Active)
        {
            var blockers = await _dependencies.GetBlockingTasksAsync(taskId);
            if (blockers.Count > 0)
            {
                var blockerList = string.Join(", ", blockers.Select(b => $"'{b.Title}' ({b.Status})"));
                throw new InvalidOperationException(
                    $"Cannot activate task '{taskId}' — unmet dependencies: {blockerList}");
            }
        }

        var now = DateTime.UtcNow;
        entity.Status = status.ToString();
        entity.UpdatedAt = now;

        if (status == Shared.Models.TaskStatus.Active && entity.StartedAt is null)
            entity.StartedAt = now;

        if (status == Shared.Models.TaskStatus.Completed || status == Shared.Models.TaskStatus.Cancelled)
            entity.CompletedAt = now;
        else
            entity.CompletedAt = null;

        await _db.SaveChangesAsync();
        return TaskSnapshotFactory.BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Updates a task's priority level.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskPriorityAsync(string taskId, TaskPriority priority)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        entity.Priority = (int)priority;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return TaskSnapshotFactory.BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Records a branch name on a task. Branch metadata is write-once per task.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskBranchAsync(string taskId, string branchName)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task ID is required.", nameof(taskId));
        if (string.IsNullOrWhiteSpace(branchName))
            throw new ArgumentException("Branch name is required.", nameof(branchName));

        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        if (string.IsNullOrWhiteSpace(entity.BranchName))
        {
            entity.BranchName = branchName;
            entity.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return TaskSnapshotFactory.BuildTaskSnapshot(entity);
        }

        if (string.Equals(entity.BranchName, branchName, StringComparison.Ordinal))
            return TaskSnapshotFactory.BuildTaskSnapshot(entity);

        _logger.LogError(
            "Refusing to reassign branch for task {TaskId}: existing {ExistingBranch}, attempted {AttemptedBranch}",
            taskId,
            entity.BranchName,
            branchName);
        throw new InvalidOperationException(
            $"Task '{taskId}' already has branch '{entity.BranchName}' and cannot be reassigned to '{branchName}'.");
    }

    /// <summary>
    /// Records PR information on a task.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskPrAsync(
        string taskId, string url, int number, PullRequestStatus status)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");
        entity.PullRequestUrl = url;
        entity.PullRequestNumber = number;
        entity.PullRequestStatus = status.ToString();
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return TaskSnapshotFactory.BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Removes a spec-task link.
    /// </summary>
    public async Task UnlinkTaskFromSpecAsync(string taskId, string specSectionId)
    {
        var link = await _db.SpecTaskLinks
            .FirstOrDefaultAsync(l => l.TaskId == taskId && l.SpecSectionId == specSectionId)
            ?? throw new InvalidOperationException(
                $"No link exists between task '{taskId}' and spec '{specSectionId}'");

        _db.SpecTaskLinks.Remove(link);
        await _db.SaveChangesAsync();
    }

    // ── Bulk Operations ───────────────────────────────────────

    /// <summary>
    /// Statuses that are safe for bulk update (no dedicated lifecycle handlers).
    /// </summary>
    private static readonly HashSet<Shared.Models.TaskStatus> BulkSafeStatuses =
    [
        Shared.Models.TaskStatus.Queued,
        Shared.Models.TaskStatus.Active,
        Shared.Models.TaskStatus.Blocked,
        Shared.Models.TaskStatus.AwaitingValidation,
        Shared.Models.TaskStatus.InReview,
    ];

    /// <summary>
    /// Updates the status of multiple tasks. Skips tasks that fail validation
    /// (not found, dependency-blocked) and returns per-item results.
    /// </summary>
    public async Task<BulkOperationResult> BulkUpdateStatusAsync(
        IReadOnlyList<string> taskIds, Shared.Models.TaskStatus status)
    {
        if (!BulkSafeStatuses.Contains(status))
            throw new ArgumentException(
                $"Status '{status}' is not allowed for bulk update. " +
                $"Allowed: {string.Join(", ", BulkSafeStatuses)}.");

        var dedupedIds = taskIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var updated = new List<TaskSnapshot>();
        var errors = new List<BulkOperationError>();

        foreach (var taskId in dedupedIds)
        {
            try
            {
                var snapshot = await UpdateTaskStatusAsync(taskId, status);
                updated.Add(snapshot);
            }
            catch (InvalidOperationException ex)
            {
                var code = ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? "NOT_FOUND" : "VALIDATION";
                errors.Add(new BulkOperationError(taskId, code, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk status update failed for task '{TaskId}'", taskId);
                errors.Add(new BulkOperationError(taskId, "INTERNAL", $"Unexpected error: {ex.Message}"));
            }
        }

        return new BulkOperationResult(dedupedIds.Count, updated.Count, errors.Count, updated, errors);
    }

    /// <summary>
    /// Assigns multiple tasks to a single agent. Skips tasks that fail validation
    /// (not found) and returns per-item results.
    /// </summary>
    public async Task<BulkOperationResult> BulkAssignAsync(
        IReadOnlyList<string> taskIds, string agentId, string? agentName)
    {
        var dedupedIds = taskIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Resolve agent — if not in catalog, require agentName
        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == agentId);
        var resolvedName = agent?.Name ?? agentName;
        if (string.IsNullOrWhiteSpace(resolvedName))
            throw new ArgumentException(
                $"Agent '{agentId}' is not in the catalog. Provide agentName for unknown agents.");

        var updated = new List<TaskSnapshot>();
        var errors = new List<BulkOperationError>();

        foreach (var taskId in dedupedIds)
        {
            try
            {
                var snapshot = await AssignTaskAsync(taskId, agent?.Id ?? agentId, resolvedName);
                updated.Add(snapshot);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new BulkOperationError(taskId, "NOT_FOUND", ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk assign failed for task '{TaskId}'", taskId);
                errors.Add(new BulkOperationError(taskId, "INTERNAL", $"Unexpected error: {ex.Message}"));
            }
        }

        return new BulkOperationResult(dedupedIds.Count, updated.Count, errors.Count, updated, errors);
    }

    // ── Shared Helpers (workspace query) ────────────────────────

    internal async Task<string?> GetActiveWorkspacePathAsync()
    {
        return await _db.Workspaces
            .Where(w => w.IsActive)
            .Select(w => w.Path)
            .FirstOrDefaultAsync();
    }

}
