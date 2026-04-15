using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AgentAcademy.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Manages Slack channel lifecycle for Agent Academy rooms.
/// Owns channel creation, mapping recovery, rename/archive, and room→channel routing.
/// Extracted from SlackNotificationProvider to mirror the DiscordChannelManager pattern.
/// </summary>
internal sealed class SlackChannelManager
{
    private readonly ILogger<SlackChannelManager> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _channelCreateLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _roomChannels = new();

    public SlackChannelManager(
        ILogger<SlackChannelManager> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public bool TryGetCachedChannel(string roomId, out string channelId)
        => _roomChannels.TryGetValue(roomId, out channelId!);

    public void ClearMappings() => _roomChannels.Clear();

    /// <summary>
    /// Resolves the Slack channel ID for a given AA room.
    /// Creates the channel if it doesn't exist. Falls back to default channel on failure.
    /// </summary>
    public async Task<string> ResolveRoomChannelAsync(
        string roomId, string defaultChannelId, SlackApiClient apiClient, CancellationToken ct)
    {
        if (_roomChannels.TryGetValue(roomId, out var cached))
            return cached;

        await _channelCreateLock.WaitAsync(ct);
        try
        {
            if (_roomChannels.TryGetValue(roomId, out cached))
                return cached;

            string? projectName = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
                projectName = await roomService.GetProjectNameForRoomAsync(roomId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve project name for room {RoomId}", roomId);
            }

            var prefix = projectName is not null ? ToSlackChannelName(projectName) : "aa";
            var channelName = $"{prefix}-{roomId[..Math.Min(8, roomId.Length)]}";
            channelName = ToSlackChannelName(channelName);

            if (channelName.Length > 80)
                channelName = channelName[..80];

            try
            {
                var createResult = await apiClient.CreateChannelAsync(channelName, ct: ct);
                if (createResult.Ok && createResult.Channel is not null)
                {
                    var newChannelId = createResult.Channel.Id;
                    _roomChannels[roomId] = newChannelId;

                    var topic = $"Agent Academy room · ID: {roomId}";
                    await apiClient.SetChannelTopicAsync(newChannelId, topic, ct);

                    _logger.LogInformation("Created Slack channel #{Name} ({Id}) for room {RoomId}",
                        channelName, newChannelId, roomId);
                    return newChannelId;
                }

                if (createResult.Error == "name_taken")
                {
                    var existing = await FindChannelByNameAsync(apiClient, channelName, ct);
                    if (existing is not null)
                    {
                        _roomChannels[roomId] = existing;
                        return existing;
                    }
                }

                _logger.LogWarning("Failed to create Slack channel '{Name}': {Error}. Falling back to default.",
                    channelName, createResult.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error creating Slack channel for room {RoomId}. Falling back to default.", roomId);
            }

            _roomChannels[roomId] = defaultChannelId;
            return defaultChannelId;
        }
        finally
        {
            _channelCreateLock.Release();
        }
    }

    /// <summary>
    /// Scans existing Slack channels to rebuild the room → channel mapping on startup.
    /// </summary>
    public async Task RebuildChannelMappingAsync(SlackApiClient apiClient, CancellationToken ct)
    {
        try
        {
            string? cursor = null;
            var recovered = 0;

            do
            {
                var result = await apiClient.ListChannelsAsync(cursor: cursor, ct: ct);
                if (!result.Ok || result.Channels is null)
                    break;

                foreach (var channel in result.Channels)
                {
                    var roomId = ExtractRoomIdFromTopic(channel.Topic?.Value);
                    if (roomId is not null)
                    {
                        _roomChannels[roomId] = channel.Id;
                        recovered++;
                    }
                }

                cursor = result.ResponseMetadata?.NextCursor;
            } while (!string.IsNullOrEmpty(cursor));

            if (recovered > 0)
            {
                _logger.LogInformation("Recovered {Count} Slack channel mappings from existing channels", recovered);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to rebuild Slack channel mapping — new channels will be created as needed");
        }
    }

    /// <summary>
    /// Renames the Slack channel associated with a room.
    /// </summary>
    public async Task RenameRoomChannelAsync(
        string roomId, string newName, SlackApiClient apiClient, CancellationToken ct)
    {
        if (_roomChannels.TryGetValue(roomId, out var channelId))
        {
            try
            {
                var slackName = ToSlackChannelName(newName);
                var result = await apiClient.RenameChannelAsync(channelId, slackName, ct);
                if (result.Ok)
                    _logger.LogInformation("Renamed Slack channel {ChannelId} to {NewName}", channelId, slackName);
                else
                    _logger.LogWarning("Failed to rename Slack channel {ChannelId}: {Error}", channelId, result.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error renaming Slack channel for room {RoomId}", roomId);
            }
        }
    }

    /// <summary>
    /// Archives the Slack channel associated with a room and removes the mapping.
    /// </summary>
    public async Task ArchiveRoomChannelAsync(
        string roomId, SlackApiClient apiClient, CancellationToken ct)
    {
        if (_roomChannels.TryRemove(roomId, out var channelId))
        {
            try
            {
                var result = await apiClient.ArchiveChannelAsync(channelId, ct);
                if (result.Ok)
                    _logger.LogInformation("Archived Slack channel {ChannelId} for room {RoomId}", channelId, roomId);
                else
                    _logger.LogWarning("Failed to archive Slack channel {ChannelId}: {Error}", channelId, result.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error archiving Slack channel for room {RoomId}", roomId);
            }
        }
    }

    /// <summary>
    /// Extracts a room ID from a Slack channel topic containing "ID: {roomId}".
    /// </summary>
    internal static string? ExtractRoomIdFromTopic(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return null;
        var match = Regex.Match(topic, @"ID:\s*(\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Converts a name to a valid Slack channel name (lowercase, no spaces, max 80 chars).
    /// </summary>
    internal static string ToSlackChannelName(string name)
    {
        var result = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('.', '-')
            .Replace('/', '-');

        result = Regex.Replace(result, @"[^a-z0-9\-_]", "");
        result = Regex.Replace(result, @"-{2,}", "-");
        result = result.Trim('-');

        return string.IsNullOrEmpty(result) ? "agent-academy" : result;
    }

    private static async Task<string?> FindChannelByNameAsync(
        SlackApiClient apiClient, string name, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var result = await apiClient.ListChannelsAsync(cursor: cursor, ct: ct);
            if (!result.Ok || result.Channels is null) break;

            var match = result.Channels.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Id;

            cursor = result.ResponseMetadata?.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));

        return null;
    }
}
