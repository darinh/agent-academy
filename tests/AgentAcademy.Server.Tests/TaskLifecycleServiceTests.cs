using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

[Collection("WorkspaceRuntime")]
public class TaskLifecycleServiceTests : IDisposable
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    private const string RoomId = "test-room";
    private const string WorkspacePath = "/workspace/test";

    public TaskLifecycleServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

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
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false)
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // Seed a default room
        db.Rooms.Add(new RoomEntity
        {
            Id = RoomId,
            Name = "Test Room",
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private (TaskLifecycleService Svc, AgentAcademyDbContext Db) CreateScope()
    {
        var scope = _serviceProvider.CreateScope();
        _scopes.Add(scope);
        return (
            scope.ServiceProvider.GetRequiredService<TaskLifecycleService>(),
            scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>());
    }

    private TaskEntity SeedTask(
        AgentAcademyDbContext db,
        string id = "task-1",
        string status = nameof(TaskStatus.Active),
        string? assignedAgentId = null,
        string? assignedAgentName = null,
        string? pullRequestStatus = null,
        int? pullRequestNumber = null,
        int reviewRounds = 0,
        string? mergeCommitSha = null,
        DateTime? completedAt = null,
        string? roomId = RoomId)
    {
        var entity = new TaskEntity
        {
            Id = id,
            Title = "Test Task",
            Description = "A test task",
            SuccessCriteria = "Tests pass",
            Status = status,
            Type = "Feature",
            CurrentPhase = "Planning",
            CurrentPlan = "# Plan",
            RoomId = roomId,
            WorkspacePath = WorkspacePath,
            AssignedAgentId = assignedAgentId,
            AssignedAgentName = assignedAgentName,
            PullRequestStatus = pullRequestStatus,
            PullRequestNumber = pullRequestNumber,
            ReviewerAgentId = null,
            ReviewRounds = reviewRounds,
            MergeCommitSha = mergeCommitSha,
            CompletedAt = completedAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Tasks.Add(entity);
        db.SaveChanges();
        return entity;
    }

    // ═══════════════════════════════════════════════════════════════
    // ClaimTaskAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClaimTask_Succeeds()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        var result = await svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus");

        Assert.Equal("engineer-1", result.AssignedAgentId);
        Assert.Equal("Hephaestus", result.AssignedAgentName);
    }

    [Fact]
    public async Task ClaimTask_AutoActivatesQueuedTask()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.Queued));

        var result = await svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus");

        Assert.Equal(TaskStatus.Active, result.Status);
        Assert.NotNull(result.StartedAt);
    }

    [Fact]
    public async Task ClaimTask_SameAgentReClaimIsOk()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "engineer-1", assignedAgentName: "Hephaestus");

        var result = await svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus");

        Assert.Equal("engineer-1", result.AssignedAgentId);
    }

    [Fact]
    public async Task ClaimTask_DifferentAgentThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "engineer-1", assignedAgentName: "Hephaestus");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClaimTaskAsync("task-1", "planner-1", "Aristotle"));
    }

    [Fact]
    public async Task ClaimTask_MissingTaskThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClaimTaskAsync("nonexistent", "engineer-1", "Hephaestus"));
    }

    [Fact]
    public async Task ClaimTask_LooksUpAgentNameFromCatalog()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        var result = await svc.ClaimTaskAsync("task-1", "engineer-1", "WrongName");

        // Should use catalog name, not the passed-in name
        Assert.Equal("Hephaestus", result.AssignedAgentName);
    }

    // ═══════════════════════════════════════════════════════════════
    // ReleaseTaskAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReleaseTask_Succeeds()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "engineer-1", assignedAgentName: "Hephaestus");

        var result = await svc.ReleaseTaskAsync("task-1", "engineer-1");

        Assert.Null(result.AssignedAgentId);
        Assert.Null(result.AssignedAgentName);
    }

    [Fact]
    public async Task ReleaseTask_UnclaimedTaskThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReleaseTaskAsync("task-1", "engineer-1"));
    }

    [Fact]
    public async Task ReleaseTask_WrongAgentThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "engineer-1", assignedAgentName: "Hephaestus");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReleaseTaskAsync("task-1", "planner-1"));
    }

    [Fact]
    public async Task ReleaseTask_MissingTaskThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReleaseTaskAsync("nonexistent", "engineer-1"));
    }

    // ═══════════════════════════════════════════════════════════════
    // SyncTaskPrStatusAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncPrStatus_UpdatesStatus()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, pullRequestStatus: nameof(PullRequestStatus.Open), pullRequestNumber: 42);

        var result = await svc.SyncTaskPrStatusAsync("task-1", PullRequestStatus.Merged);

        Assert.NotNull(result);
        Assert.Equal(PullRequestStatus.Merged, result!.PullRequestStatus);
    }

    [Fact]
    public async Task SyncPrStatus_ReturnsNullOnSameStatus()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, pullRequestStatus: nameof(PullRequestStatus.Open));

        var result = await svc.SyncTaskPrStatusAsync("task-1", PullRequestStatus.Open);

        Assert.Null(result);
    }

    [Fact]
    public async Task SyncPrStatus_MissingTaskThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SyncTaskPrStatusAsync("nonexistent", PullRequestStatus.Open));
    }

    // ═══════════════════════════════════════════════════════════════
    // AddTaskCommentAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddTaskComment_Succeeds()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        var result = await svc.AddTaskCommentAsync(
            "task-1", "engineer-1", "Hephaestus",
            TaskCommentType.Finding, "Found an issue");

        Assert.Equal("task-1", result.TaskId);
        Assert.Equal("engineer-1", result.AgentId);
        Assert.Equal("Hephaestus", result.AgentName);
        Assert.Equal(TaskCommentType.Finding, result.CommentType);
        Assert.Equal("Found an issue", result.Content);

        var stored = await db.TaskComments.FirstOrDefaultAsync(c => c.TaskId == "task-1");
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task AddTaskComment_MissingTaskThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AddTaskCommentAsync(
                "nonexistent", "engineer-1", "Hephaestus",
                TaskCommentType.Finding, "Found an issue"));
    }

    // ═══════════════════════════════════════════════════════════════
    // StageNewTask
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void StageNewTask_CreatesCorrectly()
    {
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Implement auth",
            Description: "Add JWT authentication",
            SuccessCriteria: "Login works",
            RoomId: null,
            PreferredRoles: ["SoftwareEngineer"],
            Type: TaskType.Feature);

        var (task, activity) = svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-1");

        Assert.Equal("Implement auth", task.Title);
        Assert.Equal("Add JWT authentication", task.Description);
        Assert.Equal(TaskStatus.Active, task.Status);
        Assert.Equal(TaskType.Feature, task.Type);
        Assert.Equal(CollaborationPhase.Planning, task.CurrentPhase);
        Assert.Equal(WorkspacePath, task.WorkspacePath);
        Assert.NotNull(activity);
    }

    [Fact]
    public void StageNewTask_GeneratesDefaultPlanWhenNoPlanProvided()
    {
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Build feature",
            Description: "Build the feature",
            SuccessCriteria: "Feature works",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Feature,
            CurrentPlan: null);

        var (task, _) = svc.StageNewTask(request, RoomId, null, false, "corr-2");

        Assert.Contains("# Build feature", task.CurrentPlan);
        Assert.Contains("## Plan", task.CurrentPlan);
    }

    [Fact]
    public void StageNewTask_UsesProvidedPlan()
    {
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Build feature",
            Description: "Build the feature",
            SuccessCriteria: "Feature works",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Feature,
            CurrentPlan: "My custom plan");

        var (task, _) = svc.StageNewTask(request, RoomId, null, false, "corr-3");

        Assert.Equal("My custom plan", task.CurrentPlan);
    }

    [Fact]
    public void StageNewTask_ThrowsOnEmptyTitle()
    {
        var (svc, _) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "",
            Description: "Some description",
            SuccessCriteria: "Criteria",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Feature);

        Assert.Throws<ArgumentException>(
            () => svc.StageNewTask(request, RoomId, null, false, "corr-4"));
    }

    [Fact]
    public void StageNewTask_ThrowsOnEmptyDescription()
    {
        var (svc, _) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Valid title",
            Description: "",
            SuccessCriteria: "Criteria",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Feature);

        Assert.Throws<ArgumentException>(
            () => svc.StageNewTask(request, RoomId, null, false, "corr-5"));
    }

    [Fact]
    public void StageNewTask_SetsCorrectInitialStatus()
    {
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "New task",
            Description: "Task description",
            SuccessCriteria: "Done",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Bug);

        var (task, _) = svc.StageNewTask(request, RoomId, null, false, "corr-6");

        Assert.Equal(TaskStatus.Active, task.Status);
        Assert.Equal(TaskType.Bug, task.Type);
    }

    [Fact]
    public void StageNewTask_StagesMessages()
    {
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "New task",
            Description: "Description",
            SuccessCriteria: "Done",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, null, false, "corr-7");

        // Staged (not saved yet) — check EF tracker
        var trackedMessages = db.ChangeTracker.Entries<MessageEntity>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        // Should have at least 2 messages: assignment + coordination
        Assert.True(trackedMessages.Count >= 2);
    }

    // ═══════════════════════════════════════════════════════════════
    // AssociateTaskWithActiveSprintAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AssociateWithSprint_LinksToActiveSprint()
    {
        var (svc, db) = CreateScope();

        // Create an active sprint for the workspace
        db.Sprints.Add(new SprintEntity
        {
            Id = "sprint-1",
            Number = 1,
            WorkspacePath = WorkspacePath,
            Status = "Active",
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        // Stage a task (adds it to the change tracker)
        var request = new TaskAssignmentRequest(
            Title: "Sprint task",
            Description: "A task in the sprint",
            SuccessCriteria: "Done",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Feature);
        var (task, _) = svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-sprint");

        await svc.AssociateTaskWithActiveSprintAsync(task.Id, WorkspacePath);

        var taskEntity = db.Tasks.Local.First(t => t.Id == task.Id);
        Assert.Equal("sprint-1", taskEntity.SprintId);
    }

    [Fact]
    public async Task AssociateWithSprint_NoOpWhenNoSprint()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        await svc.AssociateTaskWithActiveSprintAsync("task-1", WorkspacePath);

        var entity = await db.Tasks.FindAsync("task-1");
        Assert.Null(entity!.SprintId);
    }

    [Fact]
    public async Task AssociateWithSprint_NoOpWhenNullWorkspace()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        await svc.AssociateTaskWithActiveSprintAsync("task-1", null);

        var entity = await db.Tasks.FindAsync("task-1");
        Assert.Null(entity!.SprintId);
    }

    // ═══════════════════════════════════════════════════════════════
    // CompleteTaskCoreAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompleteTask_MarksCompleted()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        var (snapshot, roomId) = await svc.CompleteTaskCoreAsync("task-1", 5);

        Assert.Equal(TaskStatus.Completed, snapshot.Status);
        Assert.NotNull(snapshot.CompletedAt);
        Assert.Equal(RoomId, roomId);
    }

    [Fact]
    public async Task CompleteTask_StoresCommitCountAndTests()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        var (snapshot, _) = await svc.CompleteTaskCoreAsync(
            "task-1", 7,
            testsCreated: ["test1.cs", "test2.cs"],
            mergeCommitSha: "abc123");

        Assert.Equal(TaskStatus.Completed, snapshot.Status);

        var entity = await db.Tasks.FindAsync("task-1");
        Assert.Equal(7, entity!.CommitCount);
        Assert.Equal("abc123", entity.MergeCommitSha);
        Assert.Contains("test1.cs", entity.TestsCreated);
    }

    [Fact]
    public async Task CompleteTask_MissingTaskThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CompleteTaskCoreAsync("nonexistent", 1));
    }

    [Fact]
    public async Task CompleteTask_PublishesUnblockEvent_WhenDownstreamFullyUnblocked()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "upstream-1", status: nameof(TaskStatus.Active));
        SeedTask(db, id: "downstream-1", status: nameof(TaskStatus.Active));
        db.TaskDependencies.Add(new TaskDependencyEntity
        {
            TaskId = "downstream-1", DependsOnTaskId = "upstream-1", CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        await svc.CompleteTaskCoreAsync("upstream-1", 3);

        var unblockEvents = db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.TaskUnblocked))
            .ToList();
        Assert.Single(unblockEvents);
        Assert.Equal("downstream-1", unblockEvents[0].TaskId);
        Assert.Contains("unblocked", unblockEvents[0].Message);
    }

    [Fact]
    public async Task CompleteTask_NoUnblockEvent_WhenDownstreamHasOtherBlockers()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "upstream-1", status: nameof(TaskStatus.Active));
        SeedTask(db, id: "upstream-2", status: nameof(TaskStatus.Active));
        SeedTask(db, id: "downstream-1", status: nameof(TaskStatus.Active));
        db.TaskDependencies.Add(new TaskDependencyEntity
        {
            TaskId = "downstream-1", DependsOnTaskId = "upstream-1", CreatedAt = DateTime.UtcNow
        });
        db.TaskDependencies.Add(new TaskDependencyEntity
        {
            TaskId = "downstream-1", DependsOnTaskId = "upstream-2", CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        await svc.CompleteTaskCoreAsync("upstream-1", 3);

        var unblockEvents = db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.TaskUnblocked))
            .ToList();
        Assert.Empty(unblockEvents);
    }

    [Fact]
    public async Task CompleteTask_NoUnblockEvent_WhenNoDownstreamTasks()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        await svc.CompleteTaskCoreAsync("task-1", 5);

        var unblockEvents = db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.TaskUnblocked))
            .ToList();
        Assert.Empty(unblockEvents);
    }

    // ═══════════════════════════════════════════════════════════════
    // ResolveTaskPlanContent (static)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveTaskPlanContent_ReturnsExistingPlan()
    {
        var result = TaskLifecycleService.ResolveTaskPlanContent("Title", "Existing plan");

        Assert.Equal("Existing plan", result);
    }

    [Fact]
    public void ResolveTaskPlanContent_GeneratesDefaultPlan()
    {
        var result = TaskLifecycleService.ResolveTaskPlanContent("My Task", null);

        Assert.StartsWith("# My Task", result);
        Assert.Contains("## Plan", result);
        Assert.Contains("1. Review requirements", result);
    }

    [Fact]
    public void ResolveTaskPlanContent_GeneratesDefaultForWhitespacePlan()
    {
        var result = TaskLifecycleService.ResolveTaskPlanContent("My Task", "   ");

        Assert.StartsWith("# My Task", result);
    }

    // ═══════════════════════════════════════════════════════════════
    // ApproveTaskAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApproveTask_ApprovesInReviewTask()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.InReview));

        var result = await svc.ApproveTaskAsync("task-1", "reviewer-1");

        Assert.Equal(TaskStatus.Approved, result.Status);
    }

    [Fact]
    public async Task ApproveTask_ApprovesAwaitingValidationTask()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.AwaitingValidation));

        var result = await svc.ApproveTaskAsync("task-1", "reviewer-1");

        Assert.Equal(TaskStatus.Approved, result.Status);
    }

    [Fact]
    public async Task ApproveTask_WrongStatusThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ApproveTaskAsync("task-1", "reviewer-1"));
    }

    [Fact]
    public async Task ApproveTask_MissingTaskThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ApproveTaskAsync("nonexistent", "reviewer-1"));
    }

    [Fact]
    public async Task ApproveTask_PostsReviewMessageWithFindings()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.InReview));

        await svc.ApproveTaskAsync("task-1", "reviewer-1", "Looks great!");

        var messages = await db.Messages.Where(m => m.RoomId == RoomId).ToListAsync();
        Assert.Contains(messages, m => m.Content.Contains("Approved") && m.Content.Contains("Looks great!"));
    }

    [Fact]
    public async Task ApproveTask_IncrementsReviewRounds()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.InReview), reviewRounds: 1);

        await svc.ApproveTaskAsync("task-1", "reviewer-1");

        var entity = await db.Tasks.FindAsync("task-1");
        Assert.Equal(2, entity!.ReviewRounds);
    }

    [Fact]
    public async Task ApproveTask_ResolvesReviewerNameFromCatalog()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.InReview));

        await svc.ApproveTaskAsync("task-1", "reviewer-1", "LGTM");

        var messages = await db.Messages.Where(m => m.RoomId == RoomId).ToListAsync();
        Assert.Contains(messages, m => m.Content.Contains("Socrates"));
    }

    // ═══════════════════════════════════════════════════════════════
    // RequestChangesAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RequestChanges_Succeeds()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.InReview));

        var result = await svc.RequestChangesAsync("task-1", "reviewer-1", "Fix the tests");

        Assert.Equal(TaskStatus.ChangesRequested, result.Status);
    }

    [Fact]
    public async Task RequestChanges_WrongStatusThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RequestChangesAsync("task-1", "reviewer-1", "Fix tests"));
    }

    [Fact]
    public async Task RequestChanges_MaxReviewRoundsThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.InReview), reviewRounds: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RequestChangesAsync("task-1", "reviewer-1", "More fixes"));
    }

    [Fact]
    public async Task RequestChanges_IncrementsReviewRounds()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.InReview), reviewRounds: 2);

        await svc.RequestChangesAsync("task-1", "reviewer-1", "Fix tests");

        var entity = await db.Tasks.FindAsync("task-1");
        Assert.Equal(3, entity!.ReviewRounds);
    }

    // ═══════════════════════════════════════════════════════════════
    // RejectTaskCoreAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RejectTask_RejectsApprovedTask()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.Approved));

        var result = await svc.RejectTaskCoreAsync("task-1", "reviewer-1", "Not good enough");

        Assert.Equal(TaskStatus.ChangesRequested, result.Snapshot.Status);
        Assert.Equal("task-1", result.TaskId);
        Assert.Equal(RoomId, result.RoomId);
    }

    [Fact]
    public async Task RejectTask_RejectsCompletedTaskClearsMergeShaAndCompletedAt()
    {
        var (svc, db) = CreateScope();
        SeedTask(db,
            status: nameof(TaskStatus.Completed),
            mergeCommitSha: "sha-abc",
            completedAt: DateTime.UtcNow.AddHours(-1));

        var result = await svc.RejectTaskCoreAsync("task-1", "reviewer-1", "Found regression");

        Assert.Equal(TaskStatus.ChangesRequested, result.Snapshot.Status);

        // RejectTaskCoreAsync does NOT save, so check the tracked entity
        var entity = db.Tasks.Local.First(t => t.Id == "task-1");
        Assert.Null(entity.MergeCommitSha);
        Assert.Null(entity.CompletedAt);
    }

    [Fact]
    public async Task RejectTask_WrongStatusThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RejectTaskCoreAsync("task-1", "reviewer-1", "Reason"));
    }

    [Fact]
    public async Task RejectTask_MaxReviewRoundsThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.Approved), reviewRounds: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RejectTaskCoreAsync("task-1", "reviewer-1", "Reason"));
    }

    [Fact]
    public async Task RejectTask_ReturnsRejectTaskResult()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.Approved));

        var result = await svc.RejectTaskCoreAsync("task-1", "reviewer-1", "Reason");

        Assert.IsType<RejectTaskResult>(result);
        Assert.Equal("Socrates", result.ReviewerName);
        Assert.NotNull(result.Snapshot);
    }

    [Fact]
    public async Task RejectTask_IncrementsReviewRounds()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, status: nameof(TaskStatus.Approved), reviewRounds: 2);

        await svc.RejectTaskCoreAsync("task-1", "reviewer-1", "Reason");

        var entity = db.Tasks.Local.First(t => t.Id == "task-1");
        Assert.Equal(3, entity.ReviewRounds);
    }

    // ═══════════════════════════════════════════════════════════════
    // LinkTaskToSpecAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkTaskToSpec_CreatesNewLink()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        var result = await svc.LinkTaskToSpecAsync(
            "task-1", "spec-section-1", "engineer-1", "Hephaestus",
            "Implements", "Initial implementation");

        Assert.Equal("task-1", result.TaskId);
        Assert.Equal("spec-section-1", result.SpecSectionId);
        Assert.Equal(SpecLinkType.Implements, result.LinkType);
        Assert.Equal("Initial implementation", result.Note);

        var stored = await db.SpecTaskLinks.FirstOrDefaultAsync(l => l.TaskId == "task-1");
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task LinkTaskToSpec_UpdatesExistingLink()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        // Create initial link
        await svc.LinkTaskToSpecAsync(
            "task-1", "spec-section-1", "engineer-1", "Hephaestus", "Implements");

        // Update the same task+section pair
        var result = await svc.LinkTaskToSpecAsync(
            "task-1", "spec-section-1", "reviewer-1", "Socrates", "Fixes", "Bug fix");

        Assert.Equal(SpecLinkType.Fixes, result.LinkType);

        var links = await db.SpecTaskLinks
            .Where(l => l.TaskId == "task-1" && l.SpecSectionId == "spec-section-1")
            .ToListAsync();
        Assert.Single(links);
    }

    [Fact]
    public async Task LinkTaskToSpec_InvalidLinkTypeThrows()
    {
        var (svc, db) = CreateScope();
        SeedTask(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.LinkTaskToSpecAsync(
                "task-1", "spec-section-1", "engineer-1", "Hephaestus", "InvalidType"));
    }

    [Fact]
    public async Task LinkTaskToSpec_EmptyTaskIdThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.LinkTaskToSpecAsync(
                "", "spec-section-1", "engineer-1", "Hephaestus", "Implements"));
    }

    [Fact]
    public async Task LinkTaskToSpec_EmptySpecSectionIdThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.LinkTaskToSpecAsync(
                "task-1", "", "engineer-1", "Hephaestus", "Implements"));
    }

    [Fact]
    public async Task LinkTaskToSpec_MissingTaskThrows()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.LinkTaskToSpecAsync(
                "nonexistent", "spec-section-1", "engineer-1", "Hephaestus", "Implements"));
    }

    // ═══════════════════════════════════════════════════════════════
    // ValidLinkTypes
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Implements")]
    [InlineData("Modifies")]
    [InlineData("Fixes")]
    [InlineData("References")]
    public void ValidLinkTypes_ContainsExpectedTypes(string linkType)
    {
        Assert.Contains(linkType, TaskLifecycleService.ValidLinkTypes);
    }

    [Fact]
    public void ValidLinkTypes_HasExactlyFourEntries()
    {
        Assert.Equal(4, TaskLifecycleService.ValidLinkTypes.Count);
    }

    [Fact]
    public void ValidLinkTypes_IsCaseInsensitive()
    {
        Assert.Contains("implements", TaskLifecycleService.ValidLinkTypes);
        Assert.Contains("IMPLEMENTS", TaskLifecycleService.ValidLinkTypes);
    }

    // ═══════════════════════════════════════════════════════════════
    // Mutation-Killing: Behavioral Mutants
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClaimTask_UnknownAgent_DoesNotThrow()
    {
        // Kills L63 (FirstOrDefault → First): when agentId is not in the
        // catalog, FirstOrDefault returns null gracefully. First would throw.
        var (svc, db) = CreateScope();
        SeedTask(db);

        var result = await svc.ClaimTaskAsync("task-1", "unknown-agent", "Unknown Agent");

        Assert.Equal("unknown-agent", result.AssignedAgentId);
        Assert.Equal("Unknown Agent", result.AssignedAgentName);
    }

    [Fact]
    public async Task ClaimTask_PreservesExistingStartedAt()
    {
        // Kills L74 (??= → =): when a Queued task already has StartedAt,
        // the ??= operator preserves it. The = mutation would overwrite.
        var (svc, db) = CreateScope();
        var originalStart = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var entity = SeedTask(db, status: nameof(TaskStatus.Queued));
        entity.StartedAt = originalStart;
        db.SaveChanges();

        await svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus");

        var updated = await db.Tasks.FindAsync("task-1");
        Assert.Equal(originalStart, updated!.StartedAt);
    }

    [Fact]
    public async Task ClaimTask_PublishesTaskClaimedEvent()
    {
        // Kills L77 (statement removal): Publish for TaskClaimed must execute.
        var (svc, db) = CreateScope();
        SeedTask(db);

        await svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus");

        var events = await db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.TaskClaimed))
            .ToListAsync();
        Assert.Single(events);
        Assert.Contains("Hephaestus", events[0].Message);
        Assert.Equal("task-1", events[0].TaskId);
    }

    [Fact]
    public async Task ClaimTask_ErrorMessageShowsDisplayName()
    {
        // Kills L61 null coalescing mutations: error message should prefer
        // AssignedAgentName, falling back to AssignedAgentId.
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "other-agent", assignedAgentName: "Other Agent");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus"));

        Assert.Contains("Other Agent", ex.Message);
    }

    [Fact]
    public async Task ClaimTask_ErrorFallsBackToAgentId_WhenNameNull()
    {
        // Kills L61 null coalescing (remove right): when name is null,
        // error must show the agentId instead.
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "other-agent", assignedAgentName: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus"));

        Assert.Contains("other-agent", ex.Message);
    }

    [Fact]
    public async Task ReleaseTask_PublishesTaskReleasedEvent()
    {
        // Kills L105 (statement removal): Publish for TaskReleased must execute.
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "engineer-1", assignedAgentName: "Hephaestus");

        await svc.ReleaseTaskAsync("task-1", "engineer-1");

        var events = await db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.TaskReleased))
            .ToListAsync();
        Assert.Single(events);
        Assert.Contains("Hephaestus", events[0].Message);
    }

    [Fact]
    public async Task ReleaseTask_PersistsChanges()
    {
        // Kills L108 (statement removal of SaveChangesAsync): changes must be saved.
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "engineer-1", assignedAgentName: "Hephaestus");

        await svc.ReleaseTaskAsync("task-1", "engineer-1");

        // Verify in a fresh scope to confirm DB persistence
        var (_, db2) = CreateScope();
        var entity = await db2.Tasks.FindAsync("task-1");
        Assert.Null(entity!.AssignedAgentId);
        Assert.Null(entity.AssignedAgentName);
    }

    [Fact]
    public async Task ReleaseTask_ErrorShowsDisplayNameOrId()
    {
        // Kills L98 null coalescing mutations: wrong agent error should
        // show the display name of the actual claimant.
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "other-agent", assignedAgentName: "Other Agent");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReleaseTaskAsync("task-1", "engineer-1"));

        Assert.Contains("Other Agent", ex.Message);
    }

    [Fact]
    public async Task AddComment_PublishesActivityEvent()
    {
        // Kills L160 (statement removal): Publish for TaskCommentAdded must execute.
        var (svc, db) = CreateScope();
        SeedTask(db);

        await svc.AddTaskCommentAsync("task-1", "engineer-1", "Hephaestus",
            TaskCommentType.Finding, "Found a bug");

        var events = await db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.TaskCommentAdded))
            .ToListAsync();
        Assert.Single(events);
        Assert.Contains("finding", events[0].Message);
    }

    [Fact]
    public void StageNewTask_PublishesRoomCreatedWhenNewRoom()
    {
        // Kills L251 (negate) + L253 (statement removal): RoomCreated
        // event should only appear when isNewRoom=true.
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Test", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, WorkspacePath, isNewRoom: true, "corr-new");
        db.SaveChanges();

        var events = db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.RoomCreated))
            .ToList();
        Assert.Single(events);
    }

    [Fact]
    public void StageNewTask_DoesNotPublishRoomCreatedWhenNotNewRoom()
    {
        // Kills L251 (negate !(isNewRoom)): must NOT publish RoomCreated
        // when isNewRoom is false.
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Test", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, WorkspacePath, isNewRoom: false, "corr-old");
        db.SaveChanges();

        var events = db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.RoomCreated))
            .ToList();
        Assert.Empty(events);
    }

    [Fact]
    public void StageNewTask_PublishesPhaseChangedEvent()
    {
        // Kills L260 (statement removal): PhaseChanged event must be published.
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Test", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-phase");
        db.SaveChanges();

        var events = db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.PhaseChanged))
            .ToList();
        Assert.Single(events);
        Assert.Contains("Planning", events[0].Message);
    }

    [Fact]
    public async Task AssociateWithSprint_DoesNotMatchWrongWorkspace()
    {
        // Kills L275 (&& → ||): the query must require BOTH matching
        // workspace AND active status — not just one of them.
        var (svc, db) = CreateScope();

        // Sprint is Active but for a DIFFERENT workspace
        db.Sprints.Add(new SprintEntity
        {
            Id = "sprint-wrong",
            Number = 1,
            WorkspacePath = "/other/workspace",
            Status = "Active",
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var request = new TaskAssignmentRequest(
            Title: "Sprint task", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);
        var (task, _) = svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-ws");

        await svc.AssociateTaskWithActiveSprintAsync(task.Id, WorkspacePath);

        var taskEntity = db.Tasks.Local.First(t => t.Id == task.Id);
        Assert.Null(taskEntity.SprintId);
    }

    [Fact]
    public async Task AssociateWithSprint_DoesNotMatchInactiveSprintForWorkspace()
    {
        // Kills L275 variant: sprint at correct workspace but not Active.
        var (svc, db) = CreateScope();

        db.Sprints.Add(new SprintEntity
        {
            Id = "sprint-done",
            Number = 1,
            WorkspacePath = WorkspacePath,
            Status = "Completed",
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var request = new TaskAssignmentRequest(
            Title: "Sprint task", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);
        var (task, _) = svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-inactive");

        await svc.AssociateTaskWithActiveSprintAsync(task.Id, WorkspacePath);

        var taskEntity = db.Tasks.Local.First(t => t.Id == task.Id);
        Assert.Null(taskEntity.SprintId);
    }

    [Fact]
    public async Task ClaimTask_ActivityMessage_TruncatesLongTitle()
    {
        // Kills L361 (conditional mutation + equality mutation): Truncate
        // must shorten strings exceeding maxLength.
        var (svc, db) = CreateScope();
        var longTitle = new string('A', 200);
        var entity = SeedTask(db);
        entity.Title = longTitle;
        db.SaveChanges();

        await svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus");

        var evt = await db.ActivityEvents
            .FirstAsync(e => e.Type == nameof(ActivityEventType.TaskClaimed));
        // The title should be truncated (80 chars) with "..." suffix
        Assert.Contains("...", evt.Message);
        Assert.True(evt.Message.Length < longTitle.Length + 50);
    }

    [Fact]
    public async Task ClaimTask_ActivityMessage_DoesNotTruncateShortTitle()
    {
        // Kills L361 equality mutation (< vs <=): a title exactly at the
        // limit should NOT be truncated.
        var (svc, db) = CreateScope();
        var exactTitle = new string('B', 80);
        var entity = SeedTask(db);
        entity.Title = exactTitle;
        db.SaveChanges();

        await svc.ClaimTaskAsync("task-1", "engineer-1", "Hephaestus");

        var evt = await db.ActivityEvents
            .FirstAsync(e => e.Type == nameof(ActivityEventType.TaskClaimed));
        Assert.DoesNotContain("...", evt.Message);
        Assert.Contains(exactTitle, evt.Message);
    }

    [Fact]
    public async Task ClaimTask_BlockedByDependency_ThrowsWithBlockerInfo()
    {
        // Kills L54 string mutations: error message must contain blocker details.
        var (svc, db) = CreateScope();
        var blocker = SeedTask(db, id: "blocker-1", status: nameof(TaskStatus.Active));
        blocker.Title = "Blocker Task";
        var dependent = SeedTask(db, id: "dependent-1", status: nameof(TaskStatus.Queued));

        // Create dependency: dependent-1 depends on blocker-1
        db.TaskDependencies.Add(new TaskDependencyEntity
        {
            TaskId = "dependent-1",
            DependsOnTaskId = "blocker-1"
        });
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClaimTaskAsync("dependent-1", "engineer-1", "Hephaestus"));

        Assert.Contains("dependent-1", ex.Message);
        Assert.Contains("Blocker Task", ex.Message);
        Assert.Contains("unmet dependencies", ex.Message);
    }

    [Fact]
    public async Task ClaimTask_CatalogAgent_UsesCanonicalId()
    {
        // Kills L64 null coalescing (agent?.Id ?? agentId): when agent IS in
        // catalog, the canonical ID from catalog should be used.
        var (svc, db) = CreateScope();
        SeedTask(db);

        var result = await svc.ClaimTaskAsync("task-1", "engineer-1", "AnyName");

        // Catalog has engineer-1 → should use catalog's ID
        Assert.Equal("engineer-1", result.AssignedAgentId);
    }

    [Fact]
    public async Task ReleaseTask_ErrorFallsBackToAgentId_WhenNameNull()
    {
        // Kills L98 null coalescing (remove right) on release path.
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "other-agent", assignedAgentName: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReleaseTaskAsync("task-1", "engineer-1"));

        Assert.Contains("other-agent", ex.Message);
    }

    [Fact]
    public async Task ReleaseTask_ReleasedName_UsesDisplayNameThenFallback()
    {
        // Kills L100 null coalescing: released name should prefer
        // AssignedAgentName, fall back to agentId parameter.
        var (svc, db) = CreateScope();
        SeedTask(db, assignedAgentId: "engineer-1", assignedAgentName: null);

        await svc.ReleaseTaskAsync("task-1", "engineer-1");

        var evt = await db.ActivityEvents
            .FirstAsync(e => e.Type == nameof(ActivityEventType.TaskReleased));
        // With name=null, should use the agentId "engineer-1"
        Assert.Contains("engineer-1", evt.Message);
    }

    [Fact]
    public async Task SyncPrStatus_PublishesPrStatusChangedEvent()
    {
        // Kills L119 string mutation and L129 Publish statement removal.
        var (svc, db) = CreateScope();
        SeedTask(db, pullRequestStatus: "Open", pullRequestNumber: 42);

        await svc.SyncTaskPrStatusAsync("task-1", PullRequestStatus.Merged);

        var evt = await db.ActivityEvents
            .FirstAsync(e => e.Type == nameof(ActivityEventType.TaskPrStatusChanged));
        Assert.Contains("Open", evt.Message);
        Assert.Contains("Merged", evt.Message);
        Assert.Contains("#42", evt.Message);
    }

    [Fact]
    public async Task AddComment_GeneratesNonEmptyId()
    {
        // Kills L150 string mutation on Guid format ("N" → "").
        var (svc, db) = CreateScope();
        SeedTask(db);

        var comment = await svc.AddTaskCommentAsync(
            "task-1", "engineer-1", "Hephaestus",
            TaskCommentType.Finding, "test");

        Assert.NotEmpty(comment.Id);
        Assert.Equal(32, comment.Id.Length); // "N" format = 32 hex chars, no dashes
    }

    [Fact]
    public void StageNewTask_CreatesAssignmentMessage()
    {
        // Kills L181/L183/L186/L205/L207 string mutations in CreateMessageEntity:
        // messages must have proper sender identity.
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Test", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-msg");
        db.SaveChanges();

        var messages = db.Messages.Where(m => m.RoomId == RoomId).ToList();
        Assert.True(messages.Count >= 2); // assignment + plan messages
        foreach (var msg in messages)
        {
            Assert.Equal("system", msg.SenderId);
            Assert.Equal("System", msg.SenderName);
            Assert.Equal(nameof(MessageSenderKind.System), msg.SenderKind);
            Assert.NotNull(msg.Id);
            Assert.NotEmpty(msg.Id);
        }
    }

    [Fact]
    public void StageNewTask_AssignmentMessageContainsTitle()
    {
        // Kills L241 $"" mutation on assignment message content.
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Design the schema", Description: "Create tables", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-content");
        db.SaveChanges();

        var assignMsg = db.Messages
            .First(m => m.Kind == nameof(MessageKind.TaskAssignment) && m.RoomId == RoomId);
        Assert.Contains("Design the schema", assignMsg.Content);
        Assert.Contains("Create tables", assignMsg.Content);
    }

    [Fact]
    public void StageNewTask_PlanMessageMentionsPlanning()
    {
        // Kills L247 $"" mutation on plan message content.
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Test", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-plan-msg");
        db.SaveChanges();

        var planMsg = db.Messages
            .First(m => m.Kind == nameof(MessageKind.Coordination) && m.RoomId == RoomId);
        Assert.Contains("Planning", planMsg.Content);
    }

    [Fact]
    public void StageNewTask_ActivityEventContainsTitle()
    {
        // Kills L254/L258 $"" mutations on activity publish message.
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "Build the API", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, WorkspacePath, false, "corr-title");
        db.SaveChanges();

        var evt = db.ActivityEvents
            .First(e => e.Type == nameof(ActivityEventType.TaskCreated));
        Assert.Contains("Build the API", evt.Message);
    }

    [Fact]
    public async Task AssociateWithSprint_NullWorkspace_ReturnsEarly()
    {
        // Kills L272 statement removal: null workspace must short-circuit
        // without querying for sprints.
        var (svc, db) = CreateScope();

        var request = new TaskAssignmentRequest(
            Title: "Test", Description: "Desc", SuccessCriteria: "Done",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);
        var (task, _) = svc.StageNewTask(request, RoomId, null, false, "corr-null-ws");
        db.SaveChanges();

        // Should return early without error or matching any sprint
        await svc.AssociateTaskWithActiveSprintAsync(task.Id, null);

        var taskEntity = db.Tasks.Local.First(t => t.Id == task.Id);
        Assert.Null(taskEntity.SprintId);
    }

    [Fact]
    public async Task CompleteTask_UnblockedTaskEventContainsTitle()
    {
        // Kills L294 $"" mutation on unblock event message.
        var (svc, db) = CreateScope();
        var blocker = SeedTask(db, id: "blocker-2", status: nameof(TaskStatus.Active));
        var dependent = SeedTask(db, id: "dependent-2", status: nameof(TaskStatus.Queued));
        dependent.Title = "Downstream Feature";
        db.TaskDependencies.Add(new TaskDependencyEntity
        {
            TaskId = "dependent-2",
            DependsOnTaskId = "blocker-2"
        });
        db.SaveChanges();

        await svc.CompleteTaskCoreAsync("blocker-2", commitCount: 3);

        var evt = await db.ActivityEvents
            .FirstOrDefaultAsync(e => e.Type == nameof(ActivityEventType.TaskUnblocked));
        Assert.NotNull(evt);
        Assert.Contains("Downstream Feature", evt!.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // Mutation-coverage tests (raise score to 100% on the core partial)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClaimTask_MissingTask_ErrorMessageContainsTaskId()
    {
        // Kills L48 string mutation: error message must name the missing task.
        var (svc, _) = CreateScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClaimTaskAsync("missing-id-xyz", "engineer-1", "Hephaestus"));

        Assert.Contains("missing-id-xyz", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ClaimTask_MultipleBlockers_ErrorUsesCommaSeparator()
    {
        // Kills L54 ", " → "" separator mutation: multiple blockers must be
        // delimited so both are visible and distinguishable.
        var (svc, db) = CreateScope();
        var b1 = SeedTask(db, id: "blocker-A", status: nameof(TaskStatus.Active));
        b1.Title = "Alpha Blocker";
        var b2 = SeedTask(db, id: "blocker-B", status: nameof(TaskStatus.Active));
        b2.Title = "Beta Blocker";
        SeedTask(db, id: "dependent-multi", status: nameof(TaskStatus.Queued));
        db.TaskDependencies.Add(new TaskDependencyEntity
        {
            TaskId = "dependent-multi",
            DependsOnTaskId = "blocker-A"
        });
        db.TaskDependencies.Add(new TaskDependencyEntity
        {
            TaskId = "dependent-multi",
            DependsOnTaskId = "blocker-B"
        });
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClaimTaskAsync("dependent-multi", "engineer-1", "Hephaestus"));

        Assert.Contains("Alpha Blocker", ex.Message);
        Assert.Contains("Beta Blocker", ex.Message);
        Assert.Contains(", ", ex.Message);
    }

    [Fact]
    public async Task ReleaseTask_MissingTask_ErrorMessageContainsTaskId()
    {
        // Kills L90 string mutation: missing-task error must name the id.
        var (svc, _) = CreateScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReleaseTaskAsync("ghost-task-123", "engineer-1"));

        Assert.Contains("ghost-task-123", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ReleaseTask_Unclaimed_ErrorMentionsNotClaimed()
    {
        // Kills L93 statement removal (the throw) AND L94 string mutation:
        // unclaimed task must throw with a distinguishable "not currently
        // claimed by any agent" message — not the wrong-agent message.
        var (svc, db) = CreateScope();
        SeedTask(db); // AssignedAgentId = null

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReleaseTaskAsync("task-1", "engineer-1"));

        Assert.Contains("not currently claimed", ex.Message);
        Assert.Contains("task-1", ex.Message);
    }

    [Fact]
    public async Task SyncPrStatus_MissingTask_ErrorMessageContainsTaskId()
    {
        // Kills L119 string mutation: missing-task error must name the id.
        var (svc, _) = CreateScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SyncTaskPrStatusAsync("pr-ghost", PullRequestStatus.Open));

        Assert.Contains("pr-ghost", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task SyncPrStatus_FromNull_ActivityMessageUsesNoneFallback()
    {
        // Kills L125 string mutation "None" → "": when the task has no prior
        // PR status, the activity event must show "None" as the old value.
        var (svc, db) = CreateScope();
        SeedTask(db, pullRequestStatus: null, pullRequestNumber: 77);

        await svc.SyncTaskPrStatusAsync("task-1", PullRequestStatus.Open);

        var evt = await db.ActivityEvents
            .FirstAsync(e => e.Type == nameof(ActivityEventType.TaskPrStatusChanged));
        Assert.Contains("None", evt.Message);
        Assert.Contains("Open", evt.Message);
    }

    [Fact]
    public async Task AddTaskComment_MissingTask_ErrorMessageContainsTaskId()
    {
        // Kills L146 string mutation: missing-task error must name the id.
        var (svc, _) = CreateScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AddTaskCommentAsync(
                "comment-ghost", "engineer-1", "Hephaestus",
                TaskCommentType.Finding, "x"));

        Assert.Contains("comment-ghost", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void StageNewTask_EmptyTitle_ErrorMessageMentionsTitle()
    {
        // Kills L181 string mutation: validation error must say "Title".
        var (svc, _) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "",
            Description: "desc",
            SuccessCriteria: "c",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Feature);

        var ex = Assert.Throws<ArgumentException>(
            () => svc.StageNewTask(request, RoomId, null, false, "c"));

        Assert.Contains("Title", ex.Message);
    }

    [Fact]
    public void StageNewTask_EmptyDescription_ErrorMessageMentionsDescription()
    {
        // Kills L183 string mutation: validation error must say "Description".
        var (svc, _) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "t",
            Description: "",
            SuccessCriteria: "c",
            RoomId: null,
            PreferredRoles: [],
            Type: TaskType.Feature);

        var ex = Assert.Throws<ArgumentException>(
            () => svc.StageNewTask(request, RoomId, null, false, "c"));

        Assert.Contains("Description", ex.Message);
    }

    [Fact]
    public void StageNewTask_TaskIdUsesNFormat()
    {
        // Kills L186 string mutation "N" → "": task id must be N-format
        // (32 hex chars, no dashes), not the default D format.
        var (svc, _) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "t", Description: "d", SuccessCriteria: "c",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        var (task, _) = svc.StageNewTask(request, RoomId, null, false, "corr-id");

        Assert.Equal(32, task.Id.Length);
        Assert.DoesNotContain('-', task.Id);
    }

    [Fact]
    public void StageNewTask_SnapshotHasCanonicalValidationSummary()
    {
        // Kills L205 string mutation: initial ValidationSummary must match
        // the documented placeholder so downstream status displays are correct.
        var (svc, _) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "t", Description: "d", SuccessCriteria: "c",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        var (task, _) = svc.StageNewTask(request, RoomId, null, false, "corr-vs");

        Assert.Equal("Pending reviewer and validator feedback.", task.ValidationSummary);
    }

    [Fact]
    public void StageNewTask_SnapshotHasCanonicalImplementationSummary()
    {
        // Kills L207 string mutation: initial ImplementationSummary must match
        // the documented placeholder.
        var (svc, _) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "t", Description: "d", SuccessCriteria: "c",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        var (task, _) = svc.StageNewTask(request, RoomId, null, false, "corr-is");

        Assert.Equal("Implementation has not started yet.", task.ImplementationSummary);
    }

    [Fact]
    public void StageNewTask_RoomCreatedEvent_ContainsRoomCreatedForTaskPrefix()
    {
        // Kills L254 $"" mutation: the RoomCreated activity event message
        // must name the task so operators can trace origin.
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "New Onboarding Feature",
            Description: "d", SuccessCriteria: "c",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, null, isNewRoom: true, "corr-room");
        db.SaveChanges();

        var evt = db.ActivityEvents
            .First(e => e.Type == nameof(ActivityEventType.RoomCreated));
        Assert.Contains("Room created for task:", evt.Message);
        Assert.Contains("New Onboarding Feature", evt.Message);
    }

    [Fact]
    public async Task CompleteTask_MissingTask_ErrorMessageContainsTaskId()
    {
        // Kills L294 string mutation: missing-task error must name the id.
        var (svc, _) = CreateScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CompleteTaskCoreAsync("complete-ghost", 1));

        Assert.Contains("complete-ghost", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void StageNewTask_StagedMessagesUseNFormatIds()
    {
        // Kills L347 string mutation "N" → "" in CreateMessageEntity: message
        // ids must be N-format (32 hex chars, no dashes).
        var (svc, db) = CreateScope();
        var request = new TaskAssignmentRequest(
            Title: "t", Description: "d", SuccessCriteria: "c",
            RoomId: null, PreferredRoles: [], Type: TaskType.Feature);

        svc.StageNewTask(request, RoomId, null, false, "corr-msg-id");
        db.SaveChanges();

        var messages = db.Messages.Where(m => m.RoomId == RoomId).ToList();
        Assert.NotEmpty(messages);
        foreach (var msg in messages)
        {
            Assert.Equal(32, msg.Id.Length);
            Assert.DoesNotContain('-', msg.Id);
        }
    }
}
