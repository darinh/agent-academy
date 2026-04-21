using Discord.WebSocket;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Abstraction over <see cref="DiscordConnectionManager"/> so that
/// <see cref="DiscordNotificationProvider"/> can be exercised against a fake
/// in tests (failure-path coverage for partial-connect teardown and
/// dispose/connect races).
/// </summary>
public interface IDiscordConnectionManager : IAsyncDisposable
{
    DiscordSocketClient? Client { get; }
    bool IsConnected { get; }
    string? LastError { get; }
    Task ConnectAsync(string botToken, CancellationToken cancellationToken = default);
    ValueTask DisposeClientAsync();
}
