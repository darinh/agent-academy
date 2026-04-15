using System.ComponentModel.DataAnnotations;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
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
    private readonly IBreakoutRoomService _breakoutRoomService;
    private readonly IAgentExecutor _executor;
    private readonly IAgentCatalog _catalog;
    private readonly AgentQuotaService _quotaService;
    private readonly ILogger<AgentController> _logger;

    public AgentController(
        AgentLocationService agentLocationService,
        IBreakoutRoomService breakoutRoomService,
        IAgentExecutor executor,
        IAgentCatalog catalog,
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
            return BadRequest(ApiProblem.BadRequest(ex.Message, "move_failed"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiProblem.BadRequest(ex.Message, "move_failed"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update location for agent '{AgentId}'", agentId);
            return Problem("Failed to update agent location.");
        }
    }

    /// <summary>
    /// GET /api/agents/{agentId}/knowledge — agent-specific knowledge entries.
    /// Backed by the agent memory system (spec 008). Returns non-expired memories
    /// for the given agent as a flat list of formatted strings.
    /// </summary>
    [HttpGet("{agentId}/knowledge")]
    public async Task<IActionResult> GetAgentKnowledge(string agentId, CancellationToken ct)
    {
        var agent = FindAgent(agentId);
        if (agent is null)
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));

        try
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AgentAcademyDbContext>();
            var now = DateTime.UtcNow;

            var memories = await db.AgentMemories
                .Where(m => m.AgentId == agent.Id)
                .Where(m => m.ExpiresAt == null || m.ExpiresAt > now)
                .OrderBy(m => m.Category)
                .ThenBy(m => m.Key)
                .ToListAsync(ct);

            var entries = memories.Select(m => $"[{m.Category}] {m.Key}: {m.Value}").ToArray();
            return Ok(new { entries });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get knowledge for agent '{AgentId}'", agentId);
            return Problem("Failed to retrieve agent knowledge.");
        }
    }

    /// <summary>
    /// POST /api/agents/{agentId}/knowledge — append a knowledge entry.
    /// Creates a memory in the "knowledge" category with an auto-generated key.
    /// Backed by the agent memory system (spec 008).
    /// </summary>
    [HttpPost("{agentId}/knowledge")]
    public async Task<IActionResult> AppendAgentKnowledge(
        string agentId,
        [FromBody] AppendKnowledgeRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Entry))
            return BadRequest(ApiProblem.BadRequest("entry string required", "missing_entry"));

        var agent = FindAgent(agentId);
        if (agent is null)
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));

        try
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AgentAcademyDbContext>();
            var now = DateTime.UtcNow;

            // Generate a key from the first ~60 chars of the entry, kebab-cased
            var key = GenerateKnowledgeKey(request.Entry);

            // Upsert scoped to "knowledge" category to avoid clobbering other categories
            var existing = await db.AgentMemories
                .FirstOrDefaultAsync(m => m.AgentId == agent.Id && m.Key == key && m.Category == "knowledge", ct);

            if (existing is not null)
            {
                existing.Value = request.Entry;
                existing.UpdatedAt = now;
            }
            else
            {
                db.AgentMemories.Add(new Data.Entities.AgentMemoryEntity
                {
                    AgentId = agent.Id,
                    Category = "knowledge",
                    Key = key,
                    Value = request.Entry,
                    CreatedAt = now,
                });
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (existing is null)
            {
                // Concurrent insert race — retry as update
                db.ChangeTracker.Clear();
                var conflict = await db.AgentMemories.FindAsync(new object[] { agent.Id, key }, ct);
                if (conflict is not null)
                {
                    conflict.Value = request.Entry;
                    conflict.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                }
            }

            _logger.LogInformation(
                "Knowledge appended for agent '{AgentId}': key={Key}", agent.Id, key);

            return Ok(new { agentId = agent.Id, key, category = "knowledge", entry = request.Entry });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append knowledge for agent '{AgentId}'", agentId);
            return Problem("Failed to append agent knowledge.");
        }
    }

    /// <summary>
    /// GET /api/knowledge — shared knowledge across all agents.
    /// Returns non-expired memories grouped by agent ID.
    /// </summary>
    [HttpGet("/api/knowledge")]
    public async Task<IActionResult> GetSharedKnowledge(CancellationToken ct)
    {
        try
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AgentAcademyDbContext>();
            var now = DateTime.UtcNow;

            var memories = await db.AgentMemories
                .Where(m => m.ExpiresAt == null || m.ExpiresAt > now)
                .OrderBy(m => m.AgentId)
                .ThenBy(m => m.Category)
                .ThenBy(m => m.Key)
                .ToListAsync(ct);

            var grouped = memories
                .GroupBy(m => m.AgentId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(m => $"[{m.Category}] {m.Key}: {m.Value}").ToArray());

            return Ok(grouped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get shared knowledge");
            return Problem("Failed to retrieve shared knowledge.");
        }
    }

    private static string GenerateKnowledgeKey(string entry)
    {
        // Take first ~60 chars, lowercase, replace non-alphanumeric with hyphens, trim
        var slug = entry.Length > 60 ? entry[..60] : entry;
        var chars = slug.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var raw = new string(chars).Trim('-');
        // Collapse consecutive hyphens
        while (raw.Contains("--"))
            raw = raw.Replace("--", "-");
        if (string.IsNullOrEmpty(raw))
        {
            // Deterministic fallback: stable hash of the full entry text
            var hashBytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(entry));
            raw = $"knowledge-{Convert.ToHexString(hashBytes)[..16].ToLowerInvariant()}";
        }
        return raw;
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
            return BadRequest(ApiProblem.BadRequest("Prompt body is required.", "empty_prompt"));

        var agent = _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));

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
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));

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
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));

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
            return BadRequest(ApiProblem.BadRequest("MaxRequestsPerHour must be >= 0", "invalid_quota"));
        if (request.MaxTokensPerHour is < 0)
            return BadRequest(ApiProblem.BadRequest("MaxTokensPerHour must be >= 0", "invalid_quota"));
        if (request.MaxCostPerHour is < 0)
            return BadRequest(ApiProblem.BadRequest("MaxCostPerHour must be >= 0", "invalid_quota"));

        var agent = FindAgent(agentId);
        if (agent is null)
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));

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
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));

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
    [Required, StringLength(100)] string RoomId,
    [EnumDataType(typeof(AgentState))] AgentState State,
    [StringLength(100)] string? BreakoutRoomId = null
);

/// <summary>
/// Request body for appending agent knowledge.
/// </summary>
public record AppendKnowledgeRequest([Required, MinLength(1), StringLength(10_000)] string Entry);

/// <summary>
/// Request body for updating an agent's resource quotas.
/// Null values mean unlimited.
/// </summary>
public record UpdateQuotaRequest(
    [Range(1, 100_000)] int? MaxRequestsPerHour,
    [Range(1, 100_000_000)] long? MaxTokensPerHour,
    [Range(0.01, 10_000)] decimal? MaxCostPerHour
);
