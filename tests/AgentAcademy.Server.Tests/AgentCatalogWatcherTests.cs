using System.Text.Json;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class AgentCatalogWatcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;
    private readonly string _agentsJsonPath;
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _broadcaster;
    private readonly CopilotSessionPool _sessionPool;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public AgentCatalogWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aa-test-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_tempDir, "Config");
        Directory.CreateDirectory(_configDir);
        _agentsJsonPath = Path.Combine(_configDir, "agents.json");

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var opts = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection).Options;
        _db = new AgentAcademyDbContext(opts);
        _db.Database.EnsureCreated();

        _broadcaster = new ActivityBroadcaster();
        _sessionPool = new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(AgentAcademyDbContext)).Returns(_db);
        _scopeFactory = scopeFactory;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void WriteAgents(params AgentDefinition[] agents)
    {
        var catalog = new { AgentCatalog = new { DefaultRoomId = "main", DefaultRoomName = "Main Room", Agents = agents } };
        File.WriteAllText(_agentsJsonPath, JsonSerializer.Serialize(catalog, JsonOpts));
    }

    private static AgentDefinition MakeAgent(string id, string name, string role, string prompt = "test prompt") =>
        new(id, name, role, $"{name} summary", prompt, null, [], [], true);

    private AgentCatalogWatcher CreateWatcher(AgentCatalog catalog) =>
        new(catalog, new AgentCatalogFileInfo(_agentsJsonPath),
            _scopeFactory, _broadcaster, _sessionPool,
            NullLogger<AgentCatalogWatcher>.Instance);

    // ──────────────── CatalogDiff tests ────────────────

    [Fact]
    public async Task TriggerReload_WhenNoChanges_ReturnsNoChanges()
    {
        var agents = new[] { MakeAgent("a1", "Alpha", "Engineer") };
        WriteAgents(agents);
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        var result = await watcher.TriggerReloadAsync();

        Assert.False(result.WasReloaded);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TriggerReload_WhenAgentAdded_DetectsAddition()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));

        var result = await watcher.TriggerReloadAsync();

        Assert.True(result.WasReloaded);
        Assert.NotNull(result.Diff);
        Assert.Single(result.Diff.Added);
        Assert.Equal("a2", result.Diff.Added[0].Id);
        Assert.Empty(result.Diff.Removed);
        Assert.Empty(result.Diff.Modified);
    }

    [Fact]
    public async Task TriggerReload_WhenAgentRemoved_DetectsRemoval()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));

        var result = await watcher.TriggerReloadAsync();

        Assert.True(result.WasReloaded);
        Assert.NotNull(result.Diff);
        Assert.Single(result.Diff.Removed);
        Assert.Equal("a2", result.Diff.Removed[0].Id);
    }

    [Fact]
    public async Task TriggerReload_WhenAgentModified_DetectsModification()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer", "old prompt"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer", "new prompt"));

        var result = await watcher.TriggerReloadAsync();

        Assert.True(result.WasReloaded);
        Assert.NotNull(result.Diff);
        Assert.Single(result.Diff.Modified);
        Assert.Equal("a1", result.Diff.Modified[0].Id);
    }

    [Fact]
    public async Task TriggerReload_WhenRoleChanged_DetectsModification()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        WriteAgents(new AgentDefinition("a1", "Alpha", "Reviewer", "Alpha summary", "test prompt", null, [], [], true));

        var result = await watcher.TriggerReloadAsync();

        Assert.True(result.WasReloaded);
        Assert.NotNull(result.Diff);
        Assert.Single(result.Diff.Modified);
    }

    // ──────────────── Catalog update tests ────────────────

    [Fact]
    public async Task TriggerReload_UpdatesCatalogSnapshot()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        Assert.Single(catalog.Agents);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));
        await watcher.TriggerReloadAsync();

        Assert.Equal(2, catalog.Agents.Count);
        Assert.Contains(catalog.Agents, a => a.Id == "a2");
    }

    [Fact]
    public async Task TriggerReload_PreservesOldCatalogOnParseFailure()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        File.WriteAllText(_agentsJsonPath, "{ invalid json }}}");

        var result = await watcher.TriggerReloadAsync();

        Assert.False(result.WasReloaded);
        Assert.NotNull(result.Error);
        Assert.Single(catalog.Agents);
        Assert.Equal("a1", catalog.Agents[0].Id);
    }

    // ──────────────── DB reconciliation tests ────────────────

    [Fact]
    public async Task TriggerReload_CreatesAgentLocationForNewAgent()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));
        await watcher.TriggerReloadAsync();

        var loc = await _db.AgentLocations.FindAsync("a2");
        Assert.NotNull(loc);
        Assert.Equal("main", loc.RoomId);
        Assert.Equal("Idle", loc.State);
    }

    [Fact]
    public async Task TriggerReload_SetsRemovedAgentToOffline()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);

        _db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = "a2", RoomId = "main", State = "Idle", UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        using var watcher = CreateWatcher(catalog);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        await watcher.TriggerReloadAsync();

        var loc = await _db.AgentLocations.FindAsync("a2");
        Assert.NotNull(loc);
        Assert.Equal("Offline", loc.State);
    }

    [Fact]
    public async Task TriggerReload_DoesNotDuplicateExistingLocation()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);

        _db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = "a2", RoomId = "breakout-1", State = "Working", UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        using var watcher = CreateWatcher(catalog);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));
        await watcher.TriggerReloadAsync();

        var loc = await _db.AgentLocations.FindAsync("a2");
        Assert.NotNull(loc);
        Assert.Equal("Working", loc.State); // Preserved existing state
    }

    // ──────────────── Activity broadcast tests ────────────────

    [Fact]
    public async Task TriggerReload_BroadcastsActivityEvent()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        ActivityEvent? capturedEvent = null;
        _broadcaster.Subscribe(evt => capturedEvent = evt);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));
        await watcher.TriggerReloadAsync();

        Assert.NotNull(capturedEvent);
        Assert.Equal(ActivityEventType.AgentCatalogReloaded, capturedEvent.Type);
        Assert.Contains("added: Beta", capturedEvent.Message);
    }

    [Fact]
    public async Task TriggerReload_NoBroadcastWhenNoChanges()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        ActivityEvent? capturedEvent = null;
        _broadcaster.Subscribe(evt => capturedEvent = evt);

        await watcher.TriggerReloadAsync();

        Assert.Null(capturedEvent);
    }

    // ──────────────── Reload serialization tests ────────────────

    [Fact]
    public async Task TriggerReload_ConcurrentCalls_SerializesCorrectly()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => watcher.TriggerReloadAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Exactly one should report WasReloaded; the rest should be Skipped or NoChanges
        var reloaded = results.Count(r => r.WasReloaded);
        Assert.Equal(1, reloaded);
    }

    // ──────────────── Complex scenario tests ────────────────

    [Fact]
    public async Task TriggerReload_MultipleChanges_AllDetected()
    {
        WriteAgents(
            MakeAgent("a1", "Alpha", "Engineer"),
            MakeAgent("a2", "Beta", "Reviewer"),
            MakeAgent("a3", "Gamma", "Planner"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        WriteAgents(
            MakeAgent("a1", "Alpha", "Architect"), // modified role
            MakeAgent("a4", "Delta", "Frontend")); // added; a2 + a3 removed

        var result = await watcher.TriggerReloadAsync();

        Assert.True(result.WasReloaded);
        Assert.NotNull(result.Diff);
        Assert.Single(result.Diff.Added);
        Assert.Equal("a4", result.Diff.Added[0].Id);
        Assert.Equal(2, result.Diff.Removed.Count);
        Assert.Single(result.Diff.Modified);
        Assert.Equal("a1", result.Diff.Modified[0].Id);
    }

    [Fact]
    public async Task TriggerReload_EmptyCatalog_RemovesAll()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        WriteAgents(); // empty agents list

        var result = await watcher.TriggerReloadAsync();

        Assert.True(result.WasReloaded);
        Assert.NotNull(result.Diff);
        Assert.Equal(2, result.Diff.Removed.Count);
        Assert.Empty(catalog.Agents);
    }

    [Fact]
    public async Task TriggerReload_SequentialReloads_TrackChangesCorrectly()
    {
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var options = AgentCatalogLoader.Load(_tempDir);
        var catalog = new AgentCatalog(options);
        using var watcher = CreateWatcher(catalog);

        // First reload: add agent
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"), MakeAgent("a2", "Beta", "Reviewer"));
        var result1 = await watcher.TriggerReloadAsync();
        Assert.True(result1.WasReloaded);
        Assert.Single(result1.Diff!.Added);

        // Second reload: remove the added agent
        WriteAgents(MakeAgent("a1", "Alpha", "Engineer"));
        var result2 = await watcher.TriggerReloadAsync();
        Assert.True(result2.WasReloaded);
        Assert.Single(result2.Diff!.Removed);
        Assert.Equal("a2", result2.Diff.Removed[0].Id);
    }

    // ──────────────── CatalogReloadResult tests ────────────────

    [Fact]
    public void CatalogReloadResult_Success_HasCorrectProperties()
    {
        var diff = new CatalogDiff([], [], []);
        var result = CatalogReloadResult.Success(diff);
        Assert.True(result.WasReloaded);
        Assert.False(result.WasSkipped);
        Assert.Null(result.Error);
        Assert.Same(diff, result.Diff);
    }

    [Fact]
    public void CatalogReloadResult_Failed_HasError()
    {
        var result = CatalogReloadResult.Failed("parse error");
        Assert.False(result.WasReloaded);
        Assert.Equal("parse error", result.Error);
    }

    [Fact]
    public void CatalogDiff_HasChanges_FalseWhenEmpty()
    {
        var diff = new CatalogDiff([], [], []);
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void CatalogDiff_HasChanges_TrueWhenAdded()
    {
        var diff = new CatalogDiff([MakeAgent("a1", "A", "E")], [], []);
        Assert.True(diff.HasChanges);
    }
}
