using System.Collections.Concurrent;
using AgentAcademy.Server.Services;
using Discord;
using Discord.Webhook;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Manages Discord channel and category lifecycle for Agent Academy rooms and agent DM channels.
/// Owns channel/webhook caching, creation, renaming, deletion, and startup rebuild.
/// Naming utilities live in <see cref="DiscordNameFormatter"/>.
/// Rebuild/discovery logic lives in <see cref="DiscordChannelRebuilder"/>.
/// </summary>
public sealed class DiscordChannelManager : IAsyncDisposable
{
    private readonly ILogger<DiscordChannelManager> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly ConcurrentDictionary<ulong, AgentChannelInfo> _agentChannels = new();
    private readonly ConcurrentDictionary<string, ulong> _workspaceCategories = new();
    private readonly ConcurrentDictionary<string, ulong> _roomChannels = new();
    private readonly ConcurrentDictionary<ulong, string> _channelToRoom = new();
    private readonly ConcurrentDictionary<ulong, DiscordWebhookClient> _webhooks = new();
    private readonly ConcurrentDictionary<string, ulong> _roomCategories = new();

    internal const string DefaultCategoryKey = "__default__";

    // Serializes channel/category creation to prevent duplicates from concurrent calls
    private readonly SemaphoreSlim _channelCreateLock = new(1, 1);

    public DiscordChannelManager(
        ILogger<DiscordChannelManager> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    // ── Lookup Methods ──────────────────────────────────────────

    /// <summary>Looks up the AA room ID for a Discord channel.</summary>
    public bool TryGetRoomForChannel(ulong channelId, out string? roomId)
        => _channelToRoom.TryGetValue(channelId, out roomId);

    /// <summary>Looks up agent routing info for a Discord channel.</summary>
    public bool TryGetAgentInfoForChannel(ulong channelId, out AgentChannelInfo? info)
        => _agentChannels.TryGetValue(channelId, out info);

    /// <summary>Gets the Discord channel ID for an AA room, if mapped.</summary>
    public bool TryGetRoomChannelId(string roomId, out ulong channelId)
        => _roomChannels.TryGetValue(roomId, out channelId);

    // ── Composite Operations (lock-protected) ───────────────────

    /// <summary>
    /// Finds or creates a room channel and optionally its webhook, atomically.
    /// Returns the channel and webhook (null if agent name not provided).
    /// </summary>
    public async Task<(ITextChannel Channel, DiscordWebhookClient? Webhook)> EnsureRoomChannelAsync(
        SocketGuild guild, string roomId, string? agentName)
    {
        await _channelCreateLock.WaitAsync();
        try
        {
            var channel = await FindOrCreateRoomChannelAsync(guild, roomId);
            DiscordWebhookClient? webhook = null;

            if (!string.IsNullOrEmpty(agentName))
                webhook = await GetOrCreateWebhookAsync(channel);

            return (channel, webhook);
        }
        finally
        {
            _channelCreateLock.Release();
        }
    }

    /// <summary>
    /// Finds or creates a workspace category and agent channel for DM/question routing, atomically.
    /// Returns the agent channel.
    /// </summary>
    public async Task<ITextChannel> EnsureAgentChannelAsync(
        SocketGuild guild, string roomId, string roomName, string agentId, string agentName,
        CancellationToken cancellationToken = default)
    {
        await _channelCreateLock.WaitAsync(cancellationToken);
        try
        {
            var category = await FindOrCreateWorkspaceCategoryAsync(guild, roomId, roomName);
            return await FindOrCreateAgentChannelAsync(guild, category, agentId, agentName, roomId);
        }
        finally
        {
            _channelCreateLock.Release();
        }
    }

    /// <summary>
    /// Finds or creates workspace category, agent channel, and question thread, atomically.
    /// </summary>
    public async Task<(ITextChannel Channel, IThreadChannel Thread)> EnsureQuestionThreadAsync(
        SocketGuild guild, string roomId, string roomName, string agentId, string agentName,
        string questionText, CancellationToken cancellationToken = default)
    {
        await _channelCreateLock.WaitAsync(cancellationToken);
        try
        {
            var category = await FindOrCreateWorkspaceCategoryAsync(guild, roomId, roomName);
            var channel = await FindOrCreateAgentChannelAsync(guild, category, agentId, agentName, roomId);
            var thread = await CreateQuestionThreadAsync(channel, questionText);
            return (channel, thread);
        }
        finally
        {
            _channelCreateLock.Release();
        }
    }

    // ── Room Lifecycle ──────────────────────────────────────────

    /// <summary>
    /// Renames the Discord channel associated with a room.
    /// </summary>
    public async Task RenameRoomChannelAsync(SocketGuild guild, string roomId, string newName)
    {
        if (!_roomChannels.TryGetValue(roomId, out var channelId)) return;

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

    /// <summary>
    /// Deletes the Discord channel for a closed room and cleans up caches.
    /// </summary>
    public async Task DeleteRoomChannelAsync(SocketGuild guild, string roomId, CancellationToken cancellationToken = default)
    {
        if (!_roomChannels.TryGetValue(roomId, out var channelId)) return;

        var channel = guild.GetTextChannel(channelId);
        if (channel is null)
        {
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

    // ── Rebuild / Reset ─────────────────────────────────────────

    /// <summary>
    /// Clears all cached state and disposes webhooks.
    /// Call before rebuild or on disconnect/reconfiguration.
    /// </summary>
    public async Task ResetAsync()
    {
        _agentChannels.Clear();
        _workspaceCategories.Clear();
        _roomChannels.Clear();
        _channelToRoom.Clear();
        _roomCategories.Clear();

        foreach (var webhook in _webhooks.Values)
        {
            try { webhook.Dispose(); } catch { /* best-effort */ }
        }
        _webhooks.Clear();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Rebuilds in-memory channel mappings from existing Discord state.
    /// Delegates scanning to <see cref="DiscordChannelRebuilder"/> and applies results.
    /// </summary>
    public Task RebuildAsync(SocketGuild guild)
    {
        try
        {
            _agentChannels.Clear();
            _workspaceCategories.Clear();
            _roomChannels.Clear();
            _channelToRoom.Clear();
            _roomCategories.Clear();
            // Keep webhooks — they survive reconnect and are keyed by channel ID

            var result = DiscordChannelRebuilder.ScanGuild(guild, _logger);

            foreach (var (channelId, info) in result.AgentChannels)
                _agentChannels[channelId] = info;

            foreach (var (roomId, channelId) in result.RoomChannels)
                _roomChannels[roomId] = channelId;

            foreach (var (channelId, roomId) in result.ChannelToRoom)
                _channelToRoom[channelId] = roomId;

            foreach (var (key, categoryId) in result.RoomCategories)
                _roomCategories[key] = categoryId;

            var agentCount = result.AgentChannels.Count;
            var roomCount = result.RoomChannels.Count;

            if (agentCount > 0 || roomCount > 0)
            {
                _logger.LogInformation(
                    "Rebuilt Discord channel mapping: {AgentChannels} agent channels, {RoomChannels} room channels",
                    agentCount, roomCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rebuild Discord channel mapping — new channels will be created on demand");
        }

        return Task.CompletedTask;
    }

    // ── Static Helpers (delegated to DiscordNameFormatter) ────

    /// <inheritdoc cref="DiscordNameFormatter.SanitizeChannelName"/>
    internal static string SanitizeChannelName(string name, string? fallbackId = null)
        => DiscordNameFormatter.SanitizeChannelName(name, fallbackId);

    /// <inheritdoc cref="DiscordNameFormatter.SanitizeCategoryName"/>
    internal static string SanitizeCategoryName(string name)
        => DiscordNameFormatter.SanitizeCategoryName(name);

    /// <inheritdoc cref="DiscordNameFormatter.FormatAgentDisplayName"/>
    internal static string FormatAgentDisplayName(string agentNameOrId)
        => DiscordNameFormatter.FormatAgentDisplayName(agentNameOrId);

    /// <inheritdoc cref="DiscordNameFormatter.GetAgentAvatarUrl"/>
    internal static string GetAgentAvatarUrl(string agentNameOrId)
        => DiscordNameFormatter.GetAgentAvatarUrl(agentNameOrId);

    // ── Disposal ────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _channelCreateLock.Dispose();
    }

    // ── Private Helpers ─────────────────────────────────────────

    /// <summary>Routing info for an agent's Discord channel.</summary>
    public sealed record AgentChannelInfo(string AgentId, string AgentName, string RoomId);

    private async Task<ICategoryChannel> FindOrCreateWorkspaceCategoryAsync(
        SocketGuild guild, string roomId, string roomName)
    {
        string? projectName = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
            projectName = await roomService.GetProjectNameForRoomAsync(roomId);

            if (projectName is null)
                projectName = await roomService.GetActiveProjectNameAsync();
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

    private async Task<ITextChannel> FindOrCreateAgentChannelAsync(
        SocketGuild guild, ICategoryChannel category, string agentId, string agentName, string roomId)
    {
        var channelName = SanitizeChannelName(agentName, fallbackId: agentId);

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

        var roomName = roomId;
        string? projectName = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
            var room = await roomService.GetRoomAsync(roomId);
            if (room is not null)
                roomName = room.Name;
            projectName = await roomService.GetProjectNameForRoomAsync(roomId);

            if (projectName is null)
                projectName = await roomService.GetActiveProjectNameAsync();
        }
        catch { /* fall back to roomId, no project scoping */ }

        var category = await FindOrCreateRoomCategoryAsync(guild, projectName);
        var channelName = SanitizeChannelName(roomName);

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

    private async Task<DiscordWebhookClient?> GetOrCreateWebhookAsync(ITextChannel channel)
    {
        if (_webhooks.TryGetValue(channel.Id, out var existing))
            return existing;

        try
        {
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

    private static async Task<IThreadChannel> CreateQuestionThreadAsync(ITextChannel channel, string questionText)
    {
        var threadName = questionText.Length > 97
            ? string.Concat(questionText.AsSpan(0, 97), "...")
            : questionText;

        return await channel.CreateThreadAsync(
            name: threadName,
            type: ThreadType.PublicThread,
            autoArchiveDuration: ThreadArchiveDuration.OneDay);
    }

    private void CleanupChannelCaches(string roomId, ulong channelId)
    {
        _roomChannels.TryRemove(roomId, out _);
        _channelToRoom.TryRemove(channelId, out _);
        if (_webhooks.TryRemove(channelId, out var webhook))
            webhook.Dispose();
    }
}
