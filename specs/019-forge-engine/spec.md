# 019 — Forge Pipeline Engine

## Purpose
Defines the standalone pipeline engine that transforms a task brief into validated, content-addressed software artifacts through a sequence of LLM-driven phases. The engine is self-contained in `AgentAcademy.Forge` — it has no dependency on the main server, database, or SignalR.

## Current Behavior

> **Status: Implemented** — Core engine, five artifact schemas, three-tier validation, disk-based content-addressed storage, and benchmark infrastructure are compiled and tested. LLM integration requires an OpenAI API key; benchmarks have not yet been executed against live models.

### Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                      PipelineRunner                          │
│  Run state machine: Pending → Running → Succeeded/Failed     │
│  Iterates methodology phases in order                        │
│  Resolves inter-phase artifact inputs                        │
│  Accumulates token counts and final artifact hashes          │
├──────────────────┬───────────────────────────────────────────┤
│                  │                                           │
│  ┌───────────────▼────────────────┐                          │
│  │        PhaseExecutor           │                          │
│  │  Per-phase attempt loop:       │                          │
│  │  Pending → Prompting →         │                          │
│  │  Generating → Validating →     │                          │
│  │  Accepted | Rejected | Errored │                          │
│  └───┬──────────┬─────────┬───────┘                          │
│      │          │         │                                  │
│  ┌───▼───┐  ┌──▼──┐  ┌───▼──────────────────┐               │
│  │Prompt │  │ LLM │  │ ValidatorPipeline     │               │
│  │Builder│  │Client│  │ Structural → Semantic │               │
│  │       │  │      │  │ → CrossArtifact       │               │
│  └───────┘  └──────┘  └──────────────────────┘               │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│  ┌─────────────────────┐  ┌────────────────────────────────┐ │
│  │  IArtifactStore      │  │  IRunStore                     │ │
│  │  Content-addressed   │  │  Run-centric persistence       │ │
│  │  DiskArtifactStore   │  │  DiskRunStore                  │ │
│  └─────────────────────┘  └────────────────────────────────┘ │
│                                                              │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │  SchemaRegistry (5 frozen schemas)                      │ │
│  │  requirements/v1 → contract/v1 → function_design/v1     │ │
│  │  → implementation/v1 → review/v1                        │ │
│  └─────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Namespace | Responsibility |
|-----------|-----------|---------------|
| `PipelineRunner` | `Execution` | Run-level state machine, phase iteration, input resolution, token rollup |
| `PhaseExecutor` | `Execution` | Per-phase attempt loop, prompt building, LLM calls, validation, artifact persistence |
| `PromptBuilder` | `Prompt` | Renders the frozen prompt envelope (system message + user message sections) |
| `ILlmClient` | `Llm` | Single-turn LLM abstraction — no retry, no streaming, no tool use |
| `OpenAiLlmClient` | `Llm` | OpenAI chat completions via `HttpClient`, JSON mode, error classification |
| `StubLlmClient` | `Llm` | Test double with configurable responses and fault injection |
| `ValidatorPipeline` | `Validation` | Three-tier cascade: Structural → Semantic → CrossArtifact, short-circuits on blocking |
| `StructuralValidator` | `Validation` | JSON Schema validation + per-schema in-artifact structural checks |
| `SemanticValidator` | `Validation` | LLM-judge (gpt-4o-mini) grades artifact against semantic rules |
| `CrossArtifactValidator` | `Validation` | Reference integrity across artifact boundaries (pure code, no LLM) |
| `AttemptResponseParser` | `Validation` | Parses LLM JSON response, extracts `body` field, wraps into `ArtifactEnvelope` |
| `IArtifactStore` | `Artifacts` | Content-addressed artifact storage (write, read, verify by SHA-256 hash) |
| `DiskArtifactStore` | `Artifacts` | Disk implementation with hash-sharded directories, atomic writes, collision detection |
| `CanonicalJson` | `Artifacts` | Deterministic JSON serialization (sorted keys, stable formatting) for content hashing |
| `IRunStore` | `Storage` | Run-centric persistence (run.json, phase snapshots, attempt files, trace log) |
| `DiskRunStore` | `Storage` | Disk implementation with atomic temp-rename writes, NDJSON trace log |
| `SchemaRegistry` | `Schemas` | Central registry of 5 frozen artifact schemas with JSON Schema bodies and semantic rules |
| `ForgeServiceExtensions` | Root | DI registration via `AddForgeEngine()` |
| `ForgeId` | Root | Run ID generation (`R_` + ULID) and parsing |

### Pipeline Execution Flow

1. **Initialize**: `PipelineRunner.ExecuteAsync()` generates a run ID (`R_` + ULID), writes `run.json`, `task.json`, and `methodology.json` to the run store.
2. **Iterate phases**: For each phase in `MethodologyDefinition.Phases` (in declared order):
   a. Resolve inputs by looking up accepted artifact hashes from prior phases via `IArtifactStore`.
   b. Delegate to `PhaseExecutor.ExecuteAsync()`.
3. **Phase attempt loop** (max attempts configurable per-phase or methodology-wide, default 3):
   a. **Prompt**: `PromptBuilder` renders the user message with task, phase, schema, inputs, and amendment notes.
   b. **Generate**: `ILlmClient.GenerateAsync()` with `gpt-4o`, temperature 0.2, JSON mode enabled.
   c. **Parse**: `AttemptResponseParser` extracts `{"body": ...}` and wraps into `ArtifactEnvelope`.
   d. **Validate**: `ValidatorPipeline` runs three tiers. If blocking findings: reject, generate amendment notes for next attempt.
   e. **Persist**: Write artifact to `IArtifactStore`, update phase-run scratch in `IRunStore`, record attempt files.
4. **Finalize**: On all phases succeeded, read accepted artifacts and populate `RunTrace.FinalArtifactHashes`. On any phase exhausting attempts, mark run as Failed.
5. **Cancellation**: Via `CancellationToken` — writes Aborted state and exits cleanly.

### State Machines

**Run lifecycle** (`RunStatus`):
```
Pending → Running → Succeeded
                  → Failed
                  → Aborted
```

**Phase lifecycle** (`PhaseRunStatus`):
```
Pending → Running → Succeeded
                  → Failed
                  → Skipped
```

**Attempt lifecycle** (`AttemptStatus`):
```
Pending → Prompting → Generating → Validating → Accepted
                                              → Rejected (retryable)
                                              → Errored (infrastructure failure)
```

### Methodology Definition

A `MethodologyDefinition` is a JSON document that declares the ordered phases:

```json
{
  "id": "forge-spike-v1",
  "description": "Five-phase software engineering pipeline",
  "max_attempts_default": 3,
  "phases": [
    {
      "id": "requirements",
      "goal": "...",
      "inputs": [],
      "output_schema": "requirements/v1",
      "instructions": "..."
    },
    {
      "id": "contract",
      "inputs": ["requirements"],
      "output_schema": "contract/v1",
      ...
    },
    {
      "id": "function_design",
      "inputs": ["requirements", "contract"],
      "output_schema": "function_design/v1",
      ...
    },
    {
      "id": "implementation",
      "inputs": ["requirements", "contract", "function_design"],
      "output_schema": "implementation/v1",
      ...
    },
    {
      "id": "review",
      "inputs": ["requirements", "contract", "function_design", "implementation"],
      "output_schema": "review/v1",
      ...
    }
  ]
}
```

Each phase declares its `inputs` (prior phase IDs), `output_schema` (schema registry key), `goal`, and `instructions`. Phases execute sequentially — there is no parallelism.

### Prompt Envelope

The `PromptBuilder` renders a frozen prompt template with these sections:

| Section | Content |
|---------|---------|
| **System message** | Constant. Instructs the LLM to produce JSON, no prose, no code fences, populate `open_questions` instead of refusing. |
| **TASK** | Task brief description. |
| **PHASE** | Phase ID and goal. |
| **OUTPUT CONTRACT** | Schema identifier, JSON Schema body, and semantic rules. |
| **INPUTS** | Prior-phase artifacts verbatim (labeled by schema and phase). |
| **INSTRUCTIONS** | Phase-specific instructions from methodology. |
| **AMENDMENT NOTES** | Blocking validator findings from the previous rejected attempt, or "(none — this is the first attempt)". |
| **RESPONSE FORMAT** | `{ "body": { ...matches schema... } }` |

Amendment notes are generated from `ValidatorResultTrace` records — only blocking findings are included. The LLM never sees its own previous output; it regenerates from scratch with failure guidance.

## Interfaces & Contracts

### Core Interfaces

```csharp
// Llm/ILlmClient.cs
public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default);
}

// Artifacts/IArtifactStore.cs
public interface IArtifactStore
{
    Task<string> WriteAsync(ArtifactEnvelope envelope, ArtifactMeta meta, CancellationToken ct = default);
    Task<ArtifactEnvelope?> ReadAsync(string hash, CancellationToken ct = default);
    Task<ArtifactMeta?> ReadMetaAsync(string hash, CancellationToken ct = default);
    Task<bool> ExistsAsync(string hash, CancellationToken ct = default);
    Task<bool> VerifyAsync(string hash, CancellationToken ct = default);
}

// Storage/IRunStore.cs
public interface IRunStore
{
    Task InitializeRunAsync(string runId, RunTrace run, TaskBrief task, MethodologyDefinition methodology, CancellationToken ct = default);
    Task WriteRunSnapshotAsync(string runId, RunTrace run, CancellationToken ct = default);
    Task<RunTrace?> ReadRunAsync(string runId, CancellationToken ct = default);
    Task WritePhaseRunScratchAsync(string runId, int phaseIndex, string phaseId, PhaseRunTrace phaseRun, CancellationToken ct = default);
    Task WritePhaseRunsRollupAsync(string runId, IReadOnlyList<PhaseRunTrace> phaseRuns, CancellationToken ct = default);
    Task<IReadOnlyList<PhaseRunTrace>?> ReadPhaseRunsRollupAsync(string runId, CancellationToken ct = default);
    Task<PhaseRunTrace?> ReadPhaseRunScratchAsync(string runId, int phaseIndex, string phaseId, CancellationToken ct = default);
    Task WriteAttemptFilesAsync(string runId, int phaseIndex, string phaseId, int attemptNumber, AttemptFiles files, CancellationToken ct = default);
    Task WriteReviewSummaryAsync(string runId, ReviewSummaryTrace summary, CancellationToken ct = default);
    Task AppendTraceEventAsync(string runId, object traceEvent, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListRunsAsync(CancellationToken ct = default);
    Task<bool> RunExistsAsync(string runId, CancellationToken ct = default);
    string GetRunDirectory(string runId);
}
```

### Domain Types

```csharp
// Models/ArtifactEnvelope.cs — Hash-bound artifact identity
public sealed record ArtifactEnvelope
{
    public required string ArtifactType { get; init; }      // e.g. "requirements"
    public required string SchemaVersion { get; init; }     // e.g. "1"
    public required string ProducedByPhase { get; init; }   // e.g. "requirements"
    public required JsonElement Payload { get; init; }      // Schema-specific body
}

// Models/ArtifactMeta.cs — Advisory metadata (not hash-bound)
public sealed record ArtifactMeta
{
    public required IReadOnlyList<string> DerivedFrom { get; init; }
    public required IReadOnlyList<string> InputHashes { get; init; }
    public required DateTime ProducedAt { get; init; }
    public required int AttemptNumber { get; init; }
}

// Models/RunTrace.cs — On-disk run summary
public sealed record RunTrace
{
    public required string RunId { get; init; }
    public required string TaskId { get; init; }
    public required string MethodologyVersion { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public required string Outcome { get; init; }           // "pending", "running", "succeeded", "failed", "aborted"
    public string? ControlOutcome { get; init; }
    public required TokenCount PipelineTokens { get; init; }
    public required TokenCount ControlTokens { get; init; }
    public double? CostRatio { get; init; }
    public required Dictionary<string, string> FinalArtifactHashes { get; init; }
}

// Models/Methodology.cs — Pipeline definition
public sealed record MethodologyDefinition
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public int MaxAttemptsDefault { get; init; } = 3;
    public required IReadOnlyList<PhaseDefinition> Phases { get; init; }
}

public sealed record PhaseDefinition
{
    public required string Id { get; init; }                // Snake_case identifier
    public required string Goal { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public required string OutputSchema { get; init; }      // e.g. "requirements/v1"
    public required string Instructions { get; init; }
    public int? MaxAttempts { get; init; }                  // Per-phase override
}
```

### Artifact Schema Registry

Five frozen schemas are registered at startup:

| Schema ID | Artifact Type | Top-level Fields |
|-----------|--------------|-----------------|
| `requirements/v1` | Requirements | `task_summary`, `user_outcomes`, `functional_requirements[]`, `non_functional_requirements[]`, `out_of_scope[]`, `open_questions[]` |
| `contract/v1` | Contract | `interfaces[]`, `data_shapes[]`, `invariants[]`, `examples[]` |
| `function_design/v1` | Function Design | `components[]`, `data_flow[]`, `error_handling[]`, `deferred_decisions[]` |
| `implementation/v1` | Implementation | `files[]`, `build_command`, `test_command`, `notes` |
| `review/v1` | Review | `verdict`, `summary`, `checks[]`, `defects[]`, `improvements_for_next_iteration[]` |

Each `SchemaEntry` carries:
- `SchemaBodyJson` — JSON Schema used for structural validation and prompt rendering.
- `SemanticRules` — Natural-language rubric used by the semantic validator and included in prompts.

### Three-Tier Validation Cascade

The `ValidatorPipeline` runs tiers sequentially and short-circuits at the first tier with blocking findings:

**Tier 1 — Structural** (`StructuralValidator`):
- JSON Schema validation via `JsonSchema.Net`.
- Per-schema in-artifact checks:
  - Requirements: unique IDs, no dangling `outcome_ids`.
  - Contract: unique interface names.
  - Function Design: unique components, no dangling `depends_on`, DAG cycle detection, valid flow refs, valid error handling refs.
  - Implementation: no absolute paths, no `..` traversal.
  - Review: verdict/critical-defect consistency.
- Error codes: `TYPE_MISMATCH`, `MISSING_REQUIRED_FIELD`, `ARRAY_TOO_SHORT`, `DUPLICATE_ID`, `DUPLICATE_VALUE`, `DANGLING_REFERENCE`, `DEPENDENCY_CYCLE`, `ABSOLUTE_PATH`, `PATH_TRAVERSAL`, `VERDICT_DEFECT_MISMATCH`, `ADDITIONAL_PROPERTIES`, `INVALID_ENUM_VALUE`, `PATTERN_MISMATCH`, `STRING_TOO_LONG`, `SCHEMA_VALIDATION_FAILED`, `SCHEMA_LOAD_FAILED`.

**Tier 2 — Semantic** (`SemanticValidator`):
- LLM judge using `gpt-4o-mini`, temperature 0, JSON mode.
- Grades the artifact against the schema's semantic rules.
- Empty semantic rules → tier skipped entirely.
- LLM failures → blocking `SEMANTIC_LLM_FAILED`.
- Parse failures → blocking `SEMANTIC_PARSE_FAILED`.
- Only failed findings are emitted; passed checks and warnings are non-blocking.

**Tier 3 — Cross-Artifact** (`CrossArtifactValidator`):
- Pure code, no LLM — checks reference integrity across artifacts.
- Contract → Requirements: `satisfies_fr_ids` and `examples[].fr_id` resolve.
- Function Design → Contract: `implements[]` resolve to contract interface names.
- Implementation → Function Design: `implements_component_ids` resolve.
- Review → Upstream: `checks[].target_id` resolves based on `kind`.

### Validator Finding Contract

```csharp
public sealed record ValidatorResultTrace
{
    public required string Phase { get; init; }         // "structural" | "semantic" | "cross-artifact"
    public required string Code { get; init; }          // SCREAMING_SNAKE stable code
    public required string Severity { get; init; }      // "error" | "warning" | "info"
    public required bool Blocking { get; init; }
    public string? Path { get; init; }                  // JSONPath into artifact payload
    public string? Evidence { get; init; }
    public required int AttemptNumber { get; init; }
    public string? AdvisoryReason { get; init; }        // Never parsed
    public string? BlockingReason { get; init; }        // Never parsed
}
```

### Content-Addressed Storage

Artifacts are identified by SHA-256 hash of their canonical JSON representation:

1. **Canonical JSON** (`CanonicalJson`): Object keys sorted recursively, arrays preserve order, whitespace stripped. `sha256:` prefix on all stored hashes.
2. **DiskArtifactStore**: Sharded by first two hex characters of the hash (e.g., `artifacts/ab/abcdef...json`). Writes are atomic (temp file + rename). Advisory `.meta.json` stored alongside. Idempotent on identical content; detects hash collision on different content.
3. **Immutability**: Once written, an artifact is never modified or deleted. `VerifyAsync` re-hashes on read to detect corruption.

### Run Storage Layout

```
forge-runs/
├── artifacts/                          # Content-addressed artifact store
│   └── ab/                             # Hash-sharded directories
│       ├── abcdef....json              # ArtifactEnvelope
│       └── abcdef....meta.json         # ArtifactMeta (advisory)
└── runs/
    └── R_01JWEX.../                    # Run directory (ULID-sorted)
        ├── run.json                    # RunTrace (atomic snapshots)
        ├── task.json                   # TaskBrief (frozen)
        ├── methodology.json            # MethodologyDefinition (frozen)
        ├── phase-runs.json             # PhaseRunTrace[] rollup
        ├── review-summary.json         # ReviewSummaryTrace
        ├── trace.log                   # NDJSON event log
        └── phases/
            └── 01-requirements/        # Per-phase directory (1-indexed, zero-padded)
                ├── phase-run.json      # PhaseRunTrace scratch
                └── attempts/
                    └── 01/             # Per-attempt directory (1-indexed, zero-padded)
                        ├── prompt.txt
                        ├── response.raw.txt
                        ├── response.parsed.json
                        ├── meta.json
                        └── validator-report.json
```

Snapshot writes are atomic (temp file → rename). `run.json` is snapshotted after every state transition for crash recovery. `trace.log` is append-only NDJSON (non-atomic appends — last line may truncate on crash).

### LLM Abstraction

```csharp
public sealed record LlmRequest
{
    public required string SystemMessage { get; init; }
    public required string UserMessage { get; init; }
    public required string Model { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 8192;
    public int TimeoutSeconds { get; init; } = 120;
    public bool JsonMode { get; init; } = true;
}

public sealed record LlmResponse
{
    public required string Content { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required string Model { get; init; }
    public required long LatencyMs { get; init; }
}
```

**Design decisions**:
- Deliberately thin: no retry logic, no streaming, no tool use.
- Retries are handled at the attempt level by `PhaseExecutor`.
- `LlmClientException` wraps infrastructure failures with `LlmErrorKind` (Transient, Timeout, Authentication, BadRequest, MalformedResponse, Unknown).
- `OpenAiLlmClient` uses raw `HttpClient` (no SDK dependency). Reads `OPENAI_API_KEY` from environment.
- `StubLlmClient` supports fixed responses and configurable fault injection for testing.

### DI Registration

```csharp
// Usage:
services.AddForgeEngine(forgeRunsRoot: "/path/to/forge-runs");

// Registers (all singletons):
// IArtifactStore → DiskArtifactStore
// IRunStore → DiskRunStore
// SchemaRegistry, PromptBuilder
// StructuralValidator, SemanticValidator, CrossArtifactValidator, ValidatorPipeline
// PhaseExecutor, PipelineRunner
```

Default `forgeRunsRoot` is `./forge-runs` under the current working directory.

### Run Identity

`ForgeId.NewRunId()` generates `R_` + ULID. ULID encoding ensures:
- Time-ordered (sorted by creation time).
- Globally unique without coordination.
- Filesystem-safe (no special characters).

### Benchmark Infrastructure

Three benchmark tasks are defined as frozen `TaskBrief` constants in `BenchmarkTasks`:

| Task | Description | Complexity |
|------|------------|-----------|
| T1 | Build a small MCP server with `code_search` and `file_read` tools | Implementation-heavy |
| T2 | Write an 800–1200 word technical spec for `NotificationManager` | Document generation |
| T3 | Adversarial refactor of `NotificationManager.cs` extracting three patterns | Code modification with constraints |

Acceptance criteria for each task live in `forge-spike/benchmarks/T{N}-acceptance.md` (human reference, not parsed by engine).

A standalone console runner (`AgentAcademy.Forge.Benchmarks`) executes all three tasks against the pipeline with a live `OpenAiLlmClient`.

## Invariants

1. **Content identity**: An artifact's hash is `sha256(canonical_json(envelope))`. The same logical content always produces the same hash. Different content never produces the same hash (collision detection raises an exception).
2. **Immutable artifacts**: Once written to the artifact store, an artifact is never modified or deleted.
3. **Phase ordering**: Phases execute in methodology-declared order. A phase's inputs are only the accepted artifacts from its declared input phases.
4. **Validation cascade**: Tiers execute in order (Structural → Semantic → CrossArtifact). The pipeline short-circuits at the first tier with any blocking finding.
5. **Atomic persistence**: Snapshot and JSON file writes use temp-file-then-rename. A crash mid-write never produces a corrupted snapshot. The `trace.log` uses append semantics (`File.AppendAllTextAsync`) — a crash during append may truncate the last NDJSON line but won't corrupt prior entries.
6. **No cross-phase state leakage**: The LLM sees only the frozen prompt (task + phase + schema + inputs + amendments). It has no memory of prior phases or attempts.
7. **Amendment isolation**: On retry, the LLM receives only the blocking validator findings — never its own previous output.
8. **Methodology is frozen per run**: The `methodology.json` written at run start is the authoritative definition for the entire run. Schema registry contents are baked in at compile time.
9. **Severity normalization**: Unknown severity values from the semantic validator are treated as `error` and marked blocking.
10. **Advisory metadata is non-authoritative**: Missing or corrupted `.meta.json` does not affect artifact identity or pipeline correctness.

## Known Gaps

1. **Model configurability**: `PhaseExecutor` hardcodes `gpt-4o` for generation; `SemanticValidator` hardcodes `gpt-4o-mini` for judging. Model selection is not configurable per-phase or per-methodology. Deferred to post-spike.
2. **No live benchmark results**: Benchmarks require `OPENAI_API_KEY`; results have not been collected yet.
3. **Intent fidelity**: The current pipeline verifies internal consistency but not fidelity to the original task intent. The spike findings document proposes a `source-intent` artifact and terminal fidelity phase — not yet implemented.
4. **No cost tracking**: Token counts are accumulated but no cost calculation or budget enforcement exists.
5. **No parallelism**: Phases execute sequentially. The methodology model supports inputs that could enable parallel execution, but the runner doesn't exploit it.
6. **No crash recovery**: State snapshots are written for potential crash recovery, but no resume-from-snapshot logic exists yet.
7. **Control arm not implemented**: `RunTrace` includes `ControlOutcome` and `ControlTokens` fields for A/B benchmark comparison, but no control executor exists.
8. **Schema evolution**: All schemas are frozen at v1. No migration or versioning strategy exists for schema changes.

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2026-04-20 | Initial spec written from implemented code | Handoff from forge spike sessions |
| 2026-04-20 | Forge engine core (Layer 1) | `feat/forge-engine-core` |
| 2026-04-20 | Execution foundation — LLM abstraction, prompt builder, schemas (Layer 2a) | `feat/forge-execution-foundation` |
| 2026-04-20 | Validation pipeline and phase executor (Layer 2b) | `feat/forge-phase-executor` |
| 2026-04-20 | PipelineRunner, OpenAI client, benchmark infrastructure (Layer 3) | `feat/forge-pipeline-runner` |
