using System.Text.Json;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Forge.Tests;

public sealed class DiskArtifactStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskArtifactStore _store;

    public DiskArtifactStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forge-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new DiskArtifactStore(_tempDir, NullLogger<DiskArtifactStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static (ArtifactEnvelope Envelope, ArtifactMeta Meta) CreateTestArtifact(string content = "test")
    {
        var payloadJson = "{\"data\":\"" + content + "\"}";
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        var envelope = new ArtifactEnvelope
        {
            ArtifactType = "requirements",
            SchemaVersion = "1",
            ProducedByPhase = "requirements",
            Payload = payload.Clone()
        };
        var meta = new ArtifactMeta
        {
            DerivedFrom = Array.Empty<string>(),
            InputHashes = Array.Empty<string>(),
            ProducedAt = DateTime.UtcNow,
            AttemptNumber = 1
        };
        return (envelope, meta);
    }

    [Fact]
    public async Task WriteAsync_ReturnsValidHash()
    {
        var (envelope, meta) = CreateTestArtifact();
        var hash = await _store.WriteAsync(envelope, meta);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public async Task WriteAsync_CreatesShardedFiles()
    {
        var (envelope, meta) = CreateTestArtifact();
        var hash = await _store.WriteAsync(envelope, meta);

        var shard = hash[..2];
        Assert.True(File.Exists(Path.Combine(_tempDir, shard, $"{hash}.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, shard, $"{hash}.meta.json")));
    }

    [Fact]
    public async Task WriteAsync_Idempotent_SameContent()
    {
        var (envelope, meta) = CreateTestArtifact();

        var hash1 = await _store.WriteAsync(envelope, meta);
        var hash2 = await _store.WriteAsync(envelope, meta);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task ReadAsync_ReturnsWrittenEnvelope()
    {
        var (envelope, meta) = CreateTestArtifact("roundtrip");
        var hash = await _store.WriteAsync(envelope, meta);

        var read = await _store.ReadAsync(hash);

        Assert.NotNull(read);
        Assert.Equal("requirements", read.ArtifactType);
        Assert.Equal("1", read.SchemaVersion);
        Assert.Equal("requirements", read.ProducedByPhase);
        Assert.Equal("roundtrip", read.Payload.GetProperty("data").GetString());
    }

    [Fact]
    public async Task ReadAsync_NonExistent_ReturnsNull()
    {
        var result = await _store.ReadAsync("0000000000000000000000000000000000000000000000000000000000000000");
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadMetaAsync_ReturnsWrittenMeta()
    {
        var (envelope, meta) = CreateTestArtifact();
        var hash = await _store.WriteAsync(envelope, meta);

        var read = await _store.ReadMetaAsync(hash);

        Assert.NotNull(read);
        Assert.Equal(1, read.AttemptNumber);
        Assert.Empty(read.DerivedFrom);
        Assert.Empty(read.InputHashes);
    }

    [Fact]
    public async Task ReadMetaAsync_NonExistent_ReturnsNull()
    {
        var result = await _store.ReadMetaAsync("0000000000000000000000000000000000000000000000000000000000000000");
        Assert.Null(result);
    }

    [Fact]
    public async Task ExistsAsync_TrueWhenWritten()
    {
        var (envelope, meta) = CreateTestArtifact();
        var hash = await _store.WriteAsync(envelope, meta);

        Assert.True(await _store.ExistsAsync(hash));
    }

    [Fact]
    public async Task ExistsAsync_FalseWhenNotWritten()
    {
        Assert.False(await _store.ExistsAsync("0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [Fact]
    public async Task VerifyAsync_TrueForValidArtifact()
    {
        var (envelope, meta) = CreateTestArtifact();
        var hash = await _store.WriteAsync(envelope, meta);

        Assert.True(await _store.VerifyAsync(hash));
    }

    [Fact]
    public async Task VerifyAsync_FalseForNonExistent()
    {
        Assert.False(await _store.VerifyAsync("0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [Fact]
    public async Task VerifyAsync_FalseForCorrupted()
    {
        var (envelope, meta) = CreateTestArtifact();
        var hash = await _store.WriteAsync(envelope, meta);

        // Corrupt the file
        var path = Path.Combine(_tempDir, hash[..2], $"{hash}.json");
        await File.WriteAllTextAsync(path, """{"corrupted": true}""");

        Assert.False(await _store.VerifyAsync(hash));
    }

    [Fact]
    public async Task WriteAsync_DifferentContent_DifferentHash()
    {
        var (envelope1, meta1) = CreateTestArtifact("content-A");
        var (envelope2, meta2) = CreateTestArtifact("content-B");

        var hash1 = await _store.WriteAsync(envelope1, meta1);
        var hash2 = await _store.WriteAsync(envelope2, meta2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task WriteAsync_ContentAddressed_HashMatchesCanonicalJson()
    {
        var (envelope, meta) = CreateTestArtifact("verify-hash");
        var expectedHash = CanonicalJson.Hash(envelope);

        var actualHash = await _store.WriteAsync(envelope, meta);

        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public async Task WriteAsync_EnvelopeFile_IsCanonicalJson()
    {
        var (envelope, meta) = CreateTestArtifact("canonical-check");
        var hash = await _store.WriteAsync(envelope, meta);

        var filePath = Path.Combine(_tempDir, hash[..2], $"{hash}.json");
        var fileContent = await File.ReadAllTextAsync(filePath);

        // Re-canonicalize and verify it matches
        using var doc = JsonDocument.Parse(fileContent);
        var recanonical = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal(recanonical, fileContent);
    }

    [Fact]
    public async Task WriteAsync_MultipleArtifacts_DifferentShards()
    {
        // Write many artifacts — they should distribute across shard dirs
        var hashes = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var (envelope, meta) = CreateTestArtifact($"multi-{i}");
            hashes.Add(await _store.WriteAsync(envelope, meta));
        }

        // Verify each is in the right shard
        foreach (var hash in hashes)
        {
            var shard = hash[..2];
            Assert.True(File.Exists(Path.Combine(_tempDir, shard, $"{hash}.json")));
        }
    }
}
