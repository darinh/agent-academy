using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Regression tests for the two defects called out in the connection manager
/// extraction review:
///   (A) ConnectAsync leaves the connection alive if post-connect init
///       (RebuildAsync / MessageReceived hookup) throws.
///   (B) DisposeAsync does not serialize with in-flight ConnectAsync and is
///       not idempotent (disposed semaphore on second call).
/// These tests drive <see cref="DiscordNotificationProvider"/> through the
/// <see cref="IDiscordConnectionManager"/> seam so we can force failures and
/// observe teardown without touching the real Discord gateway.
/// </summary>
public class DiscordNotificationProviderConcurrencyTests
{
    private static DiscordNotificationProvider CreateProvider(
        IDiscordConnectionManager connection,
        out DiscordChannelManager channelManager)
    {
        var logger = Substitute.For<ILogger<DiscordNotificationProvider>>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        channelManager = new DiscordChannelManager(
            Substitute.For<ILogger<DiscordChannelManager>>(), scopeFactory);
        var inputHandler = new DiscordInputHandler(
            Substitute.For<ILogger<DiscordInputHandler>>());
        var sender = new DiscordMessageSender(
            channelManager, Substitute.For<ILogger<DiscordMessageSender>>());
        var orchestrator = CreateMockOrchestrator();
        var router = new DiscordMessageRouter(
            scopeFactory, orchestrator, channelManager,
            Substitute.For<ILogger<DiscordMessageRouter>>());
        return new DiscordNotificationProvider(
            logger, channelManager, inputHandler, sender, router, connection);
    }

    private static Dictionary<string, string> ValidConfig() => new()
    {
        ["BotToken"] = "test-token",
        ["ChannelId"] = "123456789012345678",
        ["GuildId"] = "987654321098765432"
    };

    [Fact]
    public async Task ConnectAsync_WhenPostConnectInitThrows_DisposesClient()
    {
        // Arrange: fake connection succeeds but exposes a null Client so that
        // the provider's post-connect init NREs on `_connection.Client!.GetGuild(...)`.
        var connection = new FakeDiscordConnectionManager
        {
            ConnectSucceeds = true,
            ClientAfterConnect = null
        };
        var provider = CreateProvider(connection, out _);
        await provider.ConfigureAsync(ValidConfig());

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ConnectAsync());

        // Assert: provider tore down the leaked client.
        Assert.Equal(1, connection.DisposeClientCallCount);
    }

    [Fact]
    public async Task ConnectAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);
        await provider.ConfigureAsync(ValidConfig());

        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.ConnectAsync());
        // No new client was spun up that could leak.
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_AfterDispose_IsIdempotentNoOp_DecisionC()
    {
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);
        await provider.DisposeAsync();

        // Decision C: must NOT throw — Disposed is one of the no-op states.
        await provider.DisconnectAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledConcurrently_DoesNotThrow()
    {
        // Race multiple DisposeAsync callers. Only one should run teardown;
        // none should throw ObjectDisposedException on the internal semaphore.
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () => await provider.DisposeAsync()))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, connection.DisposeClientCallCount);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);

        await provider.DisposeAsync();
        await provider.DisposeAsync(); // Must not throw on disposed semaphore.

        // DisposeClientAsync should only run the first time; second call is a no-op.
        Assert.Equal(1, connection.DisposeClientCallCount);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightConnectAsync()
    {
        // Arrange: fake ConnectAsync blocks until we release a gate.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeDiscordConnectionManager
        {
            ConnectSucceeds = true,
            ClientAfterConnect = null, // Will cause post-connect init to NRE
            ConnectGate = gate.Task
        };
        var provider = CreateProvider(connection, out _);
        await provider.ConfigureAsync(ValidConfig());

        // Start a ConnectAsync that will block inside the lock.
        var connectTask = Task.Run(async () =>
        {
            try { await provider.ConnectAsync(); } catch { /* expected: NRE from null client */ }
        });

        // Give Connect a chance to enter the lock.
        await connection.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Act: start Dispose while Connect is still inside the lock.
        var disposeTask = provider.DisposeAsync().AsTask();

        // Dispose should NOT complete while Connect holds the lock.
        var completed = await Task.WhenAny(disposeTask, Task.Delay(150));
        Assert.NotSame(disposeTask, completed);

        // Release Connect; Dispose should now complete.
        gate.SetResult(true);
        await connectTask;
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(connection.DisposeClientCallCount >= 1);
    }

    [Fact]
    public async Task ConfigureAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);
        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.ConfigureAsync(ValidConfig()));
    }

    [Fact]
    public async Task ConnectAsync_UsesLatestConfig_WhenConfigureRacesAcrossLock()
    {
        // Arrange: first Configure with token-A, start Connect blocked on gate.
        // While Connect holds _connectLock, fire Configure(token-B). Because
        // Configure takes the same lock, it queues behind Connect. Connect must
        // therefore observe the config that was published BEFORE it acquired
        // the lock (token-A) — not a mid-flight swap.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeDiscordConnectionManager
        {
            ConnectSucceeds = true,
            ClientAfterConnect = null, // post-connect init will NRE; that's fine
            ConnectGate = gate.Task
        };
        var provider = CreateProvider(connection, out _);

        var configA = ValidConfig();
        configA["BotToken"] = "token-A";
        await provider.ConfigureAsync(configA);

        var connectTask = Task.Run(async () =>
        {
            try { await provider.ConnectAsync(); } catch { /* NRE expected */ }
        });

        // Wait for Connect to enter ConnectAsync on the fake — this is AFTER
        // ConnectAsync has already acquired _connectLock and read _config.
        await connection.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var configB = ValidConfig();
        configB["BotToken"] = "token-B";
        var configureTask = provider.ConfigureAsync(configB);

        // ConfigureAsync must block on _connectLock while Connect holds it.
        var raced = await Task.WhenAny(configureTask, Task.Delay(150));
        Assert.NotSame(configureTask, raced);

        // Release Connect; both should complete cleanly.
        gate.SetResult(true);
        await connectTask;
        await configureTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The running connection used token-A — the config that was current when
        // Connect acquired the lock. token-B only applies to the NEXT connection.
        Assert.Equal("token-A", connection.LastBotToken);
        Assert.True(provider.IsConfigured);
    }

    [Fact]
    public async Task ConfigureAsync_ConcurrentCalls_DoNotCorruptState()
    {
        // Race many Configure calls alternating between two owners. State must
        // remain consistent (IsConfigured, no exceptions, one of the two owners
        // as final value — we don't care which).
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);

        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            var cfg = ValidConfig();
            cfg["OwnerId"] = (i % 2 == 0 ? "111111111111111111" : "222222222222222222");
            await provider.ConfigureAsync(cfg);
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(provider.IsConfigured);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightSendOperation_BeforeDisposingClient()
    {
        // Race: a send captures (client, config) and is mid-flight when DisposeAsync
        // runs. Without drain, DisposeClientAsync would tear down the client out from
        // under the send. With drain, Dispose waits until the send completes.
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);

        var sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start an "in-flight send" — counts toward _inFlight via the same tracker
        // used by SendNotificationAsync/RequestInputAsync/etc.
        var sendTask = Task.Run(() => provider.RunUnderInFlightForTestingAsync(async () =>
        {
            sendStarted.TrySetResult(true);
            await releaseSend.Task;
        }));

        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Act: dispose while the "send" is still in flight.
        var disposeTask = provider.DisposeAsync().AsTask();

        // Dispose must NOT have torn down the client yet — the send is still
        // running and would race with DisposeClientAsync.
        var raced = await Task.WhenAny(disposeTask, Task.Delay(150));
        Assert.NotSame(disposeTask, raced);
        Assert.Equal(0, connection.DisposeClientCallCount);

        // Release the send; Dispose should now drain and tear down.
        releaseSend.SetResult(true);
        Assert.True(await sendTask);

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connection.DisposeClientCallCount);
    }

    [Fact]
    public async Task DisconnectAsync_WaitsForInFlightSendOperation_BeforeDisposingClient()
    {
        // Same race as Dispose, but for the lifecycle Disconnect path. Disconnect
        // also calls _connection.DisposeClientAsync() and must wait for in-flight
        // sends to drain first.
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);
        await provider.ConfigureAsync(ValidConfig());
        // Force the FSM into Connected so DisconnectAsync exercises the
        // teardown branch (per decision C, Disconnect from Configured is a
        // no-op and would not exercise the drain contract).
        provider.ForceLifecycleStateForTesting(LifecycleState.Connected);

        var sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sendTask = Task.Run(() => provider.RunUnderInFlightForTestingAsync(async () =>
        {
            sendStarted.TrySetResult(true);
            await releaseSend.Task;
        }));

        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disconnectTask = provider.DisconnectAsync();

        // Disconnect must NOT have torn down the client yet.
        var raced = await Task.WhenAny(disconnectTask, Task.Delay(150));
        Assert.NotSame(disconnectTask, raced);
        Assert.Equal(0, connection.DisposeClientCallCount);

        releaseSend.SetResult(true);
        Assert.True(await sendTask);

        await disconnectTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connection.DisposeClientCallCount);
    }

    [Fact]
    public async Task SendNotificationAsync_DuringTeardown_ReturnsFalseWithoutTouchingClient()
    {
        // While DisposeAsync is mid-drain (waiting on an in-flight send), a NEW
        // send must short-circuit cleanly via the _teardownInProgress gate rather
        // than capturing a soon-to-be-disposed client snapshot.
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);
        await provider.ConfigureAsync(ValidConfig());

        var sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // First send: keeps _inFlight > 0 so Dispose stays in WaitForDrainAsync.
        var inflightTask = Task.Run(() => provider.RunUnderInFlightForTestingAsync(async () =>
        {
            sendStarted.TrySetResult(true);
            await releaseSend.Task;
        }));
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Start dispose — sets _teardownInProgress=true, then waits for drain.
        var disposeTask = provider.DisposeAsync().AsTask();

        // Give Dispose a moment to enter teardown state.
        await Task.Delay(50);

        // Capture client-access count BEFORE the late send. If the teardown gate
        // works, TryEnterOperation rejects BEFORE GetGuildIfConnected reads
        // _connection.Client — so this counter must NOT increase. (Without this
        // assertion the test would pass even with a broken gate, because
        // IsConnected=false would also cause SendNotificationAsync to return
        // false via GetGuildIfConnected — a false positive the reviewer caught.)
        var clientAccessesBefore = connection.ClientAccessCount;

        var msg = new NotificationMessage(NotificationType.AgentThinking, Title: "t", Body: "b");
        var late = await provider.SendNotificationAsync(msg);
        Assert.False(late);
        Assert.Equal(clientAccessesBefore, connection.ClientAccessCount);

        // Cleanup: release in-flight, allow Dispose to complete.
        releaseSend.SetResult(true);
        Assert.True(await inflightTask);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConfigureAsync_WhileConnected_ThrowsInvalidOperation_DecisionA1()
    {
        // Decision A1: reconfiguring a live connection silently used to rewrite
        // _config without tearing down the underlying client (different BotToken
        // → addressing the new GuildId with the old client). The FSM rejects.
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);
        await provider.ConfigureAsync(ValidConfig());
        provider.ForceLifecycleStateForTesting(LifecycleState.Connected);

        var newConfig = ValidConfig();
        newConfig["BotToken"] = "different-token";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ConfigureAsync(newConfig));
        Assert.Contains("DisconnectAsync first", ex.Message);
    }

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_IsIdempotentNoOp_DecisionC()
    {
        // Decision C: Disconnect from non-connected state is an idempotent no-op,
        // not an error. Today's settings page double-click pattern relies on this.
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);
        await provider.ConfigureAsync(ValidConfig());

        // Three back-to-back disconnects from Configured. None should throw,
        // none should hit the connection manager.
        await provider.DisconnectAsync();
        await provider.DisconnectAsync();
        await provider.DisconnectAsync();

        Assert.Equal(0, connection.DisposeClientCallCount);
    }

    [Fact]
    public async Task DisposeAsync_DrainTimesOut_ProceedsAnyway()
    {
        // If a send hangs forever, Dispose must NOT hang forever waiting for it.
        // We can't easily test the 5-second production timeout in CI, but we can
        // assert the contract: a never-completing in-flight op does not deadlock
        // teardown — it eventually proceeds (possibly with the in-flight op still
        // pending). We bound the test wait at 7 seconds.
        var connection = new FakeDiscordConnectionManager();
        var provider = CreateProvider(connection, out _);

        var sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Never released: simulates a wedged send.
        var neverReleased = new TaskCompletionSource<bool>();

        _ = Task.Run(() => provider.RunUnderInFlightForTestingAsync(async () =>
        {
            sendStarted.TrySetResult(true);
            await neverReleased.Task;
        }));
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposeTask = provider.DisposeAsync().AsTask();

        // Dispose's drain timeout is 5s in production. Allow up to 7s here.
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(7));

        Assert.Equal(1, connection.DisposeClientCallCount);
    }


    private static AgentOrchestrator CreateMockOrchestrator()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var catalog = new AgentCatalogOptions("main", "Main", []);
        var executor = Substitute.For<IAgentExecutor>();
        var specManager = new SpecManager();
        var pipeline = new CommandPipeline(
            Array.Empty<ICommandHandler>(),
            Substitute.For<ILogger<CommandPipeline>>());
        var gitService = new GitService(Substitute.For<ILogger<GitService>>());
        var worktreeService = new WorktreeService(
            Substitute.For<ILogger<WorktreeService>>(), repositoryRoot: "/tmp/test-repo");
        var memoryLoader = new AgentMemoryLoader(
            scopeFactory, Substitute.For<ILogger<AgentMemoryLoader>>());
        var breakoutCompletion = new BreakoutCompletionService(
            scopeFactory, catalog, executor, new TestDoubles.NoOpWatchdogAgentRunner(executor), specManager, pipeline,
            memoryLoader, Substitute.For<ILogger<BreakoutCompletionService>>());
        var breakoutLifecycle = new BreakoutLifecycleService(
            scopeFactory, catalog, executor, specManager, gitService,
            memoryLoader, breakoutCompletion,
            Substitute.For<ILogger<BreakoutLifecycleService>>());
        var taskAssignmentHandler = new TaskAssignmentHandler(
            catalog, gitService, worktreeService, breakoutLifecycle,
            Substitute.For<ILogger<TaskAssignmentHandler>>());
        var turnRunner = new AgentTurnRunner(
            executor, pipeline, taskAssignmentHandler, memoryLoader, scopeFactory,
            Substitute.For<ILogger<AgentTurnRunner>>(), new TestDoubles.NoOpAgentLivenessTracker());
        var roundRunner = new ConversationRoundRunner(
            scopeFactory, catalog, turnRunner,
            Substitute.For<ILogger<ConversationRoundRunner>>());
        var dmRouter = new DirectMessageRouter(
            scopeFactory, catalog, turnRunner,
            Substitute.For<ILogger<DirectMessageRouter>>());
        var dispatchService = new OrchestratorDispatchService(roundRunner, dmRouter);
        return new AgentOrchestrator(
            scopeFactory, dispatchService, breakoutLifecycle,
            Substitute.For<ILogger<AgentOrchestrator>>());
    }

    /// <summary>
    /// Minimal fake that lets us control ConnectAsync timing, Client nullability,
    /// and observe DisposeClientAsync calls without touching the Discord gateway.
    /// </summary>
    private sealed class FakeDiscordConnectionManager : IDiscordConnectionManager
    {
        public bool ConnectSucceeds { get; set; } = true;
        public DiscordSocketClient? ClientAfterConnect { get; set; }
        public Task? ConnectGate { get; set; }
        public TaskCompletionSource<bool> ConnectEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeClientCallCount { get; private set; }
        public string? LastBotToken { get; private set; }
        public int ClientAccessCount { get; private set; }

        private DiscordSocketClient? _client;
        private bool _isConnected;

        public DiscordSocketClient? Client
        {
            get
            {
                ClientAccessCount++;
                return _client;
            }
        }
        public bool IsConnected => _isConnected;
        public string? LastError { get; private set; }

        public async Task ConnectAsync(string botToken, CancellationToken cancellationToken = default)
        {
            LastBotToken = botToken;
            ConnectEntered.TrySetResult(true);
            if (ConnectGate is not null)
                await ConnectGate.WaitAsync(cancellationToken);

            if (!ConnectSucceeds)
            {
                LastError = "fake connect failure";
                throw new InvalidOperationException(LastError);
            }

            _client = ClientAfterConnect;
            _isConnected = true;
            LastError = null;
        }

        public ValueTask DisposeClientAsync()
        {
            DisposeClientCallCount++;
            _client = null;
            _isConnected = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => DisposeClientAsync();
    }
}
