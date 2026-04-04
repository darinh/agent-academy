using System.Net;
using System.Net.Http;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class CopilotAuthMonitorServiceTests
{
    [Fact]
    public async Task ProbeOnceAsync_WhenProbeHealthy_MarksOperational()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.Healthy);
        var executor = Substitute.For<IAgentExecutor>();
        var sut = new CopilotAuthMonitorService(
            probe,
            executor,
            new CopilotTokenProvider(),
            NullLogger<CopilotAuthMonitorService>.Instance);

        await sut.ProbeOnceAsync();

        await executor.Received(1).MarkAuthOperationalAsync(Arg.Any<CancellationToken>());
        await executor.DidNotReceive().MarkAuthDegradedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeOnceAsync_WhenProbeAuthFailed_MarksDegraded()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.AuthFailed);
        var executor = Substitute.For<IAgentExecutor>();
        var sut = new CopilotAuthMonitorService(
            probe,
            executor,
            new CopilotTokenProvider(),
            NullLogger<CopilotAuthMonitorService>.Instance);

        await sut.ProbeOnceAsync();

        await executor.Received(1).MarkAuthDegradedAsync(Arg.Any<CancellationToken>());
        await executor.DidNotReceive().MarkAuthOperationalAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeOnceAsync_WhenProbeIsTransient_DoesNotChangeAuthState()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.TransientFailure);
        var executor = Substitute.For<IAgentExecutor>();
        var sut = new CopilotAuthMonitorService(
            probe,
            executor,
            new CopilotTokenProvider(),
            NullLogger<CopilotAuthMonitorService>.Instance);

        await sut.ProbeOnceAsync();

        await executor.DidNotReceive().MarkAuthDegradedAsync(Arg.Any<CancellationToken>());
        await executor.DidNotReceive().MarkAuthOperationalAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeOnceAsync_WhenProbeSkipped_DoesNotChangeAuthState()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.Skipped);
        var executor = Substitute.For<IAgentExecutor>();
        var sut = new CopilotAuthMonitorService(
            probe,
            executor,
            new CopilotTokenProvider(),
            NullLogger<CopilotAuthMonitorService>.Instance);

        await sut.ProbeOnceAsync();

        await executor.DidNotReceive().MarkAuthDegradedAsync(Arg.Any<CancellationToken>());
        await executor.DidNotReceive().MarkAuthOperationalAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TokenChanged_TriggersImmediateProbe()
    {
        var probeCount = 0;
        var probe = new CountingAuthProbe(() =>
        {
            Interlocked.Increment(ref probeCount);
            return CopilotAuthProbeResult.Healthy;
        });
        var executor = Substitute.For<IAgentExecutor>();
        var tokenProvider = new CopilotTokenProvider();
        var sut = new CopilotAuthMonitorService(
            probe,
            executor,
            tokenProvider,
            NullLogger<CopilotAuthMonitorService>.Instance);

        using var cts = new CancellationTokenSource();
        var task = sut.StartAsync(cts.Token);

        // Wait for the initial probe
        await Task.Delay(200);
        var initialCount = probeCount;
        Assert.True(initialCount >= 1, "Initial probe should have fired");

        // Set a token — should trigger an immediate probe
        tokenProvider.SetToken("gho_new_token");
        await Task.Delay(500);

        Assert.True(probeCount > initialCount, "Token change should trigger additional probe");
        await executor.Received().MarkAuthOperationalAsync(Arg.Any<CancellationToken>());

        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);
    }
}

internal sealed class CountingAuthProbe : ICopilotAuthProbe
{
    private readonly Func<CopilotAuthProbeResult> _resultFactory;

    public CountingAuthProbe(Func<CopilotAuthProbeResult> resultFactory)
    {
        _resultFactory = resultFactory;
    }

    public Task<CopilotAuthProbeResult> ProbeAsync(CancellationToken ct = default)
        => Task.FromResult(_resultFactory());
}

public class GitHubCopilotAuthProbeTests
{
    [Fact]
    public async Task ProbeAsync_WhenTokenMissing_ReturnsSkipped()
    {
        var probe = CreateProbe(new StubHttpMessageHandler(_ => throw new InvalidOperationException("should not send")));

        var result = await probe.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.Skipped, result);
    }

    [Fact]
    public async Task ProbeAsync_WhenGitHubReturnsOk_ReturnsHealthy()
    {
        var tokenProvider = new CopilotTokenProvider();
        tokenProvider.SetToken("gho_test");
        var probe = CreateProbe(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), tokenProvider);

        var result = await probe.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.Healthy, result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ProbeAsync_WhenGitHubReturnsAuthFailure_ReturnsAuthFailed(HttpStatusCode statusCode)
    {
        var tokenProvider = new CopilotTokenProvider();
        tokenProvider.SetToken("gho_test");
        var probe = CreateProbe(new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)), tokenProvider);

        var result = await probe.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.AuthFailed, result);
    }

    [Fact]
    public async Task ProbeAsync_WhenGitHubReturnsOtherFailure_ReturnsTransientFailure()
    {
        var tokenProvider = new CopilotTokenProvider();
        tokenProvider.SetToken("gho_test");
        var probe = CreateProbe(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error")
        }), tokenProvider);

        var result = await probe.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.TransientFailure, result);
    }

    private static GitHubCopilotAuthProbe CreateProbe(
        HttpMessageHandler handler,
        CopilotTokenProvider? tokenProvider = null)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        return new GitHubCopilotAuthProbe(
            client,
            tokenProvider ?? new CopilotTokenProvider(),
            new ConfigurationBuilder().Build(),
            NullLogger<GitHubCopilotAuthProbe>.Instance);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}

[Collection("WorkspaceRuntime")]
public class CopilotExecutorAuthTransitionTests
{
    [Fact]
    public async Task MarkAuthDegradedAsync_DebouncesRoomNoticeAndNotification()
    {
        await using var fixture = await CopilotExecutorFixture.CreateAsync();

        await fixture.Executor.MarkAuthDegradedAsync();
        await fixture.Executor.MarkAuthDegradedAsync();

        Assert.True(fixture.Executor.IsAuthFailed);
        Assert.Single(fixture.Provider.Messages.Where(m => m.Title == "Copilot SDK authentication degraded"));

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var authMessages = await db.Messages
            .Where(m => m.RoomId == "main" && m.Content.Contains("Copilot SDK authentication failed"))
            .ToListAsync();

        Assert.Single(authMessages);
    }

    [Fact]
    public async Task MarkAuthOperationalAsync_DebouncesRecoveryNoticeAndNotification()
    {
        await using var fixture = await CopilotExecutorFixture.CreateAsync();
        await fixture.Executor.MarkAuthDegradedAsync();

        fixture.Provider.Messages.Clear();

        await fixture.Executor.MarkAuthOperationalAsync();
        await fixture.Executor.MarkAuthOperationalAsync();

        Assert.False(fixture.Executor.IsAuthFailed);
        Assert.Single(fixture.Provider.Messages.Where(m => m.Title == "Copilot SDK authentication restored"));

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var recoveryMessages = await db.Messages
            .Where(m => m.RoomId == "main" && m.Content.Contains("Copilot SDK reconnected"))
            .ToListAsync();

        Assert.Single(recoveryMessages);
    }

    private sealed class CopilotExecutorFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private CopilotExecutorFixture(
            ServiceProvider services,
            SqliteConnection connection,
            CopilotExecutor executor,
            RecordingNotificationProvider provider)
        {
            Services = services;
            _connection = connection;
            Executor = executor;
            Provider = provider;
        }

        public ServiceProvider Services { get; }
        public CopilotExecutor Executor { get; }
        public RecordingNotificationProvider Provider { get; }

        public static async Task<CopilotExecutorFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<ActivityBroadcaster>();
            services.AddSingleton(new AgentCatalogOptions("main", "Main Room", new List<AgentDefinition>()));
            services.AddSingleton<ILogger<WorkspaceRuntime>>(NullLogger<WorkspaceRuntime>.Instance);
            services.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
            services.AddScoped<WorkspaceRuntime>();
            services.AddScoped<SystemSettingsService>();
            services.AddScoped<ConversationSessionService>();
            services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
            services.AddSingleton(new NotificationManager(NullLogger<NotificationManager>.Instance));

            var provider = new RecordingNotificationProvider();
            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetRequiredService<NotificationManager>().RegisterProvider(provider);

            await using (var scope = serviceProvider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
                await db.Database.EnsureCreatedAsync();
                var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
                await runtime.InitializeAsync();
            }

            var executor = new CopilotExecutor(
                NullLogger<CopilotExecutor>.Instance,
                NullLogger<StubExecutor>.Instance,
                new ConfigurationBuilder().Build(),
                new CopilotTokenProvider(),
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<NotificationManager>());

            return new CopilotExecutorFixture(serviceProvider, connection, executor, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingNotificationProvider : INotificationProvider
    {
        public string ProviderId => "recording";
        public string DisplayName => "Recording";
        public bool IsConfigured => true;
        public bool IsConnected => true;
        public List<NotificationMessage> Messages { get; } = new();

        public Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UserResponse?> RequestInputAsync(InputRequest request, CancellationToken cancellationToken = default) => Task.FromResult<UserResponse?>(null);
        public ProviderConfigSchema GetConfigSchema() => new(ProviderId, DisplayName, "test", new());

        public Task<bool> SendNotificationAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(true);
        }
    }
}

internal sealed class StubAuthProbe : ICopilotAuthProbe
{
    private readonly CopilotAuthProbeResult _result;

    public StubAuthProbe(CopilotAuthProbeResult result)
    {
        _result = result;
    }

    public Task<CopilotAuthProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_result);
    }
}
