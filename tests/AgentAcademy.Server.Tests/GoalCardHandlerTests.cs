using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class GoalCardHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly IServiceProvider _services;
    private readonly CommandContext _context;

    private const string AgentId = "engineer-1";
    private const string AgentName = "Hephaestus";
    private const string RoomId = "room-1";

    public GoalCardHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _db.Rooms.Add(new RoomEntity
        {
            Id = RoomId,
            Name = "Test Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        var bus = new ActivityBroadcaster();
        var activity = new ActivityPublisher(_db, bus);
        var goalCardService = new GoalCardService(
            _db, activity, NullLogger<GoalCardService>.Instance);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IGoalCardService>(goalCardService);
        _services = serviceCollection.BuildServiceProvider();

        _context = new CommandContext(
            AgentId: AgentId,
            AgentName: AgentName,
            AgentRole: "Engineer",
            RoomId: RoomId,
            BreakoutRoomId: null,
            Services: _services);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CommandEnvelope MakeEnvelope(string commandName, Dictionary<string, object?> args) => new(
        Command: commandName,
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: $"cmd-{Guid.NewGuid():N}",
        Timestamp: DateTime.UtcNow,
        ExecutedBy: AgentId);

    private static Dictionary<string, object?> FullGoalCardArgs() => new()
    {
        ["task_description"] = "Add per-user rate limiting to the API",
        ["intent"] = "Protect the API from abuse",
        ["divergence"] = "Task and intent are aligned",
        ["steelman"] = "Rate limiting is essential for production APIs. It protects resources.",
        ["strawman"] = "We already have global rate limiting. Is per-user worth the complexity?",
        ["verdict"] = "Proceed",
        ["fresh_eyes_1"] = "Yes, rate limiting makes sense on its own",
        ["fresh_eyes_2"] = "All parts contribute to the goal",
        ["fresh_eyes_3"] = "No — this is standard engineering practice"
    };

    // ── CREATE_GOAL_CARD ─────────────────────────────────────

    [Fact]
    public async Task CreateGoalCard_Success()
    {
        var handler = new CreateGoalCardHandler();
        var envelope = MakeEnvelope("CREATE_GOAL_CARD", FullGoalCardArgs());

        var result = await handler.ExecuteAsync(envelope, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.NotNull(result.Result);
        Assert.Contains("goalCardId", result.Result.Keys);
        Assert.Equal("Proceed", result.Result["verdict"]?.ToString());
    }

    [Fact]
    public async Task CreateGoalCard_Challenge_Returns_Warning()
    {
        var handler = new CreateGoalCardHandler();
        var args = FullGoalCardArgs();
        args["verdict"] = "Challenge";
        var envelope = MakeEnvelope("CREATE_GOAL_CARD", args);

        var result = await handler.ExecuteAsync(envelope, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Contains("CHALLENGED", result.Result!["message"]?.ToString());
    }

    [Fact]
    public async Task CreateGoalCard_Missing_Fields_Returns_Error()
    {
        var handler = new CreateGoalCardHandler();
        var args = new Dictionary<string, object?>
        {
            ["task_description"] = "Something",
            ["intent"] = "Something"
        };
        var envelope = MakeEnvelope("CREATE_GOAL_CARD", args);

        var result = await handler.ExecuteAsync(envelope, _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Missing required fields", result.Result!["error"]?.ToString());
    }

    [Fact]
    public async Task CreateGoalCard_Invalid_Verdict_Returns_Error()
    {
        var handler = new CreateGoalCardHandler();
        var args = FullGoalCardArgs();
        args["verdict"] = "InvalidVerdict";
        var envelope = MakeEnvelope("CREATE_GOAL_CARD", args);

        var result = await handler.ExecuteAsync(envelope, _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Invalid verdict", result.Result!["error"]?.ToString());
    }

    [Fact]
    public async Task CreateGoalCard_No_Room_Returns_Error()
    {
        var handler = new CreateGoalCardHandler();
        var envelope = MakeEnvelope("CREATE_GOAL_CARD", FullGoalCardArgs());
        var noRoomContext = _context with { RoomId = null };

        var result = await handler.ExecuteAsync(envelope, noRoomContext);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("requires a room context", result.Result!["error"]?.ToString());
    }

    [Fact]
    public async Task CreateGoalCard_With_TaskId()
    {
        var task = new TaskEntity
        {
            Id = "task-001",
            Title = "Test",
            Description = "Desc",
            SuccessCriteria = "Crit",
            Status = "Active",
            RoomId = RoomId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var handler = new CreateGoalCardHandler();
        var args = FullGoalCardArgs();
        args["task_id"] = "task-001";
        var envelope = MakeEnvelope("CREATE_GOAL_CARD", args);

        var result = await handler.ExecuteAsync(envelope, _context);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── UPDATE_GOAL_CARD_STATUS ──────────────────────────────

    [Fact]
    public async Task UpdateGoalCardStatus_Success()
    {
        var createHandler = new CreateGoalCardHandler();
        var createResult = await createHandler.ExecuteAsync(
            MakeEnvelope("CREATE_GOAL_CARD", FullGoalCardArgs()), _context);
        var goalCardId = createResult.Result!["goalCardId"]!.ToString()!;

        var updateHandler = new UpdateGoalCardStatusHandler();
        var updateArgs = new Dictionary<string, object?>
        {
            ["goal_card_id"] = goalCardId,
            ["status"] = "Completed"
        };
        var result = await updateHandler.ExecuteAsync(
            MakeEnvelope("UPDATE_GOAL_CARD_STATUS", updateArgs), _context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Completed", result.Result!["status"]?.ToString());
    }

    [Fact]
    public async Task UpdateGoalCardStatus_Missing_Fields_Returns_Error()
    {
        var handler = new UpdateGoalCardStatusHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope("UPDATE_GOAL_CARD_STATUS", new Dictionary<string, object?>()), _context);

        Assert.Equal(CommandStatus.Error, result.Status);
    }

    [Fact]
    public async Task UpdateGoalCardStatus_Invalid_Status_Returns_Error()
    {
        var handler = new UpdateGoalCardStatusHandler();
        var args = new Dictionary<string, object?>
        {
            ["goal_card_id"] = "any",
            ["status"] = "InvalidStatus"
        };
        var result = await handler.ExecuteAsync(
            MakeEnvelope("UPDATE_GOAL_CARD_STATUS", args), _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Invalid status", result.Result!["error"]?.ToString());
    }

    [Fact]
    public async Task UpdateGoalCardStatus_NotFound_Returns_Error()
    {
        var handler = new UpdateGoalCardStatusHandler();
        var args = new Dictionary<string, object?>
        {
            ["goal_card_id"] = "nonexistent",
            ["status"] = "Completed"
        };
        var result = await handler.ExecuteAsync(
            MakeEnvelope("UPDATE_GOAL_CARD_STATUS", args), _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Result!["error"]?.ToString());
    }

    [Fact]
    public async Task UpdateGoalCardStatus_Illegal_Transition_Returns_Error()
    {
        // Create and complete a card
        var createHandler = new CreateGoalCardHandler();
        var createResult = await createHandler.ExecuteAsync(
            MakeEnvelope("CREATE_GOAL_CARD", FullGoalCardArgs()), _context);
        var goalCardId = createResult.Result!["goalCardId"]!.ToString()!;

        var updateHandler = new UpdateGoalCardStatusHandler();
        await updateHandler.ExecuteAsync(
            MakeEnvelope("UPDATE_GOAL_CARD_STATUS", new Dictionary<string, object?>
            {
                ["goal_card_id"] = goalCardId,
                ["status"] = "Completed"
            }), _context);

        // Try to transition from Completed → Active (illegal)
        var result = await updateHandler.ExecuteAsync(
            MakeEnvelope("UPDATE_GOAL_CARD_STATUS", new Dictionary<string, object?>
            {
                ["goal_card_id"] = goalCardId,
                ["status"] = "Active"
            }), _context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Cannot transition", result.Result!["error"]?.ToString());
    }
}
