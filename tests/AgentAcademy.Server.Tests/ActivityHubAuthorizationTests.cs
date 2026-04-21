using System.Net;
using System.Net.Http;
using System.Text;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Tests.Fixtures;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class ActivityHubAuthorizationTests : IClassFixture<ApiContractFixture>
{
    private readonly ApiContractFixture _fixture;

    public ActivityHubAuthorizationTests(ApiContractFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ActivityHub_HasAuthorizeAttribute()
    {
        var hasAuthorize = Attribute.IsDefined(typeof(ActivityHub), typeof(AuthorizeAttribute), inherit: true);
        Assert.True(hasAuthorize);
    }

    [Fact]
    public async Task Negotiate_WhenAuthDisabled_AllowsAnonymous()
    {
        using var client = _fixture.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/hubs/activity/negotiate?negotiateVersion=1", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Regression test: with GitHub OAuth enabled, an unauthenticated SignalR negotiate
    /// must return 401 (so the SignalR client surfaces an auth error cleanly) rather
    /// than a 302 redirect to github.com/login/oauth/authorize — which the SignalR
    /// client cannot follow across origins and would surface as a negotiation error.
    /// </summary>
    [Fact]
    public async Task Negotiate_WhenGitHubAuthEnabled_ReturnsUnauthorized()
    {
        using var factory = new GitHubAuthEnabledFactory();
        var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false };
        using var client = factory.CreateClient(options);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/hubs/activity/negotiate?negotiateVersion=1", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedApi_WhenGitHubAuthEnabled_ReturnsUnauthorized()
    {
        using var factory = new GitHubAuthEnabledFactory();
        var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false };
        using var client = factory.CreateClient(options);

        var response = await client.GetAsync("/api/rooms");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Minimal factory that starts the app with GitHub OAuth enabled so we can
    /// exercise the challenge path. External boundaries are stubbed.
    /// </summary>
    private sealed class GitHubAuthEnabledFactory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly SqliteConnection _connection;

        public GitHubAuthEnabledFactory()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(TestPaths.ServerContentRoot);
            builder.UseSetting("GitHub:ClientId", "test-client-id");
            builder.UseSetting("GitHub:ClientSecret", "test-client-secret");
            builder.UseSetting("GitHub:CallbackPath", "/api/auth/callback");
            builder.UseSetting("Auth:FrontendUrl", "http://localhost:5066/");
            builder.UseSetting("ConsultantApi:SharedSecret", "");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AgentAcademyDbContext>>();
                services.RemoveAll<AgentAcademyDbContext>();
                services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));

                var executor = Substitute.For<IAgentExecutor>();
                executor.IsFullyOperational.Returns(true);
                executor.IsAuthFailed.Returns(false);
                executor.CircuitBreakerState.Returns(CircuitState.Closed);
                services.RemoveAll<IAgentExecutor>();
                services.AddSingleton(executor);
                services.RemoveAll<CopilotExecutor>();

                var authProbe = Substitute.For<ICopilotAuthProbe>();
                authProbe.ProbeAsync(Arg.Any<CancellationToken>())
                    .Returns(CopilotAuthProbeResult.Healthy);
                services.RemoveAll<ICopilotAuthProbe>();
                services.AddSingleton(authProbe);

                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _connection.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
