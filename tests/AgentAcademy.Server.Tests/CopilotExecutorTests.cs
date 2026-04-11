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

namespace AgentAcademy.Server.Tests;

public class CopilotExceptionTests
{
    [Fact]
    public void CopilotAuthException_HasCorrectErrorType()
    {
        var ex = new CopilotAuthException("Token expired");
        Assert.Equal("authentication", ex.ErrorType);
        Assert.Contains("Token expired", ex.Message);
    }

    [Fact]
    public void CopilotAuthorizationException_HasCorrectErrorType()
    {
        var ex = new CopilotAuthorizationException("Insufficient scope");
        Assert.Equal("authorization", ex.ErrorType);
        Assert.Contains("Insufficient scope", ex.Message);
    }

    [Fact]
    public void CopilotTransientException_HasCorrectErrorType()
    {
        var ex = new CopilotTransientException("Server error");
        Assert.Equal("transient", ex.ErrorType);
        Assert.Contains("Server error", ex.Message);
    }

    [Fact]
    public void CopilotQuotaException_PreservesOriginalErrorType()
    {
        var quotaEx = new CopilotQuotaException("quota", "Quota exceeded");
        Assert.Equal("quota", quotaEx.ErrorType);

        var rateLimitEx = new CopilotQuotaException("rate_limit", "Rate limited");
        Assert.Equal("rate_limit", rateLimitEx.ErrorType);
    }

    [Fact]
    public void AllExceptions_InheritFromCopilotException()
    {
        Assert.IsAssignableFrom<CopilotException>(new CopilotAuthException("test"));
        Assert.IsAssignableFrom<CopilotException>(new CopilotAuthorizationException("test"));
        Assert.IsAssignableFrom<CopilotException>(new CopilotTransientException("test"));
        Assert.IsAssignableFrom<CopilotException>(new CopilotQuotaException("quota", "test"));
    }
}

public class CopilotTokenProviderTests
{
    [Fact]
    public void Token_IsNull_Initially()
    {
        var provider = new CopilotTokenProvider();
        Assert.Null(provider.Token);
        Assert.Null(provider.TokenSetAt);
    }

    [Fact]
    public void SetToken_StoresTokenAndTimestamp()
    {
        var provider = new CopilotTokenProvider();
        var before = DateTime.UtcNow;

        provider.SetToken("gho_abc123");

        Assert.Equal("gho_abc123", provider.Token);
        Assert.NotNull(provider.TokenSetAt);
        Assert.True(provider.TokenSetAt >= before);
        Assert.True(provider.TokenSetAt <= DateTime.UtcNow);
    }

    [Fact]
    public void ClearToken_ClearsTokenAndTimestamp()
    {
        var provider = new CopilotTokenProvider();
        provider.SetToken("gho_abc123");

        provider.ClearToken();

        Assert.Null(provider.Token);
        Assert.Null(provider.TokenSetAt);
    }

    [Fact]
    public void SetToken_UpdatesTimestamp_OnSubsequentCalls()
    {
        var provider = new CopilotTokenProvider();

        provider.SetToken("token1");
        var firstSetAt = provider.TokenSetAt;

        // Small delay to ensure timestamp changes
        Thread.Sleep(10);

        provider.SetToken("token2");
        var secondSetAt = provider.TokenSetAt;

        Assert.Equal("token2", provider.Token);
        Assert.True(secondSetAt >= firstSetAt);
    }
}

public class ErrorClassificationTests
{
    [Fact]
    public void ClassifyError_AuthenticationError_ReturnsCopilotAuthException()
    {
        var err = CreateSessionErrorEvent("authentication", "Token expired");
        var ex = CopilotExecutor.ClassifyError(err);
        Assert.IsType<CopilotAuthException>(ex);
        Assert.Contains("Token expired", ex.Message);
    }

    [Fact]
    public void ClassifyError_AuthorizationError_ReturnsCopilotAuthorizationException()
    {
        var err = CreateSessionErrorEvent("authorization", "Insufficient scope");
        var ex = CopilotExecutor.ClassifyError(err);
        Assert.IsType<CopilotAuthorizationException>(ex);
    }

    [Fact]
    public void ClassifyError_QuotaError_ReturnsCopilotQuotaException()
    {
        var err = CreateSessionErrorEvent("quota", "Quota exceeded");
        var ex = CopilotExecutor.ClassifyError(err);
        Assert.IsType<CopilotQuotaException>(ex);
    }

    [Fact]
    public void ClassifyError_RateLimitError_ReturnsCopilotQuotaException()
    {
        var err = CreateSessionErrorEvent("rate_limit", "Too many requests");
        var ex = CopilotExecutor.ClassifyError(err);
        Assert.IsType<CopilotQuotaException>(ex);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("")]
    [InlineData("unknown")]
    public void ClassifyError_OtherErrors_ReturnsCopilotTransientException(string errorType)
    {
        var err = CreateSessionErrorEvent(errorType, "Something went wrong");
        var ex = CopilotExecutor.ClassifyError(err);
        Assert.IsType<CopilotTransientException>(ex);
    }

    [Fact]
    public void ClassifyError_NullErrorType_ReturnsCopilotTransientException()
    {
        var err = CreateSessionErrorEvent(null, "Unknown error");
        var ex = CopilotExecutor.ClassifyError(err);
        Assert.IsType<CopilotTransientException>(ex);
    }

    [Fact]
    public void ClassifyError_CaseInsensitive()
    {
        var err = CreateSessionErrorEvent("AUTHENTICATION", "Token expired");
        var ex = CopilotExecutor.ClassifyError(err);
        Assert.IsType<CopilotAuthException>(ex);
    }

    [Fact]
    public void ClassifyError_NullMessage_UsesDefault()
    {
        var err = CreateSessionErrorEvent("authentication", null);
        var ex = CopilotExecutor.ClassifyError(err);
        Assert.IsType<CopilotAuthException>(ex);
        Assert.Equal("Unknown Copilot session error", ex.Message);
    }

    [Fact]
    public void IsAuthFailed_DefaultsFalse()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(new AgentCatalogOptions(
            "main", "Main Room", new List<AgentDefinition>()));
        services.AddSingleton<ILogger<WorkspaceRuntime>>(
            NullLogger<WorkspaceRuntime>.Instance);
        services.AddSingleton<ILogger<TaskQueryService>>(
            NullLogger<TaskQueryService>.Instance);
        services.AddSingleton<ILogger<TaskLifecycleService>>(NullLogger<TaskLifecycleService>.Instance);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddSingleton<ILogger<MessageService>>(NullLogger<MessageService>.Instance);
        services.AddScoped<MessageService>();
        services.AddSingleton<ILogger<BreakoutRoomService>>(NullLogger<BreakoutRoomService>.Instance);
        services.AddScoped<AgentLocationService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<RoomService>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
        services.AddScoped<ConversationSessionService>();
        var sp = services.BuildServiceProvider();

        var executor = new CopilotExecutor(
            NullLogger<CopilotExecutor>.Instance,
            NullLogger<StubExecutor>.Instance,
            new ConfigurationBuilder().Build(),
            new CopilotTokenProvider(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            new NotificationManager(NullLogger<NotificationManager>.Instance),
            NSubstitute.Substitute.For<IAgentToolRegistry>(),
            new LlmUsageTracker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmUsageTracker>.Instance),
            new AgentErrorTracker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentErrorTracker>.Instance),
            new AgentQuotaService(sp.GetRequiredService<IServiceScopeFactory>(), new LlmUsageTracker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmUsageTracker>.Instance), NullLogger<AgentQuotaService>.Instance));

        Assert.False(executor.IsAuthFailed);
        connection.Dispose();
    }

    /// <summary>
    /// Creates a SessionErrorEvent for testing. Uses reflection since
    /// the SDK type may not have a public constructor.
    /// </summary>
    private static GitHub.Copilot.SDK.SessionErrorEvent CreateSessionErrorEvent(
        string? errorType, string? message)
    {
        // SessionErrorEvent has Data.ErrorType and Data.Message.
        // We create a minimal instance for classification testing.
        var dataType = typeof(GitHub.Copilot.SDK.SessionErrorEvent)
            .GetProperty("Data")!.PropertyType;

        var data = Activator.CreateInstance(dataType)!;
        dataType.GetProperty("ErrorType")?.SetValue(data, errorType);
        dataType.GetProperty("Message")?.SetValue(data, message);

        var evt = Activator.CreateInstance(typeof(GitHub.Copilot.SDK.SessionErrorEvent))!;
        typeof(GitHub.Copilot.SDK.SessionErrorEvent)
            .GetProperty("Data")!.SetValue(evt, data);

        return (GitHub.Copilot.SDK.SessionErrorEvent)evt;
    }
}
