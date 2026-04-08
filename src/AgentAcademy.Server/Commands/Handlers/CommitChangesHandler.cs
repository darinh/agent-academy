using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles COMMIT_CHANGES — commits staged changes with a message.
/// Available to all roles (unlike SHELL git-commit which is Planner/Reviewer only).
/// Designed for SoftwareEngineer agents to commit their own work after using write_file.
/// </summary>
public sealed class CommitChangesHandler : ICommandHandler
{
    private readonly GitService _gitService;
    private readonly ILogger<CommitChangesHandler> _logger;

    public CommitChangesHandler(GitService gitService, ILogger<CommitChangesHandler> logger)
    {
        _gitService = gitService;
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
            var commitSha = await _gitService.CommitAsync(message, context.GitIdentity);

            _logger.LogInformation(
                "COMMIT_CHANGES by {AgentId} ({Role}): {CommitSha} — {Message}",
                context.AgentId, context.AgentRole, commitSha, message);

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
