# Spec Management

## Purpose

This section documents the living specification system — how specs are created, maintained, reviewed, and kept in sync with the codebase.

## Current Behavior

### Spec Directory Structure

The project specification lives in `specs/` at the repository root, organized into numbered sections:

```
specs/
├── README.md                    — Index, conventions, and usage guide
├── CHANGELOG.md                 — Log of all spec changes with task references
├── 000-system-overview/spec.md  — High-level architecture and design principles
├── 001-domain-model/spec.md     — All domain types from AgentAcademy.Shared
├── 002-development-workflow/    — Development workflow and branching
├── 003-agent-system/spec.md     — Agent roles, configuration, execution
├── 004-notification-system/     — Notification providers and routing
├── 005-workspace-runtime/       — Runtime lifecycle and room management
├── 006-orchestrator/spec.md     — Orchestrator mechanics and conversation flow
└── 009-spec-management/spec.md  — This document
```

### Numbering Convention

Sections use three-digit numbering (`000-xxx`) for stable ordering. New sections are inserted at the next available number without renumbering existing ones. This provides stable references across tasks and commits.

### Document Template

Every spec section follows this structure:

```markdown
# [Section Title]

## Purpose
What this section covers.

## Current Behavior
Verified description of how the system works today.

## Interfaces & Contracts
Types, APIs, data shapes with exact definitions.

## Invariants
Rules that must always hold.

## Known Gaps
Where implementation is incomplete or diverges from intent.

## Revision History
| Date | Task | Changes |
|------|------|---------|
| YYYY-MM-DD | [task reference] | [description] |
```

### Spec Ownership

The specification is owned by **Thucydides** (agent `tech-writer-1`, role `TechnicalWriter`). This agent:

- Produces a **Spec Change Proposal** during Planning for every task
- Updates spec files during Implementation
- Verifies spec accuracy against delivered code during Validation
- Maintains `specs/CHANGELOG.md`

### Spec Change Workflow

#### Plans ARE Spec Change Proposals

Every implementation plan must include a Spec Change Proposal. The plan is the formal proposal for how the spec will change — not just a task list. The spec update is a tracked deliverable alongside code changes, validated with the same rigor.

**Workflow**:
1. **Plan phase**: Plan includes a Spec Change Proposal section identifying affected spec sections, change type, proposed updates, and verification method.
2. **Implementation phase**: The spec update is a tracked todo item in the plan — code and spec changes are delivered together.
3. **Validation phase**: Completed work is validated against the plan's spec change proposal. Reviewers verify that (a) every code change has a spec update, (b) every spec claim references actual code, (c) the spec update matches what was proposed.

#### Spec Change Proposal Format

When a new task is planned, the plan includes:

```
SPEC CHANGE PROPOSAL:
Task: [task title]
Affected Sections:
- [spec section number and name]: [what changes]
Change Type: NEW_CAPABILITY | MODIFICATION | BUG_FIX_CODE | BUG_FIX_SPEC
Proposed Changes:
- [section]: [current behavior] → [new behavior]
Spec Sections to Read: [files engineers should reference]
Verification Plan: [how to confirm accuracy after implementation]
```

**Change Types:**

| Type | Meaning | Spec Action | Code Action |
|------|---------|-------------|-------------|
| `NEW_CAPABILITY` | Adding new functionality | Add new spec sections | Implement feature |
| `MODIFICATION` | Changing existing behavior | Update affected sections | Modify code |
| `BUG_FIX_CODE` | Code deviates from spec | No change (spec is correct) | Fix code to match spec |
| `BUG_FIX_SPEC` | Spec doesn't capture desired behavior | Update spec | Fix code |

#### Implementation Phase — Spec Updates

- Spec updates are tracked todo items in the plan, not afterthoughts
- Every spec claim must reference actual code (file paths, function names)
- Engineers reference relevant spec sections during implementation
- Engineers flag any spec-code divergences discovered during work

#### Validation Phase — Review Against Plan

Completed work is validated against the plan's spec change proposal:

1. **Code ↔ Spec consistency**: Every code change has a corresponding spec update; every spec claim references actual code
2. **Plan fidelity**: The delivered spec update matches what was proposed in the plan
3. **No aspirational claims**: Spec describes what IS, not what SHOULD BE
4. **CHANGELOG updated**: `specs/CHANGELOG.md` has an entry for the change

Socrates (Reviewer) adversarially verifies spec accuracy:

- Reads spec changes and compares against actual code
- Checks for claims not backed by implementation
- Checks for code changes not reflected in the spec
- Reports spec-code mismatches as CRITICAL findings
- Does not approve work if spec is out of sync

Archimedes (Architect) validates technical accuracy:

- Verifies type definitions, API contracts, architecture descriptions
- Challenges technically incorrect or misleading spec claims

#### FinalSynthesis Phase — Spec Confirmation

- All spec changes are complete, accurate, and match the plan's spec change proposal
- `specs/CHANGELOG.md` is updated with the task reference and summary

### Integration with the Orchestrator

The spec system is managed by `SpecManager` (`AgentAcademy.Server.Services.SpecManager`) and integrated into the orchestrator workflow:

- **`SpecManager.LoadSpecContext()`**: Reads `specs/` directory, extracts heading and purpose from each section's `spec.md`, returns a condensed index for prompt injection
- **`SpecManager.GetSpecSections()`**: Returns structured metadata (id, heading, summary, file path) for all spec sections
- **`SpecManager.GetSpecContent(sectionId)`**: Reads the full content of a specific spec section by directory name
- **`AgentOrchestrator.BuildConversationPrompt()`**: Injects spec context into every conversation prompt when specs exist
- **`AgentOrchestrator.BuildReviewPrompt()`**: Includes spec context and requests spec accuracy verification in review verdicts
- **`AgentOrchestrator.InferMessageKind()`**: Maps `TechnicalWriter` role to `SpecChangeProposal` message kind

### Agent Spec Awareness

All agents have spec awareness built into their startup prompts:

| Agent | Role | Spec Responsibility |
|-------|------|---------------------|
| Aristotle | Planner | Ensures every behavior-changing task includes Thucydides; gates approval on spec updates |
| Archimedes | Architect | Reviews technical accuracy of spec changes |
| Hephaestus | SoftwareEngineer | References specs during implementation; flags divergences |
| Athena | SoftwareEngineer | References specs during implementation; flags divergences |
| Socrates | Reviewer | Adversarial spec verification; blocks approval on mismatches |
| Thucydides | TechnicalWriter | Owns the spec; produces proposals, updates, and verifications |

### Writing Standards

1. **Evidence-based**: Never write "the system does X" without verifying it in code
2. **Precise**: Use exact type names, function signatures, endpoint paths
3. **Current**: Describes what IS, not what SHOULD BE (aspirational behavior goes in Known Gaps)
4. **Traceable**: Every change links to the task that caused it
5. **Concise**: Technical accuracy over prose quality

## Interfaces & Contracts

### SpecManager Service

```csharp
public sealed class SpecManager
{
    public record SpecSection(string Id, string Heading, string Summary, string FilePath);
    public record SpecVersionInfo(string Version, string LastUpdated, string ContentHash, int SectionCount);

    public SpecManager(string? specsDir = null, ILogger<SpecManager>? logger = null);
    public Task<string?> LoadSpecContextAsync();
    public Task<string?> LoadSpecContextForTaskAsync(IEnumerable<string> linkedSectionIds);
    public Task<List<SpecSection>> GetSpecSectionsAsync();
    public Task<string?> GetSpecContentAsync(string sectionId);
    public Task<SpecVersionInfo?> GetSpecVersionAsync();
    public Task<string> ComputeContentHashAsync();
}
```

Registered as singleton in DI. Injected into `AgentOrchestrator`, `BreakoutLifecycleService`, and `CollaborationController`.

### MessageKind Extension

The `SpecChangeProposal` message kind (defined in `AgentAcademy.Shared.Models.MessageKind`) is used for messages from the TechnicalWriter agent containing spec change proposals.

### Spec Context in Prompts

When specs exist, the orchestrator injects a `=== PROJECT SPECIFICATION (vX.Y.Z) ===` section into conversation prompts listing all spec sections with their headings and purpose summaries. The version tag is included when `spec-version.json` exists.

### Review Verdict Extension

When specs exist, review prompts include a `Spec Accuracy` section requesting:
- Whether spec updates match the delivered implementation
- Whether any code changes are missing from the spec
- Whether any spec claims are contradicted by code

## Invariants

1. The spec must always describe actual system behavior, verified against code
2. Every meaningful code change (non-bugfix) must be accompanied by a spec update
3. Every implementation plan must include a Spec Change Proposal section
4. The spec update is a tracked deliverable in the plan — validated alongside code changes
5. Bug fixes where code deviates from spec do not require spec changes
6. Bug fixes where behavior is undesirable but matches spec require both spec and code changes
7. The CHANGELOG.md must have an entry for every spec modification
8. Spec sections use three-digit numbered folders that are never renumbered
9. Thucydides is the sole owner of spec content — other agents review but don't write specs
10. `GetSpecContent` guards against path traversal attacks
11. The `spec-drift` CI job warns (does not block) when source changes lack corresponding spec updates

### Automated Spec Drift Detection

**Files**: `scripts/check-spec-drift.sh` (bash wrapper), `scripts/check-spec-drift.js` (Node.js analyzer), `specs/drift-map.json` (path-to-spec mapping)

**CI Integration**: `.github/workflows/ci.yml` → `spec-drift` job. Runs on PRs only. Non-blocking (warning-only).

**How it works**:
1. Bash wrapper resolves base/head SHAs from PR context or CLI args
2. Checks for `spec-exempt:` marker in PR body or commit messages
3. Gets changed files via `git diff --name-only --diff-filter=ACMR -M -C`
4. Pipes file list to Node.js analyzer
5. Analyzer loads `specs/drift-map.json`, filters excluded files, matches remaining against mappings
6. Reports: which spec sections should have been updated (based on which source files changed) vs which specs were actually modified
7. Warns about unmapped source files (files that changed but don't match any mapping)

**Drift map format** (`specs/drift-map.json`):
```json
{
  "mappings": [
    { "pattern": "src/AgentAcademy.Server/Commands/", "specs": ["007"], "note": "..." }
  ],
  "excludes": ["tests/", "**/*.test.*", "*.md", ...]
}
```

Patterns support glob wildcards (`*`, `**`), directory prefixes (trailing `/`), and exact file matches. Files are matched against ALL patterns (union), not first-match. Excludes use the same glob syntax.

**Exemption mechanism**: Add `spec-exempt: <reason>` to either:
- The PR description body
- Any commit message in the PR

This suppresses all drift warnings for the PR.

## Known Gaps

1. ~~**No automated spec drift detection**~~: **Resolved** — `scripts/check-spec-drift.sh` runs as a CI job on PRs (`.github/workflows/ci.yml`, `spec-drift` job). Uses `specs/drift-map.json` to map source file path patterns to spec sections. Detects when code changes lack corresponding spec updates and produces GitHub Actions warnings. Supports `spec-exempt:` marker in PR body or commit messages to suppress false positives. Reports unmapped source files that need mapping additions. Warning-only (non-blocking) posture for initial rollout. Analysis engine in `scripts/check-spec-drift.js` (Node.js).
2. ~~**No spec search**: `LoadSpecContext()` loads all spec section headings. There is no semantic search or filtering by relevance to the current task — all sections are included.~~ **Resolved**: `SpecManager.SearchSpecsAsync` provides keyword-based search across spec content with weighted TF scoring (heading 3×, purpose 2×, body 1×) and multi-term coverage bonus. `LoadSpecContextWithRelevanceAsync` combines task-linked sections (★) with keyword-matched sections (◆), ranking relevant sections first. Breakout prompts now use task title + description as search query for automatic relevance. `GET /api/specs/search?q=` endpoint exposes search to clients. `LINK_TASK_TO_SPEC` command provides explicit traceability links.
3. ~~**No spec versioning beyond git**~~: **Resolved** — `specs/spec-version.json` tracks the spec corpus version (semver) and `lastUpdated` date. `SpecManager.GetSpecVersionAsync()` reads the manifest and computes a SHA256 content hash of all `specs/*/spec.md` files for freshness detection (cached in memory, invalidated by file write-time changes). Version is included in agent prompt headers (`=== PROJECT SPECIFICATION (vX.Y.Z) ===`) via the `specVersion` parameter on `PromptBuilder.BuildConversationPrompt`, `BuildBreakoutPrompt`, and `BuildReviewPrompt`. `GET /api/specs/version` endpoint returns `SpecVersionInfo` (version, lastUpdated, contentHash, sectionCount). `scripts/bump-spec-version.sh` bumps major/minor/patch. `check-spec-drift.js` warns in CI when spec content files change but `spec-version.json` is not updated.

### Spec Versioning

**Manifest**: `specs/spec-version.json`
```json
{
  "version": "2.1.0",
  "lastUpdated": "2026-04-12"
}
```

**Bump semantics** (semver for the spec corpus):
- **patch**: Wording/clarification only — no behavioral changes
- **minor**: Additive requirement or process change — new sections, expanded behavior
- **major**: Breaking behavioral or contract change — existing spec claims invalidated

**Bump command**: `scripts/bump-spec-version.sh [major|minor|patch]` (defaults to patch)

**Content hash**: `SpecManager.ComputeContentHashAsync()` computes a truncated SHA256 (12 hex chars) of all `specs/*/spec.md` files. Sorted paths and normalized line endings ensure deterministic output across platforms. Cached in memory and invalidated when any spec file's `LastWriteTimeUtc` changes. Non-spec files (`README.md`, `CHANGELOG.md`, `drift-map.json`, `spec-version.json`) are excluded from the hash.

**Freshness detection**: Compare the content hash from `GET /api/specs/version` against a previously stored hash to detect if spec content has changed since last read.

**Prompt integration**: All three prompt types include the version in their spec section header when available. Version is loaded once per conversation/breakout round in the orchestrator and passed through to `PromptBuilder`. Agents see which spec version they are working against.

**CI enforcement**: `check-spec-drift.js` warns when spec content files (`specs/NNN-*/spec.md`) change in a PR but `specs/spec-version.json` was not updated.

## Revision History

| Date | Task | Changes |
|------|------|---------|
| 2025-07-25 | Port spec system | Created SpecManager service, integrated with orchestrator, added agent spec awareness |
| 2026-04-05 | spec-task-linking | Added `LoadSpecContextForTaskAsync` to SpecManager for task-filtered spec loading. Updated known gap #2. |
| 2026-04-12 | spec-drift-detection | Added automated spec drift detection CI job. `scripts/check-spec-drift.sh` + `scripts/check-spec-drift.js` + `specs/drift-map.json`. Integrated into `.github/workflows/ci.yml`. Known gap #1 resolved. |
| 2026-04-12 | spec-versioning | Added `specs/spec-version.json` manifest, `SpecManager.GetSpecVersionAsync()` and `ComputeContentHashAsync()`, version tags in prompt headers, `GET /api/specs/version` endpoint, `scripts/bump-spec-version.sh`, CI enforcement in `check-spec-drift.js`. Known gap #3 resolved. |
