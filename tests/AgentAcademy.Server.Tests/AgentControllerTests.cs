using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
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
    private readonly AgentQuotaService _quotaService;
    private readonly AgentController _controller;
    private readonly AgentConfigController _configController;

    public AgentControllerTests()
    {
        _svc = new TestServiceGraph([TestAgent]);

        _quotaService = new AgentQuotaService(
            _svc.ScopeFactory, _svc.UsageTracker,
            NullLogger<AgentQuotaService>.Instance);

        _controller = new AgentController(
            _svc.AgentLocationService, _svc.BreakoutRoomService,
            _svc.Executor, _svc.Catalog,
            _quotaService, NullLogger<AgentController>.Instance);

        _configController = new AgentConfigController(
            _svc.Catalog, _svc.AgentConfigService,
            NullLogger<AgentConfigController>.Instance);
    }

    public void Dispose() => _svc.Dispose();

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
    public void GetAgentKnowledge_ReturnsEmptyEntries()
    {
        var result = _controller.GetAgentKnowledge("engineer-1");
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("entries", json);
    }

    // ── AppendAgentKnowledge ─────────────────────────────────────

    [Fact]
    public void AppendAgentKnowledge_ReturnsNotImplemented()
    {
        var result = _controller.AppendAgentKnowledge("engineer-1",
            new AppendKnowledgeRequest("Test knowledge entry"));
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(501, obj.StatusCode);
    }

    // ── GetSharedKnowledge ───────────────────────────────────────

    [Fact]
    public void GetSharedKnowledge_ReturnsEmptyDictionary()
    {
        var result = _controller.GetSharedKnowledge();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<Dictionary<string, string[]>>(ok.Value);
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
