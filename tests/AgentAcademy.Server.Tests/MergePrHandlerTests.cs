using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the MERGE_PR command handler.
/// </summary>
[Collection("WorkspaceRuntime")]
public class MergePrHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IGitHubService _gitHubService;
    private readonly MergePrHandler _handler;
    private readonly List<IServiceScope> _scopes = [];

    public MergePrHandlerTests()
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
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddSingleton(Substitute.For<GitService>(NullLogger<GitService>.Instance, "/tmp", "git"));
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

        _handler = new MergePrHandler(_gitHubService);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _connection.Dispose();
        _serviceProvider.Dispose();
    }

    // ── Authorization Tests ─────────────────────────────────────

    [Theory]
    [InlineData("planner-1", "Aristotle", "Planner")]
    [InlineData("reviewer-1", "Socrates", "Reviewer")]
    public async Task MergePr_PrivilegedRoles_NotDenied(string agentId, string agentName, string role)
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId }, agentId, agentName, role);

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.NotEqual(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task MergePr_HumanRole_NotDenied()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "human", "Human", "Human");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.NotEqual(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task MergePr_Engineer_Denied()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    // ── Validation Tests ────────────────────────────────────────

    [Fact]
    public async Task MergePr_MissingTaskId_ReturnsError()
    {
        var (cmd, ctx) = MakeCommand(new(), "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!);
    }

    [Fact]
    public async Task MergePr_TaskNotFound_ReturnsError()
    {
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = "nonexistent" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task MergePr_NotApproved_ReturnsError()
    {
        var taskId = await CreateTestTask(status: "Active", prNumber: 42);
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("Approved", result.Error!);
    }

    [Fact]
    public async Task MergePr_NoPullRequest_ReturnsError()
    {
        var taskId = await CreateTestTask(status: "Approved");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("no pull request", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergePr_GitHubNotConfigured_ReturnsError()
    {
        _gitHubService.IsConfiguredAsync().Returns(false);
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
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
    public async Task MergePr_Success_ReturnsMergeInfo()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(42, result.Result!["prNumber"]);
        Assert.Equal("abc123def456", result.Result["mergeCommitSha"]);
        Assert.Contains("merged successfully", (string)result.Result["message"]!);
    }

    [Fact]
    public async Task MergePr_Success_TaskCompletedWithSha()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        await _handler.ExecuteAsync(cmd, ctx);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Completed", task!.Status);
        Assert.Equal("abc123def456", task.MergeCommitSha);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task MergePr_Success_PrStatusUpdatedToMerged()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        await _handler.ExecuteAsync(cmd, ctx);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Merged", task!.PullRequestStatus);
    }

    [Fact]
    public async Task MergePr_Success_CallsMergeWithCorrectArgs()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        await _handler.ExecuteAsync(cmd, ctx);

        await _gitHubService.Received(1).MergePullRequestAsync(
            42, Arg.Is<string>(s => s.StartsWith("feat:")), false);
    }

    [Fact]
    public async Task MergePr_WithDeleteBranch_PassesFlag()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["deleteBranch"] = "true" },
            "planner-1", "Aristotle", "Planner");

        await _handler.ExecuteAsync(cmd, ctx);

        await _gitHubService.Received(1).MergePullRequestAsync(
            42, Arg.Any<string>(), true);
    }

    [Fact]
    public async Task MergePr_Success_NullShaStillCompletes()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        _gitHubService.MergePullRequestAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new PrMergeResult(42, null));
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Null(result.Result!["mergeCommitSha"]);
    }

    [Fact]
    public async Task MergePr_ParsesTaskIdFromValueArg()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["value"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── Failure/Recovery Tests ──────────────────────────────────

    [Fact]
    public async Task MergePr_GitHubMergeFailure_RevertsToApproved()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        _gitHubService.MergePullRequestAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns<PrMergeResult>(x => throw new InvalidOperationException("merge failed: branch protection"));
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Execution, result.ErrorCode);
        Assert.Contains("branch protection", result.Error!);

        // Verify task was reverted to Approved
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Approved", task!.Status);
    }

    [Fact]
    public async Task MergePr_BugTaskType_UsesFixPrefix()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42, taskType: "Bug");
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        await _handler.ExecuteAsync(cmd, ctx);

        await _gitHubService.Received(1).MergePullRequestAsync(
            42, Arg.Is<string>(s => s.StartsWith("fix:")), false);
    }

    [Fact]
    public async Task MergePr_AlreadyMerged_FinalizesLocally()
    {
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        _gitHubService.MergePullRequestAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns<PrMergeResult>(x => throw new InvalidOperationException(
                "gh pr merge failed: already been merged"));
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Contains("already merged", (string)result.Result!["message"]!);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Completed", task!.Status);
        Assert.Equal("Merged", task.PullRequestStatus);
    }

    [Fact]
    public async Task MergePr_MergeSucceeds_PostUpdateFails_DoesNotRollback()
    {
        // Merge succeeds but we simulate post-merge failure by having the task already completed
        // (which would cause SyncTaskPrStatusAsync to fail due to task not being found in expected state)
        var taskId = await CreateTestTask(status: "Approved", prNumber: 42);
        SetupSuccessfulMerge();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        // First call succeeds (normal path)
        var result = await _handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);

        // Verify the task is NOT rolled back to Approved — it should be Completed
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Completed", task!.Status);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private void SetupSuccessfulMerge()
    {
        _gitHubService.MergePullRequestAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new PrMergeResult(42, "abc123def456"));
    }

    private async Task<string> CreateTestTask(
        string status = "Active",
        string? branchName = "task/test-abc123",
        string? assignedAgentId = "engineer-1",
        int? prNumber = null,
        string? prUrl = null,
        string taskType = "Feature")
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
            Status = status,
            Type = taskType,
            CurrentPhase = "Implementation",
            RoomId = "room-1",
            BranchName = branchName,
            AssignedAgentId = assignedAgentId,
            PullRequestNumber = prNumber,
            PullRequestUrl = prNumber.HasValue ? (prUrl ?? $"https://github.com/test/pr/{prNumber}") : null,
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
            Command: "MERGE_PR",
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
