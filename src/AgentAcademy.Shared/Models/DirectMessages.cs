namespace AgentAcademy.Shared.Models;

/// <summary>
/// Summary of a DM conversation thread between the human and an agent.
/// </summary>
public record DmThreadSummary(
    string AgentId,
    string AgentName,
    string AgentRole,
    string LastMessage,
    DateTime LastMessageAt,
    int MessageCount
);

/// <summary>
/// A direct message in a DM thread, formatted for API responses.
/// </summary>
public record DmMessage(
    string Id,
    string SenderId,
    string SenderName,
    string Content,
    DateTime SentAt,
    bool IsFromHuman
);
