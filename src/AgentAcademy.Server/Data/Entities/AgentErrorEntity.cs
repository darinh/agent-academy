namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for an agent error event.
/// Captured from CopilotExecutor catch blocks. Maps to the "agent_errors" table.
/// </summary>
public class AgentErrorEntity
{
    public string Id { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string? RoomId { get; set; }
    public string ErrorType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool Recoverable { get; set; }
    public bool Retried { get; set; }
    public int? RetryAttempt { get; set; }
    public DateTime OccurredAt { get; set; }
}
