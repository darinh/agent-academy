using System.Security.Claims;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;
using AgentAcademy.Server.Services.Contracts;

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
            _svc.TaskDependencyService, _svc.MessageService, _svc.RoomService, _svc.Catalog,
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

    // ── Task Dependency Endpoints ──────────────────────────────

    private TaskEntity SeedTaskEntity(string id, string title, string status = "Active")
    {
        var entity = new TaskEntity
        {
            Id = id, Title = title, Status = status,
            RoomId = "main", AssignedAgentId = "engineer-1",
            SuccessCriteria = "Done",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _svc.Db.Tasks.Add(entity);
        _svc.Db.SaveChanges();
        return entity;
    }

    // ── Bulk Status Tests ───────────────────────────────────────

    [Fact]
    public async Task BulkUpdateStatus_AllValid_UpdatesAll()
    {
        SeedTaskEntity("b1", "Task 1", "Active");
        SeedTaskEntity("b2", "Task 2", "Active");
        SeedTaskEntity("b3", "Task 3", "Active");

        var request = new BulkUpdateStatusRequest(
            ["b1", "b2", "b3"], TaskStatus.Blocked);

        var result = await _controller.BulkUpdateStatus(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(3, bulk.Requested);
        Assert.Equal(3, bulk.Succeeded);
        Assert.Equal(0, bulk.Failed);
        Assert.Equal(3, bulk.Updated.Count);
        Assert.Empty(bulk.Errors);
        Assert.All(bulk.Updated, t => Assert.Equal(TaskStatus.Blocked, t.Status));
    }

    [Fact]
    public async Task BulkUpdateStatus_PartialFailure_SomeMissing()
    {
        SeedTaskEntity("exists-1", "Task 1", "Active");

        var request = new BulkUpdateStatusRequest(
            ["exists-1", "does-not-exist"], TaskStatus.Queued);

        var result = await _controller.BulkUpdateStatus(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(2, bulk.Requested);
        Assert.Equal(1, bulk.Succeeded);
        Assert.Equal(1, bulk.Failed);
        Assert.Single(bulk.Updated);
        Assert.Single(bulk.Errors);
        Assert.Equal("NOT_FOUND", bulk.Errors[0].Code);
    }

    [Fact]
    public async Task BulkUpdateStatus_DependencyBlocked_ReportsValidationError()
    {
        SeedTaskEntity("blocker", "Blocker Task", "Queued");
        SeedTaskEntity("blocked", "Blocked Task", "Queued");

        // blocked depends on blocker
        await _controller.AddDependency("blocked", new AddDependencyRequest("blocker"));

        var request = new BulkUpdateStatusRequest(
            ["blocked"], TaskStatus.Active);

        var result = await _controller.BulkUpdateStatus(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(0, bulk.Succeeded);
        Assert.Equal(1, bulk.Failed);
        Assert.Equal("VALIDATION", bulk.Errors[0].Code);
    }

    [Fact]
    public async Task BulkUpdateStatus_UnsafeStatus_ReturnsBadRequest()
    {
        SeedTaskEntity("t1", "Task 1");

        var request = new BulkUpdateStatusRequest(["t1"], TaskStatus.Completed);

        var result = await _controller.BulkUpdateStatus(request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task BulkUpdateStatus_ExceedsMaxSize_ReturnsBadRequest()
    {
        var ids = Enumerable.Range(1, 51).Select(i => $"task-{i}").ToList();
        var request = new BulkUpdateStatusRequest(ids, TaskStatus.Queued);

        var result = await _controller.BulkUpdateStatus(request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task BulkUpdateStatus_DuplicateIds_DedupesAndUpdatesOnce()
    {
        SeedTaskEntity("dup-task", "Duplicate");

        var request = new BulkUpdateStatusRequest(
            ["dup-task", "dup-task", "dup-task"], TaskStatus.Blocked);

        var result = await _controller.BulkUpdateStatus(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(1, bulk.Requested); // deduped
        Assert.Equal(1, bulk.Succeeded);
    }

    [Fact]
    public async Task BulkUpdateStatus_EmptyAndWhitespaceIds_SkippedGracefully()
    {
        SeedTaskEntity("real-task", "Real Task");

        var request = new BulkUpdateStatusRequest(
            ["real-task", "", "  ", "real-task"], TaskStatus.InReview);

        var result = await _controller.BulkUpdateStatus(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(1, bulk.Requested); // deduped + trimmed
        Assert.Equal(1, bulk.Succeeded);
    }

    // ── Bulk Assign Tests ───────────────────────────────────────

    [Fact]
    public async Task BulkAssign_KnownAgent_AssignsAll()
    {
        SeedTaskEntity("a1", "Task 1");
        SeedTaskEntity("a2", "Task 2");

        var request = new BulkAssignRequest(
            ["a1", "a2"], "engineer-1", "Engineer One");

        var result = await _controller.BulkAssign(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(2, bulk.Succeeded);
        Assert.All(bulk.Updated, t => Assert.Equal("engineer-1", t.AssignedAgentId));
    }

    [Fact]
    public async Task BulkAssign_UnknownAgentWithName_Succeeds()
    {
        SeedTaskEntity("a3", "Task 3");

        var request = new BulkAssignRequest(["a3"], "external-1", "External Agent");

        var result = await _controller.BulkAssign(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(1, bulk.Succeeded);
        Assert.Equal("External Agent", bulk.Updated[0].AssignedAgentName);
    }

    [Fact]
    public async Task BulkAssign_UnknownAgentNoName_ReturnsBadRequest()
    {
        SeedTaskEntity("a4", "Task 4");

        var request = new BulkAssignRequest(["a4"], "unknown-agent-id", null);

        var result = await _controller.BulkAssign(request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task BulkAssign_PartialFailure_SomeMissing()
    {
        SeedTaskEntity("real", "Real Task");

        var request = new BulkAssignRequest(
            ["real", "nonexistent"], "engineer-1", "Engineer One");

        var result = await _controller.BulkAssign(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(1, bulk.Succeeded);
        Assert.Equal(1, bulk.Failed);
    }

    [Fact]
    public async Task BulkAssign_ExceedsMaxSize_ReturnsBadRequest()
    {
        var ids = Enumerable.Range(1, 51).Select(i => $"task-{i}").ToList();
        var request = new BulkAssignRequest(ids, "engineer-1", "Engineer One");

        var result = await _controller.BulkAssign(request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddDependency_Valid_Returns201()
    {
        SeedTaskEntity("dep-a", "Task A");
        SeedTaskEntity("dep-b", "Task B");

        var result = await _controller.AddDependency("dep-a", new AddDependencyRequest("dep-b"));

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        var info = Assert.IsType<TaskDependencyInfo>(created.Value);
        Assert.Single(info.DependsOn);
        Assert.Equal("dep-b", info.DependsOn[0].TaskId);
    }

    [Fact]
    public async Task AddDependency_SelfDep_ReturnsBadRequest()
    {
        SeedTaskEntity("dep-a", "Task A");

        var result = await _controller.AddDependency("dep-a", new AddDependencyRequest("dep-a"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddDependency_TaskNotFound_ReturnsBadRequest()
    {
        SeedTaskEntity("dep-b", "Task B");

        var result = await _controller.AddDependency("nonexistent", new AddDependencyRequest("dep-b"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RemoveDependency_Valid_ReturnsOk()
    {
        SeedTaskEntity("dep-a", "Task A");
        SeedTaskEntity("dep-b", "Task B");

        // Add first
        await _controller.AddDependency("dep-a", new AddDependencyRequest("dep-b"));

        // Remove
        var result = await _controller.RemoveDependency("dep-a", "dep-b");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var info = Assert.IsType<TaskDependencyInfo>(ok.Value);
        Assert.Empty(info.DependsOn);
    }

    [Fact]
    public async Task RemoveDependency_NotFound_ReturnsNotFound()
    {
        SeedTaskEntity("dep-a", "Task A");
        SeedTaskEntity("dep-b", "Task B");

        var result = await _controller.RemoveDependency("dep-a", "dep-b");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetDependencies_ExistingTask_ReturnsOk()
    {
        SeedTaskEntity("dep-a", "Task A");
        SeedTaskEntity("dep-b", "Task B");

        await _controller.AddDependency("dep-a", new AddDependencyRequest("dep-b"));

        var result = await _controller.GetDependencies("dep-a");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var info = Assert.IsType<TaskDependencyInfo>(ok.Value);
        Assert.Single(info.DependsOn);
    }

    [Fact]
    public async Task GetDependencies_UnknownTask_ReturnsNotFound()
    {
        var result = await _controller.GetDependencies("nonexistent");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetDependencies_NoDeps_ReturnsEmptyLists()
    {
        SeedTaskEntity("dep-a", "Task A");

        var result = await _controller.GetDependencies("dep-a");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var info = Assert.IsType<TaskDependencyInfo>(ok.Value);
        Assert.Empty(info.DependsOn);
        Assert.Empty(info.DependedOnBy);
    }

    [Fact]
    public async Task GetDependencies_ReturnsBothDirections()
    {
        SeedTaskEntity("dep-a", "Task A");
        SeedTaskEntity("dep-b", "Task B");

        await _controller.AddDependency("dep-a", new AddDependencyRequest("dep-b"));

        // A depends on B → B is depended-on-by A
        var resultB = await _controller.GetDependencies("dep-b");
        var okB = Assert.IsType<OkObjectResult>(resultB.Result);
        var infoB = Assert.IsType<TaskDependencyInfo>(okB.Value);
        Assert.Single(infoB.DependedOnBy);
        Assert.Equal("dep-a", infoB.DependedOnBy[0].TaskId);
    }

    // ── Bulk Status Edge Cases ──────────────────────────────────

    [Theory]
    [InlineData(TaskStatus.Queued)]
    [InlineData(TaskStatus.Active)]
    [InlineData(TaskStatus.InReview)]
    [InlineData(TaskStatus.AwaitingValidation)]
    public async Task BulkUpdateStatus_AllSafeStatuses_Accepted(TaskStatus safeStatus)
    {
        SeedTaskEntity("safe-1", "Safe Task", "Queued");

        var request = new BulkUpdateStatusRequest(["safe-1"], safeStatus);
        var result = await _controller.BulkUpdateStatus(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(1, bulk.Succeeded);
        Assert.Equal(safeStatus, bulk.Updated[0].Status);
    }

    [Theory]
    [InlineData(TaskStatus.Cancelled)]
    [InlineData(TaskStatus.Approved)]
    [InlineData(TaskStatus.Merging)]
    public async Task BulkUpdateStatus_TerminalStatuses_ReturnsBadRequest(TaskStatus terminalStatus)
    {
        SeedTaskEntity("us-1", "Terminal Task", "Active");

        var request = new BulkUpdateStatusRequest(["us-1"], terminalStatus);
        var result = await _controller.BulkUpdateStatus(request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task BulkUpdateStatus_AllWhitespace_ReturnsZeroRequested()
    {
        var request = new BulkUpdateStatusRequest(
            ["", "  ", "\t"], TaskStatus.Queued);

        var result = await _controller.BulkUpdateStatus(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(0, bulk.Requested);
        Assert.Equal(0, bulk.Succeeded);
    }

    [Fact]
    public async Task BulkUpdateStatus_CaseInsensitiveDedup()
    {
        SeedTaskEntity("UPPER", "Upper Case Task", "Active");

        var request = new BulkUpdateStatusRequest(
            ["UPPER", "upper", "Upper"], TaskStatus.Blocked);

        var result = await _controller.BulkUpdateStatus(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(1, bulk.Requested);
    }

    // ── Bulk Assign Edge Cases ──────────────────────────────────

    [Fact]
    public async Task BulkAssign_DuplicateIds_DedupesAndAssignsOnce()
    {
        SeedTaskEntity("dup-assign", "Dup Assign Task");

        var request = new BulkAssignRequest(
            ["dup-assign", "dup-assign", "dup-assign"], "engineer-1", "Engineer One");

        var result = await _controller.BulkAssign(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(1, bulk.Requested);
        Assert.Equal(1, bulk.Succeeded);
    }

    [Fact]
    public async Task BulkAssign_EmptyAndWhitespaceIds_SkippedGracefully()
    {
        SeedTaskEntity("real-assign", "Real Assign Task");

        var request = new BulkAssignRequest(
            ["real-assign", "", "  "], "engineer-1", "Engineer One");

        var result = await _controller.BulkAssign(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(1, bulk.Requested);
        Assert.Equal(1, bulk.Succeeded);
    }

    [Fact]
    public async Task BulkAssign_AllMissing_ReturnsAllErrors()
    {
        var request = new BulkAssignRequest(
            ["ghost-1", "ghost-2"], "engineer-1", "Engineer One");

        var result = await _controller.BulkAssign(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal(0, bulk.Succeeded);
        Assert.Equal(2, bulk.Failed);
        Assert.All(bulk.Errors, e => Assert.Equal("NOT_FOUND", e.Code));
    }

    [Fact]
    public async Task BulkAssign_PreservesAssignedAgentIdAndName()
    {
        SeedTaskEntity("name-check", "Name Check Task");

        var request = new BulkAssignRequest(
            ["name-check"], "engineer-1", "Engineer One");

        var result = await _controller.BulkAssign(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bulk = Assert.IsType<BulkOperationResult>(ok.Value);

        Assert.Equal("engineer-1", bulk.Updated[0].AssignedAgentId);
        Assert.Equal("Engineer One", bulk.Updated[0].AssignedAgentName);
    }
}
