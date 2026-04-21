using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Artifacts;

/// <summary>
/// Content-addressed artifact store. Artifacts are immutable and identified by SHA-256 hash.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Write an artifact envelope and its advisory metadata.
    /// Returns the raw hex hash (no prefix).
    /// Idempotent: if the hash already exists, verifies byte-equality and skips.
    /// </summary>
    Task<string> WriteAsync(ArtifactEnvelope envelope, ArtifactMeta meta, CancellationToken ct = default);

    /// <summary>
    /// Read an artifact envelope by its raw hex hash.
    /// Returns null if not found.
    /// </summary>
    Task<ArtifactEnvelope?> ReadAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Read advisory metadata for an artifact by its raw hex hash.
    /// Returns null if not found (meta is advisory — absence is not an error).
    /// </summary>
    Task<ArtifactMeta?> ReadMetaAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Check if an artifact exists by its raw hex hash.
    /// </summary>
    Task<bool> ExistsAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Verify that an artifact's content matches its hash.
    /// Returns false if the file doesn't exist or the hash doesn't match.
    /// </summary>
    Task<bool> VerifyAsync(string hash, CancellationToken ct = default);
}
