using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles COMMIT_CHANGES — commits staged changes with a message.
/// Available to all roles (unlike SHELL git-commit which is Planner/Reviewer only).
/// Designed for SoftwareEngineer agents to commit their own work after using write_file.
/// </summary>
public sealed class CommitChangesHandler : ICommandHandler
{
    private readonly IGitService _gitService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommitChangesHandler> _logger;

    public CommitChangesHandler(IGitService gitService, IServiceScopeFactory scopeFactory, ILogger<CommitChangesHandler> logger)
    {
        _gitService = gitService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string CommandName => "COMMIT_CHANGES";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var message = GetTrimmed(command.Args, "message") ?? GetTrimmed(command.Args, "value");
        if (string.IsNullOrWhiteSpace(message))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: message. Provide a conventional commit message (e.g., 'feat: add user endpoint')."
            };
        }

        if (message.Length > 5000)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Commit message exceeds 5000 characters."
            };
        }

        if (!ConventionalCommitMessage.TryValidate(message, out var validationError))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = validationError
            };
        }

        // P1.9 blocker D: refuse to commit against the develop checkout.
        // The structured COMMIT_CHANGES path mirrors the SDK write_file/commit_changes
        // wrappers — without this guard, an agent in the main room could route around
        // the wrapper-level refusal by emitting a structured command instead.
        var worktreeRefusal = TryRefuseMainCheckoutCommit(context.WorkingDirectory);
        if (worktreeRefusal is not null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = worktreeRefusal
            };
        }

        try
        {
            string commitSha;
            if (context.WorkingDirectory is not null)
                commitSha = await _gitService.CommitInDirAsync(context.WorkingDirectory, message, context.GitIdentity);
            else
                commitSha = await _gitService.CommitAsync(message, context.GitIdentity);

            _logger.LogInformation(
                "COMMIT_CHANGES by {AgentId} ({Role}): {CommitSha} — {Message}",
                context.AgentId, context.AgentRole, commitSha, message);

            // Record committed files as room artifacts
            try
            {
                using var artifactScope = _scopeFactory.CreateScope();
                var tracker = artifactScope.ServiceProvider.GetRequiredService<IRoomArtifactTracker>();
                var files = await _gitService.GetFilesInCommitAsync(commitSha, context.WorkingDirectory);
                await tracker.RecordCommitAsync(context.RoomId, context.AgentId, commitSha, files);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record commit artifacts for {Sha}", commitSha);
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["commitSha"] = commitSha,
                    ["message"] = message,
                    ["success"] = true
                }
            };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("no changes", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Nothing to commit. Stage files first (write_file auto-stages, or use other file operations)."
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "COMMIT_CHANGES failed for {AgentId}", context.AgentId);
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Commit failed: {ex.Message}"
            };
        }
    }

    private static string? GetTrimmed(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value is string text
            ? text.Trim()
            : null;

    /// <summary>
    /// P1.9 blocker D: refuses COMMIT_CHANGES when the working directory is
    /// missing or is the main develop checkout (rather than a per-task linked
    /// git worktree). Returns null when the commit may proceed; returns a
    /// user-facing refusal string otherwise. Mirrors
    /// <c>CodeWriteToolWrapper.TryRefuseMainCheckoutWrite</c>.
    /// </summary>
    /// <remarks>
    /// Codex review caught the gap: the orchestrator's per-agent workspace
    /// resolver falls back to <c>roomWorkspacePath</c> (= the develop checkout)
    /// when the agent has no claimed task. Without classifying the supplied
    /// directory we'd commit there silently. Fail-closed when classification
    /// is unavailable to avoid silently re-introducing the contamination.
    /// </remarks>
    private static string? TryRefuseMainCheckoutCommit(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return "Cannot commit_changes from the develop checkout. " +
                   "Call CLAIM_TASK <taskId> first to provision a per-task worktree, " +
                   "then retry on your next turn. (P1.9 blocker D enforcement.)";

        var classification = ScopeRootValidator.IsLinkedWorktree(workingDirectory);
        if (classification == false)
            return "Cannot commit_changes from the develop checkout (cwd=" + workingDirectory + "). " +
                   "Call CLAIM_TASK <taskId> first to provision a per-task worktree, " +
                   "then retry on your next turn. (P1.9 blocker D enforcement.)";

        if (classification is null)
            return "commit_changes could not verify the working directory is a per-task worktree " +
                   "(git classification unavailable for cwd=" + workingDirectory + "). " +
                   "Refusing rather than risking commits to the develop checkout. " +
                   "Call CLAIM_TASK <taskId> first to provision a per-task worktree, then retry.";

        // classification == true (linked worktree) → allow
        return null;
    }
}
