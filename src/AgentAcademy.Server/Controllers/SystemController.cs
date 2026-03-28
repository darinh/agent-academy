using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Root, health, overview, agent catalog, and model endpoints.
/// </summary>
[ApiController]
public class SystemController : ControllerBase
{
    private readonly WorkspaceRuntime _runtime;
    private readonly IAgentExecutor _executor;
    private readonly AgentCatalogOptions _catalog;
    private readonly ILogger<SystemController> _logger;

    private static readonly DateTime StartedAt = DateTime.UtcNow;

    public SystemController(
        WorkspaceRuntime runtime,
        IAgentExecutor executor,
        AgentCatalogOptions catalog,
        ILogger<SystemController> logger)
    {
        _runtime = runtime;
        _executor = executor;
        _catalog = catalog;
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
    /// GET /healthz — health check.
    /// </summary>
    [HttpGet("/healthz")]
    [AllowAnonymous]
    public IActionResult GetHealth()
    {
        var uptime = DateTime.UtcNow - StartedAt;

        return Ok(new HealthResult(
            Status: "healthy",
            Uptime: uptime.ToString(@"d\.hh\:mm\:ss"),
            Timestamp: DateTime.UtcNow
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
            var overview = await _runtime.GetOverviewAsync();
            return Ok(overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get workspace overview");
            return Problem("Failed to retrieve workspace overview.");
        }
    }

    /// <summary>
    /// GET /api/agents/configured — agent catalog list.
    /// </summary>
    [HttpGet("api/agents/configured")]
    public ActionResult<IReadOnlyList<AgentDefinition>> GetConfiguredAgents()
    {
        return Ok(_catalog.Agents);
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
}
