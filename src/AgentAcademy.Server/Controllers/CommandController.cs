using System.Text.Json;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private const string HumanUiSource = "human-ui";
    private const string PendingAuditStatus = "Pending";

    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "READ_FILE",
        "SEARCH_CODE",
        "LIST_ROOMS",
        "LIST_AGENTS",
        "LIST_TASKS",
        "LIST_COMMANDS",
        "SHOW_DIFF",
        "GIT_LOG",
        "SHOW_REVIEW_QUEUE",
        "ROOM_HISTORY",
        "ROOM_TOPIC",
        "RUN_BUILD",
        "RUN_TESTS",
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
    };

    private static readonly HashSet<string> AsyncCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "RUN_BUILD",
        "RUN_TESTS",
        "CREATE_PR",
        "MERGE_PR",
    };

    private readonly Dictionary<string, ICommandHandler> _handlers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommandController> _logger;

    public CommandController(
        IEnumerable<ICommandHandler> handlers,
        IServiceScopeFactory scopeFactory,
        ILogger<CommandController> logger)
    {
        _handlers = handlers.ToDictionary(h => h.CommandName, StringComparer.OrdinalIgnoreCase);
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/commands/metadata — returns the full command catalog for the human UI.
    /// Drives dynamic form rendering instead of a hardcoded frontend catalog.
    /// </summary>
    [HttpGet("metadata")]
    public IActionResult GetMetadata()
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

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
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (request is null)
            return BadRequest(new { code = "invalid_command_request", message = "Command payload is required." });

        var commandName = request.Command?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(commandName))
            return BadRequest(new { code = "invalid_command_request", message = "Command is required." });

        if (!AllowedCommands.Contains(commandName))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "command_denied",
                message = $"Command '{commandName}' is not available in the human command API."
            });
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
            return BadRequest(new { code = "invalid_command_args", message = ex.Message });
        }

        var correlationId = $"cmd-{Guid.NewGuid():N}";
        if (AsyncCommands.Contains(commandName))
        {
            await CreatePendingAuditAsync(commandName, normalizedArgs, correlationId);

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
        await CreateCompletedAuditAsync(envelope);
        return Ok(ToResponse(envelope));
    }

    /// <summary>
    /// GET /api/commands/{correlationId} — retrieve status for a human-triggered command.
    /// </summary>
    [HttpGet("{correlationId}")]
    public async Task<IActionResult> GetStatus(string correlationId)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audit = await db.CommandAudits
            .AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.CorrelationId == correlationId &&
                a.AgentId == HumanAgentId &&
                a.Source == HumanUiSource);

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
            await UpdateAuditAsync(envelope);
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

    private async Task CreatePendingAuditAsync(
        string commandName,
        Dictionary<string, object?> args,
        string correlationId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        db.CommandAudits.Add(new CommandAuditEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            AgentId = HumanAgentId,
            Source = HumanUiSource,
            Command = commandName,
            ArgsJson = JsonSerializer.Serialize(args),
            Status = PendingAuditStatus,
            Timestamp = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private async Task CreateCompletedAuditAsync(CommandEnvelope envelope)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        db.CommandAudits.Add(CreateAuditEntity(envelope));
        await db.SaveChangesAsync();
    }

    private async Task UpdateAuditAsync(CommandEnvelope envelope)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audit = await db.CommandAudits.FirstOrDefaultAsync(a =>
            a.CorrelationId == envelope.CorrelationId &&
            a.AgentId == HumanAgentId &&
            a.Source == HumanUiSource);

        if (audit is null)
        {
            db.CommandAudits.Add(CreateAuditEntity(envelope));
        }
        else
        {
            audit.Status = envelope.Status.ToString();
            audit.ResultJson = envelope.Result is null ? null : JsonSerializer.Serialize(envelope.Result);
            audit.ErrorMessage = envelope.Error;
            audit.ErrorCode = envelope.ErrorCode;
            audit.Timestamp = envelope.Timestamp;
        }

        await db.SaveChangesAsync();
    }

    private static CommandAuditEntity CreateAuditEntity(CommandEnvelope envelope)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = envelope.CorrelationId,
            AgentId = HumanAgentId,
            Source = HumanUiSource,
            Command = envelope.Command,
            ArgsJson = JsonSerializer.Serialize(envelope.Args),
            Status = envelope.Status.ToString(),
            ResultJson = envelope.Result is null ? null : JsonSerializer.Serialize(envelope.Result),
            ErrorMessage = envelope.Error,
            ErrorCode = envelope.ErrorCode,
            Timestamp = envelope.Timestamp
        };

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

    private static ExecuteCommandResponse ToResponse(CommandAuditEntity audit)
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

    /// <summary>
    /// GET /api/commands/audit — paginated command audit log with optional filters.
    /// </summary>
    [HttpGet("audit")]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] string? agentId = null,
        [FromQuery] string? command = null,
        [FromQuery] string? status = null,
        [FromQuery] int? hoursBack = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(new { code = "invalid_hours_back", message = "hoursBack must be between 1 and 8760." });

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.CommandAudits.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(agentId))
            query = query.Where(a => a.AgentId == agentId);

        if (!string.IsNullOrWhiteSpace(command))
            query = query.Where(a => a.Command == command.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(status))
        {
            // Normalize common casing variants
            var normalized = status switch
            {
                var s when s.Equals("Success", StringComparison.OrdinalIgnoreCase) => "Success",
                var s when s.Equals("Error", StringComparison.OrdinalIgnoreCase) => "Error",
                var s when s.Equals("Denied", StringComparison.OrdinalIgnoreCase) => "Denied",
                var s when s.Equals("Pending", StringComparison.OrdinalIgnoreCase) => "Pending",
                _ => status
            };
            query = query.Where(a => a.Status == normalized);
        }

        if (hoursBack.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-hoursBack.Value);
            query = query.Where(a => a.Timestamp >= since);
        }

        var total = await query.CountAsync();

        var records = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip(offset)
            .Take(limit)
            .Select(a => new AuditLogEntry(
                a.Id,
                a.CorrelationId,
                a.AgentId,
                a.Source,
                a.Command,
                a.Status,
                a.ErrorMessage,
                a.ErrorCode,
                a.RoomId,
                a.Timestamp))
            .ToListAsync();

        return Ok(new AuditLogResponse(records, total, limit, offset));
    }

    /// <summary>
    /// GET /api/commands/audit/stats — aggregate command execution statistics.
    /// </summary>
    [HttpGet("audit/stats")]
    public async Task<IActionResult> GetAuditStats([FromQuery] int? hoursBack = null)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(new { code = "invalid_hours_back", message = "hoursBack must be between 1 and 8760." });

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.CommandAudits.AsNoTracking().AsQueryable();

        if (hoursBack.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-hoursBack.Value);
            query = query.Where(a => a.Timestamp >= since);
        }

        var totalCommands = await query.CountAsync();

        var byStatus = await query
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var byAgent = await query
            .GroupBy(a => a.AgentId)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync();

        var byCommand = await query
            .GroupBy(a => a.Command)
            .Select(g => new { Command = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(20)
            .ToListAsync();

        return Ok(new AuditStatsResponse(
            TotalCommands: totalCommands,
            ByStatus: byStatus.ToDictionary(x => x.Status, x => x.Count),
            ByAgent: byAgent.ToDictionary(x => x.AgentId, x => x.Count),
            ByCommand: byCommand.ToDictionary(x => x.Command, x => x.Count),
            WindowHours: hoursBack));
    }
}

public sealed record ExecuteCommandRequest(
    string Command,
    Dictionary<string, JsonElement>? Args);

public sealed record ExecuteCommandResponse(
    string Command,
    string Status,
    object? Result,
    string? Error,
    string? ErrorCode,
    string CorrelationId,
    DateTime Timestamp,
    string ExecutedBy);

public sealed record AuditLogEntry(
    string Id,
    string CorrelationId,
    string AgentId,
    string? Source,
    string Command,
    string Status,
    string? ErrorMessage,
    string? ErrorCode,
    string? RoomId,
    DateTime Timestamp);

public sealed record AuditLogResponse(
    List<AuditLogEntry> Records,
    int Total,
    int Limit,
    int Offset);

public sealed record AuditStatsResponse(
    int TotalCommands,
    Dictionary<string, int> ByStatus,
    Dictionary<string, int> ByAgent,
    Dictionary<string, int> ByCommand,
    int? WindowHours);
