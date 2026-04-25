# Discord Notification Provider — Lifecycle Refactor: Design Doc

**Status**: PROPOSAL — awaiting human triage. Do not start coding.
**Backlog source**: `roadmap.md` Proposed Additions — "Refactor candidate: `DiscordNotificationProvider.cs`" (surfaced 2026-04-25 by stabilization gate).
**Risk if implemented**: 🔴 (concurrency-sensitive; central to the only operational notification path; touches dispose semantics).
**Author**: anvil (operator: agent-academy), 2026-04-25.

> Per the project rule, items in **Proposed Additions** are not active roadmap work. This document exists so a human has a concrete proposal to triage rather than an abstract refactor request. Implementation does NOT begin until the human moves this item up the roadmap.

---

## 1. Problem statement

`DiscordNotificationProvider.cs` is 540 lines. **14 fix-or-refactor commits in the last 30 days, 4 of them lifecycle/concurrency fixes in the last 14 days**:

| Commit | Date | Subject | Failure mode it fixed |
|---|---|---|---|
| `e7302d2` | 14d ago | fix: harden Discord provider connect/dispose lifecycle (#114) | Race between `ConnectAsync` post-init failure and concurrent dispose leaving an orphaned client + handlers attached. |
| `40885cc` | 14d ago | fix: serialize Discord provider Configure/Connect on `_config` access (#115) | `ConnectAsync` could read a `_config` snapshot that `ConfigureAsync` was rewriting, connecting with stale `BotToken` while addressing messages to the new `GuildId`. |
| `a0b2a9c` | 14d ago | fix: drain Discord outbound ops before client teardown (#116) | `DisposeAsync`/`DisconnectAsync` could tear down the underlying client while a `SendNotificationAsync` was mid-call, producing `ObjectDisposedException` on a different thread. |
| `2f83574` | 14d ago | fix: align forge execution with copilot sdk auth + stabilization (#112) | Adjacent — forced revisiting of provider lifecycle assumptions. |

Each fix was correct in isolation. The pattern is the **structural** problem: the provider's lifecycle is encoded as a Cartesian product of independent flags rather than as an explicit state machine. Every new lifecycle invariant has to re-prove correctness against every other flag's transitions, by hand, in code review.

**This refactor is preventative, not corrective.** No bug is open against the file right now. The hypothesis is that the next lifecycle bug will land within ~14 days at the current rate, and that an explicit state machine reduces the surface area where such bugs can be introduced.

---

## 2. What's already been factored out (don't redo this)

A lot. The current provider is *not* a god class — it's a thin coordinator over collaborators that each own one piece. Recent refactors have already extracted:

| Collaborator | Owns | Source |
|---|---|---|
| `DiscordConnectionManager` (`IDiscordConnectionManager`) | Raw `DiscordSocketClient` lifecycle: login, ready handshake, disconnect, dispose. | `DiscordConnectionManager.cs:23–206` |
| `DiscordChannelManager` | Channel + category infrastructure, room→channel mapping, rebuild on reconnect. | `DiscordChannelManager.cs` |
| `DiscordMessageSender` | Outbound message construction + delivery (room channel, default channel, agent-question thread, DM). | `DiscordMessageSender.cs:1–256` |
| `DiscordMessageRouter` | Inbound `MessageReceived` event routing → input handler / human messages. | `DiscordMessageRouter.cs:1–130` |
| `DiscordInputHandler` | Interactive choice + freeform input collection. | `DiscordInputHandler.cs` |
| `DiscordProviderConfig` | Validated config value object. | `DiscordProviderConfig.cs` |
| `OperationDrainTracker` | In-flight outbound op accounting + drain-on-teardown. | `OperationDrainTracker.cs:37–132` |

**What remains in `DiscordNotificationProvider.cs` is exactly the layer that's been buggy**: the orchestration of `_disposed`, `_config`, `_connection.IsConnected`, and `_drainTracker.IsTeardownInProgress` across `ConfigureAsync`/`ConnectAsync`/`DisconnectAsync`/`DisposeAsync` plus every public outbound operation that has to consult those flags before doing work.

**Reuse opportunity, not extraction.** The collaborators above are correct and stable. The refactor must NOT regress them.

---

## 3. The implicit state machine (today)

The provider's lifecycle is currently encoded as four independent fields read in combinations:

```text
_disposed:                   {0, 1}                (Volatile.Read / Interlocked.Exchange)
_config:                     {null, DiscordProviderConfig}  (volatile)
_connection.IsConnected:     {false, true}         (delegated to connection manager)
_drainTracker.IsTeardownInProgress: {false, true}  (volatile in the tracker)
```

That is a Cartesian product of **2 × 2 × 2 × 2 = 16 reachable combinations**, of which only ~6 are legal:

| State | `_disposed` | `_config` | `_connection.IsConnected` | `_drainTracker.IsTeardownInProgress` | Public-API behaviour |
|---|---|---|---|---|---|
| `Created` | 0 | null | false | false | Configure OK; Connect throws "must be configured"; Send returns false; Dispose OK. |
| `Configured` | 0 | set | false | false | Configure OK (rewrites); Connect OK; Send returns false (not connected); Dispose OK. |
| `Connecting`† | 0 | set | false | false | (transient — held under `_connectLock`). |
| `Connected` | 0 | set | true | false | Configure OK (rewrites; current connection NOT torn down — see §6 open question A); Connect is a no-op; Send works; Disconnect OK; Dispose OK. |
| `Disconnecting`† | 0 | set | varies | true | (transient — held under `_connectLock`). New ops rejected, drain in progress. |
| `Disposed` | 1 | set | false | true (sticky) | All ops throw `ObjectDisposedException` or return false. Terminal. |

†Not real states — they exist only between `_connectLock.WaitAsync` and the field write that exits them.

The other ~10 combinations are *unreachable by construction* but only because every lifecycle method orders its writes carefully. Each lifecycle fix in §1 was a case where one of those orderings was wrong and produced an illegal-but-temporarily-reachable state.

**The risk this refactor addresses**: the legality of states is enforced by hand, in distributed code, with no central place that says "these are the valid states" or "these are the legal transitions." The reviewer's job is to mentally re-derive the table above for every change.

---

## 4. Proposed direction

**Extract a `DiscordProviderLifecycle` finite-state machine** that owns the legal states and transitions. The provider becomes a thin adapter that:

1. Asks the FSM for permission to perform an operation ("can I send right now?").
2. Performs the operation against its collaborators.
3. Reports outcomes back to the FSM ("connect succeeded" / "connect failed").

The provider's public surface (`INotificationProvider`) does not change. The FSM is an *internal* abstraction.

### 4.1 States (explicit)

```csharp
internal enum LifecycleState
{
    Created,         // No config. No client. No drain.
    Configured,      // Config present. No client.
    Connecting,      // Config present. Client being created. _connectLock held.
    Connected,       // Config present. Client live. Hooks attached.
    Disconnecting,   // Drain in flight. Client about to be torn down. Recoverable.
    Disposing,       // Drain in flight. Client about to be torn down. Terminal.
    Disposed         // Terminal. All ops rejected.
}
```

Note: `Configured` and the post-disconnect "config present, client gone" state collapse to the same node. `Disconnecting → Configured` is the recoverable path (the same model that `_drainTracker.EndTeardown()` already supports).

### 4.2 Legal transitions (explicit)

```text
Created       --Configure-->        Configured
Configured    --Configure-->        Configured           (rewrite; preserves OwnerId)
Configured    --Connect-->          Connecting --ok-->   Connected
Connecting    --connect-failed-->   Configured           (rolled back; client disposed)
Connected     --Configure-->        Configured           (see §6 open question A)
Connected     --Disconnect-->       Disconnecting --done-> Configured
Connected     --Send-->             Connected            (drain-tracked)
*             --Dispose-->          Disposing --done-->  Disposed   (terminal from anywhere)
```

Any other transition raises `InvalidOperationException` ("not legal in state X") — observable, not just-fail-silently — and the test suite asserts that **every illegal transition** produces this exception. That's the safety net that today's distributed checks don't have.

### 4.3 Operation gates (explicit)

```csharp
internal enum OperationKind { Send, RequestInput, RoomLifecycle }

// Returned by lifecycle.TryEnter(OperationKind).
internal readonly record struct OperationLease(bool Permitted, string? RejectionReason)
    : IDisposable;
```

`TryEnter`:
- `Send` / `RequestInput`: requires `Connected`, increments drain counter, returns lease.
- `RoomLifecycle` (rename, close): requires `Configured` (matches today's "configured, not necessarily connected" path in `ExecuteWithConfiguredGuildAsync`).
- All ops: rejected if state is `Disconnecting` / `Disposing` / `Disposed`.
- Lease's `Dispose` decrements the drain counter — replaces today's `try/finally + _drainTracker.Leave()` boilerplate at every call site.

This collapses `TryEnterOperation` + `_drainTracker.Leave` + `GetGuildIfConnected` + post-`TryEnter` re-checks into one structured object per op.

### 4.4 Locking discipline (consolidated)

The FSM owns the only `SemaphoreSlim`. State *reads* are lock-free (a single `volatile LifecycleState`). State *transitions* take the lock, validate, write, release. This matches today's design but moves the lock into the FSM where invariants live, instead of leaving it in the provider where it has to coordinate with three other fields.

### 4.5 What the provider becomes

Public methods reduce to: take a lease, call the collaborator, dispose the lease. The 540-line file shrinks to roughly the public-API surface (~150 lines) plus a `DiscordProviderLifecycle.cs` (~250 lines including the transition table and tests' internal seams). Every fix from §1 maps to a single FSM-level invariant rather than four ad-hoc field-ordering rules.

---

## 5. What stays out of scope

- **No change to `INotificationProvider` contract.** Public API identical.
- **No change to `DiscordConnectionManager`, `DiscordChannelManager`, `DiscordMessageSender`, `DiscordMessageRouter`, `DiscordInputHandler`.** All collaborators are already extracted, tested, and stable.
- **No change to `OperationDrainTracker`.** The lease pattern (§4.3) is a thin wrapper around it; the tracker's contract doesn't move.
- **No change to `DiscordProviderConfig`.** The OwnerId-preservation rule (current `ConfigureAsync` body) becomes a single transition action in the FSM.
- **No change to Discord wire behaviour.** A reconnect under this design produces exactly the same packet sequence to Discord as today.
- **`SlackNotificationProvider.cs` is NOT in scope.** It has its own structure and a fraction of the fix history. If the FSM proves valuable, propose a parallel refactor — don't generalize prematurely.

---

## 6. Open design questions (must be resolved before implementation)

**Question A — Reconfigure-while-Connected semantics.** Today `ConfigureAsync` while `Connected` rewrites `_config` but does NOT tear down the live client, even if the new config has a different `BotToken` or `GuildId`. This is silently wrong: subsequent sends will use the new `GuildId` against a client logged in with the old token. The current behaviour is undocumented in `INotificationProvider`. Options:

  - **A1**: Reject `Configure` while `Connected` with a clear exception. Caller must Disconnect first. Loudest, safest.
  - **A2**: If the new config differs in `BotToken`, transparently `Disconnect → Configured → Connect` inside `ConfigureAsync`. Convenient but hides a network operation behind a config call.
  - **A3**: Same as today (silent rewrite), document the hazard, add a `RequiresReconnect` boolean to the diff. Backward-compatible.

  **Recommendation**: A1. The Configure-while-Connected path is not exercised by today's UI flow (Configure happens once at startup or via the settings page; the settings page Disconnects first). Making it loud now prevents the next bug.

**Question B — Lease cancellation token plumbing.** Today the various `Send*Async` methods accept a `CancellationToken` and pass it down. With the lease pattern, should the lease itself be cancellable (so a long-running operation can observe a teardown that's *waiting* on its drain)? Today the answer is no — teardown waits up to `DrainTimeout` (5s) and then proceeds; in-flight ops complete or get `ObjectDisposedException`. Keeping that contract is simpler. Recommendation: leases are not cancellable; teardown semantics unchanged.

**Question C — `DisconnectAsync` while `Disconnecting`.** Today a second concurrent `DisconnectAsync` blocks on `_connectLock` and then no-ops because the client is already gone. With explicit states, should it throw or return idempotently? Recommendation: idempotent return (today's behaviour). Throwing breaks reasonable retry patterns.

**Question D — `Connected` → `Connecting` (reconnect after disconnect detected).** Today the connection manager fires `Disconnected` events but the provider does NOT auto-reconnect. The FSM as drafted has no `AutoReconnect` transition. Should it? Recommendation: out of scope for this refactor. Current product behaviour is "manual reconnect via the UI's Connect button"; auto-reconnect is a separate feature.

**Question E — `IDiscordProviderLifecycle` interface seam for tests.** Today's tests use `IDiscordConnectionManager` to inject a fake. Should the lifecycle FSM be similarly behind an interface? Recommendation: NO. The FSM is internal coordination — tests should drive the public `DiscordNotificationProvider` surface and assert observable behaviour, not poke at the FSM directly. Make the FSM `internal sealed`, use `InternalsVisibleTo` for the test assembly only if a state-transition unit test is genuinely valuable.

---

## 7. Verification plan (when this work is approved)

The implementation must produce all of the following:

1. **A test matrix** asserting every transition in §4.2 is legal and every other transition raises `InvalidOperationException`. This is the central artifact — the safety net we don't have today.
2. **Every existing test in `DiscordNotificationProviderTests.cs` (415 lines) and `DiscordNotificationProviderConcurrencyTests.cs` (492 lines) continues to pass unchanged.** This proves the public contract is preserved.
3. **A regression test reproducing each of the 4 fixes from §1**, run against both the old and new implementations during PR review (the old is the baseline; the new must not regress).
4. **Three adversarial reviewers** at the PR (codex + gpt-5.5 + opus-4.6) per the Anvil Large-task gate. The hot-spot history justifies the cost.
5. **Manual smoke**: bring up a real Discord bot, run Configure → Connect → Send → Disconnect → Connect → Send → Dispose. Watch logs for stray `ObjectDisposedException` or `Discord client disconnected` outside the disconnect path.

---

## 8. Cost vs. benefit

**Cost**:
- ~1 day of design implementation.
- ~1 day of test refactoring (the existing concurrency tests are intricate; some will need re-expression against the new seams).
- One PR with ~600 lines of net change (extraction + tests).
- Adversarial-review burn.

**Benefit, if the hypothesis holds**:
- The next lifecycle bug (statistically due in ~14 days) is caught at PR time by the §7.1 transition matrix.
- Reviewers no longer have to re-derive the legal-state table for every change.
- New collaborator changes (e.g., adding a 6th outbound op) become localized — they consult the FSM for permission rather than each re-implementing the disposed/configured/connected dance.

**Benefit, if the hypothesis is wrong** (no more lifecycle bugs were coming): the refactor is mostly cosmetic. Code is clearer; nothing operationally improves. This is the failure mode the human should weigh against the cost.

---

## 9. Recommended decision path

1. Human reads §1 (failure history) and §3 (state machine table).
2. Human decides whether the hypothesis "lifecycle bug rate is structural, not coincidental" is credible.
3. If yes → answer §6 questions (A is the only one with a real choice; B–E have clear recommendations) → move item from Proposed Additions to active roadmap (e.g., as `P2.x — Discord lifecycle refactor`).
4. If no → close this doc with a "deferred" note in the Proposed Additions entry; revisit after the next lifecycle bug.

Either decision is fine. **Doing nothing is a valid response**; the rule the project enforces is "no silent work on Proposed Additions," not "every Proposed Addition must ship."
