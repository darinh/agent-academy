using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class AgentMemoryLoaderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentMemoryLoader _sut;

    public AgentMemoryLoaderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        _sut = new AgentMemoryLoader(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentMemoryLoader>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private AgentAcademyDbContext GetDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    }

    private void SeedMemories(params AgentMemoryEntity[] memories)
    {
        using var db = GetDb();
        db.AgentMemories.AddRange(memories);
        db.SaveChanges();
    }

    private static AgentMemoryEntity MakeMemory(
        string agentId = "agent-1",
        string key = "key1",
        string category = "general",
        string value = "some value",
        DateTime? createdAt = null,
        DateTime? updatedAt = null,
        DateTime? lastAccessedAt = null,
        DateTime? expiresAt = null) =>
        new()
        {
            AgentId = agentId,
            Key = key,
            Category = category,
            Value = value,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = updatedAt,
            LastAccessedAt = lastAccessedAt,
            ExpiresAt = expiresAt,
        };

    // ── Basic loading ──

    [Fact]
    public async Task LoadAsync_ReturnsEmptyForAgentWithNoMemories()
    {
        var result = await _sut.LoadAsync("nonexistent-agent");
        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadAsync_ReturnsAgentsOwnMemories()
    {
        SeedMemories(
            MakeMemory(agentId: "agent-1", key: "pref1"),
            MakeMemory(agentId: "agent-1", key: "pref2"));

        var result = await _sut.LoadAsync("agent-1");

        Assert.Equal(2, result.Count);
        Assert.All(result, m => Assert.Equal("agent-1", m.AgentId));
    }

    [Fact]
    public async Task LoadAsync_ReturnsSharedMemories()
    {
        SeedMemories(
            MakeMemory(agentId: "system", key: "shared-key", category: "shared"));

        var result = await _sut.LoadAsync("agent-1");

        Assert.Single(result);
        Assert.Equal("shared", result[0].Category);
    }

    [Fact]
    public async Task LoadAsync_ExcludesOtherAgentsNonSharedMemories()
    {
        SeedMemories(
            MakeMemory(agentId: "agent-1", key: "mine"),
            MakeMemory(agentId: "agent-2", key: "theirs", category: "private"));

        var result = await _sut.LoadAsync("agent-1");

        Assert.Single(result);
        Assert.Equal("agent-1", result[0].AgentId);
    }

    // ── Expiry filtering ──

    [Fact]
    public async Task LoadAsync_ExcludesExpiredMemories()
    {
        SeedMemories(
            MakeMemory(agentId: "agent-1", key: "expired",
                expiresAt: DateTime.UtcNow.AddHours(-1)));

        var result = await _sut.LoadAsync("agent-1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadAsync_IncludesMemoriesWithNullExpiresAt()
    {
        SeedMemories(
            MakeMemory(agentId: "agent-1", key: "no-expiry", expiresAt: null));

        var result = await _sut.LoadAsync("agent-1");

        Assert.Single(result);
    }

    [Fact]
    public async Task LoadAsync_IncludesMemoriesWithFutureExpiresAt()
    {
        SeedMemories(
            MakeMemory(agentId: "agent-1", key: "future",
                expiresAt: DateTime.UtcNow.AddDays(7)));

        var result = await _sut.LoadAsync("agent-1");

        Assert.Single(result);
    }

    // ── Ordering ──

    [Fact]
    public async Task LoadAsync_OrdersByCategoryThenKey()
    {
        SeedMemories(
            MakeMemory(agentId: "agent-1", key: "z-key", category: "b-cat"),
            MakeMemory(agentId: "agent-1", key: "a-key", category: "b-cat"),
            MakeMemory(agentId: "agent-1", key: "m-key", category: "a-cat"));

        var result = await _sut.LoadAsync("agent-1");

        Assert.Equal(3, result.Count);
        Assert.Equal("a-cat", result[0].Category);
        Assert.Equal("m-key", result[0].Key);
        Assert.Equal("b-cat", result[1].Category);
        Assert.Equal("a-key", result[1].Key);
        Assert.Equal("b-cat", result[2].Category);
        Assert.Equal("z-key", result[2].Key);
    }

    // ── LastAccessedAt update ──

    [Fact]
    public async Task LoadAsync_UpdatesLastAccessedAtOnLoadedMemories()
    {
        var before = DateTime.UtcNow.AddMinutes(-10);
        SeedMemories(
            MakeMemory(agentId: "agent-1", key: "tracked", lastAccessedAt: before));

        await _sut.LoadAsync("agent-1");

        using var db = GetDb();
        var entity = await db.AgentMemories
            .FirstAsync(m => m.AgentId == "agent-1" && m.Key == "tracked");
        Assert.NotNull(entity.LastAccessedAt);
        Assert.True(entity.LastAccessedAt > before);
    }

    // ── Field mapping ──

    [Fact]
    public async Task LoadAsync_MapsAllFieldsToAgentMemoryRecord()
    {
        var created = DateTime.UtcNow.AddDays(-1);
        var updated = DateTime.UtcNow.AddHours(-1);
        var expires = DateTime.UtcNow.AddDays(7);
        SeedMemories(
            MakeMemory(
                agentId: "agent-1", key: "detail", category: "config",
                value: "test-value", createdAt: created,
                updatedAt: updated, expiresAt: expires));

        var result = await _sut.LoadAsync("agent-1");

        var m = Assert.Single(result);
        Assert.Equal("agent-1", m.AgentId);
        Assert.Equal("config", m.Category);
        Assert.Equal("detail", m.Key);
        Assert.Equal("test-value", m.Value);
        Assert.Equal(created, m.CreatedAt);
        Assert.Equal(updated, m.UpdatedAt);
        Assert.Equal(expires, m.ExpiresAt);
    }

    // ── Error handling ──

    [Fact]
    public async Task LoadAsync_ReturnsEmptyOnException()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var svc = new ServiceCollection();
        svc.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(conn));
        var sp = svc.BuildServiceProvider();
        var factory = sp.GetRequiredService<IServiceScopeFactory>();
        sp.Dispose();
        conn.Dispose();

        var brokenLoader = new AgentMemoryLoader(factory, NullLogger<AgentMemoryLoader>.Instance);

        var result = await brokenLoader.LoadAsync("agent-1");

        Assert.Empty(result);
    }
}
