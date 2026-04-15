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
}
