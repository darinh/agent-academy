namespace AgentAcademy.Server.Services;

/// <summary>
/// Thrown when a write is attempted against a room whose lifecycle is in a
/// terminal state (Completed or Archived). Controllers map this to HTTP 409
/// Conflict with code <c>room_read_only</c>. Distinct from "room not found"
/// (404) and from generic invalid-state errors.
/// </summary>
public sealed class RoomReadOnlyException : InvalidOperationException
{
    public string RoomId { get; }
    public string Status { get; }

    public RoomReadOnlyException(string roomId, string status)
        : base($"Room '{roomId}' is {status} and is read-only.")
    {
        RoomId = roomId;
        Status = status;
    }
}
