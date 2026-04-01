using System.Diagnostics;
using System.Reflection;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

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
        services.AddSingleton(_catalog);
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
    public async Task TaskAssignment_KeepsWorkInMainRoom_WhenBreakoutsAreDisabled()
    {
        const string assignmentResponse = """
            TASK ASSIGNMENT:
            Agent: @Hephaestus
            Title: Disable breakout creation
            Description: Keep assigned work in the main room
            Acceptance Criteria:
            - No breakout room created
            - Branch still created
            """;

        _executor.RunAsync(
                Arg.Is<AgentDefinition>(a => a.Role == "Planner"),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(assignmentResponse),
                Task.FromResult("PASS"));

        _executor.RunAsync(
                Arg.Is<AgentDefinition>(a => a.Role == "SoftwareEngineer"),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("PASS"));

        using (var scope = _serviceProvider.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            await runtime.InitializeAsync();
            await runtime.PostHumanMessageAsync("main", "Assign the backend task.");
        }

        var orchestrator = new AgentOrchestrator(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _executor,
            _serviceProvider.GetRequiredService<ActivityBroadcaster>(),
            new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            _gitService,
            NullLogger<AgentOrchestrator>.Instance);

        await InvokeConversationRoundAsync(orchestrator, "main");

        using var assertScope = _serviceProvider.CreateScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var runtimeAfter = assertScope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        Assert.Empty(await runtimeAfter.GetBreakoutRoomsAsync("main"));

        var taskItem = await db.TaskItems.SingleAsync();
        Assert.Equal("Disable breakout creation", taskItem.Title);
        Assert.Null(taskItem.BreakoutRoomId);

        var task = await db.Tasks.SingleAsync();
        Assert.Equal("Disable breakout creation", task.Title);
        Assert.NotNull(task.BranchName);
        Assert.StartsWith("task/disable-breakout-creation-", task.BranchName, StringComparison.Ordinal);

        var engineerLocation = await runtimeAfter.GetAgentLocationAsync("engineer-1");
        Assert.NotNull(engineerLocation);
        Assert.Equal("main", engineerLocation!.RoomId);
        Assert.Equal(AgentState.Idle, engineerLocation.State);
        Assert.Null(engineerLocation.BreakoutRoomId);

        var systemMessages = await db.Messages
            .Where(m => m.RoomId == "main" && m.SenderId == "system")
            .Select(m => m.Content)
            .ToListAsync();
        Assert.Contains(systemMessages, content =>
            content.Contains("work from the main room while breakout rooms are disabled", StringComparison.Ordinal));
        Assert.DoesNotContain(systemMessages, content =>
            content.Contains("heading to breakout room", StringComparison.OrdinalIgnoreCase));

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
