using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class GoalCardControllerTests : IDisposable
{
    private readonly TestServiceGraph _svc;
    private readonly GoalCardController _controller;

    private const string AgentId = "engineer-1";
    private const string AgentName = "Hephaestus";
    private const string RoomId = "room-main";

    public GoalCardControllerTests()
    {
        _svc = new TestServiceGraph();
        _controller = new GoalCardController(
            _svc.GoalCardService,
            NullLogger<GoalCardController>.Instance);

        SeedRoom(RoomId);
    }

    public void Dispose() => _svc.Dispose();

    private void SeedRoom(string roomId)
    {
        var room = new Data.Entities.RoomEntity
        {
            Id = roomId,
            Name = $"Room {roomId}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _svc.Db.Rooms.Add(room);
        _svc.Db.SaveChanges();
    }

    private void SeedTask(string taskId)
    {
        _svc.Db.Tasks.Add(new Data.Entities.TaskEntity
        {
            Id = taskId,
            Title = "Test task",
            Description = "Desc",
            SuccessCriteria = "Criteria",
            Status = "Active",
            RoomId = RoomId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _svc.Db.SaveChanges();
    }

    private CreateGoalCardRequest MakeRequest(
        GoalCardVerdict verdict = GoalCardVerdict.Proceed,
        string? taskId = null) => new(
        TaskDescription: "Add per-user rate limiting to the API (100 requests/hour)",
        Intent: "Protect the API from abuse while ensuring fair usage per user",
        Divergence: "Task and intent are aligned — both point toward per-user rate limiting",
        Steelman: "Rate limiting is essential for production APIs. Per-user ensures fair access.",
        Strawman: "We already have global rate limiting via the reverse proxy. Is per-user worth it?",
        Verdict: verdict,
        FreshEyes1: "Yes, rate limiting makes sense on its own for any production API",
        FreshEyes2: "All parts contribute to the goal of fair API usage",
        FreshEyes3: "No — this is a standard engineering practice, not questionable",
        TaskId: taskId
    );

    private async Task<GoalCard> CreateCard(
        GoalCardVerdict verdict = GoalCardVerdict.Proceed,
        string? taskId = null,
        string roomId = RoomId)
    {
        return await _svc.GoalCardService.CreateAsync(
            AgentId, AgentName, roomId, MakeRequest(verdict, taskId));
    }

    // ── GET /api/goal-cards ──────────────────────────────────

    [Fact]
    public async Task List_Empty_ReturnsEmptyList()
    {
        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Empty(cards);
    }

    [Fact]
    public async Task List_ReturnsAllCards()
    {
        await CreateCard();
        await CreateCard(GoalCardVerdict.Challenge);

        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Equal(2, cards.Count);
    }

    [Fact]
    public async Task List_FilterByRoomId()
    {
        SeedRoom("room-2");
        await CreateCard(roomId: RoomId);
        await CreateCard(roomId: "room-2");

        var result = await _controller.List(roomId: RoomId);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Single(cards);
        Assert.Equal(RoomId, cards[0].RoomId);
    }

    [Fact]
    public async Task List_FilterByStatus()
    {
        var card = await CreateCard();
        await _svc.GoalCardService.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);
        await CreateCard(); // Active

        var result = await _controller.List(status: "Completed");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Single(cards);
        Assert.Equal(GoalCardStatus.Completed, cards[0].Status);
    }

    [Fact]
    public async Task List_FilterByVerdict()
    {
        await CreateCard(GoalCardVerdict.Proceed);
        await CreateCard(GoalCardVerdict.Challenge);

        var result = await _controller.List(verdict: "Challenge");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Single(cards);
        Assert.Equal(GoalCardVerdict.Challenge, cards[0].Verdict);
    }

    [Fact]
    public async Task List_FilterByAgentId()
    {
        await CreateCard();
        await _svc.GoalCardService.CreateAsync("other-agent", "Other", RoomId, MakeRequest());

        var result = await _controller.List(agentId: AgentId);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Single(cards);
        Assert.Equal(AgentId, cards[0].AgentId);
    }

    [Fact]
    public async Task List_FilterByTaskId()
    {
        SeedTask("task-1");
        await CreateCard(taskId: "task-1");
        await CreateCard(); // no task

        var result = await _controller.List(taskId: "task-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Single(cards);
        Assert.Equal("task-1", cards[0].TaskId);
    }

    [Fact]
    public async Task List_InvalidStatusFilter_IgnoredReturnsAll()
    {
        await CreateCard();

        var result = await _controller.List(status: "NotAStatus");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Single(cards);
    }

    [Fact]
    public async Task List_AgentId_With_StatusFilter()
    {
        var card = await CreateCard();
        await _svc.GoalCardService.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);
        await CreateCard(); // Active

        var result = await _controller.List(agentId: AgentId, status: "Active");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsType<List<GoalCard>>(ok.Value);
        Assert.Single(cards);
        Assert.Equal(GoalCardStatus.Active, cards[0].Status);
    }

    // ── GET /api/goal-cards/{id} ─────────────────────────────

    [Fact]
    public async Task Get_ExistingCard_ReturnsOk()
    {
        var created = await CreateCard();

        var result = await _controller.Get(created.Id);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var card = Assert.IsType<GoalCard>(ok.Value);
        Assert.Equal(created.Id, card.Id);
    }

    [Fact]
    public async Task Get_MissingCard_Returns404()
    {
        var result = await _controller.Get("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── PATCH /api/goal-cards/{id}/status ────────────────────

    [Fact]
    public async Task UpdateStatus_ValidTransition_ReturnsUpdatedCard()
    {
        var created = await CreateCard();
        var request = new UpdateGoalCardStatusRequest(GoalCardStatus.Completed);

        var result = await _controller.UpdateStatus(created.Id, request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var card = Assert.IsType<GoalCard>(ok.Value);
        Assert.Equal(GoalCardStatus.Completed, card.Status);
    }

    [Fact]
    public async Task UpdateStatus_MissingCard_Returns404()
    {
        var request = new UpdateGoalCardStatusRequest(GoalCardStatus.Completed);

        var result = await _controller.UpdateStatus("nonexistent", request);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateStatus_IllegalTransition_Returns400()
    {
        var created = await CreateCard();
        await _svc.GoalCardService.UpdateStatusAsync(created.Id, GoalCardStatus.Completed);
        var request = new UpdateGoalCardStatusRequest(GoalCardStatus.Active);

        var result = await _controller.UpdateStatus(created.Id, request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateStatus_ActiveToAbandoned()
    {
        var created = await CreateCard();
        var request = new UpdateGoalCardStatusRequest(GoalCardStatus.Abandoned);

        var result = await _controller.UpdateStatus(created.Id, request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var card = Assert.IsType<GoalCard>(ok.Value);
        Assert.Equal(GoalCardStatus.Abandoned, card.Status);
    }

    [Fact]
    public async Task UpdateStatus_ChallengedToActive()
    {
        var created = await CreateCard(GoalCardVerdict.Challenge);
        Assert.Equal(GoalCardStatus.Challenged, created.Status);

        var request = new UpdateGoalCardStatusRequest(GoalCardStatus.Active);
        var result = await _controller.UpdateStatus(created.Id, request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var card = Assert.IsType<GoalCard>(ok.Value);
        Assert.Equal(GoalCardStatus.Active, card.Status);
    }

    // ── PATCH /api/goal-cards/{id}/task ──────────────────────

    [Fact]
    public async Task AttachToTask_ValidLink_ReturnsOk()
    {
        SeedTask("task-attach");
        var created = await CreateCard();
        var request = new AttachGoalCardToTaskRequest("task-attach");

        var result = await _controller.AttachToTask(created.Id, request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var card = Assert.IsType<GoalCard>(ok.Value);
        Assert.Equal("task-attach", card.TaskId);
    }

    [Fact]
    public async Task AttachToTask_MissingCard_Returns404()
    {
        var request = new AttachGoalCardToTaskRequest("task-1");

        var result = await _controller.AttachToTask("nonexistent", request);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task AttachToTask_AlreadyLinked_Returns400()
    {
        SeedTask("task-1");
        SeedTask("task-2");
        var created = await CreateCard(taskId: "task-1");
        var request = new AttachGoalCardToTaskRequest("task-2");

        var result = await _controller.AttachToTask(created.Id, request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AttachToTask_TaskNotFound_Returns404()
    {
        var created = await CreateCard();
        var request = new AttachGoalCardToTaskRequest("nonexistent-task");

        var result = await _controller.AttachToTask(created.Id, request);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }
}
