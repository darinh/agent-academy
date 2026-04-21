using AgentAcademy.Shared.Models;
using Discord;
using Discord.Webhook;
using Discord.WebSocket;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Handles all outbound Discord message delivery — room channels, agent questions,
/// direct messages, and default channel fallback.
/// Extracted from DiscordNotificationProvider to separate message sending from connection lifecycle.
/// </summary>
public sealed class DiscordMessageSender
{
    private readonly DiscordChannelManager _channelManager;
    private readonly ILogger<DiscordMessageSender> _logger;

    public DiscordMessageSender(
        DiscordChannelManager channelManager,
        ILogger<DiscordMessageSender> logger)
    {
        _channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends a notification to the default channel with embed and optional action buttons.
    /// </summary>
    public async Task<bool> SendToDefaultChannelAsync(
        SocketGuild guild, ulong channelId, NotificationMessage message, CancellationToken cancellationToken = default)
    {
        var channel = ResolveDefaultChannel(guild, channelId);
        if (channel is null)
            return false;

        var embed = BuildEmbed(message);
        var components = BuildActionComponents(message.Actions);
        await channel.SendMessageAsync(embed: embed, components: components);

        _logger.LogDebug("Sent Discord notification to default channel: {Title}", message.Title);
        return true;
    }

    /// <summary>
    /// Sends a notification to a room-specific Discord channel using a webhook
    /// so the agent appears as the sender with a custom name and avatar.
    /// Creates the room channel and webhook on demand.
    /// Falls back to the default channel if creation fails (e.g., missing permissions).
    /// </summary>
    public async Task<bool> SendToRoomChannelAsync(
        SocketGuild guild, ulong defaultChannelId, NotificationMessage message, CancellationToken cancellationToken = default)
    {
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
            return await SendToDefaultChannelAsync(guild, defaultChannelId, message, cancellationToken);
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
    /// Sends an agent's question to the human via a dedicated Discord channel and thread.
    /// </summary>
    public async Task<bool> SendAgentQuestionAsync(
        SocketGuild guild, AgentQuestion question, CancellationToken cancellationToken = default)
    {
        try
        {
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
    }

    /// <summary>
    /// Posts a DM as a simple embed in the agent's channel (no thread).
    /// Used for agent-to-agent DMs where no reply routing is needed.
    /// </summary>
    public async Task<bool> SendDirectMessageAsync(
        SocketGuild guild, AgentQuestion dm, CancellationToken cancellationToken = default)
    {
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

    /// <summary>
    /// Maps notification type to Discord embed color.
    /// </summary>
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

    #region Internal helpers

    internal static Embed BuildEmbed(NotificationMessage message)
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

    internal static ITextChannel? ResolveDefaultChannel(SocketGuild guild, ulong channelId)
    {
        return guild.GetTextChannel(channelId);
    }

    internal static MessageComponent? BuildActionComponents(Dictionary<string, string>? actions)
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

    /// <summary>
    /// Sends a long message via webhook, splitting into multiple parts if it exceeds
    /// Discord's 2000-character limit. Parts are labeled (1/N), (2/N), etc.
    /// </summary>
    internal static async Task SendChunkedWebhookAsync(
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

    #endregion
}
