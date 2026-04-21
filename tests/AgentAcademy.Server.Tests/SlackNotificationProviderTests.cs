using System.Net;
using System.Text.Json;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class SlackNotificationProviderTests : IDisposable
{
    private readonly SlackNotificationProvider _provider;
    private readonly ILogger<SlackNotificationProvider> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public SlackNotificationProviderTests()
    {
        _logger = Substitute.For<ILogger<SlackNotificationProvider>>();
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _provider = new SlackNotificationProvider(
            _logger,
            _scopeFactory,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<IHttpClientFactory>());
    }

    public void Dispose()
    {
        _provider.Dispose();
    }

    #region Properties

    [Fact]
    public void ProviderId_ReturnsSlack()
    {
        Assert.Equal("slack", _provider.ProviderId);
    }

    [Fact]
    public void DisplayName_ReturnsSlack()
    {
        Assert.Equal("Slack", _provider.DisplayName);
    }

    [Fact]
    public void IsConfigured_FalseByDefault()
    {
        Assert.False(_provider.IsConfigured);
    }

    [Fact]
    public void IsConnected_FalseByDefault()
    {
        Assert.False(_provider.IsConnected);
    }

    #endregion

    #region ConfigureAsync

    [Fact]
    public async Task ConfigureAsync_SetsIsConfigured()
    {
        await _provider.ConfigureAsync(ValidConfig());

        Assert.True(_provider.IsConfigured);
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsOnNullConfiguration()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _provider.ConfigureAsync(null!));
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsOnMissingBotToken()
    {
        var config = ValidConfig();
        config.Remove("BotToken");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ConfigureAsync(config));

        Assert.Contains("BotToken", ex.Message);
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsOnEmptyBotToken()
    {
        var config = ValidConfig();
        config["BotToken"] = "";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ConfigureAsync(config));

        Assert.Contains("BotToken", ex.Message);
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsOnMissingDefaultChannelId()
    {
        var config = ValidConfig();
        config.Remove("DefaultChannelId");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ConfigureAsync(config));

        Assert.Contains("DefaultChannelId", ex.Message);
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsOnEmptyDefaultChannelId()
    {
        var config = ValidConfig();
        config["DefaultChannelId"] = "   ";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ConfigureAsync(config));

        Assert.Contains("DefaultChannelId", ex.Message);
    }

    #endregion

    #region ConnectAsync

    [Fact]
    public async Task ConnectAsync_ThrowsIfNotConfigured()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _provider.ConnectAsync());
    }

    #endregion

    #region SendNotificationAsync

    [Fact]
    public async Task SendNotificationAsync_ReturnsFalseWhenNotConnected()
    {
        var message = new NotificationMessage(NotificationType.TaskComplete, "Test", "Body");

        var result = await _provider.SendNotificationAsync(message);

        Assert.False(result);
    }

    [Fact]
    public async Task SendNotificationAsync_ThrowsOnNullMessage()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _provider.SendNotificationAsync(null!));
    }

    #endregion

    #region SendAgentQuestionAsync

    [Fact]
    public async Task SendAgentQuestionAsync_ReturnsFalseWhenNotConnected()
    {
        var question = new AgentQuestion("agent1", "Planner", "room1", "Main Room", "What should I do?");

        var result = await _provider.SendAgentQuestionAsync(question);

        Assert.False(result);
    }

    [Fact]
    public async Task SendAgentQuestionAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _provider.SendAgentQuestionAsync(null!));
    }

    #endregion

    #region SendDirectMessageAsync

    [Fact]
    public async Task SendDirectMessageAsync_ReturnsFalseWhenNotConnected()
    {
        var dm = new AgentQuestion("agent1", "Planner", "room1", "Main Room", "Here's what I found");

        var result = await _provider.SendDirectMessageAsync(dm);

        Assert.False(result);
    }

    [Fact]
    public async Task SendDirectMessageAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _provider.SendDirectMessageAsync(null!));
    }

    #endregion

    #region RequestInputAsync

    [Fact]
    public async Task RequestInputAsync_ReturnsNull()
    {
        var request = new InputRequest("What do you think?");

        var result = await _provider.RequestInputAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task RequestInputAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _provider.RequestInputAsync(null!));
    }

    #endregion

    #region GetConfigSchema

    [Fact]
    public void GetConfigSchema_ReturnsSlackSchema()
    {
        var schema = _provider.GetConfigSchema();

        Assert.Equal("slack", schema.ProviderId);
        Assert.Equal("Slack", schema.DisplayName);
        Assert.Equal(2, schema.Fields.Count);
    }

    [Fact]
    public void GetConfigSchema_IncludesBotTokenField()
    {
        var schema = _provider.GetConfigSchema();

        var botToken = schema.Fields.Single(f => f.Key == "BotToken");
        Assert.Equal("secret", botToken.Type);
        Assert.True(botToken.Required);
    }

    [Fact]
    public void GetConfigSchema_IncludesDefaultChannelField()
    {
        var schema = _provider.GetConfigSchema();

        var channelId = schema.Fields.Single(f => f.Key == "DefaultChannelId");
        Assert.Equal("string", channelId.Type);
        Assert.True(channelId.Required);
    }

    #endregion

    #region ToSlackChannelName

    [Theory]
    [InlineData("Main Room", "main-room")]
    [InlineData("Agent Academy", "agent-academy")]
    [InlineData("Task: Fix Bug", "task-fix-bug")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("dots.and/slashes", "dots-and-slashes")]
    [InlineData("special!@#chars", "specialchars")]
    [InlineData("---multiple---hyphens---", "multiple-hyphens")]
    [InlineData("", "agent-academy")]
    public void ToSlackChannelName_FormatsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, SlackChannelManager.ToSlackChannelName(input));
    }

    #endregion

    #region ExtractRoomIdFromTopic

    [Theory]
    [InlineData("Agent Academy room · ID: abc123", "abc123")]
    [InlineData("ID: room-uuid-here", "room-uuid-here")]
    [InlineData("Some topic without id", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractRoomIdFromTopic_ExtractsCorrectly(string? topic, string? expectedRoomId)
    {
        Assert.Equal(expectedRoomId, SlackChannelManager.ExtractRoomIdFromTopic(topic));
    }

    #endregion

    #region EscapeSlackText

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("a & b", "a &amp; b")]
    [InlineData("<script>", "&lt;script&gt;")]
    [InlineData("1 < 2 > 0", "1 &lt; 2 &gt; 0")]
    [InlineData("a & <b>", "a &amp; &lt;b&gt;")]
    public void EscapeSlackText_EscapesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, SlackMessageBuilder.EscapeSlackText(input));
    }

    #endregion

    #region OnRoomRenamedAsync / OnRoomClosedAsync

    [Fact]
    public async Task OnRoomRenamedAsync_DoesNothingWhenNotConnected()
    {
        // Should not throw
        await _provider.OnRoomRenamedAsync("room1", "New Name");
    }

    [Fact]
    public async Task OnRoomClosedAsync_DoesNothingWhenNotConnected()
    {
        // Should not throw
        await _provider.OnRoomClosedAsync("room1");
    }

    #endregion

    #region Helpers

    private static Dictionary<string, string> ValidConfig() => new()
    {
        ["BotToken"] = "xoxb-test-token-12345",
        ["DefaultChannelId"] = "C0123456789"
    };

    #endregion
}
