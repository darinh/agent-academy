using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Exposes git worktree status for operator visibility into agent work-in-progress.
/// </summary>
[ApiController]
[Route("api/worktrees")]
public class WorktreeController : ControllerBase
{
    private readonly WorktreeService _worktreeService;
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<WorktreeController> _logger;

    public WorktreeController(
        WorktreeService worktreeService,
        AgentAcademyDbContext db,
        ILogger<WorktreeController> logger)
    {
        _worktreeService = worktreeService;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/worktrees — list all active worktrees with git status and linked task/agent info.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<WorktreeStatusSnapshot>>> GetAll(CancellationToken cancellationToken)
    {
        var worktrees = _worktreeService.GetActiveWorktrees();
        if (worktrees.Count == 0)
            return Ok(new List<WorktreeStatusSnapshot>());

        // Batch query: fetch all tasks that match any active worktree branch
        var branches = worktrees.Select(w => w.Branch).ToList();
        var tasksByBranch = await _db.Tasks
            .Where(t => t.BranchName != null && branches.Contains(t.BranchName))
            .Select(t => new TaskBranchInfo(
                t.BranchName!,
                t.Id,
                t.Title,
                t.Status,
                t.AssignedAgentId,
                t.AssignedAgentName,
                t.UpdatedAt))
            .ToListAsync(cancellationToken);

        // Build a lookup keyed by branch — prefer the most recent non-completed task
        var taskLookup = tasksByBranch
            .GroupBy(t => t.BranchName)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(t => t.Status != "Completed" ? 1 : 0)
                      .ThenByDescending(t => t.UpdatedAt)
                      .First());

        var repoRoot = _worktreeService.RepositoryRoot;

        // Collect status for each worktree with bounded parallelism
        var semaphore = new SemaphoreSlim(4);
        var statusTasks = worktrees.Select(async wt =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await BuildSnapshotAsync(wt, taskLookup, repoRoot, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var snapshots = await Task.WhenAll(statusTasks);
        return Ok(snapshots.ToList());
    }

    private async Task<WorktreeStatusSnapshot> BuildSnapshotAsync(
        WorktreeInfo wt,
        Dictionary<string, TaskBranchInfo> taskLookup,
        string repoRoot,
        CancellationToken cancellationToken)
    {
        var relativePath = Path.GetRelativePath(repoRoot, wt.Path);
        WorktreeGitStatus gitStatus;

        try
        {
            gitStatus = await _worktreeService.GetWorktreeGitStatusAsync(wt.Path, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get git status for worktree {Branch}", wt.Branch);
            gitStatus = WorktreeGitStatus.Unavailable(ex.Message);
        }

        taskLookup.TryGetValue(wt.Branch, out var task);

        return new WorktreeStatusSnapshot(
            Branch: wt.Branch,
            RelativePath: relativePath,
            CreatedAt: wt.CreatedAt,
            StatusAvailable: gitStatus.StatusAvailable,
            Error: gitStatus.Error,
            TotalDirtyFiles: gitStatus.TotalDirtyFiles,
            DirtyFilesPreview: gitStatus.DirtyFilesPreview,
            FilesChanged: gitStatus.FilesChanged,
            Insertions: gitStatus.Insertions,
            Deletions: gitStatus.Deletions,
            LastCommitSha: gitStatus.LastCommitSha,
            LastCommitMessage: gitStatus.LastCommitMessage,
            LastCommitAuthor: gitStatus.LastCommitAuthor,
            LastCommitDate: gitStatus.LastCommitDate,
            TaskId: task?.TaskId,
            TaskTitle: task?.Title,
            TaskStatus: task?.Status,
            AgentId: task?.AssignedAgentId,
            AgentName: task?.AssignedAgentName
        );
    }

    private sealed record TaskBranchInfo(
        string BranchName,
        string TaskId,
        string Title,
        string Status,
        string? AssignedAgentId,
        string? AssignedAgentName,
        DateTime UpdatedAt);
}
