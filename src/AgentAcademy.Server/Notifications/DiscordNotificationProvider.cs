using AgentAcademy.Shared.Models;
using Discord;
using Discord.WebSocket;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Notification provider that delivers notifications and collects user input via a Discord bot.
/// Owns the Discord client connection lifecycle and delegates to:
/// <see cref="DiscordChannelManager"/> for channel/category infrastructure,
/// <see cref="DiscordMessageSender"/> for outbound message delivery,
/// <see cref="DiscordMessageRouter"/> for inbound message routing,
/// <see cref="DiscordInputHandler"/> for interactive input collection.
/// </summary>
public sealed class DiscordNotificationProvider : INotificationProvider, IAsyncDisposable
{
    private readonly ILogger<DiscordNotificationProvider> _logger;
    private readonly DiscordChannelManager _channelManager;
    private readonly DiscordInputHandler _inputHandler;
    private readonly DiscordMessageSender _sender;
    private readonly DiscordMessageRouter _router;
    private readonly IDiscordConnectionManager _connection;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private DiscordProviderConfig? _config;
    private bool _messageReceivedHooked;
    private int _disposed;

    public DiscordNotificationProvider(
        ILogger<DiscordNotificationProvider> logger,
        DiscordChannelManager channelManager,
        DiscordInputHandler inputHandler,
        DiscordMessageSender sender,
        DiscordMessageRouter router,
        IDiscordConnectionManager connection)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
        _inputHandler = inputHandler ?? throw new ArgumentNullException(nameof(inputHandler));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc />
    public string ProviderId => "discord";

    /// <inheritdoc />
    public string DisplayName => "Discord";

    /// <inheritdoc />
    public bool IsConfigured => _config is not null;

    /// <inheritdoc />
    public bool IsConnected => _connection.IsConnected;

    /// <inheritdoc />
    public string? LastError => _connection.LastError;

    /// <inheritdoc />
    public Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default)
    {
        var config = DiscordProviderConfig.FromDictionary(configuration);

        // Preserve prior OwnerId across reconfiguration when the new config omits it.
        // This matches the original field-based behavior where OwnerId was sticky:
        // silently widening access scope on reconfigure would be a surprising regression.
        if (config.OwnerId is null && _config?.OwnerId is { } previousOwnerId)
            config = config with { OwnerId = previousOwnerId };

        _config = config;

        _logger.LogInformation("Discord provider configured for guild {GuildId}, channel {ChannelId}, owner {OwnerId}",
            config.GuildId, config.ChannelId, config.OwnerId?.ToString() ?? "(any user)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var config = _config
            ?? throw new InvalidOperationException("Discord provider must be configured before connecting. Call ConfigureAsync first.");

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection.IsConnected)
            {
                _logger.LogDebug("Discord provider is already connected");
                return;
            }

            // Ensure any external subscription is cleared before the connection
            // manager tears down a stale client.
            UnhookRouter();

            await _connection.ConnectAsync(config.BotToken, cancellationToken);

            try
            {
                // Rebuild channel mapping from existing Discord state (survives restarts).
                var guild = _connection.Client!.GetGuild(config.GuildId);
                if (guild is not null)
                    await _channelManager.RebuildAsync(guild);

                // Attach inbound message router AFTER rebuild so channel mappings are ready.
                _connection.Client.MessageReceived += _router.HandleMessageReceivedAsync;
                _messageReceivedHooked = true;
            }
            catch (Exception ex)
            {
                // Post-connect init failed: the client is alive but the provider
                // would be in an inconsistent state. Tear down so that a retry
                // can reconnect cleanly and we don't leak the underlying client.
                _logger.LogError(ex, "Discord provider post-connect initialization failed; tearing down client");
                UnhookRouter();
                try
                {
                    await _connection.DisposeClientAsync();
                }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx, "Error disposing Discord client after failed post-connect init");
                }
                throw;
            }

            _logger.LogInformation("Discord provider connected");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            UnhookRouter();
            await _channelManager.ResetAsync();
            await _connection.DisposeClientAsync();
            _logger.LogInformation("Discord provider disconnected");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendNotificationAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return await ExecuteSafeWithConnectedGuildAsync(
            operationName: "send notification",
            operation: (guild, config) =>
                !string.IsNullOrEmpty(message.RoomId)
                    ? _sender.SendToRoomChannelAsync(guild, config.ChannelId, message, cancellationToken)
                    : _sender.SendToDefaultChannelAsync(guild, config.ChannelId, message, cancellationToken),
            onFailure: ex => _logger.LogError(ex, "Failed to send Discord notification: {Title}", message.Title));
    }

    /// <inheritdoc />
    public async Task<UserResponse?> RequestInputAsync(InputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = GetGuildIfConnected("request input");
        if (resolved is null) return null;
        var (guild, config) = resolved.Value;

        try
        {
            var channel = DiscordMessageSender.ResolveDefaultChannel(guild, config.ChannelId);
            if (channel is null)
            {
                _logger.LogError("Discord channel {ChannelId} not found in guild {GuildId}", config.ChannelId, config.GuildId);
                return null;
            }

            var embed = new EmbedBuilder()
                .WithTitle("Input Requested")
                .WithDescription(request.Prompt)
                .WithColor(Color.Gold)
                .WithCurrentTimestamp();

            if (request.AgentName is not null)
                embed.AddField("Agent", request.AgentName, inline: true);
            if (request.RoomId is not null)
                embed.AddField("Room", request.RoomId, inline: true);

            if (request.Choices is { Count: > 0 })
            {
                return await _inputHandler.RequestChoiceInputAsync(
                    _connection.Client!, channel, embed, request.Choices, ProviderId, cancellationToken);
            }

            if (request.AllowFreeform)
            {
                return await _inputHandler.RequestFreeformInputAsync(
                    _connection.Client!, channel, embed, config.ChannelId, config.OwnerId, ProviderId, cancellationToken);
            }

            _logger.LogWarning("InputRequest has no choices and freeform is disabled — cannot collect input");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to request input via Discord");
            return null;
        }
    }

    /// <inheritdoc />
    public ProviderConfigSchema GetConfigSchema() => new(
        ProviderId: "discord",
        DisplayName: "Discord",
        Description: "Send notifications and receive responses via a Discord bot",
        Fields: new List<ConfigField>
        {
            new("BotToken", "Bot Token", "secret", true,
                "Create a bot at https://discord.com/developers/applications"),
            new("GuildId", "Server ID", "string", true,
                "Right-click your Discord server → Copy Server ID"),
            new("ChannelId", "Channel ID", "string", true,
                "Right-click the notification channel → Copy Channel ID"),
            new("OwnerId", "Owner User ID", "string", false,
                "Your Discord user ID — scopes freeform input to you only (right-click yourself → Copy User ID)")
        }
    );

    /// <inheritdoc cref="IAsyncDisposable.DisposeAsync" />
    public async ValueTask DisposeAsync()
    {
        // Single-winner dispose gate. Interlocked.Exchange ensures exactly one
        // caller proceeds to teardown; other concurrent callers return immediately
        // without touching _connectLock. This prevents the ObjectDisposedException
        // race where one thread could Dispose the semaphore while another was
        // still waiting on or releasing it.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Serialize with any in-flight ConnectAsync/DisconnectAsync so we don't
        // tear down the client mid-connect and leave orphaned handlers.
        await _connectLock.WaitAsync();
        try
        {
            UnhookRouter();
            await _connection.DisposeClientAsync();
        }
        finally
        {
            _connectLock.Release();
            // Intentionally NOT disposing _connectLock. SemaphoreSlim only needs
            // Dispose for its AvailableWaitHandle (unused here), and disposing
            // it while other callers (e.g. a late ConnectAsync) may still race
            // into WaitAsync/Release is precisely the bug we're avoiding.
        }
    }

    /// <summary>
    /// Sends an agent's question to the human via a dedicated Discord channel and thread.
    /// </summary>
    public async Task<bool> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        return await ExecuteSafeWithConnectedGuildAsync(
            operationName: "send agent question",
            operation: (guild, _) => _sender.SendAgentQuestionAsync(guild, question, cancellationToken),
            onFailure: ex => _logger.LogError(ex, "Failed to send agent question from '{AgentName}'", question.AgentName));
    }

    /// <summary>
    /// Posts a DM as a simple embed in the agent's channel (no thread).
    /// </summary>
    public async Task<bool> SendDirectMessageAsync(AgentQuestion dm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dm);

        return await ExecuteSafeWithConnectedGuildAsync(
            operationName: "send direct message",
            operation: (guild, _) => _sender.SendDirectMessageAsync(guild, dm, cancellationToken),
            onFailure: ex => _logger.LogError(ex, "Failed to send DM for '{AgentName}'", dm.AgentName));
    }


    /// <summary>
    /// Renames the Discord channel associated with a room (delegates to channel manager).
    /// </summary>
    public async Task OnRoomRenamedAsync(string roomId, string newName, CancellationToken cancellationToken = default)
    {
        await ExecuteWithConfiguredGuildAsync(
            guild => _channelManager.RenameRoomChannelAsync(guild, roomId, newName));
    }

    /// <inheritdoc />
    public async Task OnRoomClosedAsync(string roomId, CancellationToken cancellationToken = default)
    {
        await ExecuteWithConfiguredGuildAsync(
            guild => _channelManager.DeleteRoomChannelAsync(guild, roomId, cancellationToken));
    }


    #region Private helpers

    /// <summary>
    /// Resolves the Discord guild and current config when the provider is connected.
    /// Logs a warning if disconnected or guild unavailable. Returns null on failure.
    /// </summary>
    private (SocketGuild Guild, DiscordProviderConfig Config)? GetGuildIfConnected(string operationName)
    {
        var client = _connection.Client;
        var config = _config;
        if (!_connection.IsConnected || client is null || config is null)
        {
            _logger.LogWarning("Cannot {Operation} — Discord provider is not connected", operationName);
            return null;
        }

        var guild = client.GetGuild(config.GuildId);
        if (guild is null)
        {
            _logger.LogError("Discord guild {GuildId} not found", config.GuildId);
            return null;
        }

        return (guild, config);
    }

    /// <summary>
    /// Resolves the Discord guild when the provider is configured (but not necessarily fully connected).
    /// Used by best-effort lifecycle operations. Returns null silently if unavailable.
    /// </summary>
    private SocketGuild? GetGuildIfConfigured()
    {
        var client = _connection.Client;
        var config = _config;
        if (client is null || config is null) return null;
        return client.GetGuild(config.GuildId);
    }

    private void UnhookRouter()
    {
        if (!_messageReceivedHooked) return;
        var client = _connection.Client;
        if (client is not null)
            client.MessageReceived -= _router.HandleMessageReceivedAsync;
        _messageReceivedHooked = false;
    }

    private async Task<bool> ExecuteSafeWithConnectedGuildAsync(
        string operationName,
        Func<SocketGuild, DiscordProviderConfig, Task<bool>> operation,
        Action<Exception> onFailure)
    {
        var resolved = GetGuildIfConnected(operationName);
        if (resolved is null) return false;
        var (guild, config) = resolved.Value;

        try
        {
            return await operation(guild, config);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onFailure(ex);
            return false;
        }
    }

    private async Task ExecuteWithConfiguredGuildAsync(Func<SocketGuild, Task> operation)
    {
        var guild = GetGuildIfConfigured();
        if (guild is null) return;

        await operation(guild);
    }

    #endregion
}
