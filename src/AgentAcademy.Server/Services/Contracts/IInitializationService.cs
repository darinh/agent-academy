namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for one-time server initialization: room setup,
/// agent configuration, and crash recovery.
/// </summary>
public interface IInitializationService
{
    Task InitializeAsync();
}
