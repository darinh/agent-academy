using AgentAcademy.Server.Notifications;
using Discord;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Tests;

public class DiscordDisconnectReasonResolverTests
{
    [Fact]
    public void Resolve_ReturnsNull_ForNullException()
    {
        Assert.Null(DiscordDisconnectReasonResolver.Resolve(null));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoKnownSignatureMatches()
    {
        var ex = new InvalidOperationException("totally unrelated failure");
        Assert.Null(DiscordDisconnectReasonResolver.Resolve(ex));
    }

    [Theory]
    [InlineData("WebSocket close 4014 received")]
    [InlineData("Disallowed intent provided")]
    [InlineData("disallowed INTENT requested")]
    public void Resolve_DetectsDisallowedIntent(string message)
    {
        var result = DiscordDisconnectReasonResolver.Resolve(new Exception(message));
        Assert.NotNull(result);
        Assert.Contains("MESSAGE CONTENT INTENT", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WebSocket close 4004 received")]
    [InlineData("Authentication failed for bot")]
    [InlineData("authentication FAILED")]
    public void Resolve_DetectsAuthFailure(string message)
    {
        var result = DiscordDisconnectReasonResolver.Resolve(new Exception(message));
        Assert.NotNull(result);
        Assert.Contains("invalid or revoked", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DetectsHttp401Unauthorized()
    {
        var result = DiscordDisconnectReasonResolver.Resolve(new Exception("Response 401 Unauthorized"));
        Assert.NotNull(result);
        Assert.Contains("401 Unauthorized", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DoesNotMatchHttp401WithoutUnauthorized()
    {
        Assert.Null(DiscordDisconnectReasonResolver.Resolve(new Exception("status 401")));
    }

    [Fact]
    public void Resolve_DoesNotMatchUnauthorizedWithout401()
    {
        Assert.Null(DiscordDisconnectReasonResolver.Resolve(new Exception("Unauthorized: missing scope")));
    }

    [Fact]
    public void Resolve_WalksInnerExceptions()
    {
        var inner = new Exception("WebSocket close 4014 received");
        var middle = new Exception("middle wrapper", inner);
        var outer = new Exception("outer wrapper", middle);

        var result = DiscordDisconnectReasonResolver.Resolve(outer);
        Assert.NotNull(result);
        Assert.Contains("MESSAGE CONTENT INTENT", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PrefersOutermostMatch_WhenBothOuterAndInnerMatch()
    {
        var inner = new Exception("Authentication failed");
        var outer = new Exception("WebSocket close 4014 received", inner);

        var result = DiscordDisconnectReasonResolver.Resolve(outer);
        Assert.NotNull(result);
        Assert.Contains("MESSAGE CONTENT INTENT", result, StringComparison.Ordinal);
    }
}

public class DiscordLogSeverityMapperTests
{
    [Theory]
    [InlineData(LogSeverity.Critical, LogLevel.Critical)]
    [InlineData(LogSeverity.Error, LogLevel.Error)]
    [InlineData(LogSeverity.Warning, LogLevel.Warning)]
    [InlineData(LogSeverity.Info, LogLevel.Information)]
    [InlineData(LogSeverity.Verbose, LogLevel.Debug)]
    [InlineData(LogSeverity.Debug, LogLevel.Trace)]
    public void ToLogLevel_MapsKnownSeverities(LogSeverity input, LogLevel expected)
    {
        Assert.Equal(expected, DiscordLogSeverityMapper.ToLogLevel(input));
    }

    [Fact]
    public void ToLogLevel_DefaultsToInformation_ForUnknownSeverity()
    {
        Assert.Equal(LogLevel.Information, DiscordLogSeverityMapper.ToLogLevel((LogSeverity)int.MaxValue));
    }
}
