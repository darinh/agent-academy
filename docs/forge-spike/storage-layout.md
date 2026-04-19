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

### `run.json`

```json
{
  "run_id": "R_01HX...",
  "task_id": "T1-mcp-server",
  "methodology_hash": "sha256:...",
  "status": "Pending|Running|Succeeded|Failed|Aborted",
  "created_at": "2026-04-19T...Z",
  "started_at": "2026-04-19T...Z",
  "ended_at": "2026-04-19T...Z|null",
  "control_seed": "string (rng seed for tie-breaking; recorded for reproducibility)",
  "phases": [
    { "phase_id": "requirements", "dir": "01-requirements", "status": "Succeeded" },
    { "phase_id": "contract",     "dir": "02-contract",     "status": "Succeeded" },
    { "phase_id": "function_design", "dir": "03-function-design", "status": "Pending" },
    { "phase_id": "implement",    "dir": "04-implement",    "status": "Pending" },
    { "phase_id": "verify",       "dir": "05-verify",       "status": "Pending" }
  ],
  "totals": {
    "tokens_in": 0,
    "tokens_out": 0,
    "wall_clock_seconds": 0,
    "usd_estimate": 0.0
  }
}
```

### `phase-run.json`

```json
{
  "phase_id": "requirements",
  "status": "Pending|Running|Succeeded|Failed",
  "input_artifact_hashes": ["sha256:..."],
  "output_artifact_hash": "sha256:...|null",
  "attempts": [
    {
      "n": 1,
      "status": "Accepted|Rejected|Errored",
      "error_kind": "null|llm_error|parse_error|timeout|interrupted",
      "validator_failures": [
        { "validator": "structural|semantic|cross_artifact", "message": "string" }
      ],
      "artifact_hash": "sha256:...|null",
      "tokens_in": 0,
      "tokens_out": 0,
      "latency_ms": 0,
      "model": "claude-sonnet-4.5",
      "started_at": "...",
      "ended_at": "..."
    }
  ]
}
```

### `attempt/meta.json` (per-attempt)
Same shape as one entry of `attempts[]` above. Duplicated here as the source of truth for the attempt; `phase-run.json.attempts[]` is the rolled-up index, written after the attempt finalizes.

### `attempt/validator-report.json`
```json
{
  "structural": { "passed": true, "failures": [] },
  "semantic":   { "passed": true, "failures": [], "judge_model": "claude-haiku-4.5", "judge_tokens_in": 0, "judge_tokens_out": 0 },
  "cross_artifact": { "passed": true, "failures": [] }
}
```

### `review-summary.json` (terminal, written when Run reaches terminal state)
```json
{
  "run_id": "R_01HX...",
  "task_id": "T1-mcp-server",
  "verdict": "Succeeded|Failed",
  "phases": [
    { "phase_id": "requirements", "attempts_used": 1, "final_artifact_hash": "sha256:...", "tokens": 1234 },
    ...
  ],
  "implementation_files": [ "<repo-relative paths from implementation/v1 artifact>" ],
  "self_review_verdict": "pass|fail|needs_revision",
  "totals": { "tokens_in": 0, "tokens_out": 0, "wall_clock_seconds": 0, "usd_estimate": 0.0 },
  "blind_review_bundle": "blind/T1-pipeline.json"
}
```

### `trace.log` (NDJSON, append-only)
One JSON object per line. Schema:
```json
{ "ts": "...", "event": "run.started|phase.started|attempt.started|attempt.llm_call|attempt.validated|attempt.accepted|attempt.rejected|phase.succeeded|phase.failed|run.succeeded|run.failed", "details": {...} }
```
Used for live observation and post-hoc debugging. Not load-bearing — `run.json` and `phase-run.json` are the source of truth.

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
