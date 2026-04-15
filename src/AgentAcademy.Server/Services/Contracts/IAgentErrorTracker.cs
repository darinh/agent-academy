using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Records and queries agent error events. Implementations must be
/// safe to call from hot paths — failures in <see cref="RecordAsync"/>
/// must not propagate to callers.
/// </summary>
public interface IAgentErrorTracker
{
    /// <summary>
    /// Records an agent error. Failures are logged but never propagated.
    /// </summary>
    Task RecordAsync(
        string agentId,
        string? roomId,
        string errorType,
        string message,
        bool recoverable,
        bool retried = false,
        int? retryAttempt = null);

    /// <summary>
    /// Returns errors for a specific room, most recent first.
    /// </summary>
    Task<List<ErrorRecord>> GetRoomErrorsAsync(string roomId, int limit = 50);

    /// <summary>
    /// Returns recent errors across all rooms, optionally filtered by agent or time.
    /// </summary>
    Task<List<ErrorRecord>> GetRecentErrorsAsync(
        string? agentId = null,
        DateTime? since = null,
        int limit = 50);

    /// <summary>
    /// Returns error counts grouped by type for a time window.
    /// </summary>
    Task<ErrorSummary> GetErrorSummaryAsync(DateTime? since = null);
}
