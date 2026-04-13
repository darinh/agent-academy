using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class SessionControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ConversationSessionQueryService _sessionService;
    private readonly SessionController _controller;

    public SessionControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _sessionService = new ConversationSessionQueryService(_db);

        _controller = new SessionController(
            _sessionService,
            NullLogger<SessionController>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetSessions_ReturnsAllSessions()
    {
        _db.ConversationSessions.AddRange(
            new ConversationSessionEntity { Id = "s1", RoomId = "room-1", SequenceNumber = 1, Status = "Active", MessageCount = 5 },
            new ConversationSessionEntity { Id = "s2", RoomId = "room-2", SequenceNumber = 1, Status = "Archived", MessageCount = 10, ArchivedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetSessions();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<SessionListResponse>(ok.Value);
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(2, body.Sessions.Count);
    }

    [Fact]
    public async Task GetSessions_FiltersbyStatus()
    {
        _db.ConversationSessions.AddRange(
            new ConversationSessionEntity { Id = "s1", RoomId = "room-1", SequenceNumber = 1, Status = "Active" },
            new ConversationSessionEntity { Id = "s2", RoomId = "room-2", SequenceNumber = 1, Status = "Archived", ArchivedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetSessions(status: "Active");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<SessionListResponse>(ok.Value);
        Assert.Equal(1, body.TotalCount);
        Assert.Equal("Active", body.Sessions[0].Status);
    }

    [Fact]
    public async Task GetSessions_PaginatesCorrectly()
    {
        for (int i = 0; i < 30; i++)
        {
            _db.ConversationSessions.Add(new ConversationSessionEntity
            {
                Id = $"p-{i:D2}",
                RoomId = "room-1",
                SequenceNumber = i + 1,
                Status = "Archived",
                CreatedAt = DateTime.UtcNow.AddMinutes(-i),
                ArchivedAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();

        var result = await _controller.GetSessions(limit: 10, offset: 10);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<SessionListResponse>(ok.Value);
        Assert.Equal(30, body.TotalCount);
        Assert.Equal(10, body.Sessions.Count);
    }

    [Fact]
    public async Task GetSessions_ReturnsEmptyWhenNoSessions()
    {
        var result = await _controller.GetSessions();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<SessionListResponse>(ok.Value);
        Assert.Equal(0, body.TotalCount);
        Assert.Empty(body.Sessions);
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCounts()
    {
        _db.ConversationSessions.AddRange(
            new ConversationSessionEntity { Id = "s1", RoomId = "room-1", SequenceNumber = 1, Status = "Active", MessageCount = 10 },
            new ConversationSessionEntity { Id = "s2", RoomId = "room-1", SequenceNumber = 2, Status = "Archived", MessageCount = 20, ArchivedAt = DateTime.UtcNow },
            new ConversationSessionEntity { Id = "s3", RoomId = "room-2", SequenceNumber = 1, Status = "Archived", MessageCount = 30, ArchivedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetStats();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var stats = Assert.IsType<SessionStats>(ok.Value);
        Assert.Equal(3, stats.TotalSessions);
        Assert.Equal(1, stats.ActiveSessions);
        Assert.Equal(2, stats.ArchivedSessions);
        Assert.Equal(60, stats.TotalMessages);
    }

    [Fact]
    public async Task GetStats_ReturnsZerosWhenEmpty()
    {
        var result = await _controller.GetStats();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var stats = Assert.IsType<SessionStats>(ok.Value);
        Assert.Equal(0, stats.TotalSessions);
        Assert.Equal(0, stats.ActiveSessions);
        Assert.Equal(0, stats.ArchivedSessions);
        Assert.Equal(0, stats.TotalMessages);
    }
}
