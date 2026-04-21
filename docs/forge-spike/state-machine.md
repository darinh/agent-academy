# State Machine — Run / PhaseRun / Attempt

Three nested entities. All state is persisted to disk after every transition (see [storage-layout.md](storage-layout.md)). The executor is a pure function of disk state + methodology — crash anywhere, resume from disk.

## Entity hierarchy

```
Run                 (one per benchmark task execution)
└── PhaseRun        (one per layer in methodology, in order)
    └── Attempt     (one per LLM call for that phase; ≥1)
        └── Artifact (output, content-addressed, immutable)
```

A `Run` has N `PhaseRun`s in strict sequence (no parallelism in spike).
A `PhaseRun` has 1..MAX_ATTEMPTS `Attempt`s. New attempt = fresh LLM session, no carryover of failed attempt's output (only its validator failures, fed back as `amendment_notes`).

## States

### Run
```
Pending → Running → (Succeeded | Failed | Aborted)
```
- **Pending**: created, not started.
- **Running**: at least one PhaseRun has started.
- **Succeeded**: all PhaseRuns terminal-Succeeded.
- **Failed**: any PhaseRun terminal-Failed (no remaining attempts).
- **Aborted**: external stop (CLI Ctrl-C, kill signal). Resumable.

### PhaseRun
```
Pending → Running → (Succeeded | Failed | Skipped)
```
- **Skipped**: methodology marks phase optional and predicate evaluates false. (Not used in spike's hardcoded methodology — all 5 phases mandatory. Reserved.)
- **Failed**: terminal only after MAX_ATTEMPTS reached without a valid artifact.

### Attempt
```
Pending → Prompting → Generating → Validating → (Accepted | Rejected | Errored)
```
- **Prompting**: building prompt envelope from inputs.
- **Generating**: LLM call in flight.
- **Validating**: running validator pipeline (structural → semantic → cross-artifact).
- **Accepted**: all validators passed. Artifact written. PhaseRun → Succeeded.
- **Rejected**: ≥1 validator failed. If attempts remain, spawn next Attempt with amendment_notes; else PhaseRun → Failed.
- **Errored**: infrastructure failure (LLM API error, JSON parse failure, timeout). Counted as a rejected attempt with `error_kind` set; same retry budget applies.

## Transition rules

### Run start
1. Load methodology JSON, validate against methodology schema.
2. Create `Run` directory; write `run.json` with status=Pending, methodology hash, task_id, control_seed (for reproducibility).
3. Set status=Running. Begin first PhaseRun.

### PhaseRun start
1. Resolve inputs: list of prior artifacts named in `methodology.phases[i].inputs`. Look them up by phase_id from prior PhaseRuns. **It is an invariant that all named inputs must be Accepted artifacts; else PhaseRun fails immediately with `inputs_missing`.**
2. Write `phase-run.json` with status=Pending, phase_id, input_artifact_hashes.
3. Set status=Running. Begin Attempt 1.

### Attempt
1. **Prompting**: render envelope (see [prompt-envelope.md](prompt-envelope.md)) using:
   - Phase's `goal` and `output_contract` from methodology.
   - Resolved input artifacts (verbatim JSON, not summarized).
   - Phase's `instructions`.
   - For attempt N>1: `amendment_notes` = the rejected validator messages from attempt N-1.
2. **Generating**: single LLM call. Fresh session. No system memory. No tool use in spike. Capture full response, token counts, latency.
3. **Validating**: run validator pipeline in order. First failure short-circuits *the validator pipeline only* — collect all failures from the failing validator before stopping. (Rationale: avoid spurious cascading errors in later validators when structure is already broken.)
4. On Accepted: hash artifact, write to artifact store, append to phase-run's `attempts[]` with `accepted=true`. PhaseRun → Succeeded. Move to next PhaseRun.
5. On Rejected/Errored: append to `attempts[]` with `accepted=false`, `validator_failures[]`, `error_kind` (if applicable). If `attempts.length < MAX_ATTEMPTS`, spawn next Attempt. Else PhaseRun → Failed → Run → Failed.

### Resume (crash recovery)
On startup with `--resume <run_id>`:
1. Read `run.json`. If status ∈ {Succeeded, Failed}, exit (idempotent).
2. Find first PhaseRun with status ∈ {Pending, Running}.
3. If status=Running and last Attempt is in non-terminal state, mark that Attempt as Errored (`error_kind=interrupted`) — we cannot trust partial generation.
4. Continue per normal flow.

## Retry budget
- `MAX_ATTEMPTS = 3` per PhaseRun (spike default; configurable per phase in methodology).
- No global retry budget. A failed PhaseRun fails the Run.

## Amendment vs. retry
The spike does **not** distinguish "retry from scratch" from "amend prior attempt." Every attempt is a fresh session. The previous attempt's output is **not** included in the next attempt's prompt — only the validator failure messages are, as a list of corrections to satisfy. This is deliberate:
- Tests the "context isolation by construction" claim.
- Avoids the model anchoring on its own broken output.
- Post-spike, an `amend` mode that includes prior output may be added and A/B tested.

## Invariants (must hold at all times on disk)
1. Every `phase-run.json` references only artifact hashes that exist in the `artifacts/` store.
2. Every Accepted attempt produced exactly one artifact, present in the store.
3. `run.json.status=Succeeded` ⇒ every methodology phase has a corresponding PhaseRun with status=Succeeded and exactly one Accepted artifact.
4. Artifact files are write-once: re-writing the same hash is a no-op; writing a different content under an existing hash is a fatal bug.
5. Hash of an artifact file === its filename stem.

## Diagram

```
        ┌─────────┐
        │ Pending │
        └────┬────┘
             │ start()
        ┌────▼────┐                          ┌──────────┐
        │ Running │──── any PhaseRun fails ──▶  Failed  │
        └────┬────┘                          └──────────┘
             │ all PhaseRuns Succeeded
        ┌────▼──────┐
        │ Succeeded │
        └───────────┘
        (Aborted is reachable from Running via external signal.)


  PhaseRun:
    Pending ──start──▶ Running ──Attempt.Accepted──▶ Succeeded
                          │
                          └── all attempts exhausted ──▶ Failed


  Attempt:
    Pending ─▶ Prompting ─▶ Generating ─▶ Validating ─┬─ all pass ─▶ Accepted
                                                       └─ any fail ─▶ Rejected
    (Errored from any non-terminal state on infra failure.)
```
