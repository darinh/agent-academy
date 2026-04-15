using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;

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
        services.AddSingleton<MessageBroadcaster>();
        return services;
    }

    private static IServiceCollection AddCopilotTokenServices(this IServiceCollection services)
    {
        services.AddSingleton<CopilotTokenProvider>();
        services.AddSingleton<TokenPersistenceService>();
        services.AddHostedService(sp => sp.GetRequiredService<TokenPersistenceService>());
        return services;
    }

    private static IServiceCollection AddAgentObservability(this IServiceCollection services)
    {
        services.AddSingleton<LlmUsageTracker>();
        services.AddSingleton<AgentErrorTracker>();
        services.AddSingleton<AgentQuotaService>();
        services.AddSingleton<AgentAnalyticsService>();
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
        services.AddSingleton<WorktreeService>();
        services.AddSingleton<GitHubService>(sp =>
            new GitHubService(
                sp.GetRequiredService<ILogger<GitHubService>>(),
                tokenProvider: sp.GetRequiredService<CopilotTokenProvider>()));
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
        services.AddSingleton<BreakoutLifecycleService>();
        services.AddSingleton<TaskAssignmentHandler>();
        services.AddSingleton<AgentTurnRunner>();
        services.AddSingleton<ConversationRoundRunner>();
        services.AddSingleton<DirectMessageRouter>();
        services.AddSingleton<AgentOrchestrator>();
        return services;
    }
}
