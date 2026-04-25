# Cost Tracking & Token-Cap Guard: Design Doc

**Status**: DRAFT — pending human review before implementation.
**Roadmap items**: P1.2 §4.6 (cost-cap insertion point) + P1.4 §10 (cost tracking deferred). This doc is the deferred design surface both items reserved.
**Closes gap**: G1 (runaway-cost potential identified by roadmap §P1.2 critical safety requirement: "max-cost-per-sprint cap if API costs are tracked").
**Risk**: 🟡 (additive instrumentation + one new guard call site; no new halt primitive, no new schema for the recording side).

This doc is the deferred design preamble for the per-sprint cost cap. Read this before implementing the cost guard.

---

## 1. Problem statement

Once P1.2 (Self-Drive) lands, the orchestrator will autonomously enqueue continuations until a halt condition trips. P1.2 ships **round-count caps** (`MaxRoundsPerSprint`, `MaxRoundsPerStage`, `MaxConsecutiveSelfDriveContinuations`) but **explicitly defers cost caps**, citing "token/cost tracking does not exist yet" (P1.2 §3.2 / §4.6).

That premise is **stale**. Cost and token tracking already exist:

- **`LlmUsageTracker`** (`Services/LlmUsageTracker.cs`, 305 LOC) records every Copilot SDK call: `AgentId`, `RoomId`, `Model`, `InputTokens`, `OutputTokens`, `CacheReadTokens`, `CacheWriteTokens`, `Cost` (USD), `DurationMs`, `ApiCallId`, `Initiator`, `ReasoningEffort`, `RecordedAt`. Backed by the `llm_usage` table (`LlmUsageEntity`).
- **`AgentQuotaService`** (`Services/AgentQuotaService.cs`, 246 LOC) already enforces per-agent `MaxRequestsPerHour` / `MaxTokensPerHour` / `MaxCostPerHour` quotas using `ILlmUsageTracker.GetAgentUsageSinceAsync`. The "read totals → compare to cap → halt" pattern is implemented and battle-tested for the per-agent scope.
- The single recording call site is `CopilotSdkSender.SendAsync` (`CopilotSdkSender.cs:180`), which fires `_usageTracker.RecordAsync(agentId, roomId, ...)` after every SDK turn.

What's missing is **sprint-scoped enforcement**: per-agent quotas don't prevent a single sprint from accidentally burning $200 across five agents. This doc designs that layer, reusing both existing primitives.

The shape stays exactly as P1.2 §4.6 reserved: an `ICostGuard.ShouldHaltAsync(sprint, ct)` hook called once per self-drive continuation, returning `true` when the cap is exceeded, and triggering `MarkSprintBlockedAsync(sprintId, "Cost cap reached")`.

---

## 2. Design principles (informed by what's already in the codebase)

These constrain every decision below.

1. **Reuse `LlmUsageTracker`, do not parallel-record.** All cost/token data already flows through one chokepoint (`CopilotSdkSender.SendAsync`). The guard reads from the same table the tracker writes to. No new recording path. No double accounting risk.
2. **Reuse the `AgentQuotaService` enforcement pattern.** It already follows the right shape: cached config, time-windowed `GetAgentUsageSinceAsync`, decision returns "deny + reason," exception (or for sprint-scope: block). The sprint guard is a structural cousin, not a novel design.
3. **Halt mechanism reuses `MarkSprintBlockedAsync`.** P1.4-narrow shipped the atomic block primitive. Cap-exceeded is the same operation with `BlockReason = "Cost cap reached: $X.XX / $Y.YY"`. Discord NeedsInput notification ships for free via the existing `ActivityEventType.SprintBlocked` mapping (P1.7).
4. **Counters live on `SprintEntity`, but only the *caps* — not the *running total*.** Computing the running total on demand (sum from `llm_usage`) is cheap (indexed `RecordedAt + RoomId` query), avoids drift between cached and authoritative values, and survives crashes without reconstruction logic. Only the **per-sprint override caps** need to be persisted.
5. **The hook P1.2 §4.6 reserved is sacrosanct.** P1.2 designed exactly one hook for cost-cap enforcement: in `SelfDriveDecisionService`, before enqueueing the continuation. The guard plugs into that exact location with no restructuring. We do *not* add a guard call inside `CopilotSdkSender` — that would block individual SDK calls mid-sprint, which is too granular (an agent's reply could be cut off mid-message) and bypasses the existing block-aware queue (principle 7 of P1.2).
6. **Default is `NoOpCostGuard` returning `false`.** The interface ships with both implementations from day one, but the default DI binding is the no-op until an operator explicitly enables cost caps in `appsettings.json`. This keeps cost enforcement opt-in (no surprise blocks for users running locally with a Copilot subscription where cost is irrelevant).
7. **Sprint scoping is by `SprintId` stamped at write time.** `LlmUsageEntity` gets a new nullable `SprintId` column (additive migration). At the single recording call site (`CopilotSdkSender.SendAsync` line 180), the sprint id is resolved from the active `ConversationSessionEntity` for that room (`ConversationSessionEntity.SprintId` already exists for exactly this scoping purpose) and stamped on the row. Cost-for-sprint = `SUM(Cost) WHERE SprintId = X`. This handles breakout rooms (their session also carries `SprintId`), avoids the cross-sprint window pollution that time-window scoping suffers from when one sprint completes and the next starts in the same workspace, and survives EF query plan changes. **§3.3 details the rejected alternative** (time-window via `RoomId ∩ workspace ∩ [sprint.CreatedAt, sprint.CompletedAt]`) and why it was wrong.
8. **The guard surfaces a decision record, not a bool.** `ICostGuard.EvaluateAsync(sprint, ct)` returns `CostGuardDecision { ShouldHalt, HaltReason, RunningCostUsd, RunningTokens, EffectiveCostCapUsd, EffectiveTokenCap, WarningEmitted }`. The caller (`SelfDriveDecisionService`) uses `decision.HaltReason` directly when calling `MarkSprintBlockedAsync` so the running totals appear in the block reason and in the SprintBlocked event payload.
9. **Fail-closed in production for guard read errors.** A persistent failure in the aggregation query is the exact failure mode this guard is supposed to catch: if we cannot determine whether the cap is exceeded, the safe default for an unattended autonomous loop is to halt and let a human review. Local-dev can opt into fail-open via `CostGuardOptions.FailOpenOnError = true` (default `false`).
10. **Cap exceedance is a `BlockedAt` halt, not a sprint cancellation.** The work isn't *bad*; the budget needs human review. `MarkSprintBlockedAsync` keeps `Status = "Active"` so a human's `POST /api/sprints/{id}/raise-cost-cap` (or plain `/unblock`) immediately resumes self-drive. Cancelling would lose the sprint's progress.
11. **Race rule: re-read sprint state, do not cache it.** A self-drive decision call that observes `BlockedAt == null` and a passed cost guard MUST re-read the sprint state immediately before enqueueing the continuation, to avoid racing against a concurrent block (cost guard from another decision call, P1.4-full self-eval block, operator emergency stop, or sprint timeout sweep). The continuation dispatcher (`SystemContinuation` queue item processor) MUST also re-resolve the sprint and drop the item if `BlockedAt != null`, `Status != "Active"`, or `AwaitingSignOff`. This is principle 7 of P1.2 made explicit for the cost-cap path.

---

## 3. State model additions

### 3.1 Schema additions (additive — all 🟢)

**`SprintEntity` (new columns)** — for per-sprint overrides + persisted warning flag:

```csharp
// Cost/token caps (this design). Both nullable: null means "use config default."
// Set at sprint creation OR via PATCH on the sprint resource.
public decimal? MaxCostUsdOverride { get; set; }
public long?    MaxTokensOverride  { get; set; }

// Persisted "warned at 80% threshold" timestamp. Cleared on UnblockSprintAsync
// when block reason was "Cost cap reached" (humans raising the cap deserve
// a fresh warning when usage crosses the new threshold). Persisted, not
// in-memory: survives restart and is visible to GET /api/sprints/{id}/cost.
public DateTime? CostWarnEmittedAt { get; set; }
```

**`LlmUsageEntity` (new column)** — for sprint-scoped aggregation:

```csharp
// Sprint that owned the conversation when this LLM call recorded.
// Resolved at write time via the active ConversationSessionEntity for the room.
// Nullable for legacy/orphan records (DM threads, agent-mod activity outside a sprint).
public string? SprintId { get; set; }
```

Migration is **additive** — no backfill required:

- `SprintEntity.MaxCostUsdOverride` / `MaxTokensOverride` / `CostWarnEmittedAt` default to `NULL`.
- `LlmUsageEntity.SprintId` defaults to `NULL` for pre-migration rows. New writes stamp it via `ConversationSessionEntity.SprintId` (which is already populated for sprint-attached rooms — see `ConversationSessionEntity.cs:23`).

We do **not** add `RunningCostUsd` / `RunningTokens` columns on `SprintEntity`. The running total is computed from `llm_usage` on the same query that powers the guard decision (see §4). This avoids drift between the tracker's authoritative table and a cached counter.

### 3.2 Caps (configurable, with safe defaults)

Read from `appsettings.json` under a new `Orchestrator:CostGuard` section, with hardcoded fallbacks if missing:

| Cap                          | Default | Per-sprint override field           | Rationale |
|------------------------------|---------|-------------------------------------|-----------|
| `MaxCostUsdPerSprint`        | `5.00`  | `SprintEntity.MaxCostUsdOverride?`  | Conservative default; a normal Phase 1 sprint completes under $1. Five-dollar ceiling catches a runaway loop within a few continuations. |
| `MaxTokensPerSprint`         | `2_000_000` | `SprintEntity.MaxTokensOverride?` | Backstop for tracking gaps where SDK cost is null but token counts are present. ~2M tokens ≈ 50 typical agent rounds at current model usage. |
| `Enabled`                    | `false` | —                                   | Default-off (principle 6). Explicit opt-in via `appsettings.json` flips to `CostGuard` from `NoOpCostGuard`. |
| `WarnAtPercent`              | `80`    | —                                   | Below halt threshold, broadcast a Progress notification once per sprint when usage crosses 80%. The "once per sprint" guarantee is durable — backed by `SprintEntity.CostWarnEmittedAt`, not in-memory state. |
| `FailOpenOnError`            | `false` | —                                   | If the aggregation query throws (DB connection drop, query plan failure, migration mismatch), default behavior is to halt the sprint with reason `"Cost guard unavailable; human review required"` and re-broadcast `SprintBlocked`. Setting `true` reverts to fail-open (continuation proceeds despite the error) — only appropriate for local-dev where the cap is informational. |

Reviewer asks (§7) cover whether `Enabled` should be ON in production deployments by default, and whether the warn-at threshold should fire multiple times (e.g., 50%, 80%, 95%).

### 3.3 Why **time-window** scoping was considered and rejected (in favour of `LlmUsageEntity.SprintId`)

An earlier draft of this design used "time window ∩ rooms-in-workspace" scoping to avoid a schema migration on the high-traffic `llm_usage` table. Adversarial review found two correctness bugs:

1. **Breakout-room undercount.** Breakout LLM calls pass `breakoutRoomId` as the usage `RoomId`, but breakout rooms live in `BreakoutRooms`, not `Rooms` (see `BreakoutLifecycleService.cs:217`, `BreakoutRoomEntity.cs:17-31`). A `Where(r => r.WorkspacePath == sprint.WorkspacePath)` over `db.Rooms` would have missed every breakout call — a sprint that does most of its work in breakout implementation loops would pass the cap check while burning cost outside the guard.
2. **Cross-sprint overcount.** `LlmUsageTracker.RecordAsync` sets `RecordedAt = DateTime.UtcNow` at persistence time, which is *after* the SDK call returns. A previous sprint's in-flight SDK call that records usage after the next sprint's `CreatedAt` would land in the new sprint's window, charging cost from the prior sprint to the next one.

Both bugs vanish if usage records carry the `SprintId` they were generated under. `ConversationSessionEntity.SprintId` already exists for exactly this scoping purpose (`ConversationSessionEntity.cs:23`) — it is set whenever a session is opened in a sprint-attached room (main or breakout). The recording call site in `CopilotSdkSender.SendAsync` already has `roomId` in scope; resolving the active session's `SprintId` is one indexed lookup. The migration is additive (one nullable column on `llm_usage`, default `NULL` for pre-migration rows).

**Pre-migration rows have `SprintId = NULL`.** The guard query is `WHERE SprintId = X` — it correctly excludes those rows. Cost computed for sprints that ran before this feature lands is therefore $0 by construction, which is acceptable: the guard only matters for sprints that begin under the new code path.

The cost of being wrong on this choice is *runaway-cost potential* — the exact thing the cap is supposed to prevent. The cost of the migration is one column. We take the column.

---

## 4. Control-flow design

### 4.1 The decision point

The guard plugs into the hook P1.2 §4.6 already reserved, with **no other call sites**:

```csharp
// In SelfDriveDecisionService.DecideAndMaybeEnqueueAsync, step 12 (CONTINUE):
var decision = await _costGuard.EvaluateAsync(sprint, cancellationToken);

if (decision.ShouldHalt)
{
    // Block primitive returns (sprint, true) iff this call atomically transitioned
    // BlockedAt: NULL → now. Only the winning caller broadcasts the event.
    await _sprintService.MarkSprintBlockedAsync(sprint.Id, decision.HaltReason!);
    return; // do NOT enqueue continuation
}

// Race rule (principle 11): re-read sprint state immediately before enqueueing.
// Another caller (timeout sweep, self-eval block, operator emergency stop, or
// even another concurrent decision call that lost the cost-cap race) may have
// blocked the sprint between our state load and now.
var refreshed = await _sprintService.GetSprintByIdAsync(sprint.Id);
if (refreshed is null
    || refreshed.Status != "Active"
    || refreshed.BlockedAt != null
    || refreshed.AwaitingSignOff)
{
    return; // someone else halted us; honor it
}

// ... existing P1.2 continuation enqueue logic
```

The continuation dispatcher (the consumer of `SystemContinuation` queue items) re-reads the sprint state on its side too — see §4.4.

### 4.2 The interface

```csharp
public interface ICostGuard
{
    Task<CostGuardDecision> EvaluateAsync(SprintEntity sprint, CancellationToken ct);
    void ClearWarnedFlag(string sprintId); // called by UnblockSprintAsync (§4.3)
}

public sealed record CostGuardDecision(
    bool      ShouldHalt,
    string?   HaltReason,            // non-null iff ShouldHalt; e.g. "Cost cap reached: $0.83 / $0.50"
    decimal   RunningCostUsd,
    long      RunningTokens,
    decimal   EffectiveCostCapUsd,
    long      EffectiveTokenCap,
    bool      WarningEmitted);       // true if this evaluation crossed the warn-at threshold for the first time
```

The decision record carries the full picture so the caller can construct the block reason and the SprintBlocked event payload without recomputing. `WarningEmitted = true` is the trigger for the caller (or the guard itself, see §4.3) to broadcast the Progress notification.

### 4.3 The guard implementation

```csharp
public sealed class CostGuard : ICostGuard
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<CostGuardOptions> _options;
    private readonly ILogger<CostGuard> _logger;
    private readonly IActivityBus _activityBus;

    public async Task<CostGuardDecision> EvaluateAsync(SprintEntity sprint, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            return new CostGuardDecision(
                ShouldHalt: false, HaltReason: null,
                RunningCostUsd: 0, RunningTokens: 0,
                EffectiveCostCapUsd: 0, EffectiveTokenCap: 0,
                WarningEmitted: false);
        }

        var effectiveCostCap = sprint.MaxCostUsdOverride ?? (decimal)opts.MaxCostUsdPerSprint;
        var effectiveTokenCap = sprint.MaxTokensOverride  ?? opts.MaxTokensPerSprint;

        decimal runningCost;
        long runningTokens;
        bool warnNow = false;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            // Sprint-scoped via SprintId stamped at write time (principle 7, §3.3).
            // This includes breakout-room usage (their session also carries SprintId).
            var usage = await db.LlmUsage
                .Where(u => u.SprintId == sprint.Id)
                .GroupBy(_ => 1)
                .Select(g => new {
                    Cost = g.Sum(u => u.Cost ?? 0),
                    Tokens = g.Sum(u => u.InputTokens + u.OutputTokens)
                })
                .FirstOrDefaultAsync(ct);

            runningCost = (decimal)(usage?.Cost ?? 0);
            runningTokens = usage?.Tokens ?? 0;

            // Persisted "warned at 80%" flag: atomic ExecuteUpdateAsync only sets
            // CostWarnEmittedAt if it is currently NULL. The caller that wins the
            // update (rows-affected == 1) is the one whose decision broadcasts
            // the Progress notification. Subsequent crossings (or restart) see
            // a non-null flag and don't re-warn.
            var costPct = effectiveCostCap > 0 ? runningCost / effectiveCostCap * 100 : 0;
            if (costPct >= opts.WarnAtPercent)
            {
                var rowsUpdated = await db.Sprints
                    .Where(s => s.Id == sprint.Id && s.CostWarnEmittedAt == null)
                    .ExecuteUpdateAsync(setters =>
                        setters.SetProperty(s => s.CostWarnEmittedAt, DateTime.UtcNow),
                        ct);
                warnNow = rowsUpdated == 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Cost guard aggregation failed for sprint {SprintId}; FailOpenOnError={FailOpen}",
                sprint.Id, opts.FailOpenOnError);

            if (opts.FailOpenOnError)
            {
                return new CostGuardDecision(
                    ShouldHalt: false, HaltReason: null,
                    RunningCostUsd: 0, RunningTokens: 0,
                    EffectiveCostCapUsd: effectiveCostCap, EffectiveTokenCap: effectiveTokenCap,
                    WarningEmitted: false);
            }

            // Fail-closed (default for production): halt with explicit reason.
            return new CostGuardDecision(
                ShouldHalt: true,
                HaltReason: "Cost guard unavailable; human review required",
                RunningCostUsd: 0, RunningTokens: 0,
                EffectiveCostCapUsd: effectiveCostCap, EffectiveTokenCap: effectiveTokenCap,
                WarningEmitted: false);
        }

        if (warnNow)
        {
            _activityBus.Broadcast(/* Progress event: cost at WarnAtPercent% of cap */);
        }

        var halt = runningCost >= effectiveCostCap || runningTokens >= effectiveTokenCap;
        var reason = halt
            ? (runningCost >= effectiveCostCap
                ? $"Cost cap reached: ${runningCost:F2} / ${effectiveCostCap:F2}"
                : $"Token cap reached: {runningTokens:N0} / {effectiveTokenCap:N0}")
            : null;

        return new CostGuardDecision(
            halt, reason,
            runningCost, runningTokens,
            effectiveCostCap, effectiveTokenCap,
            warnNow);
    }

    public void ClearWarnedFlag(string sprintId) { /* see §4.5 */ }
}
```

The persisted warn flag (`CostWarnEmittedAt`) replaces the in-memory `ConcurrentDictionary` from the earlier draft. Adversarial review caught that the in-memory approach contradicted the API field `warnedThisSprint`: a process restart would lose the flag and re-warn on the next decision call, but `GET /cost` would have reported `warnedThisSprint = false` after restart even if the warning had already fired. Persisting it as a sprint column makes the API field accurate, survives restart, and is correct under any future HA deployment.

### 4.4 Continuation dispatch must also re-read sprint state

The race rule in principle 11 has two halves. The first (re-read before enqueue) is in §4.1. The second is here: when the queue worker dequeues a `SystemContinuation` item, before invoking the round runner it must re-resolve the sprint and drop the item if `BlockedAt != null`, `Status != "Active"`, or `AwaitingSignOff`. This protects against:

- a continuation enqueued microseconds before another caller blocked the sprint;
- a continuation surviving server restart while the sprint state changed during downtime;
- the enqueue-then-block window between `MarkSprintBlockedAsync` returning success and `SprintBlocked` notifications being processed.

The check is identical to the one P1.4-narrow already added to `ConversationRoundRunner` for blocked sprints. Self-drive continuations reuse it.

### 4.5 Unblock recovery — clearing the warn flag

P1.4-narrow's `UnblockSprintAsync` already resets `BlockedAt`/`BlockReason`. P1.4-full extends it to reset `SelfEvalAttempts`. This design extends it once more — but **only when the prior block reason was a cost-cap halt**:

```csharp
// In SprintService.UnblockSprintAsync, after the existing ExecuteUpdateAsync block:
if (preState.BlockReason?.StartsWith("Cost cap reached") == true
    || preState.BlockReason?.StartsWith("Token cap reached") == true)
{
    await _db.Sprints
        .Where(s => s.Id == sprintId && s.BlockedAt == null) // re-check, defensive
        .ExecuteUpdateAsync(setters =>
            setters.SetProperty(s => s.CostWarnEmittedAt, (DateTime?)null));
}
```

Effect: a human raising the cap and unblocking gets a fresh warn at the new 80% threshold. If the block reason was something else (a self-eval block from P1.4-full, an operator emergency stop), the warn flag is preserved — the cap didn't change, so the warning still applies.

### 4.6 The cap-raise path (human intervention)

`POST /api/sprints/{id}/raise-cost-cap` is the operator path for "the agent burned its budget but the work is good — give it more." It has two effects that must be all-or-nothing:

1. Persist the new override(s) on `SprintEntity`.
2. (If `autoUnblock = true` AND current `BlockReason` starts with `"Cost cap reached"` or `"Token cap reached"`) clear `BlockedAt`/`BlockReason`/`CostWarnEmittedAt`, broadcast `SprintUnblocked`.

The implementation **must** wrap both halves in an explicit DB transaction (the earlier draft's "transaction-equivalent flow" wording was wrong — EF Core does not implicitly transactionalise across `SaveChangesAsync` boundaries). The endpoint is:

```csharp
public async Task<RaiseCostCapResult> RaiseCostCapAsync(
    string sprintId, decimal? newMaxCostUsd, long? newMaxTokens, bool autoUnblock)
{
    using var tx = await _db.Database.BeginTransactionAsync();

    var sprint = await _db.Sprints.FirstOrDefaultAsync(s => s.Id == sprintId);
    if (sprint is null) return RaiseCostCapResult.NotFound;

    // Step 1: persist override(s).
    if (newMaxCostUsd.HasValue) sprint.MaxCostUsdOverride = newMaxCostUsd;
    if (newMaxTokens.HasValue)  sprint.MaxTokensOverride  = newMaxTokens;

    // Step 2: conditional unblock + clear warn flag, conditional on block reason.
    var didUnblock = false;
    if (autoUnblock
        && sprint.BlockedAt != null
        && (sprint.BlockReason?.StartsWith("Cost cap reached") == true
            || sprint.BlockReason?.StartsWith("Token cap reached") == true))
    {
        sprint.BlockedAt = null;
        sprint.BlockReason = null;
        sprint.CostWarnEmittedAt = null;
        didUnblock = true;
    }

    // Step 3: persist the SprintUnblocked activity event in the same transaction.
    if (didUnblock)
    {
        _db.ActivityEvents.Add(new ActivityEventEntity { /* SprintUnblocked, sprintId */ });
    }

    await _db.SaveChangesAsync();
    await tx.CommitAsync();

    // Broadcast post-commit (best effort — durability of the state change is in the DB).
    if (didUnblock) _activityBus.Broadcast(/* SprintUnblocked */);

    return RaiseCostCapResult.Ok(sprint);
}
```

If the transaction commit fails, both the override persistence AND the unblock half roll back together. The endpoint returns 500; the operator can retry. Partial success ("cap raised, sprint still blocked") is impossible by construction.

If `autoUnblock = true` but the block reason is a non-cost reason (self-eval block, operator emergency stop), the cap raise still happens but the sprint stays blocked. The operator can then call `POST /api/sprints/{id}/unblock` separately. **Reviewer ask (§7 q5)** — confirm this is the right product behaviour.

---

## 5. New API surface

### 5.1 GET `/api/sprints/{id}/cost`

Returns the running cost view for a sprint. Used by the future UI to surface a per-sprint cost meter.

```json
{
  "sprintId": "abc...",
  "runningCostUsd": 0.83,
  "runningTokens": 145222,
  "maxCostUsdEffective": 5.00,
  "maxTokensEffective": 2000000,
  "maxCostUsdOverride": null,
  "maxTokensOverride": null,
  "configEnabled": true,
  "warnAtPercent": 80,
  "warnedAt": "2026-04-25T04:25:00Z",
  "asOf": "2026-04-25T04:31:00Z"
}
```

`warnedAt` reflects `SprintEntity.CostWarnEmittedAt` — durable, accurate across restart and HA. Pure read-only; computed via the same query the guard uses (`WHERE SprintId = X`). Available regardless of `Enabled` flag (so operators can monitor before turning on enforcement).

### 5.2 POST `/api/sprints/{id}/raise-cost-cap`

See §4.5.

### 5.3 No frontend in this design

The UI for the cost meter and the raise-cap button is a follow-on ergonomic task. The endpoints (5.1, 5.2) are shaped for it; bikeshedding the UI here is out of scope. **Reviewer ask (§7 q6)** — should we ship a minimal cost badge in the SprintPanel as part of this work, or strictly keep it backend-only?

---

## 6. Notifications

| Event | Maps to | Surface |
|-------|---------|---------|
| Cost ≥ 80% of cap (one-shot per sprint) | `ActivityEventType.Progress` | Discord room post (Progress channel mapping; no NeedsInput escalation) |
| Cost cap reached → sprint blocked | `ActivityEventType.SprintBlocked` (existing) | Discord NeedsInput "Sprint needs attention" — same surface as P1.4-narrow blocked notifications |
| Cap raised → sprint unblocked | `ActivityEventType.SprintUnblocked` (existing) | Same surface as P1.4-narrow |

Zero new notification types. The warn event reuses `Progress`; the block/unblock events are already wired (P1.7).

---

## 7. Reviewer asks (open questions for human review)

1. **Default `Enabled = false`?** This design ships with cost enforcement OFF until explicitly enabled in `appsettings.json`. Right call for local-dev where Copilot subscription means $0 marginal cost. **Should the autonomous-operator deployment flip it ON by default in `appsettings.Production.json`?** I'd argue yes — the whole point of P1.2 is unattended overnight runs.
2. **Default cap of $5/sprint?** Felt deliberately conservative. Phase 1 acceptance test runs in <$1 historically. Five-dollar ceiling halts a runaway after ~50× the normal sprint cost. Confirm this is right for your tolerance, or pick a different number.
3. **`FailOpenOnError = false` (fail-closed) the right default?** §4.3 implements fail-closed by default in production: if the aggregation query throws, the sprint is halted with reason `"Cost guard unavailable; human review required"`. Adversarial review caught that fail-open silently defeats the cap during the exact failure mode it's meant to control. Local-dev can opt into fail-open. Confirm fail-closed is the right production default.
4. **`raise-cost-cap` auto-unblocks?** §4.6. Default is yes-when-block-reason-matches; requiring two API calls (raise + unblock) is more surgical but more friction. Confirm.
5. **Frontend cost badge in this design's scope?** §5.3. I scoped it as backend-only. If the operator wants visibility before P1.2 lands, a minimal badge in `SprintPanel` is ~40 LOC of frontend work and would help observability. Confirm in or out.
6. **Multi-tier warn thresholds (50% / 80% / 95%)?** I picked one threshold (80%) for simplicity. Multi-tier gives finer-grained operator awareness but more Discord noise. With persisted `CostWarnEmittedAt` as a single column, multi-tier needs either a JSON `EmittedThresholds` column or three columns. Confirm one is enough.
7. **`SprintId` write-time stamping in `CopilotSdkSender`** — the recording call site needs the sprint id for the row. Resolution path: `CopilotSdkSender.SendAsync` already has `roomId`; one DB lookup to `db.ConversationSessions.Where(s => s.RoomId == roomId && s.Status == "Active").Select(s => s.SprintId)` resolves it. Adds one round-trip per SDK call (current path is one INSERT). Cache by room id with a short TTL (the sprint binding rarely changes mid-session)? Or accept the round-trip? **I'd prefer no cache** — caching introduces correctness risk for one DB call on a path that already takes seconds to complete an LLM round.

(Reviewer asks 3 and 7 in the previous draft were merged into the design itself based on adversarial review findings; the remaining questions above are the genuinely open ones.)

---

## 8. Implementation order (after design approval)

This is a small implementation — about half the size of P1.2 or P1.4-full. Sequence:

1. **Schema migration** (additive). `MaxCostUsdOverride` + `MaxTokensOverride` + `CostWarnEmittedAt` on `SprintEntity`; `SprintId` on `LlmUsageEntity`. EF migration only; no backfill.
2. **`SprintId` write-time stamping** in `CopilotSdkSender.SendAsync` (line 180). Lookup `ConversationSessionEntity.SprintId` via `roomId`; pass to `RecordAsync`. Extend `ILlmUsageTracker.RecordAsync` signature with the new `string? sprintId` parameter.
3. **`CostGuardOptions` record + DI binding.** New `Orchestrator:CostGuard` config section, `appsettings.json` defaults (`Enabled = false`, `FailOpenOnError = false`), `AddOptions<CostGuardOptions>().BindConfiguration(...)` in startup.
4. **`ICostGuard` interface + `CostGuardDecision` record + `NoOpCostGuard` impl + `CostGuard` impl.** Singleton DI. Default binding to `NoOpCostGuard` unless `CostGuardOptions.Enabled = true`, then `CostGuard`.
5. **Wire into `SelfDriveDecisionService.DecideAndMaybeEnqueueAsync` step 12.** Single call site. Includes the post-guard re-read of sprint state (§4.1, principle 11).
6. **Continuation dispatch re-read** in the queue worker that consumes `SystemContinuation` items (§4.4). If the sprint is no longer Active / is BlockedAt / is AwaitingSignOff, drop the item.
7. **Extend `SprintService.UnblockSprintAsync`** (§4.5) — when prior block reason matches cost/token cap, clear `CostWarnEmittedAt`.
8. **`SprintService.RaiseCostCapAsync`** (§4.6) — explicit DB transaction wrapping override update + conditional unblock + `SprintUnblocked` event persistence.
9. **`GET /api/sprints/{id}/cost`** controller + DTO (`SprintCostDto`).
10. **`POST /api/sprints/{id}/raise-cost-cap`** controller + DTO + `RaiseCostCapAsync` wiring.
11. **Tests**: see §10 acceptance criteria + unit tests per the cases below.
12. **Acceptance test thread (manual)** — see §10.

This sequencing ships a working halt before any UI surface, which is the right order: enforcement first, observability second.

---

## 9. Out of scope (deferred or non-goals)

- **Per-agent vs. per-sprint reconciliation.** `AgentQuotaService` and `CostGuard` operate on different scopes (per-agent rolling hour vs. per-sprint lifetime). They can both halt independently. We do *not* try to unify them — they answer different questions, and the existing per-agent service is fine as-is.
- **Cost prediction / pre-flight estimates.** Halting on actual usage is the simpler, observable contract. Prompting an operator before a continuation that *might* breach the cap is a much harder design (bin-packing future agent costs is unsolved). Deferred.
- **Dollar-denominated SLA reports / monthly statements.** `LlmUsageTracker.GetGlobalUsageAsync` already does this. UI exposing it is its own task.
- **Cost-aware model selection.** "If we're at 60% of cap, switch to a cheaper model." Tempting but couples scheduling decisions to cost in ways that are hard to reason about. Deferred.
- **A `CostGuard` for non-self-drive paths.** Today only self-drive can run unbounded; human-message-driven rounds are bounded by the human's own pace. If a future feature lets human messages also trigger long autonomous sequences, a guard call site there will be additive — same hook pattern.
- **Frontend.** §5.3.

---

## 10. Acceptance criteria (the moment this lands)

Adversarial review caught that the earlier draft's acceptance test could pass while major production paths were broken (breakouts uncounted, sprint-boundary leak, raise-cap partial failure, restart drift). The acceptance criteria below cover the failure modes the design choices in §3.3 / §4.1 / §4.4 / §4.6 / §3.1 are specifically designed to handle. **Each criterion must pass for the implementation to ship.**

**Setup**: `Orchestrator:CostGuard:Enabled = true`, `MaxCostUsdPerSprint = 0.10` (artificially low to trigger fast), `FailOpenOnError = false`.

### A. Happy path (main-room only)

1. Create a sprint, post a kickoff that triggers a few rounds of agent activity.
2. Within a few rounds, cost crosses 0.10. Self-drive's next decision call sees `EvaluateAsync` return `ShouldHalt = true` with `HaltReason = "Cost cap reached: $0.NN / $0.10"`.
3. `SprintBlocked` event fires; sprint's `BlockedAt` is non-null with the matching reason.
4. Discord NeedsInput notification arrives **exactly once**.
5. `GET /api/sprints/{id}/cost` shows `runningCostUsd ≥ 0.10`, `maxCostUsdEffective = 0.10`, `warnedAt` non-null.

### B. Cap raise + auto-unblock atomicity

6. `POST /api/sprints/{id}/raise-cost-cap { "newMaxCostUsd": 1.00 }` returns 200; sprint is unblocked + warn flag cleared in the same call.
7. Self-drive immediately resumes (next round runs); cost continues to accrue toward the new cap.
8. No second `SprintBlocked` notification fires for the same crossing.

### C. Breakout-room usage MUST contribute to the sprint total

9. Run a sprint where most LLM activity happens in a breakout room (force a task implementation loop).
10. Verify `GET /cost` running totals include the breakout's usage.
11. Verify the cap halt fires when breakout-room cost crosses the cap (not just main-room cost).

### D. Sprint-boundary isolation

12. Sprint A runs to completion; usage records for A have `LlmUsageEntity.SprintId = A.Id`.
13. Sprint B starts in the same workspace immediately. Sprint A's late-arriving usage records (if any) carry `SprintId = A.Id`.
14. `GET /api/sprints/{B.Id}/cost` returns running totals attributable only to B — A's late records do not leak in.

### E. Concurrent-block race

15. Pause a self-drive decision call at the moment after `EvaluateAsync` returns `ShouldHalt = false` but before the post-guard re-read (§4.1).
16. From another path, call `MarkSprintBlockedAsync` on the same sprint with reason `"Operator emergency stop"`.
17. Resume the paused decision call. The post-guard re-read sees `BlockedAt != null` and returns without enqueueing.
18. **No agent round runs.**

### F. Fail-closed on guard error

19. With `FailOpenOnError = false`, force the aggregation query to throw (e.g., schema mismatch test fixture).
20. `EvaluateAsync` returns `ShouldHalt = true`, `HaltReason = "Cost guard unavailable; human review required"`.
21. Sprint is blocked; Discord NeedsInput fires.
22. With `FailOpenOnError = true` (local-dev only), same setup; `EvaluateAsync` returns `ShouldHalt = false` and a warning-level log is emitted.

### G. Restart preserves warn flag

23. Sprint warns at 80% (Progress notification fires; `CostWarnEmittedAt` is persisted).
24. Server restarts.
25. Next decision call after restart re-evaluates the sprint. `CostWarnEmittedAt` is still non-null.
26. **No duplicate Progress notification fires.**

### H. Cap raise transaction atomicity

27. Force `SaveChangesAsync` to throw on the `RaiseCostCapAsync` transaction (e.g., constraint violation injected by test fixture).
28. Verify: the sprint's override columns are unchanged AND the sprint remains blocked.
29. Endpoint returns 500. No partial state.

If any of A–H fail, this design is wrong and needs revision before merging. Tests should exist for every criterion. The harder criteria (E, F via fixture, G via process restart, H via injected fault) are integration tests; A–D and §C are runtime acceptance.
