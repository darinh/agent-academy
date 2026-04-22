using System.Text.Json;
using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Human-triggered command execution endpoints for the frontend Commands tab.
/// Reuses the existing command handlers with a controller-level allowlist.
/// </summary>
[ApiController]
[Route("api/commands")]
public sealed class CommandController : ControllerBase
{
    private const string HumanAgentId = "human";
    private const string HumanAgentName = "Human";
    private const string HumanAgentRole = "Human";
    private const string PendingAuditStatus = "Pending";

    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIST_ROOMS",
        "LIST_AGENTS",
        "LIST_TASKS",
        "LIST_COMMANDS",
        "SHOW_REVIEW_QUEUE",
        "ROOM_HISTORY",
        "ROOM_TOPIC",
        "CREATE_ROOM",
        "REOPEN_ROOM",
        "CLOSE_ROOM",
        "CLEANUP_ROOMS",
        "INVITE_TO_ROOM",
        "EXPORT_MEMORIES",
        "CREATE_PR",
        "POST_PR_REVIEW",
        "GET_PR_REVIEWS",
        "MERGE_PR",
        "LINK_TASK_TO_SPEC",
        "SHOW_UNLINKED_CHANGES",
        "UPDATE_TASK",
        "CANCEL_TASK",
        "APPROVE_TASK",
        "RECORD_EVIDENCE",
        "QUERY_EVIDENCE",
        "CHECK_GATES",
        "ADD_TASK_DEPENDENCY",
        "REMOVE_TASK_DEPENDENCY",
        "SCHEDULE_SPRINT",
        "LIST_WORKTREES",
        "LIST_AGENT_STATS",
        "CLEANUP_WORKTREES",
        "GENERATE_DIGEST",
        // Tier 2E — Backend Execution
        "RUN_FRONTEND_BUILD",
        "RUN_TYPECHECK",
        "CALL_ENDPOINT",
        "TAIL_LOGS",
        "SHOW_CONFIG",
        // Tier 2F — Data & Operations
        "QUERY_DB",
        "RUN_MIGRATIONS",
        "SHOW_MIGRATION_STATUS",
        "HEALTHCHECK",
        "SHOW_ACTIVE_CONNECTIONS",
        // Tier 2G — Audit & Debug
        "SHOW_AUDIT_EVENTS",
        "SHOW_LAST_ERROR",
        "TRACE_REQUEST",
        "LIST_SYSTEM_SETTINGS",
        "RETRY_FAILED_JOB",
        // Forge Pipeline
        "RUN_FORGE",
        "FORGE_STATUS",
        "LIST_FORGE_RUNS",
    };

    private static readonly HashSet<string> AsyncCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE_PR",
        "MERGE_PR",
        "GENERATE_DIGEST",
        "RUN_FRONTEND_BUILD",
        "RUN_TYPECHECK",
        "RUN_MIGRATIONS",
        "RUN_FORGE",
    };

    private readonly Dictionary<string, ICommandHandler> _handlers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommandController> _logger;
    private readonly AppAuthSetup _authSetup;
    private readonly HumanCommandAuditor _auditor;

    public CommandController(
        IEnumerable<ICommandHandler> handlers,
        IServiceScopeFactory scopeFactory,
        ILogger<CommandController> logger,
        AppAuthSetup authSetup,
        HumanCommandAuditor auditor)
    {
        _handlers = handlers.ToDictionary(h => h.CommandName, StringComparer.OrdinalIgnoreCase);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _authSetup = authSetup;
        _auditor = auditor;
    }

    /// <summary>
    /// GET /api/commands/metadata — returns the full command catalog for the human UI.
    /// Drives dynamic form rendering instead of a hardcoded frontend catalog.
    /// </summary>
    [HttpGet("metadata")]
    public IActionResult GetMetadata()
    {
        if (_authSetup.AnyAuthEnabled && User.Identity?.IsAuthenticated != true)
            return Unauthorized(ApiProblem.Unauthorized("Authentication is required.", "not_authenticated"));

        var metadata = HumanCommandRegistry.GetAll()
            .Where(m => AllowedCommands.Contains(m.Command) && _handlers.ContainsKey(m.Command))
            .ToList();

        return Ok(metadata);
    }

    /// <summary>
    /// POST /api/commands/execute — execute an allowlisted command as the authenticated human user.
    /// </summary>
    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] ExecuteCommandRequest? request)
    {
        if (_authSetup.AnyAuthEnabled && User.Identity?.IsAuthenticated != true)
            return Unauthorized(ApiProblem.Unauthorized("Authentication is required.", "not_authenticated"));

        if (request is null)
            return BadRequest(ApiProblem.BadRequest("Command payload is required.", "invalid_command_request"));

        var commandName = request.Command?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(commandName))
            return BadRequest(ApiProblem.BadRequest("Command is required.", "invalid_command_request"));

        if (!AllowedCommands.Contains(commandName))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiProblem.Forbidden($"Command '{commandName}' is not available in the human command API.", "command_denied"));
        }

        if (!_handlers.TryGetValue(commandName, out var handler))
        {
            _logger.LogError("Allowlisted command {Command} has no registered handler", commandName);
            return Problem($"Command '{commandName}' is not currently available.");
        }

        Dictionary<string, object?> normalizedArgs;
        try
        {
            normalizedArgs = NormalizeArgs(request.Args);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiProblem.BadRequest(ex.Message, "invalid_command_args"));
        }

        var correlationId = $"cmd-{Guid.NewGuid():N}";

        // Destructive confirmation gate (server-side, applies to human API too)
        if (handler.IsDestructive && !CommandPipeline.HasConfirmFlag(normalizedArgs))
        {
            var response = new ExecuteCommandResponse(
                Command: commandName,
                Status: "confirmation_required",
                Result: new Dictionary<string, object?>
                {
                    ["requiresConfirmation"] = true,
                    ["command"] = commandName,
                    ["warning"] = handler.DestructiveWarning,
                    ["retryWith"] = "confirm=true"
                },
                Error: handler.DestructiveWarning + " Re-issue with confirm=true to proceed.",
                ErrorCode: CommandErrorCode.ConfirmationRequired,
                CorrelationId: correlationId,
                Timestamp: DateTime.UtcNow,
                ExecutedBy: HumanAgentId);

            // Audit the confirmation-required attempt for traceability
            await _auditor.CreateConfirmationRequiredAsync(commandName, normalizedArgs, correlationId);

            return Ok(response);
        }

        if (AsyncCommands.Contains(commandName))
        {
            await _auditor.CreatePendingAsync(commandName, normalizedArgs, correlationId);

            _ = Task.Run(() => ExecuteAsyncCommandAsync(commandName, normalizedArgs, correlationId, handler));

            return Accepted(new ExecuteCommandResponse(
                Command: commandName,
                Status: "pending",
                Result: null,
                Error: null,
                ErrorCode: null,
                CorrelationId: correlationId,
                Timestamp: DateTime.UtcNow,
                ExecutedBy: HumanAgentId));
        }

        var envelope = await ExecuteHandlerAsync(commandName, normalizedArgs, correlationId, handler);
        await _auditor.CreateCompletedAsync(envelope);
        return Ok(ToResponse(envelope));
    }

    /// <summary>
    /// GET /api/commands/{correlationId} — retrieve status for a human-triggered command.
    /// </summary>
    [HttpGet("{correlationId}")]
    public async Task<IActionResult> GetStatus(string correlationId)
    {
        if (_authSetup.AnyAuthEnabled && User.Identity?.IsAuthenticated != true)
            return Unauthorized(ApiProblem.Unauthorized("Authentication is required.", "not_authenticated"));

        var audit = await _auditor.GetByCorrelationIdAsync(correlationId);

        if (audit is null)
        {
            return NotFound(new
            {
                code = "command_not_found",
                message = $"No human command execution found for correlationId '{correlationId}'."
            });
        }

        return Ok(ToResponse(audit));
    }

    private async Task ExecuteAsyncCommandAsync(
        string commandName,
        Dictionary<string, object?> args,
        string correlationId,
        ICommandHandler handler)
    {
        var envelope = await ExecuteHandlerAsync(commandName, args, correlationId, handler);

        try
        {
            await _auditor.UpdateAsync(envelope);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update async command audit for {Command} ({CorrelationId})",
                commandName, correlationId);
        }
    }

    private async Task<CommandEnvelope> ExecuteHandlerAsync(
        string commandName,
        Dictionary<string, object?> args,
        string correlationId,
        ICommandHandler handler)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = new CommandContext(
            AgentId: HumanAgentId,
            AgentName: HumanAgentName,
            AgentRole: HumanAgentRole,
            RoomId: null,
            BreakoutRoomId: null,
            Services: scope.ServiceProvider);

        var envelope = new CommandEnvelope(
            Command: commandName,
            Args: args,
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: correlationId,
            Timestamp: DateTime.UtcNow,
            ExecutedBy: HumanAgentId);

        try
        {
            return await handler.ExecuteAsync(envelope, context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Human command {Command} failed", commandName);
            return envelope with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Internal,
                Error = $"Command execution failed: {ex.Message}"
            };
        }
    }

    private static ExecuteCommandResponse ToResponse(CommandEnvelope envelope)
        => new(
            Command: envelope.Command,
            Status: MapAuditStatus(envelope.Status.ToString()),
            Result: envelope.Result,
            Error: envelope.Error,
            ErrorCode: envelope.ErrorCode,
            CorrelationId: envelope.CorrelationId,
            Timestamp: envelope.Timestamp,
            ExecutedBy: envelope.ExecutedBy);

    private static ExecuteCommandResponse ToResponse(Data.Entities.CommandAuditEntity audit)
        => new(
            Command: audit.Command,
            Status: MapAuditStatus(audit.Status),
            Result: DeserializeResult(audit.ResultJson),
            Error: audit.ErrorMessage,
            ErrorCode: audit.ErrorCode,
            CorrelationId: audit.CorrelationId,
            Timestamp: audit.Timestamp,
            ExecutedBy: audit.AgentId);

    private static Dictionary<string, object?> NormalizeArgs(Dictionary<string, JsonElement>? args)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (args is null)
            return normalized;

        foreach (var (key, value) in args)
            normalized[key] = NormalizeArgValue(key, value);

        return normalized;
    }

    private static object? NormalizeArgValue(string key, JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => throw new ArgumentException($"Argument '{key}' must be a scalar JSON value.")
        };

    private static object? DeserializeResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        using var document = JsonDocument.Parse(resultJson);
        return document.RootElement.Clone();
    }

    private static string MapAuditStatus(string status)
        => status switch
        {
            PendingAuditStatus => "pending",
            nameof(CommandStatus.Success) => "completed",
            nameof(CommandStatus.Denied) => "denied",
            _ => "failed"
        };

}
