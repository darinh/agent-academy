# Cost Tracking & Anomaly Detection: Design Doc

**Status**: RESOLVED — design questions in §7 answered 2026-04-25; ready for implementation per §8.
**Roadmap items**: P1.2 §4.6 (cost-cap insertion point) + P1.4 §10 (cost tracking deferred). This doc supersedes the prior cap-based draft per human direction (2026-04-25): there is no default per-sprint cap, tracking is always-on, and the binary cap-or-no-cap decision is replaced with a configurable `BreachAction` triggered by anomaly detection against a learned baseline.
**Closes gap**: G1 (runaway-cost potential identified by roadmap §P1.2 critical safety requirement). G1 is now closed by the combination of P1.2's `MaxRoundsPerSprint` (the hard backstop against runaway loops) and this design's anomaly detection (the soft signal against silent-cost-burn).
**Risk**: 🟡 (additive instrumentation + one new guard call site; one new schema column on `llm_usage`, two on `sprints`; no new halt primitive).

This doc is the design preamble for sprint-scoped cost observability and breach-action enforcement. Read this before implementing the cost guard.

---

## 1. Problem statement

Once P1.2 (Self-Drive) lands, the orchestrator will autonomously enqueue continuations until a halt condition trips. P1.2 ships **round-count caps** (`MaxRoundsPerSprint`, `MaxRoundsPerStage`, `MaxConsecutiveSelfDriveContinuations`) but **explicitly defers cost-side controls**, citing "token/cost tracking does not exist yet" (P1.2 §3.2 / §4.6).

That premise is **stale**. Cost and token tracking already exist:

- **`LlmUsageTracker`** (`Services/LlmUsageTracker.cs`, 305 LOC) records every Copilot SDK call: `AgentId`, `RoomId`, `Model`, `InputTokens`, `OutputTokens`, `CacheReadTokens`, `CacheWriteTokens`, `Cost` (USD), `DurationMs`, `ApiCallId`, `Initiator`, `ReasoningEffort`, `RecordedAt`. Backed by the `llm_usage` table (`LlmUsageEntity`).
- **`AgentQuotaService`** (`Services/AgentQuotaService.cs`, 246 LOC) already enforces per-agent `MaxRequestsPerHour` / `MaxTokensPerHour` / `MaxCostPerHour` quotas using `ILlmUsageTracker.GetAgentUsageSinceAsync`. The "read totals → compare to cap → halt" pattern is implemented and battle-tested for the per-agent scope.
- The single recording call site is `CopilotSdkSender.SendAsync` (`CopilotSdkSender.cs:180`), which fires `_usageTracker.RecordAsync(agentId, roomId, ...)` after every SDK turn.

What's missing is **sprint-scoped observability and breach detection**: per-agent quotas don't tell you whether a single sprint is silently burning $200 across five agents. This doc designs that layer, reusing both existing primitives.

The shape stays as P1.2 §4.6 reserved: an `ICostGuard.EvaluateAsync(sprint, ct)` hook called once per self-drive continuation, returning a decision record. The earlier draft of this design picked a hard `MaxCostUsdPerSprint = $5` cap as the trigger. **That was the wrong primitive**, for three reasons:

1. **Wrong question.** A static dollar cap asks "did this sprint exceed an arbitrary threshold?" The right question is "is this sprint anomalously expensive compared to what this team historically delivers per unit of value?" A $20 sprint that delivers 40 story points may be cheaper-per-point than a $0.50 sprint delivering 1 point. A flat cap charges them the same.
2. **Per-team variation is real.** A small-language-model team and a frontier-model team have totally different cost baselines. One global default serves neither, and forcing per-sprint overrides for every sprint is friction without insight.
3. **Caps create cap-raising friction without useful signal.** When the cap fires, the operator's only signal is "you hit $5." They have no information about whether $5 was reasonable for the work done. Anomaly detection ("this sprint is projected at 3.2σ above the team's cost-per-point baseline") gives the operator the signal needed to act.

We replace the hard cap with **anomaly detection on a learned baseline + configurable `BreachAction`**: track every sprint's cost (always-on, no enable flag), categorize each LLM call as Dev or Prod cost, learn a baseline cost-per-point from clean completed sprints (rolling window, mean + stddev), project the current sprint's end-of-sprint cost from its current burn rate, and trigger a configurable action — `Notify`, `Warn`, or `Block` — when the projected cost crosses `mean + N · stddev`. Round-count caps remain the hard backstop; cost is observed continuously and acted on relative to history.

---

## 2. Design principles (informed by what's already in the codebase)

These constrain every decision below.

1. **Reuse `LlmUsageTracker`, do not parallel-record.** All cost/token data already flows through one chokepoint (`CopilotSdkSender.SendAsync`). The guard reads from the same table the tracker writes to. No new recording path. No double accounting risk.
2. **Tracking is always-on. There is no `Enabled` flag.** Every LLM call is recorded with `SprintId` and `CostCategory` regardless of environment, configuration, or operator preference. Tracking has no operational risk — one indexed lookup at write time, one nullable column on the existing usage table. The previous design's `Enabled = false` default conflated *tracking* (which should always run) with *enforcement* (which is the part operators want a knob for). Enforcement is what `BreachAction` controls.
3. **No hard per-sprint cost cap. No `MaxCostUsdPerSprint` field, no per-sprint cost override.** A static dollar cap is the wrong primitive (§1). The hard backstop against runaway loops is the round-count cap from P1.2 (`MaxRoundsPerSprint`). Cost is observed and acted on via anomaly detection, never via a static threshold the operator has to guess.
4. **`BreachAction` is configurable, not binary.** Three modes: `Notify` (Progress event, sprint continues, low-noise observability), `Warn` (NeedsInput event, sprint continues, surfaces to human via the same channel as a block), `Block` (call `MarkSprintBlockedAsync`, sprint halts, identical surface to any other block). Default is `Notify` in development and `Warn` in production. `Block` is opt-in for environments where any anomaly is unacceptable (shared org account with billing alerts, long-overnight unattended runs).
5. **Halt mechanism reuses `MarkSprintBlockedAsync`.** When `BreachAction = Block` and the breach fires, the sprint blocks with `BlockReason = "Cost anomaly: ..."`. NeedsInput notification ships for free via the existing `ActivityEventType.SprintBlocked` mapping (P1.7). Identical to any other block.
6. **Running totals are computed on demand, not cached.** Computing the running total from `llm_usage` is cheap (indexed `SprintId` + `CostCategory` query), avoids drift between cached and authoritative values, and survives crashes without reconstruction logic. Only the **breach state** (was BreachAction already fired this sprint) is persisted.
7. **The hook P1.2 §4.6 reserved is sacrosanct.** Single call site in `SelfDriveDecisionService.DecideAndMaybeEnqueueAsync`, before enqueueing the continuation. We do *not* add a guard call inside `CopilotSdkSender` — that would block individual SDK calls mid-sprint, which is too granular (an agent's reply could be cut off mid-message) and bypasses the existing block-aware queue (principle 7 of P1.2).
8. **Sprint scoping is by `SprintId` stamped at write time.** `LlmUsageEntity` gets a new nullable `SprintId` column (additive migration). At the single recording call site (`CopilotSdkSender.SendAsync` line 180), the sprint id is resolved from the active `ConversationSessionEntity` for that room (`ConversationSessionEntity.SprintId` already exists for exactly this scoping purpose) and stamped on the row. Cost-for-sprint = `SUM(Cost) WHERE SprintId = X AND CostCategory = "Prod"`. This handles breakout rooms (their session also carries `SprintId`), avoids the cross-sprint window pollution that time-window scoping suffers from, and survives EF query plan changes. **§3.4 details the rejected alternative** (time-window via `RoomId ∩ workspace ∩ [sprint.CreatedAt, sprint.CompletedAt]`) and why it was wrong.
9. **Cost categorization: every recorded row is `Dev` or `Prod`.** Set at write time based on whether the active `ConversationSession` has a non-null `SprintId`. `Prod` cost is what users are paying for the agent team to deliver sprint work. `Dev` cost is what the operator pays to maintain/iterate on the system itself (agent-mod tweaks, ops chatter, DM threads, breakouts not attached to a sprint). **Anomaly baselines and `BreachAction` only consider `Prod` cost.** `Dev` cost is tracked for visibility (`GET /api/cost/dashboard`) but never gates behavior.
10. **Fail-closed-with-notify in production. Fail-open-with-notify in development.** No `FailOpenOnError` flag — the environment determines behavior, detected via `IHostEnvironment.IsProduction()`. On aggregation error: production fires `BreachAction = Block` + notify regardless of configured `BreachAction` (the cost guard is the safety; if it can't see, halt); development logs warning + fires `Progress` activity event + continues. The previous design's `FailOpenOnError = true` knob was a footgun: setting it in production silently defeated the guard during exactly the failure mode it was meant to control.
11. **Race rule: re-read sprint state, do not cache it.** A self-drive decision call that observes a passed cost guard MUST re-read the sprint state immediately before enqueueing the continuation, to avoid racing against a concurrent block (cost-guard from another decision call, P1.4-full self-eval block, operator emergency stop, sprint timeout sweep). The continuation dispatcher (`SystemContinuation` queue item processor) MUST also re-resolve the sprint and drop the item if `BlockedAt != null`, `Status != "Active"`, or `AwaitingSignOff`. This is principle 7 of P1.2 made explicit for the cost-guard path.
12. **Baseline learning is opt-in by data, not by flag.** The guard requires at least `MinCleanSprintsForBaseline` (default 5) clean completed sprints before any `BreachAction` can fire. Below that threshold, all evaluations return `ShouldHalt = false`, `BaselineReady = false`, `TakenAction = None`. Tracking continues; `GET /api/sprints/{id}/cost` still returns running totals. This prevents premature triggers when the team is new and no baseline exists.

---

## 3. State model additions

### 3.1 Schema additions (additive — all 🟢)

**`LlmUsageEntity` (two new columns)** — for sprint-scoped aggregation and dev/prod classification:

```csharp
// Sprint that owned the conversation when this LLM call recorded.
// Resolved at write time via the active ConversationSessionEntity for the room.
// Nullable for legacy/orphan records (DM threads, agent-mod activity outside a sprint).
public string? SprintId { get; set; }

// Categorization at write time. "Prod" iff the active ConversationSession has a
// non-null SprintId; otherwise "Dev". Set once, never re-categorized. Used by the
// guard to compute baselines and project costs from Prod rows only.
[StringLength(8)]
public string CostCategory { get; set; } = "Dev";
```

**`SprintEntity` (three new columns)** — for breach-state idempotence and banded re-fire (§7 decision 8):

```csharp
// Set when a BreachAction fires. Updated on each new-band fire so the
// timestamp always reflects the most recent fire. Persisted (not in-memory)
// so it survives restart and is visible via GET /api/sprints/{id}/cost.
public DateTime? BreachActionFiredAt { get; set; }

// "Notify" | "Warn" | "Block" — the action taken when BreachActionFiredAt was set.
// Useful for ops to see what was actually fired (config may have changed since).
[StringLength(16)]
public string? LastBreachAction { get; set; }

// Highest integer σ band already fired this sprint (e.g. 2 means a 2σ fire
// happened; a 3σ fire is still allowed, a 2.5σ re-fire is not). NULL means
// no band has been fired yet. See §7 decision 8 for the banded-firing rationale.
public int? LastBreachActionZBand { get; set; }
```

**Removed from the previous draft** (do NOT add):

- `SprintEntity.MaxCostUsdOverride` — there is no per-sprint cost cap to override.
- `SprintEntity.MaxTokensOverride` — same. The existing `AgentQuotaService` per-agent token caps remain; sprint-level token caps are gone with the cost cap.
- `SprintEntity.CostWarnEmittedAt` — replaced by `BreachActionFiredAt` (single column covers both warn and block under the unified BreachAction model).

Migration is **additive** — no backfill required:

- `LlmUsageEntity.SprintId` defaults to `NULL`. New writes stamp it via `ConversationSessionEntity.SprintId` (which is already populated for sprint-attached rooms — see `ConversationSessionEntity.cs:23`).
- `LlmUsageEntity.CostCategory` defaults to `"Dev"`. Pre-migration rows are conservatively classified as Dev — they are **not** retroactively included in baseline calculations (which is the safe default: historical baselines should only learn from rows whose categorization was set deliberately at write time).
- `SprintEntity.BreachActionFiredAt`, `LastBreachAction`, and `LastBreachActionZBand` all default to `NULL`.

We do **not** add `RunningCostUsd` / `RunningTokens` columns on `SprintEntity`. The running total is computed from `llm_usage` on the same query that powers the guard decision (§4). This avoids drift between the tracker's authoritative table and a cached counter.

### 3.2 Configuration

Read from `appsettings.json` under a new `Orchestrator:CostGuard` section:

```jsonc
"Orchestrator": {
  "CostGuard": {
    // Notify | Warn | Block. Default depends on environment (see below).
    "BreachAction": "Notify",

    // Anomaly threshold: breach fires when projected end-of-sprint cost exceeds
    // baseline_mean_per_point * sprint_points + N * baseline_stddev * sprint_points.
    "AnomalyStdDevs": 2.0,

    // Baseline learning. No breach can fire until at least N clean completed
    // sprints have provided cost-per-point samples.
    "MinCleanSprintsForBaseline": 5,

    // Rolling window of recent clean completed sprints to draw the baseline from.
    // Older sprints fall out as the team's behaviour evolves.
    "BaselineWindowSprints": 30,

    // Don't project end-of-sprint cost until the sprint is at least this fraction
    // complete. Early-sprint projections are wildly noisy; this prevents the guard
    // from firing on the first agent round.
    "ProjectionFloorPercentComplete": 10
  }
}
```

**Per-environment defaults** (set in `appsettings.Development.json` and `appsettings.Production.json`):

| Environment | `BreachAction` default | Rationale |
|-------------|------------------------|-----------|
| Development | `Notify`               | Operator is actively present; chat noise has minimal cost; halt is overkill. |
| Production  | `Warn`                 | Operator is mostly absent; surfacing via NeedsInput gets attention without sacrificing autonomous progress. Block is opt-in for environments where any anomaly should halt. |

**Removed knobs from the previous draft**:

- `Enabled` — gone. Tracking is always on.
- `MaxCostUsdPerSprint` / `MaxTokensPerSprint` — gone. No hard caps.
- `WarnAtPercent` — gone. There is no percentage of a cap to warn at; anomaly score replaces it.
- `FailOpenOnError` — gone. Behaviour is determined by `IHostEnvironment.IsProduction()`, never by a flag (principle 10).

### 3.3 Why hard per-sprint caps were rejected

(See §1 for the full argument.) Summary: a static cap asks the wrong question, picks an arbitrary threshold that suits no team, and gives the operator no actionable signal when it fires. We retain `MaxRoundsPerSprint` from P1.2 as the hard backstop against runaway loops; cost is observed continuously and acted on relative to history. The cost of getting this wrong (silent cost burn that exceeds the operator's tolerance) is mitigated by the always-on `Notify` default — the operator sees the anomaly even if no halt is configured.

### 3.4 Why time-window scoping was rejected (in favour of `LlmUsageEntity.SprintId`)

An earlier iteration of this design used "time window ∩ rooms-in-workspace" scoping to avoid a schema migration on the high-traffic `llm_usage` table. Adversarial review found two correctness bugs:

1. **Breakout-room undercount.** Breakout LLM calls pass `breakoutRoomId` as the usage `RoomId`, but breakout rooms live in `BreakoutRooms`, not `Rooms` (see `BreakoutLifecycleService.cs:217`, `BreakoutRoomEntity.cs:17-31`). A `Where(r => r.WorkspacePath == sprint.WorkspacePath)` over `db.Rooms` would have missed every breakout call — a sprint that does most of its work in breakout implementation loops would pass the cap check while burning cost outside the guard.
2. **Cross-sprint overcount.** `LlmUsageTracker.RecordAsync` sets `RecordedAt = DateTime.UtcNow` at persistence time, which is *after* the SDK call returns. A previous sprint's in-flight SDK call that records usage after the next sprint's `CreatedAt` would land in the new sprint's window, charging cost from the prior sprint to the next one.

Both bugs vanish if usage records carry the `SprintId` they were generated under. `ConversationSessionEntity.SprintId` already exists for exactly this scoping purpose (`ConversationSessionEntity.cs:23`) — it is set whenever a session is opened in a sprint-attached room (main or breakout). The recording call site in `CopilotSdkSender.SendAsync` already has `roomId` in scope; resolving the active session's `SprintId` is one indexed lookup. The migration is additive (one nullable column on `llm_usage`).

**Pre-migration rows have `SprintId = NULL` and `CostCategory = "Dev"`.** The guard query is `WHERE SprintId = X AND CostCategory = "Prod"` — it correctly excludes those rows. Cost computed for sprints that ran before this feature lands is therefore $0 by construction, which is acceptable: the guard only matters for sprints that begin under the new code path. Historical baselines learn nothing from pre-migration rows, which is the safe default — those rows have no reliable categorization.

### 3.5 Cost categorization (Dev vs Prod) — resolution rule

At write time in `CopilotSdkSender.SendAsync` (line 180), before calling `_usageTracker.RecordAsync`:

```csharp
var session = await _db.ConversationSessions
    .Where(s => s.RoomId == roomId && s.Status == "Active")
    .Select(s => new { s.SprintId })
    .FirstOrDefaultAsync(ct);

var sprintId = session?.SprintId;            // may be null
var category = sprintId is null ? "Dev" : "Prod";

await _usageTracker.RecordAsync(agentId, roomId, sprintId, category, /* existing args */);
```

The categorization is set once at recording time and never changed. This means:

- **Baseline computation**: `WHERE CostCategory = "Prod" AND SprintId IN (clean completed sprints)`.
- **Anomaly detection** for the current sprint: `WHERE SprintId = currentSprintId AND CostCategory = "Prod"` (always Prod for the active sprint by construction, but the explicit filter is defensive).
- **Dev-cost reporting** (ops dashboard): `WHERE CostCategory = "Dev"` — for operator visibility into agent-mod / ad-hoc spend, never for gating.

If a breakout room is later promoted to attach to a sprint (rare), late-attached rows are not retroactively re-categorized. This is acceptable: the cost was incurred under the dev categorization, and re-tagging would require backfill logic for a small edge case. Confirmed per §7 decision 6.

---

## 4. Control-flow design

### 4.1 The decision point

The guard plugs into the hook P1.2 §4.6 reserved, with **no other call sites**:

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

// Notify/Warn modes: decision.TakenAction is non-None and the activity event was
// already broadcast by the guard. Continue with the normal race-rule re-read.

// Race rule (principle 11): re-read sprint state immediately before enqueueing.
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
public enum BreachAction
{
    None,    // No anomaly OR baseline not yet ready.
    Notify,  // Progress event broadcast; sprint continues.
    Warn,    // NeedsInput event broadcast; sprint continues; surfaces to human.
    Block    // SprintBlocked event broadcast; sprint halted via MarkSprintBlockedAsync.
}

public interface ICostGuard
{
    Task<CostGuardDecision> EvaluateAsync(SprintEntity sprint, CancellationToken ct);
}

public sealed record CostGuardDecision(
    bool         ShouldHalt,                     // true iff TakenAction == Block.
    string?      HaltReason,                     // non-null iff ShouldHalt.

    decimal      RunningCostUsd,                 // sum of Prod cost rows for this sprint.
    long         RunningTokens,                  // sum of input+output tokens, Prod rows only.

    decimal?     FractionComplete,               // sprint progress 0..1; null if unresolvable.
    decimal?     ProjectedEndOfSprintCostUsd,    // null if FractionComplete < ProjectionFloorPercentComplete OR baseline not ready.

    decimal?     BaselineMeanCostPerPointUsd,    // null if baseline not ready.
    decimal?     BaselineStdDevPerPointUsd,
    int          BaselineSampleSize,

    decimal?     AnomalyZScore,                  // (projected - expected) / (stddev * points); null if cannot compute.
    BreachAction TakenAction,
    bool         BaselineReady);
```

The decision record carries the full picture so the caller can construct block reasons, the SprintBlocked event payload, and the GET /cost response without recomputing.

### 4.3 The guard implementation (sketch)

```csharp
public sealed class CostGuard : ICostGuard
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<CostGuardOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly IBaselineService _baselineService;   // see §4.5
    private readonly ILogger<CostGuard> _logger;
    private readonly IActivityBus _activityBus;

    public async Task<CostGuardDecision> EvaluateAsync(SprintEntity sprint, CancellationToken ct)
    {
        var opts = _options.CurrentValue;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            // Running totals — Prod-categorized only, this sprint only.
            var current = await db.LlmUsage
                .Where(u => u.SprintId == sprint.Id && u.CostCategory == "Prod")
                .GroupBy(_ => 1)
                .Select(g => new {
                    Cost = g.Sum(u => u.Cost ?? 0),
                    Tokens = g.Sum(u => u.InputTokens + u.OutputTokens)
                })
                .FirstOrDefaultAsync(ct);

            var runningCost = (decimal)(current?.Cost ?? 0);
            var runningTokens = current?.Tokens ?? 0;

            // Baseline (cached; invalidated on SprintCompleted events — §4.5).
            var baseline = await _baselineService.GetAsync(ct);
            if (!baseline.IsReady(opts.MinCleanSprintsForBaseline))
            {
                return Observable(runningCost, runningTokens, sprint, baseline, projected: null, z: null, action: BreachAction.None, baselineReady: false);
            }

            // Project end-of-sprint cost.
            var fraction = await ResolveFractionCompleteAsync(db, sprint.Id, ct);
            decimal? projected = null;
            decimal? z = null;
            if (fraction is { } f && f >= opts.ProjectionFloorPercentComplete / 100m && f > 0)
            {
                projected = runningCost / f;
                var sprintPoints = await ResolveSprintPointsAsync(db, sprint.Id, ct);
                if (sprintPoints > 0 && baseline.StdDevPerPoint > 0)
                {
                    var expected = baseline.MeanCostPerPoint * sprintPoints;
                    z = (projected.Value - expected) / (baseline.StdDevPerPoint * sprintPoints);
                }
            }

            // Anomaly?
            var anomaly = z.HasValue && z.Value >= (decimal)opts.AnomalyStdDevs;
            // Banded re-fire (§7 decision 8): fire iff anomaly AND we have not already
            // fired at-or-above the current integer σ band this sprint.
            var currentBand = z.HasValue ? (int)Math.Floor(z.Value) : 0;
            var alreadyFiredThisBand = sprint.LastBreachActionZBand.HasValue
                && currentBand <= sprint.LastBreachActionZBand.Value;
            if (!anomaly || alreadyFiredThisBand)
            {
                return Observable(runningCost, runningTokens, sprint, baseline, projected, z, action: BreachAction.None, baselineReady: true);
            }

            // Atomic claim: only one decision call wins the right to fire BreachAction
            // for this sprint AT THIS BAND. The conditional WHERE encodes "no band yet
            // fired, OR the highest band fired is strictly below the band we'd fire now."
            var rowsUpdated = await db.Sprints
                .Where(s => s.Id == sprint.Id
                    && (s.LastBreachActionZBand == null || s.LastBreachActionZBand < currentBand))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.BreachActionFiredAt, DateTime.UtcNow)
                    .SetProperty(s => s.LastBreachAction, opts.BreachAction.ToString())
                    .SetProperty(s => s.LastBreachActionZBand, currentBand),
                    ct);
            if (rowsUpdated == 0)
            {
                // Another decision call beat us to it (or raced past our band); treat as observed.
                return Observable(runningCost, runningTokens, sprint, baseline, projected, z, action: BreachAction.None, baselineReady: true);
            }

            var reason = $"Cost anomaly: projected ${projected:F2} vs baseline ${baseline.MeanCostPerPoint:F2}/pt × pts ± stddev (z = {z:F2}σ)";

            switch (opts.BreachAction)
            {
                case BreachAction.Notify:
                    _activityBus.Broadcast(/* Progress: cost-anomaly observed */);
                    return Decision(runningCost, runningTokens, sprint, baseline, projected, z, BreachAction.Notify, halt: false, halt_reason: null);

                case BreachAction.Warn:
                    _activityBus.Broadcast(/* NeedsInput: cost-anomaly */);
                    return Decision(runningCost, runningTokens, sprint, baseline, projected, z, BreachAction.Warn, halt: false, halt_reason: null);

                case BreachAction.Block:
                default:
                    return Decision(runningCost, runningTokens, sprint, baseline, projected, z, BreachAction.Block, halt: true, halt_reason: reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cost guard aggregation failed for sprint {SprintId}", sprint.Id);

            // Principle 10: environment determines, no flag.
            if (_environment.IsProduction())
            {
                // Do NOT broadcast SprintBlocked here. The caller's
                // MarkSprintBlockedAsync (§4.1) is the single atomic source of the
                // SprintBlocked event — it claims BlockedAt and broadcasts iff it
                // wins the transition. Broadcasting here would cause double-fire
                // (first from this catch, then from MarkSprintBlockedAsync) and
                // could broadcast even when MarkSprintBlockedAsync's
                // ExecuteUpdateAsync loses the race to a concurrent block.
                return new CostGuardDecision(
                    ShouldHalt: true,
                    HaltReason: "Cost guard unavailable; human review required",
                    RunningCostUsd: 0, RunningTokens: 0,
                    FractionComplete: null, ProjectedEndOfSprintCostUsd: null,
                    BaselineMeanCostPerPointUsd: null, BaselineStdDevPerPointUsd: null, BaselineSampleSize: 0,
                    AnomalyZScore: null,
                    TakenAction: BreachAction.Block,
                    BaselineReady: false);
            }
            else
            {
                _activityBus.Broadcast(/* Progress: cost guard unavailable in dev, continuing */);
                return new CostGuardDecision(
                    ShouldHalt: false, HaltReason: null,
                    RunningCostUsd: 0, RunningTokens: 0,
                    FractionComplete: null, ProjectedEndOfSprintCostUsd: null,
                    BaselineMeanCostPerPointUsd: null, BaselineStdDevPerPointUsd: null, BaselineSampleSize: 0,
                    AnomalyZScore: null,
                    TakenAction: BreachAction.None,
                    BaselineReady: false);
            }
        }
    }
}
```

`Observable(...)` and `Decision(...)` are private helpers that pack the record with the correct fields; sketched here for clarity, not a contract.

The persisted `LastBreachActionZBand` (claimed atomically via `ExecuteUpdateAsync`) ensures only one decision call wins the right to broadcast at any given σ band, and re-firing at the same or lower band — including across process restarts — is impossible. Crossing into a higher integer band re-arms exactly one new fire (per §7 decision 8). This generalises across all three BreachAction modes.

### 4.4 Continuation dispatch must also re-read sprint state

The race rule in principle 11 has two halves. The first (re-read before enqueue) is in §4.1. The second is here: when the queue worker dequeues a `SystemContinuation` item, before invoking the round runner it must re-resolve the sprint and drop the item if `BlockedAt != null`, `Status != "Active"`, or `AwaitingSignOff`. This protects against:

- a continuation enqueued microseconds before another caller blocked the sprint (cost-guard from another decision call, self-eval, operator emergency stop);
- a continuation surviving server restart while the sprint state changed during downtime;
- the enqueue-then-block window between `MarkSprintBlockedAsync` returning success and `SprintBlocked` notifications being processed.

The check is identical to the one P1.4-narrow already added to `ConversationRoundRunner` for blocked sprints. Self-drive continuations reuse it.

### 4.5 Baseline service

A `BaselineService` (singleton) owns the cached baseline and recomputes it lazily on demand, with cache invalidation triggered by `ActivityEventType.SprintCompleted` events on the activity bus. The cache holds:

```csharp
public sealed record Baseline(
    decimal MeanCostPerPoint,
    decimal StdDevPerPoint,
    int     SampleSize,
    DateTime ComputedAt)
{
    public bool IsReady(int min) => SampleSize >= min;
    public static Baseline Empty => new(0m, 0m, 0, DateTime.UtcNow);
}
```

Recomputation:

```csharp
public async Task<Baseline> RecomputeAsync(CancellationToken ct)
{
    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    var opts = _options.CurrentValue;

    // "Clean completed sprint" definition (per §7 decision 4):
    //   Status == "Completed"
    //   AND BreachActionFiredAt IS NULL  (no anomaly fired during the sprint)
    //   AND BlockedAt IS NULL            (currently unblocked at completion)
    //   AND CompletedAt IS NOT NULL
    //   AND RoundsThisSprint < MaxRoundsPerSprint at completion (no round-cap-hit either)
    //
    // NOTE: the round-cap predicate uses the *current* configured MaxRoundsPerSprint
    // value snapshotted at recompute time. If the operator changes that value, the
    // next recompute reclassifies historical sprints accordingly. Acceptable: the
    // alternative (persisting a "hit round cap" boolean at completion time) requires
    // a schema migration and a SprintEntity column we don't otherwise need.
    var selfDriveOpts = _selfDriveOptions.CurrentValue;
    var recent = await db.Sprints
        .Where(s => s.Status == "Completed"
                 && s.BreachActionFiredAt == null
                 && s.BlockedAt == null
                 && s.CompletedAt != null
                 && s.RoundsThisSprint < selfDriveOpts.MaxRoundsPerSprint)
        .OrderByDescending(s => s.CompletedAt)
        .Take(opts.BaselineWindowSprints)
        .Select(s => s.Id)
        .ToListAsync(ct);

    var perPointCosts = new List<decimal>();
    foreach (var sprintId in recent)
    {
        var cost = await db.LlmUsage
            .Where(u => u.SprintId == sprintId && u.CostCategory == "Prod")
            .SumAsync(u => (decimal)(u.Cost ?? 0), ct);
        var points = await ResolveSprintPointsAsync(db, sprintId, ct);
        if (points > 0) perPointCosts.Add(cost / points);
    }

    if (perPointCosts.Count == 0) return Baseline.Empty;

    var mean = perPointCosts.Average();
    var variance = perPointCosts.Average(c => (c - mean) * (c - mean));
    var stddev = (decimal)Math.Sqrt((double)variance);

    return new Baseline(mean, stddev, perPointCosts.Count, DateTime.UtcNow);
}
```

Cache strategy: hold the result indefinitely; invalidate (force recompute on next `GetAsync`) when the service observes an `ActivityEventType.SprintCompleted` event. This means decision calls don't pay the recomputation cost; only completions do, and at most once per completion.

`ResolveSprintPointsAsync` returns the count of `TaskEntity` rows for the sprint where `Status != Cancelled`. This is the "task-count fallback" — there is no `Points` field on `TaskEntity` and one will not be added (per §7 decision 5). Cost-per-point is therefore literally cost-per-non-cancelled-task throughout this design.

### 4.6 No unblock-recovery extension. No raise-cap endpoint.

The previous draft extended `UnblockSprintAsync` to clear `CostWarnEmittedAt` and added a `POST /api/sprints/{id}/raise-cost-cap` endpoint with transactional cap-update + auto-unblock. Both are **removed**:

- **No warn flag to clear.** `BreachActionFiredAt` / `LastBreachActionZBand` intentionally persist across the unblock. If the human reviews and unblocks via the existing `POST /api/sprints/{id}/unblock`, the sprint resumes; re-firing at the same σ band would be noise. Drift into a *higher* band will fire again per §7 decision 8 (the operator who unblocked at 2σ assuming "small overage" gets a fresh notification at 3σ). The `ProjectedEndOfSprintCostUsd` continues to update on `GET /cost` so the human can monitor whether the unblock was justified.
- **No cap to raise.** Without `MaxCostUsdOverride`, there is nothing to PATCH. If the human wants to relax enforcement for a specific sprint, the path is: review the anomaly, unblock with `POST /unblock`, monitor `GET /cost` for further drift. If anomalies become routine and the operator wants the guard quieter, they change the global `BreachAction` to `Notify`.

This is intentional friction reduction: every "raise the cap" call in the previous design was operator toil that produced no signal about whether the new cap was right either. The unified path — observe via `GET /cost`, decide, unblock — covers the same cases with one fewer endpoint.

**Banded re-fire (§7 decision 8)**: `BreachAction` re-fires once per integer σ band crossed (2σ → 3σ → 4σ → …). `SprintEntity.LastBreachActionZBand` (int, nullable) tracks the highest band already fired this sprint; a new fire requires `floor(currentZ) > LastBreachActionZBand`. `BreachActionFiredAt` and `LastBreachAction` continue to record the most recent fire (used for one-shot idempotence within a band, including across process restarts).

---

## 5. New API surface

### 5.1 `GET /api/sprints/{id}/cost`

Returns the full cost view for a sprint. Drives operator visibility and (eventually) the SprintPanel cost meter.

```json
{
  "sprintId": "abc...",
  "runningCostUsd": 0.83,
  "runningTokens": 145222,
  "fractionComplete": 0.42,
  "projectedEndOfSprintCostUsd": 1.97,
  "baseline": {
    "ready": true,
    "meanCostPerPointUsd": 0.18,
    "stdDevPerPointUsd": 0.06,
    "sampleSize": 12
  },
  "anomalyZScore": 1.40,
  "breachAction": {
    "configured": "Warn",
    "fired": false,
    "firedAt": null,
    "lastFiredAction": null
  },
  "asOf": "2026-04-25T07:31:00Z"
}
```

When the baseline is not ready (insufficient clean completed sprints), `baseline.ready = false`, `projectedEndOfSprintCostUsd = null`, `anomalyZScore = null`. Running totals and fraction-complete are still returned — observability without gating.

The endpoint is read-only and unauthenticated to the same degree as other sprint read endpoints. It is computed via the same query the guard uses (`WHERE SprintId = X AND CostCategory = "Prod"`), so the displayed values and the guard's decisions never disagree by construction.

### 5.2 `GET /api/cost/dashboard`

Global ops view. Returns dev-vs-prod breakdown over a rolling window plus the current baseline.

```json
{
  "windowDays": 30,
  "prodCostUsd": 12.45,
  "devCostUsd": 4.20,
  "prodSprintsCompleted": 8,
  "baseline": {
    "ready": true,
    "meanCostPerPointUsd": 0.18,
    "stdDevPerPointUsd": 0.06,
    "sampleSize": 12
  }
}
```

This is the surface the operator uses to answer "how much am I spending on agent self-mod vs delivering sprints?" Useful for budgeting decisions about model selection, agent tweaks, etc. Out of scope for any gating — purely informational.

### 5.3 No `POST /api/sprints/{id}/raise-cost-cap`

Removed (§4.6). The existing `POST /api/sprints/{id}/unblock` is the only operator action for a Block-mode breach.

### 5.4 No frontend in this design

A cost meter on `SprintPanel` and a cost dashboard view are obvious follow-ups; they're shaped for by the endpoints above. Bikeshedding the UI here is out of scope. Frontend is **out of scope** for this design (§7 decision 7) and may be added independently.

---

## 6. Notifications

| Trigger | `BreachAction` configured | ActivityEventType | NotificationType | Surface |
|---------|---------------------------|-------------------|------------------|---------|
| Anomaly detected, new σ band crossed     | `Notify`               | `Progress`         | (Progress mapping) | Discord room post — low-noise observability |
| Anomaly detected, new σ band crossed     | `Warn`                 | new `CostAnomalyWarning` (per §7 decision 9) | `NeedsInput`       | Discord NeedsInput "Sprint X cost is anomalous (z = N.Nσ)" |
| Anomaly detected, new σ band crossed     | `Block`                | `SprintBlocked` (existing) | `NeedsInput`       | Same surface as any other block — sprint halts |
| Cost guard unavailable (production)      | (any)                   | `SprintBlocked` (existing) | `NeedsInput`       | Sprint halts; reason = "Cost guard unavailable; human review required" |
| Cost guard unavailable (development)     | (any)                   | `Progress`         | (Progress mapping) | Discord room post; sprint continues |

`Notify` mode reuses `Progress`, `Block` mode reuses `SprintBlocked`, `Warn` mode adds `ActivityEventType.CostAnomalyWarning` mapped to `NotificationType.NeedsInput` (per §7 decision 9).

---

## 7. Resolved decisions

Resolved 2026-04-25 by the human reviewer. Each decision below was a `## 7. Reviewer ask` in the prior revision; the rationale paragraphs are kept so future readers see why the choice was made.

1. **Production default `BreachAction = "Warn"`** — confirmed. Warn surfaces to humans without halting the sprint; Block halts; Notify is invisible-but-logged. Warn is the right default for prod overnight runs: humans get told something looks weird without losing autonomous progress. Block is appropriate for environments where any anomaly is unacceptable (shared org account with billing alerts). Notify is appropriate for dev where the operator is actively present.

2. **`AnomalyStdDevs = 2.0`** — confirmed. Two-sigma is the conventional anomaly threshold (~5% false-positive rate on a normal distribution). With small baseline sample sizes (5–10), false positives go up. Three-sigma (~0.3% FPR) gives quieter alerts but lets bigger overshoots through. Configurable per environment.

3. **`MinCleanSprintsForBaseline = 5`** — confirmed. Statistically marginal (variance estimates with N=5 are noisy) but waiting for 30 sprints means the guard is effectively off for months. Five is the smallest defensible number. Raise to 10 if real data shows the variance is too noisy to be useful.

4. **"Clean completed sprint" definition** — confirmed: `Status=Completed AND BreachActionFiredAt IS NULL AND BlockedAt IS NULL AND CompletedAt IS NOT NULL AND RoundsThisSprint < MaxRoundsPerSprint at completion`. The round-cap filter is intentional: a round-cap-hit means the sprint terminated abnormally and its cost-per-point is not a clean reference. Small sprints (< 3 points) are NOT excluded — every sprint with at least one point counts.

5. **"Point" definition: non-cancelled task count (option c)** — confirmed. No `Points` field will be added to `TaskEntity`. Per-task cost is meaningful even if coarse, and a points-field that humans-or-agents have to fill in correctly is a reliability cost we're not taking on. The cost-per-point ratio in this doc is therefore literally cost-per-non-cancelled-task. Revisit only if real data shows task counts are too noisy a proxy.

6. **Ad-hoc sprint-context calls categorized as `Prod`** — confirmed. If an agent makes a `CopilotSdkSender` call from within a sprint room but for a non-sprint reason (e.g., the orchestrator asks an agent to clarify something procedural), the call is classified `Prod` because the active session has a `SprintId`. Classifying intent at write time would be brittle.

7. **Frontend cost badge: out of scope for this design.** Add separately if visibility is wanted before P1.2 lands. The ~40 LOC frontend badge is non-blocking and unrelated to the backend design here.

8. **Banded BreachAction firing** — confirmed: re-fire only when a *new integer σ band* is crossed (2σ → 3σ → 4σ). Adds one column `LastBreachActionZBand` (int, nullable) tracking the highest band fired this sprint. `BreachActionFiredAt` and `LastBreachAction` remain — they record the *most recent* fire. Idempotence: re-firing within the same band must remain a no-op across process restarts. Update `SprintEntity` columns from §6 accordingly.

9. **`Warn` mode event type: add `ActivityEventType.CostAnomalyWarning`** — confirmed. Mapped to `NotificationType.NeedsInput`. Reusing `SprintBlocked` semantically would overload the event with non-block semantics and is rejected.

10. **Baseline cache invalidation: event-driven on `SprintCompleted` + 1-hour TTL safety net** — confirmed. Event-driven is more accurate; TTL is more robust to bus glitches. Both are wired.

11. **Mid-sprint baseline becoming ready: guard starts evaluating immediately** — confirmed. A sprint already in flight when the 5th clean completion lands gets evaluated from that point on. There is no free pass once data exists.

---

## 8. Implementation order (after design approval)

This is a small implementation — comparable in scope to the prior cap-based draft, but with the baseline service and the categorization column adding modest extra work.

1. **Schema migration** (additive). `LlmUsageEntity.SprintId` (nullable string), `LlmUsageEntity.CostCategory` (string default "Dev"); `SprintEntity.BreachActionFiredAt` (nullable DateTime), `SprintEntity.LastBreachAction` (nullable string ≤ 16), `SprintEntity.LastBreachActionZBand` (nullable int — see §7 decision 8). EF migration only; no backfill.
2. **`SprintId` + `CostCategory` write-time stamping** in `CopilotSdkSender.SendAsync` (line 180). Lookup `ConversationSessionEntity.SprintId` via `roomId`; categorize Prod iff non-null. Extend `ILlmUsageTracker.RecordAsync` signature with `string? sprintId` and `string costCategory` parameters.
3. **`CostGuardOptions` record + DI binding.** New `Orchestrator:CostGuard` config section, per-environment defaults (Dev=Notify, Prod=Warn), `AddOptions<CostGuardOptions>().BindConfiguration(...)` in startup.
4. **Sprint-points resolution: task-count fallback** (per §7 decision 5). `ResolveSprintPointsAsync` returns `count(TaskEntity where SprintId = X and Status != Cancelled)`. No `TaskEntity.Points` field, no migration, no agent-prompt change.
5. **`IBaselineService` + `Baseline` record + `BaselineService` impl.** Singleton DI. Subscribes to the activity bus for `SprintCompleted` events to invalidate the cache. Includes a TTL safety net.
6. **`ICostGuard` interface + `CostGuardDecision` record + `BreachAction` enum + `NoOpCostGuard` impl + `CostGuard` impl.** Singleton DI. Default binding to `CostGuard` (no env-or-flag toggle — tracking is always-on, the BreachAction config controls the action). `NoOpCostGuard` is retained for tests.
7. **Wire into `SelfDriveDecisionService.DecideAndMaybeEnqueueAsync` step 12.** Single call site. Includes the post-guard re-read of sprint state (§4.1, principle 11).
8. **Continuation dispatch re-read** in the queue worker that consumes `SystemContinuation` items (§4.4). If the sprint is no longer Active / is BlockedAt / is AwaitingSignOff, drop the item. (Same as the prior design — unchanged by this rewrite.)
9. **`GET /api/sprints/{id}/cost`** controller + DTO (`SprintCostDto`).
10. **`GET /api/cost/dashboard`** controller + DTO (`CostDashboardDto`).
11. **New `ActivityEventType.CostAnomalyWarning`** (per §7 decision 9), with NeedsInput mapping in `ActivityNotificationBroadcaster`.
12. **Tests**: see §10 acceptance criteria + unit tests per the cases below.
13. **Acceptance test thread (manual)** — see §10.

This sequencing ships always-on tracking in step 2 (immediately useful for ops dashboards even before the guard wires in), and ships breach-action enforcement only after the baseline can be meaningful.

---

## 9. Out of scope (deferred or non-goals)

- **Per-agent vs. per-sprint reconciliation.** `AgentQuotaService` and `CostGuard` operate on different scopes (per-agent rolling hour vs. per-sprint anomaly). They can both halt independently. We do *not* try to unify them — they answer different questions.
- **Per-archetype baselines.** Phase 1 sprints and Phase 2 sprints may have systematically different cost profiles. The current design pools them. If real data shows the variance is too high to be useful, partition the baseline by `SprintEntity.Archetype` (or similar). Defer until evidence demands it.
- **Per-team / per-deployment baselines.** Single-tenant by construction today.
- **Cost prediction at sprint creation time.** Pre-flight estimates ("this sprint will cost ~$X") would require bin-packing future agent costs against a plan; unsolved.
- **Backfill of historical `CostCategory`.** Pre-migration rows stay `CostCategory = "Dev"`. Acceptable: those rows do not contribute to baselines (which is the safe default).
- **Cost-aware model selection.** "If we're at 1σ, switch to a cheaper model." Tempting but couples scheduling decisions to cost in ways that are hard to reason about.
- **A `CostGuard` for non-self-drive paths.** Today only self-drive can run unbounded; human-message-driven rounds are bounded by the human's own pace.
- **Frontend.** §5.4.

---

## 10. Acceptance criteria (the moment this lands)

Each criterion below maps to a design choice in §2/§3/§4 and must pass for the implementation to ship.

**Setup**: `Orchestrator:CostGuard:BreachAction = "Block"` (forced for testability), `MinCleanSprintsForBaseline = 2` (artificially low), `AnomalyStdDevs = 1.0` (artificially sensitive), `ProjectionFloorPercentComplete = 10`.

### A. Always-on tracking, no enable flag

1. With **no** `Orchestrator:CostGuard:Enabled` key in config (it does not exist), every LLM call records `SprintId` and `CostCategory`. Verified by querying `llm_usage` after a sprint runs and seeing populated columns on every row.

### B. Baseline learning

2. With zero clean completed sprints, `EvaluateAsync` returns `BaselineReady = false`, `TakenAction = None`, `ProjectedEndOfSprintCostUsd = null`. No `BreachAction` fires regardless of how high the running cost climbs.
3. Run two clean sprints to completion (no anomaly fired, no block, completed normally). Run a third sprint that climbs hot. Now `BaselineReady = true`, projected cost is non-null, and (if hot enough) `BreachAction` fires.

### C. Anomaly detection + projection

4. Sprint at 50% complete with cost = 2 × baseline-mean × points: projected = 4 × baseline-mean × points. With `AnomalyStdDevs = 1.0` and stddev > 0, z-score crosses threshold and `Block` fires.
5. The same sprint at 5% complete (below `ProjectionFloorPercentComplete = 10`) does NOT fire — projection is suppressed by the floor.

### D. BreachAction modes

6. With `BreachAction = "Notify"` and a clear anomaly: `Progress` activity event broadcast, `ShouldHalt = false`, sprint continues.
7. With `BreachAction = "Warn"` and a clear anomaly: NeedsInput-mapped event broadcast, `ShouldHalt = false`, sprint continues, Discord NeedsInput received.
8. With `BreachAction = "Block"` and a clear anomaly: `SprintBlocked` event broadcast, `ShouldHalt = true`, `MarkSprintBlockedAsync` called, sprint halts. Discord NeedsInput received exactly once.

### E. Dev vs Prod categorization

9. Make an LLM call from a DM thread (no sprint context). Verify `CostCategory = "Dev"`, `SprintId IS NULL`. Verify the call does NOT contribute to any sprint's running total or to the baseline.
10. Make an LLM call from a breakout room attached to a sprint. Verify `CostCategory = "Prod"`, `SprintId = the_sprint_id`. Verify the call contributes to the sprint's running total AND eventually to the baseline (after the sprint completes cleanly).

### F. Fail behaviour by environment

11. With `IHostEnvironment.IsProduction() == true`, force the aggregation query to throw. `EvaluateAsync` returns `ShouldHalt = true`, `HaltReason = "Cost guard unavailable; human review required"`, `TakenAction = Block`. Sprint halts. Discord NeedsInput received.
12. With `IHostEnvironment.IsDevelopment() == true`, same setup. `EvaluateAsync` returns `ShouldHalt = false`, `TakenAction = None`. `Progress` event broadcast. Sprint continues. Warning logged.

### G. Sprint-boundary isolation

13. Sprint A runs to completion; usage records for A have `SprintId = A.Id`, `CostCategory = "Prod"`.
14. Sprint B starts in the same workspace immediately. Sprint A's late-arriving usage records carry `SprintId = A.Id`.
15. `GET /api/sprints/{B.Id}/cost` returns running totals attributable only to B — A's late records do not leak in.

### H. Concurrent-block race

16. Pause a self-drive decision call at the moment after `EvaluateAsync` returns (no halt, no breach) but before the post-guard re-read (§4.1).
17. From another path, call `MarkSprintBlockedAsync` on the same sprint with reason `"Operator emergency stop"`.
18. Resume the paused decision call. The post-guard re-read sees `BlockedAt != null` and returns without enqueueing. **No agent round runs.**

### I. Banded BreachAction firing (idempotence + restart + new-band re-arm)

19. With `BreachAction = "Notify"`, an anomaly fires at z = 2.1 (band 2) and `LastBreachActionZBand = 2`. The next decision call (still at z ≈ 2.3, same band) returns `TakenAction = None` — no second `Progress` event broadcasts.
20. Server restarts. Next decision call after restart at z = 2.4 sees `LastBreachActionZBand = 2 ≥ floor(2.4)`, does not re-fire.
21. Sprint drifts further; next decision call at z = 3.1 (band 3) fires again — `LastBreachActionZBand` advances to 3. The next decision call at z = 3.2 returns `TakenAction = None`. (Per §7 decision 8.)

### J. Cap raise + raise-cap endpoint do NOT exist

22. `POST /api/sprints/{id}/raise-cost-cap` returns 404 (or is not registered). No `MaxCostUsdOverride` column on `SprintEntity`. (Negative test: confirms removal.)

### K. Baseline excludes anomaly-fired and round-cap-hit sprints

23. Run a sprint that fires `BreachAction` (any mode) and completes. Confirm it is NOT included in the next baseline computation.
24. Run a sprint that hits `MaxRoundsPerSprint` and completes (or is blocked at round-cap). Confirm it is NOT included in the next baseline computation.

If any of A–K fail, this design is wrong and needs revision before merging. Tests should exist for every criterion. The harder criteria (F via fixture, H via injected pause, I via process restart) are integration tests; A–E and G are runtime acceptance.
