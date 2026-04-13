using System.Security.Claims;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public sealed class RetrospectiveControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly RetrospectiveController _controller;

    public RetrospectiveControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _controller = new RetrospectiveController(_db);
        SetAuthenticated(true);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void SetAuthenticated(bool authenticated)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = authenticated
                    ? new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "test-user")], "Cookies"))
                    : new ClaimsPrincipal(new ClaimsIdentity())
            }
        };
    }

    private TaskEntity SeedTask(string? id = null, string title = "Test task", string status = "Done", DateTime? completedAt = null)
    {
        var task = new TaskEntity
        {
            Id = id ?? $"task-{Guid.NewGuid():N}",
            Title = title,
            Status = status,
            CompletedAt = completedAt ?? DateTime.UtcNow.AddHours(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    private TaskCommentEntity SeedRetrospective(
        string? taskId = null,
        string agentId = "hephaestus",
        string agentName = "Hephaestus",
        string content = "Key learnings from this task.",
        DateTime? createdAt = null)
    {
        var task = taskId != null
            ? _db.Tasks.Find(taskId) ?? SeedTask(id: taskId)
            : SeedTask();

        var comment = new TaskCommentEntity
        {
            Id = $"retro-{Guid.NewGuid():N}",
            TaskId = task.Id,
            AgentId = agentId,
            AgentName = agentName,
            CommentType = nameof(TaskCommentType.Retrospective),
            Content = content,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        _db.TaskComments.Add(comment);
        _db.SaveChanges();
        return comment;
    }

    private TaskCommentEntity SeedNonRetrospective(string? taskId = null, string commentType = "Comment")
    {
        var task = taskId != null
            ? _db.Tasks.Find(taskId) ?? SeedTask(id: taskId)
            : SeedTask();

        var comment = new TaskCommentEntity
        {
            Id = $"comment-{Guid.NewGuid():N}",
            TaskId = task.Id,
            AgentId = "agent-1",
            AgentName = "Agent 1",
            CommentType = commentType,
            Content = "This is a regular comment.",
            CreatedAt = DateTime.UtcNow,
        };
        _db.TaskComments.Add(comment);
        _db.SaveChanges();
        return comment;
    }

    // ── List ──────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsEmptyWhenNoRetrospectives()
    {
        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        Assert.Empty(body.Retrospectives);
        Assert.Equal(0, body.Total);
    }

    [Fact]
    public async Task List_ReturnsOnlyRetrospectives_NotOtherCommentTypes()
    {
        SeedRetrospective();
        SeedNonRetrospective(commentType: "Comment");
        SeedNonRetrospective(commentType: "Finding");
        SeedNonRetrospective(commentType: "Evidence");

        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        Assert.Single(body.Retrospectives);
        Assert.Equal(1, body.Total);
    }

    [Fact]
    public async Task List_OrderedByCreatedAtDescending()
    {
        var r1 = SeedRetrospective(agentId: "a1", agentName: "A1", createdAt: DateTime.UtcNow.AddHours(-2));
        var r2 = SeedRetrospective(agentId: "a2", agentName: "A2", createdAt: DateTime.UtcNow.AddHours(-1));
        var r3 = SeedRetrospective(agentId: "a3", agentName: "A3", createdAt: DateTime.UtcNow);

        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        Assert.Equal(3, body.Retrospectives.Count);
        Assert.Equal(r3.Id, body.Retrospectives[0].Id);
        Assert.Equal(r2.Id, body.Retrospectives[1].Id);
        Assert.Equal(r1.Id, body.Retrospectives[2].Id);
    }

    [Fact]
    public async Task List_FiltersbyAgentId()
    {
        SeedRetrospective(agentId: "hephaestus", agentName: "Hephaestus");
        SeedRetrospective(agentId: "athena", agentName: "Athena");
        SeedRetrospective(agentId: "hephaestus", agentName: "Hephaestus");

        var result = await _controller.List(agentId: "athena");
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        Assert.Single(body.Retrospectives);
        Assert.Equal("athena", body.Retrospectives[0].AgentId);
    }

    [Fact]
    public async Task List_Pagination_RespectsLimitAndOffset()
    {
        for (var i = 0; i < 5; i++)
            SeedRetrospective(agentId: $"agent-{i}", agentName: $"Agent {i}",
                createdAt: DateTime.UtcNow.AddMinutes(-i));

        var result = await _controller.List(limit: 2, offset: 1);
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        Assert.Equal(2, body.Retrospectives.Count);
        Assert.Equal(5, body.Total);
        Assert.Equal(2, body.Limit);
        Assert.Equal(1, body.Offset);
    }

    [Fact]
    public async Task List_ClampsBadLimitAndOffset()
    {
        SeedRetrospective();

        var result = await _controller.List(limit: -5, offset: -10);
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        Assert.Equal(1, body.Limit);
        Assert.Equal(0, body.Offset);
    }

    [Fact]
    public async Task List_TruncatesContentPreview()
    {
        var longContent = new string('X', 300);
        SeedRetrospective(content: longContent);

        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        var item = Assert.Single(body.Retrospectives);
        Assert.True(item.ContentPreview.Length <= 201); // 200 + "…"
        Assert.EndsWith("…", item.ContentPreview);
    }

    [Fact]
    public async Task List_ShortContentNotTruncated()
    {
        SeedRetrospective(content: "Short content.");

        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        var item = Assert.Single(body.Retrospectives);
        Assert.Equal("Short content.", item.ContentPreview);
    }

    [Fact]
    public async Task List_IncludesTaskTitle()
    {
        var task = SeedTask(title: "Implement auth module");
        SeedRetrospective(taskId: task.Id);

        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveListResponse>(ok.Value);
        Assert.Equal("Implement auth module", body.Retrospectives[0].TaskTitle);
    }

    [Fact]
    public async Task List_Unauthorized_Returns401()
    {
        SetAuthenticated(false);
        var result = await _controller.List();
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    // ── Get Detail ───────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsRetrospectiveWithTaskMetadata()
    {
        var task = SeedTask(title: "Fix login bug", status: "Done", completedAt: DateTime.UtcNow.AddHours(-2));
        var retro = SeedRetrospective(taskId: task.Id, content: "Full retrospective content here.");

        var result = await _controller.Get(retro.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveDetailResponse>(ok.Value);
        Assert.Equal(retro.Id, body.Id);
        Assert.Equal(task.Id, body.TaskId);
        Assert.Equal("Fix login bug", body.TaskTitle);
        Assert.Equal("Done", body.TaskStatus);
        Assert.Equal("Full retrospective content here.", body.Content);
        Assert.NotNull(body.TaskCompletedAt);
    }

    [Fact]
    public async Task Get_NonExistentId_Returns404()
    {
        var result = await _controller.Get("nonexistent-id");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Get_NonRetrospectiveComment_Returns404()
    {
        var comment = SeedNonRetrospective();
        var result = await _controller.Get(comment.Id);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Get_TaskWithNullCompletedAt_ReturnsNullable()
    {
        var task = SeedTask(status: "Active", completedAt: null);
        // Fix: EF won't set CompletedAt to null via the SeedTask helper, set explicitly
        task.CompletedAt = null;
        _db.SaveChanges();

        var retro = SeedRetrospective(taskId: task.Id);

        var result = await _controller.Get(retro.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveDetailResponse>(ok.Value);
        Assert.Null(body.TaskCompletedAt);
    }

    [Fact]
    public async Task Get_Unauthorized_Returns401()
    {
        SetAuthenticated(false);
        var result = await _controller.Get("any-id");
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    // ── Stats ────────────────────────────────────────────────

    [Fact]
    public async Task Stats_EmptyDatabase_ReturnsZeros()
    {
        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveStatsResponse>(ok.Value);
        Assert.Equal(0, body.TotalRetrospectives);
        Assert.Empty(body.ByAgent);
        Assert.Equal(0, body.AverageContentLength);
        Assert.Null(body.LatestRetrospectiveAt);
    }

    [Fact]
    public async Task Stats_CountsOnlyRetrospectives()
    {
        SeedRetrospective();
        SeedRetrospective();
        SeedNonRetrospective(commentType: "Comment");
        SeedNonRetrospective(commentType: "Finding");

        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveStatsResponse>(ok.Value);
        Assert.Equal(2, body.TotalRetrospectives);
    }

    [Fact]
    public async Task Stats_ByAgent_GroupsCorrectly()
    {
        SeedRetrospective(agentId: "hephaestus", agentName: "Hephaestus");
        SeedRetrospective(agentId: "hephaestus", agentName: "Hephaestus");
        SeedRetrospective(agentId: "athena", agentName: "Athena");

        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveStatsResponse>(ok.Value);
        Assert.Equal(2, body.ByAgent.Count);
        Assert.Equal("hephaestus", body.ByAgent[0].AgentId);
        Assert.Equal(2, body.ByAgent[0].Count);
        Assert.Equal("athena", body.ByAgent[1].AgentId);
        Assert.Equal(1, body.ByAgent[1].Count);
    }

    [Fact]
    public async Task Stats_AverageContentLength_Calculated()
    {
        SeedRetrospective(content: new string('A', 100));
        SeedRetrospective(content: new string('B', 200));

        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveStatsResponse>(ok.Value);
        Assert.Equal(150, body.AverageContentLength);
    }

    [Fact]
    public async Task Stats_LatestRetrospectiveAt_ReturnsNewest()
    {
        var oldest = DateTime.UtcNow.AddDays(-5);
        var newest = DateTime.UtcNow.AddMinutes(-1);
        SeedRetrospective(createdAt: oldest);
        SeedRetrospective(createdAt: newest);

        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RetrospectiveStatsResponse>(ok.Value);
        Assert.NotNull(body.LatestRetrospectiveAt);
        // SQLite datetime precision: check within 1 second
        Assert.InRange(body.LatestRetrospectiveAt!.Value, newest.AddSeconds(-1), newest.AddSeconds(1));
    }

    [Fact]
    public async Task Stats_Unauthorized_Returns401()
    {
        SetAuthenticated(false);
        var result = await _controller.Stats();
        Assert.IsType<UnauthorizedObjectResult>(result);
    }
}
