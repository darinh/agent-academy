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
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private DiscordSocketClient? _client;
    private string? _botToken;
    private ulong _channelId;
    private ulong _guildId;
    private ulong? _ownerId;
    private bool _isConfigured;
    private TaskCompletionSource<bool>? _readyTcs;
    private Func<Task>? _readyHandler;
    private Func<Exception, Task>? _disconnectedHandler;

    public DiscordNotificationProvider(
        ILogger<DiscordNotificationProvider> logger,
        DiscordChannelManager channelManager,
        DiscordInputHandler inputHandler,
        DiscordMessageSender sender,
        DiscordMessageRouter router)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
        _inputHandler = inputHandler ?? throw new ArgumentNullException(nameof(inputHandler));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    private string? _lastError;

    /// <inheritdoc />
    public string ProviderId => "discord";

    /// <inheritdoc />
    public string DisplayName => "Discord";

    /// <inheritdoc />
    public bool IsConfigured => _isConfigured;

    /// <inheritdoc />
    public bool IsConnected => _client?.ConnectionState == ConnectionState.Connected;

    /// <inheritdoc />
    public string? LastError => _lastError;

    /// <inheritdoc />
    public Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.TryGetValue("BotToken", out var token) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("BotToken is required.", nameof(configuration));

        if (!configuration.TryGetValue("ChannelId", out var channelIdStr) || !ulong.TryParse(channelIdStr, out var channelId))
            throw new ArgumentException("ChannelId is required and must be a valid numeric ID.", nameof(configuration));

        if (!configuration.TryGetValue("GuildId", out var guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId))
            throw new ArgumentException("GuildId is required and must be a valid numeric ID.", nameof(configuration));

        _botToken = token;
        _channelId = channelId;
        _guildId = guildId;

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
            _lastError = null; // Clear previous error on each connect attempt

            if (IsConnected)
            {
                _logger.LogDebug("Discord provider is already connected");
                return;
            }

            // Dispose any stale client
            if (_client is not null)
            {
                await DisposeClientAsync();
            }

            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds
                    | GatewayIntents.GuildMessages
                    | GatewayIntents.MessageContent,
                LogLevel = LogSeverity.Info
            };

            _client = new DiscordSocketClient(config);
            _client.Log += OnDiscordLog;

            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? disconnectReason = null;

            _readyHandler = () =>
            {
                _readyTcs.TrySetResult(true);
                return Task.CompletedTask;
            };

            _disconnectedHandler = ex =>
            {
                _logger.LogWarning(ex, "Discord client disconnected");
                disconnectReason = ExtractDisconnectReason(ex);
                _readyTcs?.TrySetResult(false);
                return Task.CompletedTask;
            };

            _client.Ready += _readyHandler;
            _client.Disconnected += _disconnectedHandler;

            try
            {
                await _client.LoginAsync(TokenType.Bot, _botToken);
            }
            catch (ArgumentException ex)
            {
                _lastError = $"Invalid bot token: {ex.Message}";
                await DisposeClientAsync();
                throw;
            }
            await _client.StartAsync();

            // Wait for Ready event with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                var ready = await _readyTcs.Task.WaitAsync(cts.Token);
                if (!ready)
                {
                    await DisposeClientAsync();
                    _lastError = disconnectReason
                        ?? "Discord client failed to reach Ready state. Check bot token and Message Content Intent in Discord Developer Portal.";
                    throw new InvalidOperationException(_lastError);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await DisposeClientAsync();
                _lastError = "Discord client did not become ready within 30 seconds. Check bot token and network connectivity.";
                throw new TimeoutException("Discord client did not become ready within 30 seconds.");
            }
            catch (Exception ex) when (_client is not null)
            {
                await DisposeClientAsync();
                _lastError ??= $"Discord connection failed: {ex.Message}";
                throw;
            }

            _lastError = null; // Connected successfully — clear any previous error
            _logger.LogInformation("Discord provider connected as {BotUser}", _client.CurrentUser?.Username ?? "unknown");

            // Rebuild channel mapping from existing Discord state (survives restarts)
            var guild = _client.GetGuild(_guildId);
            if (guild is not null)
                await _channelManager.RebuildAsync(guild);

            // Attach inbound message router AFTER rebuild so channel mappings are ready
            _client.MessageReceived += _router.HandleMessageReceivedAsync;
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
            await _channelManager.ResetAsync();
            await DisposeClientAsync();
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

        var guild = GetGuildIfConnected("send notification");
        if (guild is null) return false;

        try
        {
            if (!string.IsNullOrEmpty(message.RoomId))
                return await _sender.SendToRoomChannelAsync(guild, _channelId, message, cancellationToken);

            return await _sender.SendToDefaultChannelAsync(guild, _channelId, message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send Discord notification: {Title}", message.Title);
            return false;
        }
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
                    _client!, channel, embed, request.Choices, ProviderId, cancellationToken);
            }

            if (request.AllowFreeform)
            {
                return await _inputHandler.RequestFreeformInputAsync(
                    _client!, channel, embed, _channelId, _ownerId, ProviderId, cancellationToken);
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
        await DisposeClientAsync();
        _connectLock.Dispose();
    }

    /// <summary>
    /// Sends an agent's question to the human via a dedicated Discord channel and thread.
    /// </summary>
    public async Task<bool> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        var guild = GetGuildIfConnected("send agent question");
        if (guild is null) return false;

        try
        {
            return await _sender.SendAgentQuestionAsync(guild, question, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send agent question from '{AgentName}'", question.AgentName);
            return false;
        }
    }

    /// <summary>
    /// Posts a DM as a simple embed in the agent's channel (no thread).
    /// </summary>
    public async Task<bool> SendDirectMessageAsync(AgentQuestion dm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dm);

        var guild = GetGuildIfConnected("send direct message");
        if (guild is null) return false;

        try
        {
            return await _sender.SendDirectMessageAsync(guild, dm, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send DM for '{AgentName}'", dm.AgentName);
            return false;
        }
    }


    /// <summary>
    /// Renames the Discord channel associated with a room (delegates to channel manager).
    /// </summary>
    public async Task OnRoomRenamedAsync(string roomId, string newName, CancellationToken cancellationToken = default)
    {
        var guild = GetGuildIfConfigured();
        if (guild is not null)
            await _channelManager.RenameRoomChannelAsync(guild, roomId, newName);
    }

    /// <inheritdoc />
    public async Task OnRoomClosedAsync(string roomId, CancellationToken cancellationToken = default)
    {
        var guild = GetGuildIfConfigured();
        if (guild is not null)
            await _channelManager.DeleteRoomChannelAsync(guild, roomId, cancellationToken);
    }


    #region Private helpers

    /// <summary>
    /// Resolves the Discord guild when the provider is connected.
    /// Logs a warning if disconnected or guild unavailable. Returns null on failure.
    /// </summary>
    private SocketGuild? GetGuildIfConnected(string operationName)
    {
        if (!IsConnected || _client is null)
        {
            _logger.LogWarning("Cannot {Operation} — Discord provider is not connected", operationName);
            return null;
        }

        var guild = _client.GetGuild(_guildId);
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
        if (_client is null || !_isConfigured) return null;
        return _client.GetGuild(_guildId);
    }

    private Task OnDiscordLog(LogMessage logMessage)
    {
        var level = logMessage.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(level, logMessage.Exception, "Discord: {Message}", logMessage.Message);
        return Task.CompletedTask;
    }

    private static string? ExtractDisconnectReason(Exception? ex)
    {
        var current = ex;
        while (current is not null)
        {
            var msg = current.Message;
            if (msg.Contains("4014", StringComparison.Ordinal) || msg.Contains("Disallowed intent", StringComparison.OrdinalIgnoreCase))
            {
                return "Discord rejected the connection: privileged Message Content Intent is not enabled. "
                     + "Go to https://discord.com/developers/applications → your bot → Bot → Privileged Gateway Intents → enable MESSAGE CONTENT INTENT, then reconnect.";
            }
            if (msg.Contains("4004", StringComparison.Ordinal) || msg.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                return "Discord rejected the bot token — it may be invalid or revoked. Regenerate the token in the Discord Developer Portal and reconfigure.";
            }
            if (msg.Contains("401", StringComparison.Ordinal) && msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                return "Discord returned 401 Unauthorized — the bot token is invalid, expired, or was never a bot token. "
                     + "Go to https://discord.com/developers/applications → your bot → Bot → Reset Token, then reconfigure with the new token.";
            }
            current = current.InnerException;
        }
        return null;
    }

    private async Task DisposeClientAsync()
    {
        if (_client is null) return;

        try
        {
            _client.Log -= OnDiscordLog;
            _client.MessageReceived -= _router.HandleMessageReceivedAsync;
            if (_readyHandler is not null)
                _client.Ready -= _readyHandler;
            if (_disconnectedHandler is not null)
                _client.Disconnected -= _disconnectedHandler;
            _readyHandler = null;
            _disconnectedHandler = null;

            await _client.StopAsync();
            await _client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing Discord client");
        }
        finally
        {
            _client = null;
        }
    }

    #endregion
}
