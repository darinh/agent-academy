using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Records and queries LLM API usage metrics. Implementations must be
/// safe to call from hot paths — failures in <see cref="RecordAsync"/>
/// must not propagate to callers.
/// </summary>
public interface ILlmUsageTracker
{
    /// <summary>
    /// Persists a single LLM usage record. Failures are logged but never
    /// propagated — usage tracking must not break agent execution.
    /// </summary>
    Task RecordAsync(
        string agentId,
        string? roomId,
        string? model,
        double? inputTokens,
        double? outputTokens,
        double? cacheReadTokens,
        double? cacheWriteTokens,
        double? cost,
        double? durationMs,
        string? apiCallId,
        string? initiator,
        string? reasoningEffort);

    /// <summary>
    /// Aggregated usage for a specific agent since a given time.
    /// Used for quota enforcement.
    /// </summary>
    Task<AgentUsageWindow> GetAgentUsageSinceAsync(string agentId, DateTime since);

    /// <summary>
    /// Aggregated usage for a specific room.
    /// </summary>
    Task<UsageSummary> GetRoomUsageAsync(string roomId);

    /// <summary>
    /// Per-agent usage breakdown for a room.
    /// </summary>
    Task<List<AgentUsageSummary>> GetRoomUsageByAgentAsync(string roomId);

    /// <summary>
    /// Global usage summary across all rooms, optionally filtered by time window.
    /// </summary>
    Task<UsageSummary> GetGlobalUsageAsync(DateTime? since = null);

    /// <summary>
    /// Returns the current context window usage for each agent in a room.
    /// </summary>
    Task<List<AgentContextUsage>> GetLatestContextPerAgentAsync(string roomId);

    /// <summary>
    /// Recent individual usage records (for detailed inspection).
    /// </summary>
    Task<List<LlmUsageRecord>> GetRecentUsageAsync(
        string? roomId = null,
        string? agentId = null,
        int limit = 50,
        DateTime? since = null,
        CancellationToken ct = default);
}
