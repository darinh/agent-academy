using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Tracks file artifacts produced by agents in rooms (append-only event log).
/// Records write_file, commit, and delete operations for observability.
/// </summary>
public sealed class RoomArtifactTracker : IRoomArtifactTracker
{
    private readonly AgentAcademyDbContext _db;
    private readonly IActivityPublisher _activity;
    private readonly ILogger<RoomArtifactTracker> _logger;

    public RoomArtifactTracker(
        AgentAcademyDbContext db,
        IActivityPublisher activity,
        ILogger<RoomArtifactTracker> logger)
    {
        _db = db;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>
    /// Records a file artifact produced by an agent. No-ops if roomId is null/empty.
    /// </summary>
    public async Task RecordAsync(
        string? roomId,
        string agentId,
        string filePath,
        string operation,
        string? commitSha = null)
    {
        if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(filePath))
            return;

        var entity = new RoomArtifactEntity
        {
            RoomId = roomId,
            AgentId = agentId,
            FilePath = filePath,
            Operation = operation,
            CommitSha = commitSha,
            Timestamp = DateTime.UtcNow,
        };

        _db.RoomArtifacts.Add(entity);

        _activity.Publish(
            ActivityEventType.ArtifactEvaluated, // reuse existing event type
            roomId,
            agentId,
            taskId: null,
            $"{agentId} {operation.ToLowerInvariant()} {filePath}");

        await _db.SaveChangesAsync();

        _logger.LogDebug(
            "Recorded artifact: {Operation} {FilePath} by {AgentId} in room {RoomId}",
            operation, filePath, agentId, roomId);
    }

    /// <summary>
    /// Records multiple file artifacts from a single commit in one batch.
    /// </summary>
    public async Task RecordCommitAsync(
        string? roomId,
        string agentId,
        string commitSha,
        IReadOnlyList<string> filePaths)
    {
        if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(agentId) || filePaths.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var filePath in filePaths)
        {
            _db.RoomArtifacts.Add(new RoomArtifactEntity
            {
                RoomId = roomId,
                AgentId = agentId,
                FilePath = filePath,
                Operation = "Committed",
                CommitSha = commitSha,
                Timestamp = now,
            });
        }

        _activity.Publish(
            ActivityEventType.ArtifactEvaluated,
            roomId,
            agentId,
            taskId: null,
            $"{agentId} committed {filePaths.Count} file(s) ({commitSha[..Math.Min(7, commitSha.Length)]})");

        await _db.SaveChangesAsync();

        _logger.LogDebug(
            "Recorded {Count} committed artifacts by {AgentId} in room {RoomId} (commit {Sha})",
            filePaths.Count, agentId, roomId, commitSha[..Math.Min(7, commitSha.Length)]);
    }

    /// <summary>
    /// Returns artifacts for a room, ordered by most recent first.
    /// </summary>
    public async Task<List<ArtifactRecord>> GetRoomArtifactsAsync(
        string roomId, int limit = 100, CancellationToken ct = default)
    {
        return await _db.RoomArtifacts
            .Where(a => a.RoomId == roomId)
            .OrderByDescending(a => a.Timestamp)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(a => new ArtifactRecord(a.AgentId, a.RoomId, a.FilePath, a.Operation, a.Timestamp))
            .ToListAsync(ct);
    }
}
