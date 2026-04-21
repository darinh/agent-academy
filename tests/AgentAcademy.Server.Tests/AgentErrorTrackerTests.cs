using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class AgentErrorTrackerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentErrorTracker _sut;

    public AgentErrorTrackerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        _sut = new AgentErrorTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentErrorTracker>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private AgentAcademyDbContext GetDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    }

    private void SeedErrors(params AgentErrorEntity[] errors)
    {
        using var db = GetDb();
        db.AgentErrors.AddRange(errors);
        db.SaveChanges();
    }

    private static AgentErrorEntity MakeError(
        string agentId = "agent-1",
        string? roomId = "room-1",
        string errorType = "transient",
        string message = "Something went wrong",
        bool recoverable = true,
        bool retried = false,
        int? retryAttempt = null,
        DateTime? occurredAt = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            AgentId = agentId,
            RoomId = roomId,
            ErrorType = errorType,
            Message = message,
            Recoverable = recoverable,
            Retried = retried,
            RetryAttempt = retryAttempt,
            OccurredAt = occurredAt ?? DateTime.UtcNow,
        };

    // ── RecordAsync ──

    [Fact]
    public async Task RecordAsync_PersistsErrorToDatabase()
    {
        await _sut.RecordAsync("agent-1", "room-1", "auth", "Token expired", false);

        using var db = GetDb();
        Assert.Single(await db.AgentErrors.ToListAsync());
    }

    [Fact]
    public async Task RecordAsync_PersistsAllFieldsCorrectly()
    {
        await _sut.RecordAsync(
            "agent-1", "room-1", "quota", "Rate limited",
            recoverable: true, retried: true, retryAttempt: 3);

        using var db = GetDb();
        var e = await db.AgentErrors.SingleAsync();
        Assert.Equal("agent-1", e.AgentId);
        Assert.Equal("room-1", e.RoomId);
        Assert.Equal("quota", e.ErrorType);
        Assert.Equal("Rate limited", e.Message);
        Assert.True(e.Recoverable);
        Assert.True(e.Retried);
        Assert.Equal(3, e.RetryAttempt);
        Assert.True(e.OccurredAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task RecordAsync_HandlesNullRoomId()
    {
        await _sut.RecordAsync("agent-1", null, "unknown", "Error", true);

        using var db = GetDb();
        var e = await db.AgentErrors.SingleAsync();
        Assert.Null(e.RoomId);
    }

    [Fact]
    public async Task RecordAsync_DoesNotThrowOnInternalFailure()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var svc = new ServiceCollection();
        svc.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(conn));
        var sp = svc.BuildServiceProvider();
        var factory = sp.GetRequiredService<IServiceScopeFactory>();
        sp.Dispose();
        conn.Dispose();

        var brokenTracker = new AgentErrorTracker(factory, NullLogger<AgentErrorTracker>.Instance);

        var ex = await Record.ExceptionAsync(() =>
            brokenTracker.RecordAsync("agent-1", "room-1", "test", "msg", true));
        Assert.Null(ex);
    }

    [Fact]
    public async Task RecordAsync_TruncatesLongMessages()
    {
        var longMessage = new string('x', 3000);
        await _sut.RecordAsync("agent-1", "room-1", "transient", longMessage, true);

        using var db = GetDb();
        var e = await db.AgentErrors.SingleAsync();
        Assert.True(e.Message.Length <= 2001);
        Assert.EndsWith("…", e.Message);
    }

    [Fact]
    public async Task RecordAsync_RedactsBearerTokens()
    {
        await _sut.RecordAsync("agent-1", "room-1", "auth", "Header: Bearer abc123secret", true);

        using var db = GetDb();
        var e = await db.AgentErrors.SingleAsync();
        Assert.Contains("[REDACTED]", e.Message);
        Assert.DoesNotContain("abc123secret", e.Message);
    }

    [Fact]
    public async Task RecordAsync_RedactsKeyValues()
    {
        await _sut.RecordAsync("agent-1", "room-1", "config", "api key=myapikey123", true);

        using var db = GetDb();
        var e = await db.AgentErrors.SingleAsync();
        Assert.Contains("[REDACTED]", e.Message);
        Assert.DoesNotContain("myapikey123", e.Message);
    }

    [Fact]
    public async Task RecordAsync_RedactsPasswordValues()
    {
        await _sut.RecordAsync("agent-1", "room-1", "auth", "password=supersecret123", true);

        using var db = GetDb();
        var e = await db.AgentErrors.SingleAsync();
        Assert.Contains("[REDACTED]", e.Message);
        Assert.DoesNotContain("supersecret123", e.Message);
    }

    [Fact]
    public async Task RecordAsync_RedactsSecretValues()
    {
        await _sut.RecordAsync("agent-1", "room-1", "config", "secret=s3cr3tval", true);

        using var db = GetDb();
        var e = await db.AgentErrors.SingleAsync();
        Assert.Contains("[REDACTED]", e.Message);
        Assert.DoesNotContain("s3cr3tval", e.Message);
    }

    [Fact]
    public async Task RecordAsync_RedactsTokenValues()
    {
        await _sut.RecordAsync("agent-1", "room-1", "auth", "token=tok_abc123def", true);

        using var db = GetDb();
        var e = await db.AgentErrors.SingleAsync();
        Assert.Contains("[REDACTED]", e.Message);
        Assert.DoesNotContain("tok_abc123def", e.Message);
    }

    // ── GetRoomErrorsAsync ──

    [Fact]
    public async Task GetRoomErrorsAsync_ReturnsEmptyWhenNoErrorsForRoom()
    {
        var errors = await _sut.GetRoomErrorsAsync("nonexistent");
        Assert.Empty(errors);
    }

    [Fact]
    public async Task GetRoomErrorsAsync_ReturnsErrorsForSpecificRoomOnly()
    {
        SeedErrors(
            MakeError(roomId: "room-1"),
            MakeError(roomId: "room-1"),
            MakeError(roomId: "room-2"));

        var errors = await _sut.GetRoomErrorsAsync("room-1");

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal("room-1", e.RoomId));
    }

    [Fact]
    public async Task GetRoomErrorsAsync_OrdersByOccurredAtDescending()
    {
        var now = DateTime.UtcNow;
        SeedErrors(
            MakeError(roomId: "room-1", occurredAt: now.AddMinutes(-10)),
            MakeError(roomId: "room-1", occurredAt: now.AddMinutes(-5)),
            MakeError(roomId: "room-1", occurredAt: now));

        var errors = await _sut.GetRoomErrorsAsync("room-1");

        Assert.Equal(3, errors.Count);
        Assert.True(errors[0].Timestamp >= errors[1].Timestamp);
        Assert.True(errors[1].Timestamp >= errors[2].Timestamp);
    }

    [Fact]
    public async Task GetRoomErrorsAsync_RespectsLimit()
    {
        SeedErrors(
            MakeError(roomId: "room-1"),
            MakeError(roomId: "room-1"),
            MakeError(roomId: "room-1"),
            MakeError(roomId: "room-1"),
            MakeError(roomId: "room-1"));

        var errors = await _sut.GetRoomErrorsAsync("room-1", limit: 3);

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task GetRoomErrorsAsync_MapsToErrorRecordCorrectly()
    {
        var now = DateTime.UtcNow;
        SeedErrors(MakeError(
            agentId: "agent-x",
            roomId: "room-1",
            errorType: "auth",
            message: "Auth failed",
            recoverable: false,
            occurredAt: now));

        var errors = await _sut.GetRoomErrorsAsync("room-1");

        var e = Assert.Single(errors);
        Assert.Equal("agent-x", e.AgentId);
        Assert.Equal("room-1", e.RoomId);
        Assert.Equal("auth", e.ErrorType);
        Assert.Equal("Auth failed", e.Message);
        Assert.False(e.Recoverable);
        Assert.Equal(now, e.Timestamp);
    }

    // ── GetRecentErrorsAsync ──

    [Fact]
    public async Task GetRecentErrorsAsync_ReturnsAllWhenNoFilters()
    {
        SeedErrors(
            MakeError(agentId: "agent-1"),
            MakeError(agentId: "agent-2"),
            MakeError(agentId: "agent-3"));

        var errors = await _sut.GetRecentErrorsAsync();

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task GetRecentErrorsAsync_FiltersByAgentId()
    {
        SeedErrors(
            MakeError(agentId: "agent-1"),
            MakeError(agentId: "agent-2"));

        var errors = await _sut.GetRecentErrorsAsync(agentId: "agent-1");

        Assert.Single(errors);
        Assert.Equal("agent-1", errors[0].AgentId);
    }

    [Fact]
    public async Task GetRecentErrorsAsync_FiltersBySinceDatetime()
    {
        var now = DateTime.UtcNow;
        SeedErrors(
            MakeError(occurredAt: now.AddHours(-2)),
            MakeError(occurredAt: now.AddMinutes(-5)));

        var errors = await _sut.GetRecentErrorsAsync(since: now.AddHours(-1));

        Assert.Single(errors);
    }

    [Fact]
    public async Task GetRecentErrorsAsync_CombinesAgentIdAndSinceFilters()
    {
        var now = DateTime.UtcNow;
        SeedErrors(
            MakeError(agentId: "agent-1", occurredAt: now.AddMinutes(-5)),
            MakeError(agentId: "agent-1", occurredAt: now.AddHours(-2)),
            MakeError(agentId: "agent-2", occurredAt: now.AddMinutes(-5)));

        var errors = await _sut.GetRecentErrorsAsync(agentId: "agent-1", since: now.AddHours(-1));

        Assert.Single(errors);
        Assert.Equal("agent-1", errors[0].AgentId);
    }

    [Fact]
    public async Task GetRecentErrorsAsync_RespectsLimit()
    {
        SeedErrors(
            MakeError(), MakeError(), MakeError(), MakeError(), MakeError());

        var errors = await _sut.GetRecentErrorsAsync(limit: 2);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task GetRecentErrorsAsync_ReturnsEmptyWhenNoMatches()
    {
        SeedErrors(MakeError(agentId: "agent-1"));

        var errors = await _sut.GetRecentErrorsAsync(agentId: "nonexistent");

        Assert.Empty(errors);
    }

    // ── GetErrorSummaryAsync ──

    [Fact]
    public async Task GetErrorSummaryAsync_ReturnsZerosForEmptyDatabase()
    {
        var summary = await _sut.GetErrorSummaryAsync();

        Assert.Equal(0, summary.TotalErrors);
        Assert.Equal(0, summary.RecoverableErrors);
        Assert.Equal(0, summary.UnrecoverableErrors);
        Assert.Empty(summary.ByType);
        Assert.Empty(summary.ByAgent);
    }

    [Fact]
    public async Task GetErrorSummaryAsync_CountsTotalRecoverableUnrecoverableCorrectly()
    {
        SeedErrors(
            MakeError(recoverable: true),
            MakeError(recoverable: true),
            MakeError(recoverable: false));

        var summary = await _sut.GetErrorSummaryAsync();

        Assert.Equal(3, summary.TotalErrors);
        Assert.Equal(2, summary.RecoverableErrors);
        Assert.Equal(1, summary.UnrecoverableErrors);
    }

    [Fact]
    public async Task GetErrorSummaryAsync_GroupsByErrorType()
    {
        SeedErrors(
            MakeError(errorType: "auth"),
            MakeError(errorType: "auth"),
            MakeError(errorType: "quota"));

        var summary = await _sut.GetErrorSummaryAsync();

        Assert.Equal(2, summary.ByType.Count);
        Assert.Contains(summary.ByType, t => t.ErrorType == "auth" && t.Count == 2);
        Assert.Contains(summary.ByType, t => t.ErrorType == "quota" && t.Count == 1);
    }

    [Fact]
    public async Task GetErrorSummaryAsync_GroupsByAgent()
    {
        SeedErrors(
            MakeError(agentId: "agent-1"),
            MakeError(agentId: "agent-1"),
            MakeError(agentId: "agent-2"));

        var summary = await _sut.GetErrorSummaryAsync();

        Assert.Equal(2, summary.ByAgent.Count);
        Assert.Contains(summary.ByAgent, a => a.AgentId == "agent-1" && a.Count == 2);
        Assert.Contains(summary.ByAgent, a => a.AgentId == "agent-2" && a.Count == 1);
    }

    [Fact]
    public async Task GetErrorSummaryAsync_FiltersBySinceWhenProvided()
    {
        var now = DateTime.UtcNow;
        SeedErrors(
            MakeError(occurredAt: now.AddHours(-2)),
            MakeError(occurredAt: now.AddMinutes(-5)));

        var summary = await _sut.GetErrorSummaryAsync(since: now.AddHours(-1));

        Assert.Equal(1, summary.TotalErrors);
    }
}
