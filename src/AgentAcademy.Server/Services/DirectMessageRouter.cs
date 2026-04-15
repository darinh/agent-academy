using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Routes direct messages to agents: forwards DMs to breakout rooms when
/// the agent is working there, otherwise runs a targeted turn in the
/// agent's current room. Creates a fresh DI scope per invocation.
/// Extracted from AgentOrchestrator to isolate DM routing from queue management.
/// </summary>
public sealed class DirectMessageRouter : IDirectMessageRouter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentCatalog _catalog;
    private readonly AgentTurnRunner _turnRunner;
    private readonly ILogger<DirectMessageRouter> _logger;

    public DirectMessageRouter(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
        AgentTurnRunner turnRunner,
        ILogger<DirectMessageRouter> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _turnRunner = turnRunner;
        _logger = logger;
    }

    /// <summary>
    /// Routes a direct message to the specified agent. If the agent is in a
    /// breakout room, DMs are forwarded there. Otherwise a targeted turn is
    /// run in the agent's current room.
    /// </summary>
    public async Task RouteAsync(string recipientAgentId)
    {
        _logger.LogInformation("DM round for agent {AgentId}", recipientAgentId);

        using var scope = _scopeFactory.CreateScope();
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var agentLocationService = scope.ServiceProvider.GetRequiredService<IAgentLocationService>();
        var activity = scope.ServiceProvider.GetRequiredService<IActivityPublisher>();
        var configService = scope.ServiceProvider.GetRequiredService<IAgentConfigService>();
        var contextLoader = scope.ServiceProvider.GetRequiredService<RoundContextLoader>();

        var catalogAgent = _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, recipientAgentId, StringComparison.OrdinalIgnoreCase));

        if (catalogAgent is null)
        {
            _logger.LogWarning("DM round: agent {AgentId} not found in catalog", recipientAgentId);
            return;
        }

        var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

        // If agent is in a breakout room, forward DMs there instead
        var location = await agentLocationService.GetAgentLocationAsync(agent.Id);
        if (location?.State == AgentState.Working && location.BreakoutRoomId is not null)
        {
            var dms = await messageService.GetDirectMessagesForAgentAsync(agent.Id, limit: 5);
            if (dms.Count > 0)
            {
                foreach (var dm in dms)
                {
                    await messageService.PostBreakoutMessageAsync(
                        location.BreakoutRoomId,
                        "system", "System", "System",
                        $"📩 Direct message from {dm.SenderName}: {dm.Content}");
                }
                await messageService.AcknowledgeDirectMessagesAsync(agent.Id, dms.Select(m => m.Id).ToList());
            }
            _logger.LogInformation(
                "DM round: agent {AgentName} is in breakout room. DM posted to breakout context.",
                agent.Name);
            return;
        }

        var roomId = location?.RoomId;
        if (roomId is null)
        {
            var rooms = await roomService.GetRoomsAsync();
            roomId = rooms.FirstOrDefault()?.Id ?? "main";
        }

        var room = await roomService.GetRoomAsync(roomId);
        if (room is null) return;

        var ctx = await contextLoader.LoadAsync(roomId);

        await _turnRunner.RunAgentTurnAsync(
            catalogAgent, scope, messageService, configService, activity,
            room, roomId, ctx.SpecContext,
            sessionSummary: ctx.SessionSummary, sprintPreamble: ctx.SprintPreamble, specVersion: ctx.SpecVersion);

        _logger.LogInformation("DM round completed for agent {AgentName}", agent.Name);
    }
}
