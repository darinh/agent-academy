using System.Text.Json;
using AgentAcademy.Forge.Models;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Artifacts;

/// <summary>
/// Disk-backed content-addressed artifact store.
/// Artifacts sharded by first two hex chars of SHA-256 hash.
/// Uses write-temp-then-rename for atomic writes.
/// </summary>
public sealed class DiskArtifactStore : IArtifactStore
{
    private readonly string _rootDir;
    private readonly ILogger<DiskArtifactStore> _logger;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DiskArtifactStore(string rootDir, ILogger<DiskArtifactStore> logger)
    {
        _rootDir = rootDir;
        _logger = logger;
    }

    public async Task<string> WriteAsync(ArtifactEnvelope envelope, ArtifactMeta meta, CancellationToken ct = default)
    {
        var canonicalJson = CanonicalJson.Serialize(envelope);
        var hash = CanonicalJson.Hash(envelope);

        var envelopePath = GetEnvelopePath(hash);
        var metaPath = GetMetaPath(hash);

        // Ensure shard directory exists
        var shardDir = Path.GetDirectoryName(envelopePath)!;
        Directory.CreateDirectory(shardDir);

        // Idempotent: if envelope already exists, verify byte-equality
        if (File.Exists(envelopePath))
        {
            var existing = await File.ReadAllTextAsync(envelopePath, ct);
            if (existing == canonicalJson)
            {
                _logger.LogDebug("Artifact {Hash} already exists, skipping write", hash[..12]);
                return hash;
            }

            // Hash collision — catastrophic invariant violation
            throw new InvalidOperationException(
                $"Hash collision detected for artifact {hash[..12]}: existing content differs from new content");
        }

        // Write envelope atomically
        await WriteAtomicAsync(envelopePath, canonicalJson, ct);
        _logger.LogDebug("Wrote artifact envelope {Hash}", hash[..12]);

        // Write advisory metadata (not hash-bound, best-effort)
        try
        {
            var metaJson = JsonSerializer.Serialize(meta, ReadOptions);
            await WriteAtomicAsync(metaPath, metaJson, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write artifact meta for {Hash} — advisory only, continuing", hash[..12]);
        }

        return hash;
    }

    public async Task<ArtifactEnvelope?> ReadAsync(string hash, CancellationToken ct = default)
    {
        var path = GetEnvelopePath(hash);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<ArtifactEnvelope>(json, ReadOptions);
    }

    public async Task<ArtifactMeta?> ReadMetaAsync(string hash, CancellationToken ct = default)
    {
        var path = GetMetaPath(hash);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<ArtifactMeta>(json, ReadOptions);
    }

    public Task<bool> ExistsAsync(string hash, CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(GetEnvelopePath(hash)));
    }

    public async Task<bool> VerifyAsync(string hash, CancellationToken ct = default)
    {
        var path = GetEnvelopePath(hash);
        if (!File.Exists(path))
            return false;

        var content = await File.ReadAllTextAsync(path, ct);

        // Re-parse and canonicalize to compute expected hash
        using var doc = JsonDocument.Parse(content);
        var actualHash = CanonicalJson.Hash(doc.RootElement);

        return string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase);
    }

    private string GetEnvelopePath(string hash)
    {
        var shard = hash[..2];
        return Path.Combine(_rootDir, shard, $"{hash}.json");
    }

    private string GetMetaPath(string hash)
    {
        var shard = hash[..2];
        return Path.Combine(_rootDir, shard, $"{hash}.meta.json");
    }

    /// <summary>
    /// Write-temp-then-rename for atomic file writes (POSIX same-filesystem).
    /// </summary>
    private static async Task WriteAtomicAsync(string targetPath, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        var tmpPath = Path.Combine(dir, $"{Path.GetFileName(targetPath)}.tmp.{Environment.ProcessId}.{Random.Shared.Next()}");

        try
        {
            await using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                await writer.WriteAsync(content.AsMemory(), ct);
                await writer.FlushAsync(ct);
                stream.Flush(flushToDisk: true); // fsync
            }

            File.Move(tmpPath, targetPath, overwrite: false);
        }
        catch
        {
            // Clean up temp file on failure
            try { File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }
    }
}
