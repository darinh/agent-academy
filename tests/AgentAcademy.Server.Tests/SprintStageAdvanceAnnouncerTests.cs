using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="SprintStageAdvanceAnnouncer"/> — verifies that a stage
/// transition produces an observable system message in every active room and
/// wakes the orchestrator. Closes P1.3 of <c>specs/100-product-vision</c>.
/// </summary>
public sealed class SprintStageAdvanceAnnouncerTests : IDisposable
{
    private const string Workspace = "/tmp/announce-test";

    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly IMessageService _messageService;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly SprintStageAdvanceAnnouncer _service;

    public SprintStageAdvanceAnnouncerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _messageService = Substitute.For<IMessageService>();
        _orchestrator = Substitute.For<IAgentOrchestrator>();
        _service = new SprintStageAdvanceAnnouncer(
            _db, _messageService, _orchestrator, NullLogger<SprintStageAdvanceAnnouncer>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private SprintEntity NewSprint(string currentStage = "Planning", int number = 1) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Number = number,
        WorkspacePath = Workspace,
        Status = "Active",
        CurrentStage = currentStage,
        CreatedAt = DateTime.UtcNow,
    };

    private RoomEntity AddRoom(string name, string status = "Active", string? workspace = null)
    {
        var room = new RoomEntity
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            WorkspacePath = workspace ?? Workspace,
            Status = status,
            CurrentPhase = "Planning",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Rooms.Add(room);
        _db.SaveChanges();
        return room;
    }

    [Fact]
    public async Task Announce_PostsToEveryActiveRoomInWorkspace()
    {
        var roomA = AddRoom("alpha");
        var roomB = AddRoom("beta");
        var sprint = NewSprint("Planning");

        var posted = await _service.AnnounceAsync(sprint, previousStage: "Intake");

        Assert.Equal(2, posted);
        await _messageService.Received(1).PostSystemMessageAsync(roomA.Id, Arg.Any<string>());
        await _messageService.Received(1).PostSystemMessageAsync(roomB.Id, Arg.Any<string>());
    }

    [Fact]
    public async Task Announce_WakesOrchestratorOnceForEachRoom()
    {
        var roomA = AddRoom("alpha");
        var roomB = AddRoom("beta");
        var sprint = NewSprint("Planning");

        await _service.AnnounceAsync(sprint, previousStage: "Intake");

        _orchestrator.Received(1).HandleHumanMessage(roomA.Id);
        _orchestrator.Received(1).HandleHumanMessage(roomB.Id);
    }

    [Fact]
    public async Task Announce_ExcludesArchivedAndCompletedRooms()
    {
        var active = AddRoom("alpha");
        var archived = AddRoom("beta", status: nameof(RoomStatus.Archived));
        var completed = AddRoom("gamma", status: nameof(RoomStatus.Completed));
        var sprint = NewSprint("Planning");

        var posted = await _service.AnnounceAsync(sprint, previousStage: "Intake");

        Assert.Equal(1, posted);
        await _messageService.Received(1).PostSystemMessageAsync(active.Id, Arg.Any<string>());
        await _messageService.DidNotReceive().PostSystemMessageAsync(archived.Id, Arg.Any<string>());
        await _messageService.DidNotReceive().PostSystemMessageAsync(completed.Id, Arg.Any<string>());
    }

    [Fact]
    public async Task Announce_ExcludesRoomsInOtherWorkspaces()
    {
        var inWorkspace = AddRoom("alpha");
        var elsewhere = AddRoom("beta", workspace: "/tmp/other-workspace");
        var sprint = NewSprint("Planning");

        var posted = await _service.AnnounceAsync(sprint, previousStage: "Intake");

        Assert.Equal(1, posted);
        await _messageService.Received(1).PostSystemMessageAsync(inWorkspace.Id, Arg.Any<string>());
        await _messageService.DidNotReceive().PostSystemMessageAsync(elsewhere.Id, Arg.Any<string>());
    }

    [Fact]
    public async Task Announce_NoActiveRooms_ReturnsZeroAndPostsNothing()
    {
        var sprint = NewSprint("Planning");

        var posted = await _service.AnnounceAsync(sprint, previousStage: "Intake");

        Assert.Equal(0, posted);
        await _messageService.DidNotReceive().PostSystemMessageAsync(Arg.Any<string>(), Arg.Any<string>());
        _orchestrator.DidNotReceive().HandleHumanMessage(Arg.Any<string>());
    }

    [Fact]
    public async Task Announce_PostFails_ContinuesToOtherRoomsAndDoesNotWakeForFailedRoom()
    {
        var roomA = AddRoom("alpha");
        var roomB = AddRoom("beta");
        _messageService
            .PostSystemMessageAsync(roomA.Id, Arg.Any<string>())
            .Throws(new InvalidOperationException("boom"));

        var sprint = NewSprint("Planning");

        var posted = await _service.AnnounceAsync(sprint, previousStage: "Intake");

        Assert.Equal(1, posted);
        // Successful room got the wake; failed room did NOT.
        _orchestrator.Received(1).HandleHumanMessage(roomB.Id);
        _orchestrator.DidNotReceive().HandleHumanMessage(roomA.Id);
    }

    [Fact]
    public async Task Announce_OrchestratorWakeFails_StillCountsThePostAsSuccessful()
    {
        var roomA = AddRoom("alpha");
        _orchestrator
            .When(o => o.HandleHumanMessage(roomA.Id))
            .Do(_ => throw new InvalidOperationException("orchestrator down"));

        var sprint = NewSprint("Planning");

        var posted = await _service.AnnounceAsync(sprint, previousStage: "Intake");

        Assert.Equal(1, posted);
        await _messageService.Received(1).PostSystemMessageAsync(roomA.Id, Arg.Any<string>());
    }

    [Fact]
    public async Task Announce_MessageContainsBothStagesAndSprintNumber()
    {
        var room = AddRoom("alpha");
        var sprint = NewSprint("Implementation", number: 7);
        string? captured = null;
        await _messageService.PostSystemMessageAsync(
            Arg.Any<string>(),
            Arg.Do<string>(c => captured = c));

        await _service.AnnounceAsync(sprint, previousStage: "Validation");

        Assert.NotNull(captured);
        Assert.Contains("Sprint #7", captured);
        Assert.Contains("Validation", captured);
        Assert.Contains("Implementation", captured);
    }

    [Fact]
    public async Task Announce_TriggerLabelAppearsInMessage()
    {
        AddRoom("alpha");
        var sprint = NewSprint("Discussion");
        string? captured = null;
        await _messageService.PostSystemMessageAsync(
            Arg.Any<string>(),
            Arg.Do<string>(c => captured = c));

        await _service.AnnounceAsync(sprint, previousStage: "Planning", trigger: "approved");

        Assert.NotNull(captured);
        Assert.Contains("user-approved", captured);
    }

    [Fact]
    public async Task Announce_ExplicitTargetRoomIds_OverridesWorkspaceQueryAndIncludesEvenCompletedRooms()
    {
        // Regression for the FinalSynthesis bug: when SprintStageService snapshots
        // active rooms BEFORE the stage sync flips them to Completed, it must be
        // able to pass them in here and have them all receive the announcement.
        var roomA = AddRoom("alpha");
        var roomCompleted = AddRoom("beta-just-completed", status: nameof(RoomStatus.Completed));
        var sprint = NewSprint("FinalSynthesis");

        var posted = await _service.AnnounceAsync(
            sprint,
            previousStage: "Implementation",
            targetRoomIds: new[] { roomA.Id, roomCompleted.Id });

        Assert.Equal(2, posted);
        await _messageService.Received(1).PostSystemMessageAsync(roomA.Id, Arg.Any<string>());
        await _messageService.Received(1).PostSystemMessageAsync(roomCompleted.Id, Arg.Any<string>());
        _orchestrator.Received(1).HandleHumanMessage(roomA.Id);
        _orchestrator.Received(1).HandleHumanMessage(roomCompleted.Id);
    }

    [Fact]
    public async Task Announce_ExplicitEmptyTargetRoomIds_ReturnsZeroEvenIfWorkspaceHasRooms()
    {
        AddRoom("alpha");
        var sprint = NewSprint("Planning");

        var posted = await _service.AnnounceAsync(
            sprint, previousStage: "Intake", targetRoomIds: Array.Empty<string>());

        Assert.Equal(0, posted);
        await _messageService.DidNotReceive().PostSystemMessageAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Announce_NullSprint_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.AnnounceAsync(null!, previousStage: "Intake"));
    }

    [Fact]
    public async Task Announce_EmptyPreviousStage_Throws()
    {
        var sprint = NewSprint("Planning");
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.AnnounceAsync(sprint, previousStage: ""));
    }

    [Theory]
    [InlineData("Planning", "SprintPlan")]
    [InlineData("Validation", "ValidationReport")]
    [InlineData("Implementation", "build the plan")]
    [InlineData("FinalSynthesis", "SprintReport")]
    public async Task Announce_StageIntent_ReflectsTargetStage(string newStage, string expectedFragment)
    {
        AddRoom("alpha");
        var sprint = NewSprint(newStage);
        string? captured = null;
        await _messageService.PostSystemMessageAsync(
            Arg.Any<string>(),
            Arg.Do<string>(c => captured = c));

        await _service.AnnounceAsync(sprint, previousStage: "Intake");

        Assert.NotNull(captured);
        Assert.Contains(expectedFragment, captured!);
    }
}
