using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Commands;

/// <summary>
/// Orchestrates command processing: Parse → Authorize → Execute → Audit.
/// Runs in parallel with existing TASK ASSIGNMENT/WORK REPORT/REVIEW parsing.
/// </summary>
public sealed class CommandPipeline
{
    private const int MaxRetries = 2;
    private const int RetryBaseDelayMs = 1000;

    /// <summary>
    /// Error codes the pipeline will auto-retry (excludes RATE_LIMIT — that's a policy, not transient).
    /// </summary>
    private static readonly HashSet<string> PipelineRetryableCodes = new(StringComparer.Ordinal)
    {
        CommandErrorCode.Timeout,
        CommandErrorCode.Internal
    };

    private readonly CommandParser _parser;
    private readonly CommandAuthorizer _authorizer;
    private readonly CommandRateLimiter _rateLimiter;
    private readonly Dictionary<string, ICommandHandler> _handlers;
    private readonly ILogger<CommandPipeline> _logger;

    public CommandPipeline(
        IEnumerable<ICommandHandler> handlers,
        ILogger<CommandPipeline> logger,
        CommandRateLimiter? rateLimiter = null,
        CommandParser? parser = null,
        CommandAuthorizer? authorizer = null)
    {
        _handlers = handlers.ToDictionary(h => h.CommandName, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _rateLimiter = rateLimiter ?? new CommandRateLimiter();
        _parser = parser ?? new CommandParser();
        _authorizer = authorizer ?? new CommandAuthorizer();
    }

    /// <summary>
    /// Process an agent's response text for structured commands.
    /// Returns the command results and the remaining text (with commands stripped).
    /// </summary>
    public async Task<CommandPipelineResult> ProcessResponseAsync(
        string agentId,
        string responseText,
        string? roomId,
        AgentDefinition agent,
        IServiceProvider scopedServices,
        string? workingDirectory = null)
    {
        var parseResult = _parser.Parse(responseText);

        if (parseResult.Commands.Count == 0)
            return new CommandPipelineResult(new List<CommandEnvelope>(), responseText);

        _logger.LogInformation(
            "Agent {AgentId} issued {Count} command(s): {Commands}",
            agentId,
            parseResult.Commands.Count,
            string.Join(", ", parseResult.Commands.Select(c => c.Command)));

        var context = new CommandContext(
            AgentId: agentId,
            AgentName: agent.Name,
            AgentRole: agent.Role,
            RoomId: roomId,
            BreakoutRoomId: null,
            Services: scopedServices,
            GitIdentity: agent.GitIdentity,
            WorkingDirectory: workingDirectory
        );

        var results = new List<CommandEnvelope>();

        foreach (var parsed in parseResult.Commands)
        {
            var envelope = new CommandEnvelope(
                Command: parsed.Command,
                Args: new Dictionary<string, object?>(
                    parsed.Args.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)),
                    StringComparer.OrdinalIgnoreCase),
                Status: CommandStatus.Success,
                Result: null,
                Error: null,
                CorrelationId: $"cmd-{Guid.NewGuid():N}",
                Timestamp: DateTime.UtcNow,
                ExecutedBy: agentId
            );

            // Authorize
            var denied = _authorizer.Authorize(envelope, agent);
            if (denied != null)
            {
                var deniedWithCode = denied.ErrorCode is null
                    ? denied with { ErrorCode = CommandErrorCode.Permission }
                    : denied;
                _logger.LogWarning("Command {Command} denied for agent {AgentId}: {Error}",
                    parsed.Command, agentId, deniedWithCode.Error);
                await AuditAsync(deniedWithCode, roomId, scopedServices);
                results.Add(deniedWithCode);
                continue;
            }

            // Handler lookup (before rate limit and confirmation — don't waste budget on unknown commands)
            if (!_handlers.TryGetValue(parsed.Command, out var handler))
            {
                var unknown = envelope with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.NotFound,
                    Error = $"Unknown command: {parsed.Command}"
                };
                await AuditAsync(unknown, roomId, scopedServices);
                results.Add(unknown);
                continue;
            }

            // Destructive confirmation gate (before rate limiting — don't consume budget)
            if (handler.IsDestructive && !HasConfirmFlag(envelope.Args))
            {
                var confirmRequired = envelope with
                {
                    Status = CommandStatus.Denied,
                    ErrorCode = CommandErrorCode.ConfirmationRequired,
                    Error = handler.DestructiveWarning + " Re-issue with confirm=true to proceed.",
                    Result = new Dictionary<string, object?>
                    {
                        ["requiresConfirmation"] = true,
                        ["command"] = parsed.Command,
                        ["warning"] = handler.DestructiveWarning,
                        ["retryWith"] = "confirm=true"
                    }
                };
                _logger.LogInformation(
                    "Destructive command {Command} by {AgentId} requires confirmation",
                    parsed.Command, agentId);
                await AuditAsync(confirmRequired, roomId, scopedServices);
                results.Add(confirmRequired);
                continue;
            }

            // Rate limit
            if (!_rateLimiter.TryAcquire(agentId, out var retryAfterSeconds))
            {
                var rateLimited = envelope with
                {
                    Status = CommandStatus.Denied,
                    ErrorCode = CommandErrorCode.RateLimit,
                    Error = $"Rate limit exceeded. Try again in {retryAfterSeconds}s."
                };
                _logger.LogWarning(
                    "Command {Command} rate-limited for agent {AgentId}: retry after {Seconds}s",
                    parsed.Command, agentId, retryAfterSeconds);
                await AuditAsync(rateLimited, roomId, scopedServices);
                results.Add(rateLimited);
                continue;
            }

            // Execute (with optional pipeline-level retry for safe commands)
            try
            {
                var result = await ExecuteWithRetryAsync(handler, envelope, context, parsed.Command, agentId);
                await AuditAsync(result, roomId, scopedServices);
                results.Add(result);

                _logger.LogInformation("Command {Command} executed by {AgentId}: {Status}{Retries}",
                    parsed.Command, agentId, result.Status,
                    result.RetryCount > 0 ? $" (after {result.RetryCount} retries)" : "");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command {Command} failed for agent {AgentId}", parsed.Command, agentId);
                var error = envelope with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Internal,
                    Error = $"Command execution failed: {ex.Message}"
                };
                await AuditAsync(error, roomId, scopedServices);
                results.Add(error);
            }
        }

        return new CommandPipelineResult(results, parseResult.RemainingText);
    }

    /// <summary>
    /// Format command results for injection into an agent's next context turn.
    /// </summary>
    public static string FormatResultsForContext(IReadOnlyList<CommandEnvelope> results)
    {
        if (results.Count == 0)
            return string.Empty;

        var lines = new List<string> { "=== COMMAND RESULTS ===" };
        foreach (var r in results)
        {
            lines.Add($"[{r.Status}] {r.Command} ({r.CorrelationId})");
            if (r.RetryCount > 0)
                lines.Add($"  Retries: {r.RetryCount}");
            if (r.ErrorCode != null)
            {
                var retryHint = CommandErrorCode.IsRetryable(r.ErrorCode) ? " (retryable)" : " (not retryable)";
                lines.Add($"  ErrorCode: {r.ErrorCode}{retryHint}");
            }
            if (r.Error != null)
                lines.Add($"  Error: {r.Error}");
            if (r.Result != null)
            {
                var json = JsonSerializer.Serialize(r.Result, new JsonSerializerOptions { WriteIndented = true });
                // Truncate very large results
                if (json.Length > 2000)
                    json = json[..2000] + "\n  ... (truncated)";
                lines.Add($"  {json}");
            }
        }
        lines.Add("=== END COMMAND RESULTS ===");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Execute a command, retrying on transient failures if the handler opts in via <see cref="ICommandHandler.IsRetrySafe"/>.
    /// Non-retry-safe handlers are called exactly once.
    /// </summary>
    private async Task<CommandEnvelope> ExecuteWithRetryAsync(
        ICommandHandler handler,
        CommandEnvelope envelope,
        CommandContext context,
        string commandName,
        string agentId)
    {
        var maxAttempts = handler.IsRetrySafe ? MaxRetries + 1 : 1;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            CommandEnvelope result;
            try
            {
                result = await handler.ExecuteAsync(envelope, context);
            }
            catch (Exception ex)
            {
                // Last attempt — propagate as Internal error
                if (attempt >= maxAttempts - 1)
                {
                    return envelope with
                    {
                        Status = CommandStatus.Error,
                        ErrorCode = CommandErrorCode.Internal,
                        Error = $"Command execution failed after {attempt + 1} attempt(s): {ex.Message}",
                        RetryCount = attempt
                    };
                }

                // Transient exception — retry
                var delay = RetryBaseDelayMs * (int)Math.Pow(2, attempt);
                _logger.LogWarning(ex,
                    "Command {Command} threw on attempt {Attempt}/{MaxAttempts} for agent {AgentId}, retrying in {Delay}ms",
                    commandName, attempt + 1, maxAttempts, agentId, delay);
                await Task.Delay(delay);
                continue;
            }

            // Success — return with retry count
            if (result.Status == CommandStatus.Success)
                return result with { RetryCount = attempt };

            // Non-retryable error — return immediately
            if (!PipelineRetryableCodes.Contains(result.ErrorCode ?? ""))
                return result with { RetryCount = attempt };

            // Retryable error on last attempt — return the failure
            if (attempt >= maxAttempts - 1)
                return result with { RetryCount = attempt };

            // Retryable error — retry
            var retryDelay = RetryBaseDelayMs * (int)Math.Pow(2, attempt);
            _logger.LogWarning(
                "Command {Command} returned {ErrorCode} on attempt {Attempt}/{MaxAttempts} for agent {AgentId}, retrying in {Delay}ms",
                commandName, result.ErrorCode, attempt + 1, maxAttempts, agentId, retryDelay);
            await Task.Delay(retryDelay);
        }

        // Should not reach here, but safety net
        return envelope with
        {
            Status = CommandStatus.Error,
            ErrorCode = CommandErrorCode.Internal,
            Error = "Retry loop exited unexpectedly"
        };
    }

    private static async Task AuditAsync(CommandEnvelope envelope, string? roomId, IServiceProvider services)
    {
        var db = services.GetRequiredService<AgentAcademyDbContext>();

        db.CommandAudits.Add(new CommandAuditEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = envelope.CorrelationId,
            AgentId = envelope.ExecutedBy,
            Command = envelope.Command,
            ArgsJson = JsonSerializer.Serialize(envelope.Args),
            Status = envelope.Status.ToString(),
            ResultJson = envelope.Result != null ? JsonSerializer.Serialize(envelope.Result) : null,
            ErrorMessage = envelope.Error,
            ErrorCode = envelope.ErrorCode,
            RoomId = roomId,
            Timestamp = envelope.Timestamp
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Check whether the command args include an explicit confirmation flag.
    /// Agents must include <c>confirm=true</c> to execute destructive commands.
    /// </summary>
    internal static bool HasConfirmFlag(IReadOnlyDictionary<string, object?> args)
    {
        if (!args.TryGetValue("confirm", out var value))
            return false;

        return value switch
        {
            bool b => b,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}

/// <summary>
/// Result of processing an agent response through the command pipeline.
/// </summary>
public record CommandPipelineResult(
    List<CommandEnvelope> Results,
    string RemainingText,
    bool ProcessingFailed = false
);
