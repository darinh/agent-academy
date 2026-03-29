using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Pluggable notification provider interface. Implementations deliver notifications
/// and collect user input via a specific channel (console, Slack, Discord, etc.).
/// </summary>
public interface INotificationProvider
{
    /// <summary>Unique identifier for this provider (e.g., "console", "slack").</summary>
    string ProviderId { get; }

    /// <summary>Human-readable name for UI display.</summary>
    string DisplayName { get; }

    /// <summary>Whether the provider has been configured with required settings.</summary>
    bool IsConfigured { get; }

    /// <summary>Whether the provider is actively connected and able to send notifications.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Applies provider-specific configuration (e.g., API tokens, webhook URLs).
    /// </summary>
    /// <param name="configuration">Key-value pairs matching the provider's <see cref="GetConfigSchema"/> fields.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Establishes the provider's connection (e.g., opens a WebSocket, authenticates with an API).
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tears down the provider's connection gracefully.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers a notification message through this provider's channel.
    /// </summary>
    /// <param name="message">The notification to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the message was delivered successfully.</returns>
    Task<bool> SendNotificationAsync(NotificationMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests input from a user through this provider's channel.
    /// </summary>
    /// <param name="request">The input request describing what's needed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's response, or null if the provider cannot collect input.</returns>
    Task<UserResponse?> RequestInputAsync(InputRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the configuration schema describing what fields this provider requires.
    /// </summary>
    ProviderConfigSchema GetConfigSchema();

    /// <summary>
    /// Sends an agent's question to the human via this provider's channel.
    /// The provider is responsible for routing the human's reply back to the agent's room.
    /// Returns true if the question was sent successfully.
    /// </summary>
    /// <remarks>Default implementation returns false (provider does not support agent questions).</remarks>
    Task<bool> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Notifies the provider that a room has been renamed, allowing it to update
    /// any external resources (e.g., Discord channel names).
    /// </summary>
    /// <remarks>Default implementation is a no-op.</remarks>
    Task OnRoomRenamedAsync(string roomId, string newName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
