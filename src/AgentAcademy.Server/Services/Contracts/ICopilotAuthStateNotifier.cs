namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Emits room + provider notifications for Copilot auth-state transitions.
/// </summary>
public interface ICopilotAuthStateNotifier
{
    Task NotifyAsync(bool degraded, string roomId, CancellationToken ct = default);
}
