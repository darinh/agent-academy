using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Agent locations, knowledge, configuration, and execution endpoints.
/// </summary>
[ApiController]
[Route("api/agents")]
public class AgentController : ControllerBase
{
    private readonly WorkspaceRuntime _runtime;
    private readonly IAgentExecutor _executor;
    private readonly AgentCatalogOptions _catalog;
    private readonly AgentConfigService _configService;
    private readonly ILogger<AgentController> _logger;

    public AgentController(
        WorkspaceRuntime runtime,
        IAgentExecutor executor,
        AgentCatalogOptions catalog,
        AgentConfigService configService,
        ILogger<AgentController> logger)
    {
        _runtime = runtime;
        _executor = executor;
        _catalog = catalog;
        _configService = configService;
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
            var locations = await _runtime.GetAgentLocationsAsync();
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
            var location = await _runtime.MoveAgentAsync(
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
            var response = await _executor.RunAsync(agent, body, roomId: null, ct);
            return Ok(new { agentId = agent.Id, response });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run prompt for agent '{AgentId}'", agentId);
            return Problem("Failed to execute agent prompt.");
        }
    }

    // ── Agent Config Endpoints ────────────────────────────────

    /// <summary>
    /// GET /api/agents/{agentId}/config — effective config + raw override details.
    /// </summary>
    [HttpGet("{agentId}/config")]
    public async Task<ActionResult<AgentConfigResponse>> GetAgentConfig(string agentId)
    {
        var catalogAgent = _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (catalogAgent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found in catalog" });

        try
        {
            var effective = await _configService.GetEffectiveAgentAsync(catalogAgent);
            var dbOverride = await _configService.GetConfigOverrideAsync(catalogAgent.Id);

            AgentConfigOverrideDto? overrideDto = null;
            if (dbOverride is not null)
            {
                overrideDto = new AgentConfigOverrideDto(
                    dbOverride.StartupPromptOverride,
                    dbOverride.ModelOverride,
                    dbOverride.CustomInstructions,
                    dbOverride.InstructionTemplateId,
                    dbOverride.InstructionTemplate?.Name,
                    dbOverride.UpdatedAt);
            }

            return Ok(new AgentConfigResponse(
                catalogAgent.Id,
                effective.Model ?? catalogAgent.Model ?? "",
                effective.StartupPrompt,
                dbOverride is not null,
                overrideDto));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get config for agent '{AgentId}'", agentId);
            return Problem("Failed to retrieve agent configuration.");
        }
    }

    /// <summary>
    /// PUT /api/agents/{agentId}/config — create or update an agent config override.
    /// </summary>
    [HttpPut("{agentId}/config")]
    public async Task<ActionResult<AgentConfigResponse>> UpsertAgentConfig(
        string agentId, [FromBody] UpsertAgentConfigRequest request)
    {
        var catalogAgent = _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (catalogAgent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found in catalog" });

        try
        {
            var dbOverride = await _configService.UpsertConfigAsync(
                catalogAgent.Id,
                request.StartupPromptOverride,
                request.ModelOverride,
                request.CustomInstructions,
                request.InstructionTemplateId);

            var effective = await _configService.GetEffectiveAgentAsync(catalogAgent);

            var overrideDto = new AgentConfigOverrideDto(
                dbOverride.StartupPromptOverride,
                dbOverride.ModelOverride,
                dbOverride.CustomInstructions,
                dbOverride.InstructionTemplateId,
                dbOverride.InstructionTemplate?.Name,
                dbOverride.UpdatedAt);

            return Ok(new AgentConfigResponse(
                catalogAgent.Id,
                effective.Model ?? catalogAgent.Model ?? "",
                effective.StartupPrompt,
                true,
                overrideDto));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_config", message = ex.Message });
        }
        catch (DbUpdateException)
        {
            return Conflict(new { code = "config_conflict", message = "Concurrent config update conflict. Please retry." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert config for agent '{AgentId}'", agentId);
            return Problem("Failed to update agent configuration.");
        }
    }

    /// <summary>
    /// POST /api/agents/{agentId}/config/reset — delete override, revert to catalog defaults.
    /// </summary>
    [HttpPost("{agentId}/config/reset")]
    public async Task<ActionResult<AgentConfigResponse>> ResetAgentConfig(string agentId)
    {
        var catalogAgent = _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (catalogAgent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found in catalog" });

        try
        {
            await _configService.DeleteConfigAsync(catalogAgent.Id);

            return Ok(new AgentConfigResponse(
                catalogAgent.Id,
                catalogAgent.Model ?? "",
                catalogAgent.StartupPrompt,
                false,
                null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset config for agent '{AgentId}'", agentId);
            return Problem("Failed to reset agent configuration.");
        }
    }

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
            var sessions = await _runtime.GetAgentSessionsAsync(catalogAgent.Id);
            return Ok(sessions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sessions for agent '{AgentId}'", agentId);
            return Problem("Failed to retrieve agent sessions.");
        }
    }
}

/// <summary>
/// Request body for updating agent location.
/// </summary>
public record UpdateLocationRequest(
    string RoomId,
    AgentState State,
    string? BreakoutRoomId = null
);

/// <summary>
/// Request body for appending agent knowledge.
/// </summary>
public record AppendKnowledgeRequest(string Entry);

// ── Agent Config DTOs ──────────────────────────────────────

/// <summary>
/// Response containing effective agent config and raw override details.
/// </summary>
public record AgentConfigResponse(
    string AgentId,
    string EffectiveModel,
    string EffectiveStartupPrompt,
    bool HasOverride,
    AgentConfigOverrideDto? Override
);

/// <summary>
/// Raw override values stored in the database.
/// </summary>
public record AgentConfigOverrideDto(
    string? StartupPromptOverride,
    string? ModelOverride,
    string? CustomInstructions,
    string? InstructionTemplateId,
    string? InstructionTemplateName,
    DateTime UpdatedAt
);

/// <summary>
/// Request body for creating or updating an agent config override.
/// All fields nullable — null clears that override field.
/// </summary>
public record UpsertAgentConfigRequest(
    string? StartupPromptOverride,
    string? ModelOverride,
    string? CustomInstructions,
    string? InstructionTemplateId
);
