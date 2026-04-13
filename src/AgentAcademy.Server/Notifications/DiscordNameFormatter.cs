using System.Text.RegularExpressions;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Pure formatting utilities for Discord channel names, category names, agent display names, and avatars.
/// Extracted from DiscordChannelManager to separate naming concerns from channel lifecycle.
/// </summary>
internal static partial class DiscordNameFormatter
{
    /// <summary>
    /// Sanitizes a name for use as a Discord channel name.
    /// Discord channel names: lowercase, hyphens instead of spaces, max 100 chars.
    /// </summary>
    public static string SanitizeChannelName(string name, string? fallbackId = null)
    {
        var sanitized = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');

        sanitized = DisallowedChannelChars().Replace(sanitized, "");
        sanitized = MultipleHyphens().Replace(sanitized, "-").Trim('-');

        if (string.IsNullOrEmpty(sanitized))
            sanitized = fallbackId is not null ? $"agent-{fallbackId[..Math.Min(8, fallbackId.Length)]}" : "unknown";

        return sanitized.Length > 100 ? sanitized[..100] : sanitized;
    }

    /// <summary>
    /// Sanitizes a name for use as a Discord category name.
    /// Categories allow spaces and mixed case but have a 100-char limit.
    /// </summary>
    public static string SanitizeCategoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "General";

        return name.Length > 100 ? name[..100] : name;
    }

    /// <summary>
    /// Formats an agent ID or name into a display name for Discord webhook messages.
    /// </summary>
    public static string FormatAgentDisplayName(string agentNameOrId)
    {
        if (string.IsNullOrWhiteSpace(agentNameOrId))
            return "Agent Academy";

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
    public static string GetAgentAvatarUrl(string agentNameOrId)
    {
        var seed = Uri.EscapeDataString(agentNameOrId.ToLowerInvariant());
        return $"https://api.dicebear.com/9.x/identicon/png?seed={seed}&size=128";
    }

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex DisallowedChannelChars();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex MultipleHyphens();
}
