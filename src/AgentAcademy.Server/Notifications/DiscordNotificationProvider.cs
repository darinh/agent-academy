using System.Collections.Concurrent;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Notification provider that delivers notifications and collects user input via a Discord bot.
/// Supports agent-to-human question bridge: creates a category per workspace, a channel per agent,
/// and a thread per question. Human replies are routed back to the asking agent's room.
/// </summary>
public sealed class DiscordNotificationProvider : INotificationProvider, IAsyncDisposable
{
    private readonly ILogger<DiscordNotificationProvider> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _inputLock = new(1, 1);

    // Maps Discord channel ID → agent routing info (for agent question channels)
    private readonly ConcurrentDictionary<ulong, AgentChannelInfo> _agentChannels = new();
    // Maps workspace roomId → Discord category ID
    private readonly ConcurrentDictionary<string, ulong> _workspaceCategories = new();
    // Serializes channel/category creation to prevent duplicates from concurrent ASK_HUMAN calls
    private readonly SemaphoreSlim _channelCreateLock = new(1, 1);

    private DiscordSocketClient? _client;
    private string? _botToken;
    private ulong _channelId;
    private ulong _guildId;
    private bool _isConfigured;
    private TaskCompletionSource<bool>? _readyTcs;

    public DiscordNotificationProvider(ILogger<DiscordNotificationProvider> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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

            // Persistent handler for routing replies from agent channels back to rooms
            _client.MessageReceived += OnAgentChannelMessageReceived;

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

            // Rebuild channel mapping from existing Discord state (survives restarts)
            await RebuildChannelMappingAsync();
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
        _channelCreateLock.Dispose();
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

            ITextChannel channel;
            IThreadChannel thread;

            // Serialize category/channel creation to prevent duplicate Discord resources
            await _channelCreateLock.WaitAsync(cancellationToken);
            try
            {
                var category = await FindOrCreateWorkspaceCategoryAsync(guild, question.RoomId, question.RoomName);
                channel = await FindOrCreateAgentChannelAsync(guild, category, question.AgentId, question.AgentName, question.RoomId);
                thread = await CreateQuestionThreadAsync(channel, question);
            }
            finally
            {
                _channelCreateLock.Release();
            }

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

    #region Private helpers

    /// <summary>
    /// Finds or creates a Discord category for a workspace/room.
    /// Category name includes a roomId suffix to prevent collisions between rooms with similar names.
    /// </summary>
    private async Task<ICategoryChannel> FindOrCreateWorkspaceCategoryAsync(
        SocketGuild guild, string roomId, string roomName)
    {
        if (_workspaceCategories.TryGetValue(roomId, out var existingCategoryId))
        {
            var existing = guild.GetCategoryChannel(existingCategoryId);
            if (existing is not null)
                return existing;
            // Category was deleted externally — recreate
            _workspaceCategories.TryRemove(roomId, out _);
        }

        // Include first 8 chars of roomId to avoid name collisions between rooms
        var roomIdSlug = roomId.Length > 8 ? roomId[..8] : roomId;
        var categoryName = SanitizeChannelName($"aa-{roomName}-{roomIdSlug}");

        // Search for existing category by name
        var found = guild.CategoryChannels.FirstOrDefault(
            c => c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

        if (found is not null)
        {
            _workspaceCategories[roomId] = found.Id;
            return found;
        }

        var created = await guild.CreateCategoryChannelAsync(categoryName);
        _workspaceCategories[roomId] = created.Id;
        _logger.LogInformation("Created Discord category '{CategoryName}' for workspace '{RoomId}'", categoryName, roomId);
        return created;
    }

    /// <summary>
    /// Finds or creates a text channel for an agent within a workspace category.
    /// Also registers the channel in the agent routing map.
    /// </summary>
    private async Task<ITextChannel> FindOrCreateAgentChannelAsync(
        SocketGuild guild, ICategoryChannel category, string agentId, string agentName, string roomId)
    {
        var channelName = SanitizeChannelName(agentName, fallbackId: agentId);

        // Check existing channels in the category
        var existing = guild.TextChannels.FirstOrDefault(
            c => c.CategoryId == category.Id &&
                 c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _agentChannels[existing.Id] = new AgentChannelInfo(agentId, agentName, roomId);
            return existing;
        }

        var created = await guild.CreateTextChannelAsync(channelName, props =>
        {
            props.CategoryId = category.Id;
            props.Topic = $"Agent Academy — {agentName} questions for this workspace";
        });

        _agentChannels[created.Id] = new AgentChannelInfo(agentId, agentName, roomId);
        _logger.LogInformation(
            "Created Discord channel '#{ChannelName}' for agent '{AgentName}' in category '{CategoryName}'",
            channelName, agentName, category.Name);

        return created;
    }

    /// <summary>
    /// Creates a thread for a specific question within an agent's channel.
    /// Thread name is the question text, truncated to Discord's 100-char limit.
    /// </summary>
    private static async Task<IThreadChannel> CreateQuestionThreadAsync(ITextChannel channel, AgentQuestion question)
    {
        var threadName = question.Question.Length > 97
            ? string.Concat(question.Question.AsSpan(0, 97), "...")
            : question.Question;

        return await channel.CreateThreadAsync(
            name: threadName,
            type: ThreadType.PublicThread,
            autoArchiveDuration: ThreadArchiveDuration.OneDay);
    }

    /// <summary>
    /// Persistent handler for messages in agent channels.
    /// Routes human replies back to the agent's room via WorkspaceRuntime.
    /// Note: This handler only fires for tracked agent channels, which are separate
    /// from the main notification channel used by RequestInputAsync — no conflict.
    /// </summary>
    private async Task OnAgentChannelMessageReceived(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        // Skip non-text messages (images, stickers, embeds) — can't forward to agent
        if (string.IsNullOrWhiteSpace(message.Content))
        {
            try { await message.Channel.SendMessageAsync("ℹ️ Please reply with text — attachments can't be forwarded to the agent."); }
            catch { /* best-effort hint */ }
            return;
        }

        // Determine the parent channel ID for routing
        ulong parentChannelId;
        if (message.Channel is SocketThreadChannel thread)
        {
            if (thread.ParentChannel is null) return;
            parentChannelId = thread.ParentChannel.Id;
        }
        else
        {
            parentChannelId = message.Channel.Id;
        }

        if (!_agentChannels.TryGetValue(parentChannelId, out var agentInfo))
            return; // Not one of our tracked agent channels

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            await runtime.PostHumanMessageAsync(agentInfo.RoomId, message.Content);

            await message.Channel.SendMessageAsync($"✅ Reply received — sent to **{agentInfo.AgentName}**");

            _logger.LogInformation(
                "Routed Discord reply to agent '{AgentName}' in room '{RoomId}'",
                agentInfo.AgentName, agentInfo.RoomId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to route Discord reply to agent '{AgentName}'", agentInfo.AgentName);
            try
            {
                await message.Channel.SendMessageAsync($"⚠️ Failed to deliver reply to {agentInfo.AgentName}. Please try again.");
            }
            catch (Exception ackEx)
            {
                _logger.LogWarning(ackEx, "Failed to send error acknowledgment to Discord channel");
            }
        }
    }

    /// <summary>
    /// Sanitizes a name for use as a Discord channel/category name.
    /// Discord channel names: lowercase, hyphens instead of spaces, max 100 chars.
    /// Falls back to a deterministic slug if the name would be empty after sanitization.
    /// </summary>
    private static string SanitizeChannelName(string name, string? fallbackId = null)
    {
        var sanitized = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');

        // Remove chars not allowed in Discord channel names
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"[^a-z0-9\-]", "");

        // Collapse multiple hyphens and trim leading/trailing hyphens
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"-{2,}", "-").Trim('-');

        // Fallback for empty results (e.g., non-ASCII input)
        if (string.IsNullOrEmpty(sanitized))
            sanitized = fallbackId is not null ? $"agent-{fallbackId[..Math.Min(8, fallbackId.Length)]}" : "unknown";

        return sanitized.Length > 100 ? sanitized[..100] : sanitized;
    }

    /// <summary>Routing info for an agent's Discord channel.</summary>
    private sealed record AgentChannelInfo(string AgentId, string AgentName, string RoomId);

    /// <summary>
    /// Rebuilds the in-memory channel mapping by scanning for existing "aa-" categories
    /// in the guild. This allows reply routing to survive server restarts.
    /// Channel topics are used to recover the agent name; the roomId is extracted from the
    /// category name suffix.
    /// </summary>
    private async Task RebuildChannelMappingAsync()
    {
        if (_client is null) return;

        try
        {
            var guild = _client.GetGuild(_guildId);
            if (guild is null) return;

            var restoredChannels = 0;
            var restoredCategories = 0;

            foreach (var category in guild.CategoryChannels
                         .Where(c => c.Name.StartsWith("aa-", StringComparison.OrdinalIgnoreCase)))
            {
                // Extract roomId slug from category name (last segment after final hyphen cluster)
                // Category names: "aa-{room-name}-{roomIdSlug}"
                // We can't perfectly reverse the roomId, but we can map the category for future lookups
                var channels = guild.TextChannels.Where(c => c.CategoryId == category.Id).ToList();

                foreach (var channel in channels)
                {
                    // Parse agent identity from channel topic
                    var topic = channel.Topic ?? "";
                    var agentName = channel.Name; // fallback: channel name IS the agent name

                    // We don't have the exact roomId, so extract what we can from the topic
                    // Topic format: "Agent Academy — {agentName} questions for this workspace"
                    if (topic.Contains("Agent Academy"))
                    {
                        var dashIdx = topic.IndexOf('—');
                        if (dashIdx >= 0)
                        {
                            var afterDash = topic[(dashIdx + 1)..].Trim();
                            var questionsIdx = afterDash.IndexOf(" questions", StringComparison.OrdinalIgnoreCase);
                            if (questionsIdx > 0)
                                agentName = afterDash[..questionsIdx].Trim();
                        }
                    }

                    // Use channel name as agentId approximation (lowercased agent name)
                    _agentChannels[channel.Id] = new AgentChannelInfo(
                        AgentId: channel.Name,
                        AgentName: agentName,
                        RoomId: "unknown" // Will be corrected on next ASK_HUMAN from this agent
                    );
                    restoredChannels++;
                }

                restoredCategories++;
            }

            if (restoredChannels > 0)
            {
                _logger.LogInformation(
                    "Rebuilt Discord channel mapping: {Categories} categories, {Channels} agent channels",
                    restoredCategories, restoredChannels);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rebuild Discord channel mapping — new channels will be created on demand");
        }
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
