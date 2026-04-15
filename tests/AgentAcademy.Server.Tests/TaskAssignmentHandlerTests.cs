using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
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
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="TaskAssignmentHandler"/>: permission gating,
/// concurrent-assignment prevention, cleanup on failure, and the happy-path
/// assignment flow.
/// </summary>
public sealed class TaskAssignmentHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly AgentCatalogOptions _catalog;
    private readonly GitService _gitService;
    private readonly WorktreeService _worktreeService;
    private readonly BreakoutLifecycleService _breakoutLifecycle;
    private readonly TaskAssignmentHandler _handler;
    private readonly string _repoRoot;

    private static readonly AgentDefinition Planner = new(
        Id: "planner-1", Name: "Aristotle", Role: "Planner",
        Summary: "Planner", StartupPrompt: "prompt", Model: null,
        CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true);

    private static readonly AgentDefinition Engineer = new(
        Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
        Summary: "Engineer", StartupPrompt: "prompt", Model: null,
        CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true);

    public TaskAssignmentHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _repoRoot = Path.Combine(Path.GetTempPath(), $"task-assign-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        InitializeRepository(_repoRoot);

        _catalog = new AgentCatalogOptions("main", "Main Room", [Planner, Engineer]);

        _gitService = new GitService(NullLogger<GitService>.Instance, _repoRoot);
        _worktreeService = new WorktreeService(
            NullLogger<WorktreeService>.Instance, repositoryRoot: _repoRoot);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(Substitute.For<IServiceProvider>());

        var executor = Substitute.For<IAgentExecutor>();
        var specManager = new SpecManager();
        var pipeline = new Commands.CommandPipeline(
            Array.Empty<Commands.ICommandHandler>(),
            NullLogger<Commands.CommandPipeline>.Instance);
        var memoryLoader = new AgentMemoryLoader(
            scopeFactory, NullLogger<AgentMemoryLoader>.Instance);
        var completion = new BreakoutCompletionService(
            scopeFactory, _catalog, executor, specManager, pipeline,
            memoryLoader, NullLogger<BreakoutCompletionService>.Instance);
        _breakoutLifecycle = new BreakoutLifecycleService(
            scopeFactory, _catalog, executor, specManager,
            _gitService, _worktreeService, memoryLoader, completion,
            NullLogger<BreakoutLifecycleService>.Instance);

        _handler = new TaskAssignmentHandler(
            _catalog, _gitService, _worktreeService, _breakoutLifecycle,
            NullLogger<TaskAssignmentHandler>.Instance);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<MessageService>();
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<RoomService>();
        services.AddScoped<RoomSnapshotBuilder>();
        services.AddScoped<WorkspaceRoomService>();
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<SystemSettingsService>();
        services.AddLogging();
        services.AddSingleton(executor);

        _provider = services.BuildServiceProvider();

        using var dbScope = _provider.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();
    }

    // ──────────────── PERMISSION GATING ────────────────

    [Fact]
    public async Task ProcessAssignment_PlannerCanCreateFeatureTask()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await CreateRoomAsync(db, "main");

        var assignment = MakeAssignment("Hephaestus", "Build API", TaskType.Feature);

        await _handler.ProcessAssignmentAsync(scope, Planner, "main", assignment);

        // If the planner is allowed, a breakout room should be created
        var breakouts = await db.BreakoutRooms.ToListAsync();
        Assert.Single(breakouts);
        Assert.Contains("Build API", breakouts[0].Name);
    }

    [Fact]
    public async Task ProcessAssignment_NonPlannerBugTask_Allowed()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await CreateRoomAsync(db, "main");

        var assignment = MakeAssignment("Hephaestus", "Fix crash", TaskType.Bug);

        await _handler.ProcessAssignmentAsync(scope, Engineer, "main", assignment);

        // Non-planner filing a bug should proceed
        var breakouts = await db.BreakoutRooms.ToListAsync();
        Assert.Single(breakouts);
    }

    [Fact]
    public async Task ProcessAssignment_NonPlannerFeatureTask_ConvertedToProposal()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await CreateRoomAsync(db, "main");

        var assignment = MakeAssignment("Hephaestus", "Add dark mode", TaskType.Feature);

        await _handler.ProcessAssignmentAsync(scope, Engineer, "main", assignment);

        // No breakout room created — it was converted to a proposal
        var breakouts = await db.BreakoutRooms.ToListAsync();
        Assert.Empty(breakouts);

        // A proposal message should be posted
        var messages = await db.Messages.Where(m => m.RoomId == "main").ToListAsync();
        Assert.Contains(messages, m => m.Content.Contains("Task proposal"));
    }

    [Fact]
    public async Task ProcessAssignment_NonPlannerChoreTask_ConvertedToProposal()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await CreateRoomAsync(db, "main");

        var assignment = MakeAssignment("Hephaestus", "Update deps", TaskType.Chore);

        await _handler.ProcessAssignmentAsync(scope, Engineer, "main", assignment);

        var breakouts = await db.BreakoutRooms.ToListAsync();
        Assert.Empty(breakouts);
    }

    // ──────────────── UNKNOWN AGENT ────────────────

    [Fact]
    public async Task ProcessAssignment_UnknownAgent_DoesNotCreateBreakout()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await CreateRoomAsync(db, "main");

        var assignment = MakeAssignment("NonExistentAgent", "Do something", TaskType.Feature);

        await _handler.ProcessAssignmentAsync(scope, Planner, "main", assignment);

        var breakouts = await db.BreakoutRooms.ToListAsync();
        Assert.Empty(breakouts);
    }

    // ──────────────── CONCURRENT BREAKOUT PREVENTION ────────────────

    [Fact]
    public async Task ProcessAssignment_AgentAlreadyWorking_SkipsWithWarning()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await CreateRoomAsync(db, "main");

        // Mark engineer as Working with an existing breakout
        var agentLocation = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        await agentLocation.MoveAgentAsync(Engineer.Id, "main", AgentState.Working, breakoutRoomId: "br-existing");

        var assignment = MakeAssignment("Hephaestus", "New task", TaskType.Feature);

        await _handler.ProcessAssignmentAsync(scope, Planner, "main", assignment);

        // No new breakout should be created
        var breakouts = await db.BreakoutRooms.ToListAsync();
        Assert.Empty(breakouts);

        // A warning message should be posted
        var messages = await db.Messages.Where(m => m.RoomId == "main").ToListAsync();
        Assert.Contains(messages, m => m.Content.Contains("already working"));
    }

    // ──────────────── ACCEPTANCE CRITERIA FORMATTING ────────────────

    [Fact]
    public async Task ProcessAssignment_IncludesCriteriaInDescription()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await CreateRoomAsync(db, "main");

        var assignment = new ParsedTaskAssignment(
            Agent: "Hephaestus",
            Title: "Add tests",
            Description: "Write unit tests",
            Criteria: ["All tests pass", "Coverage > 80%"],
            Type: TaskType.Feature);

        await _handler.ProcessAssignmentAsync(scope, Planner, "main", assignment);

        var tasks = await db.Tasks.ToListAsync();
        Assert.Single(tasks);
        Assert.Contains("All tests pass", tasks[0].Description);
        Assert.Contains("Coverage > 80%", tasks[0].Description);
    }

    // ──────────────── HELPERS ────────────────

    private static ParsedTaskAssignment MakeAssignment(string agent, string title, TaskType type)
        => new(Agent: agent, Title: title, Description: $"Desc for {title}",
               Criteria: [], Type: type);

    private static async Task CreateRoomAsync(AgentAcademyDbContext db, string roomId)
    {
        db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = "Main Room",
            Status = "Active",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static void InitializeRepository(string path)
    {
        RunGit(path, "init");
        RunGit(path, "checkout -b develop");
        File.WriteAllText(Path.Combine(path, "README.md"), "# Test");
        RunGit(path, "add .");
        RunGit(path, "commit -m \"initial\"");
    }

    private static void RunGit(string workDir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(10_000);
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {args} failed: {err}");
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_repoRoot, true); } catch { }
    }
}
