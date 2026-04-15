using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Routes inbound Discord messages back to Agent Academy rooms.
/// Handles both room-channel messages (bidirectional bridge) and
/// ASK_HUMAN agent-channel replies.
/// Extracted from DiscordNotificationProvider to separate inbound routing from connection lifecycle.
/// </summary>
public sealed class DiscordMessageRouter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly DiscordChannelManager _channelManager;
    private readonly ILogger<DiscordMessageRouter> _logger;

    public DiscordMessageRouter(
        IServiceScopeFactory scopeFactory,
        IAgentOrchestrator orchestrator,
        DiscordChannelManager channelManager,
        ILogger<DiscordMessageRouter> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Handles a Discord message received event. Routes human replies from
    /// agent channels and room channels back to the correct Agent Academy room.
    /// </summary>
    public async Task HandleMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        // Ignore webhook messages (our own webhook-sent messages)
        if (message is SocketUserMessage { Source: Discord.MessageSource.Webhook })
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
        if (_channelManager.TryGetRoomForChannel(channelId, out var roomId) && roomId is not null)
        {
            await RouteToRoomAsync(message, roomId);
            return;
        }

        // Check if this is an ASK_HUMAN agent channel message
        if (_channelManager.TryGetAgentInfoForChannel(channelId, out var agentInfo) && agentInfo is not null)
        {
            await RouteToAgentAsync(message, agentInfo);
        }
    }

    private async Task RouteToRoomAsync(SocketMessage message, string roomId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
            await messageService.PostHumanMessageAsync(roomId, message.Content);
            _orchestrator.HandleHumanMessage(roomId);

            await message.AddReactionAsync(new Discord.Emoji("✅"));

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
    }

    private async Task RouteToAgentAsync(SocketMessage message, DiscordChannelManager.AgentChannelInfo agentInfo)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
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
}
