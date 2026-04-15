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

public class StubExecutorTests
{
    private readonly StubExecutor _sut = new(NullLogger<StubExecutor>.Instance);

    private static AgentDefinition MakeAgent(string role, string id = "test-1", string name = "TestBot") =>
        new(
            Id: id,
            Name: name,
            Role: role,
            Summary: "Test agent",
            StartupPrompt: "You are a test agent.",
            Model: null,
            CapabilityTags: new List<string>(),
            EnabledTools: new List<string>(),
            AutoJoinDefaultRoom: true
        );

    [Fact]
    public void IsFullyOperational_ReturnsFalse()
    {
        Assert.False(_sut.IsFullyOperational);
    }

    [Theory]
    [InlineData("Planner")]
    [InlineData("Architect")]
    [InlineData("SoftwareEngineer")]
    [InlineData("Reviewer")]
    [InlineData("TechnicalWriter")]
    public async Task RunAsync_ReturnsOfflineNotice_ForKnownRoles(string role)
    {
        var agent = MakeAgent(role);
        var result = await _sut.RunAsync(agent, "Title: Build a widget", "room-1");

        Assert.Contains("offline", result);
        Assert.Contains(agent.Name, result);
        Assert.Contains(agent.Role, result);
        Assert.Contains("Copilot SDK", result);
    }

    [Fact]
    public async Task RunAsync_ReturnsOfflineNotice_ForUnknownRole()
    {
        var agent = MakeAgent("UnknownRole");
        var result = await _sut.RunAsync(agent, "Do something", null);

        Assert.Contains("offline", result);
        Assert.Contains("UnknownRole", result);
    }

    [Fact]
    public async Task RunAsync_ThrowsOnCancellation()
    {
        var agent = MakeAgent("Planner");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.RunAsync(agent, "Hello", "room-1", workspacePath: null, cts.Token));
    }

    [Fact]
    public async Task InvalidateSessionAsync_DoesNotThrow()
    {
        await _sut.InvalidateSessionAsync("agent-1", "room-1");
    }

    [Fact]
    public async Task InvalidateRoomSessionsAsync_DoesNotThrow()
    {
        await _sut.InvalidateRoomSessionsAsync("room-1");
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        await _sut.DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_WithNullRoomId_ReturnsOfflineNotice()
    {
        var agent = MakeAgent("Architect");
        var result = await _sut.RunAsync(agent, "Title: Design API", null);

        Assert.Contains("offline", result);
        Assert.Contains(agent.Name, result);
    }

    [Fact]
    public async Task RunAsync_ReturnsDeterministicMessage()
    {
        var agent = MakeAgent("Planner");
        var responses = new HashSet<string>();
        for (int i = 0; i < 5; i++)
        {
            var result = await _sut.RunAsync(agent, "Title: test", "room-1");
            responses.Add(result);
        }

        Assert.Single(responses);
    }
}

public class AgentExecutorInterfaceTests
{
    [Fact]
    public void StubExecutor_ImplementsIAgentExecutor()
    {
        var executor = new StubExecutor(NullLogger<StubExecutor>.Instance);
        Assert.IsAssignableFrom<IAgentExecutor>(executor);
    }

    [Fact]
    public void CopilotExecutor_ImplementsIAgentExecutor()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(new AgentCatalogOptions("main", "Main Room", new List<AgentDefinition>()));
        services.AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<AgentCatalogOptions>());
        services.AddSingleton<ILogger<TaskQueryService>>(NullLogger<TaskQueryService>.Instance);
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
        Assert.IsAssignableFrom<IAgentExecutor>(executor);
        connection.Dispose();
    }
}
