using Microsoft.Extensions.Logging.Abstractions;
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
using Microsoft.Extensions.Logging;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for task system enhancements:
/// - TaskType + creation gating
/// - ADD_TASK_COMMENT command
/// - RECALL_AGENT command
/// - ParseTaskAssignments Type field parsing
/// </summary>
[Collection("WorkspaceRuntime")]
public class TaskSystemTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public TaskSystemTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false),
                new AgentDefinition(
                    Id: "architect-1", Name: "Archimedes", Role: "Architect",
                    Summary: "Architect", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<AgentLocationService>();
        services.AddScoped<IAgentLocationService>(sp => sp.GetRequiredService<AgentLocationService>());
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<RoomService>();
        services.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
        services.AddScoped<RoomSnapshotBuilder>();

        services.AddScoped<IRoomSnapshotBuilder>(sp => sp.GetRequiredService<RoomSnapshotBuilder>());
        services.AddSingleton<ILogger<WorkspaceRoomService>>(NullLogger<WorkspaceRoomService>.Instance);
        services.AddScoped<WorkspaceRoomService>();

        services.AddScoped<IWorkspaceRoomService>(sp => sp.GetRequiredService<WorkspaceRoomService>());
        services.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<IRoomLifecycleService>(sp => sp.GetRequiredService<RoomLifecycleService>());
        services.AddScoped<CrashRecoveryService>();
        services.AddScoped<ICrashRecoveryService>(sp => sp.GetRequiredService<CrashRecoveryService>());
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
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

    // ── ParseTaskAssignments Type Parsing ────────────────────────

    [Fact]
    public void ParseTaskAssignments_ParsesTypeField()
    {
        var content = """
            TASK ASSIGNMENT:
            Agent: @Hephaestus
            Title: Fix login bug
            Description: Login fails on empty password
            Type: Bug
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);

        Assert.Single(result);
        Assert.Equal(TaskType.Bug, result[0].Type);
        Assert.Equal("Fix login bug", result[0].Title);
    }

    [Fact]
    public void ParseTaskAssignments_DefaultsToFeature_WhenNoType()
    {
        var content = """
            TASK ASSIGNMENT:
            Agent: @Hephaestus
            Title: Build auth system
            Description: Implement JWT
            Acceptance Criteria:
            - Users can log in
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);

        Assert.Single(result);
        Assert.Equal(TaskType.Feature, result[0].Type);
    }

    [Fact]
    public void ParseTaskAssignments_ParsesCaseInsensitiveType()
    {
        var content = """
            TASK ASSIGNMENT:
            Agent: @Hephaestus
            Title: Investigate slowness
            Type: spike
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);

        Assert.Single(result);
        Assert.Equal(TaskType.Spike, result[0].Type);
    }

    [Fact]
    public void ParseTaskAssignments_InvalidType_DefaultsToFeature()
    {
        var content = """
            TASK ASSIGNMENT:
            Agent: @Hephaestus
            Title: Some task
            Type: NotARealType
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);

        Assert.Single(result);
        Assert.Equal(TaskType.Feature, result[0].Type);
    }

    [Fact]
    public void ParseTaskAssignments_TypeWithAcceptanceCriteria()
    {
        var content = """
            TASK ASSIGNMENT:
            Agent: @Athena
            Title: Fix rendering
            Description: Button clipped on mobile
            Acceptance Criteria:
            - Button visible on mobile
            - No layout shift
            Type: Bug
            """;

        var result = AgentResponseParser.ParseTaskAssignments(content);

        Assert.Single(result);
        Assert.Equal(TaskType.Bug, result[0].Type);
        Assert.Equal(2, result[0].Criteria.Count);
    }

    // ── ADD_TASK_COMMENT Command ─────────────────────────────────

    [Fact]
    public async Task SetPlan_SavesPlanForCurrentRoom()
    {
        await EnsureRoom("room-1");
        var handler = new SetPlanHandler();
        var (cmd, ctx) = MakeCommand(
            "SET_PLAN",
            new() { ["content"] = "# Backend Plan\n\n- Add command" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);

        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var plan = await plans.GetPlanAsync("room-1");

        Assert.NotNull(plan);
        Assert.Contains("Backend Plan", plan!.Content);
    }

    [Fact]
    public async Task SetPlan_WithoutRoomContext_ReturnsError()
    {
        var handler = new SetPlanHandler();
        var scope = _serviceProvider.CreateScope();
        var command = new CommandEnvelope(
            Command: "SET_PLAN",
            Args: new Dictionary<string, object?> { ["content"] = "# Plan" },
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1");
        var context = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: null,
            BreakoutRoomId: null,
            Services: scope.ServiceProvider);

        var result = await handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("room context", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddTaskComment_Assignee_CanComment()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new()
            {
                ["taskId"] = taskId,
                ["type"] = "Finding",
                ["content"] = "Build passes with 0 errors"
            }, "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var resultDict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("Finding", resultDict["type"]!.ToString());
    }

    [Fact]
    public async Task AddTaskComment_Reviewer_CanComment()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1", reviewerAgentId: "reviewer-1");
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new()
            {
                ["taskId"] = taskId,
                ["content"] = "Code looks good"
            }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task AddTaskComment_Planner_CanComment()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new()
            {
                ["taskId"] = taskId,
                ["content"] = "This is on track"
            }, "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task AddTaskComment_UnrelatedAgent_Denied()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new()
            {
                ["taskId"] = taskId,
                ["content"] = "Trying to comment"
            }, "architect-1", "Archimedes", "Architect");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("assignee, reviewer, or planner", result.Error);
    }

    [Fact]
    public async Task AddTaskComment_MissingTaskId_ReturnsError()
    {
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new() { ["content"] = "Hello" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("TaskId", result.Error);
    }

    [Fact]
    public async Task AddTaskComment_MissingContent_ReturnsError()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Content", result.Error);
    }

    [Fact]
    public async Task AddTaskComment_InvalidType_ReturnsError()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new()
            {
                ["taskId"] = taskId,
                ["type"] = "InvalidType",
                ["content"] = "Hello"
            }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Invalid comment type", result.Error);
    }

    [Fact]
    public async Task AddTaskComment_DefaultsToCommentType()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new()
            {
                ["taskId"] = taskId,
                ["content"] = "Just a note"
            }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var resultDict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("Comment", resultDict["type"]!.ToString());
    }

    [Fact]
    public async Task AddTaskComment_NonexistentTask_ReturnsError()
    {
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new()
            {
                ["taskId"] = "nonexistent-task-id",
                ["content"] = "Hello"
            }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error);
    }

    // ── Task Comments API / Runtime ─────────────────────────────

    [Fact]
    public async Task GetTaskComments_ReturnsCommentsInOrder()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");

        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        await taskLifecycle.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Comment, "First");
        await taskLifecycle.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Finding, "Second");
        await taskLifecycle.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Evidence, "Third");

        var comments = await taskQueries.GetTaskCommentsAsync(taskId);

        Assert.Equal(3, comments.Count);
        Assert.Equal("First", comments[0].Content);
        Assert.Equal(TaskCommentType.Finding, comments[1].CommentType);
        Assert.Equal("Third", comments[2].Content);
    }

    [Fact]
    public async Task GetTaskCommentCount_ReturnsCorrectCount()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");

        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        await taskLifecycle.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Comment, "One");
        await taskLifecycle.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Evidence, "Two");

        var count = await taskQueries.GetTaskCommentCountAsync(taskId);

        Assert.Equal(2, count);
    }

    // ── RECALL_AGENT Command ────────────────────────────────────

    [Fact]
    public async Task RecallAgent_Planner_CanRecall()
    {
        // Set up a room and put an agent in a breakout
        await SetupRoomAndBreakout("engineer-1");

        var handler = new RecallAgentHandler();
        var (cmd, ctx) = MakeCommand("RECALL_AGENT",
            new() { ["agentId"] = "Hephaestus" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var resultDict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("Hephaestus", resultDict["agentName"]!.ToString());
    }

    [Fact]
    public async Task RecallAgent_NonPlanner_Denied()
    {
        await SetupRoomAndBreakout("engineer-1");

        var handler = new RecallAgentHandler();
        var (cmd, ctx) = MakeCommand("RECALL_AGENT",
            new() { ["agentId"] = "Hephaestus" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("planner", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecallAgent_AgentNotInBreakout_ReturnsError()
    {
        // Ensure the agent is in a room but NOT in a breakout
        await EnsureRoom("room-1");
        using (var scope = _serviceProvider.CreateScope())
        {
            var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
            var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
            var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
            var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await agentLocations.MoveAgentAsync("engineer-1", "room-1", AgentState.Idle);
        }

        var handler = new RecallAgentHandler();
        var (cmd, ctx) = MakeCommand("RECALL_AGENT",
            new() { ["agentId"] = "Hephaestus" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not currently in a breakout", result.Error);
    }

    [Fact]
    public async Task RecallAgent_UnknownAgent_ReturnsError()
    {
        var handler = new RecallAgentHandler();
        var (cmd, ctx) = MakeCommand("RECALL_AGENT",
            new() { ["agentId"] = "NonexistentAgent" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task RecallAgent_ShorthandValue_Works()
    {
        await SetupRoomAndBreakout("engineer-1");

        var handler = new RecallAgentHandler();
        var (cmd, ctx) = MakeCommand("RECALL_AGENT",
            new() { ["value"] = "Hephaestus" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task RecallAgent_MissingAgentId_ReturnsError()
    {
        var handler = new RecallAgentHandler();
        var (cmd, ctx) = MakeCommand("RECALL_AGENT",
            new(),
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Missing required argument", result.Error);
    }

    [Fact]
    public async Task RecallAgent_VerifiesAgentReturnsToParent()
    {
        var breakoutId = await SetupRoomAndBreakout("engineer-1");

        var handler = new RecallAgentHandler();
        var (cmd, ctx) = MakeCommand("RECALL_AGENT",
            new() { ["agentId"] = "Hephaestus" },
            "planner-1", "Aristotle", "Planner");

        await handler.ExecuteAsync(cmd, ctx);

        // Verify agent is back in the parent room
        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var location = await agentLocations.GetAgentLocationAsync("engineer-1");

        Assert.NotNull(location);
        Assert.Equal(AgentState.Idle, location.State);
        Assert.Equal("room-1", location.RoomId);
    }

    // ── CLOSE_ROOM Command ─────────────────────────────────────

    [Fact]
    public async Task CloseRoom_Planner_CanArchiveEmptyRoom()
    {
        await EnsureRoom("room-2");

        var handler = new CloseRoomHandler();
        var (cmd, ctx) = MakeCommand("CLOSE_ROOM",
            new() { ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);

        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var room = await rooms.GetRoomAsync("room-2");

        Assert.NotNull(room);
        Assert.Equal(RoomStatus.Archived, room!.Status);
    }

    [Fact]
    public async Task CloseRoom_NonPlanner_Denied()
    {
        await EnsureRoom("room-2");

        var handler = new CloseRoomHandler();
        var (cmd, ctx) = MakeCommand("CLOSE_ROOM",
            new() { ["roomId"] = "room-2" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("planner", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseRoom_MainRoom_ReturnsError()
    {
        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        await initialization.InitializeAsync();

        var handler = new CloseRoomHandler();
        var (cmd, ctx) = MakeCommand("CLOSE_ROOM",
            new() { ["roomId"] = "main" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("main collaboration room", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseRoom_WithParticipants_ReturnsError()
    {
        await EnsureRoom("room-2");
        using (var scope = _serviceProvider.CreateScope())
        {
            var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
            var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
            var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
            var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await agentLocations.MoveAgentAsync("engineer-1", "room-2", AgentState.Idle);
        }

        var handler = new CloseRoomHandler();
        var (cmd, ctx) = MakeCommand("CLOSE_ROOM",
            new() { ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("active participant", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseRoom_PublishesRoomClosedActivityEvent()
    {
        await EnsureRoom("room-2");

        var handler = new CloseRoomHandler();
        var (cmd, ctx) = MakeCommand("CLOSE_ROOM",
            new() { ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var evt = await db.ActivityEvents
            .Where(e => e.RoomId == "room-2" && e.Type == nameof(ActivityEventType.RoomClosed))
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(evt);
        Assert.Contains("Room archived", evt!.Message);
    }

    [Fact]
    public async Task CloseRoom_AlreadyArchived_IsNoOpSuccess()
    {
        await EnsureRoom("room-2");

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var room = await db.Rooms.FindAsync("room-2");
            Assert.NotNull(room);

            room!.Status = nameof(RoomStatus.Archived);
            room.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var handler = new CloseRoomHandler();
        var (cmd, ctx) = MakeCommand("CLOSE_ROOM",
            new() { ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(nameof(RoomStatus.Archived), payload["status"]);
        Assert.Contains("already archived", payload["message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── CREATE_ROOM Command ─────────────────────────────────────

    [Fact]
    public async Task CreateRoom_Planner_CreatesRoom()
    {
        var handler = new CreateRoomHandler();
        var (cmd, ctx) = MakeCommand("CREATE_ROOM",
            new() { ["name"] = "Feature: User Profiles" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("Feature: User Profiles", payload["roomName"]);
        Assert.NotNull(payload["roomId"]);
        Assert.Contains("created", payload["message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRoom_WithDescription_IncludesInWelcomeMessage()
    {
        var handler = new CreateRoomHandler();
        var (cmd, ctx) = MakeCommand("CREATE_ROOM",
            new() { ["name"] = "Bug: Login timeout", ["description"] = "Users on slow connections time out" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var roomId = payload["roomId"]!.ToString()!;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var welcomeMsg = await db.Messages
            .Where(m => m.RoomId == roomId && m.Kind == nameof(MessageKind.System))
            .FirstOrDefaultAsync();

        Assert.NotNull(welcomeMsg);
        Assert.Contains("Users on slow connections", welcomeMsg!.Content);
    }

    [Fact]
    public async Task CreateRoom_NonPlanner_Denied()
    {
        var handler = new CreateRoomHandler();
        var (cmd, ctx) = MakeCommand("CREATE_ROOM",
            new() { ["name"] = "Test Room" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("planner", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRoom_MissingName_ReturnsValidationError()
    {
        var handler = new CreateRoomHandler();
        var (cmd, ctx) = MakeCommand("CREATE_ROOM",
            new() { },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task CreateRoom_PublishesActivityEvent()
    {
        var handler = new CreateRoomHandler();
        var (cmd, ctx) = MakeCommand("CREATE_ROOM",
            new() { ["name"] = "Discussion: API versioning" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var roomId = payload["roomId"]!.ToString()!;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var evt = await db.ActivityEvents
            .Where(e => e.RoomId == roomId && e.Type == nameof(ActivityEventType.RoomCreated))
            .FirstOrDefaultAsync();

        Assert.NotNull(evt);
        Assert.Contains("Discussion: API versioning", evt!.Message);
    }

    [Fact]
    public async Task CreateRoom_GeneratesSlugId()
    {
        var handler = new CreateRoomHandler();
        var (cmd, ctx) = MakeCommand("CREATE_ROOM",
            new() { ["name"] = "Feature: User Profiles & Settings" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var roomId = payload["roomId"]!.ToString()!;

        Assert.StartsWith("feature-user-profiles-settings-", roomId);
    }

    // ── REOPEN_ROOM Command ─────────────────────────────────────

    [Fact]
    public async Task ReopenRoom_ArchivedRoom_Succeeds()
    {
        await EnsureRoom("room-archived");
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var room = await db.Rooms.FindAsync("room-archived");
            room!.Status = nameof(RoomStatus.Archived);
            await db.SaveChangesAsync();
        }

        var handler = new ReopenRoomHandler();
        var (cmd, ctx) = MakeCommand("REOPEN_ROOM",
            new() { ["roomId"] = "room-archived" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var payload = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("Idle", payload["status"]!.ToString());
        Assert.Contains("reopened", payload["message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReopenRoom_NonPlanner_Denied()
    {
        var handler = new ReopenRoomHandler();
        var (cmd, ctx) = MakeCommand("REOPEN_ROOM",
            new() { ["roomId"] = "room-1" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task ReopenRoom_NotArchived_ReturnsConflict()
    {
        await EnsureRoom("room-active");

        var handler = new ReopenRoomHandler();
        var (cmd, ctx) = MakeCommand("REOPEN_ROOM",
            new() { ["roomId"] = "room-active" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
    }

    [Fact]
    public async Task ReopenRoom_NotFound_ReturnsNotFound()
    {
        var handler = new ReopenRoomHandler();
        var (cmd, ctx) = MakeCommand("REOPEN_ROOM",
            new() { ["roomId"] = "nonexistent" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ReopenRoom_PublishesStatusChangeEvent()
    {
        await EnsureRoom("room-reopen-evt");
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var room = await db.Rooms.FindAsync("room-reopen-evt");
            room!.Status = nameof(RoomStatus.Archived);
            await db.SaveChangesAsync();
        }

        var handler = new ReopenRoomHandler();
        var (cmd, ctx) = MakeCommand("REOPEN_ROOM",
            new() { ["roomId"] = "room-reopen-evt" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);

        using var scope2 = _serviceProvider.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var evt = await db2.ActivityEvents
            .Where(e => e.RoomId == "room-reopen-evt" && e.Type == nameof(ActivityEventType.RoomStatusChanged))
            .FirstOrDefaultAsync();

        Assert.NotNull(evt);
        Assert.Contains("reopened", evt!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReopenRoom_MissingRoomId_ReturnsValidation()
    {
        var handler = new ReopenRoomHandler();
        var (cmd, ctx) = MakeCommand("REOPEN_ROOM",
            new() { },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    // ── LIST_ROOMS Status Filter ────────────────────────────────

    [Fact]
    public async Task ListRooms_StatusFilter_FiltersResults()
    {
        await EnsureRoom("room-active-filter");
        await EnsureRoom("room-archived-filter");
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var archived = await db.Rooms.FindAsync("room-archived-filter");
            archived!.Status = nameof(RoomStatus.Archived);
            await db.SaveChangesAsync();
        }

        var handler = new ListRoomsHandler();
        var (cmd, ctx) = MakeCommand("LIST_ROOMS",
            new() { ["status"] = "Archived" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);

        var payload = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var rooms = Assert.IsAssignableFrom<IEnumerable<object>>(payload["rooms"]);
        foreach (var room in rooms.Cast<Dictionary<string, object?>>())
        {
            Assert.Equal("Archived", room["status"]!.ToString());
        }
    }

    // ── Task Type in Task Creation ──────────────────────────────

    [Fact]
    public async Task CreateTask_DefaultsToFeatureType()
    {
        await EnsureRoom("room-1");
        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        var result = await taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            Title: "Build feature",
            Description: "Some feature",
            SuccessCriteria: "It works",
            RoomId: "room-1",
            PreferredRoles: ["SoftwareEngineer"]
        ));

        Assert.Equal(TaskType.Feature, result.Task.Type);
    }

    [Fact]
    public async Task CreateTask_BugType()
    {
        await EnsureRoom("room-1");
        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        var result = await taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            Title: "Fix crash",
            Description: "App crashes on startup",
            SuccessCriteria: "No crash",
            RoomId: "room-1",
            PreferredRoles: ["SoftwareEngineer"],
            Type: TaskType.Bug
        ));

        Assert.Equal(TaskType.Bug, result.Task.Type);
    }

    // ── CommandParser Registration ──────────────────────────────

    [Fact]
    public void CommandParser_RecognizesAddTaskComment()
    {
        var parser = new CommandParser();
        var result = parser.Parse("ADD_TASK_COMMENT:\n  TaskId: t1\n  Content: test");

        Assert.Single(result.Commands);
        Assert.Equal("ADD_TASK_COMMENT", result.Commands[0].Command);
    }

    [Fact]
    public void CommandParser_RecognizesRecallAgent()
    {
        var parser = new CommandParser();
        var result = parser.Parse("RECALL_AGENT: Hephaestus");

        Assert.Single(result.Commands);
        Assert.Equal("RECALL_AGENT", result.Commands[0].Command);
    }

    [Fact]
    public void CommandParser_RecognizesCloseRoom()
    {
        var parser = new CommandParser();
        var result = parser.Parse("CLOSE_ROOM: roomId=room-2");

        Assert.Single(result.Commands);
        Assert.Equal("CLOSE_ROOM", result.Commands[0].Command);
        Assert.Equal("room-2", result.Commands[0].Args["roomId"]);
    }

    [Fact]
    public void CommandParser_RecognizesSetPlan()
    {
        var parser = new CommandParser();
        var result = parser.Parse("SET_PLAN:\n  Content: # Plan");

        Assert.Single(result.Commands);
        Assert.Equal("SET_PLAN", result.Commands[0].Command);
    }

    // ── INVITE_TO_ROOM ──────────────────────────────────────────

    [Fact]
    public async Task InviteToRoom_Planner_MovesAgentToRoom()
    {
        await EnsureRoom("room-2");

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "engineer-1", ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("engineer-1", dict["agentId"]);
        Assert.Equal("room-2", dict["roomId"]);

        // Verify agent is actually in room-2
        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var location = await agentLocations.GetAgentLocationAsync("engineer-1");
        Assert.NotNull(location);
        Assert.Equal("room-2", location!.RoomId);
    }

    [Fact]
    public async Task InviteToRoom_Human_Allowed()
    {
        await EnsureRoom("room-2");

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "engineer-1", ["roomId"] = "room-2" },
            "human-1", "Human", "Human");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task InviteToRoom_NonPlanner_Denied()
    {
        await EnsureRoom("room-2");

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "reviewer-1", ["roomId"] = "room-2" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("planner", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("human", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InviteToRoom_MissingAgentId_ReturnsValidationError()
    {
        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("agentId", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InviteToRoom_MissingRoomId_ReturnsValidationError()
    {
        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "engineer-1" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("roomId", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InviteToRoom_UnknownAgent_ReturnsNotFound()
    {
        await EnsureRoom("room-2");

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "nonexistent-agent", ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
        Assert.Contains("nonexistent-agent", result.Error);
    }

    [Fact]
    public async Task InviteToRoom_UnknownRoom_ReturnsNotFound()
    {
        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "engineer-1", ["roomId"] = "nonexistent-room" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
        Assert.Contains("nonexistent-room", result.Error);
    }

    [Fact]
    public async Task InviteToRoom_ArchivedRoom_ReturnsConflict()
    {
        await EnsureRoom("room-2");
        // Archive the room
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var room = await db.Rooms.FindAsync("room-2");
            room!.Status = "Archived";
            await db.SaveChangesAsync();
        }

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "engineer-1", ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("archived", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InviteToRoom_WorkingInBreakout_ReturnsConflict()
    {
        await SetupRoomAndBreakout("engineer-1");
        await EnsureRoom("room-2");

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "engineer-1", ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("breakout", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InviteToRoom_AlreadyInRoom_ReturnsSuccess()
    {
        await EnsureRoom("room-2");

        // Move agent to room-2 first
        using (var scope = _serviceProvider.CreateScope())
        {
            var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
            var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
            var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
            var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await agentLocations.MoveAgentAsync("engineer-1", "room-2", AgentState.Idle);
        }

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "engineer-1", ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Contains("already", (string)dict["message"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InviteToRoom_ByAgentName_ResolvesCorrectly()
    {
        await EnsureRoom("room-2");

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "Hephaestus", ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("engineer-1", dict["agentId"]);
    }

    [Fact]
    public async Task InviteToRoom_PostsSystemMessage()
    {
        await EnsureRoom("room-2");

        var handler = new InviteToRoomHandler();
        var (cmd, ctx) = MakeCommand("INVITE_TO_ROOM",
            new() { ["agentId"] = "engineer-1", ["roomId"] = "room-2" },
            "planner-1", "Aristotle", "Planner");

        await handler.ExecuteAsync(cmd, ctx);

        // Verify system message was posted
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var messages = await db.Messages
            .Where(m => m.RoomId == "room-2" && m.Content.Contains("invited"))
            .ToListAsync();

        Assert.NotEmpty(messages);
        Assert.Contains("Hephaestus", messages[0].Content);
    }

    // ── RETURN_TO_MAIN ──────────────────────────────────────────

    [Fact]
    public async Task ReturnToMain_MovesAgentToDefaultRoom()
    {
        await EnsureRoom("room-2");

        using (var scope = _serviceProvider.CreateScope())
        {
            var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
            var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
            var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
            var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await initialization.InitializeAsync();
            await agentLocations.MoveAgentAsync("engineer-1", "room-2", AgentState.Idle);
        }

        var handler = new ReturnToMainHandler();
        var (cmd, ctx) = MakeCommand("RETURN_TO_MAIN", new());

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("main", dict["roomId"]);
        Assert.Contains("Returned", (string)dict["message"]!);
    }

    [Fact]
    public async Task ReturnToMain_AlreadyInMain_ReturnsNoOp()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
            var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
            var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
            var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await initialization.InitializeAsync();
        }

        var handler = new ReturnToMainHandler();
        var (cmd, ctx) = MakeCommand("RETURN_TO_MAIN", new());

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Contains("Already", (string)dict["message"]!);
    }

    [Fact]
    public async Task ReturnToMain_AnyRole_Allowed()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
            var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
            var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
            var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await initialization.InitializeAsync();
            await agentLocations.MoveAgentAsync("engineer-1", "main", AgentState.Idle);
            await EnsureRoom("room-2");
            await agentLocations.MoveAgentAsync("engineer-1", "room-2", AgentState.Idle);
        }

        var handler = new ReturnToMainHandler();
        var (cmd, ctx) = MakeCommand("RETURN_TO_MAIN", new(),
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string status = "Active",
        string title = "Test Task",
        string roomId = "room-1",
        string? assignedAgentId = null,
        string? assignedAgentName = null,
        string? reviewerAgentId = null)
    {
        await EnsureRoom(roomId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var taskId = $"task-{Guid.NewGuid():N}"[..20];
        db.Tasks.Add(new TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = "Test task description",
            SuccessCriteria = "It works",
            Status = status,
            Type = "Feature",
            CurrentPhase = "Implementation",
            RoomId = roomId,
            AssignedAgentId = assignedAgentId,
            AssignedAgentName = assignedAgentName ?? (assignedAgentId != null
                ? _catalog.Agents.FirstOrDefault(a => a.Id == assignedAgentId)?.Name
                : null),
            ReviewerAgentId = reviewerAgentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return taskId;
    }

    private async Task EnsureRoom(string roomId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        if (await db.Rooms.FindAsync(roomId) == null)
        {
            db.Rooms.Add(new RoomEntity
            {
                Id = roomId,
                Name = "Test Room",
                Status = "Active",
                CurrentPhase = "Implementation",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task<string> SetupRoomAndBreakout(string agentId)
    {
        await EnsureRoom("room-1");

        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        // Move agent to room first
        await agentLocations.MoveAgentAsync(agentId, "room-1", AgentState.Idle);

        // Create breakout room (moves agent to Working state)
        var breakout = await breakouts.CreateBreakoutRoomAsync("room-1", agentId, "BR: Test Work");

        return breakout.Id;
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, string> args,
        string agentId = "engineer-1",
        string agentName = "Hephaestus",
        string agentRole = "SoftwareEngineer")
    {
        var scope = _serviceProvider.CreateScope();

        var command = new CommandEnvelope(
            Command: commandName,
            Args: args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
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
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider
        );

        return (command, context);
    }

    // ── ROOM_TOPIC ─────────────────────────────────────────────

    [Fact]
    public async Task RoomTopic_SetsTopic()
    {
        await EnsureRoom("room-2");

        var handler = new RoomTopicHandler();
        var (cmd, ctx) = MakeCommand("ROOM_TOPIC",
            new() { ["roomId"] = "room-2", ["topic"] = "Sprint 4 planning" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("Sprint 4 planning", dict["topic"]);

        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var room = await rooms.GetRoomAsync("room-2");
        Assert.Equal("Sprint 4 planning", room!.Topic);
    }

    [Fact]
    public async Task RoomTopic_ClearsTopic_WhenEmpty()
    {
        await EnsureRoom("room-2");

        // First set a topic
        var handler = new RoomTopicHandler();
        var (cmd1, ctx1) = MakeCommand("ROOM_TOPIC",
            new() { ["roomId"] = "room-2", ["topic"] = "Some topic" });
        await handler.ExecuteAsync(cmd1, ctx1);

        // Then clear it
        var (cmd2, ctx2) = MakeCommand("ROOM_TOPIC",
            new() { ["roomId"] = "room-2", ["topic"] = "" });
        var result = await handler.ExecuteAsync(cmd2, ctx2);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Null(dict["topic"]);
    }

    [Fact]
    public async Task RoomTopic_MissingRoomId_ReturnsError()
    {
        var handler = new RoomTopicHandler();
        var (cmd, ctx) = MakeCommand("ROOM_TOPIC",
            new() { ["topic"] = "test" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task RoomTopic_ArchivedRoom_ReturnsError()
    {
        await EnsureRoom("room-2");

        // Archive the room first
        using (var scope = _serviceProvider.CreateScope())
        {
            var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
            var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
            var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRoomLifecycleService>();
            var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
            var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await lifecycle.CloseRoomAsync("room-2");
        }

        var handler = new RoomTopicHandler();
        var (cmd, ctx) = MakeCommand("ROOM_TOPIC",
            new() { ["roomId"] = "room-2", ["topic"] = "test" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
    }

    [Fact]
    public async Task RoomTopic_NonexistentRoom_ReturnsNotFound()
    {
        var handler = new RoomTopicHandler();
        var (cmd, ctx) = MakeCommand("ROOM_TOPIC",
            new() { ["roomId"] = "no-such-room", ["topic"] = "test" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    // ── CLEANUP_ROOMS Command ─────────────────────────────────────

    [Fact]
    public async Task CleanupRooms_Planner_CanCleanup()
    {
        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await initialization.InitializeAsync();

        // Create a task in a new room, then mark it completed directly (simulating stale room)
        var result = await taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            "Stale task", "Desc", "Criteria", null, []));
        var taskEntity = await db.Tasks.FindAsync(result.Task.Id);
        taskEntity!.Status = nameof(Shared.Models.TaskStatus.Completed);
        taskEntity.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var handler = new CleanupRoomsHandler();
        var (cmd, ctx) = MakeCommand("CLEANUP_ROOMS",
            new(),
            "planner-1", "Aristotle", "Planner");

        var cmdResult = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, cmdResult.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(cmdResult.Result);
        Assert.Equal(1, dict["archivedCount"]);
    }

    [Fact]
    public async Task CleanupRooms_NonPlanner_Denied()
    {
        var handler = new CleanupRoomsHandler();
        var (cmd, ctx) = MakeCommand("CLEANUP_ROOMS",
            new(),
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task CleanupRooms_Human_CanCleanup()
    {
        using var scope = _serviceProvider.CreateScope();
        var agentLocations = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var breakouts = scope.ServiceProvider.GetRequiredService<IBreakoutRoomService>();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();
        var rooms = scope.ServiceProvider.GetRequiredService<RoomService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        await initialization.InitializeAsync();

        var handler = new CleanupRoomsHandler();
        var (cmd, ctx) = MakeCommand("CLEANUP_ROOMS",
            new(),
            "human-1", "Human", "Human");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, dict["archivedCount"]);
    }
}
