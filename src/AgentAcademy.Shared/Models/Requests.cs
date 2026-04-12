using System.ComponentModel.DataAnnotations;

namespace AgentAcademy.Shared.Models;

/// <summary>
/// Request to post an agent message to a room.
/// </summary>
public record PostMessageRequest(
    [property: Required, StringLength(100)] string RoomId,
    [property: Required, StringLength(100)] string SenderId,
    [property: Required, MinLength(1), StringLength(50_000)] string Content,
    [property: EnumDataType(typeof(MessageKind))] MessageKind Kind = MessageKind.Response,
    string? CorrelationId = null,
    DeliveryHint? Hint = null
);

/// <summary>
/// Request to transition a room to a new collaboration phase.
/// </summary>
public record PhaseTransitionRequest(
    [property: Required, StringLength(100)] string RoomId,
    [property: EnumDataType(typeof(CollaborationPhase))] CollaborationPhase TargetPhase,
    [property: StringLength(500)] string? Reason = null
);
