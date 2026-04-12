namespace AgentAcademy.Server.Services;

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
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<TaskEvidenceService>();
        services.AddScoped<MessageService>();
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<CrashRecoveryService>();
        services.AddScoped<InitializationService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<TaskItemService>();
        services.AddScoped<RoomService>();
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<SearchService>();

        // Agent config (merges catalog defaults with DB overrides)
        services.AddScoped<AgentConfigService>();

        // System settings (typed access to system_settings table)
        services.AddScoped<SystemSettingsService>();

        // Conversation session management (epoch lifecycle and summarization)
        services.AddScoped<ConversationSessionService>();

        // Sprint lifecycle management (creation, stage advancement, artifacts)
        services.AddScoped<SprintService>();
        services.AddScoped<SprintArtifactService>();
        services.AddScoped<SprintMetricsCalculator>();

        return services;
    }
}
