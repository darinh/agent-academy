using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="SprintKickoffService"/> — verifies that sprint creation
/// produces an observable kickoff message in every active room of the workspace
/// and wakes the orchestrator. Closes G2 of <c>specs/100-product-vision</c>.
/// </summary>
public sealed class SprintKickoffServiceTests : IDisposable
{
    private const string Workspace = "/tmp/kickoff-test";

    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly IMessageService _messageService;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly SprintKickoffService _service;

    public SprintKickoffServiceTests()
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
        _service = new SprintKickoffService(
            _db, _messageService, _orchestrator, NullLogger<SprintKickoffService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private SprintEntity NewSprint(int number = 1) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Number = number,
        WorkspacePath = Workspace,
        Status = "Active",
        CurrentStage = "Intake",
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
            CurrentPhase = "Intake",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Rooms.Add(room);
        _db.SaveChanges();
        return room;
    }

    [Fact]
    public async Task PostKickoff_PostsToEveryActiveRoomInWorkspace()
    {
        var roomA = AddRoom("alpha");
        var roomB = AddRoom("beta");
        var sprint = NewSprint();

        var posted = await _service.PostKickoffAsync(sprint);

        Assert.Equal(2, posted);
        await _messageService.Received(1).PostSystemMessageAsync(roomA.Id, Arg.Any<string>());
        await _messageService.Received(1).PostSystemMessageAsync(roomB.Id, Arg.Any<string>());
        _orchestrator.Received(1).HandleHumanMessage(roomA.Id);
        _orchestrator.Received(1).HandleHumanMessage(roomB.Id);
    }

    [Fact]
    public async Task PostKickoff_SkipsArchivedAndCompletedRooms()
    {
        var active = AddRoom("active");
        AddRoom("archived", status: nameof(RoomStatus.Archived));
        AddRoom("completed", status: nameof(RoomStatus.Completed));
        var sprint = NewSprint();

        var posted = await _service.PostKickoffAsync(sprint);

        Assert.Equal(1, posted);
        await _messageService.Received(1).PostSystemMessageAsync(active.Id, Arg.Any<string>());
        _orchestrator.Received(1).HandleHumanMessage(active.Id);
        _orchestrator.DidNotReceive().HandleHumanMessage(Arg.Is<string>(id => id != active.Id));
    }

    [Fact]
    public async Task PostKickoff_OnlyTouchesRoomsInTargetWorkspace()
    {
        var ours = AddRoom("ours");
        AddRoom("foreign", workspace: "/tmp/other-workspace");
        var sprint = NewSprint();

        var posted = await _service.PostKickoffAsync(sprint);

        Assert.Equal(1, posted);
        await _messageService.Received(1).PostSystemMessageAsync(ours.Id, Arg.Any<string>());
        _orchestrator.DidNotReceive().HandleHumanMessage(Arg.Is<string>(id => id != ours.Id));
    }

    [Fact]
    public async Task PostKickoff_NoRooms_ReturnsZeroAndDoesNotThrow()
    {
        var sprint = NewSprint();

        var posted = await _service.PostKickoffAsync(sprint);

        Assert.Equal(0, posted);
        _orchestrator.DidNotReceive().HandleHumanMessage(Arg.Any<string>());
    }

    [Fact]
    public async Task PostKickoff_MessageIncludesSprintNumberStageAndWorkspace()
    {
        var room = AddRoom("alpha");
        var sprint = NewSprint(number: 7);

        await _service.PostKickoffAsync(sprint, trigger: "auto");

        await _messageService.Received(1).PostSystemMessageAsync(
            room.Id,
            Arg.Is<string>(m =>
                m.Contains("Sprint #7") &&
                m.Contains("Intake") &&
                m.Contains(Workspace) &&
                m.Contains("auto") &&
                m.Contains("Aristotle")));
    }

    [Fact]
    public async Task PostKickoff_OneRoomFails_ContinuesWithRemainingAndReportsSuccessCount()
    {
        var bad = AddRoom("bad");
        var good = AddRoom("good");

        _messageService
            .When(m => m.PostSystemMessageAsync(bad.Id, Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("simulated failure"));

        var sprint = NewSprint();
        var posted = await _service.PostKickoffAsync(sprint);

        // Returns the number of rooms actually posted to, not attempted.
        Assert.Equal(1, posted);
        await _messageService.Received(1).PostSystemMessageAsync(good.Id, Arg.Any<string>());
        _orchestrator.Received(1).HandleHumanMessage(good.Id);
        _orchestrator.DidNotReceive().HandleHumanMessage(bad.Id);
    }

    [Fact]
    public async Task PostKickoff_PostSucceedsButOrchestratorWakeFails_StillCountsAsPosted()
    {
        var room = AddRoom("alpha");
        _orchestrator
            .When(o => o.HandleHumanMessage(room.Id))
            .Do(_ => throw new InvalidOperationException("orchestrator down"));

        var sprint = NewSprint();
        var posted = await _service.PostKickoffAsync(sprint);

        // Per ISprintKickoffService contract, the return is the number of rooms in which a
        // kickoff message was posted. The wake failure is a separate concern — the message
        // is already persisted, so the post is a success.
        Assert.Equal(1, posted);
        await _messageService.Received(1).PostSystemMessageAsync(room.Id, Arg.Any<string>());
    }

    [Fact]
    public async Task PostKickoff_NullSprint_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.PostKickoffAsync(null!));
    }
}
