using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// DI registration for the agent execution pipeline — token management,
/// LLM execution, tool calling, git/GitHub integration, learning, and orchestration.
/// Extracted from Program.cs to reduce churn (4 fix: commits in 30 days).
/// </summary>
public static class AgentPipelineExtensions
{
    /// <summary>
    /// Registers all agent-pipeline singletons. Calls subsystem helpers in order
    /// so related registrations stay grouped and discoverable.
    /// </summary>
    public static IServiceCollection AddAgentPipeline(this IServiceCollection services)
    {
        services.AddBroadcasters();
        services.AddCopilotTokenServices();
        services.AddAgentObservability();
        services.AddAgentTooling();
        services.AddAgentExecution();
        services.AddProjectAndGitServices();
        services.AddLearningServices();
        services.AddOrchestrationServices();
        return services;
    }

    private static IServiceCollection AddBroadcasters(this IServiceCollection services)
    {
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        return services;
    }

    private static IServiceCollection AddCopilotTokenServices(this IServiceCollection services)
    {
        services.AddSingleton<CopilotTokenProvider>();
        services.AddSingleton<ICopilotTokenProvider>(sp => sp.GetRequiredService<CopilotTokenProvider>());
        services.AddSingleton<TokenPersistenceService>();
        services.AddHostedService(sp => sp.GetRequiredService<TokenPersistenceService>());
        return services;
    }

    private static IServiceCollection AddAgentObservability(this IServiceCollection services)
    {
        services.AddSingleton<LlmUsageTracker>();
        services.AddSingleton<ILlmUsageTracker>(sp => sp.GetRequiredService<LlmUsageTracker>());
        services.AddSingleton<AgentErrorTracker>();
        services.AddSingleton<IAgentErrorTracker>(sp => sp.GetRequiredService<AgentErrorTracker>());
        services.AddSingleton<AgentQuotaService>();
        services.AddSingleton<IAgentQuotaService>(sp => sp.GetRequiredService<AgentQuotaService>());
        services.AddSingleton<AgentAnalyticsService>();
        services.AddSingleton<IAgentAnalyticsService>(sp => sp.GetRequiredService<AgentAnalyticsService>());
        return services;
    }

    private static IServiceCollection AddAgentTooling(this IServiceCollection services)
    {
        services.AddSingleton<AgentToolFunctions>();
        services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        return services;
    }

    private static IServiceCollection AddAgentExecution(this IServiceCollection services)
    {
        services.AddSingleton<CopilotClientFactory>();
        services.AddSingleton<CopilotSessionPool>();
        services.AddSingleton<CopilotSdkSender>();
        services.AddSingleton<CopilotExecutor>();
        services.AddSingleton<IAgentExecutor>(sp => sp.GetRequiredService<CopilotExecutor>());
        services.AddHttpClient<ICopilotAuthProbe, GitHubCopilotAuthProbe>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentAcademy.AuthProbe/1.0");
        });
        return services;
    }

    private static IServiceCollection AddProjectAndGitServices(this IServiceCollection services)
    {
        services.AddSingleton<SpecManager>();
        services.AddSingleton<ProjectScanner>();
        services.AddSingleton<GitService>();
        services.AddSingleton<IGitService>(sp => sp.GetRequiredService<GitService>());
        services.AddSingleton<WorktreeService>();
        services.AddSingleton<IWorktreeService>(sp => sp.GetRequiredService<WorktreeService>());
        services.AddSingleton<GitHubService>(sp =>
            new GitHubService(
                sp.GetRequiredService<ILogger<GitHubService>>(),
                tokenProvider: sp.GetRequiredService<ICopilotTokenProvider>()));
        services.AddSingleton<IGitHubService>(sp => sp.GetRequiredService<GitHubService>());
        return services;
    }

    private static IServiceCollection AddLearningServices(this IServiceCollection services)
    {
        services.AddSingleton<AgentMemoryLoader>();
        services.AddSingleton<LearningDigestService>();
        services.AddSingleton<RetrospectiveService>();
        return services;
    }

    private static IServiceCollection AddOrchestrationServices(this IServiceCollection services)
    {
        services.AddSingleton<BreakoutCompletionService>();
        services.AddSingleton<IBreakoutCompletionService>(sp => sp.GetRequiredService<BreakoutCompletionService>());
        services.AddSingleton<BreakoutLifecycleService>();
        services.AddSingleton<IBreakoutLifecycleService>(sp => sp.GetRequiredService<BreakoutLifecycleService>());
        services.AddSingleton<TaskAssignmentHandler>();
        services.AddSingleton<ITaskAssignmentHandler>(sp => sp.GetRequiredService<TaskAssignmentHandler>());
        services.AddSingleton<AgentTurnRunner>();
        services.AddSingleton<ConversationRoundRunner>();
        services.AddSingleton<IConversationRoundRunner>(sp => sp.GetRequiredService<ConversationRoundRunner>());
        services.AddSingleton<DirectMessageRouter>();
        services.AddSingleton<IDirectMessageRouter>(sp => sp.GetRequiredService<DirectMessageRouter>());
        services.AddSingleton<AgentOrchestrator>();
        services.AddSingleton<IAgentOrchestrator>(sp => sp.GetRequiredService<AgentOrchestrator>());
        return services;
    }
}
