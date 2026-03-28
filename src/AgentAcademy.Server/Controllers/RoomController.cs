using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Room CRUD endpoints.
/// </summary>
[ApiController]
[Route("api/rooms")]
public class RoomController : ControllerBase
{
    private readonly WorkspaceRuntime _runtime;
    private readonly ILogger<RoomController> _logger;

    public RoomController(WorkspaceRuntime runtime, ILogger<RoomController> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/rooms — list all rooms.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RoomSnapshot>>> GetRooms()
    {
        try
        {
            var rooms = await _runtime.GetRoomsAsync();
            return Ok(rooms);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list rooms");
            return Problem("Failed to retrieve rooms.");
        }
    }

    /// <summary>
    /// GET /api/rooms/{roomId} — room details.
    /// </summary>
    [HttpGet("{roomId}")]
    public async Task<ActionResult<RoomSnapshot>> GetRoom(string roomId)
    {
        try
        {
            var room = await _runtime.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(new { error = $"Room '{roomId}' not found" });

            return Ok(room);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get room '{RoomId}'", roomId);
            return Problem("Failed to retrieve room.");
        }
    }
}
