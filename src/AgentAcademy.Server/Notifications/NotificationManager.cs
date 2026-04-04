using System.Collections.Concurrent;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Manages multiple <see cref="INotificationProvider"/> instances. Thread-safe.
/// Broadcasts notifications to all connected providers and collects input from the first
/// provider that can supply it.
/// </summary>
public sealed class NotificationManager
{
    private readonly ConcurrentDictionary<string, INotificationProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<NotificationManager> _logger;
    private readonly NotificationDeliveryTracker? _tracker;

    public NotificationManager(ILogger<NotificationManager> logger, NotificationDeliveryTracker? tracker = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tracker = tracker;
    }

    /// <summary>
    /// Registers a provider. Overwrites any existing provider with the same <see cref="INotificationProvider.ProviderId"/>.
    /// </summary>
    public void RegisterProvider(INotificationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _providers[provider.ProviderId] = provider;
        _logger.LogInformation("Registered notification provider '{ProviderId}' ({DisplayName})",
            provider.ProviderId, provider.DisplayName);
    }

    /// <summary>
    /// Returns the provider with the given ID, or null if not found.
    /// </summary>
    public INotificationProvider? GetProvider(string providerId)
    {
        _providers.TryGetValue(providerId, out var provider);
        return provider;
    }

    /// <summary>
    /// Returns all registered providers.
    /// </summary>
    public IReadOnlyList<INotificationProvider> GetAllProviders()
        => _providers.Values.ToList().AsReadOnly();

    /// <summary>
    /// Sends a notification to all connected providers. Individual provider failures
    /// are logged and do not prevent delivery to remaining providers.
    /// </summary>
    /// <returns>The number of providers that successfully delivered the message.</returns>
    public async Task<int> SendToAllAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var successCount = 0;
        var providers = _providers.Values.Where(p => p.IsConnected).ToList();

        foreach (var provider in providers)
        {
            try
            {
                var sent = await NotificationRetryPolicy.ExecuteAsync(
                    () => provider.SendNotificationAsync(message, cancellationToken),
                    $"SendNotification({provider.ProviderId})",
                    _logger,
                    cancellationToken);
                if (sent)
                {
                    successCount++;
                    if (_tracker is not null)
                        await _tracker.RecordDeliveryAsync("Broadcast", provider.ProviderId, message.Title, message.Body, message.RoomId, message.AgentName);
                }
                else
                {
                    _logger.LogWarning("Provider '{ProviderId}' returned false for notification '{Title}'",
                        provider.ProviderId, message.Title);
                    if (_tracker is not null)
                        await _tracker.RecordSkippedAsync("Broadcast", provider.ProviderId, message.Title, message.Body, message.RoomId, message.AgentName);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to send notification via provider '{ProviderId}' after retries", provider.ProviderId);
                if (_tracker is not null)
                    await _tracker.RecordFailureAsync("Broadcast", provider.ProviderId, message.Title, message.Body, message.RoomId, message.AgentName, ex.Message);
            }
        }

        if (providers.Count > 0 && successCount == 0)
        {
            _logger.LogWarning("Notification '{Title}' was not delivered by any provider", message.Title);
        }

        return successCount;
    }

    /// <summary>
    /// Requests input from the first connected provider that can supply it.
    /// Tries each connected provider; returns the first non-null response.
    /// Note: iteration order over providers is not guaranteed to match registration order.
    /// </summary>
    /// <returns>The first non-null <see cref="UserResponse"/>, or null if no provider could collect input.</returns>
    public async Task<UserResponse?> RequestInputFromAnyAsync(InputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var provider in _providers.Values.Where(p => p.IsConnected))
        {
            try
            {
                var response = await provider.RequestInputAsync(request, cancellationToken);
                if (response is not null)
                {
                    return response;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to request input via provider '{ProviderId}'", provider.ProviderId);
            }
        }

        return null;
    }

    /// <summary>
    /// Sends an agent's question to the human via the first connected provider that supports it.
    /// The provider handles routing the human's reply back to the agent's room.
    /// </summary>
    /// <returns>A tuple: Sent=true if delivered, Error=detail message if failed.</returns>
    public async Task<(bool Sent, string? Error)> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        var connectedProviders = _providers.Values.Where(p => p.IsConnected).ToList();

        if (connectedProviders.Count == 0)
        {
            _logger.LogWarning("No connected provider could send agent question from '{AgentName}'", question.AgentName);
            return (false, "No notification provider is connected. Ensure Discord is configured and connected in Settings.");
        }

        string? lastError = null;
        foreach (var provider in connectedProviders)
        {
            try
            {
                var sent = await NotificationRetryPolicy.ExecuteAsync(
                    () => provider.SendAgentQuestionAsync(question, cancellationToken),
                    $"SendAgentQuestion({provider.ProviderId})",
                    _logger,
                    cancellationToken);
                if (sent)
                {
                    _logger.LogInformation(
                        "Agent question from '{AgentName}' sent via provider '{ProviderId}'",
                        question.AgentName, provider.ProviderId);
                    if (_tracker is not null)
                        await _tracker.RecordDeliveryAsync("AgentQuestion", provider.ProviderId, question.Question, null, question.RoomId, question.AgentId);
                    return (true, null);
                }
                else
                {
                    if (_tracker is not null)
                        await _tracker.RecordSkippedAsync("AgentQuestion", provider.ProviderId, question.Question, null, question.RoomId, question.AgentId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to send agent question via provider '{ProviderId}' after retries", provider.ProviderId);
                lastError = $"Provider '{provider.ProviderId}' error: {ex.Message}";
                if (_tracker is not null)
                    await _tracker.RecordFailureAsync("AgentQuestion", provider.ProviderId, question.Question, null, question.RoomId, question.AgentId, ex.Message);
            }
        }

        _logger.LogWarning("No connected provider could deliver agent question from '{AgentName}'", question.AgentName);
        return (false, lastError ?? "Connected provider(s) could not deliver the question. Check server logs for details.");
    }

    /// <summary>
    /// Posts a DM to the agent's channel (no thread) via the first connected provider.
    /// </summary>
    public async Task<(bool Sent, string? Error)> SendDirectMessageDisplayAsync(AgentQuestion dm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dm);

        var connectedProviders = _providers.Values.Where(p => p.IsConnected).ToList();
        if (connectedProviders.Count == 0)
            return (false, "No notification provider is connected.");

        string? lastError = null;
        foreach (var provider in connectedProviders)
        {
            try
            {
                var sent = await NotificationRetryPolicy.ExecuteAsync(
                    () => provider.SendDirectMessageAsync(dm, cancellationToken),
                    $"SendDirectMessage({provider.ProviderId})",
                    _logger,
                    cancellationToken);
                if (sent)
                {
                    if (_tracker is not null)
                        await _tracker.RecordDeliveryAsync("DirectMessage", provider.ProviderId, dm.Question, null, dm.RoomId, dm.AgentId);
                    return (true, null);
                }
                else
                {
                    if (_tracker is not null)
                        await _tracker.RecordSkippedAsync("DirectMessage", provider.ProviderId, dm.Question, null, dm.RoomId, dm.AgentId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = $"Provider '{provider.ProviderId}' error: {ex.Message}";
                if (_tracker is not null)
                    await _tracker.RecordFailureAsync("DirectMessage", provider.ProviderId, dm.Question, null, dm.RoomId, dm.AgentId, ex.Message);
            }
        }

        return (false, lastError ?? "Connected provider(s) could not deliver the DM.");
    }

    /// <summary>
    /// Notifies all connected providers that a room has been renamed.
    /// </summary>
    public async Task NotifyRoomRenamedAsync(string roomId, string newName, CancellationToken cancellationToken = default)
    {
        var connectedProviders = _providers.Values.Where(p => p.IsConnected).ToList();
        foreach (var provider in connectedProviders)
        {
            try
            {
                await NotificationRetryPolicy.ExecuteAsync(
                    () => provider.OnRoomRenamedAsync(roomId, newName, cancellationToken),
                    $"NotifyRoomRenamed({provider.ProviderId})",
                    _logger,
                    cancellationToken);
                if (_tracker is not null)
                    await _tracker.RecordDeliveryAsync("RoomRenamed", provider.ProviderId, $"Room renamed to \"{newName}\"", null, roomId, agentId: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to notify provider '{ProviderId}' of room rename after retries", provider.ProviderId);
                if (_tracker is not null)
                    await _tracker.RecordFailureAsync("RoomRenamed", provider.ProviderId, $"Room renamed to \"{newName}\"", null, roomId, agentId: null, ex.Message);
            }
        }
    }
}
