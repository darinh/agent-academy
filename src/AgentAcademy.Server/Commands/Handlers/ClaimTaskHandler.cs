using System.Collections.Concurrent;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CLAIM_TASK — assigns the calling agent to a task, preventing duplicate claims.
/// Auto-activates tasks in Queued status.
///
/// Lazily provisions a git branch + worktree when the claimed task has none. This is
/// the catch-up path for tasks created by <c>CREATE_TASK_ITEM</c> (which does not
/// allocate git resources). Without it, downstream <c>CREATE_PR</c> fails with
/// "Task has no branch" and breakout agents are forced to write into the develop
/// checkout, contaminating it across parallel tasks (P1.9-blocker-B).
///
/// Provisioning is best-effort: if branch or worktree creation fails we keep the
/// successful claim, log a warning, and surface the failure in the command result.
/// The caller can still operate against develop while a human investigates.
/// </summary>
public sealed class ClaimTaskHandler : ICommandHandler
{
    public string CommandName => "CLAIM_TASK";

    /// <summary>
    /// Per-task semaphores serialize provisioning across concurrent CLAIM_TASK
    /// calls. The atomic claim in <c>TaskLifecycleService.ClaimTaskAsync</c>
    /// allows the same agent to win twice (idempotent re-claim), which would
    /// otherwise drive two parallel branch-creation flows for one task.
    ///
    /// Memory profile: entries are not removed because removing them safely
    /// would require holding the dictionary against a re-entry race. Each
    /// SemaphoreSlim is ~80 bytes, bounded by the number of distinct tasks
    /// the process has ever observed. For a typical server lifetime
    /// (thousands of tasks before restart) this is &lt; 10 MB and acceptable.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _provisionLocks = new();

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId
            || string.IsNullOrWhiteSpace(taskId))
        {
            // Also accept "value" key (single-arg shorthand: CLAIM_TASK: task-123)
            if (!command.Args.TryGetValue("value", out taskIdObj) || taskIdObj is not string taskIdValue
                || string.IsNullOrWhiteSpace(taskIdValue))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "Missing required argument: taskId"
                };
            }
            taskId = taskIdValue;
        }

        var taskLifecycle = context.Services.GetRequiredService<ITaskLifecycleService>();

        TaskSnapshot task;
        try
        {
            task = await taskLifecycle.ClaimTaskAsync(taskId, context.AgentId, context.AgentName);
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Infer(ex.Message),
                Error = ex.Message
            };
        }

        // Lazily provision branch + worktree if the task has none. Failures are
        // logged and surfaced but do NOT roll back the successful claim.
        var (effectiveBranch, provisionWarning) =
            await TryProvisionBranchAndWorktreeAsync(task, context);

        var result = new Dictionary<string, object?>
        {
            ["taskId"] = task.Id,
            ["title"] = task.Title,
            ["status"] = task.Status.ToString(),
            ["assignedTo"] = task.AssignedAgentName,
            ["branchName"] = effectiveBranch ?? task.BranchName,
            ["message"] = $"Task '{task.Title}' claimed successfully"
        };

        if (provisionWarning is not null)
            result["warning"] = provisionWarning;

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
    }

    /// <summary>
    /// Provisions a task branch and worktree when the task has none, then records
    /// the branch on the task. Returns the branch name in effect (the existing
    /// branch, the freshly-provisioned branch, or null when no provisioning was
    /// possible) and a warning string if any step failed.
    /// </summary>
    private static async Task<(string? branch, string? warning)> TryProvisionBranchAndWorktreeAsync(
        TaskSnapshot task, CommandContext context)
    {
        var gitService = context.Services.GetService<IGitService>();
        var worktreeService = context.Services.GetService<IWorktreeService>();
        var taskQueryService = context.Services.GetService<ITaskQueryService>();

        // If git/worktree services aren't registered, log a warning so production
        // misconfigurations are visible in logs. In tests these are typically
        // omitted on purpose — the warning is harmless because test loggers
        // discard or NullLogger is used.
        if (gitService is null || worktreeService is null || taskQueryService is null)
        {
            LogWarning(context, null,
                "CLAIM_TASK provisioning skipped for task {TaskId}: git={GitMissing} worktree={WtMissing} taskQuery={TqMissing}. " +
                "If this is production the task will hit 'Task has no branch' on CREATE_PR.",
                task.Id, gitService is null, worktreeService is null, taskQueryService is null);
            return (null, null);
        }

        // Already has a branch — leave it alone (write-once invariant in
        // UpdateTaskBranchAsync). Opportunistically ensure the worktree exists
        // on disk; CreateWorktreeAsync is idempotent.
        if (!string.IsNullOrWhiteSpace(task.BranchName))
        {
            try
            {
                await worktreeService.CreateWorktreeAsync(task.BranchName);
                return (task.BranchName, null);
            }
            catch (Exception ex)
            {
                LogWarning(context, ex,
                    "Failed to ensure worktree for existing branch {Branch} on task {TaskId}",
                    task.BranchName, task.Id);
                return (task.BranchName,
                    $"Existing branch '{task.BranchName}' has no worktree on disk: {ex.Message}");
            }
        }

        // Serialize concurrent provisioning attempts for the same task. The
        // atomic claim guarantees at most one agent owns the task, but allows
        // the same agent to re-claim — without this lock that path would fork
        // into two parallel branch-creation flows.
        var sem = _provisionLocks.GetOrAdd(task.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            // Re-check inside the lock: a concurrent claim may have already
            // provisioned. Source-of-truth is the persisted row, queried with
            // AsNoTracking so the scoped DbContext's change-tracker cache (which
            // already loaded the entity in ClaimTaskAsync with the pre-claim
            // BranchName) doesn't mask the freshly-persisted value.
            var freshBranch = await GetPersistedBranchNameAsync(context, task.Id);
            if (!string.IsNullOrWhiteSpace(freshBranch))
            {
                try
                {
                    await worktreeService.CreateWorktreeAsync(freshBranch);
                    return (freshBranch, null);
                }
                catch (Exception ex)
                {
                    LogWarning(context, ex,
                        "Failed to ensure worktree for concurrently-provisioned branch {Branch} on task {TaskId}",
                        freshBranch, task.Id);
                    return (freshBranch,
                        $"Branch '{freshBranch}' has no worktree on disk: {ex.Message}");
                }
            }

            return await DoProvisionAsync(task, gitService, worktreeService, taskQueryService, context);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Reads the persisted BranchName for a task, bypassing the scoped
    /// DbContext's change tracker so concurrent writes from sibling scopes
    /// are visible. Returns null on any failure (caller falls through to
    /// normal provisioning, which is still serialized by the per-task lock).
    /// </summary>
    private static async Task<string?> GetPersistedBranchNameAsync(CommandContext context, string taskId)
    {
        var db = context.Services.GetService<AgentAcademyDbContext>();
        if (db is null) return null;
        try
        {
            return await db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => t.BranchName)
                .FirstOrDefaultAsync();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string? branch, string? warning)> DoProvisionAsync(
        TaskSnapshot task,
        IGitService gitService,
        IWorktreeService worktreeService,
        ITaskQueryService taskQueryService,
        CommandContext context)
    {
        string? createdBranch = null;
        var returnedToDevelop = false;
        try
        {
            // CreateTaskBranchAsync does `checkout -b`, leaving the shared
            // checkout pointing at the new branch. ReturnToDevelopAsync MUST
            // run on every code path that exits this method to avoid the
            // cross-task contamination this fix is supposed to prevent.
            createdBranch = await gitService.CreateTaskBranchAsync(task.Title);
            await gitService.ReturnToDevelopAsync(createdBranch);
            returnedToDevelop = true;
            await worktreeService.CreateWorktreeAsync(createdBranch);
            await taskQueryService.UpdateTaskBranchAsync(task.Id, createdBranch);
            return (createdBranch, null);
        }
        catch (Exception ex)
        {
            LogWarning(context, ex,
                "Failed to provision branch/worktree for claimed task {TaskId} (branch={Branch})",
                task.Id, createdBranch ?? "(not created)");

            // Defensive: only restore develop if we haven't already done so
            // successfully. ReturnToDevelopAsync stashes the working tree
            // unconditionally, so calling it again from develop would stash
            // unrelated develop changes under the task-branch label and
            // potentially pop them onto the task branch later.
            if (createdBranch is not null && !returnedToDevelop)
            {
                try
                {
                    await gitService.ReturnToDevelopAsync(createdBranch);
                }
                catch (Exception restoreEx)
                {
                    LogWarning(context, restoreEx,
                        "CRITICAL: failed to restore shared checkout to develop after provisioning failure for task {TaskId}; checkout may still be on {Branch}",
                        task.Id, createdBranch);
                }
            }

            // Best-effort: record the branch name even if worktree creation
            // failed, so a subsequent CLAIM_TASK won't try to create a
            // duplicate branch.
            if (!string.IsNullOrWhiteSpace(createdBranch))
            {
                try { await taskQueryService.UpdateTaskBranchAsync(task.Id, createdBranch); }
                catch { /* logged above; non-fatal */ }
            }

            return (createdBranch,
                $"Branch/worktree provisioning failed: {ex.Message}. " +
                "PR creation may fail until a human repairs the task infrastructure.");
        }
    }

    private static void LogWarning(
        CommandContext context, Exception? ex, string template, params object?[] args)
    {
        var loggerFactory = context.Services.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<ClaimTaskHandler>();
        if (logger is null) return;
        if (ex is null) logger.LogWarning(template, args);
        else logger.LogWarning(ex, template, args);
    }
}
