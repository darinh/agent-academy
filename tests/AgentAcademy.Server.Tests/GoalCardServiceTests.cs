using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class GoalCardServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _bus;
    private readonly ActivityPublisher _activity;
    private readonly GoalCardService _sut;

    private const string AgentId = "engineer-1";
    private const string AgentName = "Hephaestus";
    private const string RoomId = "room-1";
    private const string TaskId = "task-001";

    public GoalCardServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _bus = new ActivityBroadcaster();
        _activity = new ActivityPublisher(_db, _bus);
        _sut = new GoalCardService(
            _db,
            _activity,
            NullLogger<GoalCardService>.Instance);

        SeedRoom();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void SeedRoom(string roomId = RoomId)
    {
        if (_db.Rooms.Find(roomId) is not null) return;
        _db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = "Test Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private TaskEntity SeedTask(string id = TaskId)
    {
        var task = new TaskEntity
        {
            Id = id,
            Title = "Test task",
            Description = "Desc",
            SuccessCriteria = "Criteria",
            Status = "Active",
            RoomId = RoomId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    private CreateGoalCardRequest MakeRequest(
        GoalCardVerdict verdict = GoalCardVerdict.Proceed,
        string? taskId = null) => new(
        TaskDescription: "Add per-user rate limiting to the API (100 requests/hour per authenticated user)",
        Intent: "Protect the API from abuse while ensuring fair usage per user",
        Divergence: "Task and intent are aligned — both point toward per-user rate limiting",
        Steelman: "Rate limiting is essential for production APIs. Per-user ensures fair access. 100 req/hr is a reasonable default that can be configured later.",
        Strawman: "We already have global rate limiting via the reverse proxy. Adding per-user adds complexity to the auth layer. Is the real problem abuse from a specific user?",
        Verdict: verdict,
        FreshEyes1: "Yes, rate limiting makes sense on its own for any production API",
        FreshEyes2: "All parts contribute to the goal of fair API usage",
        FreshEyes3: "No — this is a standard engineering practice, not a questionable decision",
        TaskId: taskId
    );

    // ── CreateAsync ──────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Stores_GoalCard_With_Active_Status()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());

        Assert.NotNull(card);
        Assert.Equal(AgentId, card.AgentId);
        Assert.Equal(AgentName, card.AgentName);
        Assert.Equal(RoomId, card.RoomId);
        Assert.Equal(GoalCardVerdict.Proceed, card.Verdict);
        Assert.Equal(GoalCardStatus.Active, card.Status);
        Assert.Equal(1, card.PromptVersion);
    }

    [Fact]
    public async Task CreateAsync_Challenge_Verdict_Sets_Challenged_Status()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId,
            MakeRequest(GoalCardVerdict.Challenge));

        Assert.Equal(GoalCardVerdict.Challenge, card.Verdict);
        Assert.Equal(GoalCardStatus.Challenged, card.Status);
    }

    [Fact]
    public async Task CreateAsync_ProceedWithCaveat_Sets_Active_Status()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId,
            MakeRequest(GoalCardVerdict.ProceedWithCaveat));

        Assert.Equal(GoalCardVerdict.ProceedWithCaveat, card.Verdict);
        Assert.Equal(GoalCardStatus.Active, card.Status);
    }

    [Fact]
    public async Task CreateAsync_With_TaskId_Links_To_Task()
    {
        SeedTask();
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId,
            MakeRequest(taskId: TaskId));

        Assert.Equal(TaskId, card.TaskId);
    }

    [Fact]
    public async Task CreateAsync_Without_TaskId_Leaves_TaskId_Null()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());

        Assert.Null(card.TaskId);
    }

    [Fact]
    public async Task CreateAsync_Persists_All_Content_Fields()
    {
        var request = MakeRequest();
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, request);

        Assert.Equal(request.TaskDescription, card.TaskDescription);
        Assert.Equal(request.Intent, card.Intent);
        Assert.Equal(request.Divergence, card.Divergence);
        Assert.Equal(request.Steelman, card.Steelman);
        Assert.Equal(request.Strawman, card.Strawman);
        Assert.Equal(request.FreshEyes1, card.FreshEyes1);
        Assert.Equal(request.FreshEyes2, card.FreshEyes2);
        Assert.Equal(request.FreshEyes3, card.FreshEyes3);
    }

    [Fact]
    public async Task CreateAsync_Publishes_GoalCardCreated_Activity()
    {
        ActivityEvent? captured = null;
        _bus.Subscribe(e => captured = e);

        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());

        Assert.NotNull(captured);
        Assert.Equal(ActivityEventType.GoalCardCreated, captured.Type);
        Assert.Equal(RoomId, captured.RoomId);
        Assert.Equal(AgentId, captured.ActorId);
    }

    [Fact]
    public async Task CreateAsync_Challenge_Publishes_GoalCardChallenged_Activity()
    {
        ActivityEvent? captured = null;
        _bus.Subscribe(e => captured = e);

        await _sut.CreateAsync(AgentId, AgentName, RoomId,
            MakeRequest(GoalCardVerdict.Challenge));

        Assert.NotNull(captured);
        Assert.Equal(ActivityEventType.GoalCardChallenged, captured.Type);
    }

    [Fact]
    public async Task CreateAsync_Generates_12Char_Id()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());

        Assert.Equal(12, card.Id.Length);
    }

    // ── GetByIdAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_Returns_Card()
    {
        var created = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        var fetched = await _sut.GetByIdAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_For_Missing()
    {
        var fetched = await _sut.GetByIdAsync("nonexistent");
        Assert.Null(fetched);
    }

    // ── GetActiveAsync ───────────────────────────────────────

    [Fact]
    public async Task GetActiveAsync_Returns_Active_And_Challenged_Cards()
    {
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.Proceed));
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.Challenge));

        var active = await _sut.GetActiveAsync();

        Assert.Equal(2, active.Count);
    }

    [Fact]
    public async Task GetActiveAsync_Excludes_Completed_Cards()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);

        var active = await _sut.GetActiveAsync();

        Assert.Empty(active);
    }

    [Fact]
    public async Task GetActiveAsync_Filters_By_RoomId()
    {
        SeedRoom("room-2");
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await _sut.CreateAsync(AgentId, AgentName, "room-2", MakeRequest());

        var room1Cards = await _sut.GetActiveAsync(roomId: RoomId);
        var room2Cards = await _sut.GetActiveAsync(roomId: "room-2");

        Assert.Single(room1Cards);
        Assert.Single(room2Cards);
    }

    [Fact]
    public async Task GetActiveAsync_Returns_Newest_First()
    {
        var first = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await Task.Delay(10); // Ensure different timestamps
        var second = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());

        var active = await _sut.GetActiveAsync();

        Assert.Equal(second.Id, active[0].Id);
        Assert.Equal(first.Id, active[1].Id);
    }

    // ── GetByAgentAsync ──────────────────────────────────────

    [Fact]
    public async Task GetByAgentAsync_Returns_Only_That_Agents_Cards()
    {
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await _sut.CreateAsync("other-agent", "Other", RoomId, MakeRequest());

        var cards = await _sut.GetByAgentAsync(AgentId);

        Assert.Single(cards);
        Assert.Equal(AgentId, cards[0].AgentId);
    }

    // ── GetByTaskAsync ───────────────────────────────────────

    [Fact]
    public async Task GetByTaskAsync_Returns_Cards_Linked_To_Task()
    {
        SeedTask();
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(taskId: TaskId));
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest()); // no task

        var cards = await _sut.GetByTaskAsync(TaskId);

        Assert.Single(cards);
        Assert.Equal(TaskId, cards[0].TaskId);
    }

    // ── AttachToTaskAsync ────────────────────────────────────

    [Fact]
    public async Task AttachToTaskAsync_Links_Card_To_Task()
    {
        SeedTask();
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());

        var updated = await _sut.AttachToTaskAsync(card.Id, TaskId);

        Assert.NotNull(updated);
        Assert.Equal(TaskId, updated.TaskId);
    }

    [Fact]
    public async Task AttachToTaskAsync_Returns_Null_For_Missing_Card()
    {
        var result = await _sut.AttachToTaskAsync("nonexistent", TaskId);
        Assert.Null(result);
    }

    [Fact]
    public async Task AttachToTaskAsync_Throws_If_Already_Linked()
    {
        SeedTask();
        SeedTask("task-002");
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(taskId: TaskId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AttachToTaskAsync(card.Id, "task-002"));
    }

    [Fact]
    public async Task AttachToTaskAsync_Throws_If_Task_Not_Found()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.AttachToTaskAsync(card.Id, "nonexistent-task"));
    }

    [Fact]
    public async Task CreateAsync_Throws_If_TaskId_Not_Found()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(taskId: "bad-task")));
    }

    // ── QueryAsync ───────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_Returns_All_Statuses()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest()); // Active

        var all = await _sut.QueryAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task QueryAsync_Filters_By_Status()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest()); // Active

        var completed = await _sut.QueryAsync(status: GoalCardStatus.Completed);
        Assert.Single(completed);
        Assert.Equal(GoalCardStatus.Completed, completed[0].Status);
    }

    [Fact]
    public async Task QueryAsync_Filters_By_Verdict()
    {
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.Proceed));
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.Challenge));

        var challenges = await _sut.QueryAsync(verdict: GoalCardVerdict.Challenge);
        Assert.Single(challenges);
        Assert.Equal(GoalCardVerdict.Challenge, challenges[0].Verdict);
    }

    [Fact]
    public async Task QueryAsync_Filters_By_RoomId()
    {
        SeedRoom("room-2");
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await _sut.CreateAsync(AgentId, AgentName, "room-2", MakeRequest());

        var room1 = await _sut.QueryAsync(roomId: RoomId);
        Assert.Single(room1);
    }

    // ── UpdateStatusAsync ────────────────────────────────────

    [Fact]
    public async Task UpdateStatusAsync_Active_To_Completed()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        var updated = await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);

        Assert.NotNull(updated);
        Assert.Equal(GoalCardStatus.Completed, updated.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_Active_To_Challenged()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        var updated = await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Challenged);

        Assert.NotNull(updated);
        Assert.Equal(GoalCardStatus.Challenged, updated.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_Active_To_Abandoned()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        var updated = await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Abandoned);

        Assert.NotNull(updated);
        Assert.Equal(GoalCardStatus.Abandoned, updated.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_Challenged_To_Active_Resumes()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId,
            MakeRequest(GoalCardVerdict.Challenge));
        Assert.Equal(GoalCardStatus.Challenged, card.Status);

        var updated = await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Active);

        Assert.NotNull(updated);
        Assert.Equal(GoalCardStatus.Active, updated.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_Completed_Is_Terminal()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Active));
    }

    [Fact]
    public async Task UpdateStatusAsync_Abandoned_Is_Terminal()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Abandoned);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Active));
    }

    [Fact]
    public async Task UpdateStatusAsync_Returns_Null_For_Missing()
    {
        var result = await _sut.UpdateStatusAsync("nonexistent", GoalCardStatus.Completed);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateStatusAsync_To_Challenged_Publishes_Activity()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());

        ActivityEvent? captured = null;
        _bus.Subscribe(e => captured = e);

        await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Challenged);

        Assert.NotNull(captured);
        Assert.Equal(ActivityEventType.GoalCardChallenged, captured.Type);
    }

    [Fact]
    public async Task UpdateStatusAsync_Updates_Timestamp()
    {
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest());
        var originalUpdatedAt = card.UpdatedAt;

        await Task.Delay(10);
        var updated = await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);

        Assert.True(updated!.UpdatedAt > originalUpdatedAt);
    }

    // ── Content Immutability ─────────────────────────────────

    [Fact]
    public async Task Content_Is_Preserved_After_Status_Change()
    {
        var request = MakeRequest();
        var card = await _sut.CreateAsync(AgentId, AgentName, RoomId, request);
        await _sut.UpdateStatusAsync(card.Id, GoalCardStatus.Completed);

        var fetched = await _sut.GetByIdAsync(card.Id);

        Assert.NotNull(fetched);
        Assert.Equal(request.TaskDescription, fetched.TaskDescription);
        Assert.Equal(request.Intent, fetched.Intent);
        Assert.Equal(request.Steelman, fetched.Steelman);
        Assert.Equal(request.Strawman, fetched.Strawman);
    }

    // ── GetSummaryAsync ──────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_Empty_Returns_Zeros()
    {
        var summary = await _sut.GetSummaryAsync();

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, summary.Active);
        Assert.Equal(0, summary.Challenged);
        Assert.Equal(0, summary.Completed);
        Assert.Equal(0, summary.Abandoned);
        Assert.Equal(0, summary.VerdictProceed);
        Assert.Equal(0, summary.VerdictProceedWithCaveat);
        Assert.Equal(0, summary.VerdictChallenge);
    }

    [Fact]
    public async Task GetSummaryAsync_Counts_By_Status()
    {
        var c1 = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest()); // Active
        var c2 = await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest()); // Active → Completed
        await _sut.UpdateStatusAsync(c2.Id, GoalCardStatus.Completed);
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.Challenge)); // Challenged

        var summary = await _sut.GetSummaryAsync();

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Active);
        Assert.Equal(1, summary.Challenged);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(0, summary.Abandoned);
    }

    [Fact]
    public async Task GetSummaryAsync_Counts_By_Verdict()
    {
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.Proceed));
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.Proceed));
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.ProceedWithCaveat));
        await _sut.CreateAsync(AgentId, AgentName, RoomId, MakeRequest(GoalCardVerdict.Challenge));

        var summary = await _sut.GetSummaryAsync();

        Assert.Equal(4, summary.Total);
        Assert.Equal(2, summary.VerdictProceed);
        Assert.Equal(1, summary.VerdictProceedWithCaveat);
        Assert.Equal(1, summary.VerdictChallenge);
    }
}
