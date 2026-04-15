using System.Diagnostics;
using AgentAcademy.Server.Commands;
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
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

[Collection("WorkspaceRuntime")]
public class TaskAssignmentWorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;
    private readonly GitService _gitService;
    private readonly IAgentExecutor _executor;
    private readonly string _repoRoot;

    public TaskAssignmentWorkflowTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _repoRoot = Path.Combine(Path.GetTempPath(), $"agent-academy-task-assignment-{Guid.NewGuid():N}");
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
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
            ]);

        _executor = Substitute.For<IAgentExecutor>();
        _executor.IsFullyOperational.Returns(true);
        _executor.IsAuthFailed.Returns(false);
        _gitService = new GitService(NullLogger<GitService>.Instance, _repoRoot);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskDependencyService>();
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
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<ConversationSessionQueryService>();
        services.AddScoped<AgentConfigService>();
        services.AddSingleton<SpecManager>();
        services.AddScoped<SprintService>();
        services.AddScoped<SprintArtifactService>();
        services.AddScoped<RoundContextLoader>();
        services.AddSingleton(_executor);
        services.AddSingleton(_gitService);
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task TaskAssignment_CreatesBreakoutRoomWithBranch()
    {
        const string assignmentResponse = """
            TASK ASSIGNMENT:
            Agent: @Hephaestus
            Title: Implement the feature
            Description: Build the new feature in a breakout room
            Acceptance Criteria:
            - Breakout room created
            - Branch created
            """;

        _executor.RunAsync(
                Arg.Is<AgentDefinition>(a => a.Role == "Planner"),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(assignmentResponse),
                Task.FromResult("PASS"));

        // Engineer returns a work report so the breakout loop completes
        _executor.RunAsync(
                Arg.Is<AgentDefinition>(a => a.Role == "SoftwareEngineer"),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                "WORK REPORT:\nStatus: COMPLETE\nFiles: none\nEvidence: Done"));

        using (var scope = _serviceProvider.CreateScope())
        {
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var messages = scope.ServiceProvider.GetRequiredService<MessageService>();
            await initialization.InitializeAsync();
            await messages.PostHumanMessageAsync("main", "Assign the backend task.");
        }

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var memoryLoader = new AgentMemoryLoader(scopeFactory, NullLogger<AgentMemoryLoader>.Instance);
        var breakoutCompletion = new BreakoutCompletionService(
            scopeFactory, _catalog, _executor, new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            memoryLoader, NullLogger<BreakoutCompletionService>.Instance);
        var breakoutLifecycle = new BreakoutLifecycleService(
            scopeFactory, _catalog, _executor, new SpecManager(),
            _gitService,
            new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo"),
            memoryLoader, breakoutCompletion,
            NullLogger<BreakoutLifecycleService>.Instance);

        var pipeline = new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance);
        var taskAssignment = new TaskAssignmentHandler(_catalog, _gitService, new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo"), breakoutLifecycle, NullLogger<TaskAssignmentHandler>.Instance);
        var turnRunner = new AgentTurnRunner(
            _executor, pipeline, taskAssignment, memoryLoader,
            scopeFactory, NullLogger<AgentTurnRunner>.Instance);

        var roundRunner = new ConversationRoundRunner(
            scopeFactory, _catalog, turnRunner,
            NullLogger<ConversationRoundRunner>.Instance);

        await roundRunner.RunRoundsAsync("main");

        // Allow fire-and-forget breakout loop to complete
        await Task.Delay(2000);

        using var assertScope = _serviceProvider.CreateScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Breakout room should have been created (may be closed by now if loop completed)
        var breakoutEntity = await db.BreakoutRooms.SingleAsync();
        Assert.StartsWith("BR: Implement the feature", breakoutEntity.Name, StringComparison.Ordinal);
        Assert.Equal("main", breakoutEntity.ParentRoomId);
        Assert.Equal("engineer-1", breakoutEntity.AssignedAgentId);

        var taskItem = await db.TaskItems.SingleAsync();
        Assert.Equal("Implement the feature", taskItem.Title);
        Assert.NotNull(taskItem.BreakoutRoomId);

        var task = await db.Tasks.SingleAsync();
        Assert.Equal("Implement the feature", task.Title);
        Assert.NotNull(task.BranchName);
        Assert.StartsWith("task/implement-the-feature-", task.BranchName, StringComparison.Ordinal);

        var systemMessages = await db.Messages
            .Where(m => m.RoomId == "main" && m.SenderId == "system")
            .Select(m => m.Content)
            .ToListAsync();
        Assert.Contains(systemMessages, content =>
            content.Contains("heading to breakout room", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(systemMessages, content =>
            content.Contains("breakout rooms are disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TaskAssignment_BranchCreationFails_NoOrphanedTask()
    {
        const string assignmentResponse = """
            TASK ASSIGNMENT:
            Agent: @Hephaestus
            Title: Build the widget
            Description: Create the new widget component
            Acceptance Criteria:
            - Widget renders correctly
            """;

        _executor.RunAsync(
                Arg.Is<AgentDefinition>(a => a.Role == "Planner"),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(assignmentResponse),
                Task.FromResult("PASS"));

        _executor.RunAsync(
                Arg.Is<AgentDefinition>(a => a.Role == "SoftwareEngineer"),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("Acknowledged"));

        // Create a mock GitService whose CreateTaskBranchAsync always throws
        var mockGitService = Substitute.ForPartsOf<GitService>(
            NullLogger<GitService>.Instance, _repoRoot, "git");
        mockGitService
            .CreateTaskBranchAsync(Arg.Any<string>())
            .Returns(Task.FromException<string>(
                new InvalidOperationException("Simulated branch creation failure")));

        using (var scope = _serviceProvider.CreateScope())
        {
            var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
            var messages = scope.ServiceProvider.GetRequiredService<MessageService>();
            await initialization.InitializeAsync();
            await messages.PostHumanMessageAsync("main", "Build the widget please.");
        }

        var scopeFactory2 = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var memoryLoader2 = new AgentMemoryLoader(scopeFactory2, NullLogger<AgentMemoryLoader>.Instance);
        var breakoutCompletion2 = new BreakoutCompletionService(
            scopeFactory2, _catalog, _executor, new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            memoryLoader2, NullLogger<BreakoutCompletionService>.Instance);
        var breakoutLifecycle2 = new BreakoutLifecycleService(
            scopeFactory2, _catalog, _executor, new SpecManager(),
            mockGitService,
            new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo"),
            memoryLoader2, breakoutCompletion2,
            NullLogger<BreakoutLifecycleService>.Instance);

        var pipeline2 = new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance);
        var taskAssignment2 = new TaskAssignmentHandler(_catalog, mockGitService, new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo"), breakoutLifecycle2, NullLogger<TaskAssignmentHandler>.Instance);
        var turnRunner2 = new AgentTurnRunner(
            _executor, pipeline2, taskAssignment2, memoryLoader2,
            scopeFactory2, NullLogger<AgentTurnRunner>.Instance);

        var roundRunner2 = new ConversationRoundRunner(
            scopeFactory2, _catalog, turnRunner2,
            NullLogger<ConversationRoundRunner>.Instance);

        await roundRunner2.RunRoundsAsync("main");

        // Allow fire-and-forget to settle
        await Task.Delay(2000);

        using var assertScope = _serviceProvider.CreateScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Breakout room should have been created then cancelled
        var breakout = await db.BreakoutRooms.SingleAsync();
        Assert.Equal("Archived", breakout.Status);
        Assert.Equal("Cancelled", breakout.CloseReason);

        // No task entity should exist — branch failed before task was persisted
        Assert.Empty(await db.Tasks.ToListAsync());

        // A system message about the failure should have been posted
        var systemMessages = await db.Messages
            .Where(m => m.RoomId == "main" && m.SenderId == "system")
            .Select(m => m.Content)
            .ToListAsync();
        Assert.Contains(systemMessages, content =>
            content.Contains("Failed to set up branch", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
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

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process for test repository");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed in {workingDirectory}: {stderr.Trim()}");

        return stdout.Trim();
    }
}
