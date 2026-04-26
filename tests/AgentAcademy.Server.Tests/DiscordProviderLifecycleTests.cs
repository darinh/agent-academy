using AgentAcademy.Server.Notifications;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Transition matrix for <see cref="DiscordProviderLifecycle"/>. Asserts every
/// legal transition from §4.2 of the design doc and that every illegal
/// transition raises <see cref="InvalidOperationException"/> (or
/// <see cref="ObjectDisposedException"/> from terminal state).
///
/// <para>
/// This is the central artifact of the FSM refactor — the safety net the
/// pre-refactor code lacked. If a future change loosens an invariant
/// accidentally, one of these tests breaks.
/// </para>
/// </summary>
public class DiscordProviderLifecycleTests
{
    // Theory parameters that reference internal LifecycleState require internal
    // test methods. Mark Theory test methods internal individually below — public
    // shim methods would force ugly enum-as-int marshalling.
    private static DiscordProviderConfig SampleConfig(ulong? ownerId = null) =>
        new(BotToken: "tok", ChannelId: 123UL, GuildId: 456UL, OwnerId: ownerId);

    private static DiscordProviderConfig SampleConfig2() =>
        new(BotToken: "tok2", ChannelId: 789UL, GuildId: 4242UL, OwnerId: null);

    // ===== Initial state =====

    [Fact]
    public void NewLifecycle_StartsInCreated()
    {
        var fsm = new DiscordProviderLifecycle();
        Assert.Equal(LifecycleState.Created, fsm.State);
        Assert.False(fsm.IsConfigured);
        Assert.False(fsm.IsConnectedSnapshot);
        Assert.Null(fsm.ConfigSnapshot);
    }

    // ===== Configure transitions =====

    [Fact]
    public async Task Configure_FromCreated_TransitionsToConfigured()
    {
        var fsm = new DiscordProviderLifecycle();
        var effective = await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);

        Assert.Equal(LifecycleState.Configured, fsm.State);
        Assert.True(fsm.IsConfigured);
        Assert.Same(effective, fsm.ConfigSnapshot);
    }

    [Fact]
    public async Task Configure_FromConfigured_RewritesAndStaysConfigured()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        var second = await fsm.ConfigureAsync(SampleConfig2(), CancellationToken.None);

        Assert.Equal(LifecycleState.Configured, fsm.State);
        Assert.Equal("tok2", second.BotToken);
        Assert.Equal(4242UL, fsm.ConfigSnapshot!.GuildId);
    }

    [Fact]
    public async Task Configure_PreservesOwnerId_WhenRewriteOmitsIt()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(ownerId: 9999UL), CancellationToken.None);

        var rewrite = SampleConfig2(); // OwnerId == null
        var effective = await fsm.ConfigureAsync(rewrite, CancellationToken.None);

        Assert.Equal(9999UL, effective.OwnerId);
    }

    [Fact]
    public async Task Configure_DoesNotPreserveOwnerId_WhenRewriteSpecifiesIt()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(ownerId: 9999UL), CancellationToken.None);

        var rewrite = SampleConfig(ownerId: 1111UL);
        var effective = await fsm.ConfigureAsync(rewrite, CancellationToken.None);

        Assert.Equal(1111UL, effective.OwnerId);
    }

    [Fact]
    public async Task Configure_FromConnected_ThrowsInvalidOperation_DecisionA1()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fsm.ConfigureAsync(SampleConfig2(), CancellationToken.None));
        Assert.Contains("DisconnectAsync first", ex.Message);
    }

    [Fact]
    public async Task Configure_FromConnecting_ThrowsInvalidOperation()
    {
        // Simulate a pinned Connecting state. In production this is unreachable
        // from outside the lock, but the FSM contract still rejects it loudly.
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connecting);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fsm.ConfigureAsync(SampleConfig2(), CancellationToken.None));
    }

    [Fact]
    public async Task Configure_AfterDispose_ThrowsObjectDisposed()
    {
        var fsm = new DiscordProviderLifecycle();
        await using (await fsm.BeginDisposeAsync()) { }

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fsm.ConfigureAsync(SampleConfig(), CancellationToken.None));
    }

    [Fact]
    public async Task Configure_RejectsNullParsed()
    {
        var fsm = new DiscordProviderLifecycle();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => fsm.ConfigureAsync(null!, CancellationToken.None));
    }

    // ===== Connect transitions =====

    [Fact]
    public async Task Connect_FromCreated_ThrowsInvalidOperation()
    {
        var fsm = new DiscordProviderLifecycle();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fsm.BeginConnectAsync(CancellationToken.None));
        Assert.Equal(LifecycleState.Created, fsm.State);
    }

    [Fact]
    public async Task Connect_FromConfigured_TransitionsToConnecting_ThenConnectedOnComplete()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);

        var lease = await fsm.BeginConnectAsync(CancellationToken.None);
        Assert.Equal(LifecycleState.Connecting, fsm.State);
        Assert.False(lease.AlreadyConnectedFlag);

        lease.Complete();
        await lease.DisposeAsync();
        Assert.Equal(LifecycleState.Connected, fsm.State);
    }

    [Fact]
    public async Task Connect_FromConfigured_RollsBackOnLeaseDisposeWithoutComplete()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);

        var lease = await fsm.BeginConnectAsync(CancellationToken.None);
        Assert.Equal(LifecycleState.Connecting, fsm.State);

        // Lease disposed without Complete() — auto-rollback to Configured.
        await lease.DisposeAsync();
        Assert.Equal(LifecycleState.Configured, fsm.State);
    }

    [Fact]
    public async Task Connect_FromConnected_ReturnsAlreadyConnectedLease()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());

        var lease = await fsm.BeginConnectAsync(CancellationToken.None);
        Assert.True(lease.AlreadyConnectedFlag);
        await lease.DisposeAsync();
        Assert.Equal(LifecycleState.Connected, fsm.State);
    }

    [Fact]
    public async Task Connect_FromDisconnecting_ThrowsInvalidOperation()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Disconnecting);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fsm.BeginConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Connect_AfterDispose_ThrowsObjectDisposed()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        await using (await fsm.BeginDisposeAsync()) { }

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fsm.BeginConnectAsync(CancellationToken.None));
    }

    // ===== Disconnect transitions =====

    [Fact]
    public async Task Disconnect_FromCreated_IsNoOp_DecisionC()
    {
        var fsm = new DiscordProviderLifecycle();
        await using var lease = await fsm.BeginDisconnectAsync(CancellationToken.None);
        Assert.False(lease.NeedsTeardown);
        Assert.Equal(LifecycleState.Created, fsm.State);
    }

    [Fact]
    public async Task Disconnect_FromConfigured_IsNoOp_DecisionC()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);

        await using var lease = await fsm.BeginDisconnectAsync(CancellationToken.None);
        Assert.False(lease.NeedsTeardown);
        Assert.Equal(LifecycleState.Configured, fsm.State);
    }

    [Fact]
    public async Task Disconnect_FromConnected_TransitionsToDisconnecting_ThenConfigured()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());

        var lease = await fsm.BeginDisconnectAsync(CancellationToken.None);
        Assert.Equal(LifecycleState.Disconnecting, fsm.State);
        Assert.True(lease.NeedsTeardown);
        Assert.True(fsm.IsTeardownInProgress);

        await lease.DisposeAsync();
        Assert.Equal(LifecycleState.Configured, fsm.State);
        Assert.False(fsm.IsTeardownInProgress);
    }

    [Fact]
    public async Task Disconnect_FromDisconnecting_IsIdempotent_DecisionC()
    {
        // Two concurrent Disconnect callers — second should serialize on the
        // lock and observe Configured (after first completes), then no-op.
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());

        var firstLease = await fsm.BeginDisconnectAsync(CancellationToken.None);
        var secondTask = Task.Run(() => fsm.BeginDisconnectAsync(CancellationToken.None));

        // Second disconnect is queued behind the first.
        var raced = await Task.WhenAny(secondTask, Task.Delay(100));
        Assert.NotSame(secondTask, raced);

        await firstLease.DisposeAsync();
        var secondLease = await secondTask;
        Assert.False(secondLease.NeedsTeardown);
        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_AfterDispose_IsIdempotentNoOp_DecisionC()
    {
        var fsm = new DiscordProviderLifecycle();
        await using (await fsm.BeginDisposeAsync()) { }

        // Decision C: Disconnect from Disposed is no-op (not throw).
        await using var lease = await fsm.BeginDisconnectAsync(CancellationToken.None);
        Assert.False(lease.NeedsTeardown);
    }

    // ===== Dispose transitions =====

    [Fact]
    public async Task Dispose_FromCreated_TransitionsToDisposed()
    {
        var fsm = new DiscordProviderLifecycle();
        var lease = await fsm.BeginDisposeAsync();
        Assert.True(lease.ShouldRunTeardown);
        Assert.Equal(LifecycleState.Disposing, fsm.State);

        await lease.DisposeAsync();
        Assert.Equal(LifecycleState.Disposed, fsm.State);
    }

    [Fact]
    public async Task Dispose_SecondCall_IsNoOp()
    {
        var fsm = new DiscordProviderLifecycle();
        var first = await fsm.BeginDisposeAsync();
        Assert.True(first.ShouldRunTeardown);
        await first.DisposeAsync();

        var second = await fsm.BeginDisposeAsync();
        Assert.False(second.ShouldRunTeardown);
        await second.DisposeAsync();
        Assert.Equal(LifecycleState.Disposed, fsm.State);
    }

    [Fact]
    public async Task Dispose_NeverClearsTeardownFlag()
    {
        // Critical: Disposing → Disposed must NOT call EndTeardown(). Otherwise
        // operations could enter after terminal teardown.
        var fsm = new DiscordProviderLifecycle();
        await using (await fsm.BeginDisposeAsync()) { }

        Assert.True(fsm.IsTeardownInProgress);

        // And TryEnter rejects.
        using var op = fsm.TryEnterOperation(OperationKind.Send);
        Assert.False(op.Permitted);
    }

    [Fact]
    public async Task Disconnect_ClearsTeardownFlag_SoOpsCanResumeAfterReconnect()
    {
        // Critical: Disconnecting → Configured must call EndTeardown(). Otherwise
        // a reconnected provider's first send would be rejected.
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());

        await using (await fsm.BeginDisconnectAsync(CancellationToken.None)) { }

        Assert.False(fsm.IsTeardownInProgress);
        Assert.Equal(LifecycleState.Configured, fsm.State);

        // After a reconnect, ops should be admitted again.
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());
        using var op = fsm.TryEnterOperation(OperationKind.Send);
        Assert.True(op.Permitted);
    }

    // ===== TryEnterOperation gate =====

    [Fact]
    public Task TryEnter_Send_RejectsInCreated() => AssertSendRejected(LifecycleState.Created);

    [Fact]
    public Task TryEnter_Send_RejectsInConfigured() => AssertSendRejected(LifecycleState.Configured);

    [Fact]
    public Task TryEnter_Send_RejectsInConnecting() => AssertSendRejected(LifecycleState.Connecting);

    [Fact]
    public Task TryEnter_Send_RejectsInDisconnecting() => AssertSendRejected(LifecycleState.Disconnecting);

    [Fact]
    public Task TryEnter_Send_RejectsInDisposing() => AssertSendRejected(LifecycleState.Disposing);

    private static async Task AssertSendRejected(LifecycleState state)
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(state, SampleConfig());

        using var lease = fsm.TryEnterOperation(OperationKind.Send);
        Assert.False(lease.Permitted);
        Assert.NotNull(lease.RejectionReason);
    }

    [Fact]
    public async Task TryEnter_Send_GrantsInConnected()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());

        using var lease = fsm.TryEnterOperation(OperationKind.Send);
        Assert.True(lease.Permitted);
        Assert.Equal(1, fsm.InFlightCount);
    }

    [Fact]
    public Task TryEnter_RoomLifecycle_GrantsInConfigured() => AssertRoomLifecycleGranted(LifecycleState.Configured);

    [Fact]
    public Task TryEnter_RoomLifecycle_GrantsInConnecting() => AssertRoomLifecycleGranted(LifecycleState.Connecting);

    [Fact]
    public Task TryEnter_RoomLifecycle_GrantsInConnected() => AssertRoomLifecycleGranted(LifecycleState.Connected);

    private static async Task AssertRoomLifecycleGranted(LifecycleState state)
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(state, SampleConfig());

        using var lease = fsm.TryEnterOperation(OperationKind.RoomLifecycle);
        Assert.True(lease.Permitted);
    }

    [Fact]
    public Task TryEnter_RoomLifecycle_RejectsInCreated() => AssertRoomLifecycleRejected(LifecycleState.Created);

    [Fact]
    public Task TryEnter_RoomLifecycle_RejectsInDisconnecting() => AssertRoomLifecycleRejected(LifecycleState.Disconnecting);

    [Fact]
    public Task TryEnter_RoomLifecycle_RejectsInDisposing() => AssertRoomLifecycleRejected(LifecycleState.Disposing);

    private static async Task AssertRoomLifecycleRejected(LifecycleState state)
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(state, SampleConfig());

        using var lease = fsm.TryEnterOperation(OperationKind.RoomLifecycle);
        Assert.False(lease.Permitted);
    }

    [Fact]
    public async Task TryEnter_AfterDispose_Rejects()
    {
        var fsm = new DiscordProviderLifecycle();
        await using (await fsm.BeginDisposeAsync()) { }

        using var send = fsm.TryEnterOperation(OperationKind.Send);
        using var input = fsm.TryEnterOperation(OperationKind.RequestInput);
        using var room = fsm.TryEnterOperation(OperationKind.RoomLifecycle);
        Assert.False(send.Permitted);
        Assert.False(input.Permitted);
        Assert.False(room.Permitted);
    }

    [Fact]
    public async Task TryEnter_DuringDisconnecting_RejectsAllKinds()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());

        var disconnectLease = await fsm.BeginDisconnectAsync(CancellationToken.None);

        using (var send = fsm.TryEnterOperation(OperationKind.Send))
        using (var input = fsm.TryEnterOperation(OperationKind.RequestInput))
        using (var room = fsm.TryEnterOperation(OperationKind.RoomLifecycle))
        {
            Assert.False(send.Permitted);
            Assert.False(input.Permitted);
            Assert.False(room.Permitted);
        }

        await disconnectLease.DisposeAsync();
    }

    // ===== OperationLease semantics =====

    [Fact]
    public async Task OperationLease_DisposeIsIdempotent()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());

        var lease = fsm.TryEnterOperation(OperationKind.Send);
        Assert.Equal(1, fsm.InFlightCount);

        lease.Dispose();
        Assert.Equal(0, fsm.InFlightCount);

        // Repeated dispose must not over-decrement.
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, fsm.InFlightCount);
    }

    [Fact]
    public void OperationLease_RejectedDispose_IsSafe()
    {
        var fsm = new DiscordProviderLifecycle();
        var lease = fsm.TryEnterOperation(OperationKind.Send); // Created → rejected
        Assert.False(lease.Permitted);

        // Disposing a rejected lease must be a no-op.
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, fsm.InFlightCount);
    }

    // ===== Lock release on exception =====

    [Fact]
    public async Task BeginConnectAsync_ReleasesLock_OnInvalidStateException()
    {
        var fsm = new DiscordProviderLifecycle();
        // Created — Connect throws InvalidOperation.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fsm.BeginConnectAsync(CancellationToken.None));

        // If lock was leaked, the next Configure would hang.
        var configureTask = fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        var done = await Task.WhenAny(configureTask, Task.Delay(2000));
        Assert.Same(configureTask, done);
        await configureTask;
    }

    [Fact]
    public async Task BeginDisconnectAsync_AfterDispose_ReturnsNoOpAndDoesNotDeadlock_DecisionC()
    {
        var fsm = new DiscordProviderLifecycle();
        await using (await fsm.BeginDisposeAsync()) { }

        // Decision C: Disconnect from Disposed is idempotent no-op, never throws.
        await using (var lease = await fsm.BeginDisconnectAsync(CancellationToken.None))
        {
            Assert.False(lease.NeedsTeardown);
        }

        // State is still Disposed; subsequent ops are still rejected.
        using var op = fsm.TryEnterOperation(OperationKind.Send);
        Assert.False(op.Permitted);
    }

    // ===== Drain semantics =====

    [Fact]
    public async Task WaitForDrainAsync_ReturnsTrue_WhenNoOpsInFlight()
    {
        var fsm = new DiscordProviderLifecycle();
        var drained = await fsm.WaitForDrainAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.True(drained);
    }

    [Fact]
    public async Task WaitForDrainAsync_ReturnsFalse_OnTimeout()
    {
        var fsm = new DiscordProviderLifecycle();
        await fsm.ConfigureAsync(SampleConfig(), CancellationToken.None);
        fsm.ForceStateForTesting(LifecycleState.Connected, SampleConfig());

        var holdLease = fsm.TryEnterOperation(OperationKind.Send);
        Assert.True(holdLease.Permitted);

        try
        {
            var drained = await fsm.WaitForDrainAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.False(drained);
        }
        finally
        {
            holdLease.Dispose();
        }
    }
}
