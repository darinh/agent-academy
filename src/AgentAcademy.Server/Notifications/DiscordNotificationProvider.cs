using AgentAcademy.Shared.Models;
using Discord;
using Discord.WebSocket;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Notification provider that delivers notifications and collects user input via a Discord bot.
///
/// <para>
/// The provider is a thin adapter over its collaborators:
/// <see cref="DiscordChannelManager"/> for channel/category infrastructure,
/// <see cref="DiscordMessageSender"/> for outbound message delivery,
/// <see cref="DiscordMessageRouter"/> for inbound message routing,
/// <see cref="DiscordInputHandler"/> for interactive input collection.
/// </para>
///
/// <para>
/// Lifecycle (state, locking, drain, dispose) is owned by
/// <see cref="DiscordProviderLifecycle"/> — see
/// <c>specs/100-product-vision/discord-lifecycle-refactor-design.md</c> for the
/// full state machine and the design decisions behind it.
/// </para>
/// </summary>
public sealed class DiscordNotificationProvider : INotificationProvider, IAsyncDisposable
{
    private readonly ILogger<DiscordNotificationProvider> _logger;
    private readonly DiscordChannelManager _channelManager;
    private readonly DiscordInputHandler _inputHandler;
    private readonly DiscordMessageSender _sender;
    private readonly DiscordMessageRouter _router;
    private readonly IDiscordConnectionManager _connection;
    private readonly DiscordProviderLifecycle _lifecycle = new();

    // Hooked-state for the inbound MessageReceived subscription. Mutated only
    // under the connect lock (held by Connect/Disconnect/Dispose leases).
    private bool _messageReceivedHooked;

    // Bounded wait so a hung send (e.g. Discord API stall) can't block teardown
    // forever. After the timeout we proceed with teardown anyway; the in-flight
    // send will get an ObjectDisposedException caught by ExecuteSafe...'s catch.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

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
    public bool IsConfigured => _lifecycle.IsConfigured;

    /// <inheritdoc />
    /// <remarks>
    /// Combines the FSM's Connected snapshot with the live socket state from
    /// <see cref="IDiscordConnectionManager.IsConnected"/>. The FSM can know
    /// the provider <em>intends</em> to be connected; only the connection
    /// manager knows whether the underlying Discord socket actually is.
    /// </remarks>
    public bool IsConnected => _lifecycle.IsConnectedSnapshot && _connection.IsConnected;

    /// <inheritdoc />
    public string? LastError => _connection.LastError;

    /// <inheritdoc />
    public async Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default)
    {
        // Parse/validate outside the lock — FromDictionary may throw on bad input,
        // and we don't want to serialize other callers on argument validation.
        var parsed = DiscordProviderConfig.FromDictionary(configuration);

        var effective = await _lifecycle.ConfigureAsync(parsed, cancellationToken);

        _logger.LogInformation(
            "Discord provider configured for guild {GuildId}, channel {ChannelId}, owner {OwnerId}",
            effective.GuildId, effective.ChannelId, effective.OwnerId?.ToString() ?? "(any user)");
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Stale-socket recovery (matches pre-FSM behaviour): if the FSM thinks
        // we're Connected but the underlying socket has dropped (e.g. Discord
        // gateway disconnect, network blip), the user's manual Connect call
        // would otherwise short-circuit as "AlreadyConnected" and silently leave
        // them stuck. Per Decision D we don't auto-reconnect on socket drops,
        // but a user-initiated ConnectAsync MUST be able to recover. Disconnect
        // first to clear FSM + any lingering hooks, then proceed normally.
        if (_lifecycle.IsConnectedSnapshot && !_connection.IsConnected)
        {
            _logger.LogInformation("Discord socket has dropped while FSM reports Connected; disconnecting before reconnect");
            await DisconnectAsync(cancellationToken);
        }

        await using var lease = await _lifecycle.BeginConnectAsync(cancellationToken);

        if (lease.AlreadyConnectedFlag)
        {
            _logger.LogDebug("Discord provider is already connected");
            return;
        }

        // Ensure any external subscription is cleared before the connection
        // manager tears down a stale client. (Defensive: should already be
        // false in Configured state.)
        UnhookRouter();

        await _connection.ConnectAsync(lease.Config.BotToken, cancellationToken);

        try
        {
            // Rebuild channel mapping from existing Discord state (survives restarts).
            var guild = _connection.Client!.GetGuild(lease.Config.GuildId);
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
            // Lease falls out of scope without Complete() — FSM rolls back to Configured.
            throw;
        }

        // All post-connect initialization succeeded — mark the connect complete
        // so the FSM transitions Connecting -> Connected on lease disposal.
        lease.Complete();
        _logger.LogInformation("Discord provider connected");
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _lifecycle.BeginDisconnectAsync(cancellationToken);

        if (!lease.NeedsTeardown)
        {
            // Idempotent no-op (state was Created/Configured/Disconnecting). Decision C.
            return;
        }

        // Drain in-flight ops, then tear down the underlying client. Lease
        // dispose returns FSM to Configured + clears the teardown flag.
        //
        // Drain + teardown use CancellationToken.None: once the FSM has
        // transitioned to Disconnecting, cancelling the caller's token
        // mid-teardown would skip UnhookRouter / ResetAsync / DisposeClientAsync
        // while the lease still rolls FSM back to Configured — leaving a live
        // socket orphaned with the provider reporting disconnected. The drain
        // itself is bounded by DrainTimeout so this can't hang indefinitely.
        await WaitForDrainWithLoggingAsync(DrainTimeout, CancellationToken.None);

        UnhookRouter();
        await _channelManager.ResetAsync();
        await _connection.DisposeClientAsync();
        _logger.LogInformation("Discord provider disconnected");
    }

    /// <inheritdoc />
    public async Task<bool> SendNotificationAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return await ExecuteSafeWithConnectedGuildAsync(
            operationName: "send notification",
            kind: OperationKind.Send,
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

        using var lease = _lifecycle.TryEnterOperation(OperationKind.RequestInput);
        if (!lease.Permitted)
        {
            _logger.LogDebug("Cannot request input — Discord provider {Reason}", lease.RejectionReason);
            return null;
        }

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
        await using var lease = await _lifecycle.BeginDisposeAsync();
        if (!lease.ShouldRunTeardown) return;

        // Wait for in-flight sends/inputs that captured a client snapshot to
        // complete before disposing the underlying client. Bounded so a wedged
        // operation can't deadlock disposal.
        await WaitForDrainWithLoggingAsync(DrainTimeout, CancellationToken.None);

        UnhookRouter();
        await _connection.DisposeClientAsync();
    }

    /// <summary>
    /// Sends an agent's question to the human via a dedicated Discord channel and thread.
    /// </summary>
    public async Task<bool> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        return await ExecuteSafeWithConnectedGuildAsync(
            operationName: "send agent question",
            kind: OperationKind.Send,
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
            kind: OperationKind.Send,
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
    /// Resolves the Discord guild and current config when the provider is
    /// connected at the socket level. Logs a warning and returns null on
    /// failure. Caller MUST already hold an operation lease (so the snapshot
    /// is consistent with the gate that admitted them).
    /// </summary>
    private (SocketGuild Guild, DiscordProviderConfig Config)? GetGuildIfConnected(string operationName)
    {
        var client = _connection.Client;
        var config = _lifecycle.ConfigSnapshot;
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
    /// Resolves the Discord guild when the provider is configured (but not
    /// necessarily fully connected). Used by best-effort lifecycle operations.
    /// Returns null silently if unavailable.
    /// </summary>
    private SocketGuild? GetGuildIfConfigured()
    {
        var client = _connection.Client;
        var config = _lifecycle.ConfigSnapshot;
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
        OperationKind kind,
        Func<SocketGuild, DiscordProviderConfig, Task<bool>> operation,
        Action<Exception> onFailure)
    {
        using var lease = _lifecycle.TryEnterOperation(kind);
        if (!lease.Permitted)
        {
            _logger.LogDebug("Cannot {Operation} — {Reason}", operationName, lease.RejectionReason);
            return false;
        }

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
        using var lease = _lifecycle.TryEnterOperation(OperationKind.RoomLifecycle);
        if (!lease.Permitted) return;

        var guild = GetGuildIfConfigured();
        if (guild is null) return;

        await operation(guild);
    }

    /// <summary>
    /// Waits for drain with provider-specific timeout logging.
    /// </summary>
    private async Task WaitForDrainWithLoggingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var drained = await _lifecycle.WaitForDrainAsync(timeout, cancellationToken);
        if (!drained)
        {
            _logger.LogWarning(
                "Discord provider teardown proceeding with {InFlight} operation(s) still in flight after {Timeout}",
                _lifecycle.InFlightCount, timeout);
        }
    }

    #endregion

    /// <summary>
    /// Internal test seam: runs <paramref name="body"/> while the in-flight
    /// operation tracker treats it as a real send. Lets tests drive teardown
    /// races without needing a live Discord client. Production code MUST use
    /// the public surface (SendNotificationAsync etc.) which routes through
    /// the same tracker via <see cref="ExecuteSafeWithConnectedGuildAsync"/>.
    ///
    /// <para>
    /// Bypasses the state-based gate (Connected requirement) — the test only
    /// needs the drain semantics, not a live connection.
    /// </para>
    /// </summary>
    internal async Task<bool> RunUnderInFlightForTestingAsync(Func<Task> body)
    {
        using var lease = _lifecycle.TryEnterDrainOperationForTesting();
        if (!lease.Permitted) return false;
        await body();
        return true;
    }

    /// <summary>
    /// Internal test seam: forces the FSM into the given lifecycle state
    /// without running the underlying connect/disconnect protocol. Used by
    /// concurrency tests that need to assert the drain-on-disconnect contract
    /// without a real <see cref="DiscordSocketClient"/>.
    /// </summary>
    internal void ForceLifecycleStateForTesting(LifecycleState state)
    {
        // Reuse the captured config if any so IsConfigured reflects the forced state.
        _lifecycle.ForceStateForTesting(state, _lifecycle.ConfigSnapshot);
    }
}
