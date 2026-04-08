using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Built-in notification provider that logs notifications to the console via <see cref="ILogger"/>.
/// Always configured and connected. Cannot collect user input (returns null).
/// Serves as a reference implementation of <see cref="INotificationProvider"/>.
/// </summary>
public sealed class ConsoleNotificationProvider : INotificationProvider
{
    private readonly ILogger<ConsoleNotificationProvider> _logger;

    public ConsoleNotificationProvider(ILogger<ConsoleNotificationProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderId => "console";

    /// <inheritdoc />
    public string DisplayName => "Console";

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public bool IsConnected => true;

    /// <inheritdoc />
    public Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default)
    {
        // Console provider requires no configuration.
        _logger.LogDebug("Console provider configured (no-op)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Console provider connected (always connected)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Console provider disconnected (no-op)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> SendNotificationAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _logger.LogInformation("[{Type}] {Title}: {Body}",
            message.Type, message.Title, message.Body);

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<UserResponse?> RequestInputAsync(InputRequest request, CancellationToken cancellationToken = default)
    {
        // Console provider cannot collect interactive input.
        _logger.LogDebug("Console provider cannot collect input; returning null for prompt: {Prompt}", request.Prompt);
        return Task.FromResult<UserResponse?>(null);
    }

    /// <inheritdoc />
    public ProviderConfigSchema GetConfigSchema()
    {
        return new ProviderConfigSchema(
            ProviderId: ProviderId,
            DisplayName: DisplayName,
            Description: "Built-in console logger. Always active, no configuration required.",
            Fields: []
        );
    }
}
