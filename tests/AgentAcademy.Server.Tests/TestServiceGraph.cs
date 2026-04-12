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

/// <summary>
/// Reusable service-graph builder for controller tests.
/// Creates real services wired to an in-memory SQLite database.
/// Call <see cref="Dispose"/> to clean up.
/// </summary>
internal sealed class TestServiceGraph : IDisposable
{
    public SqliteConnection Connection { get; }
    public AgentAcademyDbContext Db { get; }
    public AgentCatalogOptions Catalog { get; }
    public ActivityBroadcaster ActivityBus { get; }
    public ActivityPublisher ActivityPublisher { get; }
    public IAgentExecutor Executor { get; }
    public SystemSettingsService SettingsService { get; }
    public ConversationSessionService SessionService { get; }
    public MessageService MessageService { get; }
    public AgentLocationService AgentLocationService { get; }
    public TaskQueryService TaskQueryService { get; }
    public TaskLifecycleService TaskLifecycleService { get; }
    public BreakoutRoomService BreakoutRoomService { get; }
    public RoomService RoomService { get; }
    public PlanService PlanService { get; }
    public SearchService SearchService { get; }
    public AgentConfigService AgentConfigService { get; }
    public AgentOrchestrator Orchestrator { get; }
    public TaskOrchestrationService TaskOrchestrationService { get; }
    public ProjectScanner ProjectScanner { get; }
    public IServiceScopeFactory ScopeFactory { get; }
    public LlmUsageTracker UsageTracker { get; }
    public AgentErrorTracker ErrorTracker { get; }

    public TestServiceGraph(List<AgentDefinition>? agents = null)
    {
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(Connection)
            .Options;

        Db = new AgentAcademyDbContext(options);
        Db.Database.EnsureCreated();

        Catalog = new AgentCatalogOptions("main", "Main Room", agents ?? []);
        ActivityBus = new ActivityBroadcaster();
        ActivityPublisher = new ActivityPublisher(Db, ActivityBus);
        Executor = Substitute.For<IAgentExecutor>();

        SettingsService = new SystemSettingsService(Db);
        SessionService = new ConversationSessionService(
            Db, SettingsService, Executor,
            NullLogger<ConversationSessionService>.Instance);

        MessageService = new MessageService(
            Db, NullLogger<MessageService>.Instance, Catalog,
            ActivityPublisher, SessionService);

        AgentLocationService = new AgentLocationService(Db, Catalog, ActivityPublisher);

        TaskQueryService = new TaskQueryService(
            Db, NullLogger<TaskQueryService>.Instance, Catalog);

        TaskLifecycleService = new TaskLifecycleService(
            Db, NullLogger<TaskLifecycleService>.Instance, Catalog, ActivityPublisher);

        BreakoutRoomService = new BreakoutRoomService(
            Db, NullLogger<BreakoutRoomService>.Instance, Catalog,
            ActivityPublisher, SessionService, TaskQueryService, AgentLocationService);

        RoomService = new RoomService(
            Db, NullLogger<RoomService>.Instance, Catalog,
            ActivityPublisher, SessionService, MessageService);

        PlanService = new PlanService(Db);
        SearchService = new SearchService(Db, NullLogger<SearchService>.Instance);
        AgentConfigService = new AgentConfigService(Db);

        // Build a minimal orchestrator with stub infrastructure
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(Substitute.For<IServiceProvider>());
        ScopeFactory = scopeFactory;

        TaskOrchestrationService = new TaskOrchestrationService(
            Db, NullLogger<TaskOrchestrationService>.Instance, Catalog,
            ActivityPublisher, TaskLifecycleService, RoomService,
            AgentLocationService, MessageService, BreakoutRoomService);

        ProjectScanner = new ProjectScanner();

        UsageTracker = new LlmUsageTracker(scopeFactory, NullLogger<LlmUsageTracker>.Instance);
        ErrorTracker = new AgentErrorTracker(scopeFactory, NullLogger<AgentErrorTracker>.Instance);

        var specManager = new SpecManager();
        var pipeline = new CommandPipeline(
            Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance);
        var gitService = new GitService(NullLogger<GitService>.Instance);
        var worktreeService = new WorktreeService(
            NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo");
        var memoryLoader = new AgentMemoryLoader(
            scopeFactory, NullLogger<AgentMemoryLoader>.Instance);
        var breakoutLifecycle = new BreakoutLifecycleService(
            scopeFactory, Catalog, Executor, specManager, pipeline,
            gitService, worktreeService, memoryLoader,
            NullLogger<BreakoutLifecycleService>.Instance);
        var taskAssignment = new TaskAssignmentHandler(
            Catalog, gitService, worktreeService, breakoutLifecycle,
            NullLogger<TaskAssignmentHandler>.Instance);

        Orchestrator = new AgentOrchestrator(
            scopeFactory, Catalog, Executor, ActivityBus, specManager,
            pipeline, breakoutLifecycle, taskAssignment, memoryLoader,
            NullLogger<AgentOrchestrator>.Instance);
    }

    public void Dispose()
    {
        Db.Dispose();
        Connection.Dispose();
    }
}
