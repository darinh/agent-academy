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
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the POST_PR_REVIEW command handler.
/// </summary>
[Collection("WorkspaceRuntime")]
public class PostPrReviewHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IGitHubService _gitHubService;
    private readonly PostPrReviewHandler _handler;
    private readonly List<IServiceScope> _scopes = [];

    public PostPrReviewHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _gitHubService = Substitute.For<IGitHubService>();
        _gitHubService.IsConfiguredAsync().Returns(true);

        var catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["*"], [])),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["*"], [])),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false,
                    Permissions: new CommandPermissionSet(["*"], [])),
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(catalog);
        services.AddSingleton<IAgentCatalog>(catalog);
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
        services.AddSingleton(Substitute.ForPartsOf<GitService>(
            NullLogger<GitService>.Instance,
            Path.GetTempPath(), "git"));
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

        _handler = new PostPrReviewHandler(_gitHubService);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _connection.Dispose();
        _serviceProvider.Dispose();
    }

    // ── Role Gate Tests ─────────────────────────────────────────

    [Theory]
    [InlineData("planner-1", "Aristotle", "Planner")]
    [InlineData("reviewer-1", "Socrates", "Reviewer")]
    public async Task PostPrReview_PrivilegedRoles_NotDenied(string agentId, string agentName, string role)
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "LGTM" },
            agentId, agentName, role);

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.NotEqual(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task PostPrReview_HumanRole_NotDenied()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "LGTM" },
            "human", "Human", "Human");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.NotEqual(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task PostPrReview_Engineer_Denied()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "LGTM" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    // ── Validation Tests ────────────────────────────────────────

    [Fact]
    public async Task PostPrReview_MissingTaskId_ReturnsError()
    {
        var (cmd, ctx) = MakeCommand(
            new() { ["body"] = "LGTM" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!);
    }

    [Fact]
    public async Task PostPrReview_MissingBody_ReturnsError()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("body", result.Error!);
    }

    [Fact]
    public async Task PostPrReview_InvalidAction_ReturnsError()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "test", ["action"] = "INVALID_ACTION" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("INVALID_ACTION", result.Error!);
    }

    [Fact]
    public async Task PostPrReview_TaskNotFound_ReturnsError()
    {
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = "nonexistent", ["body"] = "LGTM" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task PostPrReview_NoPullRequest_ReturnsError()
    {
        var taskId = await CreateTestTask();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "LGTM" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("no pull request", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostPrReview_GitHubNotConfigured_ReturnsError()
    {
        _gitHubService.IsConfiguredAsync().Returns(false);
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "LGTM" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Execution, result.ErrorCode);
        Assert.Contains("not authenticated", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Success Tests ───────────────────────────────────────────

    [Fact]
    public async Task PostPrReview_Comment_Success()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "Looks good overall" },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Comment", result.Result!["action"]);
        await _gitHubService.Received(1).PostPrReviewAsync(42, "Looks good overall", PrReviewAction.Comment);
    }

    [Fact]
    public async Task PostPrReview_DefaultAction_IsComment()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "No action specified" },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        await _gitHubService.Received(1).PostPrReviewAsync(42, "No action specified", PrReviewAction.Comment);
    }

    [Fact]
    public async Task PostPrReview_Approve_Success()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "Ship it!", ["action"] = "APPROVE" },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Approve", result.Result!["action"]);
        await _gitHubService.Received(1).PostPrReviewAsync(42, "Ship it!", PrReviewAction.Approve);
    }

    [Fact]
    public async Task PostPrReview_RequestChanges_Success()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "Please fix the tests", ["action"] = "REQUEST_CHANGES" },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("RequestChanges", result.Result!["action"]);
        await _gitHubService.Received(1).PostPrReviewAsync(42, "Please fix the tests", PrReviewAction.RequestChanges);
    }

    [Fact]
    public async Task PostPrReview_GitHubFailure_ReturnsError()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        _gitHubService.PostPrReviewAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<PrReviewAction>())
            .Returns(x => throw new InvalidOperationException("gh review failed"));
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "LGTM" },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Execution, result.ErrorCode);
        Assert.Contains("gh review failed", result.Error!);
    }

    [Fact]
    public async Task PostPrReview_Success_ReturnsTaskIdAndPrNumber()
    {
        var taskId = await CreateTestTask(prNumber: 99, prUrl: "https://github.com/test/pr/99");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["body"] = "LGTM", ["action"] = "COMMENT" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(taskId, result.Result!["taskId"]);
        Assert.Equal(99, result.Result["prNumber"]);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string? assignedAgentId = "engineer-1",
        int? prNumber = null,
        string? prUrl = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");

        var taskId = $"task-{Guid.NewGuid():N}"[..20];
        db.Tasks.Add(new Data.Entities.TaskEntity
        {
            Id = taskId,
            Title = "Test Task",
            Description = "Test task description",
            SuccessCriteria = "It works",
            Status = "Active",
            Type = "Feature",
            CurrentPhase = "Implementation",
            RoomId = "room-1",
            BranchName = "task/test-abc123",
            AssignedAgentId = assignedAgentId,
            PullRequestNumber = prNumber,
            PullRequestUrl = prUrl,
            PullRequestStatus = prNumber.HasValue ? "Open" : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return taskId;
    }

    private static async Task EnsureRoom(AgentAcademyDbContext db, string roomId)
    {
        if (await db.Rooms.FindAsync(roomId) != null) return;
        db.Rooms.Add(new Data.Entities.RoomEntity
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

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        Dictionary<string, string> args,
        string agentId = "planner-1",
        string agentName = "Aristotle",
        string agentRole = "Planner")
    {
        var scope = _serviceProvider.CreateScope();
        _scopes.Add(scope);
        var command = new CommandEnvelope(
            Command: "POST_PR_REVIEW",
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
