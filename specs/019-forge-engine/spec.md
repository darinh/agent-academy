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
│  Accumulates token counts, costs, and final artifact hashes  │
│  Enforces optional budget (aborts if exceeded)               │
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
| `SemanticValidator` | `Validation` | LLM-judge (configurable, default gpt-4o-mini) grades artifact against semantic rules |
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
2. **Schedule waves**: `BuildExecutionWaves` computes a topological schedule from phase dependency declarations. Phases whose inputs are all satisfied by prior waves execute concurrently within a wave.
3. **Execute waves**: For each wave:
   a. All phases in the wave execute concurrently via `Task.WhenAll`.
   b. Each phase resolves inputs by looking up accepted artifact hashes from prior phases.
   c. Each phase delegates to `PhaseExecutor.ExecuteAsync()`.
   d. After all phases in the wave complete, results are processed in methodology order (deterministic trace ordering).
   e. If any phase in the wave failed, the pipeline stops — no subsequent waves execute.
   f. Budget is checked between waves; within a wave, all phases share the pre-wave budget snapshot.
4. **Phase attempt loop** (max attempts configurable per-phase or methodology-wide, default 3):
   a. **Prompt**: `PromptBuilder` renders the user message with task, phase, schema, inputs, and amendment notes.
   b. **Generate**: `ILlmClient.GenerateAsync()` with configurable model (see Model Configuration below), temperature 0.2, JSON mode enabled.
   c. **Parse**: `AttemptResponseParser` extracts `{"body": ...}` and wraps into `ArtifactEnvelope`.
   d. **Validate**: `ValidatorPipeline` runs three tiers. If blocking findings: reject, generate amendment notes for next attempt.
   e. **Persist**: Write artifact to `IArtifactStore`, update phase-run scratch in `IRunStore`, record attempt files.
5. **Finalize**: On all waves completed successfully, read accepted artifacts and populate `RunTrace.FinalArtifactHashes`. On any phase exhausting attempts, mark run as Failed.
6. **Cancellation**: Via `CancellationToken` — writes Aborted state and exits cleanly.

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
  "model_defaults": {
    "generation": "gpt-4o",
    "judge": "gpt-4o-mini"
  },
  "phases": [
    {
      "id": "requirements",
      "goal": "...",
      "inputs": [],
      "output_schema": "requirements/v1",
      "instructions": "...",
      "model": null,
      "judge_model": null
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

Each phase declares its `inputs` (prior phase IDs), `output_schema` (schema registry key), `goal`, and `instructions`. Phases that share no dependency chain execute in parallel via wave scheduling — see Parallel Execution below.

### Model Configuration

Model selection uses a three-tier cascade: **phase override → methodology default → hardcoded fallback**.

| Purpose | Phase field | Methodology field | Fallback |
|---------|------------|-------------------|----------|
| Generation (artifact production) | `model` | `model_defaults.generation` | `"gpt-4o"` |
| Semantic judging (validation) | `judge_model` | `model_defaults.judge` | `"gpt-4o-mini"` |

**Resolution logic** (in `PhaseExecutor.ResolveModel`): the first non-null, non-empty, non-whitespace value wins.

```
generation_model = phase.Model ?? methodology.ModelDefaults?.Generation ?? "gpt-4o"
judge_model = phase.JudgeModel ?? methodology.ModelDefaults?.Judge ?? "gpt-4o-mini"
```

**Threading**: `PhaseExecutor` resolves both models at the start of each attempt and passes `judgeModel` through `ValidatorPipeline.ValidateAsync` → `SemanticValidator.ValidateAsync`. All new parameters are optional with backward-compatible defaults — existing code that omits them gets the same behavior as before.

**Backward compatibility**: Methodology JSON files without `model_defaults` or per-phase model fields are fully supported — all fields are optional with null defaults, and the fallback values match the previously-hardcoded models.

### Cost Tracking

The engine tracks LLM usage costs at per-attempt, per-phase, and per-run granularity. Cost calculation covers both generation and semantic judge calls.

**Pricing** (`CostCalculator`): Maps model IDs to USD per million tokens (input and output). Ships with default prices for common OpenAI models. Case-insensitive lookup. Unknown models return $0 cost (but see budget enforcement below).

**Per-attempt cost**: Each `AttemptTrace` includes:
- `tokens` / `model` — generation call usage (existing)
- `judgeTokens` / `judgeModel` — semantic validator call usage (new)
- `cost` — total USD cost for this attempt (generation + judge)

**Per-run cost**: `RunTrace.pipelineCost` aggregates all attempt costs across all phases. Token totals (`pipelineTokens`) now include both generation and judge tokens.

**Budget enforcement** (`MethodologyDefinition.budget`):
- Optional `budget` field (decimal, USD) on the methodology definition
- When set, `CostCalculator.ValidatePricingForBudget()` runs at pipeline start — if any resolved model (generation or judge) across all phases is unpriced, the run fails immediately with `InvalidOperationException`
- Budget is checked **between attempts** (PhaseExecutor stops retrying if budget exhausted) and **between phases** (PipelineRunner stops the pipeline)
- When budget is exceeded, the run outcome is `"aborted"` with `abortReason: "budget_exceeded"`
- Budget enforcement is opt-in: `budget: null` means no limit

**Methodology JSON additions**:
```json
{
  "budget": 5.00
}
```

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
    public string? ControlOutcome { get; init; }            // "structurally_valid", "structurally_invalid", "failed", or null
    public required TokenCount PipelineTokens { get; init; }
    public required TokenCount ControlTokens { get; init; }
    public decimal? PipelineCost { get; init; }
    public decimal? ControlCost { get; init; }
    public double? CostRatio { get; init; }                 // PipelineCost / ControlCost (>1 = pipeline costs more)
    public string? ControlArtifactHash { get; init; }       // sha256:... prefixed, for A/B comparison
    public string? AbortReason { get; init; }               // e.g. "budget_exceeded"
    public required Dictionary<string, string> FinalArtifactHashes { get; init; }
}

// Models/Methodology.cs — Pipeline definition
public sealed record MethodologyDefinition
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public int MaxAttemptsDefault { get; init; } = 3;
    public ModelDefaults? ModelDefaults { get; init; }
    public decimal? Budget { get; init; }
    public ControlConfig? Control { get; init; }            // Opt-in A/B benchmarking
    public required IReadOnlyList<PhaseDefinition> Phases { get; init; }
}

/// Control arm configuration for A/B benchmarking.
public sealed record ControlConfig
{
    public required string TargetSchema { get; init; }      // e.g. "implementation/v1"
    public string? Model { get; init; }                     // Falls back to ModelDefaults.Generation → "gpt-4o"
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

Seven schemas are registered at startup. The five pipeline schemas produce artifacts during normal methodology execution. The two engine-internal schemas are used by the fidelity subsystem.

**Pipeline schemas:**

| Schema ID | Artifact Type | Top-level Fields |
|-----------|--------------|-----------------|
| `requirements/v1` | Requirements | `task_summary`, `user_outcomes`, `functional_requirements[]`, `non_functional_requirements[]`, `out_of_scope[]`, `open_questions[]` |
| `contract/v1` | Contract | `interfaces[]`, `data_shapes[]`, `invariants[]`, `examples[]` |
| `function_design/v1` | Function Design | `components[]`, `data_flow[]`, `error_handling[]`, `deferred_decisions[]` |
| `implementation/v1` | Implementation | `files[]`, `build_command`, `test_command`, `notes` |
| `review/v1` | Review | `verdict`, `summary`, `checks[]`, `defects[]`, `improvements_for_next_iteration[]` |

**Engine-internal schemas** (versioned with the engine, not per-methodology):

| Schema ID | Artifact Type |
|-----------|--------------|
| `source_intent/v1` | Source Intent |
| `fidelity/v1` | Fidelity |

Each `SchemaEntry` carries:
- `SchemaBodyJson` — JSON Schema used for structural validation and prompt rendering.
- `SemanticRules` — Natural-language rubric used by the semantic validator and included in prompts.
- `Status` — Lifecycle status (Active, Deprecated, Retired). See Schema Evolution below.
- `IsInternal` — Whether the schema is engine-internal (not methodology-configurable).

### Schema Evolution

Schemas follow a versioning strategy that leverages the content-addressed artifact store's immutability.

**Principles:**

1. **Immutable artifacts, evolving schemas.** Stored artifacts are content-addressed and never change. Schema version is recorded in the `ArtifactEnvelope`. Old artifacts remain valid against their original schema version forever.
2. **Methodology pins versions.** Each methodology explicitly declares which schema version each phase uses via `output_schema` (e.g. `"requirements/v1"`). There is no implicit "latest" resolution.
3. **Multi-version registry.** `SchemaRegistry` holds all schema versions simultaneously. Both `GetSchema("requirements/v1")` and `GetSchema("requirements/v2")` work when v2 exists.
4. **No runtime migration.** Artifacts are never transformed between schema versions. To produce v2 output, re-run the phase with a v2-targeting methodology.
5. **Version-dispatched validation.** Both `StructuralValidator` (in-artifact reference checks) and `CrossArtifactValidator` dispatch by full schema ID (`type/vN`), not just artifact type. When a v2 schema is added, new validation cases are added alongside existing ones.
6. **Internal schemas are engine-coupled.** `source_intent` and `fidelity` schemas are versioned with the engine itself, not per-methodology. Their version is hardcoded in `SourceIntentGenerator` and `FidelityExecutor`.

**Schema lifecycle** (`SchemaStatus` enum):

| Status | New Runs | Resume | Pipeline Behavior |
|--------|----------|--------|------------------|
| **Active** | ✅ Allowed | ✅ Allowed | Normal |
| **Deprecated** | ⚠️ Warning logged at run start | ✅ Allowed | Pipeline starts, warns operator to migrate |
| **Retired** | ❌ Rejected at run start | ✅ Allowed | `PipelineRunner.ExecuteAsync` throws; `ResumeAsync` allows (historical run) |

`PipelineRunner` validates all schema references in the methodology before starting a new run. `ResumeAsync` validates with `isNewRun: false`, allowing deprecated and retired schemas for historical run resumption.

**Adding a new schema version (checklist):**

1. Create a new schema class (e.g. `RequirementsV2.cs`) with the new `SchemaEntry`, schema body, and semantic rules.
2. Register it in `SchemaRegistry` constructor — both v1 and v2 coexist.
3. Add version-dispatched cases in `StructuralValidator.ValidateInArtifactReferences` and `CrossArtifactValidator.Validate` for the new schema ID.
4. Optionally deprecate the old version by setting `Status = SchemaStatus.Deprecated` on the v1 entry.
5. Create a new methodology JSON that references the new schema version in `output_schema`.
6. Old methodologies referencing v1 continue to work unchanged.

**Future work (not yet implemented):**
- Read-time adapters/projections for querying artifacts across schema versions (query layer, not storage migration).
- Consumer-specific compatibility rules (modeling which consumers can read which schema versions) — deferred until the first cross-version dependency arises.

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

### Crash Recovery

`PipelineRunner.ResumeAsync(runId)` resumes a run that was interrupted (crash, process exit). Reads persisted snapshots to determine which phases completed, reconstructs accumulated tokens/cost from **all** persisted attempts (including in-progress phases), and continues from the first non-succeeded phase.

**Algorithm:**
1. Read `run.json`. If outcome is terminal (succeeded/failed/aborted) → return as-is (idempotent).
2. Read `task.json` and `methodology.json` (frozen at run start).
3. Scan ALL per-phase scratch files (not just up to the first gap — wave execution means later phases may have completed while earlier ones crashed):
   - **Succeeded** phases: read accepted artifact from store, accumulate tokens/cost.
   - **Failed** phases: record terminal state for run classification.
   - **Running/Pending** phases: crashed mid-execution. Tokens/cost from persisted attempts are accumulated for budget accuracy, but the phase is re-executed.
   - **Missing** scratch file: phase never started, will be scheduled in wave planning.
4. Restore source-intent artifact: if `runTrace.SourceIntentArtifactHash` is persisted, reload from artifact store and seed into accepted artifacts. This ensures resumed runs maintain fidelity grounding.
5. Budget guard: if accumulated cost ≥ budget before resuming, abort immediately.
6. Build wave schedule from remaining (non-completed) phases and continue execution with pre-populated artifact map and token/cost accumulators.
7. If pipeline succeeded and control arm has no outcome, re-run control arm.
8. Append `run_resumed` event to `trace.log` with completed phase IDs, pending phase count, and accumulated cost.

**Invariants:**
- Idempotent: calling `ResumeAsync` on a terminal run is a no-op.
- Budget-correct: accumulated cost from all persisted attempts (including crashed phases) counts toward budget.
- Inconsistency-safe: if a succeeded phase's artifact is missing from the artifact store, `ResumeAsync` throws `InvalidOperationException` (store corruption, not a resumable state).

**Known limitation:** When a "running" phase is re-executed on resume, its old attempt data (prompt.txt, response files) remains on disk in the attempt directories, but the phase scratch file (`phase-run.json`) is overwritten with the new execution's trace. The run-level `PipelineTokens` and `PipelineCost` correctly include the cost of those pre-crash attempts, but the `phase-runs.json` rollup does not — there is a minor audit discrepancy for resumed runs.

### Parallel Execution

`PipelineRunner` uses wave-based scheduling to execute independent phases concurrently. Parallelism is automatic — the runner derives the execution schedule from the phase dependency graph declared in `Inputs`.

**Wave scheduling** (`BuildExecutionWaves`, internal static):
Uses a Kahn's algorithm variant. Repeatedly finds phases whose inputs are all in the "available" artifact set, groups them as a wave, adds their IDs to "available", and repeats until all phases are scheduled.

```
Example — standard 5-phase methodology (strict chain):
  Wave 0: [requirements]
  Wave 1: [contract]
  Wave 2: [function_design]
  Wave 3: [implementation]
  Wave 4: [review]
  → Sequential execution (identical to pre-parallelism behavior)

Example — diamond dependency (A → [B, C] → D):
  Wave 0: [A]
  Wave 1: [B, C]  ← concurrent
  Wave 2: [D]
  → B and C execute in parallel via Task.WhenAll
```

**Execution semantics:**
- **Within a wave**: All phases execute concurrently via `Task.WhenAll`. Each phase receives an immutable snapshot of accepted artifacts from prior waves.
- **Between waves**: Results are merged single-threaded. Budget is checked. If any phase in the wave failed, the pipeline fails — no subsequent waves execute.
- **Failure handling**: No fail-fast within a wave. All phases run to completion (avoids cancellation/abort misclassification). The "let the wave finish" approach keeps the run state machine unambiguous.
- **Budget enforcement**: Budget is checked between waves, not within. All phases in a wave share the pre-wave budget snapshot. Total cost may exceed budget by at most one wave's worth of spend.
- **Trace ordering**: Results within a wave are sorted by original methodology index for deterministic `phase-runs.json` output.
- **Single-phase optimization**: Waves with exactly one phase skip `Task.WhenAll` overhead and execute directly.

**Source-intent injection**: When fidelity is configured, `source_intent` is added to the available artifact set before wave planning and injected into the first methodology phase's declared inputs. This ensures the first phase is schedulable even when it declares a `source_intent` dependency.

**Resume compatibility**: `RebuildStateFromSnapshotsAsync` scans ALL methodology phases (not just up to the first gap) to handle partial wave completion correctly. Completed phase IDs are passed to `BuildExecutionWaves` which skips them, scheduling only remaining phases.

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
// CostCalculator
// PhaseExecutor, ControlExecutor, PipelineRunner
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

### Control Arm (A/B Benchmarking)

The control arm is a single-shot LLM baseline that produces the same artifact type as the pipeline but without multi-phase scaffolding. This enables measuring whether the multi-phase pipeline improves output quality over a single LLM call, and at what cost overhead.

**Configuration** — opt-in via `MethodologyDefinition.Control`:

```json
{
  "id": "my-methodology-v1",
  "control": {
    "target_schema": "implementation/v1",
    "model": "gpt-4o"
  },
  "phases": [...]
}
```

**Execution flow**:
1. Pipeline phases run to completion (or failure).
2. If `Control` is configured and the outcome is NOT "aborted", `ControlExecutor` runs.
3. Control builds a single-shot prompt with: the same system message as the pipeline, the target schema body + semantic rules, and the task description — but NO upstream artifacts and NO amendment loop.
4. The LLM response is parsed and structurally validated. Semantic validation is intentionally skipped to keep the control's token count clean for cost comparison.
5. Control outcome is one of: `structurally_valid`, `structurally_invalid`, or `failed` (LLM error).
6. Results are merged into `RunTrace`: `ControlOutcome`, `ControlTokens`, `ControlCost`, `ControlArtifactHash`, and `CostRatio` (pipeline cost / control cost).

**Design decisions**:
- Control runs AFTER the pipeline (sequential, not parallel) — simpler, avoids rate limits.
- Single-shot, no retries — the control is a "dumb baseline" for comparison.
- Structural validation only — semantic validation uses LLM tokens that would distort the cost comparison.
- Budget enforcement does NOT include the control arm cost. The control is a benchmarking tool, not a production path.
- Control is skipped when the pipeline aborts (cancellation or budget exhaustion) — an aborted run isn't a meaningful baseline.
- Model resolution: control `model` → methodology `model_defaults.generation` → `"gpt-4o"`.
- Control artifacts are persisted to the same content-addressed store for later manual comparison, with `producedByPhase: "control"`.

**Prompt parity**: The control prompt includes the same schema body and semantic rules as the pipeline phases. Only the upstream artifacts and amendment loop are removed. This isolates the variable being tested (multi-phase orchestration vs. single-shot) rather than testing a weaker prompt.

### Intent Fidelity

The intent fidelity system detects semantic drift between the human's original request and the pipeline's final output. It addresses the "telephone game" problem where each phase faithfully implements the prior phase's output, but collectively the pipeline diverges from the human's actual intent.

**Configuration** — opt-in via `MethodologyDefinition.Fidelity`:

```json
{
  "id": "my-methodology-v1",
  "fidelity": {
    "target_phase": "implementation",
    "model": "gpt-4o",
    "judge_model": "gpt-4o-mini",
    "max_attempts": 3
  },
  "phases": [...]
}
```

**Two new schemas**:

- `source_intent/v1` — structured extraction of the human's original ask: verbatim task brief, acceptance criteria, explicit constraints, examples, counter-examples, and preferred approach. Created once at run start (immutable).
- `fidelity/v1` — terminal fidelity verdict: `overall_match` (PASS/FAIL/PARTIAL), per-criterion results, and drift detections from a closed taxonomy.

**Drift taxonomy (CLOSED — 5 codes)**:

| Code | Severity | Description |
|------|----------|-------------|
| `OMITTED_CONSTRAINT` | Blocking | A constraint from source intent was dropped |
| `CONSTRAINT_WEAKENED` | Blocking | An explicit constraint was weakened |
| `INVENTED_REQUIREMENT` | Advisory | A requirement appears with no basis in source intent |
| `SCOPE_BROADENED` | Advisory | Output covers more than what was asked |
| `SCOPE_NARROWED` | Advisory | Output covers less than what was asked |

Adding a 6th code requires a methodology version bump. The `DriftCode` enum enforces the closed taxonomy at compile time.

**Execution flow**:
1. If `Fidelity` is configured and `SourceIntentGenerator` is registered, source-intent artifact is generated before pipeline phases.
2. `SourceIntentGenerator` uses a single-shot LLM call with structural validation + retry. A verbatim check verifies the `task_brief` field matches the original `TaskBrief.Description` (≥80% word overlap after whitespace normalization).
3. Source-intent is auto-injected as an input to the first methodology phase (requirements), providing grounding for downstream phases.
4. Pipeline phases execute as normal.
5. After the pipeline succeeds, `FidelityExecutor` runs: compares source-intent against the target phase output (typically `implementation`), with **zero access to intermediate artifacts**.
6. The fidelity executor enforces a hard input constraint: exactly 2 inputs (source_intent + target output), validated by both phase ID and artifact type. Violation produces an immediate failure.
7. Fidelity results are merged into `RunTrace`: `FidelityOutcome`, `FidelityArtifactHash`, `SourceIntentArtifactHash`, `DriftCodes`, `FidelityTokens`, `FidelityCost`.

**Design decisions**:
- Intent fidelity is a **phase**, not a validator — it runs as a separate `PhaseRun` through `PhaseExecutor` with full attempt/retry loop and three-tier validation.
- The fidelity phase has **zero access to intermediate artifacts** (requirements, contract, function_design). This prevents the context pollution the phase exists to detect.
- Source-intent cost counts toward fidelity totals (separate from pipeline totals). It is part of the production path, not benchmarking.
- Pipeline `Outcome` stays "succeeded" even when fidelity reports "fail". The pipeline DID produce correct artifacts per its validators — fidelity is a separate quality signal. Consumers check `FidelityOutcome` explicitly.
- Source-intent generation is single-shot with structural validation only (no semantic). The schema forces verbatim preservation of the task brief, so the fidelity phase always has access to the raw human request.
- Model resolution: fidelity `model` → methodology `model_defaults.generation` → `"gpt-4o"`.
- Source-intent generation failure is non-fatal — the pipeline continues without fidelity checking.
- Fidelity phase is skipped when the pipeline fails or aborts.

### Seeded-Defect Benchmarks

Controlled test cases with intentionally injected drift for measuring fidelity detection accuracy. Required to validate the LLM-judge hypothesis per the spike falsifiability statement.

**Components:**
- `SeededDefect` (record in `Models/SeededDefect.cs`) — pairs a source-intent artifact with a drifted output artifact and declares the expected fidelity verdict (ground truth).
- `SeededDefectCatalog` (`Execution/SeededDefectCatalog.cs`) — frozen catalog of 7 cases covering all 5 drift codes.
- `SeededDefectRunner` (`Execution/SeededDefectRunner.cs`) — runs cases through `FidelityExecutor`, compares verdicts to ground truth, computes detection rates.
- `SeededDefectReport` (record in `Models/SeededDefect.cs`) — aggregated results with per-category and per-code metrics.

**Catalog cases:**

| ID | Drift Code | Category | Expected Match | Description |
|----|-----------|----------|---------------|-------------|
| SD-OMIT | OMITTED_CONSTRAINT | blocking | FAIL | Per-IP rate limiting dropped |
| SD-INVENT | INVENTED_REQUIREMENT | advisory | PARTIAL | Web dashboard added to CLI calculator |
| SD-BROAD | SCOPE_BROADENED | advisory | PARTIAL | PDF export, syntax highlighting added to Markdown converter |
| SD-NARROW | SCOPE_NARROWED | advisory | PARTIAL | Auth and caching omitted from HTTP client |
| SD-WEAKEN | CONSTRAINT_WEAKENED | blocking | FAIL | Hard 429 rejection weakened to warning log |
| SD-CLEAN | (none) | clean | PASS | Faithful stack implementation |
| SD-MULTI | OMITTED_CONSTRAINT + SCOPE_BROADENED | diagnostic | FAIL | Multiple drift in schema validator |

**Metrics:**
- **Blocking detection rate**: fraction of blocking defects correctly detected (threshold: ≥80%).
- **Advisory detection rate**: fraction of advisory defects where drift codes were detected (threshold: ≥60%).
- **Overall match accuracy**: fraction of threshold-bearing cases (blocking + advisory + clean) where `overall_match` was correct.
- **False positive rate**: fraction of clean cases where drift was incorrectly reported.
- **Per-code recall**: per drift code detection rate (reported, not currently thresholded).
- **Inconclusive count**: cases where the LLM failed to produce a verdict (infrastructure failure).

**Design decisions:**
- Artifacts are pre-fabricated and frozen — only the fidelity LLM judge is called. This isolates fidelity detection accuracy from pipeline quality.
- The `SeededDefectRunner` uses `FidelityExecutor` directly, not `PipelineRunner`, since we're testing fidelity detection specifically.
- The diagnostic category (multi-drift) is excluded from threshold calculations because multi-code cases make the denominator ambiguous.
- Inconclusive results (LLM failure, parse error) count as benchmark failures — they are excluded from rates but the count is reported.
- Implementation artifacts use placeholder `implements_component_ids` since cross-artifact validation against `function_design` is not what we're testing.

**Running seeded-defect benchmarks:**
```bash
dotnet run --project src/AgentAcademy.Forge.Benchmarks -- --seeded-defects
```

Returns exit code 0 if both thresholds are met, 1 otherwise.

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
11. **Fidelity input isolation**: The fidelity phase executor rejects any input set that is not exactly {source_intent, target_output} by artifact type. This is enforced at runtime by `FidelityExecutor.ValidateInputs` and prevents context pollution from intermediate artifacts.
12. **Drift taxonomy is closed**: The 5 drift codes are exhaustive, defined by the `DriftCode` enum. Adding a 6th code requires a methodology version bump and a code change.

## Known Gaps

1. ~~**Model configurability**~~: Resolved. Model selection is configurable per-phase and per-methodology via `model_defaults` (methodology-level) and `model`/`judge_model` (phase-level) fields. See Model Configuration section above.
2. **No live benchmark results**: Benchmarks require `OPENAI_API_KEY`; results have not been collected yet.
3. ~~**Intent fidelity**~~: Resolved. `SourceIntentGenerator` creates a structured source-intent artifact from the task brief (verbatim preservation + extracted criteria). `FidelityExecutor` runs a terminal comparison against the target phase output with zero intermediate artifact access. Closed 5-code drift taxonomy (`DriftCode` enum). See Intent Fidelity section above.
4. ~~**No cost tracking**~~: Resolved. `CostCalculator` provides per-model pricing, per-attempt cost on traces, run-level `PipelineCost`, and optional `budget` enforcement on methodology. See Cost Tracking section above.
5. ~~**No parallelism**~~: Resolved. `BuildExecutionWaves` computes a topological schedule from phase dependency declarations. Phases in the same wave execute concurrently via `Task.WhenAll`. Budget enforcement is between waves. Sequential methodologies produce single-phase waves (identical behavior to pre-parallelism). See Parallel Execution section above.
6. ~~**No crash recovery**~~: Resolved. `PipelineRunner.ResumeAsync` rebuilds pipeline state from per-phase snapshots, accumulates tokens/cost from all persisted attempts, and resumes from the first non-succeeded phase. See Crash Recovery section above.
7. ~~**Control arm not implemented**~~: Resolved. `ControlExecutor` provides single-shot A/B benchmarking against the multi-phase pipeline. See Control Arm section above.
8. ~~**Schema evolution**~~: Resolved. Multi-version `SchemaRegistry` with lifecycle statuses (Active/Deprecated/Retired), methodology validation at run start, version-dispatched validators, and engine-internal schema classification. See Schema Evolution section above.
9. ~~**Seeded-defect benchmarks**~~: Resolved. `SeededDefectCatalog` provides 7 frozen test cases (covering all 5 drift codes) with ground-truth verdicts. `SeededDefectRunner` runs them through `FidelityExecutor` and computes detection rates with thresholds (80% blocking, 60% advisory). See Seeded-Defect Benchmarks section above.
10. ~~**Server integration**~~: Resolved. Forge engine wired into `AgentAcademy.Server` via `AddForge()` DI extension. REST API at `/api/forge/*` with job queue, path validation, and conditional LLM wiring. See Server Integration section below.

## Server Integration

### Purpose
Connects the standalone Forge engine to the AgentAcademy server, exposing pipeline runs via REST API. The engine remains self-contained — the integration layer handles job orchestration, configuration, and HTTP surface.

### Configuration

```json
// appsettings.json
{
  "Forge": {
    "Enabled": true,
    "RunsDirectory": "forge-runs",
    "MethodologiesDirectory": "methodologies",
    "OpenAiApiKey": "",
    "OpenAiBaseUrl": "https://api.openai.com/v1"
  }
}
```

- `ForgeOptions` POCO bound from `Forge` section (`Config/ForgeOptions.cs`)
- `RunsDirectory` resolved relative to `ContentRootPath`
- `MethodologiesDirectory` resolved relative to `ContentRootPath` (default: `methodologies/`)
- `OpenAiApiKey` can be set via user-secrets or environment variables
- When `OpenAiApiKey` is empty, execution endpoints return 503; read-only endpoints remain available

### DI Wiring

`ForgeServiceRegistration.AddForge()` in `Startup/ForgeServiceRegistration.cs`:
1. Reads `ForgeOptions` from configuration
2. Registers `TimeProvider.System` (required by `PipelineRunner`)
3. Calls `AddForgeEngine(runsDir)` from the Forge library
4. Registers `ILlmClient`: `OpenAiLlmClient` when API key is present, `UnavailableLlmClient` otherwise
5. Registers `ForgeRunService` as both singleton and `IHostedService`

### REST API

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/forge/jobs` | POST | `[Authorize]` | Start pipeline run (202 + job ID) |
| `/api/forge/jobs` | GET | FallbackPolicy | List all jobs |
| `/api/forge/jobs/{jobId}` | GET | FallbackPolicy | Get job status + run ID |
| `/api/forge/runs` | GET | FallbackPolicy | List completed runs from disk store |
| `/api/forge/runs/{runId}` | GET | FallbackPolicy | Get full run trace |
| `/api/forge/runs/{runId}/phases` | GET | FallbackPolicy | Get phase-level traces |
| `/api/forge/runs/{runId}/resume` | POST | `[Authorize]` | Resume crashed run (501 placeholder) |
| `/api/forge/artifacts/{hash}` | GET | FallbackPolicy | Get artifact by content hash |
| `/api/forge/schemas` | GET | FallbackPolicy | List registered schemas |
| `/api/forge/status` | GET | FallbackPolicy | Engine status + job counts |
| `/api/forge/methodologies` | GET | FallbackPolicy | List saved methodology templates |
| `/api/forge/methodologies/{id}` | GET | FallbackPolicy | Get a methodology by ID |
| `/api/forge/methodologies/{id}` | PUT | `[Authorize]` | Save/update a methodology template |

### Job Queue

`ForgeRunService` (`Services/ForgeRunService.cs`):
- Background service with bounded `Channel<string>` (capacity 100)
- Jobs tracked in `ConcurrentDictionary<string, ForgeJob>` (in-memory, not durable across restarts)
- Job lifecycle: Queued → Running → Completed/Failed
- POST returns immediately with 12-char job ID; pipeline executes in background
- Run ID (`R_` + ULID) is generated by `PipelineRunner.ExecuteAsync`, populated on job after execution starts

### Security

- **Path traversal**: Run IDs validated against `R_[0-9A-HJKMNP-TV-Z]{26}` regex; artifact hashes validated as 64 hex chars (with optional `sha256:` prefix normalization)
- **Authentication**: Execution endpoints (`POST /api/forge/jobs`, `POST .../resume`, `PUT .../methodologies/{id}`) carry explicit `[Authorize]` attributes. Read-only GET endpoints are protected by the global `FallbackPolicy` (requires authenticated user when auth is enabled; see spec 015 §2). When auth is disabled (`AnyAuthEnabled = false`), neither middleware is registered and all endpoints are open — this is by design for single-user local development.
- **Execution gating**: `UnavailableLlmClient` throws on `GenerateAsync`, preventing accidental execution when no API key is configured
- **Methodology IDs**: Strict allowlist regex `^[a-zA-Z0-9][a-zA-Z0-9_-]{0,98}[a-zA-Z0-9]$`; resolved paths validated against catalog root; atomic writes via unique temp files

### Methodology Catalog

`IMethodologyCatalog` / `DiskMethodologyCatalog` (`Services/DiskMethodologyCatalog.cs`):
- Disk-backed catalog of saved methodology templates in `MethodologiesDirectory` (configurable, default `methodologies/`)
- Filename derived from methodology `id` field (e.g., `spike-default-v1` → `spike-default-v1.json`)
- `ListAsync()` scans directory, skips malformed files (logs warnings), returns summaries sorted by ID
- `GetAsync(id)` returns full `MethodologyDefinition` or null if not found
- `SaveAsync(methodology)` validates then writes atomically; returns methodology ID
- `SeedAsync(methodology)` writes only if file doesn't already exist (idempotent startup seeding)

**Validation on save:**
- Non-empty ID matching allowlist regex
- At least one phase with unique IDs
- All phase inputs reference existing phases
- No dependency cycles (DFS cycle detection)
- Valid `max_attempts_default` (> 0), valid `budget` (> 0 if specified)
- `fidelity.target_phase` references existing phase ID
- `control.target_schema` is non-empty when control is configured

**Default methodology**: `spike-default-v1` (5-phase pipeline) is seeded into the catalog on first startup via `SeedDefaultMethodologyAsync()`.

**Frontend**: ForgePanel "New Run" form includes:
- Methodology selector dropdown (populated from `GET /api/forge/methodologies`)
- JSON editor pre-populated when a template is selected
- "Save as Template" button to persist customized methodologies back to catalog
- Graceful fallback when catalog is unavailable (form works with manual JSON editing)
- Stale-response guard on methodology selection (fetch ID tracking)

### Known Integration Gaps

1. **Jobs are not durable**: In-memory job store is lost on restart. A restart between 202 Accepted and execution start loses the job.
2. ~~**No authentication on forge endpoints**~~: Resolved. Execution endpoints (`POST jobs`, `POST resume`, `PUT methodologies`) carry explicit `[Authorize]`. Read-only GET endpoints are protected by the global `FallbackPolicy` (authenticated when auth enabled, open when auth disabled). See Security section above.
3. **No workspace scoping**: Forge runs are global, not scoped to a workspace/room. Multi-user deployments would expose cross-tenant data.
4. **Resume not implemented**: `POST /api/forge/runs/{runId}/resume` returns 501.
5. ~~**No agent commands**~~: Resolved. Agents can trigger forge runs via `RUN_FORGE`, check status via `FORGE_STATUS`, and list jobs via `LIST_FORGE_RUNS`. See spec 007 for command details. Handlers use `IForgeJobService` interface for testability. Path traversal and symlink protections applied to methodology file loading.
6. **No SignalR events**: No real-time progress updates during pipeline execution.
7. ~~**No frontend UI**~~: Resolved. ForgePanel provides status dashboard, job list, run detail with phase/attempt/validator drill-down, inline artifact viewer, and start-run form with methodology JSON editor. See spec 300 for frontend details.

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2026-04-20 | Initial spec written from implemented code | Handoff from forge spike sessions |
| 2026-04-20 | Forge engine core (Layer 1) | `feat/forge-engine-core` |
| 2026-04-20 | Execution foundation — LLM abstraction, prompt builder, schemas (Layer 2a) | `feat/forge-execution-foundation` |
| 2026-04-20 | Validation pipeline and phase executor (Layer 2b) | `feat/forge-phase-executor` |
| 2026-04-20 | PipelineRunner, OpenAI client, benchmark infrastructure (Layer 3) | `feat/forge-pipeline-runner` |
| 2026-04-20 | Model configurability — phase and methodology-level model config, closes Known Gap #1 | `feat/forge-model-config` |
| 2026-04-20 | Cost tracking — per-attempt cost, judge token tracking, budget enforcement, closes Known Gap #4 | `feat/forge-cost-tracking` |
| 2026-04-20 | Control arm — single-shot A/B benchmarking baseline, closes Known Gap #7 | `feat/forge-control-arm` |
| 2026-04-20 | Crash recovery — resume-from-snapshot logic, closes Known Gap #6 | `feat/forge-crash-recovery` |
| 2026-04-20 | Intent fidelity — source-intent schema, fidelity phase, drift taxonomy, closes Known Gap #3 | `feat/forge-intent-fidelity` |
| 2026-04-20 | Seeded-defect benchmarks — 7 frozen cases, detection rate metrics, benchmark runner, closes Known Gap #9 | `feat/forge-seeded-defect-benchmarks` |
| 2026-04-20 | Parallel phase execution — wave-based scheduling, resume source-intent fix, closes Known Gap #5 | `feat/forge-parallel-phases` |
| 2026-04-20 | Schema evolution — multi-version registry, lifecycle statuses, methodology validation, version-dispatched validators, closes Known Gap #8 | `feat/forge-schema-evolution` |
| 2026-04-21 | Server integration — REST API, job queue, DI wiring, path validation, conditional LLM, 35 tests, closes Known Gap #10 | `feat/forge-integration` |
| 2026-04-21 | Start-run UI — New Run button, form with title/description/methodology JSON editor, 11 new tests, closes Integration Gap #7 | `develop` |
| 2026-04-21 | Methodology catalog — disk-backed catalog, REST endpoints, UI selector with save-as-template, 26 new tests | `feat/methodology-browser` |
| 2026-04-21 | Auth on forge endpoints — `[Authorize]` on execution endpoints (POST jobs, POST resume, PUT methodologies), read-only GET endpoints protected by FallbackPolicy, closes Integration Gap #2 | `feat/methodology-browser` |
