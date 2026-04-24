using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

public class TaskQueryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly AgentCatalogOptions _catalog;
    private readonly TaskQueryService _sut;

    public TaskQueryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection).Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
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

        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        var taskDeps = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, activityPublisher);
        _sut = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, _catalog, taskDeps);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private TaskEntity CreateTask(
        string id,
        string title,
        string status = "Queued",
        string? roomId = null,
        string? workspacePath = null,
        string? sprintId = null)
    {
        return new TaskEntity
        {
            Id = id,
            Title = title,
            Description = "Test description",
            SuccessCriteria = "Test criteria",
            Status = status,
            Type = "Feature",
            CurrentPhase = "Planning",
            CurrentPlan = "",
            ValidationStatus = "NotStarted",
            ValidationSummary = "",
            ImplementationStatus = "NotStarted",
            ImplementationSummary = "",
            PreferredRoles = "[]",
            FleetModels = "[]",
            TestsCreated = "[]",
            RoomId = roomId,
            WorkspacePath = workspacePath,
            SprintId = sprintId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private WorkspaceEntity CreateWorkspace(string path, bool isActive = true)
    {
        return new WorkspaceEntity
        {
            Path = path,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private RoomEntity CreateRoom(string id, string name, string? workspacePath = null)
    {
        return new RoomEntity
        {
            Id = id,
            Name = name,
            WorkspacePath = workspacePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private int _sprintNumber;

    private SprintEntity CreateSprint(string id, string workspacePath = "/ws/default")
    {
        return new SprintEntity
        {
            Id = id,
            Number = ++_sprintNumber,
            WorkspacePath = workspacePath,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private TaskCommentEntity CreateComment(
        string id,
        string taskId,
        string agentId = "engineer-1",
        string agentName = "Hephaestus",
        string commentType = "Comment",
        string content = "test comment",
        DateTime? createdAt = null)
    {
        return new TaskCommentEntity
        {
            Id = id,
            TaskId = taskId,
            AgentId = agentId,
            AgentName = agentName,
            CommentType = commentType,
            Content = content,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
    }

    private TaskEvidenceEntity CreateEvidence(
        string id,
        string taskId,
        string phase = "After",
        string checkName = "build",
        bool passed = true,
        DateTime? createdAt = null)
    {
        return new TaskEvidenceEntity
        {
            Id = id,
            TaskId = taskId,
            Phase = phase,
            CheckName = checkName,
            Tool = "bash",
            Passed = passed,
            AgentId = "engineer-1",
            AgentName = "Hephaestus",
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
    }

    private SpecTaskLinkEntity CreateSpecLink(
        string id,
        string taskId,
        string specSectionId,
        string linkType = "Implements",
        DateTime? createdAt = null)
    {
        return new SpecTaskLinkEntity
        {
            Id = id,
            TaskId = taskId,
            SpecSectionId = specSectionId,
            LinkType = linkType,
            LinkedByAgentId = "engineer-1",
            LinkedByAgentName = "Hephaestus",
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // GetTasksAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTasksAsync_NoTasks_ReturnsEmptyList()
    {
        var result = await _sut.GetTasksAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTasksAsync_NoActiveWorkspace_ReturnsAllTasks()
    {
        _db.Tasks.AddRange(
            CreateTask("t1", "Task One"),
            CreateTask("t2", "Task Two"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksAsync();
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetTasksAsync_ActiveWorkspace_FiltersToWorkspaceTasks()
    {
        _db.Workspaces.Add(CreateWorkspace("/ws/active"));
        _db.Tasks.AddRange(
            CreateTask("t1", "In workspace", workspacePath: "/ws/active"),
            CreateTask("t2", "Other workspace", workspacePath: "/ws/other"),
            CreateTask("t3", "No workspace"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksAsync();
        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    [Fact]
    public async Task GetTasksAsync_ActiveWorkspace_IncludesRoomScopedTasks()
    {
        _db.Workspaces.Add(CreateWorkspace("/ws/active"));
        _db.Rooms.Add(CreateRoom("room-1", "Room One", "/ws/active"));
        _db.Tasks.AddRange(
            CreateTask("t1", "Direct workspace", workspacePath: "/ws/active"),
            CreateTask("t2", "Via room", roomId: "room-1"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksAsync();
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetTasksAsync_ActiveWorkspace_ExcludesRoomFromOtherWorkspace()
    {
        _db.Workspaces.Add(CreateWorkspace("/ws/active"));
        _db.Rooms.Add(CreateRoom("room-other", "Other Room", "/ws/other"));
        _db.Tasks.Add(CreateTask("t1", "Wrong workspace room", roomId: "room-other"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTasksAsync_SprintFilter_ReturnsOnlyMatchingSprint()
    {
        _db.Sprints.AddRange(
            CreateSprint("sprint-a", "/ws/a"),
            CreateSprint("sprint-b", "/ws/b"));
        _db.Tasks.AddRange(
            CreateTask("t1", "Sprint A", sprintId: "sprint-a"),
            CreateTask("t2", "Sprint B", sprintId: "sprint-b"),
            CreateTask("t3", "No sprint"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksAsync("sprint-a");
        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    [Fact]
    public async Task GetTasksAsync_OrderedByCreatedAtDescending()
    {
        var older = CreateTask("t-old", "Older");
        older.CreatedAt = DateTime.UtcNow.AddHours(-2);
        var newer = CreateTask("t-new", "Newer");
        newer.CreatedAt = DateTime.UtcNow;
        _db.Tasks.AddRange(older, newer);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksAsync();
        Assert.Equal("t-new", result[0].Id);
        Assert.Equal("t-old", result[1].Id);
    }

    [Fact]
    public async Task GetTasksAsync_SprintAndWorkspaceCombined()
    {
        _db.Workspaces.Add(CreateWorkspace("/ws/active"));
        _db.Sprints.AddRange(
            CreateSprint("s1", "/ws/sprint1"),
            CreateSprint("s2", "/ws/sprint2"));
        _db.Tasks.AddRange(
            CreateTask("t1", "Match both", workspacePath: "/ws/active", sprintId: "s1"),
            CreateTask("t2", "Wrong sprint", workspacePath: "/ws/active", sprintId: "s2"),
            CreateTask("t3", "Wrong workspace", workspacePath: "/ws/other", sprintId: "s1"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksAsync("s1");
        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetTaskAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTaskAsync_Exists_ReturnsSnapshot()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskAsync("t1");
        Assert.NotNull(result);
        Assert.Equal("t1", result.Id);
        Assert.Equal("Task One", result.Title);
    }

    [Fact]
    public async Task GetTaskAsync_NotFound_ReturnsNull()
    {
        var result = await _sut.GetTaskAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetTaskAsync_IncludesCommentCount()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.TaskComments.AddRange(
            CreateComment("c1", "t1"),
            CreateComment("c2", "t1"),
            CreateComment("c3", "t1"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskAsync("t1");
        Assert.NotNull(result);
        Assert.Equal(3, result.CommentCount);
    }

    [Fact]
    public async Task GetTaskAsync_ZeroComments_ReturnsZeroCount()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskAsync("t1");
        Assert.NotNull(result);
        Assert.Equal(0, result.CommentCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // FindTaskByTitleAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindTaskByTitleAsync_Found_ReturnsSnapshot()
    {
        _db.Tasks.Add(CreateTask("t1", "Implement auth"));
        await _db.SaveChangesAsync();

        var result = await _sut.FindTaskByTitleAsync("Implement auth");
        Assert.NotNull(result);
        Assert.Equal("t1", result.Id);
    }

    [Fact]
    public async Task FindTaskByTitleAsync_NotFound_ReturnsNull()
    {
        var result = await _sut.FindTaskByTitleAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task FindTaskByTitleAsync_SkipsCancelledTasks()
    {
        _db.Tasks.Add(CreateTask("t1", "Cancelled task", status: "Cancelled"));
        await _db.SaveChangesAsync();

        var result = await _sut.FindTaskByTitleAsync("Cancelled task");
        Assert.Null(result);
    }

    [Fact]
    public async Task FindTaskByTitleAsync_ReturnsMostRecentMatch()
    {
        var old = CreateTask("t-old", "Duplicate title");
        old.CreatedAt = DateTime.UtcNow.AddHours(-2);
        var recent = CreateTask("t-new", "Duplicate title");
        recent.CreatedAt = DateTime.UtcNow;
        _db.Tasks.AddRange(old, recent);
        await _db.SaveChangesAsync();

        var result = await _sut.FindTaskByTitleAsync("Duplicate title");
        Assert.NotNull(result);
        Assert.Equal("t-new", result.Id);
    }

    [Fact]
    public async Task FindTaskByTitleAsync_IncludesCommentCount()
    {
        _db.Tasks.Add(CreateTask("t1", "With comments"));
        _db.TaskComments.Add(CreateComment("c1", "t1"));
        await _db.SaveChangesAsync();

        var result = await _sut.FindTaskByTitleAsync("With comments");
        Assert.NotNull(result);
        Assert.Equal(1, result.CommentCount);
    }

    [Fact]
    public async Task FindTaskByTitleAsync_ReturnsNonCancelledWhenBothExist()
    {
        _db.Tasks.AddRange(
            CreateTask("t-cancelled", "Same title", status: "Cancelled"),
            CreateTask("t-active", "Same title", status: "Active"));
        await _db.SaveChangesAsync();

        var result = await _sut.FindTaskByTitleAsync("Same title");
        Assert.NotNull(result);
        Assert.Equal("t-active", result.Id);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetReviewQueueAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetReviewQueueAsync_Empty_ReturnsEmptyList()
    {
        var result = await _sut.GetReviewQueueAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReviewQueueAsync_ReturnsInReviewTasks()
    {
        _db.Tasks.Add(CreateTask("t1", "In review", status: "InReview"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetReviewQueueAsync();
        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    [Fact]
    public async Task GetReviewQueueAsync_ReturnsAwaitingValidationTasks()
    {
        _db.Tasks.Add(CreateTask("t1", "Awaiting validation", status: "AwaitingValidation"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetReviewQueueAsync();
        Assert.Single(result);
    }

    [Fact]
    public async Task GetReviewQueueAsync_ExcludesOtherStatuses()
    {
        _db.Tasks.AddRange(
            CreateTask("t1", "InReview", status: "InReview"),
            CreateTask("t2", "Active", status: "Active"),
            CreateTask("t3", "Completed", status: "Completed"),
            CreateTask("t4", "Queued", status: "Queued"),
            CreateTask("t5", "AwaitingValidation", status: "AwaitingValidation"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetReviewQueueAsync();
        Assert.Equal(2, result.Count);
        Assert.All(result, t =>
            Assert.True(t.Status is Shared.Models.TaskStatus.InReview
                or Shared.Models.TaskStatus.AwaitingValidation));
    }

    [Fact]
    public async Task GetReviewQueueAsync_OrderedByCreatedAtAscending()
    {
        var older = CreateTask("t-old", "Old review", status: "InReview");
        older.CreatedAt = DateTime.UtcNow.AddHours(-2);
        var newer = CreateTask("t-new", "New review", status: "InReview");
        newer.CreatedAt = DateTime.UtcNow;
        _db.Tasks.AddRange(older, newer);
        await _db.SaveChangesAsync();

        var result = await _sut.GetReviewQueueAsync();
        Assert.Equal("t-old", result[0].Id);
        Assert.Equal("t-new", result[1].Id);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetTasksWithActivePrsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTasksWithActivePrsAsync_NoPrs_ReturnsEmpty()
    {
        _db.Tasks.Add(CreateTask("t1", "No PR"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksWithActivePrsAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_OpenPr_ReturnsTuple()
    {
        var task = CreateTask("t1", "With PR");
        task.PullRequestNumber = 42;
        task.PullRequestStatus = "Open";
        task.PullRequestUrl = "https://github.com/org/repo/pull/42";
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksWithActivePrsAsync();
        Assert.Single(result);
        Assert.Equal("t1", result[0].TaskId);
        Assert.Equal(42, result[0].PrNumber);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_MergedPr_Excluded()
    {
        var task = CreateTask("t1", "Merged");
        task.PullRequestNumber = 10;
        task.PullRequestStatus = "Merged";
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksWithActivePrsAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_ClosedPr_Excluded()
    {
        var task = CreateTask("t1", "Closed");
        task.PullRequestNumber = 10;
        task.PullRequestStatus = "Closed";
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksWithActivePrsAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_MixedStatuses_ReturnsOnlyActive()
    {
        var open = CreateTask("t-open", "Open PR");
        open.PullRequestNumber = 1;
        open.PullRequestStatus = "Open";
        var merged = CreateTask("t-merged", "Merged PR");
        merged.PullRequestNumber = 2;
        merged.PullRequestStatus = "Merged";
        var review = CreateTask("t-review", "ReviewRequested PR");
        review.PullRequestNumber = 3;
        review.PullRequestStatus = "ReviewRequested";
        _db.Tasks.AddRange(open, merged, review);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksWithActivePrsAsync();
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.TaskId == "t-open");
        Assert.Contains(result, r => r.TaskId == "t-review");
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_NullPrNumber_Excluded()
    {
        var task = CreateTask("t1", "No PR number");
        task.PullRequestStatus = "Open";
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksWithActivePrsAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTasksWithActivePrsAsync_NullPrStatus_Excluded()
    {
        var task = CreateTask("t1", "No PR status");
        task.PullRequestNumber = 5;
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksWithActivePrsAsync();
        Assert.Empty(result);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetTaskCommentsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTaskCommentsAsync_TaskNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetTaskCommentsAsync("nonexistent"));
    }

    [Fact]
    public async Task GetTaskCommentsAsync_NoComments_ReturnsEmptyList()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskCommentsAsync("t1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTaskCommentsAsync_ReturnsOrderedByCreatedAt()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.TaskComments.AddRange(
            CreateComment("c2", "t1", content: "second", createdAt: DateTime.UtcNow.AddMinutes(1)),
            CreateComment("c1", "t1", content: "first", createdAt: DateTime.UtcNow.AddMinutes(-1)));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskCommentsAsync("t1");
        Assert.Equal(2, result.Count);
        Assert.Equal("c1", result[0].Id);
        Assert.Equal("c2", result[1].Id);
    }

    [Fact]
    public async Task GetTaskCommentsAsync_MapsFieldsCorrectly()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.TaskComments.Add(CreateComment("c1", "t1", "engineer-1", "Hephaestus", "Finding", "Found a bug"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskCommentsAsync("t1");
        Assert.Single(result);
        var c = result[0];
        Assert.Equal("c1", c.Id);
        Assert.Equal("t1", c.TaskId);
        Assert.Equal("engineer-1", c.AgentId);
        Assert.Equal("Hephaestus", c.AgentName);
        Assert.Equal(TaskCommentType.Finding, c.CommentType);
        Assert.Equal("Found a bug", c.Content);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetTaskCommentCountAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTaskCommentCountAsync_Zero_ReturnsZero()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();

        var count = await _sut.GetTaskCommentCountAsync("t1");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetTaskCommentCountAsync_Multiple_ReturnsCorrectCount()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.TaskComments.AddRange(
            CreateComment("c1", "t1"),
            CreateComment("c2", "t1"),
            CreateComment("c3", "t1"));
        await _db.SaveChangesAsync();

        var count = await _sut.GetTaskCommentCountAsync("t1");
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetTaskCommentCountAsync_CountsScopedToTask()
    {
        _db.Tasks.AddRange(CreateTask("t1", "One"), CreateTask("t2", "Two"));
        _db.TaskComments.AddRange(
            CreateComment("c1", "t1"),
            CreateComment("c2", "t2"),
            CreateComment("c3", "t2"));
        await _db.SaveChangesAsync();

        Assert.Equal(1, await _sut.GetTaskCommentCountAsync("t1"));
        Assert.Equal(2, await _sut.GetTaskCommentCountAsync("t2"));
    }

    // ═══════════════════════════════════════════════════════════════
    // GetTaskEvidenceAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTaskEvidenceAsync_TaskNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetTaskEvidenceAsync("nonexistent"));
    }

    [Fact]
    public async Task GetTaskEvidenceAsync_NoEvidence_ReturnsEmptyList()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskEvidenceAsync("t1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTaskEvidenceAsync_AllPhases_ReturnsAll()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.TaskEvidence.AddRange(
            CreateEvidence("e1", "t1", "Baseline"),
            CreateEvidence("e2", "t1", "After"),
            CreateEvidence("e3", "t1", "Review"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskEvidenceAsync("t1");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetTaskEvidenceAsync_FilterByPhase_ReturnsMatching()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.TaskEvidence.AddRange(
            CreateEvidence("e1", "t1", "Baseline"),
            CreateEvidence("e2", "t1", "After"),
            CreateEvidence("e3", "t1", "After"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskEvidenceAsync("t1", EvidencePhase.After);
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal(EvidencePhase.After, e.Phase));
    }

    [Fact]
    public async Task GetTaskEvidenceAsync_FilterByPhase_NoMatch_ReturnsEmpty()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.TaskEvidence.Add(CreateEvidence("e1", "t1", "After"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskEvidenceAsync("t1", EvidencePhase.Baseline);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTaskEvidenceAsync_OrderedByCreatedAt()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.TaskEvidence.AddRange(
            CreateEvidence("e2", "t1", createdAt: DateTime.UtcNow.AddMinutes(1)),
            CreateEvidence("e1", "t1", createdAt: DateTime.UtcNow.AddMinutes(-1)));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskEvidenceAsync("t1");
        Assert.Equal("e1", result[0].Id);
        Assert.Equal("e2", result[1].Id);
    }

    [Fact]
    public async Task GetTaskEvidenceAsync_MapsFieldsCorrectly()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        var evidence = CreateEvidence("e1", "t1", "Baseline", "tests");
        evidence.Tool = "dotnet";
        evidence.Command = "dotnet test";
        evidence.ExitCode = 0;
        evidence.OutputSnippet = "All tests passed";
        _db.TaskEvidence.Add(evidence);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTaskEvidenceAsync("t1");
        Assert.Single(result);
        var e = result[0];
        Assert.Equal("e1", e.Id);
        Assert.Equal("t1", e.TaskId);
        Assert.Equal(EvidencePhase.Baseline, e.Phase);
        Assert.Equal("tests", e.CheckName);
        Assert.Equal("dotnet", e.Tool);
        Assert.Equal("dotnet test", e.Command);
        Assert.Equal(0, e.ExitCode);
        Assert.Equal("All tests passed", e.OutputSnippet);
        Assert.True(e.Passed);
        Assert.Equal("engineer-1", e.AgentId);
        Assert.Equal("Hephaestus", e.AgentName);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSpecLinksForTaskAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSpecLinksForTaskAsync_TaskNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetSpecLinksForTaskAsync("nonexistent"));
    }

    [Fact]
    public async Task GetSpecLinksForTaskAsync_NoLinks_ReturnsEmptyList()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetSpecLinksForTaskAsync("t1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSpecLinksForTaskAsync_ReturnsLinksOrderedBySpecSectionId()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.SpecTaskLinks.AddRange(
            CreateSpecLink("l1", "t1", "spec-b"),
            CreateSpecLink("l2", "t1", "spec-a"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetSpecLinksForTaskAsync("t1");
        Assert.Equal(2, result.Count);
        Assert.Equal("spec-a", result[0].SpecSectionId);
        Assert.Equal("spec-b", result[1].SpecSectionId);
    }

    [Fact]
    public async Task GetSpecLinksForTaskAsync_MapsFieldsCorrectly()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        var link = CreateSpecLink("l1", "t1", "spec-1", "Implements");
        link.Note = "implements auth section";
        _db.SpecTaskLinks.Add(link);
        await _db.SaveChangesAsync();

        var result = await _sut.GetSpecLinksForTaskAsync("t1");
        Assert.Single(result);
        var l = result[0];
        Assert.Equal("l1", l.Id);
        Assert.Equal("t1", l.TaskId);
        Assert.Equal("spec-1", l.SpecSectionId);
        Assert.Equal(SpecLinkType.Implements, l.LinkType);
        Assert.Equal("engineer-1", l.LinkedByAgentId);
        Assert.Equal("Hephaestus", l.LinkedByAgentName);
        Assert.Equal("implements auth section", l.Note);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetTasksForSpecAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTasksForSpecAsync_NoLinks_ReturnsEmptyList()
    {
        var result = await _sut.GetTasksForSpecAsync("spec-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTasksForSpecAsync_ReturnsAllLinkedTasks()
    {
        _db.Tasks.AddRange(CreateTask("t1", "One"), CreateTask("t2", "Two"));
        _db.SpecTaskLinks.AddRange(
            CreateSpecLink("l1", "t1", "spec-1"),
            CreateSpecLink("l2", "t2", "spec-1"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksForSpecAsync("spec-1");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetTasksForSpecAsync_DoesNotReturnLinksForOtherSpecs()
    {
        _db.Tasks.AddRange(CreateTask("t1", "One"), CreateTask("t2", "Two"));
        _db.SpecTaskLinks.AddRange(
            CreateSpecLink("l1", "t1", "spec-1"),
            CreateSpecLink("l2", "t2", "spec-2"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksForSpecAsync("spec-1");
        Assert.Single(result);
        Assert.Equal("t1", result[0].TaskId);
    }

    [Fact]
    public async Task GetTasksForSpecAsync_OrderedByCreatedAtDescending()
    {
        _db.Tasks.AddRange(CreateTask("t1", "One"), CreateTask("t2", "Two"));
        _db.SpecTaskLinks.AddRange(
            CreateSpecLink("l-old", "t1", "spec-1", createdAt: DateTime.UtcNow.AddHours(-1)),
            CreateSpecLink("l-new", "t2", "spec-1", createdAt: DateTime.UtcNow));
        await _db.SaveChangesAsync();

        var result = await _sut.GetTasksForSpecAsync("spec-1");
        Assert.Equal("l-new", result[0].Id);
        Assert.Equal("l-old", result[1].Id);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetUnlinkedTasksAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetUnlinkedTasksAsync_AllLinked_ReturnsEmpty()
    {
        _db.Tasks.Add(CreateTask("t1", "Linked task"));
        _db.SpecTaskLinks.Add(CreateSpecLink("l1", "t1", "spec-1"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetUnlinkedTasksAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetUnlinkedTasksAsync_UnlinkedNonTerminal_ReturnsTask()
    {
        _db.Tasks.Add(CreateTask("t1", "Unlinked active", status: "Active"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetUnlinkedTasksAsync();
        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    [Fact]
    public async Task GetUnlinkedTasksAsync_CompletedExcluded()
    {
        _db.Tasks.Add(CreateTask("t1", "Completed unlinked", status: "Completed"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetUnlinkedTasksAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetUnlinkedTasksAsync_CancelledExcluded()
    {
        _db.Tasks.Add(CreateTask("t1", "Cancelled unlinked", status: "Cancelled"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetUnlinkedTasksAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetUnlinkedTasksAsync_MixedLinkedAndUnlinked()
    {
        _db.Tasks.AddRange(
            CreateTask("t-linked", "Linked"),
            CreateTask("t-unlinked", "Unlinked", status: "Active"),
            CreateTask("t-completed", "Completed", status: "Completed"));
        _db.SpecTaskLinks.Add(CreateSpecLink("l1", "t-linked", "spec-1"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetUnlinkedTasksAsync();
        Assert.Single(result);
        Assert.Equal("t-unlinked", result[0].Id);
    }

    [Fact]
    public async Task GetUnlinkedTasksAsync_IncludesCommentCount()
    {
        _db.Tasks.Add(CreateTask("t1", "Unlinked", status: "Active"));
        _db.TaskComments.AddRange(
            CreateComment("c1", "t1"),
            CreateComment("c2", "t1"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetUnlinkedTasksAsync();
        Assert.Single(result);
        Assert.Equal(2, result[0].CommentCount);
    }

    [Fact]
    public async Task GetUnlinkedTasksAsync_OrderedByCreatedAtDescending()
    {
        var old = CreateTask("t-old", "Old", status: "Active");
        old.CreatedAt = DateTime.UtcNow.AddHours(-2);
        var recent = CreateTask("t-new", "New", status: "Active");
        recent.CreatedAt = DateTime.UtcNow;
        _db.Tasks.AddRange(old, recent);
        await _db.SaveChangesAsync();

        var result = await _sut.GetUnlinkedTasksAsync();
        Assert.Equal("t-new", result[0].Id);
        Assert.Equal("t-old", result[1].Id);
    }

    // ═══════════════════════════════════════════════════════════════
    // AssignTaskAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AssignTaskAsync_TaskNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AssignTaskAsync("nonexistent", "agent-1", "Agent"));
    }

    [Fact]
    public async Task AssignTaskAsync_AgentInCatalog_UsesCatalogName()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.AssignTaskAsync("t1", "engineer-1", "Wrong Name");
        Assert.Equal("engineer-1", result.AssignedAgentId);
        Assert.Equal("Hephaestus", result.AssignedAgentName);
    }

    [Fact]
    public async Task AssignTaskAsync_AgentNotInCatalog_UsesProvidedName()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.AssignTaskAsync("t1", "external-1", "External Agent");
        Assert.Equal("external-1", result.AssignedAgentId);
        Assert.Equal("External Agent", result.AssignedAgentName);
    }

    [Fact]
    public async Task AssignTaskAsync_UpdatesUpdatedAt()
    {
        var task = CreateTask("t1", "Task One");
        var originalTime = DateTime.UtcNow.AddHours(-1);
        task.UpdatedAt = originalTime;
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.AssignTaskAsync("t1", "engineer-1", "Hephaestus");
        Assert.True(result.UpdatedAt > originalTime);
    }

    [Fact]
    public async Task AssignTaskAsync_PersistsToDatabase()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.AssignTaskAsync("t1", "reviewer-1", "Socrates");
        _db.ChangeTracker.Clear();

        var entity = await _db.Tasks.FindAsync("t1");
        Assert.NotNull(entity);
        Assert.Equal("reviewer-1", entity.AssignedAgentId);
        Assert.Equal("Socrates", entity.AssignedAgentName);
    }

    // ═══════════════════════════════════════════════════════════════
    // UpdateTaskStatusAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateTaskStatusAsync_TaskNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskStatusAsync("nonexistent", Shared.Models.TaskStatus.Active));
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_UpdatesStatusString()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskStatusAsync("t1", Shared.Models.TaskStatus.InReview);
        Assert.Equal(Shared.Models.TaskStatus.InReview, result.Status);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_Active_SetsStartedAt()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskStatusAsync("t1", Shared.Models.TaskStatus.Active);
        Assert.NotNull(result.StartedAt);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_Active_DoesNotOverwriteExistingStartedAt()
    {
        var task = CreateTask("t1", "Task One", status: "Active");
        var originalStart = DateTime.UtcNow.AddHours(-2);
        task.StartedAt = originalStart;
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskStatusAsync("t1", Shared.Models.TaskStatus.Active);
        Assert.NotNull(result.StartedAt);
        Assert.Equal(originalStart, result.StartedAt.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_Completed_SetsCompletedAt()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskStatusAsync("t1", Shared.Models.TaskStatus.Completed);
        Assert.NotNull(result.CompletedAt);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_Cancelled_SetsCompletedAt()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskStatusAsync("t1", Shared.Models.TaskStatus.Cancelled);
        Assert.NotNull(result.CompletedAt);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_NonTerminal_ClearsCompletedAt()
    {
        var task = CreateTask("t1", "Task One", status: "Completed");
        task.CompletedAt = DateTime.UtcNow;
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskStatusAsync("t1", Shared.Models.TaskStatus.Active);
        Assert.Null(result.CompletedAt);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_UpdatesUpdatedAt()
    {
        var task = CreateTask("t1", "Task One");
        task.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var before = DateTime.UtcNow;
        var result = await _sut.UpdateTaskStatusAsync("t1", Shared.Models.TaskStatus.Active);
        Assert.True(result.UpdatedAt >= before);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_PersistsToDatabase()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.UpdateTaskStatusAsync("t1", Shared.Models.TaskStatus.Completed);
        _db.ChangeTracker.Clear();

        var entity = await _db.Tasks.FindAsync("t1");
        Assert.NotNull(entity);
        Assert.Equal("Completed", entity.Status);
        Assert.NotNull(entity.CompletedAt);
    }

    // ═══════════════════════════════════════════════════════════════
    // UpdateTaskBranchAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateTaskBranchAsync_TaskNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskBranchAsync("nonexistent", "feat/auth"));
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_EmptyTaskId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpdateTaskBranchAsync("", "feat/auth"));
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_WhitespaceTaskId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpdateTaskBranchAsync("  ", "feat/auth"));
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_EmptyBranchName_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpdateTaskBranchAsync("t1", ""));
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_WhitespaceBranchName_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpdateTaskBranchAsync("t1", "   "));
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_WriteOnce_Succeeds()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskBranchAsync("t1", "feat/auth");
        Assert.Equal("feat/auth", result.BranchName);
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_SameBranch_IsNoOp()
    {
        var task = CreateTask("t1", "Task One");
        task.BranchName = "feat/auth";
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskBranchAsync("t1", "feat/auth");
        Assert.Equal("feat/auth", result.BranchName);
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_DifferentBranch_ThrowsInvalidOperation()
    {
        var task = CreateTask("t1", "Task One");
        task.BranchName = "feat/auth";
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskBranchAsync("t1", "feat/other"));
        Assert.Contains("feat/auth", ex.Message);
        Assert.Contains("feat/other", ex.Message);
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_PersistsToDatabase()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.UpdateTaskBranchAsync("t1", "feat/auth");
        _db.ChangeTracker.Clear();

        var entity = await _db.Tasks.FindAsync("t1");
        Assert.NotNull(entity);
        Assert.Equal("feat/auth", entity.BranchName);
    }

    [Fact]
    public async Task UpdateTaskBranchAsync_UpdatesUpdatedAt()
    {
        var task = CreateTask("t1", "Task One");
        task.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var before = DateTime.UtcNow;
        var result = await _sut.UpdateTaskBranchAsync("t1", "feat/auth");
        Assert.True(result.UpdatedAt >= before);
    }

    // ═══════════════════════════════════════════════════════════════
    // UpdateTaskSprintAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateTaskSprintAsync_TaskNotFound_Throws()
    {
        _db.Sprints.Add(CreateSprint("sprint-1"));
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskSprintAsync("nonexistent", "sprint-1"));
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_EmptyTaskId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpdateTaskSprintAsync("", "sprint-1"));
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_SprintNotFound_Throws()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskSprintAsync("t1", "no-such-sprint"));
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_OrphanTask_RejectsAssignment()
    {
        // Task with no WorkspacePath and no Room → unresolvable workspace.
        _db.Sprints.Add(CreateSprint("sprint-1", workspacePath: "/ws/a"));
        _db.Tasks.Add(CreateTask("t1", "Orphan task"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskSprintAsync("t1", "sprint-1"));
        Assert.Contains("workspace", ex.Message, StringComparison.OrdinalIgnoreCase);

        var reloaded = await _db.Tasks.FindAsync("t1");
        Assert.Null(reloaded!.SprintId);
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_AssignsSprint_PersistsAndUpdatesUpdatedAt()
    {
        _db.Sprints.Add(CreateSprint("sprint-1", workspacePath: "/ws/a"));
        var task = CreateTask("t1", "Task One", workspacePath: "/ws/a");
        task.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var before = DateTime.UtcNow;
        var result = await _sut.UpdateTaskSprintAsync("t1", "sprint-1");

        Assert.Equal("sprint-1", result.SprintId);
        Assert.True(result.UpdatedAt >= before);

        var reloaded = await _db.Tasks.FindAsync("t1");
        Assert.Equal("sprint-1", reloaded!.SprintId);
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_NullSprintId_ClearsAssociation()
    {
        _db.Sprints.Add(CreateSprint("sprint-1", workspacePath: "/ws/a"));
        _db.Tasks.Add(CreateTask("t1", "Task One", workspacePath: "/ws/a", sprintId: "sprint-1"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskSprintAsync("t1", null);

        Assert.Null(result.SprintId);
        var reloaded = await _db.Tasks.FindAsync("t1");
        Assert.Null(reloaded!.SprintId);
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_WhitespaceSprintId_ClearsAssociation()
    {
        _db.Sprints.Add(CreateSprint("sprint-1", workspacePath: "/ws/a"));
        _db.Tasks.Add(CreateTask("t1", "Task One", workspacePath: "/ws/a", sprintId: "sprint-1"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskSprintAsync("t1", "   ");

        Assert.Null(result.SprintId);
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_SameSprint_IsNoOp()
    {
        _db.Sprints.Add(CreateSprint("sprint-1", workspacePath: "/ws/a"));
        var task = CreateTask("t1", "Task One", workspacePath: "/ws/a", sprintId: "sprint-1");
        var originalUpdatedAt = DateTime.UtcNow.AddHours(-2);
        task.UpdatedAt = originalUpdatedAt;
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskSprintAsync("t1", "sprint-1");

        Assert.Equal("sprint-1", result.SprintId);
        var reloaded = await _db.Tasks.FindAsync("t1");
        // No-op should not bump UpdatedAt
        Assert.Equal(originalUpdatedAt, reloaded!.UpdatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_ReassignsToDifferentSprint()
    {
        var sprint1 = CreateSprint("sprint-1", workspacePath: "/ws/a");
        sprint1.Status = "Completed";
        var sprint2 = CreateSprint("sprint-2", workspacePath: "/ws/a");
        _db.Sprints.AddRange(sprint1, sprint2);
        _db.Tasks.Add(CreateTask("t1", "Task One", workspacePath: "/ws/a", sprintId: "sprint-1"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskSprintAsync("t1", "sprint-2");

        Assert.Equal("sprint-2", result.SprintId);
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_CrossWorkspace_RejectsAssignment()
    {
        _db.Sprints.Add(CreateSprint("sprint-other", workspacePath: "/ws/other"));
        _db.Tasks.Add(CreateTask("t1", "Task One", workspacePath: "/ws/active"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskSprintAsync("t1", "sprint-other"));
        Assert.Contains("workspace", ex.Message, StringComparison.OrdinalIgnoreCase);

        var reloaded = await _db.Tasks.FindAsync("t1");
        Assert.Null(reloaded!.SprintId);
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_WorkspaceFromRoom_AllowsMatchingSprint()
    {
        _db.Workspaces.Add(CreateWorkspace("/ws/active"));
        _db.Rooms.Add(CreateRoom("room-1", "Room", workspacePath: "/ws/active"));
        _db.Sprints.Add(CreateSprint("sprint-1", workspacePath: "/ws/active"));
        // Task has no WorkspacePath of its own — workspace must be derived from room
        _db.Tasks.Add(CreateTask("t1", "Task One", roomId: "room-1"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskSprintAsync("t1", "sprint-1");

        Assert.Equal("sprint-1", result.SprintId);
    }

    [Fact]
    public async Task UpdateTaskSprintAsync_WorkspaceFromRoom_RejectsCrossWorkspaceSprint()
    {
        _db.Workspaces.Add(CreateWorkspace("/ws/active"));
        _db.Rooms.Add(CreateRoom("room-1", "Room", workspacePath: "/ws/active"));
        _db.Sprints.Add(CreateSprint("sprint-other", workspacePath: "/ws/other"));
        _db.Tasks.Add(CreateTask("t1", "Task One", roomId: "room-1"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskSprintAsync("t1", "sprint-other"));
    }

    // ═══════════════════════════════════════════════════════════════
    // UpdateTaskPrAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateTaskPrAsync_TaskNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskPrAsync("nonexistent", "https://pr", 1, PullRequestStatus.Open));
    }

    [Fact]
    public async Task UpdateTaskPrAsync_UpdatesPrFields()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskPrAsync("t1", "https://github.com/pull/42", 42, PullRequestStatus.Open);
        Assert.Equal("https://github.com/pull/42", result.PullRequestUrl);
        Assert.Equal(42, result.PullRequestNumber);
        Assert.Equal(PullRequestStatus.Open, result.PullRequestStatus);
    }

    [Fact]
    public async Task UpdateTaskPrAsync_UpdatesUpdatedAt()
    {
        var task = CreateTask("t1", "Task One");
        task.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var before = DateTime.UtcNow;
        var result = await _sut.UpdateTaskPrAsync("t1", "https://pr", 1, PullRequestStatus.Open);
        Assert.True(result.UpdatedAt >= before);
    }

    [Fact]
    public async Task UpdateTaskPrAsync_PersistsToDatabase()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.UpdateTaskPrAsync("t1", "https://pr/99", 99, PullRequestStatus.Merged);
        _db.ChangeTracker.Clear();

        var entity = await _db.Tasks.FindAsync("t1");
        Assert.NotNull(entity);
        Assert.Equal("https://pr/99", entity.PullRequestUrl);
        Assert.Equal(99, entity.PullRequestNumber);
        Assert.Equal("Merged", entity.PullRequestStatus);
    }

    [Fact]
    public async Task UpdateTaskPrAsync_OverwritesPreviousPrInfo()
    {
        var task = CreateTask("t1", "Task One");
        task.PullRequestUrl = "https://old";
        task.PullRequestNumber = 1;
        task.PullRequestStatus = "Open";
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _sut.UpdateTaskPrAsync("t1", "https://new", 2, PullRequestStatus.Merged);
        Assert.Equal("https://new", result.PullRequestUrl);
        Assert.Equal(2, result.PullRequestNumber);
        Assert.Equal(PullRequestStatus.Merged, result.PullRequestStatus);
    }

    // ═══════════════════════════════════════════════════════════════
    // UnlinkTaskFromSpecAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UnlinkTaskFromSpecAsync_NoLink_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UnlinkTaskFromSpecAsync("t1", "spec-1"));
    }

    [Fact]
    public async Task UnlinkTaskFromSpecAsync_SuccessfulUnlink()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.SpecTaskLinks.Add(CreateSpecLink("l1", "t1", "spec-1"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.UnlinkTaskFromSpecAsync("t1", "spec-1");
        _db.ChangeTracker.Clear();

        var remaining = await _db.SpecTaskLinks.CountAsync(l => l.TaskId == "t1");
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task UnlinkTaskFromSpecAsync_OnlyRemovesTargetedLink()
    {
        _db.Tasks.Add(CreateTask("t1", "Task One"));
        _db.SpecTaskLinks.AddRange(
            CreateSpecLink("l1", "t1", "spec-1"),
            CreateSpecLink("l2", "t1", "spec-2"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _sut.UnlinkTaskFromSpecAsync("t1", "spec-1");
        _db.ChangeTracker.Clear();

        var remaining = await _db.SpecTaskLinks.Where(l => l.TaskId == "t1").ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("spec-2", remaining[0].SpecSectionId);
    }

    // ═══════════════════════════════════════════════════════════════
    // BuildTaskSnapshot (static helper)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BuildTaskSnapshot_MapsAllFields()
    {
        var now = DateTime.UtcNow;
        var entity = new TaskEntity
        {
            Id = "t1",
            Title = "Build feature",
            Description = "Desc",
            SuccessCriteria = "Criteria",
            Status = "Active",
            Type = "Bug",
            CurrentPhase = "Implementation",
            CurrentPlan = "Step 1",
            ValidationStatus = "InProgress",
            ValidationSummary = "Validating",
            ImplementationStatus = "Completed",
            ImplementationSummary = "Done",
            PreferredRoles = "[\"Engineer\",\"Reviewer\"]",
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now,
            Size = "M",
            StartedAt = now.AddHours(-1),
            CompletedAt = null,
            AssignedAgentId = "engineer-1",
            AssignedAgentName = "Hephaestus",
            UsedFleet = true,
            FleetModels = "[\"gpt-4\",\"claude\"]",
            BranchName = "feat/auth",
            PullRequestUrl = "https://pr/1",
            PullRequestNumber = 1,
            PullRequestStatus = "Open",
            ReviewerAgentId = "reviewer-1",
            ReviewRounds = 2,
            TestsCreated = "[\"test1.cs\"]",
            CommitCount = 5,
            MergeCommitSha = "abc123",
            WorkspacePath = "/ws/one",
            SprintId = "sprint-1",
        };

        var snapshot = TaskSnapshotFactory.BuildTaskSnapshot(entity, 7);

        Assert.Equal("t1", snapshot.Id);
        Assert.Equal("Build feature", snapshot.Title);
        Assert.Equal("Desc", snapshot.Description);
        Assert.Equal("Criteria", snapshot.SuccessCriteria);
        Assert.Equal(Shared.Models.TaskStatus.Active, snapshot.Status);
        Assert.Equal(TaskType.Bug, snapshot.Type);
        Assert.Equal(CollaborationPhase.Implementation, snapshot.CurrentPhase);
        Assert.Equal("Step 1", snapshot.CurrentPlan);
        Assert.Equal(WorkstreamStatus.InProgress, snapshot.ValidationStatus);
        Assert.Equal("Validating", snapshot.ValidationSummary);
        Assert.Equal(WorkstreamStatus.Completed, snapshot.ImplementationStatus);
        Assert.Equal("Done", snapshot.ImplementationSummary);
        Assert.Equal(new List<string> { "Engineer", "Reviewer" }, snapshot.PreferredRoles);
        Assert.Equal(now.AddHours(-2), snapshot.CreatedAt);
        Assert.Equal(now, snapshot.UpdatedAt);
        Assert.Equal(TaskSize.M, snapshot.Size);
        Assert.Equal(now.AddHours(-1), snapshot.StartedAt);
        Assert.Null(snapshot.CompletedAt);
        Assert.Equal("engineer-1", snapshot.AssignedAgentId);
        Assert.Equal("Hephaestus", snapshot.AssignedAgentName);
        Assert.True(snapshot.UsedFleet);
        Assert.Equal(new List<string> { "gpt-4", "claude" }, snapshot.FleetModels);
        Assert.Equal("feat/auth", snapshot.BranchName);
        Assert.Equal("https://pr/1", snapshot.PullRequestUrl);
        Assert.Equal(1, snapshot.PullRequestNumber);
        Assert.Equal(PullRequestStatus.Open, snapshot.PullRequestStatus);
        Assert.Equal("reviewer-1", snapshot.ReviewerAgentId);
        Assert.Equal(2, snapshot.ReviewRounds);
        Assert.Equal(new List<string> { "test1.cs" }, snapshot.TestsCreated);
        Assert.Equal(5, snapshot.CommitCount);
        Assert.Equal("abc123", snapshot.MergeCommitSha);
        Assert.Equal(7, snapshot.CommentCount);
        Assert.Equal("/ws/one", snapshot.WorkspacePath);
        Assert.Equal("sprint-1", snapshot.SprintId);
    }

    [Fact]
    public void BuildTaskSnapshot_NullSize_MapsToNull()
    {
        var entity = CreateTask("t1", "No size");
        entity.Size = null;
        var snapshot = TaskSnapshotFactory.BuildTaskSnapshot(entity);
        Assert.Null(snapshot.Size);
    }

    [Fact]
    public void BuildTaskSnapshot_EmptySize_MapsToNull()
    {
        var entity = CreateTask("t1", "Empty size");
        entity.Size = "";
        var snapshot = TaskSnapshotFactory.BuildTaskSnapshot(entity);
        Assert.Null(snapshot.Size);
    }

    [Fact]
    public void BuildTaskSnapshot_NullPrStatus_MapsToNull()
    {
        var entity = CreateTask("t1", "No PR");
        entity.PullRequestStatus = null;
        var snapshot = TaskSnapshotFactory.BuildTaskSnapshot(entity);
        Assert.Null(snapshot.PullRequestStatus);
    }

    [Fact]
    public void BuildTaskSnapshot_EmptyPrStatus_MapsToNull()
    {
        var entity = CreateTask("t1", "Empty PR status");
        entity.PullRequestStatus = "";
        var snapshot = TaskSnapshotFactory.BuildTaskSnapshot(entity);
        Assert.Null(snapshot.PullRequestStatus);
    }

    [Fact]
    public void BuildTaskSnapshot_DefaultCommentCount_IsZero()
    {
        var entity = CreateTask("t1", "No comments");
        var snapshot = TaskSnapshotFactory.BuildTaskSnapshot(entity);
        Assert.Equal(0, snapshot.CommentCount);
    }

    [Fact]
    public void BuildTaskSnapshot_UnknownTaskType_DefaultsToFeature()
    {
        var entity = CreateTask("t1", "Unknown type");
        entity.Type = "UnknownType";
        var snapshot = TaskSnapshotFactory.BuildTaskSnapshot(entity);
        Assert.Equal(TaskType.Feature, snapshot.Type);
    }

    // ═══════════════════════════════════════════════════════════════
    // BuildTaskComment (static helper)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BuildTaskComment_MapsAllFields()
    {
        var now = DateTime.UtcNow;
        var entity = new TaskCommentEntity
        {
            Id = "c1",
            TaskId = "t1",
            AgentId = "engineer-1",
            AgentName = "Hephaestus",
            CommentType = "Finding",
            Content = "Found a bug",
            CreatedAt = now,
        };

        var comment = TaskSnapshotFactory.BuildTaskComment(entity);

        Assert.Equal("c1", comment.Id);
        Assert.Equal("t1", comment.TaskId);
        Assert.Equal("engineer-1", comment.AgentId);
        Assert.Equal("Hephaestus", comment.AgentName);
        Assert.Equal(TaskCommentType.Finding, comment.CommentType);
        Assert.Equal("Found a bug", comment.Content);
        Assert.Equal(now, comment.CreatedAt);
    }

    [Fact]
    public void BuildTaskComment_UnknownCommentType_DefaultsToComment()
    {
        var entity = new TaskCommentEntity
        {
            Id = "c1",
            TaskId = "t1",
            AgentId = "a",
            AgentName = "A",
            CommentType = "Nonexistent",
            Content = "text",
            CreatedAt = DateTime.UtcNow,
        };

        var comment = TaskSnapshotFactory.BuildTaskComment(entity);
        Assert.Equal(TaskCommentType.Comment, comment.CommentType);
    }

    // ═══════════════════════════════════════════════════════════════
    // BuildTaskEvidence (static helper)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BuildTaskEvidence_MapsAllFields()
    {
        var now = DateTime.UtcNow;
        var entity = new TaskEvidenceEntity
        {
            Id = "e1",
            TaskId = "t1",
            Phase = "Baseline",
            CheckName = "build",
            Tool = "dotnet",
            Command = "dotnet build",
            ExitCode = 0,
            OutputSnippet = "Build succeeded",
            Passed = true,
            AgentId = "engineer-1",
            AgentName = "Hephaestus",
            CreatedAt = now,
        };

        var evidence = TaskSnapshotFactory.BuildTaskEvidence(entity);

        Assert.Equal("e1", evidence.Id);
        Assert.Equal("t1", evidence.TaskId);
        Assert.Equal(EvidencePhase.Baseline, evidence.Phase);
        Assert.Equal("build", evidence.CheckName);
        Assert.Equal("dotnet", evidence.Tool);
        Assert.Equal("dotnet build", evidence.Command);
        Assert.Equal(0, evidence.ExitCode);
        Assert.Equal("Build succeeded", evidence.OutputSnippet);
        Assert.True(evidence.Passed);
        Assert.Equal("engineer-1", evidence.AgentId);
        Assert.Equal("Hephaestus", evidence.AgentName);
        Assert.Equal(now, evidence.CreatedAt);
    }

    [Fact]
    public void BuildTaskEvidence_UnknownPhase_DefaultsToAfter()
    {
        var entity = new TaskEvidenceEntity
        {
            Id = "e1",
            TaskId = "t1",
            Phase = "Nonexistent",
            CheckName = "check",
            Tool = "bash",
            AgentId = "a",
            AgentName = "A",
            CreatedAt = DateTime.UtcNow,
        };

        var evidence = TaskSnapshotFactory.BuildTaskEvidence(entity);
        Assert.Equal(EvidencePhase.After, evidence.Phase);
    }

    [Fact]
    public void BuildTaskEvidence_NullOptionalFields_MapsCorrectly()
    {
        var entity = new TaskEvidenceEntity
        {
            Id = "e1",
            TaskId = "t1",
            Phase = "After",
            CheckName = "check",
            Tool = "bash",
            Command = null,
            ExitCode = null,
            OutputSnippet = null,
            Passed = false,
            AgentId = "a",
            AgentName = "A",
            CreatedAt = DateTime.UtcNow,
        };

        var evidence = TaskSnapshotFactory.BuildTaskEvidence(entity);
        Assert.Null(evidence.Command);
        Assert.Null(evidence.ExitCode);
        Assert.Null(evidence.OutputSnippet);
        Assert.False(evidence.Passed);
    }

    // ═══════════════════════════════════════════════════════════════
    // BuildSpecTaskLink (static helper)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BuildSpecTaskLink_MapsAllFields()
    {
        var now = DateTime.UtcNow;
        var entity = new SpecTaskLinkEntity
        {
            Id = "l1",
            TaskId = "t1",
            SpecSectionId = "spec-1",
            LinkType = "Implements",
            LinkedByAgentId = "engineer-1",
            LinkedByAgentName = "Hephaestus",
            Note = "implements auth",
            CreatedAt = now,
        };

        var link = TaskSnapshotFactory.BuildSpecTaskLink(entity);

        Assert.Equal("l1", link.Id);
        Assert.Equal("t1", link.TaskId);
        Assert.Equal("spec-1", link.SpecSectionId);
        Assert.Equal(SpecLinkType.Implements, link.LinkType);
        Assert.Equal("engineer-1", link.LinkedByAgentId);
        Assert.Equal("Hephaestus", link.LinkedByAgentName);
        Assert.Equal("implements auth", link.Note);
        Assert.Equal(now, link.CreatedAt);
    }

    [Fact]
    public void BuildSpecTaskLink_UnknownLinkType_DefaultsToImplements()
    {
        var entity = new SpecTaskLinkEntity
        {
            Id = "l1",
            TaskId = "t1",
            SpecSectionId = "spec-1",
            LinkType = "Nonexistent",
            LinkedByAgentId = "a",
            LinkedByAgentName = "A",
            CreatedAt = DateTime.UtcNow,
        };

        var link = TaskSnapshotFactory.BuildSpecTaskLink(entity);
        Assert.Equal(SpecLinkType.Implements, link.LinkType);
    }

    [Fact]
    public void BuildSpecTaskLink_NullNote_MapsToNull()
    {
        var entity = new SpecTaskLinkEntity
        {
            Id = "l1",
            TaskId = "t1",
            SpecSectionId = "spec-1",
            LinkType = "Implements",
            LinkedByAgentId = "a",
            LinkedByAgentName = "A",
            Note = null,
            CreatedAt = DateTime.UtcNow,
        };

        var link = TaskSnapshotFactory.BuildSpecTaskLink(entity);
        Assert.Null(link.Note);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetActiveWorkspacePathAsync (internal helper)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetActiveWorkspacePath_NoWorkspaces_ReturnsNull()
    {
        var result = await _sut.GetActiveWorkspacePathAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveWorkspacePath_InactiveOnly_ReturnsNull()
    {
        _db.Workspaces.Add(CreateWorkspace("/ws/inactive", isActive: false));
        await _db.SaveChangesAsync();

        var result = await _sut.GetActiveWorkspacePathAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveWorkspacePath_ActiveExists_ReturnsPath()
    {
        _db.Workspaces.Add(CreateWorkspace("/ws/active"));
        await _db.SaveChangesAsync();

        var result = await _sut.GetActiveWorkspacePathAsync();
        Assert.Equal("/ws/active", result);
    }

    // ═══════════════════════════════════════════════════════════════
    // TryClaimForMergeAsync — atomic Approved → Merging transition
    // Regression coverage for the MERGE_TASK race that allowed two
    // concurrent reviewers to both squash-merge the same branch.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryClaimForMerge_ApprovedTask_ClaimsAndTransitionsToMerging()
    {
        _db.Tasks.Add(CreateTask("t-approved", "Approved task", status: nameof(TaskStatus.Approved)));
        await _db.SaveChangesAsync();

        var claimed = await _sut.TryClaimForMergeAsync("t-approved");

        Assert.True(claimed);

        // Re-read directly from DB to bypass any cached entity state.
        await using var verifyCtx = new AgentAcademyDbContext(
            new DbContextOptionsBuilder<AgentAcademyDbContext>().UseSqlite(_connection).Options);
        var fresh = await verifyCtx.Tasks.FindAsync("t-approved");
        Assert.NotNull(fresh);
        Assert.Equal(nameof(TaskStatus.Merging), fresh!.Status);
    }

    [Fact]
    public async Task TryClaimForMerge_NonexistentTask_ReturnsFalse()
    {
        var claimed = await _sut.TryClaimForMergeAsync("no-such-task");
        Assert.False(claimed);
    }

    [Theory]
    [InlineData(nameof(TaskStatus.Active))]
    [InlineData(nameof(TaskStatus.InReview))]
    [InlineData(nameof(TaskStatus.Merging))]
    [InlineData(nameof(TaskStatus.Completed))]
    [InlineData(nameof(TaskStatus.Cancelled))]
    public async Task TryClaimForMerge_NotApproved_ReturnsFalseAndPreservesStatus(string startingStatus)
    {
        _db.Tasks.Add(CreateTask("t-x", "Wrong status task", status: startingStatus));
        await _db.SaveChangesAsync();

        var claimed = await _sut.TryClaimForMergeAsync("t-x");

        Assert.False(claimed);

        await using var verifyCtx = new AgentAcademyDbContext(
            new DbContextOptionsBuilder<AgentAcademyDbContext>().UseSqlite(_connection).Options);
        var fresh = await verifyCtx.Tasks.FindAsync("t-x");
        Assert.NotNull(fresh);
        Assert.Equal(startingStatus, fresh!.Status);
    }

    [Fact]
    public async Task TryClaimForMerge_CalledTwice_OnlyFirstCallWins()
    {
        // Simulates the merge race: two reviewers both observed Approved and
        // are now both attempting to claim the merge. Only one should win.
        _db.Tasks.Add(CreateTask("t-race", "Race task", status: nameof(TaskStatus.Approved)));
        await _db.SaveChangesAsync();

        var first = await _sut.TryClaimForMergeAsync("t-race");
        var second = await _sut.TryClaimForMergeAsync("t-race");

        Assert.True(first);
        Assert.False(second);

        await using var verifyCtx = new AgentAcademyDbContext(
            new DbContextOptionsBuilder<AgentAcademyDbContext>().UseSqlite(_connection).Options);
        var fresh = await verifyCtx.Tasks.FindAsync("t-race");
        Assert.Equal(nameof(TaskStatus.Merging), fresh!.Status);
    }
}
