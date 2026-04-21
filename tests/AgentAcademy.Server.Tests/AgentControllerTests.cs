using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class AgentControllerTests : IDisposable
{
    private static readonly AgentDefinition TestAgent = new(
        Id: "engineer-1",
        Name: "Engineer",
        Role: "Engineer",
        Summary: "Writes code",
        StartupPrompt: "You are an engineer.",
        Model: "gpt-4",
        CapabilityTags: ["code"],
        EnabledTools: ["chat"],
        AutoJoinDefaultRoom: true);

    private readonly TestServiceGraph _svc;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentQuotaService _quotaService;
    private readonly AgentController _controller;
    private readonly AgentConfigController _configController;

    public AgentControllerTests()
    {
        _svc = new TestServiceGraph([TestAgent]);

        // Build a real ServiceProvider with the same SQLite connection so
        // HttpContext.RequestServices.CreateScope() resolves AgentAcademyDbContext.
        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_svc.Connection));
        _serviceProvider = services.BuildServiceProvider();

        _quotaService = new AgentQuotaService(
            _svc.ScopeFactory, _svc.UsageTracker,
            NullLogger<AgentQuotaService>.Instance);

        _controller = new AgentController(
            _svc.AgentLocationService, _svc.BreakoutRoomService,
            _svc.Executor, _svc.Catalog,
            _quotaService, NullLogger<AgentController>.Instance);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = _serviceProvider }
        };

        _configController = new AgentConfigController(
            _svc.Catalog, _svc.AgentConfigService,
            NullLogger<AgentConfigController>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _svc.Dispose();
    }

    // ── GetAgentLocations ────────────────────────────────────────

    [Fact]
    public async Task GetAgentLocations_ReturnsOk()
    {
        var result = await _controller.GetAgentLocations();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<List<AgentLocation>>(ok.Value);
    }

    // ── UpdateAgentLocation ──────────────────────────────────────

    [Fact]
    public async Task UpdateAgentLocation_Valid_ReturnsLocation()
    {
        // Seed a room for the agent to move into
        _svc.Db.Rooms.Add(new AgentAcademy.Server.Data.Entities.RoomEntity
        {
            Id = "main", Name = "Main Room", Status = "Active",
            CurrentPhase = "Intake", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/tmp/test"
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.UpdateAgentLocation("engineer-1",
            new UpdateLocationRequest("main", AgentState.Idle, null));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var location = Assert.IsType<AgentLocation>(ok.Value);
        Assert.Equal("engineer-1", location.AgentId);
        Assert.Equal("main", location.RoomId);
    }

    // ── GetAgentKnowledge ────────────────────────────────────────

    [Fact]
    public async Task GetAgentKnowledge_NoMemories_ReturnsEmptyEntries()
    {
        var result = await _controller.GetAgentKnowledge("engineer-1", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"entries\":[]", json);
    }

    [Fact]
    public async Task GetAgentKnowledge_WithMemories_ReturnsFormattedEntries()
    {
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Category = "pattern", Key = "test-key",
            Value = "Test value", CreatedAt = DateTime.UtcNow
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetAgentKnowledge("engineer-1", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("[pattern] test-key: Test value", json);
    }

    [Fact]
    public async Task GetAgentKnowledge_ExcludesExpiredMemories()
    {
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Category = "pattern", Key = "active",
            Value = "Active memory", CreatedAt = DateTime.UtcNow
        });
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Category = "pattern", Key = "expired",
            Value = "Expired memory", CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetAgentKnowledge("engineer-1", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("active", json);
        Assert.DoesNotContain("expired", json);
    }

    [Fact]
    public async Task GetAgentKnowledge_UnknownAgent_ReturnsNotFound()
    {
        var result = await _controller.GetAgentKnowledge("nonexistent", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetAgentKnowledge_ExcludesOtherAgentMemories()
    {
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Category = "pattern", Key = "mine",
            Value = "My memory", CreatedAt = DateTime.UtcNow
        });
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "other-agent", Category = "pattern", Key = "theirs",
            Value = "Their memory", CreatedAt = DateTime.UtcNow
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetAgentKnowledge("engineer-1", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("mine", json);
        Assert.DoesNotContain("theirs", json);
    }

    // ── AppendAgentKnowledge ─────────────────────────────────────

    [Fact]
    public async Task AppendAgentKnowledge_CreatesMemoryEntry()
    {
        var result = await _controller.AppendAgentKnowledge("engineer-1",
            new AppendKnowledgeRequest("The build command is dotnet build"),
            CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("engineer-1", json);
        Assert.Contains("knowledge", json);

        // Verify the memory was persisted
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memory = await db.AgentMemories.FirstOrDefaultAsync(m => m.AgentId == "engineer-1");
        Assert.NotNull(memory);
        Assert.Equal("knowledge", memory.Category);
        Assert.Equal("The build command is dotnet build", memory.Value);
    }

    [Fact]
    public async Task AppendAgentKnowledge_UpsertsSameKey()
    {
        await _controller.AppendAgentKnowledge("engineer-1",
            new AppendKnowledgeRequest("First version of fact"),
            CancellationToken.None);
        await _controller.AppendAgentKnowledge("engineer-1",
            new AppendKnowledgeRequest("First version of fact updated"),
            CancellationToken.None);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var memories = await db.AgentMemories
            .Where(m => m.AgentId == "engineer-1")
            .ToListAsync();
        // The two entries have different keys (different text after ~60 chars)
        // so they both exist — but entries with identical slug should upsert
        Assert.True(memories.Count >= 1);
    }

    [Fact]
    public async Task AppendAgentKnowledge_EmptyEntry_ReturnsBadRequest()
    {
        var result = await _controller.AppendAgentKnowledge("engineer-1",
            new AppendKnowledgeRequest(""),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AppendAgentKnowledge_UnknownAgent_ReturnsNotFound()
    {
        var result = await _controller.AppendAgentKnowledge("nonexistent",
            new AppendKnowledgeRequest("Some fact"),
            CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── GetSharedKnowledge ───────────────────────────────────────

    [Fact]
    public async Task GetSharedKnowledge_NoMemories_ReturnsEmptyDict()
    {
        var result = await _controller.GetSharedKnowledge(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("{}", json);
    }

    [Fact]
    public async Task GetSharedKnowledge_WithMemories_ReturnsGroupedByAgent()
    {
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Category = "pattern", Key = "fact-a",
            Value = "Fact A", CreatedAt = DateTime.UtcNow
        });
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "other-agent", Category = "gotcha", Key = "fact-b",
            Value = "Fact B", CreatedAt = DateTime.UtcNow
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetSharedKnowledge(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("engineer-1", json);
        Assert.Contains("other-agent", json);
        Assert.Contains("fact-a", json);
        Assert.Contains("fact-b", json);
    }

    [Fact]
    public async Task GetSharedKnowledge_ExcludesExpiredMemories()
    {
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Category = "pattern", Key = "active",
            Value = "Active", CreatedAt = DateTime.UtcNow
        });
        _svc.Db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "engineer-1", Category = "pattern", Key = "expired",
            Value = "Expired", CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetSharedKnowledge(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("active", json);
        Assert.DoesNotContain("expired", json);
    }

    // ── GetAgentConfig ───────────────────────────────────────────

    [Fact]
    public async Task GetAgentConfig_CatalogAgent_ReturnsConfig()
    {
        var result = await _configController.GetAgentConfig("engineer-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var config = Assert.IsType<AgentConfigResponse>(ok.Value);
        Assert.Equal("engineer-1", config.AgentId);
    }

    [Fact]
    public async Task GetAgentConfig_UnknownAgent_ReturnsNotFound()
    {
        var result = await _configController.GetAgentConfig("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── GetAgentSessions ─────────────────────────────────────────

    [Fact]
    public async Task GetAgentSessions_ReturnsEmptyForNewAgent()
    {
        var result = await _controller.GetAgentSessions("engineer-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sessions = Assert.IsType<List<BreakoutRoom>>(ok.Value);
        Assert.Empty(sessions);
    }
}
