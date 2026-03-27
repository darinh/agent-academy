using AgentAcademy.Shared.Models;
using Discord;
using Discord.WebSocket;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Notification provider that delivers notifications and collects user input via a Discord bot.
/// Requires a bot token, guild (server) ID, and channel ID.
/// </summary>
public sealed class DiscordNotificationProvider : INotificationProvider, IAsyncDisposable
{
    private readonly ILogger<DiscordNotificationProvider> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _inputLock = new(1, 1);

    private DiscordSocketClient? _client;
    private string? _botToken;
    private ulong _channelId;
    private ulong _guildId;
    private bool _isConfigured;
    private TaskCompletionSource<bool>? _readyTcs;

    public DiscordNotificationProvider(ILogger<DiscordNotificationProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderId => "discord";

    /// <inheritdoc />
    public string DisplayName => "Discord";

    /// <inheritdoc />
    public bool IsConfigured => _isConfigured;

    /// <inheritdoc />
    public bool IsConnected => _client?.ConnectionState == ConnectionState.Connected;

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
        _isConfigured = true;

        _logger.LogInformation("Discord provider configured for guild {GuildId}, channel {ChannelId}", _guildId, _channelId);
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

            _client.Ready += () =>
            {
                _readyTcs.TrySetResult(true);
                return Task.CompletedTask;
            };

            _client.Disconnected += ex =>
            {
                _logger.LogWarning(ex, "Discord client disconnected");
                _readyTcs?.TrySetResult(false);
                return Task.CompletedTask;
            };

            await _client.LoginAsync(TokenType.Bot, _botToken);
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
                    throw new InvalidOperationException("Discord client failed to reach Ready state.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await DisposeClientAsync();
                throw new TimeoutException("Discord client did not become ready within 30 seconds.");
            }
            catch (Exception) when (_client is not null)
            {
                await DisposeClientAsync();
                throw;
            }

            _logger.LogInformation("Discord provider connected as {BotUser}", _client.CurrentUser?.Username ?? "unknown");
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

        if (!IsConnected || _client is null)
        {
            _logger.LogWarning("Cannot send notification — Discord provider is not connected");
            return false;
        }

        try
        {
            var channel = ResolveChannel();
            if (channel is null)
            {
                return false;
            }

            var embed = BuildEmbed(message);
            var components = BuildActionComponents(message.Actions);

            await channel.SendMessageAsync(embed: embed, components: components);

            _logger.LogDebug("Sent Discord notification: {Title}", message.Title);
            return true;
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

        if (!IsConnected || _client is null)
        {
            _logger.LogWarning("Cannot request input — Discord provider is not connected");
            return null;
        }

        try
        {
            var channel = ResolveChannel();
            if (channel is null)
            {
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
                return await RequestChoiceInputAsync(channel, embed, request.Choices, cancellationToken);
            }

            if (request.AllowFreeform)
            {
                return await RequestFreeformInputAsync(channel, embed, cancellationToken);
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
                "Right-click the notification channel → Copy Channel ID")
        }
    );

    /// <inheritdoc cref="IAsyncDisposable.DisposeAsync" />
    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
        _connectLock.Dispose();
        _inputLock.Dispose();
    }

    #region Private helpers

    private Embed BuildEmbed(NotificationMessage message)
    {
        var embed = new EmbedBuilder()
            .WithTitle(message.Title)
            .WithDescription(message.Body)
            .WithColor(GetColorForType(message.Type))
            .WithCurrentTimestamp();

        if (message.RoomId is not null)
            embed.AddField("Room", message.RoomId, inline: true);
        if (message.AgentName is not null)
            embed.AddField("Agent", message.AgentName, inline: true);

        return embed.Build();
    }

    public static Color GetColorForType(NotificationType type) => type switch
    {
        NotificationType.AgentThinking => Color.Blue,
        NotificationType.NeedsInput => Color.Gold,
        NotificationType.TaskComplete => Color.Green,
        NotificationType.TaskFailed => Color.Red,
        NotificationType.SpecReview => new Color(138, 43, 226), // purple
        NotificationType.Error => Color.Red,
        _ => Color.LightGrey
    };

    /// <summary>
    /// Resolves the notification channel, validating it belongs to the configured guild.
    /// </summary>
    private IMessageChannel? ResolveChannel()
    {
        var guild = _client!.GetGuild(_guildId);
        if (guild is null)
        {
            _logger.LogError("Discord guild {GuildId} not found — bot may not be a member", _guildId);
            return null;
        }

        var channel = guild.GetTextChannel(_channelId);
        if (channel is null)
        {
            _logger.LogError("Discord channel {ChannelId} not found in guild {GuildId}", _channelId, _guildId);
            return null;
        }

        return channel;
    }

    private static MessageComponent? BuildActionComponents(Dictionary<string, string>? actions)
    {
        if (actions is not { Count: > 0 })
            return null;

        var builder = new ComponentBuilder();
        foreach (var (key, label) in actions)
        {
            builder.WithButton(label, $"action:{key}", ButtonStyle.Primary);
        }

        return builder.Build();
    }

    private async Task<UserResponse?> RequestChoiceInputAsync(
        IMessageChannel channel,
        EmbedBuilder embed,
        List<string> choices,
        CancellationToken cancellationToken)
    {
        embed.AddField("Choices", string.Join(" | ", choices.Select((c, i) => $"`{i + 1}` {c}")));

        var components = new ComponentBuilder();
        for (var i = 0; i < choices.Count; i++)
        {
            components.WithButton(choices[i], $"input-choice:{i}", ButtonStyle.Primary);
        }

        var sentMessage = await channel.SendMessageAsync(embed: embed.Build(), components: components.Build());

        // Wait for button interaction
        var tcs = new TaskCompletionSource<UserResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task OnInteractionCreated(SocketInteraction interaction)
        {
            if (interaction is not SocketMessageComponent component)
                return Task.CompletedTask;

            if (component.Message.Id != sentMessage.Id)
                return Task.CompletedTask;

            if (!component.Data.CustomId.StartsWith("input-choice:"))
                return Task.CompletedTask;

            var indexStr = component.Data.CustomId["input-choice:".Length..];
            if (!int.TryParse(indexStr, out var choiceIndex) || choiceIndex < 0 || choiceIndex >= choices.Count)
                return Task.CompletedTask;

            var selected = choices[choiceIndex];
            tcs.TrySetResult(new UserResponse(selected, selected, ProviderId));

            // Acknowledge the interaction
            _ = component.DeferAsync();
            return Task.CompletedTask;
        }

        _client!.InteractionCreated += OnInteractionCreated;
        try
        {
            var result = await tcs.Task.WaitAsync(cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Input request timed out waiting for Discord choice selection");
            return null;
        }
        finally
        {
            _client!.InteractionCreated -= OnInteractionCreated;
        }
    }

    private async Task<UserResponse?> RequestFreeformInputAsync(
        IMessageChannel channel,
        EmbedBuilder embed,
        CancellationToken cancellationToken)
    {
        // Serialize freeform input requests to prevent concurrent listeners from
        // consuming the same user message.
        await _inputLock.WaitAsync(cancellationToken);
        try
        {
            embed.WithFooter("Reply in this channel to respond");
            await channel.SendMessageAsync(embed: embed.Build());

            // Wait for next user (non-bot) message in the channel
            var tcs = new TaskCompletionSource<UserResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task OnMessageReceived(SocketMessage msg)
            {
                if (msg.Channel.Id != _channelId)
                    return Task.CompletedTask;

                if (msg.Author.IsBot)
                    return Task.CompletedTask;

                tcs.TrySetResult(new UserResponse(msg.Content, ProviderId: ProviderId));
                return Task.CompletedTask;
            }

            _client!.MessageReceived += OnMessageReceived;
            try
            {
                var result = await tcs.Task.WaitAsync(cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Input request timed out waiting for Discord freeform reply");
                return null;
            }
            finally
            {
                _client!.MessageReceived -= OnMessageReceived;
            }
        }
        finally
        {
            _inputLock.Release();
        }
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

    private async Task DisposeClientAsync()
    {
        if (_client is null) return;

        try
        {
            _client.Log -= OnDiscordLog;
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
