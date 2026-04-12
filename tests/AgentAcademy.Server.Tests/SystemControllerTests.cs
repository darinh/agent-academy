using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class SystemControllerTests : IDisposable
{
    private static readonly AgentDefinition TestAgent = new(
        Id: "planner-1",
        Name: "Planner",
        Role: "Planner",
        Summary: "Plans things",
        StartupPrompt: "You plan things.",
        Model: "gpt-4",
        CapabilityTags: [],
        EnabledTools: [],
        AutoJoinDefaultRoom: true);

    private readonly TestServiceGraph _svc;
    private readonly SystemController _controller;

    public SystemControllerTests()
    {
        _svc = new TestServiceGraph([TestAgent]);
        _controller = new SystemController(
            _svc.RoomService, _svc.AgentLocationService,
            _svc.BreakoutRoomService, _svc.ActivityPublisher,
            _svc.Executor, _svc.Catalog, _svc.Db,
            _svc.UsageTracker, _svc.ErrorTracker,
            NullLogger<SystemController>.Instance);
    }

    public void Dispose() => _svc.Dispose();

    // ── GetRoot ──────────────────────────────────────────────────

    [Fact]
    public void GetRoot_ReturnsServiceInfo()
    {
        var result = _controller.GetRoot();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Agent Academy", json);
        Assert.Contains("endpoints", json);
    }

    // ── GetHealth ─────────────────────────────────────────────────

    [Fact]
    public void GetHealth_ReturnsHealthy()
    {
        var result = _controller.GetHealth();
        var ok = Assert.IsType<OkObjectResult>(result);
        var health = Assert.IsType<HealthResult>(ok.Value);
        Assert.Equal("healthy", health.Status);
    }

    // ── GetInstanceHealth ─────────────────────────────────────────

    [Fact]
    public void GetInstanceHealth_ReturnsInstanceInfo()
    {
        _svc.Executor.IsFullyOperational.Returns(true);
        _svc.Executor.IsAuthFailed.Returns(false);
        _svc.Executor.CircuitBreakerState.Returns(CircuitState.Closed);

        var result = _controller.GetInstanceHealth();
        var ok = Assert.IsType<OkObjectResult>(result);
        var instance = Assert.IsType<InstanceHealthResult>(ok.Value);
        Assert.NotNull(instance.InstanceId);
        Assert.True(instance.ExecutorOperational);
    }

    // ── GetOverview ───────────────────────────────────────────────

    [Fact]
    public async Task GetOverview_EmptyDb_ReturnsOverview()
    {
        var result = await _controller.GetOverview();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var overview = Assert.IsType<WorkspaceOverview>(ok.Value);
        Assert.Single(overview.ConfiguredAgents); // Our test agent
        Assert.Empty(overview.Rooms);
    }

    [Fact]
    public async Task GetOverview_WithRooms_IncludesThemInOverview()
    {
        await _svc.RoomService.CreateRoomAsync("Overview Room");

        var result = await _controller.GetOverview();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var overview = Assert.IsType<WorkspaceOverview>(ok.Value);
        Assert.Single(overview.Rooms);
    }

    // ── GetConfiguredAgents ───────────────────────────────────────

    [Fact]
    public async Task GetConfiguredAgents_ReturnsCatalogAgents()
    {
        var result = await _controller.GetConfiguredAgents();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var agents = Assert.IsType<List<AgentDefinition>>(ok.Value);
        Assert.Single(agents);
        Assert.Equal("planner-1", agents[0].Id);
    }

    // ── GetModels ────────────────────────────────────────────────

    [Fact]
    public void GetModels_ReturnsDistinctModelsFromCatalog()
    {
        _svc.Executor.IsFullyOperational.Returns(true);

        var result = _controller.GetModels();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("gpt-4", json);
        Assert.Contains("executorOperational", json);
    }

    // ── GetRestartHistory ────────────────────────────────────────

    [Fact]
    public async Task GetRestartHistory_EmptyDb_ReturnsEmptyList()
    {
        var result = await _controller.GetRestartHistory();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("instances", json);
        Assert.Contains("total", json);
    }

    // ── GetRestartStats ──────────────────────────────────────────

    [Fact]
    public async Task GetRestartStats_ReturnsOk()
    {
        var result = await _controller.GetRestartStats(hours: 24);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }
}
