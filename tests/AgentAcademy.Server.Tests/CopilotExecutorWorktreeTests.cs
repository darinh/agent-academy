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
using Xunit;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for per-worktree CopilotClient lifecycle management.
/// These run without a real Copilot SDK token — the executor falls
/// back to StubExecutor, but the worktree client management paths
/// are still exercised and verified.
/// </summary>
public sealed class CopilotExecutorWorktreeTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly CopilotExecutor _executor;

    public CopilotExecutorWorktreeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(new AgentCatalogOptions("main", "Main Room", new List<AgentDefinition>()));
        services.AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<AgentCatalogOptions>());
        services.AddSingleton<ILogger<TaskQueryService>>(NullLogger<TaskQueryService>.Instance);
        services.AddSingleton<ILogger<TaskLifecycleService>>(NullLogger<TaskLifecycleService>.Instance);
        services.AddSingleton<ILogger<ConversationSessionService>>(NullLogger<ConversationSessionService>.Instance);
        services.AddScoped<TaskDependencyService>();
        services.AddSingleton<ILogger<TaskDependencyService>>(NullLogger<TaskDependencyService>.Instance);
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
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ConversationSessionService>();
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddSingleton(new NotificationManager(NullLogger<NotificationManager>.Instance));
        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
            scope.ServiceProvider.GetRequiredService<InitializationService>()
                .InitializeAsync().GetAwaiter().GetResult();
        }

        _executor = new CopilotExecutor(
            NullLogger<CopilotExecutor>.Instance,
            NullLogger<StubExecutor>.Instance,
            new CopilotClientFactory(
                NullLogger<CopilotClientFactory>.Instance,
                new ConfigurationBuilder().Build(),
                new CopilotTokenProvider()),
            new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance),
            new CopilotSdkSender(
                NullLogger<CopilotSdkSender>.Instance,
                new LlmUsageTracker(
                    _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<LlmUsageTracker>.Instance),
                new AgentErrorTracker(
                    _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<AgentErrorTracker>.Instance),
                new AgentQuotaService(
                    _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    new LlmUsageTracker(
                        _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                        NullLogger<LlmUsageTracker>.Instance),
                    NullLogger<AgentQuotaService>.Instance),
                new ActivityBroadcaster()),
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _serviceProvider.GetRequiredService<NotificationManager>(),
            Substitute.For<IAgentToolRegistry>(),
            new AgentErrorTracker(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AgentErrorTracker>.Instance),
            new AgentQuotaService(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                new LlmUsageTracker(
                    _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<LlmUsageTracker>.Instance),
                NullLogger<AgentQuotaService>.Instance),
            _serviceProvider.GetRequiredService<AgentCatalogOptions>());
    }

    [Fact]
    public async Task DisposeWorktreeClientAsync_NoOpForNonExistentPath()
    {
        // Should not throw when called with a path that has no cached client.
        await _executor.DisposeWorktreeClientAsync("/tmp/nonexistent-worktree-path");
    }

    [Fact]
    public async Task DisposeWorktreeClientAsync_SafeWhenCalledMultipleTimes()
    {
        var path = "/tmp/worktree-double-dispose";

        // Both calls should succeed without throwing.
        await _executor.DisposeWorktreeClientAsync(path);
        await _executor.DisposeWorktreeClientAsync(path);
    }

    [Fact]
    public async Task DisposeWorktreeClientAsync_NormalizesPath()
    {
        // Paths with trailing separators or relative segments should be
        // normalized the same way. Neither should throw.
        await _executor.DisposeWorktreeClientAsync("/tmp/wt-test/");
        await _executor.DisposeWorktreeClientAsync("/tmp/wt-test/../wt-test");
    }

    [Fact]
    public async Task RunAsync_WithWorkspacePath_FallsBackToStubWhenNoToken()
    {
        var agent = new AgentDefinition(
            Id: "test-agent",
            Name: "Test",
            Role: "Tester",
            Summary: "Test agent",
            StartupPrompt: "You are a test agent.",
            Model: "test-model",
            CapabilityTags: [],
            EnabledTools: [],
            AutoJoinDefaultRoom: false);

        // No token configured → EnsureWorktreeClientAsync returns null →
        // falls back to StubExecutor which produces a stub response.
        var response = await _executor.RunAsync(
            agent, "test prompt", "room-1", "/tmp/test-worktree");

        Assert.NotNull(response);
        Assert.NotEmpty(response);
    }

    [Fact]
    public async Task RunAsync_WithWorkspacePath_ConcurrentCallsSafe()
    {
        var agent = new AgentDefinition(
            Id: "concurrent-agent",
            Name: "Concurrent",
            Role: "Tester",
            Summary: "Concurrent test",
            StartupPrompt: "You are a test agent.",
            Model: "test-model",
            CapabilityTags: [],
            EnabledTools: [],
            AutoJoinDefaultRoom: false);

        // Launch several concurrent calls with different worktree paths.
        // All should complete without deadlock or crash.
        var tasks = Enumerable.Range(0, 5)
            .Select(i => _executor.RunAsync(
                agent, $"prompt {i}", "room-1", $"/tmp/worktree-{i}"))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.NotEmpty(r));
    }

    [Fact]
    public async Task DisposeAsync_SafeAfterWorktreeOperations()
    {
        var agent = new AgentDefinition(
            Id: "dispose-agent",
            Name: "Dispose",
            Role: "Tester",
            Summary: "Dispose test",
            StartupPrompt: "You are a test agent.",
            Model: "test-model",
            CapabilityTags: [],
            EnabledTools: [],
            AutoJoinDefaultRoom: false);

        // Run with worktree path (falls back to stub)
        await _executor.RunAsync(agent, "test", "room-1", "/tmp/dispose-wt");
        await _executor.DisposeWorktreeClientAsync("/tmp/dispose-wt");

        // Dispose should not throw
        await _executor.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _executor.DisposeAsync();
        await _serviceProvider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
