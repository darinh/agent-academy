using System.Diagnostics;
using System.Reflection;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
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
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
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
        services.AddScoped<CrashRecoveryService>();
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<AgentConfigService>();
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
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            await runtime.InitializeAsync();
            await runtime.PostHumanMessageAsync("main", "Assign the backend task.");
        }

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var memoryLoader = new AgentMemoryLoader(scopeFactory, NullLogger<AgentMemoryLoader>.Instance);
        var breakoutLifecycle = new BreakoutLifecycleService(
            scopeFactory, _executor, new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            _gitService,
            new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo"),
            memoryLoader,
            NullLogger<BreakoutLifecycleService>.Instance);

        var orchestrator = new AgentOrchestrator(
            scopeFactory,
            _executor,
            _serviceProvider.GetRequiredService<ActivityBroadcaster>(),
            new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            breakoutLifecycle,
            new TaskAssignmentHandler(_gitService, new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo"), breakoutLifecycle, NullLogger<TaskAssignmentHandler>.Instance),
            memoryLoader,
            NullLogger<AgentOrchestrator>.Instance);

        await InvokeConversationRoundAsync(orchestrator, "main");

        // Allow fire-and-forget breakout loop to complete
        await Task.Delay(2000);

        using var assertScope = _serviceProvider.CreateScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var runtimeAfter = assertScope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

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
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            await runtime.InitializeAsync();
            await runtime.PostHumanMessageAsync("main", "Build the widget please.");
        }

        var scopeFactory2 = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var memoryLoader2 = new AgentMemoryLoader(scopeFactory2, NullLogger<AgentMemoryLoader>.Instance);
        var breakoutLifecycle2 = new BreakoutLifecycleService(
            scopeFactory2, _executor, new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            mockGitService,
            new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo"),
            memoryLoader2,
            NullLogger<BreakoutLifecycleService>.Instance);

        var orchestrator = new AgentOrchestrator(
            scopeFactory2,
            _executor,
            _serviceProvider.GetRequiredService<ActivityBroadcaster>(),
            new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            breakoutLifecycle2,
            new TaskAssignmentHandler(mockGitService, new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo"), breakoutLifecycle2, NullLogger<TaskAssignmentHandler>.Instance),
            memoryLoader2,
            NullLogger<AgentOrchestrator>.Instance);

        await InvokeConversationRoundAsync(orchestrator, "main");

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

    private static async Task InvokeConversationRoundAsync(AgentOrchestrator orchestrator, string roomId)
    {
        var method = typeof(AgentOrchestrator).GetMethod(
            "RunConversationRoundAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RunConversationRoundAsync not found");

        var task = method.Invoke(orchestrator, [roomId]) as Task
            ?? throw new InvalidOperationException("RunConversationRoundAsync did not return a Task");

        await task;
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
