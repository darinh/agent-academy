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
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<TaskDependencyService>();
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
}
