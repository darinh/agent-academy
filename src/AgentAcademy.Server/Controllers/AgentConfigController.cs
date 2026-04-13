using System.ComponentModel.DataAnnotations;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Agent configuration and custom agent management endpoints.
/// Extracted from AgentController to separate configuration from location/execution.
/// </summary>
[ApiController]
[Route("api/agents")]
public class AgentConfigController : ControllerBase
{
    private readonly AgentCatalogOptions _catalog;
    private readonly AgentConfigService _configService;
    private readonly ILogger<AgentConfigController> _logger;

    public AgentConfigController(
        AgentCatalogOptions catalog,
        AgentConfigService configService,
        ILogger<AgentConfigController> logger)
    {
        _catalog = catalog;
        _configService = configService;
        _logger = logger;
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

        var agentId = ToKebabCase(request.Name.Trim());
        if (string.IsNullOrEmpty(agentId))
            return BadRequest(new { code = "invalid_name", message = "Name must contain alphanumeric characters" });

        if (_catalog.Agents.Any(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { code = "agent_exists", message = $"A built-in agent with ID '{agentId}' already exists. Choose a different name." });

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
}

// ── Agent Config DTOs ──────────────────────────────────────

/// <summary>
/// Request body for creating a custom agent.
/// </summary>
public record CreateCustomAgentRequest(
    [property: Required, StringLength(100)] string Name,
    [property: Required, MinLength(1), StringLength(100_000)] string Prompt,
    [property: StringLength(100)] string? Model = null
);

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
