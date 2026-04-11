using System.Collections.Concurrent;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Discord;
using Discord.Webhook;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Notification provider that delivers notifications and collects user input via a Discord bot.
/// Supports room-based channel routing (one Discord channel per Agent Academy room) with webhook-based
/// message formatting (each agent appears as a distinct sender with custom name and avatar).
/// Also supports agent-to-human question bridge: creates a category per workspace, a channel per agent,
/// and a thread per question. Human replies are routed back to the asking agent's room.
/// </summary>
public sealed class DiscordNotificationProvider : INotificationProvider, IAsyncDisposable
{
    private readonly ILogger<DiscordNotificationProvider> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentOrchestrator _orchestrator;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _inputLock = new(1, 1);

    // Maps Discord channel ID → agent routing info (for agent question channels)
    private readonly ConcurrentDictionary<ulong, AgentChannelInfo> _agentChannels = new();
    // Maps workspace roomId → Discord category ID (for ASK_HUMAN)
    private readonly ConcurrentDictionary<string, ulong> _workspaceCategories = new();
    // Maps AA roomId → Discord channel ID (for room-based routing)
    private readonly ConcurrentDictionary<string, ulong> _roomChannels = new();
    // Maps Discord channel ID → AA roomId (for reverse routing of human replies)
    private readonly ConcurrentDictionary<ulong, string> _channelToRoom = new();
    // Maps Discord channel ID → webhook client (for agent-identity messages)
    private readonly ConcurrentDictionary<ulong, DiscordWebhookClient> _webhooks = new();
    // Discord category ID for the "Agent Academy" room channels (keyed by project name, "__default__" for legacy)
    private readonly ConcurrentDictionary<string, ulong> _roomCategories = new();
    private const string DefaultCategoryKey = "__default__";
    // Serializes channel/category creation to prevent duplicates from concurrent calls
    private readonly SemaphoreSlim _channelCreateLock = new(1, 1);

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
        AgentOrchestrator orchestrator)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
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

        await _channelCreateLock.WaitAsync();
        try
        {
            roomChannel = await FindOrCreateRoomChannelAsync(guild, message.RoomId!);

            // Create webhook inside the lock to prevent duplicate creation
            if (!string.IsNullOrEmpty(message.AgentName))
                webhook = await GetOrCreateWebhookAsync(roomChannel);
        }
        catch (Discord.Net.HttpException httpEx) when (httpEx.DiscordCode == DiscordErrorCode.MissingPermissions)
        {
            _logger.LogWarning(
                "Cannot create room channel/webhook (Missing Permissions) — falling back to default channel. " +
                "Grant the bot 'Manage Channels' and 'Manage Webhooks' at the server level.");
            roomChannel = null;
        }
        finally
        {
            _channelCreateLock.Release();
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
            var agentDisplayName = FormatAgentDisplayName(message.AgentName);
            var avatarUrl = GetAgentAvatarUrl(message.AgentName);

            // For error/system messages, use a compact embed; for regular messages, plain text
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
        _channelCreateLock.Dispose();

        // Dispose webhook clients
        foreach (var webhook in _webhooks.Values)
        {
            try { webhook.Dispose(); } catch { /* best-effort */ }
        }
        _webhooks.Clear();
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

            ITextChannel channel;

            await _channelCreateLock.WaitAsync(cancellationToken);
            try
            {
                var category = await FindOrCreateWorkspaceCategoryAsync(guild, dm.RoomId, dm.RoomName);
                channel = await FindOrCreateAgentChannelAsync(guild, category, dm.AgentId, dm.AgentName, dm.RoomId);
            }
            finally
            {
                _channelCreateLock.Release();
            }

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
    /// Finds or creates a Discord category for DM/message channels.
    /// Category name uses "{ProjectName} Messages" format.
    /// </summary>
    private async Task<ICategoryChannel> FindOrCreateWorkspaceCategoryAsync(
        SocketGuild guild, string roomId, string roomName)
    {
        // Resolve project name for category naming
        string? projectName = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            projectName = await runtime.GetProjectNameForRoomAsync(roomId);

            // Fall back to active workspace name if room has no workspace link
            if (projectName is null)
                projectName = await runtime.GetActiveProjectNameAsync();
        }
        catch { /* fall back to room name */ }

        var cacheKey = projectName ?? roomId;

        if (_workspaceCategories.TryGetValue(cacheKey, out var existingCategoryId))
        {
            var existing = guild.GetCategoryChannel(existingCategoryId);
            if (existing is not null)
                return existing;
            _workspaceCategories.TryRemove(cacheKey, out _);
        }

        var displayName = projectName is not null
            ? ProjectScanner.HumanizeProjectName(projectName)
            : roomName;
        var categoryName = SanitizeCategoryName($"{displayName} Messages");

        var found = guild.CategoryChannels.FirstOrDefault(
            c => c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

        if (found is not null)
        {
            _workspaceCategories[cacheKey] = found.Id;
            return found;
        }

        var created = await guild.CreateCategoryChannelAsync(categoryName);
        _workspaceCategories[cacheKey] = created.Id;
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
            props.Topic = $"Direct messages — {agentName} · Room: {roomId}";
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

    // ── Room Channel Routing ──────────────────────────────────────

    /// <summary>
    /// Finds or creates the parent category for room channels.
    /// Uses "{projectName} Rooms" format; falls back to "Rooms" for legacy rooms without a project.
    /// </summary>
    private async Task<ICategoryChannel> FindOrCreateRoomCategoryAsync(SocketGuild guild, string? projectName)
    {
        var cacheKey = projectName ?? DefaultCategoryKey;

        if (_roomCategories.TryGetValue(cacheKey, out var cachedId) && cachedId != 0)
        {
            var existing = guild.GetCategoryChannel(cachedId);
            if (existing is not null)
                return existing;
            _roomCategories.TryRemove(cacheKey, out _);
        }

        var categoryName = SanitizeCategoryName(projectName is not null
            ? $"{ProjectScanner.HumanizeProjectName(projectName)} Rooms"
            : "Rooms");

        var found = guild.CategoryChannels.FirstOrDefault(
            c => c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

        if (found is not null)
        {
            _roomCategories[cacheKey] = found.Id;
            return found;
        }

        var created = await guild.CreateCategoryChannelAsync(categoryName);
        _roomCategories[cacheKey] = created.Id;
        _logger.LogInformation("Created Discord category '{CategoryName}'", categoryName);
        return created;
    }

    /// <summary>
    /// Finds or creates a Discord text channel for a room,
    /// under the project-specific category (or "Rooms" for legacy rooms).
    /// Also registers the reverse mapping.
    /// </summary>
    private async Task<ITextChannel> FindOrCreateRoomChannelAsync(SocketGuild guild, string roomId)
    {
        if (_roomChannels.TryGetValue(roomId, out var existingChannelId))
        {
            var existing = guild.GetTextChannel(existingChannelId);
            if (existing is not null)
                return existing;
            _roomChannels.TryRemove(roomId, out _);
            _channelToRoom.TryRemove(existingChannelId, out _);
        }

        // Resolve room name and project name for category scoping
        var roomName = roomId;
        string? projectName = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            var room = await runtime.GetRoomAsync(roomId);
            if (room is not null)
                roomName = room.Name;
            projectName = await runtime.GetProjectNameForRoomAsync(roomId);

            // If room has no workspace link, fall back to the active workspace name
            if (projectName is null)
                projectName = await runtime.GetActiveProjectNameAsync();
        }
        catch { /* fall back to roomId, no project scoping */ }

        var category = await FindOrCreateRoomCategoryAsync(guild, projectName);

        var channelName = SanitizeChannelName(roomName);

        // Search for existing channel in the category — verify topic contains correct roomId
        var found = guild.TextChannels.FirstOrDefault(
            c => c.CategoryId == category.Id &&
                 (c.Topic ?? "").Contains($"ID: {roomId}"));

        if (found is not null)
        {
            _roomChannels[roomId] = found.Id;
            _channelToRoom[found.Id] = roomId;
            return found;
        }

        var created = await guild.CreateTextChannelAsync(channelName, props =>
        {
            props.CategoryId = category.Id;
            props.Topic = $"Group discussion room for agent collaboration · ID: {roomId}";
        });

        _roomChannels[roomId] = created.Id;
        _channelToRoom[created.Id] = roomId;
        _logger.LogInformation("Created Discord room channel '#{ChannelName}' for room '{RoomId}'",
            channelName, roomId);

        return created;
    }

    /// <summary>
    /// Gets or creates a webhook for a Discord channel, enabling per-message sender identity.
    /// Webhooks are cached in memory and reused across messages.
    /// </summary>
    private async Task<DiscordWebhookClient?> GetOrCreateWebhookAsync(ITextChannel channel)
    {
        if (_webhooks.TryGetValue(channel.Id, out var existing))
            return existing;

        try
        {
            // Check for an existing AA webhook
            var webhooks = await channel.GetWebhooksAsync();
            var aaWebhook = webhooks.FirstOrDefault(w => w.Name == "Agent Academy");

            if (aaWebhook is null)
            {
                aaWebhook = await channel.CreateWebhookAsync("Agent Academy");
                _logger.LogInformation("Created webhook for channel '#{ChannelName}'", channel.Name);
            }

            var client = new DiscordWebhookClient(aaWebhook);
            _webhooks[channel.Id] = client;
            return client;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create webhook for channel '#{ChannelName}' — falling back to bot messages",
                channel.Name);
            return null;
        }
    }

    /// <summary>
    /// Formats an agent ID or name into a display name for Discord webhook messages.
    /// </summary>
    private static string FormatAgentDisplayName(string agentNameOrId)
    {
        if (string.IsNullOrWhiteSpace(agentNameOrId))
            return "Agent Academy";

        // If it looks like an ID (contains hyphens, all lowercase), try to humanize
        if (agentNameOrId.Contains('-') && agentNameOrId == agentNameOrId.ToLowerInvariant())
        {
            // "planner-1" → "Planner 1", "software-engineer-1" → "Software Engineer 1"
            return string.Join(' ', agentNameOrId.Split('-')
                .Select(s => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s));
        }

        return agentNameOrId;
    }

    /// <summary>
    /// Returns a unique avatar URL for each agent using DiceBear Identicons.
    /// Each agent gets a deterministic, visually distinct avatar.
    /// </summary>
    private static string GetAgentAvatarUrl(string agentNameOrId)
    {
        var seed = Uri.EscapeDataString(agentNameOrId.ToLowerInvariant());
        return $"https://api.dicebear.com/9.x/identicon/png?seed={seed}&size=128";
    }

    /// <summary>
    /// Persistent handler for messages in agent channels and room channels.
    /// Routes human replies back to the correct Agent Academy room via WorkspaceRuntime.
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
            try { await message.Channel.SendMessageAsync("ℹ️ Please reply with text — attachments can't be forwarded to the agents."); }
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
        if (_channelToRoom.TryGetValue(channelId, out var roomId))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
                await runtime.PostHumanMessageAsync(roomId, message.Content);
                _orchestrator.HandleHumanMessage(roomId);

                await message.AddReactionAsync(new Emoji("✅"));

                _logger.LogInformation(
                    "Routed Discord message to room '{RoomId}' from user '{User}'",
                    roomId, message.Author.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to route Discord message to room '{RoomId}'", roomId);
                try { await message.Channel.SendMessageAsync($"⚠️ Failed to deliver message to room. Please try again."); }
                catch { /* best-effort */ }
            }
            return;
        }

        // Check if this is an ASK_HUMAN agent channel message
        if (!_agentChannels.TryGetValue(channelId, out var agentInfo))
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            await runtime.PostHumanMessageAsync(agentInfo.RoomId, message.Content);
            _orchestrator.HandleHumanMessage(agentInfo.RoomId);

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
    /// Renames the Discord channel associated with a room.
    /// Updates both the channel name and topic to reflect the new room name.
    /// </summary>
    public async Task OnRoomRenamedAsync(string roomId, string newName, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_isConfigured) return;

        if (!_roomChannels.TryGetValue(roomId, out var channelId)) return;

        var guild = _client.GetGuild(_guildId);
        if (guild is null) return;

        var channel = guild.GetTextChannel(channelId);
        if (channel is null) return;

        try
        {
            var newChannelName = SanitizeChannelName(newName);

            await channel.ModifyAsync(props =>
            {
                props.Name = newChannelName;
                props.Topic = $"Group discussion room for agent collaboration · ID: {roomId}";
            });

            _logger.LogInformation("Renamed Discord channel for room '{RoomId}' to '#{ChannelName}'",
                roomId, newChannelName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rename Discord channel for room '{RoomId}'", roomId);
        }
    }

    /// <inheritdoc />
    public async Task OnRoomClosedAsync(string roomId, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_isConfigured) return;

        if (!_roomChannels.TryGetValue(roomId, out var channelId)) return;

        var guild = _client.GetGuild(_guildId);
        if (guild is null) return;

        var channel = guild.GetTextChannel(channelId);
        if (channel is null)
        {
            // Channel already gone — just clean up caches
            CleanupChannelCaches(roomId, channelId);
            return;
        }

        try
        {
            await channel.DeleteAsync(new RequestOptions { CancelToken = cancellationToken });
            _logger.LogInformation("Deleted Discord channel '#{ChannelName}' for closed room '{RoomId}'",
                channel.Name, roomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete Discord channel for closed room '{RoomId}'", roomId);
            throw; // Let NotificationManager record the failure via retry/tracker
        }

        CleanupChannelCaches(roomId, channelId);
    }

    private void CleanupChannelCaches(string roomId, ulong channelId)
    {
        _roomChannels.TryRemove(roomId, out _);
        _channelToRoom.TryRemove(channelId, out _);
        if (_webhooks.TryRemove(channelId, out var webhook))
            webhook.Dispose();
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

    /// <summary>
    /// Sanitizes a name for use as a Discord category name.
    /// Categories allow spaces and mixed case but have a 100-char limit.
    /// </summary>
    internal static string SanitizeCategoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "General";

        return name.Length > 100 ? name[..100] : name;
    }

    /// <summary>Routing info for an agent's Discord channel.</summary>
    private sealed record AgentChannelInfo(string AgentId, string AgentName, string RoomId);

    /// <summary>
    /// Rebuilds the in-memory channel mappings by scanning existing Discord categories.
    /// Restores DM agent channels (under "*Messages" categories) and
    /// room channels (under "*Rooms" categories). Also supports legacy naming.
    /// Survives server restarts.
    /// </summary>
    private Task RebuildChannelMappingAsync()
    {
        if (_client is null) return Task.CompletedTask;

        try
        {
            var guild = _client.GetGuild(_guildId);
            if (guild is null) return Task.CompletedTask;

            var restoredAgentChannels = 0;
            var restoredRoomChannels = 0;

            // Rebuild DM/message agent channel mappings (under "*Messages" categories, legacy "aa-*")
            foreach (var category in guild.CategoryChannels
                         .Where(c => c.Name.EndsWith(" Messages", StringComparison.OrdinalIgnoreCase) ||
                                     c.Name.StartsWith("aa-", StringComparison.OrdinalIgnoreCase)))
            {
                var channels = guild.TextChannels.Where(c => c.CategoryId == category.Id).ToList();

                foreach (var channel in channels)
                {
                    var topic = channel.Topic ?? "";
                    var agentName = channel.Name;

                    // Parse roomId from topic — supports both formats:
                    // New: "Direct messages — {agentName} · Room: {roomId}"
                    // Legacy: "Agent Academy — {agentName} questions (Room: {roomId})"
                    var restoredRoomId = "unknown";

                    // Try new format first: "· Room: {roomId}"
                    var roomMarkerNew = "· Room: ";
                    var roomStartNew = topic.IndexOf(roomMarkerNew, StringComparison.Ordinal);
                    if (roomStartNew >= 0)
                    {
                        restoredRoomId = topic[(roomStartNew + roomMarkerNew.Length)..].Trim();

                        // Parse agent name from "Direct messages — {agentName}"
                        var dashIdx = topic.IndexOf('—');
                        if (dashIdx >= 0)
                        {
                            var afterDash = topic[(dashIdx + 1)..].Trim();
                            var dotIdx = afterDash.IndexOf('·');
                            if (dotIdx > 0)
                                agentName = afterDash[..dotIdx].Trim();
                        }
                    }
                    else if (topic.Contains("Agent Academy"))
                    {
                        // Legacy format: "Agent Academy — {agentName} questions (Room: {roomId})"
                        var dashIdx = topic.IndexOf('—');
                        if (dashIdx >= 0)
                        {
                            var afterDash = topic[(dashIdx + 1)..].Trim();
                            var questionsIdx = afterDash.IndexOf(" questions", StringComparison.OrdinalIgnoreCase);
                            if (questionsIdx > 0)
                                agentName = afterDash[..questionsIdx].Trim();
                        }

                        var roomMarker = "(Room: ";
                        var roomStart = topic.IndexOf(roomMarker, StringComparison.Ordinal);
                        if (roomStart >= 0)
                        {
                            var roomValue = topic[(roomStart + roomMarker.Length)..];
                            var roomEnd = roomValue.IndexOf(')');
                            if (roomEnd > 0)
                                restoredRoomId = roomValue[..roomEnd];
                        }
                    }

                    _agentChannels[channel.Id] = new AgentChannelInfo(
                        AgentId: channel.Name,
                        AgentName: agentName,
                        RoomId: restoredRoomId
                    );
                    restoredAgentChannels++;
                }
            }

            // Rebuild room channel mappings from project categories ("* Rooms") and legacy ("AA: *", "Agent Academy", "Rooms")
            foreach (var roomCategory in guild.CategoryChannels.Where(
                         c => c.Name.EndsWith(" Rooms", StringComparison.OrdinalIgnoreCase) ||
                              c.Name.Equals("Rooms", StringComparison.OrdinalIgnoreCase) ||
                              c.Name.StartsWith("AA: ", StringComparison.OrdinalIgnoreCase) ||
                              c.Name.Equals("Agent Academy", StringComparison.OrdinalIgnoreCase)))
            {
                // Cache the category under the appropriate key
                string categoryKey;
                if (roomCategory.Name.Equals("Rooms", StringComparison.OrdinalIgnoreCase) ||
                    roomCategory.Name.Equals("Agent Academy", StringComparison.OrdinalIgnoreCase))
                    categoryKey = DefaultCategoryKey;
                else if (roomCategory.Name.EndsWith(" Rooms", StringComparison.OrdinalIgnoreCase))
                    categoryKey = roomCategory.Name[..^6]; // Strip " Rooms" suffix to get project name
                else
                    categoryKey = roomCategory.Name[4..]; // Strip "AA: " prefix (legacy)
                _roomCategories[categoryKey] = roomCategory.Id;

                foreach (var channel in guild.TextChannels.Where(c => c.CategoryId == roomCategory.Id))
                {
                    var topic = channel.Topic ?? "";
                    // Parse roomId from topic — supports both formats:
                    // Old: "Agent Academy — Room: {roomName} (ID: {roomId})"
                    // New: "... · ID: {roomId}"
                    string? roomId = null;

                    var oldMarker = "(ID: ";
                    var oldStart = topic.IndexOf(oldMarker, StringComparison.Ordinal);
                    if (oldStart >= 0)
                    {
                        var idValue = topic[(oldStart + oldMarker.Length)..];
                        var idEnd = idValue.IndexOf(')');
                        if (idEnd > 0)
                            roomId = idValue[..idEnd];
                    }

                    if (roomId is null)
                    {
                        var newMarker = "· ID: ";
                        var newStart = topic.IndexOf(newMarker, StringComparison.Ordinal);
                        if (newStart >= 0)
                            roomId = topic[(newStart + newMarker.Length)..].Trim();
                    }

                    if (roomId is not null)
                    {
                        _roomChannels[roomId] = channel.Id;
                        _channelToRoom[channel.Id] = roomId;
                        restoredRoomChannels++;
                    }
                }
            }

            if (restoredAgentChannels > 0 || restoredRoomChannels > 0)
            {
                _logger.LogInformation(
                    "Rebuilt Discord channel mapping: {AgentChannels} agent channels, {RoomChannels} room channels",
                    restoredAgentChannels, restoredRoomChannels);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rebuild Discord channel mapping — new channels will be created on demand");
        }

        return Task.CompletedTask;
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

                // Scope to owner if configured, otherwise accept any non-bot user
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

    /// <summary>
    /// Extracts a user-friendly error message from the disconnect exception chain.
    /// Discord close code 4014 = privileged intent not enabled in the Developer Portal.
    /// HTTP 401 = invalid/expired bot token.
    /// </summary>
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
