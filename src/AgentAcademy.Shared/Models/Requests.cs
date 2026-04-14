using System.ComponentModel.DataAnnotations;

namespace AgentAcademy.Shared.Models;

/// <summary>
/// Request to post an agent message to a room.
/// </summary>
public record PostMessageRequest(
    [Required, StringLength(100)] string RoomId,
    [Required, StringLength(100)] string SenderId,
    [Required, MinLength(1), StringLength(50_000)] string Content,
    [EnumDataType(typeof(MessageKind))] MessageKind Kind = MessageKind.Response,
    string? CorrelationId = null,
    DeliveryHint? Hint = null
);

/// <summary>
/// Request to transition a room to a new collaboration phase.
/// </summary>
public record PhaseTransitionRequest(
    [Required, StringLength(100)] string RoomId,
    [EnumDataType(typeof(CollaborationPhase))] CollaborationPhase TargetPhase,
    [StringLength(500)] string? Reason = null
);
