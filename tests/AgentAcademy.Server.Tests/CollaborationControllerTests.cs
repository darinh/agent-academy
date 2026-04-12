using System.Security.Claims;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class CollaborationControllerTests : IDisposable
{
    private readonly TestServiceGraph _svc;
    private readonly CollaborationController _controller;

    public CollaborationControllerTests()
    {
        _svc = new TestServiceGraph();
        _controller = new CollaborationController(
            _svc.TaskOrchestrationService, _svc.TaskQueryService,
            _svc.MessageService, _svc.RoomService, _svc.Catalog,
            _svc.Orchestrator, _svc.Executor, _svc.SpecManager,
            NullLogger<CollaborationController>.Instance);

        // Set a default anonymous HttpContext so User doesn't NullRef
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Seed a default room
        _svc.Db.Rooms.Add(new RoomEntity
        {
            Id = "main", Name = "Main Room", Status = "Active",
            CurrentPhase = "Intake", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/tmp/test"
        });
        _svc.Db.SaveChanges();
    }

    public void Dispose() => _svc.Dispose();

    private void SetAuthenticatedUser()
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "test-user"),
                    new Claim("urn:github:name", "Test User"),
                ], "Cookies"))
            }
        };
    }

    // ── SubmitTask ────────────────────────────────────────────────

    [Fact]
    public async Task SubmitTask_NullRequest_ReturnsBadRequest()
    {
        var result = await _controller.SubmitTask(null!);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SubmitTask_Valid_Returns201WithResult()
    {
        var request = new TaskAssignmentRequest(
            Title: "Test task",
            Description: "Do the thing",
            SuccessCriteria: "Thing is done",
            RoomId: "main",
            PreferredRoles: ["Engineer"]);

        var result = await _controller.SubmitTask(request);
        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);
        var taskResult = Assert.IsType<TaskAssignmentResult>(obj.Value);
        Assert.Equal("Test task", taskResult.Task.Title);
    }

    // ── ListTasks ─────────────────────────────────────────────────

    [Fact]
    public async Task ListTasks_Empty_ReturnsEmptyList()
    {
        var result = await _controller.ListTasks();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var tasks = Assert.IsType<List<TaskSnapshot>>(ok.Value);
        Assert.Empty(tasks);
    }

    [Fact]
    public async Task ListTasks_AfterSubmit_ReturnsTask()
    {
        var request = new TaskAssignmentRequest(
            Title: "Listed task",
            Description: "Desc",
            SuccessCriteria: "Done",
            RoomId: "main",
            PreferredRoles: []);

        await _controller.SubmitTask(request);

        var result = await _controller.ListTasks();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var tasks = Assert.IsType<List<TaskSnapshot>>(ok.Value);
        Assert.Single(tasks);
    }

    // ── GetTask ───────────────────────────────────────────────────

    [Fact]
    public async Task GetTask_NotFound_Returns404()
    {
        var result = await _controller.GetTask("nonexistent");
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetTask_Found_ReturnsSnapshot()
    {
        var request = new TaskAssignmentRequest(
            Title: "Find me",
            Description: "Desc",
            SuccessCriteria: "Done",
            RoomId: "main",
            PreferredRoles: []);

        var submitResult = await _controller.SubmitTask(request);
        var taskResult = (TaskAssignmentResult)((ObjectResult)submitResult.Result!).Value!;

        var result = await _controller.GetTask(taskResult.Task.Id);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var task = Assert.IsType<TaskSnapshot>(ok.Value);
        Assert.Equal("Find me", task.Title);
    }

    // ── GetSpecTaskLinks ─────────────────────────────────────────

    [Fact]
    public async Task GetSpecTaskLinks_ReturnsEmptyForUnknownSection()
    {
        var result = await _controller.GetSpecTaskLinks("unknown-section");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var links = Assert.IsType<List<SpecTaskLink>>(ok.Value);
        Assert.Empty(links);
    }

    // ── PostHumanMessage ─────────────────────────────────────────

    [Fact]
    public async Task PostHumanMessage_EmptyContent_ReturnsBadRequest()
    {
        // MessageService throws ArgumentException for empty content,
        // which the controller catches and returns BadRequest
        var result = await _controller.PostHumanMessage("main", new HumanMessageRequest(""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task PostHumanMessage_Valid_ReturnsEnvelope()
    {
        SetAuthenticatedUser();
        var result = await _controller.PostHumanMessage("main", new HumanMessageRequest("Hello agents!"));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ChatEnvelope>(ok.Value);
        Assert.Equal("Hello agents!", envelope.Content);
    }

    [Fact]
    public async Task PostHumanMessage_UnknownRoom_ReturnsNotFound()
    {
        // MessageService throws InvalidOperationException for unknown room
        var result = await _controller.PostHumanMessage("nonexistent", new HumanMessageRequest("Hello"));
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── TransitionPhase ──────────────────────────────────────────

    [Fact]
    public async Task TransitionPhase_NullRequest_ReturnsBadRequest()
    {
        var result = await _controller.TransitionPhase("main", null!);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task TransitionPhase_UnknownRoom_ReturnsNotFound()
    {
        var result = await _controller.TransitionPhase("nonexistent",
            new PhaseTransitionRequest("nonexistent", CollaborationPhase.Planning, "test"));
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── CompactRoom ──────────────────────────────────────────────

    [Fact]
    public async Task CompactRoom_ReturnsOk()
    {
        _svc.Executor.IsFullyOperational.Returns(false);
        var result = await _controller.CompactRoom("main");
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task CompactRoom_WhenOperational_CallsExecutor()
    {
        _svc.Executor.IsFullyOperational.Returns(true);
        var result = await _controller.CompactRoom("main");
        Assert.IsType<OkObjectResult>(result);
        await _svc.Executor.Received(1).InvalidateRoomSessionsAsync("main");
    }
}
