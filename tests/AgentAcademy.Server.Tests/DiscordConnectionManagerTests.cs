using AgentAcademy.Server.Notifications;
using Discord;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="DiscordConnectionManager"/>.
///
/// <para>
/// Exercising the full <c>ConnectAsync</c> path requires a real Discord
/// gateway, which is out of scope for unit tests. These tests cover:
/// constructor guards, initial state, argument validation on
/// <c>ConnectAsync</c>, idempotent disposal, and the internal
/// <c>CreateClientConfig</c> factory (configuration is pure).
/// </para>
/// </summary>
public class DiscordConnectionManagerTests
{
    private readonly ILogger<DiscordConnectionManager> _logger =
        Substitute.For<ILogger<DiscordConnectionManager>>();

    private DiscordConnectionManager CreateSut() => new(_logger);

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        Assert.Throws<ArgumentNullException>(() => new DiscordConnectionManager(null!));
    }

    [Fact]
    public void Client_IsNullBeforeConnect()
    {
        var sut = CreateSut();
        Assert.Null(sut.Client);
    }

    [Fact]
    public void IsConnected_IsFalseBeforeConnect()
    {
        var sut = CreateSut();
        Assert.False(sut.IsConnected);
    }

    [Fact]
    public void LastError_IsNullBeforeConnect()
    {
        var sut = CreateSut();
        Assert.Null(sut.LastError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConnectAsync_ThrowsOnMissingToken(string? token)
    {
        var sut = CreateSut();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.ConnectAsync(token!));
    }

    [Fact]
    public async Task DisposeClientAsync_IsNoOpWhenNeverConnected()
    {
        var sut = CreateSut();
        await sut.DisposeClientAsync(); // Should not throw
        Assert.Null(sut.Client);
        Assert.False(sut.IsConnected);
    }

    [Fact]
    public async Task DisposeAsync_IsNoOpWhenNeverConnected()
    {
        var sut = CreateSut();
        await sut.DisposeAsync(); // Should not throw
        Assert.Null(sut.Client);
    }

    [Fact]
    public async Task DisposeClientAsync_IsIdempotent()
    {
        var sut = CreateSut();
        await sut.DisposeClientAsync();
        await sut.DisposeClientAsync();
        Assert.Null(sut.Client);
    }

    [Fact]
    public void CreateClientConfig_SetsExpectedIntents()
    {
        var config = DiscordConnectionManager.CreateClientConfig();

        Assert.Equal(LogSeverity.Info, config.LogLevel);
        var intents = config.GatewayIntents;
        Assert.True(intents.HasFlag(GatewayIntents.Guilds));
        Assert.True(intents.HasFlag(GatewayIntents.GuildMessages));
        Assert.True(intents.HasFlag(GatewayIntents.MessageContent));
    }
}
