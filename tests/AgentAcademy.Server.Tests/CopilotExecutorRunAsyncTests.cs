using System.Reflection;
using System.Runtime.CompilerServices;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="CopilotExecutor.RunAsync"/> error handling paths:
/// circuit breaker integration, session invalidation, auth state transitions,
/// and fallback routing. Uses the internal constructor with mocked interfaces.
/// </summary>
public sealed class CopilotExecutorRunAsyncTests : IAsyncDisposable
{
    private readonly ICopilotClientFactory _clientFactory;
    private readonly ICopilotSessionPool _sessionPool;
    private readonly ICopilotSdkSender _sender;
    private readonly ICopilotAuthStateNotifier _authNotifier;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly IAgentErrorTracker _errorTracker;
    private readonly IAgentQuotaService _quotaService;
    private readonly IAgentCatalog _catalog;
    private readonly CopilotCircuitBreaker _circuitBreaker;
    private readonly StubExecutor _fallback;
    private readonly CopilotExecutor _executor;

    private static readonly AgentDefinition TestAgent = new(
        Id: "test-agent",
        Name: "TestBot",
        Role: "Tester",
        Summary: "A test agent",
        StartupPrompt: "",
        Model: "test-model",
        CapabilityTags: [],
        EnabledTools: [],
        AutoJoinDefaultRoom: false);

    public CopilotExecutorRunAsyncTests()
    {
        _clientFactory = Substitute.For<ICopilotClientFactory>();
        _sessionPool = Substitute.For<ICopilotSessionPool>();
        _sender = Substitute.For<ICopilotSdkSender>();
        _authNotifier = Substitute.For<ICopilotAuthStateNotifier>();
        _toolRegistry = Substitute.For<IAgentToolRegistry>();
        _errorTracker = Substitute.For<IAgentErrorTracker>();
        _quotaService = Substitute.For<IAgentQuotaService>();
        _catalog = Substitute.For<IAgentCatalog>();
        _catalog.DefaultRoomId.Returns("main-room");

        // Low threshold for testability
        _circuitBreaker = new CopilotCircuitBreaker(failureThreshold: 3);
        _fallback = new StubExecutor(NullLogger<StubExecutor>.Instance);

        _executor = new CopilotExecutor(
            NullLogger<CopilotExecutor>.Instance,
            NullLogger<StubExecutor>.Instance,
            _clientFactory,
            _sessionPool,
            _sender,
            _authNotifier,
            _toolRegistry,
            _errorTracker,
            _quotaService,
            _catalog,
            new TestDoubles.NoOpAgentLivenessTracker(),
            _circuitBreaker,
            _fallback);
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a non-null CopilotClient without calling any constructor.
    /// Safe because the mocked session pool never invokes it.
    /// </summary>
    private static CopilotClient CreateDummyClient()
        => (CopilotClient)RuntimeHelpers.GetUninitializedObject(typeof(CopilotClient));

    /// <summary>
    /// Creates a CopilotSession via reflection (sealed SDK type).
    /// </summary>
    private static CopilotSession CreateFakeSession(string sessionId = "test")
    {
        var ctor = typeof(CopilotSession)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 4 && ps[0].Name == "sessionId";
            })
            ?? throw new InvalidOperationException(
                "CopilotSession internal constructor not found — SDK may have changed.");
        return (CopilotSession)ctor.Invoke([sessionId, null, null, null]);
    }

    private void SetupClientAvailable(bool wasRecreated = false)
    {
        _clientFactory.GetClientAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientAcquisitionResult(CreateDummyClient(), wasRecreated));
    }

    private void SetupClientNull(bool wasRecreated = false)
    {
        _clientFactory.GetClientAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientAcquisitionResult(null, wasRecreated));
    }

    private void SetupSessionPoolReturns(string response)
    {
        _sessionPool.UseAsync(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<CopilotSession>>>(),
            Arg.Any<Func<CopilotSession, Task<string>>>(),
            Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private void SetupSessionPoolThrows(Exception exception)
    {
        _sessionPool.UseAsync(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<CopilotSession>>>(),
            Arg.Any<Func<CopilotSession, Task<string>>>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(exception);
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Success_RecordsCircuitBreakerSuccess()
    {
        SetupClientAvailable();
        SetupSessionPoolReturns("agent response");

        var result = await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        Assert.Equal("agent response", result);
        Assert.Equal(CircuitState.Closed, _circuitBreaker.State);
        Assert.Equal(0, _circuitBreaker.ConsecutiveFailures);
    }

    [Fact]
    public async Task RunAsync_CircuitBreakerOpen_UsesFallbackWithoutCallingClientOrPool()
    {
        // Open the circuit by recording enough failures
        for (var i = 0; i < 3; i++)
            _circuitBreaker.RecordFailure();
        Assert.Equal(CircuitState.Open, _circuitBreaker.State);

        var result = await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        // Fallback (StubExecutor) returns a non-empty offline notice
        Assert.NotEmpty(result);
        // Client factory and session pool must NOT have been called
        await _clientFactory.DidNotReceive().GetClientAsync(Arg.Any<CancellationToken>());
        await _sessionPool.DidNotReceive().UseAsync(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<CopilotSession>>>(),
            Arg.Any<Func<CopilotSession, Task<string>>>(),
            Arg.Any<CancellationToken>());
        // Error tracker SHOULD record circuit_open
        await _errorTracker.Received(1).RecordAsync(
            TestAgent.Id, "room-1", "circuit_open",
            Arg.Any<string>(), recoverable: true);
    }

    [Fact]
    public async Task RunAsync_ClientNull_UsesFallback()
    {
        SetupClientNull();

        var result = await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        Assert.NotEmpty(result);
        // Session pool must NOT have been called when client is null
        await _sessionPool.DidNotReceive().UseAsync(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<CopilotSession>>>(),
            Arg.Any<Func<CopilotSession, Task<string>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ClientRecreated_ResetsCircuitBeforeRequest()
    {
        // Seed circuit with 1 failure (below threshold of 3)
        _circuitBreaker.RecordFailure();
        Assert.Equal(1, _circuitBreaker.ConsecutiveFailures);

        // Return recreated client, then make UseAsync throw to add another failure
        SetupClientAvailable(wasRecreated: true);
        SetupSessionPoolThrows(new CopilotTransientException("test transient"));

        await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        // If reset happened before the request: breaker was at 0, then +1 = 1
        // If no reset: breaker was at 1, then +1 = 2
        Assert.Equal(1, _circuitBreaker.ConsecutiveFailures);

        // All sessions should have been invalidated
        await _sessionPool.Received(1).InvalidateAllAsync();
    }

    [Fact]
    public async Task RunAsync_AuthException_MarksAuthDegraded_DoesNotTripCircuit()
    {
        SetupClientAvailable();
        SetupSessionPoolThrows(new CopilotAuthException("Token expired"));

        var result = await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        // Falls back to stub
        Assert.NotEmpty(result);
        // Auth notification sent with degraded=true
        await _authNotifier.Received().NotifyAsync(true, "main-room", Arg.Any<CancellationToken>());
        // Auth state is degraded
        Assert.True(_executor.IsAuthFailed);
        // Circuit breaker NOT tripped
        Assert.Equal(0, _circuitBreaker.ConsecutiveFailures);
        Assert.Equal(CircuitState.Closed, _circuitBreaker.State);
        // Error recorded as authentication, non-recoverable
        await _errorTracker.Received().RecordAsync(
            TestAgent.Id, "room-1", "authentication",
            Arg.Any<string>(), recoverable: false);
        // Session invalidated
        await _sessionPool.Received().InvalidateAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RunAsync_AuthorizationException_InvalidatesSession_DoesNotTripCircuit()
    {
        SetupClientAvailable();
        SetupSessionPoolThrows(new CopilotAuthorizationException("Insufficient scope"));

        var result = await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        Assert.NotEmpty(result);
        // Circuit NOT tripped
        Assert.Equal(0, _circuitBreaker.ConsecutiveFailures);
        // Error recorded as authorization, non-recoverable
        await _errorTracker.Received().RecordAsync(
            TestAgent.Id, "room-1", "authorization",
            Arg.Any<string>(), recoverable: false);
        // Session invalidated
        await _sessionPool.Received().InvalidateAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RunAsync_QuotaException_TripsCircuitBreaker()
    {
        SetupClientAvailable();
        SetupSessionPoolThrows(new CopilotQuotaException("quota", "Quota exceeded"));

        await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        Assert.Equal(1, _circuitBreaker.ConsecutiveFailures);
        await _sessionPool.Received().InvalidateAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RunAsync_TransientException_TripsCircuitBreaker()
    {
        SetupClientAvailable();
        SetupSessionPoolThrows(new CopilotTransientException("Server error"));

        await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        Assert.Equal(1, _circuitBreaker.ConsecutiveFailures);
        await _sessionPool.Received().InvalidateAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RunAsync_GenericException_TripsCircuitAndRecordsError()
    {
        SetupClientAvailable();
        SetupSessionPoolThrows(new InvalidOperationException("Something unexpected"));

        await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);

        Assert.Equal(1, _circuitBreaker.ConsecutiveFailures);
        // Error recorded as "unknown", recoverable
        await _errorTracker.Received().RecordAsync(
            TestAgent.Id, "room-1", "unknown",
            Arg.Any<string>(), recoverable: true);
        await _sessionPool.Received().InvalidateAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RunAsync_ClientRecreated_ClearsAuthFailedWhenClientNotNull()
    {
        // First: put executor into auth-failed state
        SetupClientAvailable();
        SetupSessionPoolThrows(new CopilotAuthException("Token expired"));
        await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);
        Assert.True(_executor.IsAuthFailed);

        // Now: simulate token refresh — client recreated with valid client
        SetupClientAvailable(wasRecreated: true);
        SetupSessionPoolReturns("recovered");

        var result = await _executor.RunAsync(TestAgent, "try again", "room-1", workspacePath: null);

        Assert.Equal("recovered", result);
        // Auth should be cleared
        Assert.False(_executor.IsAuthFailed);
        await _authNotifier.Received().NotifyAsync(false, "main-room", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ClientRecreated_ButClientNull_DoesNotClearAuthFailed()
    {
        // First: put executor into auth-failed state
        SetupClientAvailable();
        SetupSessionPoolThrows(new CopilotAuthException("Token expired"));
        await _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null);
        Assert.True(_executor.IsAuthFailed);

        // Simulate recreation that fails to produce a client
        SetupClientNull(wasRecreated: true);

        await _executor.RunAsync(TestAgent, "try again", "room-1", workspacePath: null);

        // Auth failure should NOT be cleared — new client didn't start
        Assert.True(_executor.IsAuthFailed);
    }

    [Fact]
    public async Task RunAsync_OperationCanceledException_Propagates()
    {
        SetupClientAvailable();
        SetupSessionPoolThrows(new OperationCanceledException("Request cancelled"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null));

        // Circuit should NOT be tripped
        Assert.Equal(0, _circuitBreaker.ConsecutiveFailures);
        // No error tracking or session invalidation
        await _errorTracker.DidNotReceive().RecordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task RunAsync_QuotaPrecheckThrows_PropagatesWithoutFallback()
    {
        _quotaService.EnforceQuotaAsync(TestAgent.Id)
            .ThrowsAsync(new AgentQuotaExceededException("test-agent", "requests_per_minute", "Quota exceeded", 60));

        // The exception should propagate — not caught by RunAsync's try/catch
        await Assert.ThrowsAsync<AgentQuotaExceededException>(
            () => _executor.RunAsync(TestAgent, "hello", "room-1", workspacePath: null));

        // Circuit should NOT be tripped (precheck is outside the try/catch)
        Assert.Equal(0, _circuitBreaker.ConsecutiveFailures);
        // Client factory should NOT have been called
        await _clientFactory.DidNotReceive().GetClientAsync(Arg.Any<CancellationToken>());
    }

    public async ValueTask DisposeAsync()
    {
        await _executor.DisposeAsync();
    }
}
