using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Result of a task rejection, containing the info needed for room/breakout reopen.
/// </summary>
public sealed record RejectTaskResult(
    TaskSnapshot Snapshot,
    string? RoomId,
    string TaskId,
    string ReviewerName);
