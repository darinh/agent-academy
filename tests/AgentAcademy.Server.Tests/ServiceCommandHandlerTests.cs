using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for service-dependent command handlers:
/// MoveToRoomHandler, RoomHistoryHandler, ListCommandsHandler.
/// </summary>
public sealed class ServiceCommandHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public ServiceCommandHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(
                        ["LIST_COMMANDS", "MOVE_TO_ROOM", "ROOM_HISTORY", "LIST_ROOMS"], [])),
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["LIST_COMMANDS"], []))
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<MessageService>();
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<RoomService>();
        services.AddScoped<RoomSnapshotBuilder>();
        services.AddSingleton<ILogger<WorkspaceRoomService>>(NullLogger<WorkspaceRoomService>.Instance);
        services.AddScoped<WorkspaceRoomService>();
        services.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<CrashRecoveryService>();
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();

        // Register command handlers for ListCommandsHandler
        services.AddSingleton<ICommandHandler, ListCommandsHandler>();
        services.AddSingleton<ICommandHandler, MoveToRoomHandler>();
        services.AddSingleton<ICommandHandler, RoomHistoryHandler>();
        services.AddSingleton<ICommandHandler, ListRoomsHandler>();

        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, object?> args,
        string agentId = "engineer-1",
        string agentName = "Hephaestus",
        string agentRole = "SoftwareEngineer")
    {
        var scope = _serviceProvider.CreateScope();

        var command = new CommandEnvelope(
            Command: commandName,
            Args: args,
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: agentId
        );

        var context = new CommandContext(
            AgentId: agentId,
            AgentName: agentName,
            AgentRole: agentRole,
            RoomId: "main",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider
        );

        return (command, context);
    }

    private async Task<string> CreateRoom(string name)
    {
        using var scope = _serviceProvider.CreateScope();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        var room = await roomService.CreateRoomAsync(name, null);
        return room.Id;
    }

    private async Task PostMessage(string roomId, string content, string senderId = "engineer-1")
    {
        using var scope = _serviceProvider.CreateScope();
        var msgService = scope.ServiceProvider.GetRequiredService<MessageService>();
        await msgService.PostMessageAsync(new PostMessageRequest(
            RoomId: roomId,
            SenderId: senderId,
            Content: content,
            Kind: MessageKind.Response
        ));
    }

    // ── MOVE_TO_ROOM ─────────────────────────────────────────

    [Fact]
    public async Task MoveToRoom_Success()
    {
        var roomId = await CreateRoom("target-room");

        var handler = new MoveToRoomHandler();
        var (cmd, ctx) = MakeCommand("MOVE_TO_ROOM", new() { ["roomId"] = roomId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(roomId, (string)dict["roomId"]!);
        Assert.Equal("target-room", (string)dict["roomName"]!);
    }

    [Fact]
    public async Task MoveToRoom_MissingRoomId_ReturnsValidationError()
    {
        var handler = new MoveToRoomHandler();
        var (cmd, ctx) = MakeCommand("MOVE_TO_ROOM", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("roomId", result.Error!);
    }

    [Fact]
    public async Task MoveToRoom_EmptyRoomId_ReturnsValidationError()
    {
        var handler = new MoveToRoomHandler();
        var (cmd, ctx) = MakeCommand("MOVE_TO_ROOM", new() { ["roomId"] = "" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task MoveToRoom_NonexistentRoom_ReturnsNotFound()
    {
        var handler = new MoveToRoomHandler();
        var (cmd, ctx) = MakeCommand("MOVE_TO_ROOM", new() { ["roomId"] = "nonexistent-room" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
        Assert.Contains("nonexistent-room", result.Error!);
    }

    [Fact]
    public async Task MoveToRoom_AcceptsCaseInsensitiveArgKey()
    {
        var roomId = await CreateRoom("ci-room");

        var handler = new MoveToRoomHandler();
        // Use lowercase "roomid" instead of "roomId"
        var (cmd, ctx) = MakeCommand("MOVE_TO_ROOM", new() { ["roomid"] = roomId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── ROOM_HISTORY ─────────────────────────────────────────

    [Fact]
    public async Task RoomHistory_ReturnsMessages()
    {
        var roomId = await CreateRoom("history-room");
        await PostMessage(roomId, "Hello from test");
        await PostMessage(roomId, "Second message");

        var handler = new RoomHistoryHandler();
        var (cmd, ctx) = MakeCommand("ROOM_HISTORY", new() { ["roomId"] = roomId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(roomId, (string)dict["roomId"]!);
        Assert.Equal("history-room", (string)dict["roomName"]!);
        var messages = (List<Dictionary<string, object?>>)dict["messages"]!;
        // 1 system welcome message + 2 posted messages = 3
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public async Task RoomHistory_MissingRoomId_ReturnsValidationError()
    {
        var handler = new RoomHistoryHandler();
        var (cmd, ctx) = MakeCommand("ROOM_HISTORY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task RoomHistory_NonexistentRoom_ReturnsNotFound()
    {
        var handler = new RoomHistoryHandler();
        var (cmd, ctx) = MakeCommand("ROOM_HISTORY", new() { ["roomId"] = "fake-room" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task RoomHistory_RespectsCountArg()
    {
        var roomId = await CreateRoom("count-room");
        await PostMessage(roomId, "Message 1");
        await PostMessage(roomId, "Message 2");
        await PostMessage(roomId, "Message 3");

        var handler = new RoomHistoryHandler();
        var (cmd, ctx) = MakeCommand("ROOM_HISTORY", new() { ["roomId"] = roomId, ["count"] = "2" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var messages = (List<Dictionary<string, object?>>)dict["messages"]!;
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task RoomHistory_CapsCountAtMax()
    {
        var roomId = await CreateRoom("max-room");
        await PostMessage(roomId, "Single message");

        var handler = new RoomHistoryHandler();
        var (cmd, ctx) = MakeCommand("ROOM_HISTORY", new() { ["roomId"] = roomId, ["count"] = "999" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        // Should succeed but only return available messages (not crash)
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        // 1 system welcome + 1 posted = 2
        Assert.Equal(2, (int)dict["count"]!);
    }

    [Fact]
    public async Task RoomHistory_IntCount()
    {
        var roomId = await CreateRoom("int-count-room");
        await PostMessage(roomId, "Message 1");

        var handler = new RoomHistoryHandler();
        var (cmd, ctx) = MakeCommand("ROOM_HISTORY", new() { ["roomId"] = roomId, ["count"] = 1 });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task RoomHistory_EmptyRoom_ReturnsOnlyWelcomeMessage()
    {
        var roomId = await CreateRoom("empty-room");

        var handler = new RoomHistoryHandler();
        var (cmd, ctx) = MakeCommand("ROOM_HISTORY", new() { ["roomId"] = roomId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        // Room creation adds a system welcome message
        Assert.Equal(1, (int)dict["count"]!);
    }

    [Fact]
    public async Task RoomHistory_MessagesContainExpectedFields()
    {
        var roomId = await CreateRoom("fields-room");
        await PostMessage(roomId, "Check fields");

        var handler = new RoomHistoryHandler();
        var (cmd, ctx) = MakeCommand("ROOM_HISTORY", new() { ["roomId"] = roomId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var messages = (List<Dictionary<string, object?>>)dict["messages"]!;
        var msg = messages[0];
        Assert.True(msg.ContainsKey("sender"));
        Assert.True(msg.ContainsKey("role"));
        Assert.True(msg.ContainsKey("content"));
        Assert.True(msg.ContainsKey("sentAt"));
    }

    // ── LIST_COMMANDS ────────────────────────────────────────

    [Fact]
    public async Task ListCommands_ReturnsAllRegisteredHandlers()
    {
        var handler = new ListCommandsHandler();
        var (cmd, ctx) = MakeCommand("LIST_COMMANDS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var commands = (List<Dictionary<string, object?>>)dict["commands"]!;
        Assert.True((int)dict["count"]! > 0);
        // At minimum, the 4 handlers we registered should appear
        var names = commands.Select(c => (string)c["command"]!).ToList();
        Assert.Contains("LIST_COMMANDS", names);
        Assert.Contains("MOVE_TO_ROOM", names);
        Assert.Contains("ROOM_HISTORY", names);
        Assert.Contains("LIST_ROOMS", names);
    }

    [Fact]
    public async Task ListCommands_ShowsAuthorizationStatus()
    {
        var handler = new ListCommandsHandler();
        var (cmd, ctx) = MakeCommand("LIST_COMMANDS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var commands = (List<Dictionary<string, object?>>)dict["commands"]!;
        foreach (var c in commands)
        {
            Assert.True(c.ContainsKey("authorized"), $"Command {c["command"]} missing 'authorized' field");
            Assert.True(c.ContainsKey("description"), $"Command {c["command"]} missing 'description' field");
        }
    }

    [Fact]
    public async Task ListCommands_AuthorizedCountMatchesPermissions()
    {
        var handler = new ListCommandsHandler();
        // Use planner who only has LIST_COMMANDS permission
        var (cmd, ctx) = MakeCommand("LIST_COMMANDS", new(),
            agentId: "planner-1", agentName: "Aristotle", agentRole: "Planner");
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var authorizedCount = (int)dict["authorizedCount"]!;
        var totalCount = (int)dict["count"]!;
        // Planner only has LIST_COMMANDS permission, so authorizedCount < totalCount
        Assert.True(authorizedCount < totalCount,
            $"Expected authorizedCount ({authorizedCount}) < totalCount ({totalCount}) for restricted agent");
        Assert.True(authorizedCount >= 1, "Should authorize at least LIST_COMMANDS");
    }

    [Fact]
    public async Task ListCommands_CommandsAreSorted()
    {
        var handler = new ListCommandsHandler();
        var (cmd, ctx) = MakeCommand("LIST_COMMANDS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var commands = (List<Dictionary<string, object?>>)dict["commands"]!;
        var names = commands.Select(c => (string)c["command"]!).ToList();
        var sorted = names.OrderBy(n => n).ToList();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public async Task ListCommands_AllCommandsHaveDescriptions()
    {
        var handler = new ListCommandsHandler();
        var (cmd, ctx) = MakeCommand("LIST_COMMANDS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var commands = (List<Dictionary<string, object?>>)dict["commands"]!;
        foreach (var c in commands)
        {
            var desc = (string?)c["description"];
            Assert.NotNull(desc);
            Assert.NotEmpty(desc!);
        }
    }
}
