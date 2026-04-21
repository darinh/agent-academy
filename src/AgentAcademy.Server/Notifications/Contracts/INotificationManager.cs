using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Notifications.Contracts;

/// <summary>
/// Manages multiple <see cref="INotificationProvider"/> instances and
/// broadcasts notifications to all connected providers. Thread-safe.
/// </summary>
public interface INotificationManager
{
    /// <summary>
    /// Registers a provider. Overwrites any existing provider with the same
    /// <see cref="INotificationProvider.ProviderId"/>.
    /// </summary>
    void RegisterProvider(INotificationProvider provider);

    /// <summary>
    /// Returns the provider with the given ID, or null if not found.
    /// </summary>
    INotificationProvider? GetProvider(string providerId);

    /// <summary>
    /// Returns all registered providers.
    /// </summary>
    IReadOnlyList<INotificationProvider> GetAllProviders();

    /// <summary>
    /// Sends a notification to all connected providers.
    /// </summary>
    /// <returns>The number of providers that successfully delivered the message.</returns>
    Task<int> SendToAllAsync(NotificationMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests input from the first connected provider that can supply it.
    /// </summary>
    Task<UserResponse?> RequestInputFromAnyAsync(InputRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an agent's question to the human via the first connected provider.
    /// </summary>
    Task<(bool Sent, string? Error)> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a DM to the agent's channel via the first connected provider.
    /// </summary>
    Task<(bool Sent, string? Error)> SendDirectMessageDisplayAsync(AgentQuestion dm, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies all connected providers that a room has been renamed.
    /// </summary>
    Task NotifyRoomRenamedAsync(string roomId, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies all connected providers that a room has been closed/archived.
    /// </summary>
    Task NotifyRoomClosedAsync(string roomId, CancellationToken cancellationToken = default);
}
