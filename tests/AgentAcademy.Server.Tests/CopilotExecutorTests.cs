using AgentAcademy.Server.Data;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
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

// CopilotTokenProviderTests moved to CopilotTokenProviderTests.cs

public class ErrorClassificationTests
{
    [Fact]
    public void ClassifyError_AuthenticationError_ReturnsCopilotAuthException()
    {
        var err = CreateSessionErrorEvent("authentication", "Token expired");
        var ex = CopilotSdkSender.ClassifyError(err);
        Assert.IsType<CopilotAuthException>(ex);
        Assert.Contains("Token expired", ex.Message);
    }

    [Fact]
    public void ClassifyError_AuthorizationError_ReturnsCopilotAuthorizationException()
    {
        var err = CreateSessionErrorEvent("authorization", "Insufficient scope");
        var ex = CopilotSdkSender.ClassifyError(err);
        Assert.IsType<CopilotAuthorizationException>(ex);
    }

    [Fact]
    public void ClassifyError_QuotaError_ReturnsCopilotQuotaException()
    {
        var err = CreateSessionErrorEvent("quota", "Quota exceeded");
        var ex = CopilotSdkSender.ClassifyError(err);
        Assert.IsType<CopilotQuotaException>(ex);
    }

    [Fact]
    public void ClassifyError_RateLimitError_ReturnsCopilotQuotaException()
    {
        var err = CreateSessionErrorEvent("rate_limit", "Too many requests");
        var ex = CopilotSdkSender.ClassifyError(err);
        Assert.IsType<CopilotQuotaException>(ex);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("")]
    [InlineData("unknown")]
    public void ClassifyError_OtherErrors_ReturnsCopilotTransientException(string errorType)
    {
        var err = CreateSessionErrorEvent(errorType, "Something went wrong");
        var ex = CopilotSdkSender.ClassifyError(err);
        Assert.IsType<CopilotTransientException>(ex);
    }

    [Fact]
    public void ClassifyError_NullErrorType_ReturnsCopilotTransientException()
    {
        var err = CreateSessionErrorEvent(null, "Unknown error");
        var ex = CopilotSdkSender.ClassifyError(err);
        Assert.IsType<CopilotTransientException>(ex);
    }

    [Fact]
    public void ClassifyError_CaseInsensitive()
    {
        var err = CreateSessionErrorEvent("AUTHENTICATION", "Token expired");
        var ex = CopilotSdkSender.ClassifyError(err);
        Assert.IsType<CopilotAuthException>(ex);
    }

    [Fact]
    public void ClassifyError_NullMessage_UsesDefault()
    {
        var err = CreateSessionErrorEvent("authentication", null);
        var ex = CopilotSdkSender.ClassifyError(err);
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
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(new AgentCatalogOptions(
            "main", "Main Room", new List<AgentDefinition>()));
        services.AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<AgentCatalogOptions>());
        services.AddSingleton<ILogger<TaskQueryService>>(
            NullLogger<TaskQueryService>.Instance);
        services.AddSingleton<ILogger<TaskLifecycleService>>(NullLogger<TaskLifecycleService>.Instance);
        services.AddScoped<TaskDependencyService>();
        services.AddSingleton<ILogger<TaskDependencyService>>(NullLogger<TaskDependencyService>.Instance);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddSingleton<ILogger<MessageService>>(NullLogger<MessageService>.Instance);
        services.AddScoped<MessageService>();
        services.AddSingleton<ILogger<BreakoutRoomService>>(NullLogger<BreakoutRoomService>.Instance);
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<RoomService>();
        services.AddScoped<RoomSnapshotBuilder>();
        services.AddSingleton<ILogger<WorkspaceRoomService>>(NullLogger<WorkspaceRoomService>.Instance);
        services.AddScoped<WorkspaceRoomService>();
        services.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<CrashRecoveryService>();
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
        services.AddScoped<ConversationSessionService>();
        var sp = services.BuildServiceProvider();

        var executor = new CopilotExecutor(
            NullLogger<CopilotExecutor>.Instance,
            NullLogger<StubExecutor>.Instance,
            new CopilotClientFactory(
                NullLogger<CopilotClientFactory>.Instance,
                new ConfigurationBuilder().Build(),
                new CopilotTokenProvider()),
            new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance),
            new CopilotSdkSender(
                NullLogger<CopilotSdkSender>.Instance,
                new LlmUsageTracker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmUsageTracker>.Instance),
                new AgentErrorTracker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentErrorTracker>.Instance),
                new AgentQuotaService(sp.GetRequiredService<IServiceScopeFactory>(), new LlmUsageTracker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmUsageTracker>.Instance), NullLogger<AgentQuotaService>.Instance), new ActivityBroadcaster()),
            sp.GetRequiredService<IServiceScopeFactory>(),
            new NotificationManager(NullLogger<NotificationManager>.Instance),
            NSubstitute.Substitute.For<IAgentToolRegistry>(),
            new AgentErrorTracker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentErrorTracker>.Instance),
            new AgentQuotaService(sp.GetRequiredService<IServiceScopeFactory>(), new LlmUsageTracker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmUsageTracker>.Instance), NullLogger<AgentQuotaService>.Instance),
            new AgentCatalogOptions("main", "Main", []));

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
