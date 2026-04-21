namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Routes direct messages to the target agent context.
/// </summary>
public interface IDirectMessageRouter
{
    /// <summary>
    /// Routes pending direct messages for the specified recipient agent.
    /// </summary>
    Task RouteAsync(string recipientAgentId);
}
