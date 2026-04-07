# Spec Changelog

All changes to specifications are documented here.

## [Unreleased]

### Changed
- **007-agent-commands**: Added RECORD_EVIDENCE, QUERY_EVIDENCE, CHECK_GATES to Phase 1C (Verification). Records structured verification checks against tasks with phase (Baseline/After/Review), check names, tool info, exit codes, and output. CHECK_GATES evaluates minimum evidence for status transitions. All 6 agents permitted. Human API allowlist updated. Permission model table updated with evidence command access. 23 new tests (1375 total). Committed in `42d4124`.
- **010-task-management**: Added §6.6 Evidence Ledger documenting the task evidence system. Covers TaskEvidenceEntity data model, EvidencePhase enum, gate definitions for status transitions, authorization rules, and invariants #10 (immutable evidence) and #11 (advisory gates).
- **010-task-management**: Added Invariant #9 documenting git-DB transaction ordering. Task metadata must not persist to database until git branch creation succeeds. Documents fix from commit `36e0dda` that moved `CreateTaskItemAsync` inside the try block after git operations complete. Prevents orphaned database records referencing non-existent branches.
- **001-domain-model**: Full entity inventory update — added 14 missing entity classes (AgentConfigEntity, AgentErrorEntity, AgentMemoryEntity, CommandAuditEntity, ConversationSessionEntity, InstructionTemplateEntity, LlmUsageEntity, NotificationConfigEntity, NotificationDeliveryEntity, ServerInstanceEntity, SpecTaskLinkEntity, SystemSettingEntity, TaskCommentEntity, WorkspaceEntity). Added 5 missing model files (AgentMemory, Commands, DirectMessages, HumanCommands, Requests). Added 2 missing enums (CommandStatus, SpecLinkType). Updated ActivityEventType with 5 new values. Updated AgentDefinition with GitIdentity and Permissions. Updated RoomSnapshot with Topic field. Updated Rooms.cs types (RoomMessagesResponse, ConversationSessionSnapshot, SessionListResponse, SessionStats). Updated System.cs types (ErrorSummary, AgentUsageSummary, LlmUsageRecord, InstanceHealthResult). Added AgentQuestion to Notifications. Fixed NotificationType location (Notifications.cs, not Enums.cs). Added 30+ missing database indexes. Updated 3 new entity relationships. Resolved Known Gaps for INotificationProvider and DTO mapping.
- **003-agent-system**: Renamed SDK tool `list_agents` → `show_agents` to avoid conflict with Copilot CLI built-in `list_agents` tool. All tool group tables, flow diagrams, and revision history updated.
- **003-agent-system**: Triaged all 14 known gaps (#11). Resolved 3 (token tracking, tool calling, session resume). Accepted 8 as design constraints (tool safety limits, no direct agent-to-agent, no streaming to UI, no hot-reload, no versioning). Identified 3 genuine low-priority gaps (no resource quotas, no prompt injection mitigation, no agent-level rate limiting).
- **000-system-overview**: Updated architecture diagram to reflect actual subsystems (WorkspaceRuntime, Orchestrator, Command Pipeline, CopilotExecutor, Notification Manager). Expanded component responsibilities table from 4 to 9 entries. Resolved diagram known gap.
- **002-development-workflow**: Marked branch protection gap as resolved (scripts/protect-branches.sh). Marked pre-push hook as accepted constraint. Added #10 cross-reference.
- **004-notification-system**: Marked 3 gaps as resolved/accepted (insertion order, Discord freeform OwnerId scoping, DiceBear fallback).
- **005-workspace-runtime**: Marked 3 gaps as resolved/accepted (real-time push via SignalR/SSE, per-instance buffer, legacy rooms).
- **011-state-recovery**: Marked restart loop prevention as resolved (wrapper.sh exponential backoff).

### Fixed
- **006-orchestrator**: Fixed `HandleDirectMessage` signature (takes `recipientAgentId` only, not `agentId, roomId`). Corrected breakout loop caps (`MaxBreakoutRounds=200`, `MaxConsecutiveIdleRounds=5`) — body text previously said "no round cap" while Known Gaps said resolved. Fixed DM handling in breakout rooms (posted as messages, not injected into prompt). Added constants table.
- **007-agent-commands**: Updated handler count from 24 to 50. Fixed pipeline description to include rate limit stage. Fixed audit target from `ActivityEvent` to `CommandAuditEntity`. Fixed DM syntax to match parser format (indented `Key: value` lines, not comma-separated). Fixed `CLOSE_ROOM` role permission (Planner or Human, not Planner-only). Updated memory commands to include `EXPORT_MEMORIES`, `IMPORT_MEMORIES`, TTL, FTS5 search, shared category.
- **003-agent-system**: Updated `IAgentExecutor` interface to include `IsAuthFailed`, `CircuitBreakerState`, `MarkAuthDegradedAsync`, `MarkAuthOperationalAsync`, `InvalidateAllSessionsAsync`.
- **008-agent-memory**: Removed false claim that FORGET requires confirmation step and audit logging (it does neither).
- **012-consultant-api**: Fixed `/api/commands/metadata` description (returns allowlisted+implemented, not "all"). Fixed room messages endpoint (includes sessionless and User messages cross-session, not session-restricted). Noted archived rooms excluded by default from `GET /api/rooms`.
- **010-task-management**: Fixed `REBASE_TASK` permissions (Assignee/Planner/Reviewer/Human, not Any). Fixed `CANCEL_TASK` permissions (added Human role). Fixed `APPROVE_TASK` room message (conditional on findings). Added note about TaskEntity JSON-serialized list fields.
- **004-notification-system**: Added `SendDirectMessageAsync` to `INotificationProvider` interface. Added `/deliveries` and `/deliveries/stats` REST endpoints. Added `RoomClosed` to tracked delivery channels (was 4, now 5). Fixed `RequestInputFromAnyAsync` iteration order note.
- **001-domain-model**: Fixed `UsageSummary.TotalCost` type from `decimal` to `double`.
- **002-development-workflow**: Noted version sync gap between .NET (`0.1.0`) and client (`0.0.0`).
- **011-state-recovery**: Added `circuitBreakerState` to health endpoint response.
- **300-frontend-ui**: Added missing tabs and components (DmPanel, SettingsPanel, AgentSessionPanel, CommandPalette, RecoveryBanner, CircuitBreakerBanner, directMessages tab).

### Added
- **012-consultant-api**: Task management commands — Added `UPDATE_TASK`, `CANCEL_TASK`, `APPROVE_TASK` to the human command allowlist in `CommandController`. `CancelTaskHandler` now accepts Human role (was Planner/Reviewer only). `HumanCommandRegistry` updated with metadata entries for dynamic UI rendering. Documented full command execution surface in spec 012 including allowlisted commands table, async command polling, and identity semantics.

### Added
- **003-agent-system**: Per-project session resume — On workspace switch, `ConversationSessionService.ArchiveAllActiveSessionsAsync()` summarizes all active conversation sessions via LLM before clearing SDK sessions. When the user returns to a previous project, `GetSessionContextAsync()` retrieves the archived summary and the orchestrator injects it into agent prompts for context continuity. Empty sessions (0 messages) archived without summaries. Fallback summaries generated when executor is offline. No schema changes — reuses existing `conversation_sessions` table. 8 new tests (1298 total). Resolves spec 003 known gap: "No per-project session resume".

### Added
- **003-agent-system**: Circuit breaker for CopilotExecutor — Global circuit breaker prevents burning through retries when the Copilot API is consistently failing. Three states: Closed (normal), Open (immediate fallback), HalfOpen (one probe after 60s cooldown). Trips after 5 consecutive failures (quota, transient, or unknown errors). Auth errors do NOT trip the circuit (separate recovery pathway). Auto-resets on token change. State exposed in `GET /api/health/instance` (`CircuitBreakerState` field) and `IAgentExecutor.CircuitBreakerState`. Open-circuit events recorded in `AgentErrors` with type `circuit_open`. 22 new tests (1289 total). Resolves spec 007 known gap: "Error recovery" (partially — circuit breaker and retry semantics now documented).
- **300-frontend-ui**: Circuit breaker UI indicator — Frontend visibility for the circuit breaker state. `useCircuitBreakerPolling` hook polls `/api/health/instance` with adaptive intervals (60s normal, 10s degraded), request ID guard for stale response rejection, and visibility API gating for background tabs. Three visual indicators: floating `CircuitBreakerBanner` (red/amber gradient with aria-live=assertive), header signal chip in workspace header, and status row in `ErrorsPanel`. 24 new frontend tests (181 total).
- **300-frontend-ui**: Dashboard sparkline trend charts — Mini SVG sparkline visualizations in UsagePanel (request count + token volume), ErrorsPanel (error rate), and AuditLogPanel (command count). Client-side time-bucketing via `bucketByTime`/`bucketByTimeSum` utilities with invalid timestamp validation. `Sparkline.tsx` renders accessible polyline + gradient fill with unique IDs via `useId()`. AuditLogPanel uses separate 200-record fetch for accurate trend data. 21 new frontend tests (202 total).

### Added
- **007-agent-commands**: Command audit log — `GET /api/commands/audit` endpoint returns paginated, filterable command audit records (by agentId, command, status, hoursBack, with limit/offset pagination). `GET /api/commands/audit/stats` returns aggregate statistics grouped by status, agent, and command. `AuditLogPanel` on Dashboard shows stat cards (total, success, errors, denied), breakdowns by agent and top commands, and a paginated table of recent command records with status badges, error details, and source indicators (agent vs human-ui). Reuses existing `CommandAuditEntity` data — no schema migration needed. 15 new backend tests (1266 total), 19 new frontend tests (157 total).

### Added
- **004-notification-system**: Slack notification provider — `SlackNotificationProvider` delivers notifications, agent questions, and DMs via the Slack Web API using raw `HttpClient` (no external NuGet dependency). `SlackApiClient` wraps 8 Slack API methods with typed responses and built-in 429 rate-limit retry (2 attempts with Retry-After). Room-based channel routing with lazy creation and negative-cache fallback. Startup recovery via channel topic parsing. Agent identity via `username`/`icon_emoji` (requires `chat:write.customize` scope). Block Kit message formatting with mrkdwn escaping. Channel lifecycle: create, rename, archive. Uses `IHttpClientFactory` for DNS rotation and socket management. `volatile` state flags for cross-thread visibility. 59 new tests (1251 total). 3 adversarial reviews (GPT-5.3 Codex, Claude Opus 4.6, Claude Sonnet 4.6) — 14 unique findings, all fixed.
- **004-notification-system**: Multi-provider setup wizard — `NotificationSetupWizard` refactored from Discord-only to accept `providerId` prop. Fetches config schema dynamically from backend API. Provider-specific setup instructions for Discord (Developer Portal + invite URL generator) and Slack (app creation + OAuth scopes). Generic fallback for unknown providers. Dynamic credential form from schema fields. 19 new frontend tests (138 total).

### Added
- **007-agent-commands / 009-spec-management**: Spec-task linking — `SpecTaskLinkEntity` junction table tracks which spec sections each task implements, modifies, fixes, or references. `LINK_TASK_TO_SPEC` command creates/updates links with spec existence validation and upsert semantics. `SHOW_UNLINKED_CHANGES` command lists active tasks without spec links for drift detection. `SpecManager.LoadSpecContextForTaskAsync` filters spec context injection to mark linked sections with ★. REST endpoints `GET /api/tasks/{id}/specs` and `GET /api/specs/{sectionId}/tasks` for querying links. Cascade delete on task removal. Unique constraint on (taskId, specSectionId). `SpecTaskLinked` activity event. Both commands registered in HumanCommandRegistry and CommandController allowlist. 36 new tests (1192 total). Resolves spec 007 Phase 2 gap: "Spec-task linking".
### Added
- **003-agent-system**: Agent write tools — Extends SDK tool calling with 5 write tools in 2 new groups. `task-write` group: `create_task` (creates tasks via `WorkspaceRuntime.CreateTaskAsync`), `update_task_status` (status updates with safe-status restriction — cannot set Completed/Cancelled), `add_task_comment` (comments/findings/evidence/blockers). `memory` group: `remember` (upsert with category validation, optional TTL), `recall` (FTS5 search with BM25 ranking, LIKE fallback, shared visibility, expiry filtering). Write tools use inner wrapper classes (`TaskWriteToolWrapper`, `MemoryToolWrapper`) that capture agent identity via closures at session creation — agents cannot impersonate others. `IAgentToolRegistry.GetToolsForAgent` now accepts optional `agentId`/`agentName` for contextual groups. All 6 agents updated with `task-write` + `memory` enabled. Reuses existing `RememberHandler.ValidCategories` and `RecallHandler.SearchWithFts5Async` to avoid logic duplication. 35 new tests (1154 total).
- **003-agent-system**: SDK tool calling — `AgentToolRegistry` maps agent `EnabledTools` groups to Copilot SDK `AIFunction` objects. 5 read-only tools: `list_tasks`, `list_rooms`, `list_agents` (task-state group, all agents), `read_file`, `search_code` (code group, engineers + writer). Tools call into existing services (`WorkspaceRuntime`) via `IServiceScopeFactory`. `AgentPermissionHandler` replaces `PermissionHandler.ApproveAll` with a deny-by-default handler that approves only safe permission kinds (custom-tool, read, tool) and denies dangerous ones (shell, write, url). `CopilotExecutor.GetOrCreateSessionEntryAsync` passes resolved tools in `SessionConfig.Tools`. Security: path traversal denied in both read_file and search_code, FindProjectRoot throws on missing .sln (fail-closed), search_code reads line-by-line with global cap + timeout + stderr drain (prevents deadlock), fixed-string search by default. Adversarial review (3 models, 14 total findings, 7 fixed). 34 new tests (1119 total). Resolves spec 003 known gap: "No tool calling".

### Added
- **010-task-management**: MERGE_PR command — squash-merge task PRs via the GitHub API (`gh pr merge --squash`). Added `MergePullRequestAsync` + `PrMergeResult` to `IGitHubService`. `MergePrHandler` validates Approved status + PR existence, merges via GitHub API, updates PR status to Merged, completes task with merge commit SHA. Reverts to Approved on failure. Role gate: Planner/Reviewer/Human. Optional `deleteBranch` flag. Registered in `HumanCommandRegistry`, `CommandController` allowlist (async), and `CommandParser`. 25 new tests (1083 total). Resolves spec 010 Phase 2 gap: "No PR merge via API".

### Added
- **010-task-management**: PR review comments — `POST_PR_REVIEW` command posts reviews (approve/request changes/comment) on a task's GitHub PR via `gh pr review`. `GET_PR_REVIEWS` command fetches review history via `gh pr view --json reviews`. Added `PullRequestReview` record (Author, Body, State, SubmittedAt) and `PrReviewAction` enum to `IGitHubService`. Role gates: POST restricted to Planner/Reviewer/Human (engineers cannot self-review); GET allows assigned agent too. Both registered in `HumanCommandRegistry` and `CommandController` allowlist. `ListCommandsHandler` descriptions updated. 40 new tests (1057 total). Resolves spec 010 Phase 2 gap: "No review comments".

### Added
- **010-task-management**: PR status sync via polling — `PullRequestSyncService` background service polls GitHub every 2 minutes for PR status changes on tasks with active (non-terminal) PRs. Uses `gh pr view --json reviewDecision` to detect review state. Maps GitHub states to `PullRequestStatus` enum: `REVIEW_REQUIRED` → `ReviewRequested`, `APPROVED` → `Approved`, `CHANGES_REQUESTED` → `ChangesRequested`, merged → `Merged`, closed → `Closed`. Added `ReviewDecision` field to `PullRequestInfo` record. Added `SyncTaskPrStatusAsync` and `GetTasksWithActivePrsAsync` to `WorkspaceRuntime`. Emits `TaskPrStatusChanged` activity event on status transitions. Frontend refreshes task list on `TaskPrStatusChanged`. Error isolation: single PR failure doesn't block others. CancellationToken checks between PR polls for clean shutdown. 36 new tests (1017 total). Resolves spec 010 Phase 2 gap: "No PR status sync".

### Added
- **010-task-management**: GitHub PR integration (Phase 1) — `IGitHubService` / `GitHubService` wraps `gh` CLI for PR operations. `CREATE_PR` agent command pushes task branch to remote via `GitService.PushBranchAsync` and opens a GitHub pull request via `gh pr create`. Updates task entity with PR URL, number, and `Open` status. Role-gated to assigned agent, Planner, Reviewer, or Human. `GET /api/github/status` REST endpoint reports `gh` CLI auth status and repository slug. `CREATE_PR` added to `HumanCommandRegistry` (frontend command palette) and `CommandController` allowlist (async execution). 23 new tests (980 total). Resolves spec 010 known gaps: "GitHub PR integration not implemented" and "No remote push capability".

### Added
- **003-agent-system**: LLM usage tracking — `LlmUsageTracker` captures `AssistantUsageEvent` from Copilot SDK on every LLM API call (including session priming). Persists per-request metrics (model, input/output/cache tokens, cost, duration, API call ID, reasoning effort) to `llm_usage` table with indexes on agent, room, and time. Room-level aggregation via `GET /api/rooms/{id}/usage`, per-agent breakdown via `GET /api/rooms/{id}/usage/agents`, individual records via `GET /api/rooms/{id}/usage/records`. Global usage via `GET /api/usage` with `hoursBack` filter (1–8760), global records via `GET /api/usage/records` with agent filter. Replaces zeroed stubs in `RoomController`. Safe numeric conversion handles NaN/Infinity SDK payloads. Recording failures logged but never propagate to agent execution. Resolves spec 003 known gap: "No token/usage tracking". 20 new tests (938 total).

### Added
- **005-workspace-runtime / 007-agent-commands**: Task item management commands — `CREATE_TASK_ITEM`, `UPDATE_TASK_ITEM`, and `LIST_TASK_ITEMS` commands expose task item CRUD to agents. `CREATE_TASK_ITEM` accepts title, description, assignedTo (resolved via agent catalog by ID or name), roomId (validated for existence), and breakoutRoomId. `UPDATE_TASK_ITEM` validates caller is assignee/Planner/Reviewer/Human and accepts status + optional evidence. `LIST_TASK_ITEMS` supports room and status filters, scoped to active workspace. `UpdateTaskItemStatusAsync` now throws on missing items (was silent no-op). Added `GetTaskItemAsync` and `GetTaskItemsAsync` to WorkspaceRuntime. Registered in `HumanCommandRegistry` and `ListCommandsHandler`. 27 new tests (918 total). Resolves spec 005 known gap: "No task item management".
- **010-task-management**: REBASE_TASK command + MERGE_TASK conflict reporting — `REBASE_TASK` rebases task feature branches onto develop with conflict detection and abort-on-failure. Dry-run mode (`dryRun=true`) checks for conflicts without modifying the branch. `MergeConflictException` carries conflicting file paths for actionable error messages. `DetectMergeConflictsAsync` performs non-destructive conflict checks via dry-run merge. `MERGE_TASK` now detects conflicting files on failure and suggests `REBASE_TASK` in error response. Registered in `HumanCommandRegistry` with field metadata. Resolves spec 010 known gap: "Conflict resolution during MERGE_TASK is abort-only". 18 new tests (888 total).

### Added
- **300-frontend-ui**: Interactive task review panel — `TaskListPanel.tsx` upgraded from read-only to interactive review queue. Filter bar (All/Review Queue/Active/Completed) with counts. Expandable task cards with full detail view (description, success criteria, implementation/validation summaries, tests created). Task comments fetched from `GET /api/tasks/{id}/comments` with error handling and retry. Review action buttons (Approve/Request Changes/Reject/Merge) contextual to task status, wired through `executeCommand` API. Reason textarea for findings/rejection with validation. Review metadata display (round count, reviewer, merge commit SHA). Resolves spec 300 future work: "TaskStatePanel integration". 18 new frontend tests (73 total).
- **008-agent-memory**: Memory import/export — `EXPORT_MEMORIES` and `IMPORT_MEMORIES` agent commands for bulk memory operations. `ExportMemoriesHandler` exports calling agent's memories as structured JSON with optional category filter. `ImportMemoriesHandler` accepts JSON array, validates categories against `RememberHandler.ValidCategories`, enforces 500-char value limit, 500-entry cap, upsert semantics. REST endpoints at `GET /api/memories/export?agentId=X` (requires agentId) and `POST /api/memories/import` (requires auth, 500-entry cap). `EXPORT_MEMORIES` added to human command registry. Error reporting capped at 50 messages. Category normalization to lowercase. Resolves spec 008 known gap: "Memory import/export". 14 new backend tests (852 total).
- **008-agent-memory**: Memory decay/TTL — Optional TTL (time-to-live in hours, 1–87600) on REMEMBER and IMPORT_MEMORIES. `ExpiresAt` field on `AgentMemoryEntity`. Expired memories excluded from RECALL, LIST_MEMORIES, and prompt injection. `include_expired` flag for accessing expired entries. `LastAccessedAt` tracking on all read paths (batched per-agent for efficiency). Staleness detection: memories inactive 30+ days without TTL tagged `⚠️STALE` in prompt injection and flagged in API responses. REST cleanup endpoint `DELETE /api/memories/expired?agentId=X`. Export includes expiry/stale metadata. Updating without TTL preserves existing expiry. Migration `AddMemoryDecayTtl`. Resolves spec 008 known gap: "Memory decay". 18 new backend tests (870 total).

### Added
- **008-agent-memory**: Cross-agent shared memory — `shared` category enables universal knowledge visible to all agents. REMEMBER with `category=shared` stores cross-agent knowledge. RECALL and LIST_MEMORIES include shared memories from all agents (with source `agentId`). Prompt injection shows shared memories in separate `=== SHARED KNOWLEDGE ===` section. FORGET still scoped to own memories only. FTS5 search includes shared memories. Resolves known gap: "Cross-agent knowledge sharing". 11 new tests (837 total).
- **007-agent-commands**: Command palette — `Cmd+K` / `Ctrl+K` opens a searchable overlay for discovering and executing commands. Text search across title/name/description/category, keyboard navigation (↑/↓/Enter/Esc), grouped by category, detail view with field inputs, `Cmd+Enter` to execute, async polling, inline results. Dynamic catalog from metadata endpoint with hardcoded fallback. 13 new frontend tests (55 total).

### Changed
- **008-agent-memory**: Upgraded RECALL search from LIKE to FTS5 full-text search with BM25 ranking. Word-boundary matching replaces substring matching. Multi-word queries use AND semantics. FTS5 virtual table (`agent_memories_fts`) synced via INSERT/UPDATE/DELETE triggers. Graceful LIKE fallback if FTS5 unavailable. Migration `AddMemoryFts5Search`. Known gap resolved. 10 new tests (813 total).

### Added
- **007-agent-commands**: Command metadata endpoint — `GET /api/commands/metadata` returns `HumanCommandMetadata[]` with title, category, description, detail, isAsync, and field schemas. Server-side `HumanCommandRegistry` is the single source of truth for all 18 human-executable commands. Endpoint filters by both allowlist and registered handlers. Frontend `CommandsPanel` loads dynamically with hardcoded fallback. Resolves spec 007 known gap #2 and spec 300 future work item. 13 new backend tests, 3 new frontend tests.
- **005-workspace-runtime**: Stale room cleanup — auto-archive rooms when all tasks reach terminal state (Completed/Cancelled). `CompleteTaskAsync` calls `TryAutoArchiveRoomAsync` to check and archive. Agents evacuated to workspace default room. Manual cleanup via `CleanupStaleRoomsAsync()`, `CLEANUP_ROOMS` command (Planner/Human only), and `POST /api/rooms/cleanup`. `RejectTaskAsync` reopens auto-archived rooms. `GetRoomsAsync` now excludes archived rooms by default (`includeArchived` parameter). Resolves known gap: "No room cleanup for stale completed rooms". 15 new tests.

### Added
- **004-notification-system**: Config encryption at rest — `ConfigEncryptionService` encrypts secret provider config values (e.g., Discord bot tokens) using ASP.NET Core Data Protection API before DB persistence. Versioned `ENC.v1:` prefix enables transparent migration from plaintext. `TryDecrypt` API distinguishes decrypt failure from empty values. Explicit Data Protection key-ring persistence. 19 new tests.
- **011-state-recovery**: Server-side restart rate limiting — `RestartServerHandler` enforces max 10 intentional restarts per hour with `SemaphoreSlim`-guarded check against `ServerInstances` table. Returns `RATE_LIMIT` error when exceeded. Prevents infinite restart loops independent of wrapper script.
- **011-state-recovery**: Restart history API — `GET /api/system/restarts` (paginated instance history with derived shutdown reason: Running/IntentionalRestart/CleanShutdown/Crash/UnexpectedExit) and `GET /api/system/restarts/stats` (aggregated counts by type with configurable time window). SQL-level aggregation for scalability. 18 new tests.

### Changed
- **004-notification-system**: Marked notification endpoint auth gap as resolved — system-wide `FallbackPolicy` already covers all endpoints without `[AllowAnonymous]`.

### Added
- **004-notification-system**: Notification delivery tracking — `NotificationDeliveryTracker` records every outbound notification attempt per provider to `notification_deliveries` table. Tracks 4 channels (Broadcast, AgentQuestion, DirectMessage, RoomRenamed) with Delivered/Skipped/Failed status. REST API endpoints for delivery history (`GET /api/notifications/deliveries`) and stats (`GET /api/notifications/deliveries/stats`). `NotificationManager` integrated with optional tracker dependency. 18 new tests.
- **004-notification-system**: Room channel cleanup — `OnRoomClosedAsync` on `INotificationProvider` and `NotifyRoomClosedAsync` on `NotificationManager`. Discord provider deletes channel, disposes webhook, clears caches. `ActivityNotificationBroadcaster` routes `RoomClosed` events. 7 new tests.

### Added
- **006-orchestrator**: Queue reconstruction on startup — `ReconstructQueueAsync()` runs on every server startup and re-enqueues rooms with unanswered human messages. `WorkspaceRuntime.GetRoomsWithPendingHumanMessagesAsync()` queries for rooms where the latest message has `SenderKind = User`. Prevents message loss on crash or clean restart. 8 new tests.
- **006-orchestrator**: Breakout failure surfacing — fire-and-forget breakout loops now catch unhandled exceptions and run `HandleBreakoutFailureAsync`, which closes the breakout with `Failed` reason, marks the linked task as `Blocked`, and posts a failure notification to the parent room. Added `Failed` to `BreakoutRoomCloseReason`.
- **010-task-management**: Agent git identity attribution — `GitService.CommitAsync` and `SquashMergeAsync` now accept `AgentGitIdentity` and pass `--author` flag to git. `CommandContext` carries identity from `AgentDefinition.GitIdentity`. Wired through `ShellCommandHandler` (SHELL git-commit) and `MergeTaskHandler` (MERGE_TASK). Known Gap resolved. 1 new test.
- **011-state-recovery**: Frontend reconnect UX — global `RecoveryBanner` with 4 tones (reconnecting, syncing, crash, error) renders above all tabs. `healthCheck.ts` module encapsulates `evaluateReconnect()` logic for instance-mismatch, crash-recovery, and resume-success states. Shows reconnecting banner immediately on SignalR disconnect. Crash-recovered state surfaces `crashDetected` from health endpoint with extended visibility. Spec 011 status → Implemented. 7 new frontend tests.

### Added
- **011-state-recovery**: Breakout stuck-detection — `AgentOrchestrator` now tracks consecutive idle rounds (zero commands parsed) and enforces an absolute max-round cap (200). After 5 consecutive idle rounds, closes breakout with `StuckDetected`, marks linked task as `Blocked`, and notifies the parent room. Prevents infinite agent loops and resource waste. 3 new tests.
- **007-agent-commands**: `ROOM_TOPIC` command — any agent can set or clear a room's topic description. DB migration adds `Topic` column to rooms. `RoomSnapshot` model updated with `topic` field. `RESTORE_ROOM` consolidated into existing `REOPEN_ROOM`. All Tier 2 room commands now implemented. 5 new tests.

### Added
- **007-agent-commands**: `INVITE_TO_ROOM` command — planners and humans can move a specified agent to a specified room. Validates room exists/not archived, agent exists/not in breakout. No-op if already in room. Posts system message. Added to human command allowlist. 12 new tests.
- **007-agent-commands**: `RETURN_TO_MAIN` command — any agent can return to the main collaboration room. Syntactic sugar for MOVE_TO_ROOM with DefaultRoomId. No-op if already there. 3 new tests.

### Changed
- **010-task-management**: Reconciled spec with code — Partial → Implemented. Documented all TaskSnapshot/TaskEntity fields (including `AwaitingValidation` status, `CommentCount`, `RoomId`, validation/implementation status fields). Added command table (section 10) and WorkspaceRuntime method index (section 11). Documented auto-spec dedup via `FindTaskByTitleAsync`. Noted `APPROVE_TASK`/`REQUEST_CHANGES` lack role gates (convention only). Documented write-once invariants for branch and task identity. Removed non-existent API endpoints (`PUT /tests`, review REST endpoints). Updated frontend section to reference implemented `TaskListPanel`/`TaskStatePanel` components.

### Added
- **007-agent-commands**: Room-as-context — `CREATE_ROOM` command lets planners (and humans via UI) create persistent collaboration rooms as work contexts, without requiring a task first. `REOPEN_ROOM` restores archived rooms for continued work. `LIST_ROOMS` now supports `status=` filter. `CLOSE_ROOM` also accepts human role for frontend use. Planner system prompt updated with room lifecycle guidance (when to create, reopen, and close rooms). 13 new tests.

### Added
- **007-agent-commands**: Structured error codes — every command failure now includes an `errorCode` string field (`VALIDATION`, `NOT_FOUND`, `PERMISSION`, `CONFLICT`, `TIMEOUT`, `EXECUTION`, `INTERNAL`) so agents can programmatically branch on error type instead of parsing message strings. Applied across all 24 handlers, the command pipeline, the command authorizer, and the human command API. Frontend shows error code as a badge. String constants (not enum) for extensibility.
- **007-agent-commands**: ErrorCode audit persistence — `CommandAuditEntity.ErrorCode` column added so async command polling and audit history return structured error codes. All 4 audit write paths (agent pipeline, human sync, human async update) and the read path (`ToResponse`) now persist/return the error code. Migration `20260404083032_AddCommandAuditErrorCode`.
- **007-agent-commands**: Per-agent command rate limiting — sliding-window rate limiter (30 commands/60s) prevents agents from spamming commands. `RATE_LIMIT` error code added. Integrated into `CommandPipeline` after authorization. 6 new tests.

### Fixed
- **007-agent-commands**: DM duplication eliminated — added per-recipient `AcknowledgedAt` tracking to `MessageEntity`. `GetDirectMessagesForAgentAsync` defaults to `unreadOnly=true`, filtering to unacknowledged DMs where the agent is the recipient (not sender). `AcknowledgeDirectMessagesAsync` takes explicit message IDs to prevent race conditions between fetch and ack. Breakout forwarding now posts all unread DMs (not just the last one). 4 new tests.
- **007-agent-commands**: `READ_FILE` auto-truncates large file content at 12,000 characters. Returns `truncated=true` and a continuation hint with `startLine` for the next chunk. Prevents agents from blowing out their context window. 8 new handler tests.
- **007-agent-commands**: `SEARCH_CODE` now supports `ignoreCase: true` for case-insensitive git grep. Results exceeding the 50-match cap include `truncated=true` with a hint to narrow the query.
- **007-agent-commands**: `SHOW_REVIEW_QUEUE` now returns task description, type, branch name, commit count, and comment count — enough context for reviewers to begin work without extra queries.

### Added
- **007-agent-commands**: Human Command Execution API — `POST /api/commands/execute` and `GET /api/commands/{correlationId}` endpoints for Week 1 frontend Commands tab. 11 allowlisted commands (all read-only + RUN_BUILD/RUN_TESTS). CommandController bypasses agent pipeline, uses controller-level allowlist and cookie auth. Async commands (build/test) return 202 Accepted with polling. Added `CommandAuditEntity.Source` field to distinguish human-ui from agent invocations. Build/test handlers serialized via SemaphoreSlim.
- **300-frontend-ui**: Commands tab — added `CommandsPanel.tsx` to the workspace shell for the 11-command Week 1 human allowlist. The client hardcodes command metadata, submits scalar args only, polls async build/test executions every 2.5 seconds, keeps the last 10 runs in a result rail, and leaves the tab readable-but-disabled during degraded Copilot sessions.

### Fixed
- **007-agent-commands**: `SEARCH_CODE` switched from `grep -rn` to `git grep`. Respects `.gitignore` (skips `node_modules`, `bin`, `obj`), only searches tracked files, skips binary files. Invalid path args now return an actionable error message instead of silently returning zero results.
- **007-agent-commands**: `READ_FILE` now supports directory paths — returns a listing of directory entries instead of "File not found".
- **006-orchestrator / 010-task-management**: Fixed breakout task metadata contamination during overlapping assignment/setup. Breakout rooms now persist and reuse `TaskId` as the only task-identity source during branch setup, and `TaskEntity.BranchName` is now write-once with conflict logging instead of being replaceable by later context-derived writes.
- **007-agent-commands**: Confirmed MERGE_TASK role authorization enforcement (commit 52419d8). Handler guards Planner/Reviewer access at lines 25-31. Updated spec table with implementation reference and clarified "ship together" design principle scope.
- **007-agent-commands / 010-task-management**: `MERGE_TASK` now formats squash-merge commit messages as conventional commit subjects derived from `TaskEntity.Type` (`feat:`, `fix:`, `chore:`, `docs:`), preventing commit-msg hook rejections during reviewer/planner merges.
- **006-orchestrator / 010-task-management**: Re-enabled breakout room creation on task assignment. Fixed command processing order in `RunBreakoutLoopAsync` — commands (including `SHELL git-commit`) now execute while still on the task branch, not after switching back to `develop`. Updated spec sections to remove "disabled" language.

### Changed
- **010-task-management**: Removed unimplemented GitHub PR integration content from spec. Sections describing PR creation, review via GitHub API, and remote push workflows marked as "Planned" or rewritten to describe actual local branch workflow with `MERGE_TASK`. PR metadata fields remain in task model for future use.
- **300-frontend-ui**: Main workspace shell now uses an editorial war-room redesign. The client ships new global design tokens in `index.css`, a wider navigation rail (`356px` open / `94px` collapsed), a stronger masthead/spotlight hierarchy, brass-and-slate panel treatments, and aligned styling across dashboard, overview, tasks, commands, chat, and compose surfaces while preserving the existing auth and tab contracts.

### Fixed
- **011-state-recovery / 300-frontend-ui**: Restored degraded-session limited mode in the frontend render contract. `copilotStatus = degraded` now keeps the workspace shell visible with an in-shell reconnect banner while chat sends, DM sends, and phase transitions stay paused; `LoginPage` is reserved for the fully unavailable sign-in path.
- **011-state-recovery / 300-frontend-ui**: Frontend auth recovery is now automatic. The app polls `/api/auth/status` every 30 seconds, redirects to `/api/auth/login` on `operational` → `degraded` transitions when the browser session still exists, debounces the redirect once per tab, and suppresses auto re-auth after explicit logout.
- **003-agent-system / 011-state-recovery**: Added proactive SDK auth-expiry detection. A hosted `/user` probe now runs every 5 minutes, treats only HTTP `401/403` as definitive auth failure, leaves transient network/server issues alone, and sends Discord-backed notifications only when auth transitions between `operational` and `degraded`.
- **006-orchestrator / 010-task-management**: Disabled automatic breakout-room creation during task assignment. Assignments now create the task item and task branch, post the status notice in the main room, and keep the assignee in the main collaboration room until breakout reliability is restored.
- **011-state-recovery**: Crash-detected startup now runs server-side recovery. Active breakout rooms close with persisted `ClosedByRecovery` reason, lingering `Working` agents reset to `Idle`, and the main room receives a "System recovered from crash" notification before orchestration resumes.
- **011-state-recovery / 300-frontend-ui**: Refactored auth gating to use `copilotStatus` (`operational` / `degraded` / `unavailable`) from `/api/auth/status`. The backend now derives the state from browser auth + Copilot SDK readiness, keeps `authenticated` fail-closed when degraded, and the login UI distinguishes first-time sign-in from re-authentication.
- **300-frontend-ui**: Authentication screens now surface a three-part status summary (browser identity, Copilot runtime, workspace access) so degraded and unavailable states are visually distinct, with render-level tests covering both login paths.
- **007-agent-commands**: `GitService.SquashMergeAsync()` now runs `git add -A` before the squash-merge commit so the full merge result is staged consistently before `MERGE_TASK` records `mergeCommitSha`.
- **001-domain-model**: Updated `TaskSnapshot` signature to match actual code — added 19 missing fields (Type, Size, StartedAt, CompletedAt, AssignedAgentId, AssignedAgentName, UsedFleet, FleetModels, BranchName, PullRequestUrl, PullRequestNumber, PullRequestStatus, ReviewerAgentId, ReviewRounds, TestsCreated, CommitCount, MergeCommitSha, CommentCount) per `src/AgentAcademy.Shared/Models/Tasks.cs`
- **001-domain-model**: Removed non-existent `TaskId` parameter from `BreakoutRoom` signature (does not exist in `src/AgentAcademy.Shared/Models/Rooms.cs`)
- **001-domain-model**: Added missing enums to spec: `TaskType`, `TaskSize`, `PullRequestStatus`, `TaskCommentType` (were already in code, missing from spec)
- **010-task-management**: Rewrote Overview (lines 11-22) to describe the delivered branch-based local squash-merge workflow instead of the aspirational PR-based workflow
- **010-task-management**: Updated Post-Approval section to document actual `MERGE_TASK` command flow with `GitService.SquashMergeAsync()` instead of PR merge
- **010-task-management**: Removed false invariants requiring `PullRequestNumber` or `PullRequestStatus == Merged` for task completion — actual system uses `MergeCommitSha` from local squash-merge

### Added
- **003-agent-system**: Conversation Session Management — epoch-based session boundaries with LLM summarization. When message count exceeds configurable threshold (default 50 main/30 breakout), conversation is summarized and a new session begins. SDK sessions are invalidated at rotation boundaries to reset accumulated context. System agent identity (`system-summarizer`) used for summarization. Fallback to structural summary when Copilot is offline.
- **003-agent-system**: Prompt deduplication — `BuildConversationPrompt` and `BuildBreakoutPrompt` no longer include `agent.StartupPrompt` (already sent during SDK session priming). Eliminates the largest source of redundant context accumulation.
- **005-workspace-runtime**: Session-aware message loading — `BuildRoomSnapshotAsync` loads only messages from the active conversation session. Messages tagged with `SessionId` via `PostMessageAsync`/`PostHumanMessageAsync`/`PostBreakoutMessageAsync`.
- **005-workspace-runtime**: `ConversationSessionEntity` table — tracks epoch boundaries (Id, RoomId, RoomType, SequenceNumber, Status, Summary, MessageCount). `SystemSettingEntity` table — key-value store for configurable settings.
- **006-orchestrator**: Epoch-aware round logic — `CheckAndRotateAsync` called before conversation rounds (round 1 for main rooms, every round for breakouts). Session summary injected into prompts via `=== PREVIOUS CONVERSATION SUMMARY ===` section.

### Changed
- **011-state-recovery**: Added Section 8 (Auth Retry vs Restart Escalation) documenting CopilotExecutor's token-based authentication recovery strategy. Authentication failures trigger user re-authentication flow instead of server restart. Auth/authorization exceptions never retried; transient errors retry with exponential backoff (2s→4s→8s); quota errors retry with exponential backoff (5s→15s→30s). Health endpoint `authFailed` flag exposed for client prompts. Added Invariant 10 for auth recovery without restart policy.

### Added
- **003-agent-system**: CopilotExecutor error classification — `SessionErrorEvent.ErrorType` now parsed into typed exceptions (`CopilotAuthException`, `CopilotTransientException`, `CopilotQuotaException`). Transient and quota errors retried with exponential backoff. Auth failures trigger user notification and auto-recovery on re-login.
- **007-agent-commands**: `RESTART_SERVER` command (Phase 1F) — Planner-only command triggers graceful server restart with exit code 75. Posts system message, uses `IHostApplicationLifetime.StopApplication()`.
- **011-state-recovery**: Implemented wrapper script (`wrapper.sh`), server instance tracking (`ServerInstanceEntity` + crash detection), `GET /api/health/instance` endpoint, and `IHostApplicationLifetime` shutdown hook. Exit code table updated: crash restarts with exponential backoff (code 1+), max 5 attempts.

### Changed
- **003-agent-system**: `CopilotTokenProvider` now tracks `TokenSetAt` timestamp. `CopilotExecutor` takes `IServiceScopeFactory` for scoped service access (auth failure notifications via `WorkspaceRuntime`).
- **011-state-recovery**: Status changed from "Planned" to "Partially Implemented". Six known gaps resolved (wrapper, entity, crash detection, shutdown hook, health endpoint, restart command). Updated exit code contract to include crash-restart behavior with backoff.

### Fixed
- **010-task-management**: Breakout completion now transitions the linked task to `InReview` before presentation in the main room. The spec now describes the shipped behavior: breakout completion triggers review presentation, while `APPROVE_TASK` and `MERGE_TASK` remain the authoritative task-state transitions.
- **010-task-management**: Task matching on assignment uses cascading lookup (title → room → agent → sole-unassigned) with fallback creation. `BreakoutRoomEntity.TaskId` links breakout rooms to tasks for reliable lookup.
- **007-agent-commands**: `MERGE_TASK` now returns `mergeCommitSha`, persists it on `TaskEntity.MergeCommitSha`, and restores task status to `Approved` if the merge fails.
- **001-domain-model**: `TaskStatus` enum updated to include all 10 values (was missing `InReview`, `ChangesRequested`, `Approved`, `Merging`).

### Added
- **007-agent-commands**: `SET_PLAN` command — agents can persist markdown plan content to their current room or breakout room through `WorkspaceRuntime.SetPlanAsync`.

### Changed
- **005-workspace-runtime / 006-orchestrator / 010-task-management**: Breakout room plans are now auto-seeded during task assignment from the linked task's `CurrentPlan`, with assignment-derived markdown as the fallback. `TaskAssignmentRequest` now accepts optional `CurrentPlan` content, and plan storage is no longer restricted to main-room IDs.

### Added
- **007-agent-commands**: `MERGE_TASK` command (Phase 1B) — squash-merges approved task branches to develop. Validates caller is Reviewer or Planner, task is Approved with a BranchName.
- **010-task-management**: Branch-per-breakout workflow — task branches (`task/{slug}-{suffix}`) isolate breakout work from develop. Completion flows through `InReview` → `MERGE_TASK` → squash-merge.
- **010-task-management**: Round-scoped git locking for concurrent breakout room safety — serializes git operations to prevent working-tree corruption.

### Changed
- **specs/README.md**: Added `011-state-recovery` to the index and marked `006-orchestrator` as `Outdated` until its breakout lifecycle details are reconciled with current code.
- **006-orchestrator**: Marked the section as `Outdated` and documented spec drift around removed timeout and breakout/fix round caps.
- **011-state-recovery**: Expanded the planned spec to cover reconnect UX states, task comment recovery/rendering expectations, and breakout termination paths (`complete`, `recall`, `cancel`, `stuck-detected`).

### Added
- **007-agent-commands**: `ADD_TASK_COMMENT` command (Phase 1B) — agents can attach comments/findings/evidence/blocker notes to tasks. Only assignee, reviewer, or planner can comment. Creates `TaskCommentEntity`, posts activity event.
- **007-agent-commands**: `RECALL_AGENT` command (Phase 1B) — planner can pull agents back from breakout rooms. Validates Planner role + Working state, closes breakout room, moves agent to Idle in parent room, posts recall notices.
- **007-agent-commands**: Task creation gating — only Planners can create non-Bug tasks via TASK ASSIGNMENT blocks. Non-planner non-Bug assignments converted to proposal messages.
- **007-agent-commands**: Updated all agent permission sets — `ADD_TASK_COMMENT` added to all agents, `RECALL_AGENT` added to Aristotle.
- **010-task-management**: `TaskType` enum (Feature, Bug, Chore, Spike) added to task model, defaults to Feature.
- **010-task-management**: `TaskCommentEntity` for structured task comments (Comment, Finding, Evidence, Blocker types).
- **010-task-management**: Task creation role restrictions in Orchestration Integration section.
- **010-task-management**: `GET /api/tasks/{id}/comments` endpoint for listing task comments.

### Added
- **007-agent-commands**: Phase 1C/1D/1E commands — `RUN_BUILD`, `RUN_TESTS` (with scope filter), `SHOW_DIFF` (optional branch), `GIT_LOG` (optional file/since/count), `ROOM_HISTORY` (read any room without moving), `MOVE_TO_ROOM`. 6 new handlers registered. All agent startup prompts and permissions updated.
- **007-agent-commands**: Breakout room redesign — open-ended work loops (no round caps), DMs delivered to agents in breakout rooms via breakout messages, DMs injected into breakout prompts. All agent timeouts removed: no per-turn LLM timeout, no orchestrator CancellationToken timeout, no MaxBreakoutRounds, no MaxFixRounds.
- **005-workspace-runtime**: Task workspace scoping — `GetTasksAsync` and `GetActiveTaskItemsAsync` now filter by active workspace via room→workspace join (was returning all tasks across all projects).
- **007-agent-commands**: DM command (Phase 1D) — unified direct messaging replaces ASK_HUMAN. `DM: Recipient: @Human/@AgentName, Message: <text>`. Agent-to-agent stores DM in messages table with `RecipientId`, triggers immediate orchestrator round for recipient. Agent-to-human routes through Discord notification bridge. System notification "📩 {sender} sent a direct message to {recipient}" posted in recipient's room (audit metadata, no content). `MessageEntity.RecipientId` (nullable), `MessageKind.DirectMessage`, `ActivityEventType.DirectMessageSent`. DMs injected into agent prompts as `=== DIRECT MESSAGES ===` section. 18 new tests. `AskHumanHandler` deleted, `ASK_HUMAN` removed from `KnownCommands`. All agent permissions and startup prompts updated from `ASK_HUMAN` to `DM`.
- **007-agent-commands / 300-frontend-ui**: Telegram-style DM panel — `DmPanel.tsx` with conversation list (left sidebar, agent avatars, role pills, last message preview) + chat view (right panel, markdown rendering, message bubbles) + composer. "Messages" tab added to tab bar. `DmController.cs` API: `GET /api/dm/threads`, `GET /api/dm/threads/{agentId}`, `POST /api/dm/threads/{agentId}`. Real-time refresh via `DirectMessageSent` SignalR event.
- **003-agent-system**: Frontend agent configuration UI — SettingsPanel extended with Agents section (expandable AgentConfigCard per agent: model override, startup prompt, custom instructions, template dropdown, reset to defaults) and Instruction Templates section (TemplateCard CRUD with create/edit/delete, confirmation dialogs). 8 API functions added to `api.ts`. 3 built-in seed templates via EF migration `SeedInstructionTemplates`: Verification-First, Pushback-Enabled, Code Review Focus. Discovery Workflow section added to copilot-instructions.md.
- **003-agent-system**: Agent config API endpoints — `GET/PUT /api/agents/{agentId}/config` (effective config + raw overrides, upsert), `POST /api/agents/{agentId}/config/reset` (revert to catalog defaults), full CRUD at `/api/instruction-templates` (list, get, create, update, delete). `AgentConfigService` extended with CRUD methods. DTOs: `AgentConfigResponse`, `UpsertAgentConfigRequest`, `InstructionTemplateRequest/Response`. Template name uniqueness enforced (409 Conflict). Template delete cascades FK SetNull. 24 new tests in `AgentConfigServiceCrudTests.cs`.
- **003-agent-system**: Agent configuration overrides — DB-backed per-agent config with instruction layering. `agent_configs` table stores `StartupPromptOverride`, `ModelOverride`, `CustomInstructions`, and `InstructionTemplateId` (FK to `instruction_templates`). `AgentConfigService` merges catalog defaults with DB overrides to produce effective `AgentDefinition` (layering: override/catalog prompt + template + custom, separated by `\n\n`). `AgentOrchestrator` uses effective agents in all prompt paths (`RunConversationRoundAsync`, `RunBreakoutLoopAsync`, `HandleBreakoutCompleteAsync`, `RunReviewCycleAsync`). Identity fields preserved; only `StartupPrompt` and `Model` overridden. EF migration `AddAgentConfigOverrides`. 14 new tests.
- **004-notification-system**: Discord per-project categories — room channels are now grouped under project-specific Discord categories (`"AA: {projectName}"`) instead of a single `"Agent Academy"` category. `WorkspaceRuntime.GetProjectNameForRoomAsync(roomId)` resolves `roomId → WorkspacePath → ProjectName` (with directory basename fallback). `FindOrCreateRoomCategoryAsync` accepts optional project name. `_roomCategories` dictionary replaces single `_roomCategoryId`. `RebuildChannelMappingAsync` scans `"AA: *"` categories + legacy `"Agent Academy"`. Legacy rooms without workspace retain `"Agent Academy"` fallback. 5 new tests.
- **005-workspace-runtime / 004-notification-system**: Room rename — `WorkspaceRuntime.RenameRoomAsync(roomId, newName)` updates room name and publishes `RoomRenamed` activity event. `PUT /api/rooms/{roomId}/name` API endpoint. Discord channel name + topic cascade via `OnRoomRenamedAsync` in `INotificationProvider`. Frontend double-click-to-rename in sidebar room list. `HumanizeProjectName` converts kebab-case package names to Title Case. 8 new tests.
- **005-workspace-runtime**: Human identity in messages — `PostHumanMessageAsync` accepts optional `userId` and `userName` params. When authenticated via GitHub OAuth, messages are attributed to the user's GitHub identity (e.g., `SenderId = "darinious"`, `SenderName = "Darin"`). Agents see `[Darin (Human)]` in prompts. Unauthenticated/Discord falls back to generic `"Human"`. Activity events keep `ActorId = "human"` for broadcaster echo-suppression. 1 new test.

### Fixed
- **005-workspace-runtime**: Duplicate main rooms — the `AddWorkspacePathToRooms` migration backfilled the legacy "Main Collaboration Room" (`id: "main"`) into the active workspace, causing it to appear alongside the workspace-scoped default room. `EnsureDefaultRoomForWorkspaceAsync` now calls `RetireLegacyDefaultRoomAsync` which clears `WorkspacePath` on the catalog default room if it was backfilled into the same workspace. 3 new regression tests.
- **005-workspace-runtime**: Default room name — workspace default rooms now use `_catalog.DefaultRoomName` ("Main Collaboration Room") instead of hardcoded "Main Room". Existing rooms auto-corrected on next `EnsureDefaultRoomForWorkspaceAsync` call.
- **004-notification-system**: Discord channel naming — removed redundant roomId slug from channel names (`main-collaboration-room` instead of `main-collaboration-room-main`). Channel topics changed from technical format to descriptive (`"Group discussion room for agent collaboration · ID: {roomId}"`). Channel search now matches by topic ID only. `RebuildChannelMappingAsync` supports both old and new topic formats.

### Changed
- **009-spec-management**: Plans are now spec change proposals — every implementation plan must include a Spec Change Proposal section. The spec update is a tracked deliverable validated alongside code changes, not an afterthought. Added invariants 3 and 4. Updated workflow phases to reflect plan-driven validation.
- **.github/copilot-instructions.md**: Updated Specification Workflow to formalize plans as spec change proposals with validation checklist.

### Added
- **005-workspace-runtime**: Project-scoped rooms — `RoomEntity` now has a `WorkspacePath` FK to `workspaces.Path`. `GetRoomsAsync` filters by active workspace. New `EnsureDefaultRoomForWorkspaceAsync` creates a per-project default room (ID: `{slug}-main`) and moves all agents there. `CreateTaskAsync` stamps new rooms with the active workspace path. EF migration `AddWorkspacePathToRooms` adds nullable column + index + backfill of existing rooms.
- **003-agent-system**: Session invalidation on workspace switch — `InvalidateAllSessionsAsync` added to `IAgentExecutor` interface. `CopilotExecutor` clears all cached sessions when called. `WorkspaceController.SetActiveWorkspace` calls it on project switch so agents start fresh in each project.
- **005-workspace-runtime**: Agent location reset on workspace switch — `EnsureDefaultRoomForWorkspaceAsync` moves all agents to the new workspace's default room in Idle state, clearing any breakout assignments.

### Fixed
- **004-notification-system**: Human message echo suppression — `ActivityNotificationBroadcaster` now filters out `MessagePosted` events where `ActorId == "human"`. This prevents Discord from echoing back messages the user just typed. Human messages are stored and trigger orchestration but are not re-sent as notifications.
- **004-notification-system**: Discord messages now trigger the orchestrator — human replies from Discord room channels and ASK_HUMAN threads call `HandleHumanMessage(roomId)` to wake up agents. Previously, Discord messages were stored but agents never responded because the orchestrator was not notified.
- **005-workspace-runtime**: Default room ordering — `GetRoomsAsync` now sorts the configured default room first, then remaining rooms alphabetically by name.

### Changed
- **000-system-overview**: Updated status from Planned to Implemented — all core components are operational.
- **specs/README.md**: Updated status table — 000 Implemented, 007 Partial, 008 Implemented.

### Added
- **004-notification-system**: Discord room-based channel routing — each Agent Academy room gets a dedicated Discord channel under an "Agent Academy" category. Webhook-based message formatting: each agent appears as a distinct Discord sender with custom name and avatar (via DiceBear Identicons). Bidirectional bridging: human replies in room channels route back to the correct AA room via `PostHumanMessageAsync`. Channels and webhooks created lazily on first message, mappings rebuilt on restart from channel topics. Error/system messages use compact embeds; regular messages are clean plain text.
- **004-notification-system**: ASK_HUMAN error propagation fix — `NotificationManager.SendAgentQuestionAsync` now returns `(bool Sent, string? Error)` tuple instead of bare bool. Actual exception details (e.g., "Missing Permissions") surfaced to the agent instead of misleading "no provider connected" message.

### Added
- **007-agent-commands**: Implemented Phase 1B state management commands — CLAIM_TASK (with auto-activation from Queued), RELEASE_TASK (ownership validation), UPDATE_TASK (status/blocker/note with allowed-status guard), APPROVE_TASK (reviewable-state validation, reviewer recording, ReviewRounds increment), REQUEST_CHANGES (with required findings, review message posting), SHOW_REVIEW_QUEUE (InReview/AwaitingValidation filter). 5 new ActivityEventType values (TaskClaimed, TaskReleased, TaskApproved, TaskChangesRequested, TaskStatusUpdated). 6 new WorkspaceRuntime methods + PostTaskNoteAsync helper. 28 new tests.

### Added
- **007-agent-commands**: Implemented ASK_HUMAN command — Discord agent-to-human question bridge. Category-per-workspace, channel-per-agent, thread-per-question Discord architecture. Persistent `MessageReceived` handler routes human replies back to agent's room via `WorkspaceRuntime.PostHumanMessageAsync`. Non-blocking: handler returns immediately, reply arrives asynchronously. `INotificationProvider` extended with `SendAgentQuestionAsync` default method. All agents have `ASK_HUMAN` permission and startup prompt documentation. 11 new tests.
- **004-notification-system**: Activity event bridge — `ActivityNotificationBroadcaster` hosted service forwards 7 event types (MessagePosted, TaskCreated, errors, commands) from `ActivityBroadcaster` to `NotificationManager`. Config persistence in `notification_configs` table with atomic upsert. Non-blocking auto-restore on startup. Settings tab in frontend with inline wizard mode. 35 new tests (commit `691ec89`).

### Changed
- **007-agent-commands**: Reconciled frontend surface contradiction — Phase 1A shipped backend-only with no UI surfaces. Command execution is invisible to users; results appear as system messages in conversation history. Updated "Frontend Surfaces" section to reflect NOT IMPLEMENTED status with planned work. Documented all 9 live commands (5 Phase 1A + 4 memory) with implementation evidence (handler files, commit `63b596c`). Updated Known Gaps to remove resolved items and add backend-only reality. Changed "Implementation Note" from factual to aspirational "Design Principle (Not Yet Applied)".

### Added
- **007-agent-commands**: Implemented Phase 1A — command envelope, parser, pipeline, authorization (default-deny with wildcard patterns), audit trail. Read handlers: LIST_ROOMS, LIST_AGENTS, LIST_TASKS, READ_FILE, SEARCH_CODE. Pipeline runs in parallel with existing free-text parsing.
- **008-agent-memory**: Implemented — REMEMBER (upsert), RECALL (LIKE search), LIST_MEMORIES, FORGET. No memory cap. Memories persisted in `agent_memories` table, injected into agent prompts as `=== YOUR MEMORIES ===` section.
- **007-agent-commands**: Agent permissions co-located in `agents.json` — each agent has `Permissions` property with `Allowed`/`Denied` arrays supporting wildcard patterns (e.g., `LIST_*`).
- **007-agent-commands**: Command audit trail — every command execution recorded in `command_audits` table with correlation ID, agent, command, args, status, result, and timestamp.
- **007-agent-commands**: Agent Command System spec — unified command pipeline with envelope, authorization, audit trails. 80+ commands across 3 tiers. Covers formalized reads, task state management, verification, DMs, navigation, room management, agent self-modification. Safety constraints and permission model per agent role.
- **008-agent-memory**: Agent Memory System spec — persistent per-agent knowledge store with REMEMBER/RECALL/FORGET commands. 14 categories by role. Isolated per agent, injected into prompts.

### Fixed
- **003-agent-system**: CopilotExecutor now passes `OnPermissionRequest = PermissionHandler.ApproveAll` to `SessionConfig` (required by SDK v0.2.0) — fixes sessions failing to create and silently falling back to stubs
- **003-agent-system**: CopilotExecutor now accepts `IConfiguration` and reads `Copilot:GitHubToken` for token-based authentication
- **003-agent-system**: StubExecutor replaced canned role-based responses with a clear offline notice — users can now distinguish stub output from real agent responses
- **003-agent-system**: Documented `Copilot:CliPath` configuration — system CLI (with existing auth) must be used instead of SDK's bundled binary (which has no auth state)
- **005-workspace-runtime**: Room participants now reflect actual agent locations (`AgentLocationEntity`) instead of role-based heuristic — fixes agents appearing in wrong rooms
- **005-workspace-runtime**: Added index on `agent_locations.RoomId`; `GetRoomsAsync` pre-loads all locations to avoid N+1 queries
- **006-orchestrator**: Multi-round conversation loop — `RunConversationRoundAsync` now loops up to 3 rounds per trigger when non-PASS responses are produced in rooms with active tasks, preventing single-round stalls
- **005-workspace-runtime**: Agent room placement — `CreateTaskAsync` auto-joins `AutoJoinDefaultRoom` agents into new task rooms (skips Working agents, best-effort error handling)
- **300-frontend-ui**: Room card agent list now starts at the left edge below the badge, spanning the full card width

### Added
- **003-agent-system (Auth → SDK)**: Automatic Copilot SDK activation on GitHub OAuth login
  - `CopilotTokenProvider` singleton: captures OAuth access token during login, clears on logout
  - `CopilotExecutor` token resolution chain: user OAuth → config token → env/CLI → stub
  - Token-change-aware client lifecycle: old CopilotClient disposed + sessions cleared when token changes
  - `AuthController.Logout` clears stored token
- **003-agent-system (SSE)**: SSE activity stream as alternative to SignalR
  - `GET /api/activity/stream` endpoint with replay, bounded channel, nginx-safe headers
  - `useActivitySSE.ts` client hook with auto-reconnect and `enabled` param
  - Transport selection via `localStorage` key `aa-transport` (`"signalr"` default, `"sse"` alternative)
  - `useActivityHub.ts` updated with `enabled` param for conditional activation
- **300-frontend-ui (SignalR)**: Real-time activity event streaming via `@microsoft/signalr`
  - `useActivityHub.ts` hook: connects to `/hubs/activity` with auto-reconnect + initial retry
  - `useWorkspace.ts`: handles AgentThinking/AgentFinished (per-room thinking state), triggers refresh on MessagePosted/RoomCreated/TaskCreated/PhaseChanged/PresenceUpdated
  - Polling reduced from 30s to 120s (fallback only)
  - ChatPanel status bar shows live connection state (connected/connecting/reconnecting/disconnected)

### Changed
- **300-frontend-ui (Sidebar)**: Agents now shown inside room cards instead of separate roster box
  - Each room card displays agents located in that room via `agentLocations` data
  - Thinking agents get a spinning ring animation around their status dot
  - Thinking state tracked per-room so spinners work across all room cards
  - Room list always visible (was hidden with only 1 room)

### Added
- **010-task-management (Phases 1-3)**: Task model extension, API endpoints, and frontend task list
  - Phase 1: Fixed auto-spec task creation on onboard when `!hasSpecs`
  - Phase 2: Extended TaskSnapshot/TaskEntity with 15 new fields; TaskSize, PullRequestStatus enums; AgentGitIdentity; EF migration; 7 API endpoints; InProgressStatuses for room queries
  - Phase 3: TaskListPanel component with status grouping, collapsible completed, URL-safe PR links

### Added
- **GitHub OAuth**: GitHub App OAuth login with cookie-based sessions
  - Server: opt-in auth pipeline (AddAuthentication + AddCookie + AddOAuth), FallbackPolicy, AuthController
  - Client: LoginPage, UserBadge (top-right avatar + sign out), auth gating in App.tsx
  - Security: open redirect prevention, minimal scopes (read:user + user:email), no token in cookie
  - Startup resilience: auth status check retries on 502

### Added
- **010-task-management**: Task management & Git workflow spec — task lifecycle, agent identity, branch/PR workflow, Socrates review pipeline, GitHub integration, frontend task list panel (Planned)

### Added
- **300-frontend-ui**: Frontend UI spec — component architecture, state management, API contracts, onboarding flow, theme/layout

### Fixed
- **300-frontend-ui**: Fixed critical frontend integration issues from parallel development:
  - `listWorkspaces()` stub replaced with actual `GET /api/workspaces` call
  - Added `getActiveWorkspace()` and `switchWorkspace()` API wrappers
  - Fixed `BrowseResult`/`DirectoryEntry` types to match server contract (`current`/`isDirectory` vs `path`/`type`)
  - Workspace UI now gated behind explicit `getActiveWorkspace()` check instead of overview heuristic
  - `handleProjectSelected()` now calls `PUT /api/workspace` to activate on server
  - Added "Switch Project" button to sidebar
  - Removed TaskComposer from sidebar (tasks go through chat input)
  - Updated role colors to match v1 palette (Planner=#b794ff, Architect=#ffbe70, etc.)
  - Added `TechnicalWriter` role to theme
  - Fixed viewport layout (removed `width: 1126px` constraint from index.css)
  - Removed unused App.css (Vite starter template)
  - Cleaned up dead exports from useWorkspace.ts
  - Improved onboard dialog messaging (differentiates existing specs vs auto-generation)

### Added
- **006-orchestrator**: Agent orchestrator — multi-agent conversation lifecycle manager (Implemented)
  - `AgentOrchestrator` singleton: queue-based processing, conversation rounds, breakout rooms, review cycles
  - Ported from v1 TypeScript `CollaborationOrchestrator` with C# async/await patterns
  - Queue-based message processing with serialized room handling
  - Planner-first conversation rounds with @-mention agent tagging (max 6)
  - TASK ASSIGNMENT block parsing → breakout room creation with task items
  - Breakout loop: up to 5 rounds per agent, WORK REPORT detection for early completion
  - Review cycle: reviewer verdict parsing (APPROVED/NEEDS FIX), rejection → 2 fix rounds
  - Prompt builders: conversation, breakout, review — with spec context loading
  - Message kind inference: role → MessageKind mapping
  - PASS response detection (PASS, N/A, No comment, Nothing to add)
  - WorkspaceRuntime extensions: `GetBreakoutRoomAsync`, `PostSystemStatusAsync`, `PostBreakoutMessageAsync`, `CreateTaskItemAsync`, `UpdateTaskItemStatusAsync`, `GetBreakoutTaskItemsAsync`
  - DI registration as singleton in `Program.cs`
  - 22 unit tests covering all parsing/detection logic

### Added
- **004-notification-system (Discord provider)**: Discord notification provider via Discord.Net library
  - `DiscordNotificationProvider` implementing `INotificationProvider` with full lifecycle management
  - Embed-based notifications with type-based color coding (blue/gold/green/red/purple)
  - Two-way messaging: button-based choice selection and freeform text input collection
  - Connection management with `SemaphoreSlim`, 30s Ready timeout, graceful disconnect
  - `IAsyncDisposable` implementation for proper client cleanup
  - Config schema with BotToken (secret), GuildId, ChannelId fields
  - DI registration in `Program.cs` as singleton
  - 36 unit tests covering configuration validation, embed formatting, schema, edge cases
  - NuGet: `Discord.Net` 3.19.1

### Changed
- **004-notification-system**: Updated spec to document Discord provider; removed Discord from "Known Gaps"

### Added
- **001-domain-model (persistence)**: EF Core + SQLite persistence layer
  - 9 entity classes in `src/AgentAcademy.Server/Data/Entities/` — mutable EF Core counterparts to the immutable Shared records
  - `AgentAcademyDbContext` with 9 DbSets, relationships, indexes matching v1 schema
  - `InitialCreate` migration in `Data/Migrations/`
  - Auto-migration on startup in `Program.cs`
  - Connection string in `appsettings.json` / `appsettings.Development.json`
  - 9 new DbContext tests (schema creation, CRUD, navigation, indexes) — 44 total tests passing
  - NuGet: `Microsoft.EntityFrameworkCore.Sqlite` 8.x, `Microsoft.EntityFrameworkCore.Design` 8.x

### Changed
- **001-domain-model**: Added "Data Persistence" section documenting entity classes, relationships, indexes, and architecture decisions

### Added
- **003-agent-system**: Agent execution system — `IAgentExecutor` interface, `CopilotExecutor` (Copilot SDK), `StubExecutor` (fallback) (Implemented)
  - `IAgentExecutor` interface: `RunAsync`, `InvalidateSessionAsync`, `InvalidateRoomSessionsAsync`, `DisposeAsync`, `IsFullyOperational`
  - `CopilotExecutor`: Uses `GitHub.Copilot.SDK` v0.2.0, manages sessions per agent/room with 10-min TTL, streams and collects responses, auto-falls back to stub
  - `StubExecutor`: Role-based canned responses ported from v1, `IsFullyOperational = false`
  - DI registration in `Program.cs`: `AddSingleton<IAgentExecutor, CopilotExecutor>()`
  - 44 tests passing (11 new executor tests + 33 existing)

### Added
- **004-notification-system**: Pluggable notification provider architecture (Implemented)
  - `INotificationProvider` interface with lifecycle (configure, connect, disconnect) and messaging (send, request input)
  - `NotificationManager` — thread-safe provider orchestrator with fan-out delivery and failure isolation
  - `ConsoleNotificationProvider` — built-in reference implementation (logs via ILogger)
  - `NotificationController` — REST API for provider management and test notifications
  - Unit tests with NSubstitute mocks covering failure isolation, input collection, thread safety
  - NSubstitute added to test project dependencies

### Added
- **001-domain-model**: Implemented all domain types in `src/AgentAcademy.Shared/Models/` — ported from v1 TypeScript
  - `Enums.cs`: 13 enums with `[JsonStringEnumConverter]` (CollaborationPhase, AgentAvailability, DeliveryPriority, MessageKind, MessageSenderKind, TaskStatus, WorkstreamStatus, RoomStatus, ActivityEventType, ActivitySeverity, AgentState, TaskItemStatus, NotificationType)
  - `Agents.cs`: AgentDefinition, AgentPresence, AgentLocation, AgentCatalogOptions
  - `Rooms.cs`: RoomSnapshot, BreakoutRoom, ChatEnvelope, DeliveryHint
  - `Tasks.cs`: TaskSnapshot, TaskItem, TaskAssignmentRequest, TaskAssignmentResult
  - `Activity.cs`: ActivityEvent, WorkspaceOverview
  - `Evaluation.cs`: EvaluationResult, ArtifactRecord, MetricsEntry, MetricsSummary
  - `System.cs`: HealthResult, HealthCheckResponse, ModelInfo, PermissionPolicy, DependencyStatus, UsageSummary, ErrorRecord, PlanContent
  - `Projects.cs`: ProjectScanResult, WorkspaceMeta
  - `Notifications.cs`: NotificationType, NotificationMessage, InputRequest, UserResponse, ProviderConfigSchema, ConfigField (NEW — not from v1)

### Changed
- **001-domain-model**: Updated spec from Planned → Implemented; replaced placeholder types with actual v1-ported types; added notification types

### Added
- **002-development-workflow**: Development workflow spec — branching strategy, CI, versioning, git hooks, PR workflow (Implemented)
- `.githooks/commit-msg`: Conventional commit enforcement hook
- `.githooks/pre-push`: Protected branch push guard
- `.github/workflows/ci.yml`: CI pipeline (build + test for .NET and client)
- `.github/workflows/version-bump.yml`: Auto version bump on merge to main
- `.github/pull_request_template.md`: PR template with spec change proposal
- `Directory.Build.props`: Centralized .NET version (0.1.0)

### Changed
- `.github/copilot-instructions.md`: Added branching strategy, git hooks setup, PR workflow, and versioning sections
- `specs/README.md`: Added 002-development-workflow to spec index

### Added (Initial Scaffold)
- **000-system-overview**: Initial system overview spec (Planned)
- **001-domain-model**: Initial domain model spec (Planned)
- **specs/README.md**: Spec index and conventions
- **.github/copilot-instructions.md**: Project conventions and spec workflow

### Initial Scaffold — $(date)
- Created spec-first project structure
- All features marked as "Planned" — no aspirational claims

### Fixed
- **007-agent-commands**: ErrorCode audit persistence — async command polling now returns error codes instead of null.
- **Auth**: Login token change now triggers immediate Copilot auth probe instead of waiting 5-minute interval. Fixes UI showing "degraded" after re-login.
