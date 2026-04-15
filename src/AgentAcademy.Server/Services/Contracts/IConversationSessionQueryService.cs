using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for read-only conversation session queries — room history,
/// session stats, and context retrieval for agents.
/// </summary>
public interface IConversationSessionQueryService
{
    /// <summary>
    /// Returns the summary from the most recently archived session for a room,
    /// or null if no prior session exists.
    /// </summary>
    Task<string?> GetSessionContextAsync(string roomId);

    /// <summary>
    /// Returns the summary from the most recently archived session for a
    /// given sprint and stage.
    /// </summary>
    Task<string?> GetStageContextAsync(string sprintId, string stage);

    /// <summary>
    /// Returns one summary per stage for a sprint, deduplicated to the latest
    /// archived session per stage, ordered by canonical sprint stage sequence.
    /// </summary>
    Task<List<(string Stage, string Summary)>> GetSprintContextAsync(string sprintId);

    /// <summary>
    /// Lists conversation sessions for a specific room, ordered by sequence number descending.
    /// </summary>
    Task<(List<ConversationSessionSnapshot> Sessions, int TotalCount)> GetRoomSessionsAsync(
        string roomId, string? status = null, int limit = 20, int offset = 0);

    /// <summary>
    /// Lists conversation sessions across all rooms, ordered by creation date descending.
    /// Optionally filtered by workspace path for project-scoped queries.
    /// </summary>
    Task<(List<ConversationSessionSnapshot> Sessions, int TotalCount)> GetAllSessionsAsync(
        string? status = null, int limit = 20, int offset = 0, int? hoursBack = null,
        string? workspacePath = null);

    /// <summary>
    /// Returns a summary of session stats: total sessions, active/archived counts,
    /// total messages across all sessions. Optionally scoped to a workspace.
    /// </summary>
    Task<SessionStats> GetSessionStatsAsync(int? hoursBack = null, string? workspacePath = null);
}
