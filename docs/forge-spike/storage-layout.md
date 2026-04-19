# Storage Layout

On-disk only. No DB. The file system **is** the state machine.

## Root

```
forge-runs/                       # configurable; default ./forge-runs/
├── artifacts/                    # global, content-addressed; shared across runs
│   ├── 3a/
│   │   └── 3a7f...e21.json       # one artifact, immutable, hash-named
│   ├── 9c/
│   │   └── 9c11...b04.json
│   └── ...
└── runs/
    └── <run_id>/                 # one directory per Run; run_id = ULID
        ├── run.json              # Run state (see schema below)
        ├── methodology.json      # frozen copy of methodology used (for reproducibility)
        ├── task.json             # the benchmark task brief
        ├── phases/
        │   ├── 01-requirements/
        │   │   ├── phase-run.json
        │   │   └── attempts/
        │   │       ├── 01/
        │   │       │   ├── prompt.txt
        │   │       │   ├── response.raw.txt
        │   │       │   ├── response.parsed.json    # only if parseable
        │   │       │   ├── validator-report.json
        │   │       │   └── meta.json               # tokens, latency, model, timestamps
        │   │       └── 02/                         # if attempt 1 was rejected
        │   ├── 02-contract/
        │   ├── 03-function-design/
        │   ├── 04-implement/
        │   └── 05-verify/
        ├── review-summary.json   # rolled-up final state for human/Socrates consumption
        └── trace.log             # append-only structured log (NDJSON), one line per event
```

## Naming rules

- `run_id`: ULID (sortable by creation time, URL-safe). Prefix with `R_` for grep-ability: `R_01HX...`.
- Phase directories: `NN-<phase_id>` where `NN` is the phase index (zero-padded). Index ensures lexical sort = execution order; phase_id is the human label.
- Attempt directories: zero-padded 2-digit attempt number (`01`, `02`, ...). Max 99 by convention; spike caps at 3.
- Artifact files: `<sha256_hex>.json`, sharded by first two hex chars. (Sharding avoids one giant directory; 256 buckets is plenty for the spike.)

## Atomicity rules

Every write that mutates state uses **write-temp-then-rename**:
1. Write to `<target>.tmp.<pid>.<rand>` in the same directory.
2. `fsync` the file.
3. `rename` to final name (atomic on POSIX same-filesystem).
4. `fsync` the directory.

This guarantees: a crash never leaves a half-written `run.json` or partial artifact. Resume sees either the prior state or the new state, never a mix.

Artifact writes are additionally idempotent: if `<hash>.json` already exists, verify byte-equal (defensive — a hash collision in sha256 is catastrophic; cheap to verify) and skip the write.

## Schemas of the metadata files

> **Source of truth for trace shapes:** the trace contract locked in the design session (Athena+Socrates) on 2026-04-19. The schemas below conform to that contract. Any deviation is a bug — fix the code, not the contract.

### Directory layout note

The executor writes both **per-phase scratch files** (for resume/incremental progress) AND a **top-level rollup** (the consumer-facing trace contract):

| File | Purpose | Audience |
|---|---|---|
| `run.json` | Run-level identity + terminal status | Reviewers, UI, contract |
| `phase-runs.json` | Ordered array of phase records | Reviewers, UI, contract |
| `phases/NN-<id>/phase-run.json` | Per-phase scratch, written incrementally | Executor (resume) |
| `phases/NN-<id>/attempts/NN/*` | Per-attempt raw I/O | Debugging only |

The top-level `phase-runs.json` is regenerated from per-phase scratch on every transition; it is the authoritative trace artifact. Per-phase scratch may be deleted post-run without loss.

### `run.json` (locked contract)

```json
{
  "runId": "R_01HX...",
  "taskId": "T1-mcp-server",
  "methodologyVersion": "1",
  "startedAt": "2026-04-19T...Z",
  "endedAt":   "2026-04-19T...Z",
  "outcome":        "Succeeded|Failed|Aborted",
  "controlOutcome": "Succeeded|Failed|null",
  "pipelineTokens": { "in": 0, "out": 0 },
  "controlTokens":  { "in": 0, "out": 0 },
  "costRatio": 1.0,
  "finalArtifactHashes": {
    "requirements":    "sha256:...",
    "contract":        "sha256:...",
    "function_design": "sha256:...",
    "implementation":  "sha256:...",
    "review":          "sha256:..."
  }
}
```

**`finalArtifactHashes` is a map keyed by `phaseId`, not a positional array.** Skipped or retried phases must not break consumer joins; a viewer renders by key lookup.

**Omit-key rule:** if a phase produced no terminal artifact (skipped, errored without output, or rejected on final attempt), **omit its key entirely** from `finalArtifactHashes`. Do not emit `null` or `""`. Missing key means "no terminal artifact for that phase"; sentinel values create special cases for every consumer.

**Phase-id casing convention:** phase ids (`requirements`, `contract`, `function_design`, `implementation`, `review`) are **snake_case identifiers**, not camelCase. They appear identically as `phaseId` and `artifactType` values in `phase-runs.json` and as keys in `finalArtifactHashes`. Do not "normalize" them to camelCase — they are stable string IDs, not field names.

### `phase-runs.json` (locked contract — array)

```json
[
  {
    "phaseId": "requirements",
    "artifactType": "requirements",
    "stateTransitions": [
      { "from": null,        "to": "Pending",   "at": "..." },
      { "from": "Pending",   "to": "Running",   "at": "..." },
      { "from": "Running",   "to": "Succeeded", "at": "..." }
    ],
    "attempts": [
      {
        "attemptNumber": 1,
        "status": "Accepted|Rejected|Errored",
        "artifactHash": "sha256:...|null",
        "validatorResults": [ /* see Validator result below */ ],
        "tokens":   { "in": 0, "out": 0 },
        "latencyMs": 0,
        "model":    "claude-sonnet-4.5",
        "startedAt": "...",
        "endedAt":   "..."
      }
    ],
    "inputArtifactHashes":  ["sha256:..."],
    "outputArtifactHashes": ["sha256:..."]
  }
]
```

`stateTransitions` is **append-only**. The current state is the `to` of the last entry.

**`attempts[].artifactHash` is explicitly nullable, never omitted.** `attempts[]` is a record of what happened, including failures; every attempt object includes the field. Use `null` when the attempt produced no artifact (rejected, errored, or amend with no output). Use a `sha256:...` string when an artifact was produced — even if the attempt was later rejected, the artifact still exists in the store. `outputArtifactHashes` at the phase level lists only accepted artifacts.

### Validator result (locked contract)

Every entry in `attempt.validatorResults[]`:

```json
{
  "phase":    "structural|semantic|cross-artifact",
  "code":     "STABLE_SCREAMING_SNAKE",
  "severity": "error|warning|info",
  "blocking": true,

  "path":           "<optional jsonpath into payload>",
  "evidence":       "<optional short string>",
  "attemptNumber":  1,
  "advisoryReason": "<optional human prose>",
  "blockingReason": "<optional human prose>"
}
```

**Authoritative fields** (machine-consumed): `phase`, `code`, `severity`, `blocking`. Reason fields are advisory prose only — never parsed.

**Gate rule:** `blocking = (phase != "semantic") || (code in phase.predeclaredBlockingCodes)`. `predeclaredBlockingCodes` is the SOLE mechanism by which a semantic result becomes blocking; it only applies to `phase="semantic"`.

**Retry rule:** `shouldRetry = attempt.validatorResults.any(r => r.blocking)`. Retry budget = 3 attempts/phase, fresh session each, amendments carry only validator failures (no prior output).

### `review-summary.json` (locked contract — comparison metadata only)

```json
{
  "runId": "R_01HX...",
  "taskId": "T1-mcp-server",
  "pipelineOutcome": "Succeeded|Failed",
  "controlOutcome":  "Succeeded|Failed|null",
  "costRatio": 1.0,
  "blindReviewInputs": { "a": "blind-review-input/a.md", "b": "blind-review-input/b.md" },
  "sealedLabelMap":    "blind-review-input/.labels.sealed"
}
```

**No artifact payloads. No verdict text.** The blind review inputs are separate files; the sealed label map maps `{a,b} → {pipeline,control}` and is opened only after Socrates posts the verdict.

### Executor scratch (not part of the trace contract)

The following files are written for executor convenience and debugging. They are **not** part of the trace contract; consumers must not depend on their shapes.

- `phases/NN-<id>/phase-run.json` — per-phase incremental scratch; rolled up into top-level `phase-runs.json`.
- `phases/NN-<id>/attempts/NN/prompt.txt`, `response.raw.txt`, `response.parsed.json` — raw LLM I/O.
- `phases/NN-<id>/attempts/NN/meta.json` — same shape as one entry of `phase-runs[].attempts[]`.
- `trace.log` — NDJSON event log for live observation. Source of truth is `phase-runs.json`, not this.

## Blind review bundle (for Socrates)

After all benchmark tasks run on both pipeline and control, generate `forge-runs/blind/` with anonymized outputs:
```
blind/
├── T1/
│   ├── A.json    # could be pipeline or control, randomized
│   ├── B.json
│   └── key.json  # mapping A/B → pipeline/control; not opened until after review
├── T2/...
└── T3/...
```
`A.json` / `B.json` contain only the final implementation artifacts (or the final spec/refactor output). No traces, no token counts, no tells. Socrates reviews against the rubric blind, then `key.json` is opened to compute WIN/LOSS.

## Why no DB
- The spike's value is in the experiment, not in the infrastructure.
- File-system state is trivially inspectable, diffable, gittable, shareable.
- No migrations to write and discard.
- Crash recovery becomes "rerun and resume," needing only `run.json` + `phase-run.json` reads.
- A future production version may move to SQLite; the on-disk layout above maps 1:1 to tables (`runs`, `phase_runs`, `attempts`, `artifacts`).
