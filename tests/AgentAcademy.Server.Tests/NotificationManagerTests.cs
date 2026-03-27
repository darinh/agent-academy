using AgentAcademy.Server.Notifications;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

public class NotificationManagerTests
{
    private readonly NotificationManager _manager;
    private readonly ILogger<NotificationManager> _logger;

    public NotificationManagerTests()
    {
        _logger = Substitute.For<ILogger<NotificationManager>>();
        _manager = new NotificationManager(_logger);
    }

    #region RegisterProvider

    [Fact]
    public void RegisterProvider_AddsProvider()
    {
        var provider = CreateMockProvider("test");

        _manager.RegisterProvider(provider);

        Assert.Same(provider, _manager.GetProvider("test"));
    }

    [Fact]
    public void RegisterProvider_OverwritesExistingWithSameId()
    {
        var first = CreateMockProvider("test");
        var second = CreateMockProvider("test");

        _manager.RegisterProvider(first);
        _manager.RegisterProvider(second);

        Assert.Same(second, _manager.GetProvider("test"));
    }

    [Fact]
    public void RegisterProvider_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => _manager.RegisterProvider(null!));
    }

    [Fact]
    public void RegisterProvider_CaseInsensitiveLookup()
    {
        var provider = CreateMockProvider("Slack");
        _manager.RegisterProvider(provider);

        Assert.Same(provider, _manager.GetProvider("slack"));
        Assert.Same(provider, _manager.GetProvider("SLACK"));
    }

    #endregion

    #region GetProvider

    [Fact]
    public void GetProvider_ReturnsNullForUnknownId()
    {
        Assert.Null(_manager.GetProvider("nonexistent"));
    }

    #endregion

    #region GetAllProviders

    [Fact]
    public void GetAllProviders_ReturnsEmpty_WhenNoneRegistered()
    {
        Assert.Empty(_manager.GetAllProviders());
    }

    [Fact]
    public void GetAllProviders_ReturnsAllRegistered()
    {
        _manager.RegisterProvider(CreateMockProvider("a"));
        _manager.RegisterProvider(CreateMockProvider("b"));

        var all = _manager.GetAllProviders();
        Assert.Equal(2, all.Count);
    }

    #endregion

    #region SendToAllAsync

    [Fact]
    public async Task SendToAllAsync_SendsToAllConnectedProviders()
    {
        var connected1 = CreateMockProvider("p1", isConnected: true);
        connected1.SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var connected2 = CreateMockProvider("p2", isConnected: true);
        connected2.SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var disconnected = CreateMockProvider("p3", isConnected: false);

        _manager.RegisterProvider(connected1);
        _manager.RegisterProvider(connected2);
        _manager.RegisterProvider(disconnected);

        var message = new NotificationMessage(NotificationType.TaskComplete, "Test", "Body");
        var count = await _manager.SendToAllAsync(message);

        Assert.Equal(2, count);
        await connected1.Received(1).SendNotificationAsync(message, Arg.Any<CancellationToken>());
        await connected2.Received(1).SendNotificationAsync(message, Arg.Any<CancellationToken>());
        await disconnected.DidNotReceive().SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendToAllAsync_ContinuesOnProviderFailure()
    {
        var failing = CreateMockProvider("fail", isConnected: true);
        failing.SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("connection lost"));

        var succeeding = CreateMockProvider("ok", isConnected: true);
        succeeding.SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _manager.RegisterProvider(failing);
        _manager.RegisterProvider(succeeding);

        var message = new NotificationMessage(NotificationType.Error, "Alert", "Something broke");
        var count = await _manager.SendToAllAsync(message);

        Assert.Equal(1, count);
        await succeeding.Received(1).SendNotificationAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendToAllAsync_ReturnsZero_WhenNoProvidersConnected()
    {
        _manager.RegisterProvider(CreateMockProvider("offline", isConnected: false));

        var message = new NotificationMessage(NotificationType.TaskComplete, "Test", "Body");
        var count = await _manager.SendToAllAsync(message);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task SendToAllAsync_ThrowsOnNullMessage()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _manager.SendToAllAsync(null!));
    }

    #endregion

    #region RequestInputFromAnyAsync

    [Fact]
    public async Task RequestInputFromAnyAsync_ReturnsFirstNonNullResponse()
    {
        var noInput = CreateMockProvider("console", isConnected: true);
        noInput.RequestInputAsync(Arg.Any<InputRequest>(), Arg.Any<CancellationToken>())
            .Returns((UserResponse?)null);

        var hasInput = CreateMockProvider("slack", isConnected: true);
        var expectedResponse = new UserResponse("yes", "yes", "slack");
        hasInput.RequestInputAsync(Arg.Any<InputRequest>(), Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _manager.RegisterProvider(noInput);
        _manager.RegisterProvider(hasInput);

        var request = new InputRequest("Approve deployment?", Choices: ["yes", "no"]);
        var result = await _manager.RequestInputFromAnyAsync(request);

        Assert.NotNull(result);
        Assert.Equal("yes", result.Content);
        Assert.Equal("slack", result.ProviderId);
    }

    [Fact]
    public async Task RequestInputFromAnyAsync_ReturnsNull_WhenNoProviderCanCollectInput()
    {
        var provider = CreateMockProvider("console", isConnected: true);
        provider.RequestInputAsync(Arg.Any<InputRequest>(), Arg.Any<CancellationToken>())
            .Returns((UserResponse?)null);

        _manager.RegisterProvider(provider);

        var request = new InputRequest("Input?");
        var result = await _manager.RequestInputFromAnyAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task RequestInputFromAnyAsync_SkipsFailingProvider()
    {
        var failing = CreateMockProvider("broken", isConnected: true);
        failing.RequestInputAsync(Arg.Any<InputRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("timed out"));

        var working = CreateMockProvider("working", isConnected: true);
        var expected = new UserResponse("hello", ProviderId: "working");
        working.RequestInputAsync(Arg.Any<InputRequest>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        _manager.RegisterProvider(failing);
        _manager.RegisterProvider(working);

        var request = new InputRequest("Say something");
        var result = await _manager.RequestInputFromAnyAsync(request);

        Assert.NotNull(result);
        Assert.Equal("hello", result.Content);
    }

    [Fact]
    public async Task RequestInputFromAnyAsync_ThrowsOnNullRequest()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _manager.RequestInputFromAnyAsync(null!));
    }

    #endregion

    #region Helpers

    private static INotificationProvider CreateMockProvider(string id, bool isConnected = true, bool isConfigured = true)
    {
        var provider = Substitute.For<INotificationProvider>();
        provider.ProviderId.Returns(id);
        provider.DisplayName.Returns($"Mock {id}");
        provider.IsConnected.Returns(isConnected);
        provider.IsConfigured.Returns(isConfigured);
        return provider;
    }

    #endregion
}
