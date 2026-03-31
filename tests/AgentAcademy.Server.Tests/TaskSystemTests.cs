using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
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
        services.AddSingleton(_catalog);
        services.AddScoped<WorkspaceRuntime>();
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

        var result = AgentOrchestrator.ParseTaskAssignments(content);

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

        var result = AgentOrchestrator.ParseTaskAssignments(content);

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

        var result = AgentOrchestrator.ParseTaskAssignments(content);

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

        var result = AgentOrchestrator.ParseTaskAssignments(content);

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

        var result = AgentOrchestrator.ParseTaskAssignments(content);

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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var plan = await runtime.GetPlanAsync("room-1");

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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Comment, "First");
        await runtime.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Finding, "Second");
        await runtime.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Evidence, "Third");

        var comments = await runtime.GetTaskCommentsAsync(taskId);

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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Comment, "One");
        await runtime.AddTaskCommentAsync(taskId, "engineer-1", "Hephaestus", TaskCommentType.Evidence, "Two");

        var count = await runtime.GetTaskCommentCountAsync(taskId);

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
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            await runtime.MoveAgentAsync("engineer-1", "room-1", AgentState.Idle);
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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var location = await runtime.GetAgentLocationAsync("engineer-1");

        Assert.NotNull(location);
        Assert.Equal(AgentState.Idle, location.State);
        Assert.Equal("room-1", location.RoomId);
    }

    // ── Task Type in Task Creation ──────────────────────────────

    [Fact]
    public async Task CreateTask_DefaultsToFeatureType()
    {
        await EnsureRoom("room-1");
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var result = await runtime.CreateTaskAsync(new TaskAssignmentRequest(
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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var result = await runtime.CreateTaskAsync(new TaskAssignmentRequest(
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
    public void CommandParser_RecognizesSetPlan()
    {
        var parser = new CommandParser();
        var result = parser.Parse("SET_PLAN:\n  Content: # Plan");

        Assert.Single(result.Commands);
        Assert.Equal("SET_PLAN", result.Commands[0].Command);
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
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        // Move agent to room first
        await runtime.MoveAgentAsync(agentId, "room-1", AgentState.Idle);

        // Create breakout room (moves agent to Working state)
        var breakout = await runtime.CreateBreakoutRoomAsync("room-1", agentId, "BR: Test Work");

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
}
