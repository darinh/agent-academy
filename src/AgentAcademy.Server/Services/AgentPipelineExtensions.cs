using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.AgentWatchdog;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddSingleton<ITokenPersistenceService>(sp => sp.GetRequiredService<TokenPersistenceService>());
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
        services.AddSingleton<IAgentToolFunctions>(sp => sp.GetRequiredService<AgentToolFunctions>());
        services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        return services;
    }

    private static IServiceCollection AddAgentExecution(this IServiceCollection services)
    {
        // Watchdog liveness tracker — singleton, used by sender + permission
        // handler to record per-turn progress, by AgentTurnRunner to register
        // turns, and by AgentWatchdogService to detect stalls.
        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddOptions<AgentWatchdogOptions>()
            .Configure<IConfiguration>((o, c) => c.GetSection("Orchestrator:AgentWatchdog").Bind(o))
            .Validate(
                o => o.StallThresholdSeconds > 0
                     && o.ScanIntervalSeconds > 0
                     && o.ScanIntervalSeconds <= o.StallThresholdSeconds
                     && o.MaxDenialsPerTurn >= 0,
                "invalid Orchestrator:AgentWatchdog thresholds (stall>0, scan>0, scan<=stall, maxDenials>=0)")
            .ValidateOnStart();
        services.AddSingleton<AgentLivenessTracker>();
        services.AddSingleton<IAgentLivenessTracker>(sp => sp.GetRequiredService<AgentLivenessTracker>());
        services.AddHostedService<AgentWatchdogService>();
        services.AddSingleton<WatchdogAgentRunner>();
        services.AddSingleton<IWatchdogAgentRunner>(sp => sp.GetRequiredService<WatchdogAgentRunner>());

        services.AddSingleton<CopilotClientFactory>();
        services.AddSingleton<ICopilotClientFactory>(sp => sp.GetRequiredService<CopilotClientFactory>());
        services.AddSingleton<CopilotSessionPool>();
        services.AddSingleton<ICopilotSessionPool>(sp => sp.GetRequiredService<CopilotSessionPool>());
        services.AddSingleton<CopilotSdkSender>();
        services.AddSingleton<ICopilotSdkSender>(sp => sp.GetRequiredService<CopilotSdkSender>());
        services.AddSingleton<ICopilotAuthStateNotifier, CopilotAuthStateNotifier>();
        services.AddSingleton<CopilotCircuitBreaker>();
        services.AddSingleton<StubExecutor>();
        services.AddSingleton<CopilotExecutor>(sp => new CopilotExecutor(
            sp.GetRequiredService<ILogger<CopilotExecutor>>(),
            sp.GetRequiredService<ILogger<StubExecutor>>(),
            sp.GetRequiredService<ICopilotClientFactory>(),
            sp.GetRequiredService<ICopilotSessionPool>(),
            sp.GetRequiredService<ICopilotSdkSender>(),
            sp.GetRequiredService<ICopilotAuthStateNotifier>(),
            sp.GetRequiredService<IAgentToolRegistry>(),
            sp.GetRequiredService<IAgentErrorTracker>(),
            sp.GetRequiredService<IAgentQuotaService>(),
            sp.GetRequiredService<IAgentCatalog>(),
            sp.GetRequiredService<IAgentLivenessTracker>(),
            sp.GetRequiredService<CopilotCircuitBreaker>(),
            sp.GetRequiredService<StubExecutor>()));
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
        services.AddSingleton<ISpecManager>(sp => sp.GetRequiredService<SpecManager>());
        services.AddSingleton<ProjectScanner>();
        services.AddSingleton<IProjectScanner>(sp => sp.GetRequiredService<ProjectScanner>());
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
        services.AddSingleton<IAgentMemoryLoader>(sp => sp.GetRequiredService<AgentMemoryLoader>());
        services.AddSingleton<LearningDigestService>();
        services.AddSingleton<ILearningDigestService>(sp => sp.GetRequiredService<LearningDigestService>());
        services.AddSingleton<RetrospectiveService>();
        services.AddSingleton<IRetrospectiveService>(sp => sp.GetRequiredService<RetrospectiveService>());
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
        services.AddSingleton<IAgentTurnRunner>(sp => sp.GetRequiredService<AgentTurnRunner>());
        services.AddSingleton<ConversationRoundRunner>();
        services.AddSingleton<IConversationRoundRunner>(sp => sp.GetRequiredService<ConversationRoundRunner>());

        // ICostGuard is the deferred cost-cap hook documented in
        // p1-2-self-drive-design.md §4.6. Default impl is a no-op so the DI
        // slot exists for the future cost-tracking implementation to swap in
        // without touching the decision-tree wiring.
        services.AddSingleton<Contracts.ICostGuard, NoOpCostGuard>();

        // P1.2 §13 step 5: Self-drive decision service. Singleton — must
        // not inject scoped services directly; resolves them via
        // IServiceScopeFactory per-call. Bound config from
        // Orchestrator:SelfDrive section. Method-scoped binding via
        // OptionsBuilder so we don't need IConfiguration in this signature.
        services.AddOptions<SelfDriveOptions>()
            .Configure<Microsoft.Extensions.Configuration.IConfiguration>((opts, cfg) =>
                cfg.GetSection(SelfDriveOptions.SectionName).Bind(opts));

        // P1.4 self-eval verdict path — cap on submissions before auto-block.
        services.AddOptions<SelfEvalOptions>()
            .Configure<Microsoft.Extensions.Configuration.IConfiguration>((opts, cfg) =>
                cfg.GetSection(SelfEvalOptions.SectionName).Bind(opts));
        services.AddSingleton<Contracts.ISelfDriveDecisionService, SelfDriveDecisionService>();
        services.AddSingleton<DirectMessageRouter>();
        services.AddSingleton<IDirectMessageRouter>(sp => sp.GetRequiredService<DirectMessageRouter>());
        services.AddSingleton<OrchestratorDispatchService>();
        services.AddSingleton<IOrchestratorDispatchService>(sp => sp.GetRequiredService<OrchestratorDispatchService>());
        services.AddSingleton<AgentOrchestrator>();
        services.AddSingleton<IAgentOrchestrator>(sp => sp.GetRequiredService<AgentOrchestrator>());

        // Sprint kickoff lives here (not in AddDomainServices) because it depends on
        // IAgentOrchestrator, which is part of the agent pipeline registration.
        services.AddScoped<SprintKickoffService>();
        services.AddScoped<Contracts.ISprintKickoffService>(sp => sp.GetRequiredService<SprintKickoffService>());

        // Same constraint as kickoff: SprintStageAdvanceAnnouncer depends on
        // IAgentOrchestrator. SprintStageService accepts it as an optional ctor
        // parameter so test fixtures that build the stage service directly
        // (without DI) still work — DI will inject the registered instance in
        // production. P1.3.
        services.AddScoped<SprintStageAdvanceAnnouncer>();
        services.AddScoped<Contracts.ISprintStageAdvanceAnnouncer>(sp => sp.GetRequiredService<SprintStageAdvanceAnnouncer>());
        return services;
    }
}
