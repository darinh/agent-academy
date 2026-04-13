using System.ComponentModel.DataAnnotations;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Agent locations, knowledge, quota, execution, and session endpoints.
/// Config and custom agent management: see <see cref="AgentConfigController"/>.
/// </summary>
[ApiController]
[Route("api/agents")]
public class AgentController : ControllerBase
{
    private readonly AgentLocationService _agentLocationService;
    private readonly BreakoutRoomService _breakoutRoomService;
    private readonly IAgentExecutor _executor;
    private readonly AgentCatalogOptions _catalog;
    private readonly AgentQuotaService _quotaService;
    private readonly ILogger<AgentController> _logger;

    public AgentController(
        AgentLocationService agentLocationService,
        BreakoutRoomService breakoutRoomService,
        IAgentExecutor executor,
        AgentCatalogOptions catalog,
        AgentQuotaService quotaService,
        ILogger<AgentController> logger)
    {
        _agentLocationService = agentLocationService;
        _breakoutRoomService = breakoutRoomService;
        _executor = executor;
        _catalog = catalog;
        _quotaService = quotaService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/agents/locations — current agent locations across rooms.
    /// </summary>
    [HttpGet("locations")]
    public async Task<ActionResult<List<AgentLocation>>> GetAgentLocations()
    {
        try
        {
            var locations = await _agentLocationService.GetAgentLocationsAsync();
            return Ok(locations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent locations");
            return Problem("Failed to retrieve agent locations.");
        }
    }

    /// <summary>
    /// PUT /api/agents/{agentId}/location — move an agent to a room/state.
    /// </summary>
    [HttpPut("{agentId}/location")]
    public async Task<ActionResult<AgentLocation>> UpdateAgentLocation(
        string agentId,
        [FromBody] UpdateLocationRequest request)
    {
        try
        {
            var location = await _agentLocationService.MoveAgentAsync(
                agentId, request.RoomId, request.State, request.BreakoutRoomId);
            return Ok(location);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "move_failed", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = "move_failed", message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update location for agent '{AgentId}'", agentId);
            return Problem("Failed to update agent location.");
        }
    }

    /// <summary>
    /// GET /api/agents/{agentId}/knowledge — agent-specific knowledge entries.
    /// </summary>
    [HttpGet("{agentId}/knowledge")]
    public IActionResult GetAgentKnowledge(string agentId)
    {
        // Agent knowledge is not yet persisted in the C# runtime.
        // Return an empty list; the service will be extended in a future task.
        return Ok(new { entries = Array.Empty<string>() });
    }

    /// <summary>
    /// POST /api/agents/{agentId}/knowledge — append a knowledge entry.
    /// Returns 501 until knowledge persistence is implemented.
    /// </summary>
    [HttpPost("{agentId}/knowledge")]
    public IActionResult AppendAgentKnowledge(
        string agentId,
        [FromBody] AppendKnowledgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Entry))
            return BadRequest(new { code = "missing_entry", message = "entry string required" });

        // Agent knowledge persistence is not yet implemented in the C# runtime.
        _logger.LogInformation(
            "Knowledge append requested for agent '{AgentId}': {Entry}", agentId, request.Entry);

        return StatusCode(501, new
        {
            code = "not_implemented",
            message = "Agent knowledge persistence is not yet implemented."
        });
    }

    /// <summary>
    /// GET /api/knowledge — shared knowledge across all agents.
    /// </summary>
    [HttpGet("/api/knowledge")]
    public IActionResult GetSharedKnowledge()
    {
        // Shared knowledge is not yet persisted in the C# runtime.
        return Ok(new Dictionary<string, string[]>());
    }

    /// <summary>
    /// POST /api/agents/{agentId}/run — execute a prompt against an agent.
    /// </summary>
    [HttpPost("{agentId}/run")]
    public async Task<IActionResult> RunPrompt(string agentId, CancellationToken ct)
    {
        // Read body as raw text to match v1 behavior (accepts text or JSON)
        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(body))
            return BadRequest(new { code = "empty_prompt", message = "Prompt body is required." });

        var agent = _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found" });

        try
        {
            var response = await _executor.RunAsync(agent, body, roomId: null, workspacePath: null, ct);
            return Ok(new { agentId = agent.Id, response });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run prompt for agent '{AgentId}'", agentId);
            return Problem("Failed to execute agent prompt.");
        }
    }

    // ── Agent Config and Custom Agent endpoints: see AgentConfigController ──

    /// <summary>
    /// GET /api/agents/{agentId}/sessions — breakout room sessions for an agent.
    /// Returns both active and archived sessions, most recent first.
    /// </summary>
    [HttpGet("{agentId}/sessions")]
    public async Task<ActionResult<List<BreakoutRoom>>> GetAgentSessions(string agentId)
    {
        var catalogAgent = _catalog.Agents.FirstOrDefault(a =>
            a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (catalogAgent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found" });

        try
        {
            var sessions = await _breakoutRoomService.GetAgentSessionsAsync(catalogAgent.Id);
            return Ok(sessions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sessions for agent '{AgentId}'", agentId);
            return Problem("Failed to retrieve agent sessions.");
        }
    }

    // ── Agent Quota Endpoints ──────────────────────────────

    /// <summary>
    /// GET /api/agents/{agentId}/quota — current quota status and usage.
    /// </summary>
    [HttpGet("{agentId}/quota")]
    public async Task<ActionResult<QuotaStatus>> GetAgentQuota(string agentId)
    {
        var agent = FindAgent(agentId);
        if (agent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found" });

        try
        {
            var status = await _quotaService.GetStatusAsync(agent.Id);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get quota for agent '{AgentId}'", agentId);
            return Problem("Failed to retrieve agent quota.");
        }
    }

    /// <summary>
    /// PUT /api/agents/{agentId}/quota — update quota limits.
    /// </summary>
    [HttpPut("{agentId}/quota")]
    public async Task<ActionResult<QuotaStatus>> UpdateAgentQuota(
        string agentId, [FromBody] UpdateQuotaRequest request)
    {
        // Validate: limits must be non-negative when provided
        if (request.MaxRequestsPerHour is < 0)
            return BadRequest(new { code = "invalid_quota", message = "MaxRequestsPerHour must be >= 0" });
        if (request.MaxTokensPerHour is < 0)
            return BadRequest(new { code = "invalid_quota", message = "MaxTokensPerHour must be >= 0" });
        if (request.MaxCostPerHour is < 0)
            return BadRequest(new { code = "invalid_quota", message = "MaxCostPerHour must be >= 0" });

        var agent = FindAgent(agentId);
        if (agent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found" });

        try
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AgentAcademyDbContext>();

            var config = await db.AgentConfigs.FindAsync(agent.Id);
            if (config is null)
            {
                config = new Data.Entities.AgentConfigEntity
                {
                    AgentId = agent.Id,
                    UpdatedAt = DateTime.UtcNow
                };
                db.AgentConfigs.Add(config);
            }

            config.MaxRequestsPerHour = request.MaxRequestsPerHour;
            config.MaxTokensPerHour = request.MaxTokensPerHour;
            config.MaxCostPerHour = request.MaxCostPerHour;
            config.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _quotaService.InvalidateCache(agent.Id);

            var status = await _quotaService.GetStatusAsync(agent.Id);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update quota for agent '{AgentId}'", agentId);
            return Problem("Failed to update agent quota.");
        }
    }

    /// <summary>
    /// DELETE /api/agents/{agentId}/quota — remove quota limits (unlimited).
    /// </summary>
    [HttpDelete("{agentId}/quota")]
    public async Task<ActionResult> RemoveAgentQuota(string agentId)
    {
        var agent = FindAgent(agentId);
        if (agent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found" });

        try
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AgentAcademyDbContext>();

            var config = await db.AgentConfigs.FindAsync(agent.Id);
            if (config is not null)
            {
                config.MaxRequestsPerHour = null;
                config.MaxTokensPerHour = null;
                config.MaxCostPerHour = null;
                config.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            _quotaService.InvalidateCache(agent.Id);
            return Ok(new { status = "removed", agentId = agent.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove quota for agent '{AgentId}'", agentId);
            return Problem("Failed to remove agent quota.");
        }
    }

    private AgentDefinition? FindAgent(string agentId) =>
        _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));
}

public record UpdateLocationRequest(
    [property: Required, StringLength(100)] string RoomId,
    [property: EnumDataType(typeof(AgentState))] AgentState State,
    [property: StringLength(100)] string? BreakoutRoomId = null
);

/// <summary>
/// Request body for appending agent knowledge.
/// </summary>
public record AppendKnowledgeRequest([property: Required, MinLength(1), StringLength(10_000)] string Entry);

/// <summary>
/// Request body for updating an agent's resource quotas.
/// Null values mean unlimited.
/// </summary>
public record UpdateQuotaRequest(
    [property: Range(1, 100_000)] int? MaxRequestsPerHour,
    [property: Range(1, 100_000_000)] long? MaxTokensPerHour,
    [property: Range(0.01, 10_000)] decimal? MaxCostPerHour
);
