using System.Security.Claims;
using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public sealed class DigestControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly DigestController _controller;

    public DigestControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _controller = new DigestController(_db, new AppAuthSetup(true, false, "http://localhost:5173"));
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

    private LearningDigestEntity SeedDigest(string status = "Completed", int memoriesCreated = 3, int retrospectivesProcessed = 5, string summary = "Cross-cutting patterns found")
    {
        var digest = new LearningDigestEntity
        {
            CreatedAt = DateTime.UtcNow,
            Summary = summary,
            MemoriesCreated = memoriesCreated,
            RetrospectivesProcessed = retrospectivesProcessed,
            Status = status,
        };
        _db.LearningDigests.Add(digest);
        _db.SaveChanges();
        return digest;
    }

    private TaskCommentEntity SeedRetrospectiveComment(string taskId = "task-1", string agentId = "agent-1")
    {
        // Ensure the task exists
        if (!_db.Tasks.Any(t => t.Id == taskId))
        {
            _db.Tasks.Add(new TaskEntity
            {
                Id = taskId,
                Title = "Test task",
                Status = "Done",
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }

        var comment = new TaskCommentEntity
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = taskId,
            AgentId = agentId,
            AgentName = agentId,
            CommentType = "Retrospective",
            Content = "Learned about error handling patterns",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Set<TaskCommentEntity>().Add(comment);
        _db.SaveChanges();
        return comment;
    }

    // ── List ─────────────────────────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.List();
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task List_Unauthenticated_WhenAuthDisabled_AllowsAnonymous()
    {
        var openController = new DigestController(_db, new AppAuthSetup(false, false, "http://localhost:5173"));
        openController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };
        var result = await openController.List();
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task List_EmptyDb_ReturnsEmptyList()
    {
        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestListResponse>(ok.Value);
        Assert.Empty(response.Digests);
        Assert.Equal(0, response.Total);
    }

    [Fact]
    public async Task List_ReturnsDigestsOrderedByCreatedAtDesc()
    {
        var older = SeedDigest(summary: "First digest");
        // Small delay to ensure different CreatedAt
        var newer = new LearningDigestEntity
        {
            CreatedAt = DateTime.UtcNow.AddMinutes(1),
            Summary = "Second digest",
            MemoriesCreated = 2,
            RetrospectivesProcessed = 3,
            Status = "Completed",
        };
        _db.LearningDigests.Add(newer);
        await _db.SaveChangesAsync();

        var result = await _controller.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestListResponse>(ok.Value);
        Assert.Equal(2, response.Total);
        Assert.Equal(2, response.Digests.Count);
        Assert.Equal("Second digest", response.Digests[0].Summary);
        Assert.Equal("First digest", response.Digests[1].Summary);
    }

    [Fact]
    public async Task List_FilterByStatus_ReturnsOnlyMatching()
    {
        SeedDigest(status: "Completed", summary: "Completed one");
        SeedDigest(status: "Failed", summary: "Failed one");
        SeedDigest(status: "Pending", summary: "Pending one");

        var result = await _controller.List(status: "Completed");
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestListResponse>(ok.Value);
        Assert.Single(response.Digests);
        Assert.Equal("Completed one", response.Digests[0].Summary);
        Assert.Equal(1, response.Total);
    }

    [Fact]
    public async Task List_FilterByStatus_CaseInsensitive()
    {
        SeedDigest(status: "Completed");

        var result = await _controller.List(status: "completed");
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestListResponse>(ok.Value);
        Assert.Single(response.Digests);
    }

    [Fact]
    public async Task List_InvalidStatus_ReturnsBadRequest()
    {
        var result = await _controller.List(status: "invalid");
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        ProblemDetailsAssert.HasCode(bad.Value, "invalid_status");
    }

    [Fact]
    public async Task List_Pagination_RespectsLimitAndOffset()
    {
        for (int i = 0; i < 5; i++)
            SeedDigest(summary: $"Digest {i}");

        var result = await _controller.List(limit: 2, offset: 1);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestListResponse>(ok.Value);
        Assert.Equal(5, response.Total);
        Assert.Equal(2, response.Digests.Count);
        Assert.Equal(2, response.Limit);
        Assert.Equal(1, response.Offset);
    }

    [Fact]
    public async Task List_LimitClamped_ToRange()
    {
        SeedDigest();

        var result = await _controller.List(limit: 500);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestListResponse>(ok.Value);
        Assert.Equal(100, response.Limit);
    }

    [Fact]
    public async Task List_NegativeOffset_ClampsToZero()
    {
        SeedDigest();

        var result = await _controller.List(offset: -5);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestListResponse>(ok.Value);
        Assert.Equal(0, response.Offset);
    }

    // ── Get ──────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.Get(1);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        var result = await _controller.Get(999);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        ProblemDetailsAssert.HasCode(notFound.Value, "not_found");
    }

    [Fact]
    public async Task Get_ReturnsDigestWithSources()
    {
        var comment = SeedRetrospectiveComment(taskId: "task-42", agentId: "socrates");
        var digest = SeedDigest(summary: "Patterns from sprint 3");

        _db.LearningDigestSources.Add(new LearningDigestSourceEntity
        {
            DigestId = digest.Id,
            RetrospectiveCommentId = comment.Id,
        });
        await _db.SaveChangesAsync();

        var result = await _controller.Get(digest.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestDetailResponse>(ok.Value);
        Assert.Equal(digest.Id, response.Id);
        Assert.Equal("Patterns from sprint 3", response.Summary);
        Assert.Single(response.Sources);
        Assert.Equal(comment.Id, response.Sources[0].CommentId);
        Assert.Equal("task-42", response.Sources[0].TaskId);
        Assert.Equal("socrates", response.Sources[0].AgentId);
    }

    [Fact]
    public async Task Get_DigestWithNoSources_ReturnsEmptySourcesList()
    {
        var digest = SeedDigest(summary: "No sources digest");

        var result = await _controller.Get(digest.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestDetailResponse>(ok.Value);
        Assert.Empty(response.Sources);
    }

    // ── Stats ────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_Unauthenticated_ReturnsUnauthorized()
    {
        SetAuthenticated(false);
        var result = await _controller.Stats();
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Stats_EmptyDb_ReturnsZeros()
    {
        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestStatsResponse>(ok.Value);
        Assert.Equal(0, response.TotalDigests);
        Assert.Equal(0, response.TotalMemoriesCreated);
        Assert.Equal(0, response.TotalRetrospectivesProcessed);
        Assert.Equal(0, response.UndigestedRetrospectives);
        Assert.Null(response.LastCompletedAt);
        Assert.Empty(response.ByStatus);
    }

    [Fact]
    public async Task Stats_ComputesCorrectAggregates()
    {
        SeedDigest(status: "Completed", memoriesCreated: 3, retrospectivesProcessed: 5);
        SeedDigest(status: "Completed", memoriesCreated: 2, retrospectivesProcessed: 4);
        SeedDigest(status: "Failed", memoriesCreated: 0, retrospectivesProcessed: 0);

        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestStatsResponse>(ok.Value);
        Assert.Equal(3, response.TotalDigests);
        Assert.Equal(5, response.TotalMemoriesCreated);
        Assert.Equal(9, response.TotalRetrospectivesProcessed);
        Assert.Equal(2, response.ByStatus["Completed"]);
        Assert.Equal(1, response.ByStatus["Failed"]);
        Assert.NotNull(response.LastCompletedAt);
    }

    [Fact]
    public async Task Stats_UndigestedRetrospectives_CountsCorrectly()
    {
        // Create 3 retrospective comments
        var c1 = SeedRetrospectiveComment(taskId: "task-1", agentId: "agent-1");
        var c2 = SeedRetrospectiveComment(taskId: "task-1", agentId: "agent-2");
        var c3 = SeedRetrospectiveComment(taskId: "task-1", agentId: "agent-3");

        // Digest c1 only (completed)
        var digest = SeedDigest(status: "Completed");
        _db.LearningDigestSources.Add(new LearningDigestSourceEntity
        {
            DigestId = digest.Id,
            RetrospectiveCommentId = c1.Id,
        });
        await _db.SaveChangesAsync();

        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestStatsResponse>(ok.Value);
        Assert.Equal(2, response.UndigestedRetrospectives);
    }

    [Fact]
    public async Task Stats_FailedDigestDoesNotClaimRetrospectives()
    {
        var c1 = SeedRetrospectiveComment();

        // Create a failed digest that "claimed" c1 — sources still exist but digest is Failed
        var failedDigest = SeedDigest(status: "Failed");
        _db.LearningDigestSources.Add(new LearningDigestSourceEntity
        {
            DigestId = failedDigest.Id,
            RetrospectiveCommentId = c1.Id,
        });
        await _db.SaveChangesAsync();

        var result = await _controller.Stats();
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DigestStatsResponse>(ok.Value);
        // The retrospective is claimed by a Failed digest but the query only
        // considers Completed digest claims, so it should show as undigested.
        Assert.Equal(1, response.UndigestedRetrospectives);
    }
}
