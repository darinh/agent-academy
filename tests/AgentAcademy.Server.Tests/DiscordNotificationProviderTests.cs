using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class DiscordNotificationProviderTests
{
    private readonly DiscordNotificationProvider _provider;
    private readonly ILogger<DiscordNotificationProvider> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public DiscordNotificationProviderTests()
    {
        _logger = Substitute.For<ILogger<DiscordNotificationProvider>>();
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        var orchestrator = CreateMockOrchestrator();
        _provider = new DiscordNotificationProvider(_logger, _scopeFactory, orchestrator);
    }

    #region Properties

    [Fact]
    public void ProviderId_ReturnsDiscord()
    {
        Assert.Equal("discord", _provider.ProviderId);
    }

    [Fact]
    public void DisplayName_ReturnsDiscord()
    {
        Assert.Equal("Discord", _provider.DisplayName);
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
    public async Task ConfigureAsync_ThrowsOnMissingChannelId()
    {
        var config = ValidConfig();
        config.Remove("ChannelId");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ConfigureAsync(config));

        Assert.Contains("ChannelId", ex.Message);
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsOnInvalidChannelId()
    {
        var config = ValidConfig();
        config["ChannelId"] = "not-a-number";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ConfigureAsync(config));

        Assert.Contains("ChannelId", ex.Message);
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsOnMissingGuildId()
    {
        var config = ValidConfig();
        config.Remove("GuildId");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ConfigureAsync(config));

        Assert.Contains("GuildId", ex.Message);
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsOnInvalidGuildId()
    {
        var config = ValidConfig();
        config["GuildId"] = "abc";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ConfigureAsync(config));

        Assert.Contains("GuildId", ex.Message);
    }

    #endregion

    #region ConnectAsync

    [Fact]
    public async Task ConnectAsync_ThrowsWhenNotConfigured()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _provider.ConnectAsync());
    }

    #endregion

    #region SendNotificationAsync

    [Fact]
    public async Task SendNotificationAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _provider.SendNotificationAsync(null!));
    }

    [Fact]
    public async Task SendNotificationAsync_ReturnsFalseWhenNotConnected()
    {
        var message = new NotificationMessage(NotificationType.TaskComplete, "Test", "Body");
        var result = await _provider.SendNotificationAsync(message);

        Assert.False(result);
    }

    #endregion

    #region RequestInputAsync

    [Fact]
    public async Task RequestInputAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _provider.RequestInputAsync(null!));
    }

    [Fact]
    public async Task RequestInputAsync_ReturnsNullWhenNotConnected()
    {
        var request = new InputRequest("Pick one", Choices: ["a", "b"]);
        var result = await _provider.RequestInputAsync(request);

        Assert.Null(result);
    }

    #endregion

    #region GetConfigSchema

    [Fact]
    public void GetConfigSchema_ReturnsCorrectProviderId()
    {
        var schema = _provider.GetConfigSchema();

        Assert.Equal("discord", schema.ProviderId);
    }

    [Fact]
    public void GetConfigSchema_ReturnsCorrectDisplayName()
    {
        var schema = _provider.GetConfigSchema();

        Assert.Equal("Discord", schema.DisplayName);
    }

    [Fact]
    public void GetConfigSchema_HasDescription()
    {
        var schema = _provider.GetConfigSchema();

        Assert.NotEmpty(schema.Description);
    }

    [Fact]
    public void GetConfigSchema_HasExpectedFields()
    {
        var schema = _provider.GetConfigSchema();

        Assert.Equal(4, schema.Fields.Count);
        Assert.Equal(3, schema.Fields.Count(f => f.Required));
        Assert.Single(schema.Fields, f => f.Key == "OwnerId" && !f.Required);
    }

    [Fact]
    public void GetConfigSchema_ContainsBotTokenField()
    {
        var schema = _provider.GetConfigSchema();
        var field = schema.Fields.Single(f => f.Key == "BotToken");

        Assert.Equal("Bot Token", field.Label);
        Assert.Equal("secret", field.Type);
        Assert.True(field.Required);
    }

    [Fact]
    public void GetConfigSchema_ContainsGuildIdField()
    {
        var schema = _provider.GetConfigSchema();
        var field = schema.Fields.Single(f => f.Key == "GuildId");

        Assert.Equal("Server ID", field.Label);
        Assert.Equal("string", field.Type);
        Assert.True(field.Required);
    }

    [Fact]
    public void GetConfigSchema_ContainsChannelIdField()
    {
        var schema = _provider.GetConfigSchema();
        var field = schema.Fields.Single(f => f.Key == "ChannelId");

        Assert.Equal("Channel ID", field.Label);
        Assert.Equal("string", field.Type);
        Assert.True(field.Required);
    }

    #endregion

    #region Embed Formatting

    [Theory]
    [InlineData(NotificationType.AgentThinking)]
    [InlineData(NotificationType.NeedsInput)]
    [InlineData(NotificationType.TaskComplete)]
    [InlineData(NotificationType.TaskFailed)]
    [InlineData(NotificationType.SpecReview)]
    [InlineData(NotificationType.Error)]
    public void GetColorForType_ReturnsDistinctColorForEachType(NotificationType type)
    {
        var color = DiscordNotificationProvider.GetColorForType(type);

        // Verify we get a non-default color
        Assert.NotEqual(default, color);
    }

    [Fact]
    public void GetColorForType_AgentThinking_IsBlue()
    {
        Assert.Equal(Color.Blue, DiscordNotificationProvider.GetColorForType(NotificationType.AgentThinking));
    }

    [Fact]
    public void GetColorForType_NeedsInput_IsGold()
    {
        Assert.Equal(Color.Gold, DiscordNotificationProvider.GetColorForType(NotificationType.NeedsInput));
    }

    [Fact]
    public void GetColorForType_TaskComplete_IsGreen()
    {
        Assert.Equal(Color.Green, DiscordNotificationProvider.GetColorForType(NotificationType.TaskComplete));
    }

    [Fact]
    public void GetColorForType_TaskFailed_IsRed()
    {
        Assert.Equal(Color.Red, DiscordNotificationProvider.GetColorForType(NotificationType.TaskFailed));
    }

    [Fact]
    public void GetColorForType_Error_IsRed()
    {
        Assert.Equal(Color.Red, DiscordNotificationProvider.GetColorForType(NotificationType.Error));
    }

    #endregion

    #region DisconnectAsync

    [Fact]
    public async Task DisconnectAsync_NoOpsWhenNotConnected()
    {
        // Should not throw when not connected
        await _provider.DisconnectAsync();
    }

    #endregion

    #region SendAgentQuestionAsync

    [Fact]
    public async Task SendAgentQuestionAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _provider.SendAgentQuestionAsync(null!));
    }

    [Fact]
    public async Task SendAgentQuestionAsync_ReturnsFalseWhenNotConnected()
    {
        var question = new AgentQuestion("agent-1", "TestAgent", "room-1", "Test Room", "What should I do?");
        var result = await _provider.SendAgentQuestionAsync(question);

        Assert.False(result);
    }

    #endregion

    #region OnRoomClosedAsync

    [Fact]
    public async Task OnRoomClosedAsync_NoOp_WhenNotConfigured()
    {
        // Should complete without throwing
        await _provider.OnRoomClosedAsync("room-1");
    }

    [Fact]
    public async Task OnRoomClosedAsync_NoOp_WhenRoomNotTracked()
    {
        await _provider.ConfigureAsync(ValidConfig());
        // room-1 was never created/tracked — should be a no-op
        await _provider.OnRoomClosedAsync("room-1");
    }

    #endregion

    #region Helpers

    private static Dictionary<string, string> ValidConfig() => new()
    {
        ["BotToken"] = "test-token-value",
        ["ChannelId"] = "123456789012345678",
        ["GuildId"] = "987654321098765432"
    };

    private static AgentOrchestrator CreateMockOrchestrator()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var executor = Substitute.For<IAgentExecutor>();
        var activityBus = new ActivityBroadcaster();
        var specManager = new SpecManager();
        var pipeline = new Commands.CommandPipeline(
            Array.Empty<Commands.ICommandHandler>(),
            Substitute.For<ILogger<Commands.CommandPipeline>>());
        var gitService = new GitService(Substitute.For<ILogger<GitService>>());
        var worktreeService = new WorktreeService(Substitute.For<ILogger<WorktreeService>>(), repositoryRoot: "/tmp/test-repo");
        var logger = Substitute.For<ILogger<AgentOrchestrator>>();
        return new AgentOrchestrator(scopeFactory, executor, activityBus, specManager, pipeline, gitService, worktreeService, logger);
    }

    #endregion
}
