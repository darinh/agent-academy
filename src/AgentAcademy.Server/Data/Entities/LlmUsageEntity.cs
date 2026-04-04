namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a single LLM API call's usage metrics.
/// Captured from the Copilot SDK's <c>AssistantUsageEvent</c>.
/// Maps to the "llm_usage" table.
/// </summary>
public class LlmUsageEntity
{
    public string Id { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string? RoomId { get; set; }
    public string? Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public double? Cost { get; set; }
    public int? DurationMs { get; set; }
    public string? ApiCallId { get; set; }
    public string? Initiator { get; set; }
    public string? ReasoningEffort { get; set; }
    public DateTime RecordedAt { get; set; }
}
