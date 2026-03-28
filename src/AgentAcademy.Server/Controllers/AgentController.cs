using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Agent locations, knowledge, and execution endpoints.
/// </summary>
[ApiController]
[Route("api/agents")]
public class AgentController : ControllerBase
{
    private readonly WorkspaceRuntime _runtime;
    private readonly IAgentExecutor _executor;
    private readonly AgentCatalogOptions _catalog;
    private readonly ILogger<AgentController> _logger;

    public AgentController(
        WorkspaceRuntime runtime,
        IAgentExecutor executor,
        AgentCatalogOptions catalog,
        ILogger<AgentController> logger)
    {
        _runtime = runtime;
        _executor = executor;
        _catalog = catalog;
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
