using System.Security.Claims;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class DmControllerTests : IDisposable
{
    private readonly TestServiceGraph _svc;
    private readonly DmController _controller;

    private static readonly AgentDefinition TestAgent = new(
        Id: "test-agent",
        Name: "Test Agent",
        Role: "Engineer",
        Summary: "A helpful test agent",
        StartupPrompt: "You are a test agent.",
        Model: null,
        CapabilityTags: [],
        EnabledTools: [],
        AutoJoinDefaultRoom: true,
        Permissions: null);

    public DmControllerTests()
    {
        _svc = new TestServiceGraph([TestAgent]);

        _controller = new DmController(
            _svc.MessageService, _svc.RoomService, _svc.Catalog,
            _svc.Orchestrator, NullLogger<DmController>.Instance);

        SetUser(isConsultant: false);

        // Seed a default room so SendMessage can resolve context
        _svc.Db.Rooms.Add(new RoomEntity
        {
            Id = "main", Name = "Main Room", Status = "Active",
            CurrentPhase = "Intake", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/tmp/test"
        });
        _svc.Db.SaveChanges();
    }

    public void Dispose() => _svc.Dispose();

    private void SetUser(bool isConsultant)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, isConsultant ? "consultant" : "human"),
        };
        if (isConsultant)
            claims.Add(new Claim(ClaimTypes.Role, "Consultant"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"))
            }
        };
    }

    // ── GetThreads ───────────────────────────────────────────────

    [Fact]
    public async Task GetThreads_Empty_ReturnsEmptyList()
    {
        var result = await _controller.GetThreads();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var threads = Assert.IsType<List<DmThreadSummary>>(ok.Value);
        Assert.Empty(threads);
    }

    // ── GetThreadMessages ────────────────────────────────────────

    [Fact]
    public async Task GetThreadMessages_UnknownAgent_ReturnsNotFound()
    {
        var result = await _controller.GetThreadMessages("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetThreadMessages_ValidAgent_ReturnsEmptyForNoMessages()
    {
        var result = await _controller.GetThreadMessages("test-agent");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var messages = Assert.IsType<List<DmMessage>>(ok.Value);
        Assert.Empty(messages);
    }

    // ── SendMessage ──────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_NullBody_ReturnsBadRequest()
    {
        var result = await _controller.SendMessage("test-agent", null!);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_EmptyMessage_ReturnsBadRequest()
    {
        var result = await _controller.SendMessage("test-agent",
            new SendDmRequest(""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_UnknownAgent_ReturnsNotFound()
    {
        var result = await _controller.SendMessage("nonexistent",
            new SendDmRequest("Hello"));
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_AsHuman_Returns201WithCorrectSender()
    {
        SetUser(isConsultant: false);

        var result = await _controller.SendMessage("test-agent",
            new SendDmRequest("Hello agent"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);

        var msg = Assert.IsType<DmMessage>(obj.Value);
        Assert.Equal("human", msg.SenderId);
        Assert.Equal("Human", msg.SenderName);
        Assert.Equal("Hello agent", msg.Content);
        Assert.True(msg.IsFromHuman);
    }

    [Fact]
    public async Task SendMessage_AsConsultant_UsesConsultantIdentity()
    {
        SetUser(isConsultant: true);

        var result = await _controller.SendMessage("test-agent",
            new SendDmRequest("Consultant message"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);

        var msg = Assert.IsType<DmMessage>(obj.Value);
        Assert.Equal("consultant", msg.SenderId);
        Assert.Equal("Consultant", msg.SenderName);
    }

    [Fact]
    public async Task SendMessage_AgentIdIsCaseInsensitive()
    {
        var result = await _controller.SendMessage("TEST-AGENT",
            new SendDmRequest("Case test"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);
    }

    [Fact]
    public async Task SendMessage_MessageAppearsInThread()
    {
        await _controller.SendMessage("test-agent",
            new SendDmRequest("Thread message"));

        var threadResult = await _controller.GetThreadMessages("test-agent");
        var ok = Assert.IsType<OkObjectResult>(threadResult.Result);
        var messages = Assert.IsType<List<DmMessage>>(ok.Value);
        Assert.Single(messages);
        Assert.Equal("Thread message", messages[0].Content);
    }
}
