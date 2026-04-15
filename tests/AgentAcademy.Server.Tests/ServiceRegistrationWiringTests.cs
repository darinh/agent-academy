using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
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

        // IActivityBroadcaster should be registered (alias for ActivityBroadcaster)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IActivityBroadcaster));

        // ICopilotTokenProvider should be registered (alias for CopilotTokenProvider)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(ICopilotTokenProvider));

        // IAgentOrchestrator should be registered (alias for AgentOrchestrator)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IAgentOrchestrator));

        // IGitService should be registered (alias for GitService)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IGitService));

        // IWorktreeService should be registered (alias for WorktreeService)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IWorktreeService));

        // ILlmUsageTracker should be registered (alias for LlmUsageTracker)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(ILlmUsageTracker));

        // IAgentErrorTracker should be registered (alias for AgentErrorTracker)
        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IAgentErrorTracker));
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

    [Fact]
    public void AddDomainServices_registers_task_service_interfaces()
    {
        var services = new ServiceCollection();
        services.AddDomainServices();

        var expectedInterfaces = new[]
        {
            typeof(ITaskQueryService),
            typeof(ITaskLifecycleService),
            typeof(ITaskEvidenceService),
            typeof(ITaskDependencyService),
            typeof(ITaskItemService),
            typeof(ITaskOrchestrationService),
            typeof(ITaskAnalyticsService),
            typeof(ICrashRecoveryService),
            typeof(IActivityPublisher),
        };

        foreach (var iface in expectedInterfaces)
        {
            Assert.Contains(services, sd =>
                sd.ServiceType == iface && sd.Lifetime == ServiceLifetime.Scoped);
        }
    }

    [Fact]
    public void AddDomainServices_registers_IAgentConfigService_interface()
    {
        var services = new ServiceCollection();
        services.AddDomainServices();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IAgentConfigService)
            && sd.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddDomainServices_registers_IConversationSessionService_interface()
    {
        var services = new ServiceCollection();
        services.AddDomainServices();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IConversationSessionService)
            && sd.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddDomainServices_interface_forwards_resolve_to_concrete()
    {
        var services = new ServiceCollection();
        services.AddDomainServices();

        // Verify forward registrations use factory pattern (not direct type mapping)
        var forwardedInterfaces = new[]
        {
            typeof(ITaskQueryService),
            typeof(ITaskLifecycleService),
            typeof(ITaskEvidenceService),
            typeof(ITaskDependencyService),
            typeof(ITaskItemService),
            typeof(ITaskOrchestrationService),
            typeof(ITaskAnalyticsService),
            typeof(ICrashRecoveryService),
            typeof(IActivityPublisher),
            typeof(IAgentConfigService),
            typeof(IConversationSessionService),
        };

        foreach (var iface in forwardedInterfaces)
        {
            var descriptor = services.FirstOrDefault(sd => sd.ServiceType == iface);
            Assert.NotNull(descriptor);
            Assert.NotNull(descriptor!.ImplementationFactory);
        }
    }

    [Fact]
    public void AddAgentPipeline_registers_ITaskAssignmentHandler_interface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(ITaskAssignmentHandler)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAgentPipeline_registers_IBreakoutCompletionService_interface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IBreakoutCompletionService)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAgentPipeline_registers_IAgentOrchestrator_interface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IAgentOrchestrator)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAgentPipeline_registers_IGitService_interface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IGitService)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAgentPipeline_IGitService_resolves_to_same_GitService_instance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();
        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<GitService>();
        var iface = provider.GetRequiredService<IGitService>();

        Assert.Same(concrete, iface);
    }

    [Fact]
    public void AddAgentPipeline_registers_IWorktreeService_interface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IWorktreeService)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAgentPipeline_IWorktreeService_resolves_to_same_WorktreeService_instance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();
        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<WorktreeService>();
        var iface = provider.GetRequiredService<IWorktreeService>();

        Assert.Same(concrete, iface);
    }

    [Fact]
    public void AddAgentPipeline_registers_ILlmUsageTracker_interface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(ILlmUsageTracker)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAgentPipeline_ILlmUsageTracker_resolves_to_same_LlmUsageTracker_instance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();
        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<LlmUsageTracker>();
        var iface = provider.GetRequiredService<ILlmUsageTracker>();

        Assert.Same(concrete, iface);
    }

    [Fact]
    public void AddAgentPipeline_registers_IAgentErrorTracker_interface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IAgentErrorTracker)
            && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAgentPipeline_IAgentErrorTracker_resolves_to_same_AgentErrorTracker_instance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPipeline();
        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<AgentErrorTracker>();
        var iface = provider.GetRequiredService<IAgentErrorTracker>();

        Assert.Same(concrete, iface);
    }
}