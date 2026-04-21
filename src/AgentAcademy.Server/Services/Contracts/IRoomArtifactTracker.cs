using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for tracking file artifacts produced by agents in rooms.
/// </summary>
public interface IRoomArtifactTracker
{
    /// <summary>
    /// Records a file artifact produced by an agent. No-ops if roomId is null/empty.
    /// </summary>
    Task RecordAsync(
        string? roomId, string agentId, string filePath, string operation, string? commitSha = null);

    /// <summary>
    /// Records multiple file artifacts from a single commit in one batch.
    /// </summary>
    Task RecordCommitAsync(
        string? roomId, string agentId, string commitSha, IReadOnlyList<string> filePaths);

    /// <summary>
    /// Returns artifacts for a room, ordered by most recent first.
    /// </summary>
    Task<List<ArtifactRecord>> GetRoomArtifactsAsync(
        string roomId, int limit = 100, CancellationToken ct = default);
}
