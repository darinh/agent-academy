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

namespace AgentAcademy.Server.Tests;

public class PullRequestSyncServiceTests
{
    // ── MapToPrStatus (pure function) ───────────────────────

    [Fact]
    public void MapToPrStatus_Merged_ReturnsMerged()
    {
        var pr = new PullRequestInfo(1, "url", "MERGED", "t", "main", "feat", IsMerged: true);
        Assert.Equal(PullRequestStatus.Merged, PullRequestSyncService.MapToPrStatus(pr));
    }

    [Fact]
    public void MapToPrStatus_Closed_ReturnsClosed()
    {
        var pr = new PullRequestInfo(1, "url", "CLOSED", "t", "main", "feat", IsMerged: false);
        Assert.Equal(PullRequestStatus.Closed, PullRequestSyncService.MapToPrStatus(pr));
    }

    [Fact]
    public void MapToPrStatus_OpenApproved_ReturnsApproved()
    {
        var pr = new PullRequestInfo(1, "url", "OPEN", "t", "main", "feat", false, "APPROVED");
        Assert.Equal(PullRequestStatus.Approved, PullRequestSyncService.MapToPrStatus(pr));
    }

    [Fact]
    public void MapToPrStatus_OpenChangesRequested_ReturnsChangesRequested()
    {
        var pr = new PullRequestInfo(1, "url", "OPEN", "t", "main", "feat", false, "CHANGES_REQUESTED");
        Assert.Equal(PullRequestStatus.ChangesRequested, PullRequestSyncService.MapToPrStatus(pr));
    }

    [Fact]
    public void MapToPrStatus_OpenNoReviewDecision_ReturnsOpen()
    {
        var pr = new PullRequestInfo(1, "url", "OPEN", "t", "main", "feat", false, null);
        Assert.Equal(PullRequestStatus.Open, PullRequestSyncService.MapToPrStatus(pr));
    }

    [Fact]
    public void MapToPrStatus_OpenEmptyReviewDecision_ReturnsOpen()
    {
        var pr = new PullRequestInfo(1, "url", "OPEN", "t", "main", "feat", false, "");
        Assert.Equal(PullRequestStatus.Open, PullRequestSyncService.MapToPrStatus(pr));
    }

    [Fact]
    public void MapToPrStatus_OpenReviewRequired_ReturnsReviewRequested()
    {
        var pr = new PullRequestInfo(1, "url", "OPEN", "t", "main", "feat", false, "REVIEW_REQUIRED");
        Assert.Equal(PullRequestStatus.ReviewRequested, PullRequestSyncService.MapToPrStatus(pr));
    }

    [Fact]
    public void MapToPrStatus_ClosedCaseInsensitive_ReturnsClosed()
    {
        var pr = new PullRequestInfo(1, "url", "closed", "t", "main", "feat", false);
        Assert.Equal(PullRequestStatus.Closed, PullRequestSyncService.MapToPrStatus(pr));
    }

    [Fact]
    public void MapToPrStatus_MergedTakesPriorityOverState()
    {
        // Edge case: IsMerged=true even if State says CLOSED
        var pr = new PullRequestInfo(1, "url", "CLOSED", "t", "main", "feat", IsMerged: true);
        Assert.Equal(PullRequestStatus.Merged, PullRequestSyncService.MapToPrStatus(pr));
    }
}

/// <summary>
/// Integration tests for PullRequestSyncService.PollOnceAsync using
/// in-memory SQLite and mocked IGitHubService.
/// </summary>
[Collection("WorkspaceRuntime")]
public class PullRequestSyncServiceIntegrationTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly IGitHubService _github;

    public PullRequestSyncServiceIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _github = Substitute.For<IGitHubService>();
        _github.IsConfiguredAsync().Returns(true);

        var sc = new ServiceCollection();
        sc.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));
        sc.AddSingleton<ActivityBroadcaster>();
        sc.AddSingleton<MessageBroadcaster>();
        sc.AddScoped<ActivityPublisher>();
        sc.AddSingleton(new AgentCatalogOptions("main", "Main Room",
            new List<AgentDefinition>
            {
                new("eng-1", "Hephaestus", "SoftwareEngineer", "Engineer",
                    "You are an engineer.", null, ["impl"], ["code"], true)
            }));
        sc.AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<AgentCatalogOptions>());
        sc.AddSingleton<ILogger<TaskQueryService>>(NullLogger<TaskQueryService>.Instance);
        sc.AddSingleton<ILogger<TaskLifecycleService>>(NullLogger<TaskLifecycleService>.Instance);
        sc.AddSingleton<ILogger<TaskDependencyService>>(NullLogger<TaskDependencyService>.Instance);
        sc.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
        sc.AddSingleton(Substitute.For<IAgentExecutor>());
        sc.AddScoped<SystemSettingsService>();
        sc.AddScoped<ConversationSessionService>();
        sc.AddScoped<TaskDependencyService>();
        sc.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        sc.AddScoped<TaskQueryService>();
        sc.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        sc.AddScoped<TaskLifecycleService>();
        sc.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        sc.AddSingleton<ILogger<MessageService>>(NullLogger<MessageService>.Instance);
        sc.AddScoped<MessageService>();
        sc.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        sc.AddSingleton<ILogger<BreakoutRoomService>>(NullLogger<BreakoutRoomService>.Instance);
        sc.AddScoped<AgentLocationService>();
            sc.AddScoped<IAgentLocationService>(sp => sp.GetRequiredService<AgentLocationService>());
        sc.AddScoped<PlanService>();
        sc.AddScoped<BreakoutRoomService>();
        sc.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
        sc.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        sc.AddScoped<TaskItemService>();
        sc.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        sc.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        sc.AddScoped<PhaseTransitionValidator>();
        sc.AddScoped<RoomService>();
        sc.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
        sc.AddScoped<RoomSnapshotBuilder>();
        sc.AddSingleton<ILogger<WorkspaceRoomService>>(NullLogger<WorkspaceRoomService>.Instance);
        sc.AddScoped<WorkspaceRoomService>();
        sc.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        sc.AddScoped<RoomLifecycleService>();
        sc.AddScoped<CrashRecoveryService>();
        sc.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        sc.AddScoped<InitializationService>();
        sc.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        sc.AddScoped<TaskOrchestrationService>();
        sc.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        sc.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        sc.AddSingleton(_github);

        _services = sc.BuildServiceProvider();

        // Initialize DB schema + workspace
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        initialization.InitializeAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private PullRequestSyncService CreateService() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(),
            _github,
            NullLogger<PullRequestSyncService>.Instance);

    private async Task<string> CreateTaskWithPr(PullRequestStatus status = PullRequestStatus.Open, int prNumber = 42)
    {
        await using var scope = _services.CreateAsyncScope();
        var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
        var taskLifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var result = await taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            Title: $"Test task PR#{prNumber}",
            Description: "Test description",
            SuccessCriteria: "Tests pass",
            RoomId: "main",
            PreferredRoles: new List<string> { "SoftwareEngineer" }));
        await taskQueries.UpdateTaskBranchAsync(result.Task.Id, $"feat/test-{prNumber}");
        await taskQueries.UpdateTaskPrAsync(result.Task.Id, $"https://github.com/test/repo/pull/{prNumber}", prNumber, status);
        return result.Task.Id;
    }

    [Fact]
    public async Task PollOnce_WhenGitHubNotConfigured_SkipsSilently()
    {
        _github.IsConfiguredAsync().Returns(false);
        var svc = CreateService();

        // Should not throw
        await svc.PollOnceAsync();

        // GetPullRequestAsync should never be called
        await _github.DidNotReceive().GetPullRequestAsync(Arg.Any<int>());
    }

    [Fact]
    public async Task PollOnce_WhenNoActivePrs_DoesNothing()
    {
        var svc = CreateService();

        await svc.PollOnceAsync();

        await _github.DidNotReceive().GetPullRequestAsync(Arg.Any<int>());
    }

    [Fact]
    public async Task PollOnce_UpdatesOpenPrToMerged()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open, 42);
        _github.GetPullRequestAsync(42).Returns(
            new PullRequestInfo(42, "url", "MERGED", "t", "main", "feat", true));

        var svc = CreateService();
        await svc.PollOnceAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Merged", entity!.PullRequestStatus);
    }

    [Fact]
    public async Task PollOnce_UpdatesOpenPrToClosed()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open, 43);
        _github.GetPullRequestAsync(43).Returns(
            new PullRequestInfo(43, "url", "CLOSED", "t", "main", "feat", false));

        var svc = CreateService();
        await svc.PollOnceAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Closed", entity!.PullRequestStatus);
    }

    [Fact]
    public async Task PollOnce_UpdatesOpenPrToApproved()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open, 44);
        _github.GetPullRequestAsync(44).Returns(
            new PullRequestInfo(44, "url", "OPEN", "t", "main", "feat", false, "APPROVED"));

        var svc = CreateService();
        await svc.PollOnceAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.Tasks.FindAsync(taskId);
        Assert.Equal("Approved", entity!.PullRequestStatus);
    }

    [Fact]
    public async Task PollOnce_UpdatesOpenPrToChangesRequested()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open, 45);
        _github.GetPullRequestAsync(45).Returns(
            new PullRequestInfo(45, "url", "OPEN", "t", "main", "feat", false, "CHANGES_REQUESTED"));

        var svc = CreateService();
        await svc.PollOnceAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity = await db.Tasks.FindAsync(taskId);
        Assert.Equal("ChangesRequested", entity!.PullRequestStatus);
    }

    [Fact]
    public async Task PollOnce_NoChangeWhenStatusSame()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open, 46);
        _github.GetPullRequestAsync(46).Returns(
            new PullRequestInfo(46, "url", "OPEN", "t", "main", "feat", false, null));

        var svc = CreateService();
        await svc.PollOnceAsync();

        // Status should still be Open — no activity event emitted
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var events = await db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.TaskPrStatusChanged))
            .ToListAsync();
        Assert.Empty(events);
    }

    [Fact]
    public async Task PollOnce_EmitsActivityEventOnChange()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open, 47);
        _github.GetPullRequestAsync(47).Returns(
            new PullRequestInfo(47, "url", "MERGED", "t", "main", "feat", true));

        var svc = CreateService();
        await svc.PollOnceAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var events = await db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.TaskPrStatusChanged))
            .ToListAsync();
        Assert.Single(events);
        Assert.Contains("Open → Merged", events[0].Message);
    }

    [Fact]
    public async Task PollOnce_SkipsMergedPrs()
    {
        await CreateTaskWithPr(PullRequestStatus.Merged, 48);

        var svc = CreateService();
        await svc.PollOnceAsync();

        await _github.DidNotReceive().GetPullRequestAsync(48);
    }

    [Fact]
    public async Task PollOnce_SkipsClosedPrs()
    {
        await CreateTaskWithPr(PullRequestStatus.Closed, 49);

        var svc = CreateService();
        await svc.PollOnceAsync();

        await _github.DidNotReceive().GetPullRequestAsync(49);
    }

    [Fact]
    public async Task PollOnce_ContinuesOnSinglePrFailure()
    {
        var taskId1 = await CreateTaskWithPr(PullRequestStatus.Open, 50);
        var taskId2 = await CreateTaskWithPr(PullRequestStatus.Open, 51);

        _github.GetPullRequestAsync(50).Throws(new InvalidOperationException("gh failed"));
        _github.GetPullRequestAsync(51).Returns(
            new PullRequestInfo(51, "url", "MERGED", "t", "main", "feat", true));

        var svc = CreateService();
        await svc.PollOnceAsync();

        // Task 1 should be unchanged (error swallowed)
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var entity1 = await db.Tasks.FindAsync(taskId1);
        Assert.Equal("Open", entity1!.PullRequestStatus);

        // Task 2 should be updated
        var entity2 = await db.Tasks.FindAsync(taskId2);
        Assert.Equal("Merged", entity2!.PullRequestStatus);
    }

    [Fact]
    public async Task PollOnce_SyncsMultiplePrs()
    {
        var taskId1 = await CreateTaskWithPr(PullRequestStatus.Open, 60);
        var taskId2 = await CreateTaskWithPr(PullRequestStatus.Open, 61);

        _github.GetPullRequestAsync(60).Returns(
            new PullRequestInfo(60, "url", "OPEN", "t", "main", "feat", false, "APPROVED"));
        _github.GetPullRequestAsync(61).Returns(
            new PullRequestInfo(61, "url", "CLOSED", "t", "main", "feat", false));

        var svc = CreateService();
        await svc.PollOnceAsync();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        Assert.Equal("Approved", (await db.Tasks.FindAsync(taskId1))!.PullRequestStatus);
        Assert.Equal("Closed", (await db.Tasks.FindAsync(taskId2))!.PullRequestStatus);
    }
}

/// <summary>
/// Tests for PR sync helper methods on TaskQueryService / TaskLifecycleService.
/// </summary>
[Collection("WorkspaceRuntime")]
public class PrSyncHelperTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly InitializationService _initialization;
    private readonly TaskOrchestrationService _taskOrchestration;
    private readonly TaskQueryService _taskQueries;
    private readonly TaskLifecycleService _taskLifecycle;
    private readonly ActivityBroadcaster _activityBus;
    private readonly ActivityPublisher _activityPublisher;

    public PrSyncHelperTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        var catalog = new AgentCatalogOptions("main", "Main Room",
            new List<AgentDefinition>
            {
                new("eng-1", "Hephaestus", "SoftwareEngineer", "Engineer",
                    "You are an engineer.", null, ["impl"], ["code"], true)
            });

        _activityBus = new ActivityBroadcaster();
        _activityPublisher = new ActivityPublisher(_db, _activityBus);
        var executor = Substitute.For<IAgentExecutor>();
        var sessionLogger = Substitute.For<ILogger<ConversationSessionService>>();
        var settingsService = new SystemSettingsService(_db);
        var sessionService = new ConversationSessionService(_db, settingsService, executor, sessionLogger);
        var taskDeps = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, _activityPublisher);
        _taskQueries = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, catalog, taskDeps);
        _taskLifecycle = new TaskLifecycleService(_db, NullLogger<TaskLifecycleService>.Instance, catalog, _activityPublisher, taskDeps);
        var agentLocations = new AgentLocationService(_db, catalog, _activityPublisher);
        var messageService = new MessageService(_db, NullLogger<MessageService>.Instance, catalog, _activityPublisher, sessionService, new MessageBroadcaster());
        var breakouts = new BreakoutRoomService(_db, NullLogger<BreakoutRoomService>.Instance, catalog, _activityPublisher, sessionService, _taskQueries, agentLocations);
        var crashRecovery = new CrashRecoveryService(_db, NullLogger<CrashRecoveryService>.Instance, breakouts, agentLocations, messageService, _activityPublisher);
        var roomService = new RoomService(_db, NullLogger<RoomService>.Instance, _activityPublisher, messageService, new RoomSnapshotBuilder(_db, catalog, new PhaseTransitionValidator(_db)), new PhaseTransitionValidator(_db));
        var roomLifecycle = new RoomLifecycleService(_db, NullLogger<RoomLifecycleService>.Instance, catalog, _activityPublisher);
        _initialization = new InitializationService(_db, NullLogger<InitializationService>.Instance, catalog, _activityPublisher, crashRecovery, roomService, new WorkspaceRoomService(_db, NullLogger<WorkspaceRoomService>.Instance, catalog, _activityPublisher));
        _taskOrchestration = new TaskOrchestrationService(_db, NullLogger<TaskOrchestrationService>.Instance, catalog, _activityPublisher, _taskLifecycle, _taskQueries, roomService, new RoomSnapshotBuilder(_db, catalog, new PhaseTransitionValidator(_db)), roomLifecycle, agentLocations, messageService, breakouts);
        _initialization.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<string> CreateTaskWithPr(PullRequestStatus status, int prNumber = 42)
    {
        var result = await _taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            Title: $"Test task PR#{prNumber}",
            Description: "desc",
            SuccessCriteria: "Tests pass",
            RoomId: "main",
            PreferredRoles: new List<string> { "SoftwareEngineer" }));
        await _taskQueries.UpdateTaskBranchAsync(result.Task.Id, $"feat/test-{prNumber}");
        await _taskQueries.UpdateTaskPrAsync(result.Task.Id, $"url/{prNumber}", prNumber, status);
        return result.Task.Id;
    }

    [Fact]
    public async Task SyncTaskPrStatusAsync_UpdatesStatus()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open);

        var result = await _taskLifecycle.SyncTaskPrStatusAsync(taskId, PullRequestStatus.Merged);

        Assert.NotNull(result);
        Assert.Equal(PullRequestStatus.Merged, result!.PullRequestStatus);
    }

    [Fact]
    public async Task SyncTaskPrStatusAsync_ReturnsNull_WhenNoChange()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open);

        var result = await _taskLifecycle.SyncTaskPrStatusAsync(taskId, PullRequestStatus.Open);

        Assert.Null(result);
    }

    [Fact]
    public async Task SyncTaskPrStatusAsync_EmitsActivityEvent()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open);
        var events = new List<ActivityEvent>();
        _activityBus.Subscribe(events.Add);

        await _taskLifecycle.SyncTaskPrStatusAsync(taskId, PullRequestStatus.Approved);

        Assert.Contains(events, e => e.Type == ActivityEventType.TaskPrStatusChanged);
        Assert.Contains(events, e => e.Message.Contains("Open → Approved"));
    }

    [Fact]
    public async Task SyncTaskPrStatusAsync_DoesNotEmitEvent_WhenNoChange()
    {
        var taskId = await CreateTaskWithPr(PullRequestStatus.Open);
        var events = new List<ActivityEvent>();
        _activityBus.Subscribe(events.Add);

        await _taskLifecycle.SyncTaskPrStatusAsync(taskId, PullRequestStatus.Open);

        Assert.DoesNotContain(events, e => e.Type == ActivityEventType.TaskPrStatusChanged);
    }

    [Fact]
    public async Task SyncTaskPrStatusAsync_ThrowsOnMissingTask()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _taskLifecycle.SyncTaskPrStatusAsync("nonexistent", PullRequestStatus.Merged));
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_ReturnsOpenPrs()
    {
        await CreateTaskWithPr(PullRequestStatus.Open, 10);
        await CreateTaskWithPr(PullRequestStatus.Approved, 11);

        var results = await _taskQueries.GetTasksWithActivePrsAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.PrNumber == 10);
        Assert.Contains(results, r => r.PrNumber == 11);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_ExcludesMergedAndClosed()
    {
        await CreateTaskWithPr(PullRequestStatus.Merged, 20);
        await CreateTaskWithPr(PullRequestStatus.Closed, 21);
        await CreateTaskWithPr(PullRequestStatus.Open, 22);

        var results = await _taskQueries.GetTasksWithActivePrsAsync();

        Assert.Single(results);
        Assert.Equal(22, results[0].PrNumber);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_ExcludesTasksWithoutPr()
    {
        // Task with no PR
        await _taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            Title: "No PR task",
            Description: "desc",
            SuccessCriteria: "pass",
            RoomId: "main",
            PreferredRoles: new List<string> { "SoftwareEngineer" }));
        await CreateTaskWithPr(PullRequestStatus.Open, 30);

        var results = await _taskQueries.GetTasksWithActivePrsAsync();

        Assert.Single(results);
        Assert.Equal(30, results[0].PrNumber);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_IncludesReviewRequested()
    {
        await CreateTaskWithPr(PullRequestStatus.ReviewRequested, 40);

        var results = await _taskQueries.GetTasksWithActivePrsAsync();

        Assert.Single(results);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_IncludesChangesRequested()
    {
        await CreateTaskWithPr(PullRequestStatus.ChangesRequested, 41);

        var results = await _taskQueries.GetTasksWithActivePrsAsync();

        Assert.Single(results);
    }
}

/// <summary>
/// Tests for ReviewDecision field on PullRequestInfo via GitHubService parsing.
/// </summary>
[Collection("ProcessIntensive")]
public class GitHubServiceReviewDecisionTests : IDisposable
{
    private readonly string _tempDir;

    public GitHubServiceReviewDecisionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gh-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "AgentAcademy.sln"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string CreateGhWrapper(string stdout, int exitCode = 0)
    {
        var stdoutFile = Path.Combine(_tempDir, $"stdout-{Guid.NewGuid():N}.txt");
        File.WriteAllText(stdoutFile, stdout);
        var wrapperPath = Path.Combine(_tempDir, $"gh-wrapper-{Guid.NewGuid():N}.sh");
        var content =
            $$"""
            #!/usr/bin/env bash
            cat '{{stdoutFile}}'
            exit {{exitCode}}
            """;

        WriteExecutableScript(wrapperPath, content);
        return wrapperPath;
    }

    private static void WriteExecutableScript(string path, string content)
    {
        if (!OperatingSystem.IsWindows())
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            };
            using var fs = new FileStream(path, options);
            fs.Write(System.Text.Encoding.UTF8.GetBytes(content));
            fs.Flush(flushToDisk: true);
        }
        else
        {
            File.WriteAllText(path, content);
        }
    }

    [Fact]
    public async Task GetPullRequestAsync_ParsesReviewDecision_Approved()
    {
        var json = """{"number":1,"url":"u","state":"OPEN","title":"t","baseRefName":"main","headRefName":"feat","mergedAt":null,"reviewDecision":"APPROVED"}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var pr = await svc.GetPullRequestAsync(1);

        Assert.Equal("APPROVED", pr.ReviewDecision);
        Assert.False(pr.IsMerged);
    }

    [Fact]
    public async Task GetPullRequestAsync_ParsesReviewDecision_ChangesRequested()
    {
        var json = """{"number":1,"url":"u","state":"OPEN","title":"t","baseRefName":"main","headRefName":"feat","mergedAt":null,"reviewDecision":"CHANGES_REQUESTED"}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var pr = await svc.GetPullRequestAsync(1);

        Assert.Equal("CHANGES_REQUESTED", pr.ReviewDecision);
    }

    [Fact]
    public async Task GetPullRequestAsync_NullReviewDecision()
    {
        var json = """{"number":1,"url":"u","state":"OPEN","title":"t","baseRefName":"main","headRefName":"feat","mergedAt":null,"reviewDecision":""}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var pr = await svc.GetPullRequestAsync(1);

        // Empty string is parsed as-is
        Assert.Equal("", pr.ReviewDecision);
    }

    [Fact]
    public async Task GetPullRequestAsync_MissingReviewDecision_ReturnsNull()
    {
        var json = """{"number":1,"url":"u","state":"OPEN","title":"t","baseRefName":"main","headRefName":"feat","mergedAt":null}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var pr = await svc.GetPullRequestAsync(1);

        Assert.Null(pr.ReviewDecision);
    }

    [Fact]
    public async Task CreatePullRequestAsync_ReviewDecisionIsNull()
    {
        // pr create doesn't return reviewDecision
        var json = """{"number":1,"url":"u","state":"OPEN","title":"t","baseRefName":"main","headRefName":"feat"}""";
        var wrapper = CreateGhWrapper(json);
        var svc = new GitHubService(NullLogger<GitHubService>.Instance, _tempDir, wrapper);

        var pr = await svc.CreatePullRequestAsync("feat", "title", "body");

        Assert.Null(pr.ReviewDecision);
    }
}
