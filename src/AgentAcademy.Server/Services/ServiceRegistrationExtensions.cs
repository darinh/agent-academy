namespace AgentAcademy.Server.Services;

using AgentAcademy.Server.Services.Contracts;

/// <summary>
/// DI registration extensions for domain services.
/// Extracted from Program.cs to reduce churn — domain services are stable but numerous.
/// </summary>
public static class ServiceRegistrationExtensions
{
    /// <summary>
    /// Registers all scoped domain services that depend on DbContext.
    /// These are the core business-logic services used by controllers and command handlers.
    /// </summary>
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        // Core domain services (scoped — one per request, uses scoped DbContext)
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());

        // Task services: registered as concrete + forwarded interface for backward compat.
        // Consumers should migrate from GetRequiredService<TaskXxxService>() to
        // GetRequiredService<ITaskXxxService>() over time.
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<TaskEvidenceService>();
        services.AddScoped<ITaskEvidenceService>(sp => sp.GetRequiredService<TaskEvidenceService>());
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddScoped<TaskAnalyticsService>();
        services.AddScoped<ITaskAnalyticsService>(sp => sp.GetRequiredService<TaskAnalyticsService>());

        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<AgentLocationService>();
        services.AddScoped<IAgentLocationService>(sp => sp.GetRequiredService<AgentLocationService>());
        services.AddScoped<PlanService>();
        services.AddScoped<IPlanService>(sp => sp.GetRequiredService<PlanService>());
        services.AddScoped<CrashRecoveryService>();
        services.AddScoped<ICrashRecoveryService>(sp => sp.GetRequiredService<CrashRecoveryService>());
        services.AddScoped<InitializationService>();
        services.AddScoped<IInitializationService>(sp => sp.GetRequiredService<InitializationService>());
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
        services.AddScoped<RoomService>();
        services.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
        services.AddScoped<RoomSnapshotBuilder>();
        services.AddScoped<IRoomSnapshotBuilder>(sp => sp.GetRequiredService<RoomSnapshotBuilder>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<IPhaseTransitionValidator>(sp => sp.GetRequiredService<PhaseTransitionValidator>());
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<IRoomLifecycleService>(sp => sp.GetRequiredService<RoomLifecycleService>());
        services.AddScoped<WorkspaceRoomService>();
        services.AddScoped<IWorkspaceRoomService>(sp => sp.GetRequiredService<WorkspaceRoomService>());
        services.AddScoped<WorkspaceService>();
        services.AddScoped<IWorkspaceService>(sp => sp.GetRequiredService<WorkspaceService>());
        services.AddScoped<SearchService>();
        services.AddScoped<ISearchService>(sp => sp.GetRequiredService<SearchService>());

        // Agent config (merges catalog defaults with DB overrides)
        services.AddScoped<AgentConfigService>();
        services.AddScoped<IAgentConfigService>(sp => sp.GetRequiredService<AgentConfigService>());

        // System settings (typed access to system_settings table)
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());

        // Conversation session management (epoch lifecycle and summarization)
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddScoped<ConversationSessionQueryService>();
        services.AddScoped<IConversationSessionQueryService>(sp => sp.GetRequiredService<ConversationSessionQueryService>());
        services.AddScoped<ConversationKickoffService>();
        services.AddScoped<IConversationKickoffService>(sp => sp.GetRequiredService<ConversationKickoffService>());

        // Sprint lifecycle management (creation, stage advancement, artifacts)
        services.AddScoped<SprintService>();
        services.AddScoped<ISprintService>(sp => sp.GetRequiredService<SprintService>());
        services.AddScoped<SprintStageService>();
        services.AddScoped<ISprintStageService>(sp => sp.GetRequiredService<SprintStageService>());
        services.AddScoped<SprintArtifactService>();
        services.AddScoped<ISprintArtifactService>(sp => sp.GetRequiredService<SprintArtifactService>());
        services.AddScoped<SprintMetricsCalculator>();
        services.AddScoped<ISprintMetricsCalculator>(sp => sp.GetRequiredService<SprintMetricsCalculator>());
        services.AddScoped<SprintScheduleService>();
        services.AddScoped<ISprintScheduleService>(sp => sp.GetRequiredService<SprintScheduleService>());

        // Round context loading (extracted from AgentOrchestrator)
        services.AddScoped<RoundContextLoader>();

        // Conversation export (formats room/DM history as JSON/Markdown)
        services.AddScoped<ConversationExportService>();
        services.AddScoped<IConversationExportService>(sp => sp.GetRequiredService<ConversationExportService>());

        // Room artifact tracking (file operation event log)
        services.AddScoped<RoomArtifactTracker>();
        services.AddScoped<IRoomArtifactTracker>(sp => sp.GetRequiredService<RoomArtifactTracker>());

        // Artifact evaluation (quality checks on tracked files)
        services.AddScoped<ArtifactEvaluatorService>();
        services.AddScoped<IArtifactEvaluatorService>(sp => sp.GetRequiredService<ArtifactEvaluatorService>());

        return services;
    }
}
