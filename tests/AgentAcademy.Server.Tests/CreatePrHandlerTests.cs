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
/// Tests for the CREATE_PR command handler.
/// </summary>
[Collection("WorkspaceRuntime")]
public class CreatePrHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly GitService _gitService;
    private readonly IGitHubService _gitHubService;
    private readonly CreatePrHandler _handler;
    private readonly string _repoRoot;
    private readonly List<IServiceScope> _scopes = [];

    public CreatePrHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _repoRoot = Path.Combine(Path.GetTempPath(), $"agent-academy-pr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        InitializeRepository(_repoRoot);

        // Use NSubstitute.ForPartsOf to mock virtual PushBranchAsync while keeping real git ops
        _gitService = Substitute.ForPartsOf<GitService>(NullLogger<GitService>.Instance, _repoRoot, "git");
        _gitService.PushBranchAsync(Arg.Any<string>()).Returns(Task.CompletedTask);

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
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<RoomService>();
        services.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<CrashRecoveryService>();
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddSingleton(_gitService);
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

        _handler = new CreatePrHandler(_gitService, _gitHubService);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _connection.Dispose();
        _serviceProvider.Dispose();
        try { Directory.Delete(_repoRoot, true); } catch { }
    }

    // ── Authorization Tests ─────────────────────────────────────

    [Theory]
    [InlineData("planner-1", "Aristotle", "Planner")]
    [InlineData("reviewer-1", "Socrates", "Reviewer")]
    public async Task CreatePr_PrivilegedRoles_NotDenied(string agentId, string agentName, string role)
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");
        SetupSuccessfulPrCreation();
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId }, agentId, agentName, role);

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.NotEqual(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task CreatePr_AssignedEngineer_NotDenied()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-1");
        SetupSuccessfulPrCreation();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.NotEqual(CommandStatus.Denied, result.Status);
    }

    [Fact]
    public async Task CreatePr_UnassignedEngineer_Denied()
    {
        var taskId = await CreateTestTask(assignedAgentId: "engineer-2");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    // ── Validation Tests ────────────────────────────────────────

    [Fact]
    public async Task CreatePr_MissingTaskId_ReturnsError()
    {
        var (cmd, ctx) = MakeCommand(new(), "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!);
    }

    [Fact]
    public async Task CreatePr_TaskNotFound_ReturnsError()
    {
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = "nonexistent" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task CreatePr_NoBranch_ReturnsError()
    {
        var taskId = await CreateTestTask(branchName: null);
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("branch", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePr_AlreadyHasPr_ReturnsError()
    {
        var taskId = await CreateTestTask(prNumber: 42, prUrl: "https://github.com/test/pr/42");
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("#42", result.Error!);
    }

    [Fact]
    public async Task CreatePr_GitHubNotConfigured_ReturnsError()
    {
        _gitHubService.IsConfiguredAsync().Returns(false);
        var taskId = await CreateTestTask();
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
    public async Task CreatePr_Success_ReturnsPrInfo()
    {
        var taskId = await CreateTestTask();
        SetupSuccessfulPrCreation();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(42, result.Result!["prNumber"]);
        Assert.Equal("https://github.com/test/pr/42", result.Result["prUrl"]);
    }

    [Fact]
    public async Task CreatePr_Success_UpdatesTaskEntity()
    {
        var taskId = await CreateTestTask();
        SetupSuccessfulPrCreation();
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        await _handler.ExecuteAsync(cmd, ctx);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal(42, task!.PullRequestNumber);
        Assert.Equal("https://github.com/test/pr/42", task.PullRequestUrl);
        Assert.Equal("Open", task.PullRequestStatus);
    }

    [Fact]
    public async Task CreatePr_CustomTitleAndBody_PassedToService()
    {
        var taskId = await CreateTestTask();
        SetupSuccessfulPrCreation();
        var (cmd, ctx) = MakeCommand(
            new()
            {
                ["taskId"] = taskId,
                ["title"] = "Custom PR title",
                ["body"] = "Custom body",
                ["baseBranch"] = "main"
            },
            "planner-1", "Aristotle", "Planner");

        await _handler.ExecuteAsync(cmd, ctx);

        await _gitHubService.Received(1).CreatePullRequestAsync(
            Arg.Any<string>(), "Custom PR title", "Custom body", "main");
    }

    [Fact]
    public async Task CreatePr_GitHubFailure_ReturnsError()
    {
        var taskId = await CreateTestTask();
        _gitHubService.CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<PullRequestInfo>(x => throw new InvalidOperationException("gh failed"));
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Execution, result.ErrorCode);
        Assert.Contains("gh failed", result.Error!);
    }

    [Fact]
    public async Task CreatePr_AlreadyExistsOnGitHub_ReturnsConflict()
    {
        var taskId = await CreateTestTask();
        _gitHubService.CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<PullRequestInfo>(x => throw new InvalidOperationException(
                "gh pr create failed: a pull request already exists for this branch"));
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("already exists", result.Error!);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private void SetupSuccessfulPrCreation()
    {
        _gitHubService.CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new PullRequestInfo(42, "https://github.com/test/pr/42",
                "OPEN", "Test PR", "develop", "task/test-abc123", false));
    }

    private async Task<string> CreateTestTask(
        string status = "Active",
        string? branchName = "task/test-abc123",
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
            Status = status,
            Type = "Feature",
            CurrentPhase = "Implementation",
            RoomId = "room-1",
            BranchName = branchName,
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
            Command: "CREATE_PR",
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

    private static void InitializeRepository(string repoRoot)
    {
        RunGit(repoRoot, "init");
        RunGit(repoRoot, "config", "user.name", "Agent Academy Tests");
        RunGit(repoRoot, "config", "user.email", "tests@agent-academy.local");
        RunGit(repoRoot, "checkout", "-b", "develop");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "initial\n");
        RunGit(repoRoot, "add", "README.md");
        RunGit(repoRoot, "commit", "-m", "Initial commit");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Exception($"git {string.Join(" ", args)} failed");
    }
}
