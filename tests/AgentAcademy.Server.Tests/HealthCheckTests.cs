using AgentAcademy.Server.Data;
using AgentAcademy.Server.HealthChecks;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class HealthCheckTests : IDisposable
{
    private readonly AgentAcademyDbContext _db;

    public HealthCheckTests()
    {
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    // ── DatabaseHealthCheck ─────────────────────────────────────────────────

    [Fact]
    public async Task Database_Healthy_WhenConnectable()
    {
        var check = new DatabaseHealthCheck(_db);
        var result = await check.CheckHealthAsync(CreateContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("reachable", result.Description);
    }

    [Fact]
    public async Task Database_Unhealthy_WhenDisposed()
    {
        var badOptions = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var badDb = new AgentAcademyDbContext(badOptions);
        // Don't open connection or ensure created — CanConnect will fail
        badDb.Dispose();

        var check = new DatabaseHealthCheck(badDb);
        var result = await check.CheckHealthAsync(CreateContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // ── AgentExecutorHealthCheck ────────────────────────────────────────────

    [Fact]
    public async Task Executor_Healthy_WhenOperational()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsFullyOperational.Returns(true);
        executor.IsAuthFailed.Returns(false);
        executor.CircuitBreakerState.Returns(CircuitState.Closed);

        var check = new AgentExecutorHealthCheck(executor);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("operational", result.Description!);
    }

    [Fact]
    public async Task Executor_Degraded_WhenAuthFailed()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsFullyOperational.Returns(true);
        executor.IsAuthFailed.Returns(true);
        executor.CircuitBreakerState.Returns(CircuitState.Open);

        var check = new AgentExecutorHealthCheck(executor);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("authentication", result.Description!);
    }

    [Fact]
    public async Task Executor_Degraded_WhenNotOperational()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsFullyOperational.Returns(false);
        executor.IsAuthFailed.Returns(false);
        executor.CircuitBreakerState.Returns(CircuitState.Closed);

        var check = new AgentExecutorHealthCheck(executor);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("not fully operational", result.Description!);
    }

    [Fact]
    public async Task Executor_IncludesDataDictionary()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsFullyOperational.Returns(true);
        executor.IsAuthFailed.Returns(false);
        executor.CircuitBreakerState.Returns(CircuitState.Closed);

        var check = new AgentExecutorHealthCheck(executor);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.NotNull(result.Data);
        Assert.True(result.Data.ContainsKey("operational"));
        Assert.True(result.Data.ContainsKey("authFailed"));
        Assert.True(result.Data.ContainsKey("circuitBreaker"));
    }

    private static HealthCheckContext CreateContext() => new()
    {
        Registration = new HealthCheckRegistration("test", Substitute.For<IHealthCheck>(), null, null),
    };
}
