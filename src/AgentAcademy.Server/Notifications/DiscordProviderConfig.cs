namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Immutable Discord provider configuration parsed from the raw configuration dictionary.
/// Presence of a non-null instance indicates the provider has been configured.
/// </summary>
/// <param name="BotToken">The bot's authentication token used to connect to Discord.</param>
/// <param name="ChannelId">The default notification channel ID.</param>
/// <param name="GuildId">The Discord server (guild) ID.</param>
/// <param name="OwnerId">Optional user ID that scopes freeform input to a specific user.</param>
internal sealed record DiscordProviderConfig(
    string BotToken,
    ulong ChannelId,
    ulong GuildId,
    ulong? OwnerId)
{
    /// <summary>
    /// Parses a raw configuration dictionary into a <see cref="DiscordProviderConfig"/>.
    /// Throws <see cref="ArgumentException"/> if required fields are missing or invalid.
    /// </summary>
    public static DiscordProviderConfig FromDictionary(Dictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var botToken = GetRequiredString(configuration, "BotToken");
        var channelId = GetRequiredUlong(configuration, "ChannelId");
        var guildId = GetRequiredUlong(configuration, "GuildId");

        ulong? ownerId = null;
        if (configuration.TryGetValue("OwnerId", out var ownerIdStr) && ulong.TryParse(ownerIdStr, out var parsedOwnerId))
            ownerId = parsedOwnerId;

        return new DiscordProviderConfig(botToken, channelId, guildId, ownerId);
    }

    private static string GetRequiredString(Dictionary<string, string> configuration, string key)
    {
        if (!configuration.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{key} is required.", nameof(configuration));

        return value;
    }

    private static ulong GetRequiredUlong(Dictionary<string, string> configuration, string key)
    {
        if (!configuration.TryGetValue(key, out var value) || !ulong.TryParse(value, out var parsed))
            throw new ArgumentException($"{key} is required and must be a valid numeric ID.", nameof(configuration));

        return parsed;
    }
}
