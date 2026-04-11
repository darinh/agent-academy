using System.ComponentModel.DataAnnotations;
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
    private readonly AgentQuotaService _quotaService;
    private readonly ILogger<AgentController> _logger;

    public AgentController(
        WorkspaceRuntime runtime,
        IAgentExecutor executor,
        AgentCatalogOptions catalog,
        AgentConfigService configService,
        AgentQuotaService quotaService,
        ILogger<AgentController> logger)
    {
        _runtime = runtime;
        _executor = executor;
        _catalog = catalog;
        _configService = configService;
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
            var response = await _executor.RunAsync(agent, body, roomId: null, workspacePath: null, ct);
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

    // ── Custom Agent Endpoints ────────────────────────────────

    /// <summary>
    /// POST /api/agents/custom — create a custom agent from an agent.md prompt.
    /// </summary>
    [HttpPost("custom")]
    public async Task<ActionResult<AgentDefinition>> CreateCustomAgent(
        [FromBody] CreateCustomAgentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { code = "invalid_name", message = "Agent name is required" });

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { code = "invalid_prompt", message = "Agent prompt is required" });

        // Generate kebab-case ID from name
        var agentId = ToKebabCase(request.Name.Trim());
        if (string.IsNullOrEmpty(agentId))
            return BadRequest(new { code = "invalid_name", message = "Name must contain alphanumeric characters" });

        // Check for conflicts with catalog agents
        if (_catalog.Agents.Any(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { code = "agent_exists", message = $"A built-in agent with ID '{agentId}' already exists. Choose a different name." });

        // Check for conflicts with existing custom agents
        var existing = await _configService.GetConfigOverrideAsync(agentId);
        if (existing is not null)
            return Conflict(new { code = "agent_exists", message = $"An agent with ID '{agentId}' already exists. Choose a different name." });

        try
        {
            var metadata = System.Text.Json.JsonSerializer.Serialize(
                new { displayName = request.Name.Trim(), role = "Custom" });

            await _configService.UpsertConfigAsync(agentId,
                startupPromptOverride: request.Prompt,
                modelOverride: request.Model,
                customInstructions: metadata,
                instructionTemplateId: null);

            var agent = new AgentDefinition(
                Id: agentId,
                Name: request.Name.Trim(),
                Role: "Custom",
                Summary: $"Custom agent: {request.Name.Trim()}",
                StartupPrompt: request.Prompt,
                Model: request.Model,
                CapabilityTags: new List<string> { "custom" },
                EnabledTools: new List<string> { "chat", "memory" },
                AutoJoinDefaultRoom: false);

            return CreatedAtAction(nameof(GetAgentConfig), new { agentId }, agent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create custom agent '{AgentId}'", agentId);
            return Problem("Failed to create custom agent.");
        }
    }

    /// <summary>
    /// DELETE /api/agents/custom/{agentId} — delete a custom agent.
    /// </summary>
    [HttpDelete("custom/{agentId}")]
    public async Task<ActionResult> DeleteCustomAgent(string agentId)
    {
        // Don't allow deleting catalog agents
        if (_catalog.Agents.Any(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { code = "cannot_delete_builtin", message = "Cannot delete built-in agents" });

        var existing = await _configService.GetConfigOverrideAsync(agentId);
        if (existing is null)
            return NotFound(new { code = "agent_not_found", message = $"Custom agent '{agentId}' not found" });

        try
        {
            await _configService.DeleteConfigAsync(agentId);
            return Ok(new { status = "deleted", agentId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete custom agent '{AgentId}'", agentId);
            return Problem("Failed to delete custom agent.");
        }
    }

    private static string ToKebabCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (c is ' ' or '_' or '-' && sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private AgentDefinition? FindAgent(string agentId) =>
        _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Request body for creating a custom agent.
/// </summary>
public record CreateCustomAgentRequest(
    [property: Required, StringLength(100)] string Name,
    [property: Required, MinLength(1), StringLength(100_000)] string Prompt,
    [property: StringLength(100)] string? Model = null
);
public record UpdateLocationRequest(
    [property: Required, StringLength(100)] string RoomId,
    [property: EnumDataType(typeof(AgentState))] AgentState State,
    [property: StringLength(100)] string? BreakoutRoomId = null
);

/// <summary>
/// Request body for appending agent knowledge.
/// </summary>
public record AppendKnowledgeRequest([property: Required, MinLength(1), StringLength(10_000)] string Entry);

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
    [property: StringLength(100_000)] string? StartupPromptOverride,
    [property: StringLength(100)] string? ModelOverride,
    [property: StringLength(100_000)] string? CustomInstructions,
    [property: StringLength(100)] string? InstructionTemplateId
);

/// <summary>
/// Request body for updating an agent's resource quotas.
/// Null values mean unlimited.
/// </summary>
public record UpdateQuotaRequest(
    [property: Range(1, 100_000)] int? MaxRequestsPerHour,
    [property: Range(1, 100_000_000)] long? MaxTokensPerHour,
    [property: Range(0.01, 10_000)] decimal? MaxCostPerHour
);
