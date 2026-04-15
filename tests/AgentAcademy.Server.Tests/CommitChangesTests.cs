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

    public CommitChangesTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _repoRoot = Path.Combine(Path.GetTempPath(), $"agent-academy-commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        InitializeRepository(_repoRoot);

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
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<AgentLocationService>();
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
        services.AddScoped<RoomArtifactTracker>();
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
        File.WriteAllText(Path.Combine(_repoRoot, "newfile.cs"), "public class Foo {}\n");
        RunGit(_repoRoot, "add", "newfile.cs");

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["message"] = "feat: add Foo class" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Result!["commitSha"]?.ToString()));
        Assert.Equal("feat: add Foo class", RunGit(_repoRoot, "log", "-1", "--pretty=%s"));
    }

    [Fact]
    public async Task CommitChanges_WithValueArg_Succeeds()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "inline.cs"), "public class Bar {}\n");
        RunGit(_repoRoot, "add", "inline.cs");

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["value"] = "fix: inline value arg" },
            "engineer-2", "Athena", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("fix: inline value arg", RunGit(_repoRoot, "log", "-1", "--pretty=%s"));
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

    [Fact]
    public async Task CommitChanges_UsesGitIdentity()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "identity.cs"), "// authored by agent\n");
        RunGit(_repoRoot, "add", "identity.cs");

        var gitIdentity = new AgentGitIdentity("Hephaestus (SoftwareEngineer)", "hephaestus@agent-academy.local");

        var scope = _serviceProvider.CreateScope();
        var envelope = MakeEnvelope(
            new Dictionary<string, object?> { ["message"] = "feat: agent-authored commit" },
            "engineer-1");
        var context = new CommandContext("engineer-1", "Hephaestus", "SoftwareEngineer", "main", null, scope.ServiceProvider, gitIdentity);

        var result = await _handler.ExecuteAsync(envelope, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Hephaestus (SoftwareEngineer)", RunGit(_repoRoot, "log", "-1", "--pretty=%an"));
        Assert.Equal("hephaestus@agent-academy.local", RunGit(_repoRoot, "log", "-1", "--pretty=%ae"));
    }

    [Fact]
    public async Task CommitChanges_AnyRole_Allowed()
    {
        // Verify planners can also use COMMIT_CHANGES
        File.WriteAllText(Path.Combine(_repoRoot, "planner.txt"), "planner commit\n");
        RunGit(_repoRoot, "add", "planner.txt");

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
        var context = new CommandContext(agentId, agentName, role, "main", null, scope.ServiceProvider);
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
