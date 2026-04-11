using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
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
/// Tests for the GET_PR_REVIEWS command handler.
/// </summary>
[Collection("WorkspaceRuntime")]
public class GetPrReviewsHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IGitHubService _gitHubService;
    private readonly GetPrReviewsHandler _handler;
    private readonly List<IServiceScope> _scopes = [];

    public GetPrReviewsHandlerTests()
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
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(catalog);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<MessageService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<RoomService>();
        services.AddScoped<WorkspaceRuntime>();
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

        _handler = new GetPrReviewsHandler(_gitHubService);
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
    public async Task GetPrReviews_PrivilegedRoles_NotDenied(string agentId, string agentName, string role)
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        _gitHubService.GetPrReviewsAsync(42).Returns(new List<PullRequestReview>());
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            agentId, agentName, role);

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.NotEqual(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task GetPrReviews_AssignedEngineer_NotDenied()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42",
            assignedAgentId: "engineer-1");
        _gitHubService.GetPrReviewsAsync(42).Returns(new List<PullRequestReview>());
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.NotEqual(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task GetPrReviews_UnassignedEngineer_Denied()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42",
            assignedAgentId: "engineer-2");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    // ── Validation Tests ────────────────────────────────────────

    [Fact]
    public async Task GetPrReviews_MissingTaskId_ReturnsError()
    {
        var (cmd, ctx) = MakeCommand(new(), "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!);
    }

    [Fact]
    public async Task GetPrReviews_TaskNotFound_ReturnsError()
    {
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = "nonexistent" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task GetPrReviews_NoPullRequest_ReturnsError()
    {
        var taskId = await CreateTestTask();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("no pull request", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPrReviews_GitHubNotConfigured_ReturnsError()
    {
        _gitHubService.IsConfiguredAsync().Returns(false);
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Execution, result.ErrorCode);
        Assert.Contains("not authenticated", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Success Tests ───────────────────────────────────────────

    [Fact]
    public async Task GetPrReviews_EmptyReviews_ReturnsEmptyList()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        _gitHubService.GetPrReviewsAsync(42).Returns(new List<PullRequestReview>());
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(0, result.Result!["reviewCount"]);
        var reviews = (List<Dictionary<string, object?>>)result.Result["reviews"]!;
        Assert.Empty(reviews);
    }

    [Fact]
    public async Task GetPrReviews_MultipleReviews_ReturnsAll()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var submitted = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        _gitHubService.GetPrReviewsAsync(42).Returns(new List<PullRequestReview>
        {
            new("user1", "LGTM", "APPROVED", submitted),
            new("user2", "Please fix tests", "CHANGES_REQUESTED", submitted.AddHours(1)),
            new("user1", "Now it looks good", "APPROVED", submitted.AddHours(2)),
        });
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(3, result.Result!["reviewCount"]);
        var reviews = (List<Dictionary<string, object?>>)result.Result["reviews"]!;
        Assert.Equal(3, reviews.Count);
        Assert.Equal("user1", reviews[0]["author"]);
        Assert.Equal("APPROVED", reviews[0]["state"]);
        Assert.Equal("LGTM", reviews[0]["body"]);
        Assert.Equal("user2", reviews[1]["author"]);
        Assert.Equal("CHANGES_REQUESTED", reviews[1]["state"]);
    }

    [Fact]
    public async Task GetPrReviews_ReturnsTaskIdAndPrNumber()
    {
        var taskId = await CreateTestTask(prNumber: 99, prUrl: "https://github.com/test/pr/99");
        _gitHubService.GetPrReviewsAsync(99).Returns(new List<PullRequestReview>());
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(taskId, result.Result!["taskId"]);
        Assert.Equal(99, result.Result["prNumber"]);
    }

    [Fact]
    public async Task GetPrReviews_GitHubFailure_ReturnsError()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        _gitHubService.GetPrReviewsAsync(42)
            .Returns<IReadOnlyList<PullRequestReview>>(x => throw new InvalidOperationException("gh api failed"));
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Execution, result.ErrorCode);
        Assert.Contains("gh api failed", result.Error!);
    }

    [Fact]
    public async Task GetPrReviews_NullSubmittedAt_ReturnsNull()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        _gitHubService.GetPrReviewsAsync(42).Returns(new List<PullRequestReview>
        {
            new("user1", "Draft review", "COMMENTED", null),
        });
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var reviews = (List<Dictionary<string, object?>>)result.Result!["reviews"]!;
        Assert.Single(reviews);
        Assert.Null(reviews[0]["submittedAt"]);
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
            Command: "GET_PR_REVIEWS",
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
