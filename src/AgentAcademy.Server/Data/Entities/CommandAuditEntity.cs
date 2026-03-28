namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a command audit trail entry.
/// Every command execution is recorded for observability.
/// Maps to the "command_audits" table.
/// </summary>
public class CommandAuditEntity
{
    public string Id { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string ArgsJson { get; set; } = "{}";
    public string Status { get; set; } = "Success";
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RoomId { get; set; }
    public DateTime Timestamp { get; set; }
}
