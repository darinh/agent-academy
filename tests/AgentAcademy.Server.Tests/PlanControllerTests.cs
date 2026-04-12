using AgentAcademy.Server.Controllers;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class PlanControllerTests : IDisposable
{
    private readonly TestServiceGraph _svc;
    private readonly PlanController _controller;

    public PlanControllerTests()
    {
        _svc = new TestServiceGraph();
        _controller = new PlanController(
            _svc.PlanService, _svc.RoomService,
            _svc.BreakoutRoomService,
            NullLogger<PlanController>.Instance);
    }

    public void Dispose() => _svc.Dispose();

    [Fact]
    public async Task GetPlan_NoPlan_ReturnsNotFound()
    {
        var result = await _controller.GetPlan("nonexistent-room");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task SetPlan_EmptyContent_ReturnsBadRequest()
    {
        var result = await _controller.SetPlan("room1", new PlanContent(""));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetPlan_WhitespaceContent_ReturnsBadRequest()
    {
        var result = await _controller.SetPlan("room1", new PlanContent("   "));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetPlan_RoomNotFound_ReturnsNotFound()
    {
        var result = await _controller.SetPlan("no-such-room", new PlanContent("My plan"));
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SetAndGetPlan_RoundTrip()
    {
        var room = await _svc.RoomService.CreateRoomAsync("Test Room");
        var roomId = room.Id;

        var setResult = await _controller.SetPlan(roomId, new PlanContent("Sprint plan v1"));
        Assert.IsType<OkObjectResult>(setResult);

        var getResult = await _controller.GetPlan(roomId);
        var ok = Assert.IsType<OkObjectResult>(getResult.Result);
        var plan = Assert.IsType<PlanContent>(ok.Value);
        Assert.Equal("Sprint plan v1", plan.Content);
    }

    [Fact]
    public async Task SetPlan_UpdateExisting()
    {
        var room = await _svc.RoomService.CreateRoomAsync("Test Room");
        await _controller.SetPlan(room.Id, new PlanContent("v1"));
        await _controller.SetPlan(room.Id, new PlanContent("v2"));

        var result = await _controller.GetPlan(room.Id);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var plan = Assert.IsType<PlanContent>(ok.Value);
        Assert.Equal("v2", plan.Content);
    }

    [Fact]
    public async Task DeletePlan_NoPlan_ReturnsNotFound()
    {
        var result = await _controller.DeletePlan("no-plan-room");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeletePlan_ExistingPlan_ReturnsOkAndRemoves()
    {
        var room = await _svc.RoomService.CreateRoomAsync("Test Room");
        await _controller.SetPlan(room.Id, new PlanContent("will be deleted"));

        var deleteResult = await _controller.DeletePlan(room.Id);
        Assert.IsType<OkObjectResult>(deleteResult);

        var getResult = await _controller.GetPlan(room.Id);
        Assert.IsType<NotFoundObjectResult>(getResult.Result);
    }
}
