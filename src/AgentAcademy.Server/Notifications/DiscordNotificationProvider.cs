using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Discord;
using Discord.Webhook;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Notification provider that delivers notifications and collects user input via a Discord bot.
/// Delegates channel/category lifecycle to <see cref="DiscordChannelManager"/>.
/// Owns the Discord client connection and message sending/routing logic.
/// </summary>
public sealed class DiscordNotificationProvider : INotificationProvider, IAsyncDisposable
{
    private readonly ILogger<DiscordNotificationProvider> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentOrchestrator _orchestrator;
    private readonly DiscordChannelManager _channelManager;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _inputLock = new(1, 1);

    private DiscordSocketClient? _client;
    private string? _botToken;
    private ulong _channelId;
    private ulong _guildId;
    private ulong? _ownerId;
    private bool _isConfigured;
    private TaskCompletionSource<bool>? _readyTcs;

    public DiscordNotificationProvider(
        ILogger<DiscordNotificationProvider> logger,
        IServiceScopeFactory scopeFactory,
        AgentOrchestrator orchestrator,
        DiscordChannelManager channelManager)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
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

            _client.Ready += () =>
            {
                _readyTcs.TrySetResult(true);
                return Task.CompletedTask;
            };

            _client.Disconnected += ex =>
            {
                _logger.LogWarning(ex, "Discord client disconnected");
                disconnectReason = ExtractDisconnectReason(ex);
                _readyTcs?.TrySetResult(false);
                return Task.CompletedTask;
            };

            // Persistent handler for routing replies from agent channels back to rooms
            _client.MessageReceived += OnAgentChannelMessageReceived;

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

        if (!IsConnected || _client is null)
        {
            _logger.LogWarning("Cannot send notification — Discord provider is not connected");
            return false;
        }

        try
        {
            // Route to room-specific channel if RoomId is available
            if (!string.IsNullOrEmpty(message.RoomId))
            {
                return await SendToRoomChannelAsync(message);
            }

            // Fallback: send to configured default channel
            var channel = ResolveChannel();
            if (channel is null)
                return false;

            var embed = BuildEmbed(message);
            var components = BuildActionComponents(message.Actions);
            await channel.SendMessageAsync(embed: embed, components: components);

            _logger.LogDebug("Sent Discord notification to default channel: {Title}", message.Title);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send Discord notification: {Title}", message.Title);
            return false;
        }
    }

    /// <summary>
    /// Sends a notification to a room-specific Discord channel using a webhook
    /// so the agent appears as the sender with a custom name and avatar.
    /// Creates the room channel and webhook on demand.
    /// Falls back to the configured default channel if creation fails (e.g., missing permissions).
    /// </summary>
    private async Task<bool> SendToRoomChannelAsync(NotificationMessage message)
    {
        var guild = _client!.GetGuild(_guildId);
        if (guild is null)
        {
            _logger.LogError("Discord guild {GuildId} not found", _guildId);
            return false;
        }

        ITextChannel? roomChannel = null;
        DiscordWebhookClient? webhook = null;

        try
        {
            (roomChannel, webhook) = await _channelManager.EnsureRoomChannelAsync(
                guild, message.RoomId!, message.AgentName);
        }
        catch (Discord.Net.HttpException httpEx) when (httpEx.DiscordCode == DiscordErrorCode.MissingPermissions)
        {
            _logger.LogWarning(
                "Cannot create room channel/webhook (Missing Permissions) — falling back to default channel. " +
                "Grant the bot 'Manage Channels' and 'Manage Webhooks' at the server level.");
            roomChannel = null;
        }

        // Fallback to configured default channel if room channel creation failed
        if (roomChannel is null)
        {
            var fallbackChannel = ResolveChannel();
            if (fallbackChannel is null)
                return false;

            var embed = BuildEmbed(message);
            await (fallbackChannel as ITextChannel)!.SendMessageAsync(embed: embed);
            return true;
        }

        // Use webhook for agent messages (custom sender name/avatar)
        if (webhook is not null && !string.IsNullOrEmpty(message.AgentName))
        {
            var agentDisplayName = DiscordChannelManager.FormatAgentDisplayName(message.AgentName);
            var avatarUrl = DiscordChannelManager.GetAgentAvatarUrl(message.AgentName);

            if (message.Type is NotificationType.Error or NotificationType.TaskFailed)
            {
                var embed = new EmbedBuilder()
                    .WithDescription(message.Body)
                    .WithColor(GetColorForType(message.Type))
                    .WithCurrentTimestamp()
                    .Build();

                await webhook.SendMessageAsync(
                    embeds: new[] { embed },
                    username: agentDisplayName,
                    avatarUrl: avatarUrl);
            }
            else
            {
                await SendChunkedWebhookAsync(webhook, message.Body, agentDisplayName, avatarUrl);
            }

            return true;
        }

        // Fallback: regular bot message with embed (no webhook available)
        var fallbackEmbed = BuildEmbed(message);
        await roomChannel.SendMessageAsync(embed: fallbackEmbed);
        return true;
    }

    /// <summary>
    /// Sends a long message via webhook, splitting into multiple parts if it exceeds
    /// Discord's 2000-character limit. Parts are labeled (1/N), (2/N), etc.
    /// </summary>
    private static async Task SendChunkedWebhookAsync(
        DiscordWebhookClient webhook, string text, string username, string? avatarUrl)
    {
        const int maxLen = 2000;
        const int suffixReserve = 10; // room for " (XX/XX)"
        var chunkSize = maxLen - suffixReserve;

        if (text.Length <= maxLen)
        {
            await webhook.SendMessageAsync(text: text, username: username, avatarUrl: avatarUrl);
            return;
        }

        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, text.Length - i);
            chunks.Add(text.Substring(i, len));
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var part = $"{chunks[i]} ({i + 1}/{chunks.Count})";
            await webhook.SendMessageAsync(text: part, username: username, avatarUrl: avatarUrl);
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
        _inputLock.Dispose();
    }

    /// <summary>
    /// Sends an agent's question to the human via a dedicated Discord channel and thread.
    /// Creates a category for the workspace, a channel for the agent, and a thread for the question.
    /// Human replies in the channel/thread are automatically routed back to the agent's room.
    /// </summary>
    public async Task<bool> SendAgentQuestionAsync(AgentQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!IsConnected || _client is null)
        {
            _logger.LogWarning("Cannot send agent question — Discord provider is not connected");
            return false;
        }

        try
        {
            var guild = _client.GetGuild(_guildId);
            if (guild is null)
            {
                _logger.LogError("Discord guild {GuildId} not found", _guildId);
                return false;
            }

            var (channel, thread) = await _channelManager.EnsureQuestionThreadAsync(
                guild, question.RoomId, question.RoomName, question.AgentId, question.AgentName,
                question.Question, cancellationToken);

            var embed = new EmbedBuilder()
                .WithTitle($"❓ {question.AgentName} asks:")
                .WithDescription(question.Question)
                .WithColor(Color.Gold)
                .AddField("Workspace", question.RoomName, inline: true)
                .AddField("Agent", question.AgentName, inline: true)
                .WithFooter("Reply in this thread — your response will be sent to the agent")
                .WithCurrentTimestamp()
                .Build();

            await thread.SendMessageAsync(embed: embed);

            _logger.LogInformation(
                "Agent question sent: {AgentName} → #{ChannelName}/{ThreadName}",
                question.AgentName, channel.Name, thread.Name);

            return true;
        }
        catch (Discord.Net.HttpException httpEx) when (httpEx.DiscordCode == DiscordErrorCode.MissingPermissions)
        {
            _logger.LogError(httpEx,
                "Discord bot lacks permissions to create channels/threads for agent question from '{AgentName}'. " +
                "Grant the bot 'Manage Channels', 'Send Messages', and 'Create Public Threads' permissions on the server.",
                question.AgentName);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send agent question from '{AgentName}'", question.AgentName);
            return false;
        }
    }

    /// <summary>
    /// Posts a DM as a simple embed in the agent's channel (no thread).
    /// Used for agent-to-agent DMs where no reply routing is needed.
    /// </summary>
    public async Task<bool> SendDirectMessageAsync(AgentQuestion dm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dm);

        if (!IsConnected || _client is null) return false;

        try
        {
            var guild = _client.GetGuild(_guildId);
            if (guild is null) return false;

            var channel = await _channelManager.EnsureAgentChannelAsync(
                guild, dm.RoomId, dm.RoomName, dm.AgentId, dm.AgentName, cancellationToken);

            var embed = new EmbedBuilder()
                .WithDescription(dm.Question)
                .WithColor(Color.Blue)
                .WithFooter(dm.AgentName)
                .WithCurrentTimestamp()
                .Build();

            await channel.SendMessageAsync(embed: embed);

            _logger.LogInformation("DM posted to #{ChannelName}: {AgentName}",
                channel.Name, dm.AgentName);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send DM for '{AgentName}'", dm.AgentName);
            return false;
        }
    }


    #region Private helpers

    /// <summary>
    /// Persistent handler for messages in agent channels and room channels.
    /// Routes human replies back to the correct Agent Academy room via MessageService.
    /// </summary>
    private async Task OnAgentChannelMessageReceived(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        // Ignore webhook messages (our own webhook-sent messages)
        if (message is SocketUserMessage { Source: MessageSource.Webhook })
            return;

        // Skip non-text messages
        if (string.IsNullOrWhiteSpace(message.Content))
        {
            try { await message.Channel.SendMessageAsync("ℹ️ Please reply with text — attachments can’t be forwarded to the agents."); }
            catch { /* best-effort hint */ }
            return;
        }

        // Determine the parent channel ID for routing
        ulong channelId;
        if (message.Channel is SocketThreadChannel thread)
        {
            if (thread.ParentChannel is null) return;
            channelId = thread.ParentChannel.Id;
        }
        else
        {
            channelId = message.Channel.Id;
        }

        // Check if this is a room channel message (bidirectional bridge)
        if (_channelManager.TryGetRoomForChannel(channelId, out var roomId) && roomId is not null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
                await messageService.PostHumanMessageAsync(roomId, message.Content);
                _orchestrator.HandleHumanMessage(roomId);

                await message.AddReactionAsync(new Emoji("✅"));

                _logger.LogInformation(
                    "Routed Discord message to room '{RoomId}' from user '{User}'",
                    roomId, message.Author.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to route Discord message to room '{RoomId}'", roomId);
                try { await message.Channel.SendMessageAsync("⚠️ Failed to deliver message to room. Please try again."); }
                catch { /* best-effort */ }
            }
            return;
        }

        // Check if this is an ASK_HUMAN agent channel message
        if (!_channelManager.TryGetAgentInfoForChannel(channelId, out var agentInfo) || agentInfo is null)
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
            await messageService.PostHumanMessageAsync(agentInfo.RoomId, message.Content);
            _orchestrator.HandleHumanMessage(agentInfo.RoomId);

            await message.Channel.SendMessageAsync("✅ Reply received — sent to **" + agentInfo.AgentName + "**");

            _logger.LogInformation(
                "Routed Discord reply to agent '{AgentName}' in room '{RoomId}'",
                agentInfo.AgentName, agentInfo.RoomId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to route Discord reply to agent '{AgentName}'", agentInfo.AgentName);
            try
            {
                await message.Channel.SendMessageAsync("⚠️ Failed to deliver reply to " + agentInfo.AgentName + ". Please try again.");
            }
            catch (Exception ackEx)
            {
                _logger.LogWarning(ackEx, "Failed to send error acknowledgment to Discord channel");
            }
        }
    }

    /// <summary>
    /// Renames the Discord channel associated with a room (delegates to channel manager).
    /// </summary>
    public async Task OnRoomRenamedAsync(string roomId, string newName, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_isConfigured) return;

        var guild = _client.GetGuild(_guildId);
        if (guild is null) return;

        await _channelManager.RenameRoomChannelAsync(guild, roomId, newName);
    }

    /// <inheritdoc />
    public async Task OnRoomClosedAsync(string roomId, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_isConfigured) return;

        var guild = _client.GetGuild(_guildId);
        if (guild is null) return;

        await _channelManager.DeleteRoomChannelAsync(guild, roomId, cancellationToken);
    }

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
        await _inputLock.WaitAsync(cancellationToken);
        try
        {
            embed.WithFooter("Reply in this channel to respond");
            await channel.SendMessageAsync(embed: embed.Build());

            var tcs = new TaskCompletionSource<UserResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task OnMessageReceived(SocketMessage msg)
            {
                if (msg.Channel.Id != _channelId)
                    return Task.CompletedTask;

                if (msg.Author.IsBot)
                    return Task.CompletedTask;

                if (_ownerId.HasValue && msg.Author.Id != _ownerId.Value)
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
            _client.MessageReceived -= OnAgentChannelMessageReceived;
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
