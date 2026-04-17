using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
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
    public IMessageService MessageService { get; }
    public MessageBroadcaster MessageBroadcaster { get; }
    public AgentLocationService AgentLocationService { get; }
    public TaskQueryService TaskQueryService { get; }
    public TaskLifecycleService TaskLifecycleService { get; }
    public TaskDependencyService TaskDependencyService { get; }
    public IBreakoutRoomService BreakoutRoomService { get; }
    public IRoomService RoomService { get; }
    public RoomSnapshotBuilder RoomSnapshotBuilder { get; }
    public PhaseTransitionValidator PhaseTransitionValidator { get; }
    public IRoomLifecycleService RoomLifecycleService { get; }
    public WorkspaceRoomService WorkspaceRoomService { get; }
    public PlanService PlanService { get; }
    public SearchService SearchService { get; }
    public AgentConfigService AgentConfigService { get; }
    public AgentOrchestrator Orchestrator { get; }
    public TaskOrchestrationService TaskOrchestrationService { get; }
    public ProjectScanner ProjectScanner { get; }
    public WorkspaceService WorkspaceService { get; }
    public SprintScheduleService SprintScheduleService { get; }
    public IServiceScopeFactory ScopeFactory { get; }
    public LlmUsageTracker UsageTracker { get; }
    public AgentErrorTracker ErrorTracker { get; }
    public RoomArtifactTracker ArtifactTracker { get; }
    public ArtifactEvaluatorService ArtifactEvaluator { get; }
    public SpecManager SpecManager { get; }

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

        MessageBroadcaster = new MessageBroadcaster();
        MessageService = new MessageService(
            Db, NullLogger<MessageService>.Instance, Catalog,
            ActivityPublisher, SessionService, MessageBroadcaster);

        AgentLocationService = new AgentLocationService(Db, Catalog, ActivityPublisher);

        TaskDependencyService = new TaskDependencyService(
            Db, NullLogger<TaskDependencyService>.Instance, ActivityPublisher);

        TaskQueryService = new TaskQueryService(
            Db, NullLogger<TaskQueryService>.Instance, Catalog, TaskDependencyService);

        TaskLifecycleService = new TaskLifecycleService(
            Db, NullLogger<TaskLifecycleService>.Instance, Catalog, ActivityPublisher, TaskDependencyService);

        BreakoutRoomService = new BreakoutRoomService(
            Db, NullLogger<BreakoutRoomService>.Instance, Catalog,
            ActivityPublisher, SessionService, TaskQueryService, AgentLocationService);

        PhaseTransitionValidator = new PhaseTransitionValidator(Db);

        RoomSnapshotBuilder = new RoomSnapshotBuilder(Db, Catalog, PhaseTransitionValidator);

        RoomService = new RoomService(
            Db, NullLogger<RoomService>.Instance,
            ActivityPublisher, MessageService, RoomSnapshotBuilder, PhaseTransitionValidator);

        RoomLifecycleService = new RoomLifecycleService(
            Db, NullLogger<RoomLifecycleService>.Instance, Catalog, ActivityPublisher);

        WorkspaceRoomService = new WorkspaceRoomService(
            Db, NullLogger<WorkspaceRoomService>.Instance, Catalog, ActivityPublisher);

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
            ActivityPublisher, TaskLifecycleService, TaskQueryService, RoomService, RoomSnapshotBuilder,
            RoomLifecycleService, AgentLocationService, MessageService, BreakoutRoomService,
            Substitute.For<IWorktreeService>());

        ProjectScanner = new ProjectScanner();
        WorkspaceService = new WorkspaceService(Db, NullLogger<WorkspaceService>.Instance);
        SprintScheduleService = new SprintScheduleService(Db);

        UsageTracker = new LlmUsageTracker(scopeFactory, NullLogger<LlmUsageTracker>.Instance);
        ErrorTracker = new AgentErrorTracker(scopeFactory, NullLogger<AgentErrorTracker>.Instance);
        ArtifactTracker = new RoomArtifactTracker(Db, ActivityPublisher, NullLogger<RoomArtifactTracker>.Instance);
        ArtifactEvaluator = new ArtifactEvaluatorService(Db, NullLogger<ArtifactEvaluatorService>.Instance);

        SpecManager = new SpecManager();
        var pipeline = new CommandPipeline(
            Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance);
        var gitService = new GitService(NullLogger<GitService>.Instance);
        var worktreeService = new WorktreeService(
            NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/test-repo");
        var memoryLoader = new AgentMemoryLoader(
            scopeFactory, NullLogger<AgentMemoryLoader>.Instance);
        var breakoutCompletion = new BreakoutCompletionService(
            scopeFactory, Catalog, Executor, SpecManager, pipeline,
            memoryLoader, NullLogger<BreakoutCompletionService>.Instance);
        var breakoutLifecycle = new BreakoutLifecycleService(
            scopeFactory, Catalog, Executor, SpecManager,
            gitService, memoryLoader, breakoutCompletion,
            NullLogger<BreakoutLifecycleService>.Instance);
        var taskAssignment = new TaskAssignmentHandler(
            Catalog, gitService, worktreeService, breakoutLifecycle,
            NullLogger<TaskAssignmentHandler>.Instance);

        var turnRunner = new AgentTurnRunner(
            Executor, pipeline, taskAssignment, memoryLoader,
            scopeFactory, NullLogger<AgentTurnRunner>.Instance);

        Orchestrator = new AgentOrchestrator(
            scopeFactory,
            new ConversationRoundRunner(scopeFactory, Catalog, turnRunner, NullLogger<ConversationRoundRunner>.Instance),
            new DirectMessageRouter(scopeFactory, Catalog, turnRunner, NullLogger<DirectMessageRouter>.Instance),
            breakoutLifecycle,
            NullLogger<AgentOrchestrator>.Instance);
    }

    public void Dispose()
    {
        Db.Dispose();
        Connection.Dispose();
    }
}
