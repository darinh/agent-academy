using System.Collections.Concurrent;
using Discord.WebSocket;
using static AgentAcademy.Server.Notifications.DiscordChannelManager;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Rebuilds in-memory Discord channel mappings from existing guild state on startup/reconnect.
/// Parses channel topics to recover agent routing info, room-to-channel mappings, and category caches.
/// Extracted from DiscordChannelManager to separate discovery/parsing from channel lifecycle.
/// </summary>
internal static class DiscordChannelRebuilder
{
    /// <summary>Results of scanning a Discord guild for existing Agent Academy channels.</summary>
    public sealed record RebuildResult(
        Dictionary<ulong, AgentChannelInfo> AgentChannels,
        Dictionary<string, ulong> RoomChannels,
        Dictionary<ulong, string> ChannelToRoom,
        Dictionary<string, ulong> RoomCategories);

    /// <summary>
    /// Scans the guild for existing Agent Academy channels and categories, returning
    /// the discovered mappings. The caller applies these to its concurrent dictionaries.
    /// </summary>
    public static RebuildResult ScanGuild(SocketGuild guild, ILogger logger)
    {
        var agentChannels = new Dictionary<ulong, AgentChannelInfo>();
        var roomChannels = new Dictionary<string, ulong>();
        var channelToRoom = new Dictionary<ulong, string>();
        var roomCategories = new Dictionary<string, ulong>();

        ScanAgentChannels(guild, agentChannels, logger);
        ScanRoomChannels(guild, roomChannels, channelToRoom, roomCategories, logger);

        return new RebuildResult(agentChannels, roomChannels, channelToRoom, roomCategories);
    }

    /// <summary>
    /// Scans DM/message agent channel mappings under "*Messages" and legacy "aa-*" categories.
    /// </summary>
    private static void ScanAgentChannels(
        SocketGuild guild,
        Dictionary<ulong, AgentChannelInfo> agentChannels,
        ILogger logger)
    {
        foreach (var category in guild.CategoryChannels
                     .Where(c => c.Name.EndsWith(" Messages", StringComparison.OrdinalIgnoreCase) ||
                                 c.Name.StartsWith("aa-", StringComparison.OrdinalIgnoreCase)))
        {
            var channels = guild.TextChannels.Where(c => c.CategoryId == category.Id).ToList();

            foreach (var channel in channels)
            {
                var topic = channel.Topic ?? "";
                var agentName = channel.Name;
                var restoredRoomId = "unknown";

                ParseAgentChannelTopic(topic, ref agentName, ref restoredRoomId);

                agentChannels[channel.Id] = new AgentChannelInfo(
                    AgentId: channel.Name,
                    AgentName: agentName,
                    RoomId: restoredRoomId
                );
            }
        }
    }

    /// <summary>
    /// Parses an agent channel topic to extract agent name and room ID.
    /// Supports both current and legacy topic formats.
    /// </summary>
    internal static void ParseAgentChannelTopic(string topic, ref string agentName, ref string roomId)
    {
        // Try new format first: "Direct messages — {agentName} · Room: {roomId}"
        var roomMarkerNew = "· Room: ";
        var roomStartNew = topic.IndexOf(roomMarkerNew, StringComparison.Ordinal);
        if (roomStartNew >= 0)
        {
            roomId = topic[(roomStartNew + roomMarkerNew.Length)..].Trim();

            var dashIdx = topic.IndexOf('—');
            if (dashIdx >= 0)
            {
                var afterDash = topic[(dashIdx + 1)..].Trim();
                var dotIdx = afterDash.IndexOf('·');
                if (dotIdx > 0)
                    agentName = afterDash[..dotIdx].Trim();
            }
            return;
        }

        // Legacy format: "Agent Academy — {agentName} questions (Room: {roomId})"
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

            var roomMarker = "(Room: ";
            var roomStart = topic.IndexOf(roomMarker, StringComparison.Ordinal);
            if (roomStart >= 0)
            {
                var roomValue = topic[(roomStart + roomMarker.Length)..];
                var roomEnd = roomValue.IndexOf(')');
                if (roomEnd > 0)
                    roomId = roomValue[..roomEnd];
            }
        }
    }

    /// <summary>
    /// Scans room channels from project categories ("* Rooms") and legacy categories.
    /// </summary>
    private static void ScanRoomChannels(
        SocketGuild guild,
        Dictionary<string, ulong> roomChannels,
        Dictionary<ulong, string> channelToRoom,
        Dictionary<string, ulong> roomCategories,
        ILogger logger)
    {
        foreach (var roomCategory in guild.CategoryChannels.Where(
                     c => c.Name.EndsWith(" Rooms", StringComparison.OrdinalIgnoreCase) ||
                          c.Name.Equals("Rooms", StringComparison.OrdinalIgnoreCase) ||
                          c.Name.StartsWith("AA: ", StringComparison.OrdinalIgnoreCase) ||
                          c.Name.Equals("Agent Academy", StringComparison.OrdinalIgnoreCase)))
        {
            string categoryKey;
            if (roomCategory.Name.Equals("Rooms", StringComparison.OrdinalIgnoreCase) ||
                roomCategory.Name.Equals("Agent Academy", StringComparison.OrdinalIgnoreCase))
                categoryKey = DefaultCategoryKey;
            else if (roomCategory.Name.EndsWith(" Rooms", StringComparison.OrdinalIgnoreCase))
                categoryKey = roomCategory.Name[..^6];
            else
                categoryKey = roomCategory.Name[4..];

            roomCategories[categoryKey] = roomCategory.Id;

            foreach (var channel in guild.TextChannels.Where(c => c.CategoryId == roomCategory.Id))
            {
                var roomId = ParseRoomIdFromTopic(channel.Topic ?? "");
                if (roomId is not null)
                {
                    roomChannels[roomId] = channel.Id;
                    channelToRoom[channel.Id] = roomId;
                }
            }
        }
    }

    /// <summary>
    /// Parses a room ID from a channel topic. Supports both old and new topic formats.
    /// </summary>
    internal static string? ParseRoomIdFromTopic(string topic)
    {
        // Old format: "Agent Academy — Room: {roomName} (ID: {roomId})"
        var oldMarker = "(ID: ";
        var oldStart = topic.IndexOf(oldMarker, StringComparison.Ordinal);
        if (oldStart >= 0)
        {
            var idValue = topic[(oldStart + oldMarker.Length)..];
            var idEnd = idValue.IndexOf(')');
            if (idEnd > 0)
                return idValue[..idEnd];
        }

        // New format: "... · ID: {roomId}"
        var newMarker = "· ID: ";
        var newStart = topic.IndexOf(newMarker, StringComparison.Ordinal);
        if (newStart >= 0)
            return topic[(newStart + newMarker.Length)..].Trim();

        return null;
    }
}
