using System.Reflection;
using AgentAcademy.Server.Notifications;

namespace AgentAcademy.Server.Commands;

/// <summary>
/// DI registration extensions for the command system and notification providers.
/// Extracted from Program.cs to reduce churn — new handlers are auto-discovered.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the command pipeline, rate limiter, and all <see cref="ICommandHandler"/>
    /// implementations found in the executing assembly.
    /// </summary>
    public static IServiceCollection AddCommandSystem(this IServiceCollection services)
    {
        services.AddSingleton<CommandRateLimiter>();
        services.AddSingleton<CommandPipeline>();

        // Auto-discover all ICommandHandler implementations in this assembly
        var handlerTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(ICommandHandler).IsAssignableFrom(t));

        foreach (var handlerType in handlerTypes)
        {
            services.AddSingleton(typeof(ICommandHandler), handlerType);
        }

        return services;
    }

    /// <summary>
    /// Registers notification infrastructure: encryption, delivery tracking,
    /// manager, and all built-in providers.
    /// </summary>
    public static IServiceCollection AddNotificationSystem(this IServiceCollection services)
    {
        services.AddSingleton<ConfigEncryptionService>();
        services.AddSingleton<NotificationDeliveryTracker>();
        services.AddSingleton<NotificationManager>();
        services.AddSingleton<ConsoleNotificationProvider>();
        services.AddSingleton<DiscordChannelManager>();
        services.AddSingleton<DiscordInputHandler>();
        services.AddSingleton<DiscordMessageSender>();
        services.AddSingleton<DiscordMessageRouter>();
        services.AddSingleton<DiscordNotificationProvider>();
        services.AddSingleton<SlackNotificationProvider>();
        services.AddHttpClient("Slack");

        return services;
    }
}
