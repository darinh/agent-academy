using System.Collections.Concurrent;
using AgentAcademy.Server.Services;
using Discord;
using Discord.Webhook;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Manages Discord channel and category lifecycle for Agent Academy rooms and agent DM channels.
/// Owns all channel/webhook caching, creation, renaming, deletion, and startup rebuild.
/// Extracted from DiscordNotificationProvider to separate channel infrastructure from messaging logic.
/// </summary>
public sealed class DiscordChannelManager : IAsyncDisposable
{
    private readonly ILogger<DiscordChannelManager> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

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
    // Discord category ID for room channels (keyed by project name, "__default__" for legacy)
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
    /// Clears stale state first, then scans categories for agent and room channels.
    /// </summary>
    public Task RebuildAsync(SocketGuild guild)
    {
        try
        {
            // Clear stale state before rebuilding
            _agentChannels.Clear();
            _workspaceCategories.Clear();
            _roomChannels.Clear();
            _channelToRoom.Clear();
            _roomCategories.Clear();
            // Keep webhooks — they survive reconnect and are keyed by channel ID

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

    // ── Static Helpers ──────────────────────────────────────────

    /// <summary>
    /// Sanitizes a name for use as a Discord channel name.
    /// Discord channel names: lowercase, hyphens instead of spaces, max 100 chars.
    /// </summary>
    internal static string SanitizeChannelName(string name, string? fallbackId = null)
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

    /// <summary>
    /// Formats an agent ID or name into a display name for Discord webhook messages.
    /// </summary>
    internal static string FormatAgentDisplayName(string agentNameOrId)
    {
        if (string.IsNullOrWhiteSpace(agentNameOrId))
            return "Agent Academy";

        // If it looks like an ID (contains hyphens, all lowercase), try to humanize
        if (agentNameOrId.Contains('-') && agentNameOrId == agentNameOrId.ToLowerInvariant())
        {
            return string.Join(' ', agentNameOrId.Split('-')
                .Select(s => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s));
        }

        return agentNameOrId;
    }

    /// <summary>
    /// Returns a unique avatar URL for each agent using DiceBear Identicons.
    /// </summary>
    internal static string GetAgentAvatarUrl(string agentNameOrId)
    {
        var seed = Uri.EscapeDataString(agentNameOrId.ToLowerInvariant());
        return $"https://api.dicebear.com/9.x/identicon/png?seed={seed}&size=128";
    }

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
            var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
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
            var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
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
