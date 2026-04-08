using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class CommandRateLimiterTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;

    private static AgentDefinition TestAgent() =>
        new("test-1", "TestAgent", "SoftwareEngineer", "Test", "prompt", null,
            new List<string>(), new List<string>(), true, null,
            new CommandPermissionSet(
                Allowed: new List<string> { "*" },
                Denied: new List<string>()));

    public CommandRateLimiterTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void TryAcquire_UnderLimit_ReturnsTrue()
    {
        var limiter = new CommandRateLimiter(maxCommands: 5, windowSeconds: 60);

        Assert.True(limiter.TryAcquire("agent-1", out var retry));
        Assert.Equal(0, retry);
    }

    [Fact]
    public void TryAcquire_AtLimit_ReturnsFalse()
    {
        var limiter = new CommandRateLimiter(maxCommands: 3, windowSeconds: 60);

        Assert.True(limiter.TryAcquire("agent-1", out _));
        Assert.True(limiter.TryAcquire("agent-1", out _));
        Assert.True(limiter.TryAcquire("agent-1", out _));
        Assert.False(limiter.TryAcquire("agent-1", out var retry));
        Assert.True(retry > 0);
    }

    [Fact]
    public void TryAcquire_DifferentAgents_IndependentLimits()
    {
        var limiter = new CommandRateLimiter(maxCommands: 2, windowSeconds: 60);

        Assert.True(limiter.TryAcquire("agent-1", out _));
        Assert.True(limiter.TryAcquire("agent-1", out _));
        Assert.False(limiter.TryAcquire("agent-1", out _));

        // Different agent is unaffected
        Assert.True(limiter.TryAcquire("agent-2", out _));
        Assert.True(limiter.TryAcquire("agent-2", out _));
    }

    [Fact]
    public void GetCurrentCount_ReturnsCorrectCount()
    {
        var limiter = new CommandRateLimiter(maxCommands: 10, windowSeconds: 60);

        Assert.Equal(0, limiter.GetCurrentCount("agent-1"));

        limiter.TryAcquire("agent-1", out _);
        limiter.TryAcquire("agent-1", out _);
        limiter.TryAcquire("agent-1", out _);

        Assert.Equal(3, limiter.GetCurrentCount("agent-1"));
        Assert.Equal(0, limiter.GetCurrentCount("agent-2"));
    }

    [Fact]
    public async Task Pipeline_RateLimited_ReturnsDeniedWithRateLimitCode()
    {
        var rateLimiter = new CommandRateLimiter(maxCommands: 1, windowSeconds: 60);
        var handlers = new ICommandHandler[]
        {
            new RememberHandler()
        };

        var pipeline = new CommandPipeline(
            handlers,
            NullLogger<CommandPipeline>.Instance,
            rateLimiter);

        var agent = TestAgent();

        using var scope = _serviceProvider.CreateScope();

        // First command succeeds
        var result1 = await pipeline.ProcessResponseAsync(
            "test-1",
            "REMEMBER:\n  Category: lesson\n  Key: k1\n  Value: v1",
            "room-1", agent, scope.ServiceProvider);
        Assert.Single(result1.Results);
        Assert.Equal(CommandStatus.Success, result1.Results[0].Status);

        // Second command is rate-limited
        var result2 = await pipeline.ProcessResponseAsync(
            "test-1",
            "REMEMBER:\n  Category: lesson\n  Key: k2\n  Value: v2",
            "room-1", agent, scope.ServiceProvider);
        Assert.Single(result2.Results);
        Assert.Equal(CommandStatus.Denied, result2.Results[0].Status);
        Assert.Equal(CommandErrorCode.RateLimit, result2.Results[0].ErrorCode);
        Assert.Contains("Rate limit exceeded", result2.Results[0].Error);
    }

    [Fact]
    public async Task Pipeline_RateLimited_AuditsWithErrorCode()
    {
        var rateLimiter = new CommandRateLimiter(maxCommands: 1, windowSeconds: 60);
        var handlers = new ICommandHandler[]
        {
            new RememberHandler()
        };

        var pipeline = new CommandPipeline(
            handlers,
            NullLogger<CommandPipeline>.Instance,
            rateLimiter);

        var agent = TestAgent();

        using var scope = _serviceProvider.CreateScope();

        // First command
        await pipeline.ProcessResponseAsync(
            "test-1",
            "REMEMBER:\n  Category: lesson\n  Key: k1\n  Value: v1",
            "room-1", agent, scope.ServiceProvider);

        // Second command — rate-limited
        await pipeline.ProcessResponseAsync(
            "test-1",
            "REMEMBER:\n  Category: lesson\n  Key: k2\n  Value: v2",
            "room-1", agent, scope.ServiceProvider);

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audits = await db.CommandAudits.ToListAsync();

        Assert.Equal(2, audits.Count);
        var success = audits.Single(a => a.Status == "Success");
        var denied = audits.Single(a => a.Status == "Denied");
        Assert.Null(success.ErrorCode);
        Assert.Equal("RATE_LIMIT", denied.ErrorCode);
    }
}
