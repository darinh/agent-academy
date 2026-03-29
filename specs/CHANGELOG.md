# Spec Changelog

All changes to specifications are documented here.

## [Unreleased]

### Added
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
