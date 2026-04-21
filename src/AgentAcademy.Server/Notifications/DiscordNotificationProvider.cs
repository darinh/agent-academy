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
    private readonly DiscordConnectionManager _connection;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private string? _botToken;
    private ulong _channelId;
    private ulong _guildId;
    private ulong? _ownerId;
    private bool _isConfigured;
    private bool _messageReceivedHooked;

    public DiscordNotificationProvider(
        ILogger<DiscordNotificationProvider> logger,
        DiscordChannelManager channelManager,
        DiscordInputHandler inputHandler,
        DiscordMessageSender sender,
        DiscordMessageRouter router,
        DiscordConnectionManager connection)
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
    public bool IsConfigured => _isConfigured;

    /// <inheritdoc />
    public bool IsConnected => _connection.IsConnected;

    /// <inheritdoc />
    public string? LastError => _connection.LastError;

    /// <inheritdoc />
    public Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _botToken = GetRequiredString(configuration, "BotToken");
        _channelId = GetRequiredUlong(configuration, "ChannelId");
        _guildId = GetRequiredUlong(configuration, "GuildId");

        if (configuration.TryGetValue("OwnerId", out var ownerIdStr) && ulong.TryParse(ownerIdStr, out var ownerId))
            _ownerId = ownerId;

        _isConfigured = true;

        _logger.LogInformation("Discord provider configured for guild {GuildId}, channel {ChannelId}, owner {OwnerId}",
            _guildId, _channelId, _ownerId?.ToString() ?? "(any user)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConfigured)
            throw new InvalidOperationException("Discord provider must be configured before connecting. Call ConfigureAsync first.");

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

            await _connection.ConnectAsync(_botToken!, cancellationToken);

            _logger.LogInformation("Discord provider connected");

            // Rebuild channel mapping from existing Discord state (survives restarts).
            var guild = _connection.Client!.GetGuild(_guildId);
            if (guild is not null)
                await _channelManager.RebuildAsync(guild);

            // Attach inbound message router AFTER rebuild so channel mappings are ready.
            _connection.Client.MessageReceived += _router.HandleMessageReceivedAsync;
            _messageReceivedHooked = true;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
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
            operation: guild =>
                !string.IsNullOrEmpty(message.RoomId)
                    ? _sender.SendToRoomChannelAsync(guild, _channelId, message, cancellationToken)
                    : _sender.SendToDefaultChannelAsync(guild, _channelId, message, cancellationToken),
            onFailure: ex => _logger.LogError(ex, "Failed to send Discord notification: {Title}", message.Title));
    }

    /// <inheritdoc />
    public async Task<UserResponse?> RequestInputAsync(InputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var guild = GetGuildIfConnected("request input");
        if (guild is null) return null;

        try
        {
            var channel = DiscordMessageSender.ResolveDefaultChannel(guild, _channelId);
            if (channel is null)
            {
                _logger.LogError("Discord channel {ChannelId} not found in guild {GuildId}", _channelId, _guildId);
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
                    _connection.Client!, channel, embed, _channelId, _ownerId, ProviderId, cancellationToken);
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
        UnhookRouter();
        await _connection.DisposeClientAsync();
        _connectLock.Dispose();
    }

    /// <summary>
    /// Sends an agent's question to the human via a dedicated Discord channel and thread.
    /// </summary>
    public async Task<bool> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        return await ExecuteSafeWithConnectedGuildAsync(
            operationName: "send agent question",
            operation: guild => _sender.SendAgentQuestionAsync(guild, question, cancellationToken),
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
            operation: guild => _sender.SendDirectMessageAsync(guild, dm, cancellationToken),
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
    /// Resolves the Discord guild when the provider is connected.
    /// Logs a warning if disconnected or guild unavailable. Returns null on failure.
    /// </summary>
    private SocketGuild? GetGuildIfConnected(string operationName)
    {
        var client = _connection.Client;
        if (!_connection.IsConnected || client is null)
        {
            _logger.LogWarning("Cannot {Operation} — Discord provider is not connected", operationName);
            return null;
        }

        var guild = client.GetGuild(_guildId);
        if (guild is null)
            _logger.LogError("Discord guild {GuildId} not found", _guildId);

        return guild;
    }

    /// <summary>
    /// Resolves the Discord guild when the provider is configured (but not necessarily fully connected).
    /// Used by best-effort lifecycle operations. Returns null silently if unavailable.
    /// </summary>
    private SocketGuild? GetGuildIfConfigured()
    {
        var client = _connection.Client;
        if (client is null || !_isConfigured) return null;
        return client.GetGuild(_guildId);
    }

    private static string GetRequiredString(Dictionary<string, string> configuration, string key)
    {
        if (!configuration.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{key} is required.", nameof(configuration));

        return value;
    }

    private static ulong GetRequiredUlong(Dictionary<string, string> configuration, string key)
    {
        if (!configuration.TryGetValue(key, out var value) || !ulong.TryParse(value, out var parsed))
            throw new ArgumentException($"{key} is required and must be a valid numeric ID.", nameof(configuration));

        return parsed;
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
        Func<SocketGuild, Task<bool>> operation,
        Action<Exception> onFailure)
    {
        var guild = GetGuildIfConnected(operationName);
        if (guild is null) return false;

        try
        {
            return await operation(guild);
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
