using AgentAcademy.Server.Notifications;
using AgentAcademy.Shared.Models;
using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Net;

namespace AgentAcademy.Server.Tests;

public class NotificationRetryPolicyTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    #region IsTransient

    [Fact]
    public void IsTransient_TimeoutException_ReturnsTrue()
    {
        Assert.True(NotificationRetryPolicy.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void IsTransient_HttpRequestException_ReturnsTrue()
    {
        Assert.True(NotificationRetryPolicy.IsTransient(new HttpRequestException()));
    }

    [Fact]
    public void IsTransient_IOException_ReturnsTrue()
    {
        Assert.True(NotificationRetryPolicy.IsTransient(new IOException()));
    }

    [Fact]
    public void IsTransient_DiscordHttpException_429_ReturnsTrue()
    {
        // Discord.Net HttpException for rate limiting (429)
        var ex = new HttpException(
            HttpStatusCode.TooManyRequests,
            Substitute.For<IRequest>(),
            null);
        Assert.True(NotificationRetryPolicy.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_DiscordHttpException_500_ReturnsTrue()
    {
        var ex = new HttpException(
            HttpStatusCode.InternalServerError,
            Substitute.For<IRequest>(),
            null);
        Assert.True(NotificationRetryPolicy.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_DiscordHttpException_403_ReturnsFalse()
    {
        var ex = new HttpException(
            HttpStatusCode.Forbidden,
            Substitute.For<IRequest>(),
            null);
        Assert.False(NotificationRetryPolicy.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_InvalidOperationException_ReturnsFalse()
    {
        Assert.False(NotificationRetryPolicy.IsTransient(new InvalidOperationException()));
    }

    [Fact]
    public void IsTransient_ArgumentException_ReturnsFalse()
    {
        Assert.False(NotificationRetryPolicy.IsTransient(new ArgumentException()));
    }

    [Fact]
    public void IsTransient_WrappedTransientInner_ReturnsTrue()
    {
        var inner = new TimeoutException("connection timed out");
        var wrapper = new Exception("wrapper", inner);
        Assert.True(NotificationRetryPolicy.IsTransient(wrapper));
    }

    [Fact]
    public void IsTransient_WrappedNonTransientInner_ReturnsFalse()
    {
        var inner = new ArgumentException("bad arg");
        var wrapper = new Exception("wrapper", inner);
        Assert.False(NotificationRetryPolicy.IsTransient(wrapper));
    }

    [Fact]
    public void IsTransient_HttpRequestException_NoStatusCode_ReturnsTrue()
    {
        // Transport-level failure (no HTTP response at all)
        var ex = new HttpRequestException("connection refused");
        Assert.True(NotificationRetryPolicy.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_HttpRequestException_429_ReturnsTrue()
    {
        var ex = new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
        Assert.True(NotificationRetryPolicy.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_HttpRequestException_500_ReturnsTrue()
    {
        var ex = new HttpRequestException("server error", null, HttpStatusCode.InternalServerError);
        Assert.True(NotificationRetryPolicy.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_HttpRequestException_403_ReturnsFalse()
    {
        var ex = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);
        Assert.False(NotificationRetryPolicy.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_HttpRequestException_404_ReturnsFalse()
    {
        var ex = new HttpRequestException("not found", null, HttpStatusCode.NotFound);
        Assert.False(NotificationRetryPolicy.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_TaskCanceledException_ReturnsFalse()
    {
        Assert.False(NotificationRetryPolicy.IsTransient(new TaskCanceledException()));
    }

    #endregion

    #region CalculateDelay

    [Fact]
    public void CalculateDelay_Attempt0_NearBaseDelay()
    {
        var delay = NotificationRetryPolicy.CalculateDelay(0);
        // BaseDelayMs=200, jitter ±50 → [150, 250]
        Assert.InRange(delay, 150, 250);
    }

    [Fact]
    public void CalculateDelay_Attempt1_DoubleBase()
    {
        var delay = NotificationRetryPolicy.CalculateDelay(1);
        // 200*2=400, jitter ±50 → [350, 450]
        Assert.InRange(delay, 350, 450);
    }

    [Fact]
    public void CalculateDelay_Attempt2_QuadBase()
    {
        var delay = NotificationRetryPolicy.CalculateDelay(2);
        // 200*4=800, jitter ±50 → [750, 850]
        Assert.InRange(delay, 750, 850);
    }

    [Fact]
    public void CalculateDelay_HighAttempt_CappedAtMax()
    {
        var delay = NotificationRetryPolicy.CalculateDelay(10);
        // Capped at MaxDelayMs=2000, jitter ±50 → [1950, 2050]
        Assert.InRange(delay, 1950, 2050);
    }

    #endregion

    #region ExecuteAsync

    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttempt_NoRetry()
    {
        var callCount = 0;
        var result = await NotificationRetryPolicy.ExecuteAsync(
            () => { callCount++; return Task.FromResult(42); },
            "test-op",
            _logger);

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesTransientFailure_ThenSucceeds()
    {
        var callCount = 0;
        var result = await NotificationRetryPolicy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount < 3) throw new TimeoutException("transient");
                return Task.FromResult(99);
            },
            "test-op",
            _logger);

        Assert.Equal(99, result);
        Assert.Equal(3, callCount); // 2 failures + 1 success
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientException_NoRetry()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NotificationRetryPolicy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw new InvalidOperationException("permanent");
                },
                "test-op",
                _logger));

        Assert.Equal(1, callCount); // No retry
    }

    [Fact]
    public async Task ExecuteAsync_ExhaustsRetries_ThrowsLastException()
    {
        var callCount = 0;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            NotificationRetryPolicy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw new HttpRequestException("network down");
                },
                "test-op",
                _logger));

        Assert.Equal(NotificationRetryPolicy.MaxRetries + 1, callCount);
        Assert.Equal("network down", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_OperationCancelledException_NotRetried()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NotificationRetryPolicy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw new OperationCanceledException();
                },
                "test-op",
                _logger));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_VoidOverload_RetriesTransientFailure()
    {
        var callCount = 0;
        await NotificationRetryPolicy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount < 2) throw new IOException("disk error");
                return Task.CompletedTask;
            },
            "test-op",
            _logger);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_TaskCanceledException_RetriedWhenTokenNotCancelled()
    {
        // TaskCanceledException from HttpClient timeout (token NOT cancelled)
        var callCount = 0;
        var result = await NotificationRetryPolicy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount < 2) throw new TaskCanceledException("http timeout");
                return Task.FromResult(true);
            },
            "test-op",
            _logger);

        Assert.True(result);
        Assert.Equal(2, callCount); // 1 timeout retry + 1 success
    }

    [Fact]
    public async Task ExecuteAsync_TaskCanceledException_NotRetriedWhenTokenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var callCount = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NotificationRetryPolicy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw new TaskCanceledException("cancelled by caller");
                },
                "test-op",
                _logger,
                cts.Token));

        Assert.Equal(1, callCount);
    }

    #endregion
}

public class NotificationManagerRetryTests
{
    private readonly NotificationManager _manager;
    private readonly ILogger<NotificationManager> _logger;

    public NotificationManagerRetryTests()
    {
        _logger = Substitute.For<ILogger<NotificationManager>>();
        _manager = new NotificationManager(_logger);
    }

    [Fact]
    public async Task SendToAllAsync_RetriesTransientFailure_ThenSucceeds()
    {
        var callCount = 0;
        var provider = Substitute.For<INotificationProvider>();
        provider.ProviderId.Returns("test");
        provider.IsConnected.Returns(true);
        provider.SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount < 2) throw new TimeoutException("transient");
                return Task.FromResult(true);
            });

        _manager.RegisterProvider(provider);

        var message = new NotificationMessage(NotificationType.TaskComplete, "Test", "Body");
        var count = await _manager.SendToAllAsync(message);

        Assert.Equal(1, count);
        Assert.Equal(2, callCount); // 1 failure + 1 success
    }

    [Fact]
    public async Task SendToAllAsync_NonTransientFailure_NoRetry_ContinuesToNextProvider()
    {
        var failProvider = Substitute.For<INotificationProvider>();
        failProvider.ProviderId.Returns("fail");
        failProvider.IsConnected.Returns(true);
        failProvider.SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("permanent"));

        var okProvider = Substitute.For<INotificationProvider>();
        okProvider.ProviderId.Returns("ok");
        okProvider.IsConnected.Returns(true);
        okProvider.SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _manager.RegisterProvider(failProvider);
        _manager.RegisterProvider(okProvider);

        var message = new NotificationMessage(NotificationType.Error, "Alert", "Something broke");
        var count = await _manager.SendToAllAsync(message);

        Assert.Equal(1, count);
        // failProvider called exactly once (no retry for non-transient)
        await failProvider.Received(1).SendNotificationAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAgentQuestionAsync_RetriesTransientFailure()
    {
        var callCount = 0;
        var provider = Substitute.For<INotificationProvider>();
        provider.ProviderId.Returns("discord");
        provider.IsConnected.Returns(true);
        provider.SendAgentQuestionAsync(Arg.Any<AgentQuestion>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount < 2) throw new HttpRequestException("network error");
                return Task.FromResult(true);
            });

        _manager.RegisterProvider(provider);

        var question = new AgentQuestion("agent-1", "Agent 1", "room-1", "Room 1", "What should I do?");
        var (sent, error) = await _manager.SendAgentQuestionAsync(question);

        Assert.True(sent);
        Assert.Null(error);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task SendDirectMessageDisplayAsync_RetriesTransientFailure()
    {
        var callCount = 0;
        var provider = Substitute.For<INotificationProvider>();
        provider.ProviderId.Returns("discord");
        provider.IsConnected.Returns(true);
        provider.SendDirectMessageAsync(Arg.Any<AgentQuestion>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount < 2) throw new IOException("socket reset");
                return Task.FromResult(true);
            });

        _manager.RegisterProvider(provider);

        var dm = new AgentQuestion("agent-1", "Agent 1", "room-1", "Room 1", "Status update");
        var (sent, error) = await _manager.SendDirectMessageDisplayAsync(dm);

        Assert.True(sent);
        Assert.Null(error);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task NotifyRoomRenamedAsync_RetriesTransientFailure()
    {
        var callCount = 0;
        var provider = Substitute.For<INotificationProvider>();
        provider.ProviderId.Returns("test");
        provider.IsConnected.Returns(true);
        provider.OnRoomRenamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount < 2) throw new TimeoutException("transient");
                return Task.CompletedTask;
            });

        _manager.RegisterProvider(provider);

        await _manager.NotifyRoomRenamedAsync("room-1", "New Name");

        Assert.Equal(2, callCount);
    }
}
