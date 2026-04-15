using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Verifies that DI extension methods register expected services.
/// Guards against omission bugs when adding new services to extracted groups.
/// </summary>
public sealed class ServiceRegistrationWiringTests
{
    [Fact]
    public void AddAgentPipeline_registers_key_singleton_aliases()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        // IAgentExecutor should be registered (alias for CopilotExecutor)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IAgentExecutor));

        // IGitHubService should be registered (alias for GitHubService)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IGitHubService));

        // IAgentToolRegistry should be registered
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IAgentToolRegistry));
    }

    [Fact]
    public void AddAgentPipeline_registers_all_subsystem_singletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        var expectedTypes = new[]
        {
            // Broadcasters
            typeof(ActivityBroadcaster),
            typeof(MessageBroadcaster),
            // Token management
            typeof(CopilotTokenProvider),
            typeof(TokenPersistenceService),
            // Observability
            typeof(LlmUsageTracker),
            typeof(AgentErrorTracker),
            typeof(AgentQuotaService),
            typeof(AgentAnalyticsService),
            // Tooling
            typeof(AgentToolFunctions),
            // Execution
            typeof(CopilotClientFactory),
            typeof(CopilotSessionPool),
            typeof(CopilotSdkSender),
            typeof(CopilotExecutor),
            // Project & Git
            typeof(SpecManager),
            typeof(ProjectScanner),
            typeof(GitService),
            typeof(WorktreeService),
            typeof(GitHubService),
            // Learning
            typeof(AgentMemoryLoader),
            typeof(LearningDigestService),
            typeof(RetrospectiveService),
            // Orchestration
            typeof(BreakoutCompletionService),
            typeof(BreakoutLifecycleService),
            typeof(TaskAssignmentHandler),
            typeof(AgentTurnRunner),
            typeof(ConversationRoundRunner),
            typeof(DirectMessageRouter),
            typeof(AgentOrchestrator),
        };

        foreach (var type in expectedTypes)
        {
            Assert.Contains(services, sd =>
                sd.ServiceType == type || sd.ImplementationType == type);
        }
    }

    [Fact]
    public void AddBackgroundServices_registers_all_hosted_services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();

        var services = new ServiceCollection();
        services.AddBackgroundServices(configuration);

        // Should register hosted services via IHostedService
        var hostedServiceRegistrations = services
            .Where(sd => sd.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .ToList();

        // ActivityHubBroadcaster, ActivityNotificationBroadcaster, NotificationRestoreService,
        // CopilotAuthMonitorService, PullRequestSyncService, SprintTimeoutService, SprintSchedulerService
        Assert.Equal(7, hostedServiceRegistrations.Count);
    }

    [Fact]
    public void AddAgentPipeline_includes_token_persistence_as_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        // TokenPersistenceService must be both a singleton AND a hosted service
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(TokenPersistenceService));

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }
}
