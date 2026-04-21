using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace AgentAcademy.Server.Tests.Fixtures;

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> for API contract tests.
/// Replaces the real database with in-memory SQLite, stubs external boundaries,
/// and disables background services so tests run fast and without side effects.
/// </summary>
public sealed class ApiContractFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private SqliteConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        // Force the factory to build the host (triggers Program.cs startup)
        _ = Server;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Resolve the content root from the test assembly location, NOT from
        // Directory.GetCurrentDirectory(). The default WebApplicationFactory
        // looks for {solutionDir}/AgentAcademy.Server/ which doesn't exist
        // (the project is at src/AgentAcademy.Server/). Using cwd as fallback
        // is fragile because CwdMutating tests can change it process-wide.
        var contentRoot = TestPaths.ServerContentRoot;
        builder.UseContentRoot(contentRoot);

        // Clear auth secrets so the app starts with auth disabled.
        // User secrets from the main project would otherwise leak in.
        // UseSetting takes highest precedence over all config sources.
        builder.UseSetting("GitHub:ClientId", "");
        builder.UseSetting("GitHub:ClientSecret", "");
        builder.UseSetting("ConsultantApi:SharedSecret", "");

        builder.ConfigureServices(services =>
        {
            // ── Replace DbContext with shared in-memory SQLite connection ─────
            services.RemoveAll<DbContextOptions<AgentAcademyDbContext>>();
            services.RemoveAll<AgentAcademyDbContext>();
            services.AddDbContext<AgentAcademyDbContext>(options =>
                options.UseSqlite(_connection));

            // ── Stub IAgentExecutor (prevents LLM calls) ────────────────────
            var executor = Substitute.For<IAgentExecutor>();
            executor.IsFullyOperational.Returns(true);
            executor.IsAuthFailed.Returns(false);
            executor.CircuitBreakerState.Returns(CircuitState.Closed);
            services.RemoveAll<IAgentExecutor>();
            services.AddSingleton(executor);

            // Also replace the concrete CopilotExecutor that's registered
            services.RemoveAll<CopilotExecutor>();

            // ── Stub ICopilotAuthProbe (prevents HTTP calls to GitHub) ───────
            var authProbe = Substitute.For<ICopilotAuthProbe>();
            authProbe.ProbeAsync(Arg.Any<CancellationToken>())
                .Returns(CopilotAuthProbeResult.Healthy);
            services.RemoveAll<ICopilotAuthProbe>();
            services.AddSingleton(authProbe);

            // ── Remove all hosted services to prevent background activity ────
            services.RemoveAll<IHostedService>();
        });
    }
}
