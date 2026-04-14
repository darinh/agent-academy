using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Full-text search across workspace messages and tasks.
/// </summary>
[ApiController]
[Route("api/search")]
public class SearchController : ControllerBase
{
    private readonly SearchService _search;
    private readonly RoomService _roomService;

    public SearchController(SearchService search, RoomService roomService)
    {
        _search = search;
        _roomService = roomService;
    }

    /// <summary>
    /// Search messages and tasks by keyword.
    /// </summary>
    /// <param name="q">Search query (required, non-empty).</param>
    /// <param name="scope">"messages", "tasks", or "all" (default: "all").</param>
    /// <param name="messageLimit">Max message results (default: 25, max: 100).</param>
    /// <param name="taskLimit">Max task results (default: 25, max: 100).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<ActionResult<SearchResults>> Search(
        [FromQuery] string? q,
        [FromQuery] string scope = "all",
        [FromQuery] int messageLimit = 25,
        [FromQuery] int taskLimit = 25,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(ApiProblem.BadRequest("Search query 'q' is required and must not be empty.", "empty_query"));

        if (scope is not "all" and not "messages" and not "tasks")
            return BadRequest(ApiProblem.BadRequest("Scope must be 'all', 'messages', or 'tasks'.", "invalid_scope"));

        if (messageLimit is < 1 or > 100)
            return BadRequest(ApiProblem.BadRequest("messageLimit must be between 1 and 100.", "invalid_limit"));

        if (taskLimit is < 1 or > 100)
            return BadRequest(ApiProblem.BadRequest("taskLimit must be between 1 and 100.", "invalid_limit"));

        var workspacePath = await _roomService.GetActiveWorkspacePathAsync();

        var results = await _search.SearchAsync(q, scope, messageLimit, taskLimit, workspacePath, ct);
        return Ok(results);
    }
}
