using System.Diagnostics;
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

[Collection("WorkspaceRuntime")]
public sealed class CommitChangesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;
    private readonly GitService _gitService;
    private readonly CommitChangesHandler _handler;
    private readonly string _repoRoot;
    /// <summary>
    /// Linked git worktree off <see cref="_repoRoot"/>. P1.9 blocker D refuses
    /// COMMIT_CHANGES when the working directory is not a linked worktree, so
    /// tests run their commits from a worktree (mirroring the production path
    /// where the orchestrator routes the agent into a per-task worktree).
    /// </summary>
    private readonly string _worktreePath;

    public CommitChangesTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _repoRoot = Path.Combine(Path.GetTempPath(), $"agent-academy-commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        InitializeRepository(_repoRoot);

        // P1.9 blocker D: production routes commits through a per-task worktree;
        // the handler refuses commits against the main checkout. Provision a
        // worktree off the test repo so the existing tests exercise the same
        // shape as production.
        _worktreePath = Path.Combine(Path.GetTempPath(), $"agent-academy-commit-wt-{Guid.NewGuid():N}");
        RunGit(_repoRoot, "worktree", "add", "-b", "task/test-worktree", _worktreePath);

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Backend engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["code-write"], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["COMMIT_CHANGES", "RUN_BUILD"], [])),
                new AgentDefinition(
                    Id: "engineer-2", Name: "Athena", Role: "SoftwareEngineer",
                    Summary: "Frontend engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["code-write"], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["COMMIT_CHANGES"], [])),
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["COMMIT_CHANGES", "SHELL"], []))
            ]);

        _gitService = new GitService(NullLogger<GitService>.Instance, _repoRoot);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
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
        services.AddScoped<IPhaseTransitionValidator>(sp => sp.GetRequiredService<PhaseTransitionValidator>());
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
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddSingleton<AgentAcademy.Server.Services.AgentWatchdog.IWatchdogAgentRunner>(sp =>
            new TestDoubles.NoOpWatchdogAgentRunner(sp.GetRequiredService<IAgentExecutor>()));
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddScoped<RoomArtifactTracker>();
        services.AddScoped<IRoomArtifactTracker>(sp => sp.GetRequiredService<RoomArtifactTracker>());
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();

        using var initScope = _serviceProvider.CreateScope();
        var db = initScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        var artifactTracker = initScope.ServiceProvider.GetRequiredService<RoomArtifactTracker>();
        _handler = new CommitChangesHandler(_gitService, _serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CommitChangesHandler>.Instance);

        using var scope = _serviceProvider.CreateScope();

        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        initialization.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        // Remove the worktree first so its `.git` pointer doesn't leak orphaned
        // references in the main repo's `worktrees/` admin directory.
        try { RunGit(_repoRoot, "worktree", "remove", "--force", _worktreePath); } catch { /* best effort */ }
        if (Directory.Exists(_worktreePath))
            Directory.Delete(_worktreePath, recursive: true);
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
    }

    [Fact]
    public void CommandName_IsCommitChanges()
    {
        Assert.Equal("COMMIT_CHANGES", _handler.CommandName);
    }

    [Fact]
    public async Task CommitChanges_WithStagedFiles_Succeeds()
    {
        File.WriteAllText(Path.Combine(_worktreePath, "newfile.cs"), "public class Foo {}\n");
        RunGit(_worktreePath, "add", "newfile.cs");

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["message"] = "feat: add Foo class" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Result!["commitSha"]?.ToString()));
        Assert.Equal("feat: add Foo class", RunGit(_worktreePath, "log", "-1", "--pretty=%s"));
    }

    [Fact]
    public async Task CommitChanges_WithValueArg_Succeeds()
    {
        File.WriteAllText(Path.Combine(_worktreePath, "inline.cs"), "public class Bar {}\n");
        RunGit(_worktreePath, "add", "inline.cs");

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["value"] = "fix: inline value arg" },
            "engineer-2", "Athena", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("fix: inline value arg", RunGit(_worktreePath, "log", "-1", "--pretty=%s"));
    }

    [Fact]
    public async Task CommitChanges_MissingMessage_ReturnsError()
    {
        var (command, context) = MakeCommand(
            new Dictionary<string, object?>(),
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("message", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommitChanges_EmptyMessage_ReturnsError()
    {
        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["message"] = "   " },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task CommitChanges_MessageTooLong_ReturnsError()
    {
        var longMessage = new string('x', 5001);

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["message"] = longMessage },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("5000", result.Error!);
    }

    [Fact]
    public async Task CommitChanges_NothingStaged_ReturnsError()
    {
        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["message"] = "feat: empty commit" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
    }

    /// <summary>
    /// P1.9 blocker D: when no per-task working directory is supplied
    /// (the develop-checkout-fallback path that the orchestrator uses
    /// before the agent has a claimed task), COMMIT_CHANGES must refuse
    /// rather than silently committing to the develop checkout.
    /// </summary>
    [Fact]
    public async Task CommitChanges_NoWorkingDirectory_RefusesWithClaimTaskHint()
    {
        File.WriteAllText(Path.Combine(_worktreePath, "blocked.cs"), "// would have been committed\n");
        RunGit(_worktreePath, "add", "blocked.cs");

        var scope = _serviceProvider.CreateScope();
        var envelope = MakeEnvelope(
            new Dictionary<string, object?> { ["message"] = "feat: should be refused" },
            "engineer-1");
        // WorkingDirectory deliberately null — the pre-fix behaviour committed
        // against the GitService's main checkout. The fix turns that into a
        // user-visible refusal that names CLAIM_TASK as the next step.
        var context = new CommandContext(
            "engineer-1", "Hephaestus", "SoftwareEngineer", "main", null,
            scope.ServiceProvider, GitIdentity: null, WorkingDirectory: null);

        var result = await _handler.ExecuteAsync(envelope, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("CLAIM_TASK", result.Error!);
        Assert.Contains("commit_changes", result.Error!);
    }

    [Fact]
    public async Task CommitChanges_UsesGitIdentity()
    {
        File.WriteAllText(Path.Combine(_worktreePath, "identity.cs"), "// authored by agent\n");
        RunGit(_worktreePath, "add", "identity.cs");

        var gitIdentity = new AgentGitIdentity("Hephaestus (SoftwareEngineer)", "hephaestus@agent-academy.local");

        var scope = _serviceProvider.CreateScope();
        var envelope = MakeEnvelope(
            new Dictionary<string, object?> { ["message"] = "feat: agent-authored commit" },
            "engineer-1");
        var context = new CommandContext("engineer-1", "Hephaestus", "SoftwareEngineer", "main", null, scope.ServiceProvider, gitIdentity, WorkingDirectory: _worktreePath);

        var result = await _handler.ExecuteAsync(envelope, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Hephaestus (SoftwareEngineer)", RunGit(_worktreePath, "log", "-1", "--pretty=%an"));
        Assert.Equal("hephaestus@agent-academy.local", RunGit(_worktreePath, "log", "-1", "--pretty=%ae"));
    }

    [Fact]
    public async Task CommitChanges_AnyRole_Allowed()
    {
        // Verify planners can also use COMMIT_CHANGES
        File.WriteAllText(Path.Combine(_worktreePath, "planner.txt"), "planner commit\n");
        RunGit(_worktreePath, "add", "planner.txt");

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["message"] = "docs: planner update" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public void Authorize_CommitChanges_EngineerAllowed()
    {
        var authorizer = new CommandAuthorizer();
        var agent = _catalog.Agents.First(a => a.Id == "engineer-1");

        var envelope = MakeEnvelope(
            new Dictionary<string, object?> { ["message"] = "test" },
            "engineer-1") with { Command = "COMMIT_CHANGES" };

        var denied = authorizer.Authorize(envelope, agent);

        Assert.Null(denied); // null = authorized
    }

    [Fact]
    public void CommandParser_RecognizesCommitChanges()
    {
        var parser = new CommandParser();

        var result = parser.Parse("""
            COMMIT_CHANGES:
              message: feat: add user endpoint
            """);

        Assert.Single(result.Commands);
        Assert.Equal("COMMIT_CHANGES", result.Commands[0].Command);
        Assert.Equal("feat: add user endpoint", result.Commands[0].Args["message"]);
    }

    [Fact]
    public void CommandParser_RecognizesInlineCommitChanges()
    {
        var parser = new CommandParser();

        var result = parser.Parse("COMMIT_CHANGES: fix: correct typo in README");

        Assert.Single(result.Commands);
        Assert.Equal("COMMIT_CHANGES", result.Commands[0].Command);
        Assert.Equal("fix: correct typo in README", result.Commands[0].Args["value"]);
    }

    [Fact]
    public void CommandParser_InlineCommitChanges_WithEqualsSign_PreservesFullMessage()
    {
        var parser = new CommandParser();

        var result = parser.Parse("COMMIT_CHANGES: fix: set timeout=30s for API calls");

        Assert.Single(result.Commands);
        Assert.Equal("COMMIT_CHANGES", result.Commands[0].Command);
        Assert.Equal("fix: set timeout=30s for API calls", result.Commands[0].Args["value"]);
        Assert.False(result.Commands[0].Args.ContainsKey("timeout"));
    }

    private (CommandEnvelope Envelope, CommandContext Context) MakeCommand(
        Dictionary<string, object?> args,
        string agentId,
        string agentName,
        string role)
    {
        var scope = _serviceProvider.CreateScope();
        var envelope = MakeEnvelope(args, agentId);
        // Pass _worktreePath as WorkingDirectory so the P1.9-blocker-D refusal
        // (which rejects null WorkingDirectory) doesn't fire. The handler
        // routes through CommitInDirAsync, which is the production path
        // when an orchestrator-supplied per-agent worktree is in scope.
        var context = new CommandContext(agentId, agentName, role, "main", null, scope.ServiceProvider, GitIdentity: null, WorkingDirectory: _worktreePath);
        return (envelope, context);
    }

    private static CommandEnvelope MakeEnvelope(
        Dictionary<string, object?>? args = null,
        string executedBy = "engineer-1") =>
        new(
            Command: "COMMIT_CHANGES",
            Args: args ?? new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: executedBy);

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

    private static string RunGit(string repoRoot, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(" ", args)} failed (exit {process.ExitCode}): {stderr}");

        return stdout.Trim();
    }
}
