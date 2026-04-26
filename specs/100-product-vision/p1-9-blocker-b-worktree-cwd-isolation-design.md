# P1.9 Blocker B — Per-Breakout Worktree CWD Isolation

**Status**: PROPOSED — implementation gated on this design's adversarial review.
**Author**: anvil (operator: agent-academy), 2026-04-26
**Closes**: P1.9 acceptance blocker B (write isolation across parallel breakouts)
**Companion**: P1.9 blocker A — Permission diagnostic completeness (PR #159)
**Revision history**: v1 → v2 expansion after 3 adversarial reviewers (codex / opus / gpt-5.5) converged on five concrete gaps in the original write-only scope: (1) `commit_changes` commits via `GitService` singleton, (2) structured-command handlers have an *independent* copy of the same bug, (3) the review-fix loop drops `worktreePath`, (4) tests didn't actually exercise commit-lands-in-worktree, (5) reviewers also requested scopeRoot identity validation. v2 expands scope to the full set.

---

## 1. Problem

During the supervised P1.9 acceptance run, two breakout-session agents were dispatched in parallel against two different tasks. Each task had its own git worktree under `.worktrees/task_…/`. The expectation was that each agent's `write_file` calls would land inside its assigned worktree. Observed behaviour: **both agents wrote to the develop checkout simultaneously**. The worktrees were inert; the develop tree mutated unsafely from two writers at once. Subsequent merges of the per-task branches produced empty diffs (the work was already on develop).

This is not a "merge bug" or a "git lock bug". It is a complete absence of write isolation. The breakout dispatch path *appears* to thread a worktree path through, but the actual filesystem write happens in code paths that have no awareness of which worktree the calling agent belongs to. There are at least **three independent code paths** that all share the same root cause; the original v1 of this design only identified one.

## 2. Root cause — three parallel layers, one shared assumption

### 2a. The shared assumption

All file-touching code in the server resolves "where is the project?" via `AgentToolFunctions.FindProjectRoot()` (or a near-duplicate static method copied into individual command handlers, or `GitService._repositoryRoot` captured once at construction). Every variant ultimately walks up from `Directory.GetCurrentDirectory()` (or a hardcoded constructor argument) looking for `AgentAcademy.sln`. The server process's cwd is the develop checkout. So *every* path resolution lands in develop, regardless of which worktree the caller belongs to.

### 2b. Layer 1 — SDK-registered tool functions (`write_file`, `commit_changes`, `read_file`, `search_code`)

These are C# methods registered with the Copilot SDK via `AIFunctionFactory.Create(...)`. When the LLM calls them, our C# code runs — *not* an SDK-internal Kind. Each method calls `AgentToolFunctions.FindProjectRoot()` directly:

| Method | File | Line |
|---|---|---|
| `CodeWriteToolWrapper.WriteFileAsync` | `CodeWriteToolWrapper.cs` | 162 |
| `CodeWriteToolWrapper.StageFileAsync` | `CodeWriteToolWrapper.cs` | 247 (`WorkingDirectory = projectRoot`) |
| `CodeWriteToolWrapper.CommitChangesAsync` | `CodeWriteToolWrapper.cs` | 290 *(but commit itself is layer 2)* |
| `AgentToolFunctions.ReadFileAsync` | `AgentToolFunctions.cs` | 191 |
| `AgentToolFunctions.SearchCodeAsync` | `AgentToolFunctions.cs` | 265, 285 (`WorkingDirectory = projectRoot`) |

Read-side tools (`read_file`, `search_code`) are registered as a **shared singleton** in `AgentToolRegistry._staticGroups["code"]`. There is no per-session handle to inject a worktree. Write-side tools (`code-write`, `spec-write`) are built per-session via `CreateContextualTools`, but the `CodeWriteToolWrapper` constructor accepts no scope root.

### 2c. Layer 2 — `GitService` (singleton)

`CommitChangesAsync` validates staged paths against the local `projectRoot`, then delegates the actual commit to `IGitService.CommitAsync(message, identity)`. `GitService` is registered as a singleton with `_repositoryRoot = repositoryRoot ?? FindProjectRoot()` captured in its constructor (`GitService.cs:28`). All `RunGitAsync` calls resolve to `RunGitInDirInternalAsync(_repositoryRoot, args)` (`GitService.cs:301-302`). So even if `WriteFileAsync`/`StageFileAsync` are correctly re-rooted to a worktree, `git commit` still runs in the develop checkout. Net effect after a "fix" of layer 1 only: writes land in worktree, `git add` stages them in worktree's index, but `git commit` runs in develop's index — finds nothing staged — and either errors with "nothing to commit" or commits whatever happens to be staged in develop from another agent. **This is the critical gap all three reviewers caught and the v1 design missed.**

`GitService.GetFilesInCommitAsync(sha, workingDir = null)` defaults to `_repositoryRoot` too (`GitService.cs:377`). So even after fixing the commit, the post-commit artifact-recording call at `CodeWriteToolWrapper.RecordCommitArtifactAsync:357` would query develop's history for a SHA that lives only on the worktree's branch, and silently fail to record artifacts.

### 2d. Layer 3 — Structured command handlers (the parallel write/read path)

Agents emit structured commands (`READ_FILE`, `SHOW_DIFF`, `RUN_BUILD`, `RUN_TESTS`, `SEARCH_CODE`, `SEARCH_SPEC`, `OPEN_COMPONENT`, `GIT_LOG`, `FIND_REFERENCES`, `COMPARE_SPEC_TO_CODE`, `VERIFY_SPEC_SECTION`, `DETECT_ORPHANED_SECTIONS`, `RUN_FORGE`, plus shell commands routed via `ShellCommandHandler`). These flow through `CommandPipeline.ProcessResponseAsync(..., workingDirectory)`, which packages the workingDirectory into `CommandContext.WorkingDirectory`. The pipeline call site **does** pass `worktreePath` from the breakout loop (`BreakoutLifecycleService.cs:230` and `BreakoutCompletionService.cs:297`) — so the value is correctly available to handlers.

But **most handlers ignore `context.WorkingDirectory` and call their own private static `FindProjectRoot()` instead**:

| Handler | Calls `FindProjectRoot()` at | Honours `context.WorkingDirectory`? |
|---|---|---|
| `ReadFileHandler` | `:27` | ❌ |
| `SearchCodeHandler` | `:30` | ❌ |
| `ShowDiffHandler` | `:17` | ❌ |
| `RunBuildHandler` | `:23` | ❌ |
| `RunTestsHandler` | `:24` | ❌ |
| `OpenComponentHandler` | `:36` | ❌ |
| `SearchSpecHandler` | `:33` | ❌ |
| `CompareSpecToCodeHandler` | `:62` | ❌ |
| `GitLogHandler` | `:20` | ❌ |
| `FindReferencesHandler` | `:35` | ❌ |
| `VerifySpecSectionHandler` | `:63` | ❌ |
| `DetectOrphanedSectionsHandler` | `:33` | ❌ |
| `ShellCommandHandler` | `:266` | ✅ uses `context?.WorkingDirectory ?? FindProjectRoot()` |
| `RunForgeHandler` | `:80` | ✅ uses `context.WorkingDirectory ?? FindProjectRoot()` |

So even if layers 1 and 2 are fixed, an agent in a worktree-backed breakout that runs `READ_FILE src/Foo.cs` after writing to it would still read develop's copy. `RUN_BUILD` and `RUN_TESTS` would still build develop. `SHOW_DIFF` would diff develop. The breakout completes with nominally green tests — but it tested the *wrong tree*. **This is the second critical gap (gpt-5.5).**

### 2e. The review-fix loop also drops the path

`BreakoutCompletionService.HandleReviewRejectionAsync` accepts `worktreePath` and passes it correctly into `_completion.ProcessCommandsAsync(...)` (line 297) — but the LLM call earlier at `RunAgentAsync(reviewer, prompt, parentRoomId)` (line 183 in v1 reading; gpt-5.5 cited line 278–280 in the rejection-handler scope) passes no `workspacePath`. So the fix-round agent re-runs against the default client / develop checkout, even when the original breakout was correctly worktree-scoped. **Third gap (gpt-5.5).**

### 2f. The legacy single-checkout serialisation hides this

The reason single-agent runs *appeared* to work is the `EnsureBranchInternalAsync` + `_gitService.AcquireRoundLockAsync()` serialisation in `BreakoutLifecycleService.RunBreakoutLoopAsync:182-184`, which switches develop to the task branch under a process-wide lock for the duration of one round. That code path runs **only when `worktreePath == null`** (the explicit `useWorktree = worktreePath != null` branch). When worktrees are in use, the lock is intentionally bypassed under the (incorrect) assumption that worktrees provide isolation. They don't, because all three layers ignore them.

## 3. Goals and non-goals

### Goals

- **G1**: When a breakout agent has an assigned worktree, every write performed via the SDK `write_file` tool lands inside that worktree, and the corresponding `git add` and `git commit` operate on that worktree's index/branch.
- **G2**: SDK read tools (`read_file`, `search_code`) return the worktree's view of the tree, so an agent can read back what it just wrote.
- **G3**: Structured command handlers that touch the filesystem or git (the 12 listed in §2d that currently ignore `WorkingDirectory`) honour `context.WorkingDirectory` when set.
- **G4**: Path-traversal and symlink-escape protections still apply, with the worktree as the new root. Existing `DetectSymlinkEscape` (writes) and `IsResolvedPathInsideRoot` (reads) continue to operate against the resolved scope root.
- **G5**: Agents in the main room (no assigned task / worktree) preserve current behaviour — operations resolve via `FindProjectRoot()` against the develop checkout.
- **G6**: No `Directory.SetCurrentDirectory`. Multiple parallel breakouts must each see their own root within the same server process.
- **G7**: Tests prove the bug is gone end-to-end — including `commit_changes` actually landing in the worktree branch, not just `git add` running there.

### Non-goals

- **NG1**: Restructuring the SDK's own `read`/`write` Kinds. Those already honour `options.Cwd`; the bugs are exclusively in our C# layers.
- **NG2**: Changing the worktree lifecycle (creation, disposal, cleanup). Owned by `WorktreeService` and unaffected.
- **NG3**: Removing the legacy round-lock path for non-worktree breakouts. It still serves the (now-rare) workspace-level branch-switch fallback. Removing it is a separate refactor.
- **NG4**: Inventing a new sandboxing primitive (chroot, container, etc.). Path-prefix scoping plus existing symlink resolution is the proven, in-tree pattern.
- **NG5**: Eliminating the C# `write_file` tool entirely in favour of the SDK's built-in write Kind. The C# tool exists because we need post-write hooks (artifact recording, conventional-commit staging, protected-path enforcement, audit logging) that the SDK's raw write does not perform. Re-creating those hooks elsewhere is a larger refactor.

## 4. Design

The fix has three layers matching the three-layer root cause. They must all land together — fixing one layer in isolation would leave the system in a worse state (writes correctly isolated but commits dropped on the floor, etc.).

### 4.1 Layer 1 — SDK tool wrappers

#### 4.1.1 `CodeWriteToolWrapper`

Add an optional `scopeRoot` to the constructor:

```csharp
internal CodeWriteToolWrapper(
    IServiceScopeFactory scopeFactory, ILogger logger,
    string agentId, string agentName, AgentGitIdentity? gitIdentity, string? roomId,
    IReadOnlyList<string> allowedRoots, IReadOnlyList<string> protectedPaths,
    string? scopeRoot = null);
```

In `WriteFileAsync`, `StageFileAsync`, and `ValidateStagedPathsAsync`, replace `AgentToolFunctions.FindProjectRoot()` with `_scopeRoot ?? AgentToolFunctions.FindProjectRoot()`.

In `CommitChangesAsync`, replace `gitService.CommitAsync(message, _gitIdentity)` with a new scoped commit that runs in `scopeRoot` — see §4.2 for the `IGitService` change.

In `RecordCommitArtifactAsync`, change the `gitService.GetFilesInCommitAsync(commitSha)` call to `gitService.GetFilesInCommitAsync(commitSha, _scopeRoot)`.

Symlink defence stays the existing `DetectSymlinkEscape(projectRoot, fullPath)` for writes — the parameter just receives `_scopeRoot` instead of `FindProjectRoot()`. (Reads use `IsResolvedPathInsideRoot`; same swap.) v1 of this design called the wrong helper for writes; v2 corrects this.

#### 4.1.2 New `CodeReadToolWrapper`

Today `_staticGroups["code"]` holds a single shared instance built at registry construction time, which makes per-worktree scoping impossible (a static `AIFunction` has no per-call context). Move `code` into `ContextualGroups`. Build it per-session via a new wrapper:

```csharp
internal sealed class CodeReadToolWrapper {
    private readonly ILogger _logger;
    private readonly string? _scopeRoot;   // null ⇒ FindProjectRoot()
    // exposes ReadFileAsync, SearchCodeAsync (mirrors current methods)
}
```

The current static `AgentToolFunctions.ReadFileAsync` and `SearchCodeAsync` are lifted into this wrapper. To preserve behaviour for any pre-existing callers/tests, the static methods stay as thin shims delegating to a default-rooted instance.

`AgentToolRegistry`:
- Add `"code"` to `ContextualGroups` (currently `task-write`, `memory`, `code-write`, `spec-write`).
- Add `read_file` and `search_code` to the `contextualNames` diagnostic list at lines 55–60 so `_allToolNames` still reports them. (Found by opus during review.)
- `CreateContextualTools` gains a `"code"` branch that builds a `CodeReadToolWrapper(workspacePath)`.

### 4.2 Layer 2 — `GitService`

Add a new method:

```csharp
public Task<string> CommitStagedInDirAsync(
    string workingDir, string message, AgentGitIdentity? identity);
```

Unlike the existing `CommitInDirAsync`, this one runs **only `git commit` and `git rev-parse HEAD`** — no `git add -A` (which would pull in untracked files outside the agent's scope). Validation of staged paths is the wrapper's responsibility (already implemented in `ValidateStagedPathsAsync`).

`GitService.GetFilesInCommitAsync(sha, workingDir)` already accepts an optional `workingDir`; no change needed. The `CodeWriteToolWrapper` change in §4.1.1 is what activates it.

### 4.3 Layer 3 — Structured command handlers

Apply the same `context.WorkingDirectory ?? FindProjectRoot()` pattern (already used by `ShellCommandHandler` and `RunForgeHandler`) to the 12 handlers listed in §2d. Each handler change is small and mechanical:

```csharp
// Before
var projectRoot = FindProjectRoot();
// After
var projectRoot = context.WorkingDirectory ?? FindProjectRoot();
```

Path-traversal and symlink checks against `projectRoot` continue to work unchanged because they're parameterised on the variable, not on a hardcoded root.

A handful of handlers also shell out to `git`, `dotnet`, `npm`, etc. via `ProcessStartInfo` with `WorkingDirectory = projectRoot`. The same swap applies — the new `projectRoot` value flows through to the spawned process's cwd correctly.

### 4.4 Plumbing — threading `workspacePath` from the executor

`IAgentToolRegistry.GetToolsForAgent` gains an optional `workspacePath`:

```csharp
IReadOnlyList<AIFunction> GetToolsForAgent(
    IEnumerable<string> enabledTools,
    string? agentId = null,
    string? agentName = null,
    string? roomId = null,
    string? workspacePath = null);
```

Forwarded into `CreateContextualTools(group, agentId, agentName, roomId, workspacePath)` and from there into `CreateCodeWriteTools`, `CreateSpecWriteTools`, and the new `CreateCodeReadTools` factory.

`CopilotExecutor.CreatePrimedSessionAsync` gains a `workspacePath` parameter:

```csharp
private async Task<CopilotSession> CreatePrimedSessionAsync(
    CopilotClient client, AgentDefinition agent,
    string? roomId, string? workspacePath, CancellationToken ct);
```

And the call site at `CopilotExecutor.cs:218` updates the lambda passed to `_sessionPool.UseAsync`:

```csharp
ct => CreatePrimedSessionAsync(client, agent, roomId, workspacePath, ct)
```

(This was the v1 design's silent omission — opus caught it.) Sessions are already keyed by `BuildWorktreeKey(workspacePath, agent.Id, roomId)`, so a different `workspacePath` produces a different cached session — no cross-contamination via session reuse.

### 4.5 Review-fix loop fix

In `BreakoutCompletionService.HandleReviewRejectionAsync`, change the LLM invocation to thread `worktreePath`:

```csharp
// Before
response = await RunAgentAsync(agent, prompt, breakoutRoomId);
// After
response = await RunAgentAsync(agent, prompt, breakoutRoomId, worktreePath);
```

The method already receives `worktreePath` and already passes it to command processing; this just closes the missing branch. (gpt-5.5's finding.)

### 4.6 Validation and normalisation of `scopeRoot`

At wrapper construction (one-time cost, not per-call):

1. `scopeRoot = Path.GetFullPath(scopeRoot)` (canonicalise).
2. If the directory does not exist, **throw** at construction. Silently falling back to `FindProjectRoot()` is exactly the failure mode this design exists to fix.
3. Resolve any symlink in `scopeRoot` itself via `ResolveCanonical` and store the resolved value. This means `IsResolvedPathInsideRoot(fullPath, scopeRoot)` and `DetectSymlinkEscape(scopeRoot, fullPath)` compare resolved-target ↔ resolved-root, preserving the existing security invariant.
4. **Identity validation** (codex's finding): require `scopeRoot` to be a registered git worktree of the expected repository. Concretely: run `git -C scopeRoot rev-parse --git-common-dir` and require the result to resolve to the same path as `git -C developRoot rev-parse --git-common-dir`. (Both worktrees of the same repo share the same common dir; a directory that's not a worktree of the same repo will either fail the rev-parse or return a different common dir.) On mismatch, throw at construction. This prevents an upstream bug from threading an arbitrary path through and silently widening the agent's blast radius.

### 4.7 Logging

Add a `cwd=` field to write/read/commit log lines and to each modified handler's logging:

```
Tool call: write_file by {AgentId} (cwd={ScopeRoot}, path={Path}, length={Length}, allowedRoots={Roots})
Tool call: read_file (cwd={ScopeRoot}, path={Path}, startLine={Start})
commit_changes by {AgentId} ({AgentName}): {CommitSha} (cwd={ScopeRoot}) — {Message}
Handler RUN_TESTS: cwd={WorkingDirectory}, target={Target}
```

This single observation hook would have surfaced blocker B during the supervised P1.9 run before the merge step revealed it the hard way. It is the canary for any future regression.

## 5. Alternatives considered

### A. Set `Directory.SetCurrentDirectory` per call
Rejected. The server is a single process; every breakout would race the cwd. Two parallel agents would corrupt each other's resolution mid-write. Adopting the implicit "cwd is develop" assumption multi-thread would make it strictly worse.

### B. Spawn a child process per write/read
Rejected. Massive cost for what should be a single `File.WriteAllTextAsync`. Doesn't help reads either.

### C. Configure `AllowedRoots` to include all worktree paths
Rejected. `AllowedRoots` is a *suffix* whitelist (`src`, `specs`, `docs`) — it has no notion of which absolute root those suffixes attach to. Conflating "what subtrees are writeable" with "which worktree am I in" is the categorical confusion that produced the bug.

### D. Make `FindProjectRoot()` consult an `AsyncLocal<string?>` worktree override
Considered. Equivalent in observable behaviour to the chosen design, but couples a global static (`FindProjectRoot`) to per-session state via a hidden side channel. Explicit constructor parameters and explicit `CommandContext.WorkingDirectory` reads are auditable; `AsyncLocal` flow is not.

### E. Eliminate the C# `write_file` tool entirely; make agents use the SDK's built-in write Kind
Rejected as out of scope (NG5).

### F. Eliminate structured command handlers and have agents use shell commands routed through the SDK's `shell` Kind only
Rejected as out of scope and a much larger refactor. Structured commands carry typed parameters, audit metadata, permission scoping, and per-handler error semantics that a raw shell tool cannot replicate without effectively reimplementing them.

## 6. Implementation plan

Eight commits, all on the implementation branch (separate PR after this design merges). Each commit compiles and the test suite stays green; the behaviour change activates at commit 6 when call sites finally pass `workspacePath` through.

1. **Add `scopeRoot` to `CodeWriteToolWrapper`** — route `WriteFileAsync`, `StageFileAsync`, and `ValidateStagedPathsAsync` through `_scopeRoot ?? FindProjectRoot()`. Construction-time validation per §4.6. Pure additive — no caller change yet.
2. **Add `IGitService.CommitStagedInDirAsync`** and wire `CodeWriteToolWrapper.CommitChangesAsync` + `RecordCommitArtifactAsync` to use the scoped commit and scoped artifact lookup.
3. **Add `CodeReadToolWrapper`** — lift `ReadFileAsync` and `SearchCodeAsync` from `AgentToolFunctions`. Static methods become thin shims for backward compatibility with existing tests/callers.
4. **Move `"code"` to `ContextualGroups`** — update `_allToolNames`, add the `"code"` branch to `CreateContextualTools`.
5. **Update the 12 structured command handlers** listed in §2d — single-line change each: `var projectRoot = context.WorkingDirectory ?? FindProjectRoot();`. Keep `ShellCommandHandler` and `RunForgeHandler` unchanged (already correct).
6. **Thread `workspacePath`** through `IAgentToolRegistry.GetToolsForAgent`, `AgentToolRegistry.CreateContextualTools`, the three `AgentToolFunctions.Create*Tools` overloads, and `CopilotExecutor.CreatePrimedSessionAsync` — including the `_sessionPool.UseAsync` lambda update at `CopilotExecutor.cs:218`. **This is the commit where behaviour changes.**
7. **Fix `HandleReviewRejectionAsync`** — pass `worktreePath` into `RunAgentAsync(reviewer, …)` per §4.5.
8. **Tests** (per §7).

## 7. Tests

The v1 design's tests would have shipped a "looks fixed" implementation that still committed to develop. v2 tests must execute the bug end-to-end. Each item below is a regression check for one of the gaps the reviewers caught.

### Unit tests

- `CodeWriteToolWrapper` constructor throws when `scopeRoot` doesn't exist.
- `CodeWriteToolWrapper` constructor throws when `scopeRoot` is not a worktree of the expected repo (mismatched `git common dir`).
- `CodeWriteToolWrapper.WriteFileAsync` with a temp-dir worktree-style `scopeRoot`: writes inside that dir, refuses paths that escape it, runs `git add` in that dir.
- `CodeReadToolWrapper.ReadFileAsync` with two distinct `scopeRoot`s pointing at directories with divergent file content: each instance reads only its own root's content (concrete read-isolation regression check — closes G2 / acceptance criterion #2 below).
- `AgentToolRegistry.GetToolsForAgent` with `workspacePath="/tmp/wt-A"` vs `"/tmp/wt-B"` produces wrappers capturing the corresponding roots.

### Integration tests

- **`commit_changes` lands on the worktree branch, not develop** (closes the v1 blind spot all three reviewers caught):
  1. Set up two real git worktrees of a temp repo.
  2. Build two `CodeWriteToolWrapper` instances with distinct `scopeRoot`s.
  3. Have each write one file and call `commit_changes`.
  4. Assert: each worktree's branch HEAD has exactly one new commit containing exactly the file the corresponding wrapper wrote; develop's HEAD is unchanged; the recorded artifact entries reference the worktree commit SHAs.
- **`RUN_TESTS` / `RUN_BUILD` / `READ_FILE` honour `CommandContext.WorkingDirectory`** (closes layer-3 gap): minimal handler tests that pass a `CommandContext` with a temp-dir `WorkingDirectory` and assert the handler's `ProcessStartInfo.WorkingDirectory` (or its file open path) matches.
- **Review-fix loop preserves `worktreePath`**: a `BreakoutCompletionService` test that exercises `HandleReviewRejectionAsync` and asserts the `IAgentExecutor.RunAsync` mock was called with the expected `workspacePath` (not null).

### Regression tests

- `write_file` from a session created with `workspacePath = null` still writes to the develop checkout (preserves main-room behaviour — G5).
- The pre-existing `CodeWriteToolWrapper` symlink-escape and protected-path tests still pass against a `scopeRoot`-rooted instance.

## 8. Risk and rollout

- **Blast radius**: every agent that uses `write_file`, `commit_changes`, `read_file`, `search_code`, or any of the 12 structured handlers in §2d. The change is invisible when `workspacePath` is null (current main-room behaviour preserved); it activates only when a breakout dispatch passes a worktree path — which today *does* happen but is silently ignored.
- **Failure mode after the fix**: a breakout dispatched without a `worktreePath` falls back to develop-checkout behaviour. During implementation, grep all breakout dispatch sites and confirm they all set `worktreePath` when one was created. (`BreakoutLifecycleService.RunBreakoutLifecycleAsync` already accepts `worktreePath` and passes it down — the upstream caller is `TaskAssignmentHandler` / `TaskOrchestrationService`. One-line audit during implementation.)
- **Roll-back**: revert the implementation PR. The design is purely additive at the API level (new optional parameters); revert restores the prior, broken-but-known behaviour. No data migration, no schema change.
- **Observability after rollout**: the new `cwd=` log field is the canary. A grep for `write_file`, `read_file`, and command-handler log lines in a multi-breakout session should show one cwd per breakout. If they all share the develop path, the plumbing regressed — likely a new caller forgot to pass `workspacePath` through.

## 9. Acceptance criteria

The fix is considered done when **all** of the following hold (v1 had 5; v2 adds two read-isolation criteria the reviewers requested):

1. **Write isolation, commits**: Two parallel breakouts on different tasks each commit only their own files to their own branches. Verified by `git log <branch>` showing the expected diff per branch and develop showing none of either.
2. **Read-after-write consistency** *(new in v2)*: Within a single breakout, an agent that writes a file via `write_file` and then reads the same path via `read_file` (SDK tool) and via `READ_FILE` (structured handler) sees the content it just wrote, not the develop copy.
3. **Build/test cwd correctness** *(new in v2)*: An agent that runs `RUN_BUILD` or `RUN_TESTS` from a worktree-backed breakout builds/tests the worktree, not develop. Verified by introducing a divergent test (passes only in worktree, fails in develop) and observing PASS.
4. **P1.9 acceptance script** (`scripts/p1-9-acceptance-check.sh`) advances past the steps that previously blocked on the merge-empty-diff symptom.
5. **Logs**: Server logs show distinct `cwd=` values per concurrent breakout's `write_file`, `read_file`, and command-handler invocations.
6. **Test suite**: server + client + forge passes; no fix:feat ratio degradation.
7. **Real merge diff**: `MERGE_TASK` for a completed worktree breakout produces a non-empty squash diff on the target branch.

This document is not "done" until the implementation PR lands and #1–#7 are verified on a fresh supervised P1.9 run.

## 10. Reviewer credits

This design was substantially expanded after a 3-reviewer adversarial pass on v1 (gpt-5.3-codex, claude-opus-4.6, gpt-5.5). The critical commit-via-`GitService`-singleton gap was independently caught by all three reviewers; the structured-command-handler parallel-bug expansion is gpt-5.5's; the review-fix loop omission is gpt-5.5's; the `_allToolNames` diagnostic update is opus's; the scopeRoot-must-be-a-worktree-of-this-repo validation is codex's; the read-isolation acceptance criterion is opus's and codex's. v1's identification of `IsResolvedPathInsideRoot` as the write-side symlink check was incorrect (writes use `DetectSymlinkEscape`); v2 corrects this — codex's catch.
