namespace AgentAcademy.Shared.Models;

/// <summary>
/// Request to post an agent message to a room.
/// </summary>
public record PostMessageRequest(
    string RoomId,
    string SenderId,
    string Content,
    MessageKind Kind = MessageKind.Response,
    string? CorrelationId = null,
    DeliveryHint? Hint = null
);

/// <summary>
/// Request to transition a room to a new collaboration phase.
/// </summary>
public record PhaseTransitionRequest(
    string RoomId,
    CollaborationPhase TargetPhase,
    string? Reason = null
);
