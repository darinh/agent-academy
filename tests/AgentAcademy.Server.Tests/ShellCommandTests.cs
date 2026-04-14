using System.Diagnostics;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

[Collection("WorkspaceRuntime")]
public sealed class ShellCommandTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;
    private readonly GitService _gitService;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ShellCommandHandler _handler;
    private readonly string _repoRoot;

    public ShellCommandTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _repoRoot = Path.Combine(Path.GetTempPath(), $"agent-academy-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        InitializeRepository(_repoRoot);

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["SHELL"], [])),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["SHELL"], [])),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["SHELL"], []))
            ]);

        _lifetime = Substitute.For<IHostApplicationLifetime>();
        _gitService = new GitService(NullLogger<GitService>.Instance, _repoRoot);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<MessageService>();
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
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
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
        _handler = new ShellCommandHandler(_gitService, _lifetime, NullLogger<ShellCommandHandler>.Instance);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

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
    public void Authorize_Shell_ReviewerAllowed()
    {
        var authorizer = new CommandAuthorizer();
        var agent = _catalog.Agents.First(a => a.Id == "reviewer-1");

        var denied = authorizer.Authorize(MakeEnvelope(), agent);

        Assert.Null(denied);
    }

    [Fact]
    public void Authorize_Shell_EngineerDenied_EvenWhenPermissionAllows()
    {
        var authorizer = new CommandAuthorizer();
        var agent = _catalog.Agents.First(a => a.Id == "engineer-1");

        var denied = authorizer.Authorize(MakeEnvelope(), agent);

        Assert.NotNull(denied);
        Assert.Equal(CommandStatus.Denied, denied!.Status);
        Assert.Contains("Planner, Reviewer", denied.Error!);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedOperation_ReturnsError()
    {
        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["operation"] = "rm-rf" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Unsupported", result.Error!);
    }

    [Fact]
    public async Task ExecuteAsync_GitCheckout_Succeeds()
    {
        RunGit(_repoRoot, "checkout", "-b", "task/checkout-test");
        RunGit(_repoRoot, "checkout", "develop");

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["operation"] = "git-checkout", ["branch"] = "task/checkout-test" },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("task/checkout-test", RunGit(_repoRoot, "branch", "--show-current"));
    }

    [Fact]
    public async Task ExecuteAsync_GitCheckout_MissingBranch_ReturnsError()
    {
        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["operation"] = "git-checkout" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("branch", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_GitCommit_Succeeds()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "commit.txt"), "shell command test\n");
        RunGit(_repoRoot, "add", "commit.txt");

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["operation"] = "git-commit", ["message"] = "Test shell commit" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Test shell commit", RunGit(_repoRoot, "log", "-1", "--pretty=%s"));
        Assert.False(string.IsNullOrWhiteSpace(result.Result!["commitSha"]?.ToString()));
    }

    [Fact]
    public async Task ExecuteAsync_GitCommit_UsesAgentGitIdentity()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "identity.txt"), "agent-authored\n");
        RunGit(_repoRoot, "add", "identity.txt");

        var gitIdentity = new AgentGitIdentity("Aristotle (Planner)", "aristotle@agent-academy.local");

        var scope = _serviceProvider.CreateScope();
        var envelope = MakeEnvelope(
            new Dictionary<string, object?> { ["operation"] = "git-commit", ["message"] = "Agent commit with identity" },
            "planner-1");
        var context = new CommandContext("planner-1", "Aristotle", "Planner", "main", null, scope.ServiceProvider, gitIdentity);

        var result = await _handler.ExecuteAsync(envelope, context);

        Assert.Equal(CommandStatus.Success, result.Status);

        var authorName = RunGit(_repoRoot, "log", "-1", "--pretty=%an");
        var authorEmail = RunGit(_repoRoot, "log", "-1", "--pretty=%ae");
        Assert.Equal("Aristotle (Planner)", authorName);
        Assert.Equal("aristotle@agent-academy.local", authorEmail);
    }

    [Fact]
    public async Task ExecuteAsync_GitStashPop_Succeeds()
    {
        RunGit(_repoRoot, "checkout", "-b", "task/stash-test");
        File.WriteAllText(Path.Combine(_repoRoot, "stash.txt"), "stashed change\n");
        RunGit(_repoRoot, "add", "stash.txt");
        RunGit(_repoRoot, "stash", "push", "--include-untracked", "-m", "auto-stash:task/stash-test:123");

        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["operation"] = "git-stash-pop", ["branch"] = "task/stash-test" },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("stashed change\n", File.ReadAllText(Path.Combine(_repoRoot, "stash.txt")));
    }

    [Fact]
    public async Task ExecuteAsync_GitStashPop_MissingStash_ReturnsError()
    {
        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["operation"] = "git-stash-pop", ["branch"] = "task/missing-stash" },
            "planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("no auto-stash found", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RestartServer_ReviewerAllowed()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            var (command, context) = MakeCommand(
                new Dictionary<string, object?> { ["operation"] = "restart-server", ["reason"] = "Need to recycle host" },
                "reviewer-1", "Socrates", "Reviewer");

            var result = await _handler.ExecuteAsync(command, context);

            Assert.Equal(CommandStatus.Success, result.Status);
            Assert.Equal(RestartServerHandler.RestartExitCode, Environment.ExitCode);
            Assert.Equal("Need to recycle host", result.Result!["reason"]);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonPrivilegedRole_Denied()
    {
        var (command, context) = MakeCommand(
            new Dictionary<string, object?> { ["operation"] = "git-checkout", ["branch"] = "develop" },
            "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await _handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("Planner and Reviewer", result.Error!);
    }

    [Fact]
    public void CommandParser_RecognizesShellCommand()
    {
        var parser = new CommandParser();

        var result = parser.Parse("""
            SHELL:
              Operation: git-checkout
              Branch: develop
            """);

        Assert.Single(result.Commands);
        Assert.Equal("SHELL", result.Commands[0].Command);
        Assert.Equal("git-checkout", result.Commands[0].Args["operation"]);
        Assert.Equal("develop", result.Commands[0].Args["branch"]);
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
        string executedBy = "planner-1") =>
        new(
            Command: "SHELL",
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
