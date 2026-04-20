using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Root, health, overview, agent catalog, and model endpoints.
/// </summary>
[ApiController]
public class SystemController : ControllerBase
{
    private readonly IRoomService _roomService;
    private readonly IAgentLocationService _agentLocationService;
    private readonly IBreakoutRoomService _breakoutRoomService;
    private readonly IGoalCardService _goalCardService;
    private readonly IActivityPublisher _activity;
    private readonly IAgentExecutor _executor;
    private readonly IAgentCatalog _catalog;
    private readonly AgentAcademyDbContext _db;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IAgentErrorTracker _errorTracker;
    private readonly ILogger<SystemController> _logger;

    private static readonly DateTime StartedAt = DateTime.UtcNow;

    public SystemController(
        IRoomService roomService,
        IAgentLocationService agentLocationService,
        IBreakoutRoomService breakoutRoomService,
        IGoalCardService goalCardService,
        IActivityPublisher activity,
        IAgentExecutor executor,
        IAgentCatalog catalog,
        AgentAcademyDbContext db,
        ILlmUsageTracker usageTracker,
        IAgentErrorTracker errorTracker,
        ILogger<SystemController> logger)
    {
        _roomService = roomService;
        _agentLocationService = agentLocationService;
        _breakoutRoomService = breakoutRoomService;
        _goalCardService = goalCardService;
        _activity = activity;
        _executor = executor;
        _catalog = catalog;
        _db = db;
        _usageTracker = usageTracker;
        _errorTracker = errorTracker;
        _logger = logger;
    }

    /// <summary>
    /// GET / — basic service info + endpoint list.
    /// </summary>
    [HttpGet("/")]
    public IActionResult GetRoot()
    {
        return Ok(new
        {
            service = "Agent Academy",
            message = "Agent Academy API is running.",
            endpoints = new[]
            {
                "GET  /healthz",
                "GET  /api/overview",
                "GET  /api/agents/configured",
                "GET  /api/models",
                "GET  /api/rooms",
                "GET  /api/rooms/:roomId",
                "GET  /api/rooms/:roomId/artifacts",
                "GET  /api/rooms/:roomId/usage",
                "GET  /api/rooms/:roomId/errors",
                "GET  /api/rooms/:roomId/evaluations",
                "GET  /api/rooms/:roomId/plan",
                "PUT  /api/rooms/:roomId/plan",
                "DELETE /api/rooms/:roomId/plan",
                "POST /api/tasks",
                "POST /api/commands/execute",
                "GET  /api/commands/:correlationId",
                "GET  /api/commands/audit",
                "GET  /api/commands/audit/stats",
                "POST /api/rooms/:roomId/messages",
                "POST /api/rooms/:roomId/human",
                "POST /api/rooms/:roomId/phase",
                "POST /api/rooms/:roomId/compact",
                "GET  /api/workspace",
                "GET  /api/workspaces",
                "PUT  /api/workspace",
                "POST /api/workspaces/scan",
                "POST /api/workspaces/onboard",
                "GET  /api/filesystem/browse",
                "GET  /api/agents/locations",
                "PUT  /api/agents/:agentId/location",
                "GET  /api/agents/:agentId/knowledge",
                "POST /api/agents/:agentId/knowledge",
                "GET  /api/knowledge",
                "POST /api/agents/:agentId/run",
                "GET  /api/activity/recent",
            },
        });
    }

    /// <summary>
    /// GET /healthz — basic health check.
    /// </summary>
    [HttpGet("/healthz")]
    [AllowAnonymous]
    public IActionResult GetHealth()
    {
        var uptime = DateTime.UtcNow - StartedAt;

        return Ok(new HealthResult(
            Status: "healthy",
            Uptime: uptime.ToString(@"d\.hh\:mm\:ss"),
            Timestamp: DateTime.UtcNow,
            Message: "Agent Academy backend is healthy."
        ));
    }

    /// <summary>
    /// GET /api/health/instance — instance-level health for client reconnect protocol.
    /// Clients compare instanceId to detect server restarts.
    /// </summary>
    [HttpGet("api/health/instance")]
    [AllowAnonymous]
    public IActionResult GetInstanceHealth()
    {
        var instanceId = CrashRecoveryService.CurrentInstanceId ?? "unknown";

        return Ok(new InstanceHealthResult(
            InstanceId: instanceId,
            StartedAt: StartedAt,
            Version: typeof(SystemController).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            CrashDetected: CrashRecoveryService.CurrentCrashDetected,
            ExecutorOperational: _executor.IsFullyOperational,
            AuthFailed: _executor.IsAuthFailed,
            CircuitBreakerState: _executor.CircuitBreakerState.ToString()
        ));
    }

    /// <summary>
    /// GET /api/overview — full workspace overview.
    /// </summary>
    [HttpGet("api/overview")]
    public async Task<ActionResult<WorkspaceOverview>> GetOverview()
    {
        try
        {
            var rooms = await _roomService.GetRoomsAsync();
            var agentLocations = await _agentLocationService.GetAgentLocationsAsync();
            var breakoutRooms = await _breakoutRoomService.GetAllBreakoutRoomsAsync();
            var recentActivity = _activity.GetRecentActivity();
            var goalCardSummary = await _goalCardService.GetSummaryAsync();

            var overview = new WorkspaceOverview(
                ConfiguredAgents: [.. _catalog.Agents],
                Rooms: rooms,
                RecentActivity: [.. recentActivity],
                AgentLocations: agentLocations,
                BreakoutRooms: breakoutRooms,
                GoalCards: goalCardSummary,
                GeneratedAt: DateTime.UtcNow
            );
            return Ok(overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get workspace overview");
            return Problem("Failed to retrieve workspace overview.");
        }
    }

    /// <summary>
    /// GET /api/agents/configured — agent catalog + custom agents.
    /// </summary>
    [HttpGet("api/agents/configured")]
    public async Task<ActionResult<List<AgentDefinition>>> GetConfiguredAgents()
    {
        var agents = new List<AgentDefinition>(_catalog.Agents);

        // Add custom agents from DB (agent_configs entries with no catalog match)
        var catalogIds = new HashSet<string>(_catalog.Agents.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var customConfigs = await _db.AgentConfigs
            .Where(c => !catalogIds.Contains(c.AgentId))
            .ToListAsync();

        foreach (var config in customConfigs)
        {
            string displayName = config.AgentId;
            string role = "Custom";
            if (!string.IsNullOrEmpty(config.CustomInstructions))
            {
                try
                {
                    var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(config.CustomInstructions);
                    if (meta.TryGetProperty("displayName", out var dn)) displayName = dn.GetString() ?? config.AgentId;
                    if (meta.TryGetProperty("role", out var r)) role = r.GetString() ?? "Custom";
                }
                catch { /* metadata parse failure — use defaults */ }
            }

            agents.Add(new AgentDefinition(
                Id: config.AgentId,
                Name: displayName,
                Role: role,
                Summary: $"Custom agent: {displayName}",
                StartupPrompt: config.StartupPromptOverride ?? "",
                Model: config.ModelOverride,
                CapabilityTags: new List<string> { "custom" },
                EnabledTools: new List<string> { "chat", "memory" },
                AutoJoinDefaultRoom: false));
        }

        return Ok(agents);
    }

    /// <summary>
    /// GET /api/models — available models and executor status.
    /// </summary>
    [HttpGet("api/models")]
    public IActionResult GetModels()
    {
        var models = _catalog.Agents
            .Where(a => !string.IsNullOrEmpty(a.Model))
            .Select(a => a.Model!)
            .Distinct()
            .Select(m => new ModelInfo(Id: m, Name: m))
            .ToList();

        return Ok(new
        {
            models,
            executorOperational = _executor.IsFullyOperational,
        });
    }

    /// <summary>
    /// GET /api/system/restarts — recent server instance history with lifecycle details.
    /// </summary>
    [HttpGet("api/system/restarts")]
    public async Task<IActionResult> GetRestartHistory(
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        var instances = await _db.ServerInstances
            .OrderByDescending(si => si.StartedAt)
            .Skip(offset)
            .Take(limit)
            .Select(si => new ServerInstanceDto(
                si.Id,
                si.StartedAt,
                si.ShutdownAt,
                si.ExitCode,
                si.CrashDetected,
                si.Version,
                DeriveShutdownReason(si.ShutdownAt, si.ExitCode)))
            .ToListAsync();

        var total = await _db.ServerInstances.CountAsync();

        return Ok(new { instances, total, limit, offset });
    }

    /// <summary>
    /// GET /api/system/restarts/stats — restart statistics for a time window.
    /// </summary>
    [HttpGet("api/system/restarts/stats")]
    public async Task<IActionResult> GetRestartStats([FromQuery] int? hours = 24)
    {
        var window = TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 720));
        var since = DateTime.UtcNow - window;

        // Push aggregation to SQL to avoid materializing all rows.
        // Use StartedAt for total/running counts, ShutdownAt for outcome counts.
        var totalInstances = await _db.ServerInstances
            .CountAsync(si => si.StartedAt > since);
        var crashRestarts = await _db.ServerInstances
            .CountAsync(si => si.StartedAt > since && si.CrashDetected);
        var intentionalRestarts = await _db.ServerInstances
            .CountAsync(si => si.ShutdownAt != null && si.ShutdownAt > since
                && si.ExitCode == RestartServerHandler.RestartExitCode);
        var cleanShutdowns = await _db.ServerInstances
            .CountAsync(si => si.ShutdownAt != null && si.ShutdownAt > since
                && si.ExitCode == 0);
        var stillRunning = await _db.ServerInstances
            .CountAsync(si => si.StartedAt > since && si.ShutdownAt == null);

        var stats = new RestartStatsDto(
            TotalInstances: totalInstances,
            CrashRestarts: crashRestarts,
            IntentionalRestarts: intentionalRestarts,
            CleanShutdowns: cleanShutdowns,
            StillRunning: stillRunning,
            WindowHours: (int)window.TotalHours,
            MaxRestartsPerWindow: RestartServerHandler.MaxRestartsPerWindow,
            RestartWindowHours: RestartServerHandler.RestartWindowHours);

        return Ok(stats);
    }

    private static string DeriveShutdownReason(DateTime? shutdownAt, int? exitCode) =>
        shutdownAt is null ? "Running"
        : exitCode == RestartServerHandler.RestartExitCode ? "IntentionalRestart"
        : exitCode == 0 ? "CleanShutdown"
        : exitCode == -1 ? "Crash"
        : $"UnexpectedExit({exitCode})";

    /// <summary>
    /// GET /api/usage — global LLM usage summary, optionally filtered by time window.
    /// </summary>
    [HttpGet("api/usage")]
    public async Task<ActionResult<UsageSummary>> GetGlobalUsage([FromQuery] int? hoursBack = null)
    {
        try
        {
            if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
                return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760", "invalid_hours_back"));

            var since = hoursBack.HasValue
                ? DateTime.UtcNow.AddHours(-hoursBack.Value)
                : (DateTime?)null;
            var usage = await _usageTracker.GetGlobalUsageAsync(since);
            return Ok(usage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get global usage");
            return Problem("Failed to retrieve usage data.");
        }
    }

    /// <summary>
    /// GET /api/usage/records — recent individual LLM call records across all rooms.
    /// </summary>
    [HttpGet("api/usage/records")]
    public async Task<ActionResult<List<LlmUsageRecord>>> GetGlobalUsageRecords(
        [FromQuery] string? agentId = null,
        [FromQuery] int limit = 50)
    {
        try
        {
            var records = await _usageTracker.GetRecentUsageAsync(
                roomId: null,
                agentId: agentId,
                limit: Math.Clamp(limit, 1, 200));
            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get global usage records");
            return Problem("Failed to retrieve usage records.");
        }
    }

    /// <summary>
    /// GET /api/errors — global error summary, optionally filtered by time window.
    /// </summary>
    [HttpGet("api/errors")]
    public async Task<ActionResult<ErrorSummary>> GetGlobalErrorSummary([FromQuery] int? hoursBack = null)
    {
        try
        {
            if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
                return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760", "invalid_hours_back"));

            var since = hoursBack.HasValue
                ? DateTime.UtcNow.AddHours(-hoursBack.Value)
                : (DateTime?)null;
            var summary = await _errorTracker.GetErrorSummaryAsync(since);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get global error summary");
            return Problem("Failed to retrieve error data.");
        }
    }

    /// <summary>
    /// GET /api/errors/records — recent individual error records across all rooms.
    /// </summary>
    [HttpGet("api/errors/records")]
    public async Task<ActionResult<List<ErrorRecord>>> GetGlobalErrorRecords(
        [FromQuery] string? agentId = null,
        [FromQuery] int? hoursBack = null,
        [FromQuery] int limit = 50)
    {
        try
        {
            if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
                return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760", "invalid_hours_back"));

            var since = hoursBack.HasValue
                ? DateTime.UtcNow.AddHours(-hoursBack.Value)
                : (DateTime?)null;
            var records = await _errorTracker.GetRecentErrorsAsync(
                agentId: agentId,
                since: since,
                limit: Math.Clamp(limit, 1, 200));
            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get global error records");
            return Problem("Failed to retrieve error records.");
        }
    }

    /// <summary>
    /// Triggers a manual reload of the agent catalog from agents.json.
    /// </summary>
    [HttpPost("/api/system/reload-catalog")]
    public async Task<IActionResult> ReloadCatalog(
        [FromServices] AgentCatalogWatcher watcher,
        CancellationToken ct)
    {
        var result = await watcher.TriggerReloadAsync(ct);

        if (result.Error is not null)
            return Problem(result.Error, statusCode: 500);

        if (result.WasSkipped)
            return Ok(new { reloaded = false, reason = "Reload already in progress" });

        if (result.Diff is null)
            return Ok(new { reloaded = false, reason = "No changes detected" });

        return Ok(new
        {
            reloaded = true,
            added = result.Diff.Added.Select(a => new { a.Id, a.Name, a.Role }),
            removed = result.Diff.Removed.Select(a => new { a.Id, a.Name, a.Role }),
            modified = result.Diff.Modified.Select(a => new { a.Id, a.Name, a.Role })
        });
    }
}

public record ServerInstanceDto(
    string Id,
    DateTime StartedAt,
    DateTime? ShutdownAt,
    int? ExitCode,
    bool CrashDetected,
    string Version,
    string ShutdownReason);

public record RestartStatsDto(
    int TotalInstances,
    int CrashRestarts,
    int IntentionalRestarts,
    int CleanShutdowns,
    int StillRunning,
    int WindowHours,
    int MaxRestartsPerWindow,
    int RestartWindowHours);
