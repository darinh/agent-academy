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

    [Fact]
    public async Task ProbeOnceAsync_WhenTokenExpiringSoon_AttemptsRefresh()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.Healthy)
            .WithRefreshResult(new TokenRefreshResult(
                "gho_new_token", "ghr_new_refresh",
                TimeSpan.FromHours(8), TimeSpan.FromDays(180)));
        var executor = Substitute.For<IAgentExecutor>();
        var tokenProvider = new CopilotTokenProvider();
        // Set tokens with an expiry that's already within 30 minutes
        tokenProvider.SetTokens("gho_old", "ghr_old", TimeSpan.FromMinutes(10));
        var sut = new CopilotAuthMonitorService(
            probe, executor, tokenProvider,
            NullLogger<CopilotAuthMonitorService>.Instance);

        await sut.ProbeOnceAsync();

        // Should have refreshed — new token should be set
        Assert.Equal("gho_new_token", tokenProvider.Token);
        Assert.Equal("ghr_new_refresh", tokenProvider.RefreshToken);
        Assert.True(tokenProvider.HasPendingCookieUpdate);
        await executor.Received().MarkAuthOperationalAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeOnceAsync_WhenAuthFailed_TriesRefreshBeforeDegrading()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.AuthFailed)
            .WithRefreshResult(new TokenRefreshResult(
                "gho_refreshed", "ghr_refreshed",
                TimeSpan.FromHours(8), TimeSpan.FromDays(180)));
        var executor = Substitute.For<IAgentExecutor>();
        var tokenProvider = new CopilotTokenProvider();
        tokenProvider.SetTokens("gho_expired", "ghr_valid", TimeSpan.FromHours(8));
        var sut = new CopilotAuthMonitorService(
            probe, executor, tokenProvider,
            NullLogger<CopilotAuthMonitorService>.Instance);

        await sut.ProbeOnceAsync();

        // Should have refreshed instead of degrading
        Assert.Equal("gho_refreshed", tokenProvider.Token);
        await executor.Received().MarkAuthOperationalAsync(Arg.Any<CancellationToken>());
        await executor.DidNotReceive().MarkAuthDegradedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeOnceAsync_WhenAuthFailedAndRefreshFails_DegradesFallback()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.AuthFailed)
            .WithRefreshResult(null); // refresh fails
        var executor = Substitute.For<IAgentExecutor>();
        var tokenProvider = new CopilotTokenProvider();
        tokenProvider.SetTokens("gho_expired", "ghr_also_expired", TimeSpan.FromHours(8));
        var sut = new CopilotAuthMonitorService(
            probe, executor, tokenProvider,
            NullLogger<CopilotAuthMonitorService>.Instance);

        await sut.ProbeOnceAsync();

        // Should degrade since refresh failed
        await executor.Received().MarkAuthDegradedAsync(Arg.Any<CancellationToken>());
        await executor.DidNotReceive().MarkAuthOperationalAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeOnceAsync_WhenAuthFailedAndNoRefreshToken_DegradeImmediately()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.AuthFailed);
        var executor = Substitute.For<IAgentExecutor>();
        var tokenProvider = new CopilotTokenProvider();
        tokenProvider.SetToken("gho_expired"); // no refresh token
        var sut = new CopilotAuthMonitorService(
            probe, executor, tokenProvider,
            NullLogger<CopilotAuthMonitorService>.Instance);

        await sut.ProbeOnceAsync();

        await executor.Received().MarkAuthDegradedAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, probe.RefreshCallCount);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_UpdatesProviderAndMarksCookieUpdate()
    {
        var probe = new StubAuthProbe(CopilotAuthProbeResult.Healthy)
            .WithRefreshResult(new TokenRefreshResult(
                "gho_fresh", "ghr_fresh",
                TimeSpan.FromHours(8), TimeSpan.FromDays(180)));
        var executor = Substitute.For<IAgentExecutor>();
        var tokenProvider = new CopilotTokenProvider();
        tokenProvider.SetTokens("gho_old", "ghr_old", TimeSpan.FromMinutes(5));
        var sut = new CopilotAuthMonitorService(
            probe, executor, tokenProvider,
            NullLogger<CopilotAuthMonitorService>.Instance);

        var result = await sut.TryRefreshTokenAsync();

        Assert.True(result);
        Assert.Equal("gho_fresh", tokenProvider.Token);
        Assert.Equal("ghr_fresh", tokenProvider.RefreshToken);
        Assert.True(tokenProvider.HasPendingCookieUpdate);
        Assert.NotNull(tokenProvider.ExpiresAtUtc);
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

    public Task<TokenRefreshResult?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
        => Task.FromResult<TokenRefreshResult?>(null);
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

    [Fact]
    public async Task RefreshTokenAsync_WhenSuccessful_ReturnsNewTokens()
    {
        var responseJson = """
        {
            "access_token": "ghu_new_access",
            "refresh_token": "ghr_new_refresh",
            "expires_in": 28800,
            "refresh_token_expires_in": 15811200,
            "token_type": "bearer",
            "scope": ""
        }
        """;
        var probe = CreateProbe(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            }),
            config: new Dictionary<string, string?>
            {
                ["GitHub:ClientId"] = "test-client-id",
                ["GitHub:ClientSecret"] = "test-client-secret",
            });

        var result = await probe.RefreshTokenAsync("ghr_old_refresh");

        Assert.NotNull(result);
        Assert.Equal("ghu_new_access", result.AccessToken);
        Assert.Equal("ghr_new_refresh", result.RefreshToken);
        Assert.Equal(TimeSpan.FromSeconds(28800), result.ExpiresIn);
        Assert.Equal(TimeSpan.FromSeconds(15811200), result.RefreshTokenExpiresIn);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenGitHubReturnsError_ReturnsNull()
    {
        var responseJson = """{"error":"bad_refresh_token","error_description":"The refresh token is invalid."}""";
        var probe = CreateProbe(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            }),
            config: new Dictionary<string, string?>
            {
                ["GitHub:ClientId"] = "test-client-id",
                ["GitHub:ClientSecret"] = "test-client-secret",
            });

        var result = await probe.RefreshTokenAsync("ghr_invalid");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenHttpFails_ReturnsNull()
    {
        var probe = CreateProbe(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("server error")
            }),
            config: new Dictionary<string, string?>
            {
                ["GitHub:ClientId"] = "test-client-id",
                ["GitHub:ClientSecret"] = "test-client-secret",
            });

        var result = await probe.RefreshTokenAsync("ghr_test");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenNoClientCredentials_ReturnsNull()
    {
        var probe = CreateProbe(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("should not send")));

        var result = await probe.RefreshTokenAsync("ghr_test");

        Assert.Null(result);
    }

    private static GitHubCopilotAuthProbe CreateProbe(
        HttpMessageHandler handler,
        CopilotTokenProvider? tokenProvider = null,
        Dictionary<string, string?>? config = null)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        var configBuilder = new ConfigurationBuilder();
        if (config is not null)
            configBuilder.AddInMemoryCollection(config);

        return new GitHubCopilotAuthProbe(
            client,
            tokenProvider ?? new CopilotTokenProvider(),
            configBuilder.Build(),
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

public class CopilotTokenProviderRefreshTests
{
    [Fact]
    public void SetTokens_StoresAllFields()
    {
        var provider = new CopilotTokenProvider();

        provider.SetTokens("access", "refresh", TimeSpan.FromHours(8), TimeSpan.FromDays(180));

        Assert.Equal("access", provider.Token);
        Assert.Equal("refresh", provider.RefreshToken);
        Assert.NotNull(provider.ExpiresAtUtc);
        Assert.NotNull(provider.RefreshTokenExpiresAtUtc);
        Assert.NotNull(provider.TokenSetAt);
    }

    [Fact]
    public void IsTokenExpiringSoon_WhenFarFromExpiry_ReturnsFalse()
    {
        var provider = new CopilotTokenProvider();
        provider.SetTokens("access", "refresh", TimeSpan.FromHours(8));

        Assert.False(provider.IsTokenExpiringSoon);
    }

    [Fact]
    public void IsTokenExpiringSoon_WhenWithin30Minutes_ReturnsTrue()
    {
        var provider = new CopilotTokenProvider();
        provider.SetTokens("access", "refresh", TimeSpan.FromMinutes(20));

        Assert.True(provider.IsTokenExpiringSoon);
    }

    [Fact]
    public void IsTokenExpiringSoon_WhenNoExpiry_ReturnsFalse()
    {
        var provider = new CopilotTokenProvider();
        provider.SetToken("access");

        Assert.False(provider.IsTokenExpiringSoon);
    }

    [Fact]
    public void CanRefresh_WhenHasRefreshToken_ReturnsTrue()
    {
        var provider = new CopilotTokenProvider();
        provider.SetTokens("access", "refresh", TimeSpan.FromHours(8), TimeSpan.FromDays(180));

        Assert.True(provider.CanRefresh);
    }

    [Fact]
    public void CanRefresh_WhenNoRefreshToken_ReturnsFalse()
    {
        var provider = new CopilotTokenProvider();
        provider.SetToken("access");

        Assert.False(provider.CanRefresh);
    }

    [Fact]
    public void CanRefresh_WhenRefreshTokenExpired_ReturnsFalse()
    {
        var provider = new CopilotTokenProvider();
        // Set with zero-duration refresh token expiry (already expired)
        provider.SetTokens("access", "refresh", TimeSpan.FromHours(8), TimeSpan.Zero);

        Assert.False(provider.CanRefresh);
    }

    [Fact]
    public void ClearToken_ClearsAllFields()
    {
        var provider = new CopilotTokenProvider();
        provider.SetTokens("access", "refresh", TimeSpan.FromHours(8), TimeSpan.FromDays(180));
        provider.MarkCookieUpdatePending();

        provider.ClearToken();

        Assert.Null(provider.Token);
        Assert.Null(provider.RefreshToken);
        Assert.Null(provider.ExpiresAtUtc);
        Assert.Null(provider.RefreshTokenExpiresAtUtc);
        Assert.Null(provider.TokenSetAt);
        Assert.False(provider.HasPendingCookieUpdate);
    }

    [Fact]
    public void SetTokens_PreservesExistingRefreshToken_WhenNotProvided()
    {
        var provider = new CopilotTokenProvider();
        provider.SetTokens("access1", "refresh1", TimeSpan.FromHours(8));

        // Update access token only (refresh token null) — should keep old refresh
        provider.SetTokens("access2");

        Assert.Equal("access2", provider.Token);
        Assert.Equal("refresh1", provider.RefreshToken);
    }

    [Fact]
    public void SetToken_FiresTokenChanged()
    {
        var provider = new CopilotTokenProvider();
        var fired = false;
        provider.TokenChanged += () => fired = true;

        provider.SetToken("test");

        Assert.True(fired);
    }

    [Fact]
    public void SetTokens_FiresTokenChanged()
    {
        var provider = new CopilotTokenProvider();
        var fired = false;
        provider.TokenChanged += () => fired = true;

        provider.SetTokens("test", "refresh", TimeSpan.FromHours(8));

        Assert.True(fired);
    }

    [Fact]
    public void CookieUpdatePending_WorksCorrectly()
    {
        var provider = new CopilotTokenProvider();

        Assert.False(provider.HasPendingCookieUpdate);

        provider.MarkCookieUpdatePending();
        Assert.True(provider.HasPendingCookieUpdate);

        provider.ClearCookieUpdatePending();
        Assert.False(provider.HasPendingCookieUpdate);
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
        services.AddScoped<ActivityPublisher>();
            services.AddSingleton(new AgentCatalogOptions("main", "Main Room", new List<AgentDefinition>()));
            services.AddSingleton<ILogger<TaskQueryService>>(NullLogger<TaskQueryService>.Instance);
            services.AddSingleton<ILogger<TaskLifecycleService>>(NullLogger<TaskLifecycleService>.Instance);
            services.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
            services.AddScoped<TaskQueryService>();
            services.AddScoped<TaskLifecycleService>();
        services.AddSingleton<ILogger<MessageService>>(NullLogger<MessageService>.Instance);
        services.AddScoped<MessageService>();
        services.AddSingleton<ILogger<BreakoutRoomService>>(NullLogger<BreakoutRoomService>.Instance);
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<RoomService>();
        services.AddScoped<CrashRecoveryService>();
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
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
                var initialization = scope.ServiceProvider.GetRequiredService<InitializationService>();
                await initialization.InitializeAsync();
            }

            var executor = new CopilotExecutor(
                NullLogger<CopilotExecutor>.Instance,
                NullLogger<StubExecutor>.Instance,
                new ConfigurationBuilder().Build(),
                new CopilotTokenProvider(),
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<NotificationManager>(),
                NSubstitute.Substitute.For<IAgentToolRegistry>(),
                new LlmUsageTracker(serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmUsageTracker>.Instance),
                new AgentErrorTracker(serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentErrorTracker>.Instance),
                new AgentQuotaService(serviceProvider.GetRequiredService<IServiceScopeFactory>(), new LlmUsageTracker(serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmUsageTracker>.Instance), NullLogger<AgentQuotaService>.Instance),
                serviceProvider.GetRequiredService<AgentCatalogOptions>());

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
    private TokenRefreshResult? _refreshResult;

    public StubAuthProbe(CopilotAuthProbeResult result)
    {
        _result = result;
    }

    public int RefreshCallCount { get; private set; }

    public StubAuthProbe WithRefreshResult(TokenRefreshResult? result)
    {
        _refreshResult = result;
        return this;
    }

    public Task<CopilotAuthProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_result);
    }

    public Task<TokenRefreshResult?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        RefreshCallCount++;
        return Task.FromResult(_refreshResult);
    }
}
