# Changelog

All notable changes to Agent Academy are documented here.
Generated from [conventional commits](https://www.conventionalcommits.org/).

## Unreleased (2026-04-12)

### Features

- **add confirmation for destructive commands** — Destructive commands (`CLOSE_ROOM`, `CLEANUP_ROOMS`, `REJECT_TASK`, `CANCEL_TASK`, `RESTART_SERVER`, `FORGET`, `MERGE_TASK`) now require explicit `confirm=true` in args before execution. Without the flag, pipeline returns `CONFIRMATION_REQUIRED` error code with structured warning and retry hint. `ICommandHandler.IsDestructive` interface property lets handlers self-declare. Confirmation gate runs after authorization but before rate limiting. Applies to both agent pipeline and human/consultant API. `HumanCommandMetadata` includes `IsDestructive` and `DestructiveWarning` for frontend confirmation dialogs. 20 new tests (2087 total).

## Unreleased (2026-04-11)

### Features

- **add analytics CSV/JSON export** — `GET /api/export/agents` and `GET /api/export/usage` endpoints with downloadable CSV (RFC 4180, formula injection protection) and JSON formats. Truncation detection via `X-Truncated` header. Frontend export button on agent analytics panel.
- add GitHub integration status tab to Settings panel
- bridge OAuth token to gh CLI for PR operations
- **add DataAnnotations validation to all API request types** — enforces string length limits, required fields, and range constraints on 20+ request records. Invalid requests now return 400 + ProblemDetails.

### Tests

- add 31 request validation tests (RequestValidationTests.cs)
- add 18 controller-level tests for sprint endpoints

### Docs

- add changelog entry for GitHub status tab
- close spec 001 validation known gap with validation rules table

## 2026-04-07

### Features

- add branch protection configuration script (#8)
- add tab overflow menu to reduce information density (#32)
- register 11 missing commands in HumanCommandRegistry (#17)
- add toast notifications for activity events and timeline badge (#36)
- add auth degradation toast and reconnect button (#28)
- add toast notification on workspace switch (#41)
- add new-messages indicator and project selector retry
- add reusable EmptyState, ErrorState, and SkeletonLoader components
- persist OAuth tokens to database for crash-resilient auth
- implement OAuth refresh token for automatic token renewal
- add task management commands to human/consultant API
- add session history API and dashboard panel
- per-project session resume on workspace switch
- add sparkline trend charts to dashboard panels
- add circuit breaker UI indicator
- add circuit breaker to CopilotExecutor
- command audit log API and dashboard panel
- multi-provider notification setup wizard
- add Slack notification provider via Slack Web API
- wire task-filtered spec context into breakout prompts
- add spec-task linking for traceability
- add write SDK tools for agents (task-write, memory groups)
- wire up Copilot SDK tool calling for agents
- MERGE_PR command — merge task PRs via GitHub API
- add PR review comments (POST_PR_REVIEW, GET_PR_REVIEWS)
- add PR status sync via background polling
- add GitHub PR integration via gh CLI
- add time-range filter to dashboard panels
- add room-level usage and error stats panel
- add agent errors dashboard panel to frontend
- add agent error tracking with DB persistence and REST endpoints
- add LLM usage dashboard panel to frontend
- add LLM token/usage tracking via AssistantUsageEvent
- add CREATE_TASK_ITEM, UPDATE_TASK_ITEM, LIST_TASK_ITEMS commands
- add REBASE_TASK command with conflict detection and improved MERGE_TASK error reporting
- add focus trap and focus restore to command palette
- add memory decay/TTL with staleness detection
- add memory import/export commands and REST endpoints
- add interactive task review panel with filter tabs and action buttons
- add Cmd+K command palette for keyboard-driven command discovery
- add shared memory category for cross-agent knowledge sharing
- add command metadata endpoint for dynamic frontend catalog
- add restart history UI panel to Dashboard
- upgrade RECALL memory search from LIKE to FTS5 with BM25 ranking
- auto-archive rooms when all tasks complete
- encrypt notification provider secrets at rest
- add server-side restart rate limiting and restart history API
- make command rate limits runtime-configurable
- add REJECT_TASK command for task lifecycle completeness
- clean up Discord channels when rooms are archived
- add notification delivery tracking for observability
- reconstruct queue on startup to prevent message loss
- add retry with exponential backoff for notification providers
- wire agent git identity through CommitAsync and SquashMergeAsync
- implement frontend reconnect UX with global recovery banner
- implement ROOM_TOPIC command with room topic field
- implement breakout stuck-detection with idle round tracking
- implement RETURN_TO_MAIN command for agent navigation
- implement INVITE_TO_ROOM command for planner/human agent movement
- add CREATE_ROOM and REOPEN_ROOM commands for room-as-context workflow
- add IsRetryable to CommandErrorCode and retryability hints in context output
- add per-agent command rate limiting
- persist ErrorCode in CommandAuditEntity
- add structured error codes to command envelope
- add case-insensitive search and truncation indicator to SEARCH_CODE
- enrich SHOW_REVIEW_QUEUE with description, type, branch, and commit count
- add truncation metadata to READ_FILE for large files
- add LIST_COMMANDS handler, fix orphaned task on branch failure
- UI redesign slice 3 — editorial war-room shell overhaul
- MERGE_TASK role authorization + UI sidebar and spec updates
- UI redesign slice 2 — shell layout polish
- implement CLOSE_ROOM command for planner-only room archival
- Add CLOSE_ROOM command for main room cleanup
- add CANCEL_TASK command and fix MERGE_TASK commit prefix
- implement consultant API auth handler and messages endpoint
- UI redesign slice 2 — command panel, layout polish, auth improvements
- auto reauth degraded copilot sessions
- add proactive SDK auth-expiry detection with Discord notification
- add auth-state diagnostics panel to LoginPage
- wire frontend to copilotStatus contract
- implement server-side crash recovery actions
- add SHELL command with allowlisted operations
- implement client recovery flow
- refactor auth to copilotStatus enum and add git-add to SHELL
- conversation session management with epoch summarization and prompt dedup
- integrate wrapper.sh into npm run dev
- add supervised process wrapper script
- add server instance tracking, crash detection, and instance health endpoint
- add RESTART_SERVER command and shutdown hook
- add SDK error classification and retry logic to CopilotExecutor
- add SET_PLAN command and auto-seed breakout room plans
- agent-centric workspace UX with session history and full activity recording
- branch-per-breakout review workflow with MERGE_TASK command
- structured command results + message filtering in chat UI
- task system enhancements — type gating, comments, recall agent
- remove all agent timeouts and round caps, add Phase 1C/1D/1E commands
- unified DM command replaces ASK_HUMAN with frontend DM panel
- agent config UI and instruction template library (Phases 3+4)
- agent config and instruction template CRUD API endpoints (Phase 2)
- DB-backed agent configuration overrides (Phase 1)
- human messages use GitHub identity when authenticated
- room rename with Discord cascade + fix default room name
- Discord per-project categories + spec-as-plan workflow
- project-scoped rooms, session reset on switch, Discord echo fix
- Discord room-based channel routing with webhook agent identity
- implement Phase 1B state management commands
- implement ASK_HUMAN command for Discord agent-to-human question bridge
- add settings page with provider status and user menu
- wire Discord notifications to agent activity events
- implement agent command system Phase 1
- add in-flight task items to planner conversation prompt
- add Agent Workspaces to sidebar showing breakout room activity
- auto-activate Copilot SDK from GitHub OAuth login
- fix CopilotExecutor auth and add SSE activity stream
- show agents inside room cards with thinking spinner
- wire up SignalR client for real-time activity events
- add user badge with sign-out in top-right corner
- add GitHub App OAuth login with cookie-based sessions
- extend task model, add task API endpoints, and task list panel
- port API endpoints to ASP.NET Core controllers
- add remaining UI panels and Tasks tab
- port React app shell with project selector, sidebar, and chat
- add Discord notification setup wizard
- add SignalR hub for real-time activity events
- add REST API controllers and ProjectScanner service
- add SpecManager service and port living spec system
- port CollaborationOrchestrator to C#
- add WorkspaceRuntime service — central state manager
- add Discord notification provider
- add agent execution, database, and notification systems
- port domain types from TypeScript to C# records
- scaffold agent-academy with spec-first structure

### Fixes

- scope Discord freeform input to configured owner user (#13)
- show legacy rooms with null WorkspacePath in active workspace (#16)
- align client package.json version to 0.1.0 (#4)
- show transport type and offline guidance (#42, #43)
- add Fluent UI tooltips to collapsed sidebar items (#39)
- add keyboard shortcut hint and improve message spacing
- replace circuit breaker jargon, add logout confirmation, improve degraded banner
- add focus indicators, status icons, aria-labels, and thinking a11y
- rename SDK tool list_agents → show_agents to avoid CLI conflict
- serialize GitHubServiceTests to eliminate flaky test
- prevent orphaned TaskId when git branch creation fails
- add missing command permissions to all agents
- add CREATE_TASK_ITEM and UPDATE_TASK_ITEM to all agent permission lists
- register RoomTopicHandler in DI container
- skip recovery notification when nothing was recovered
- make MERGE_TASK conventional commit prefix exhaustive
- enforce maximum review round limit (5 rounds)
- add role gates to APPROVE_TASK and REQUEST_CHANGES handlers
- surface breakout loop failures to parent room
- prevent concurrent breakout rooms for same agent
- add missing command descriptions to LIST_COMMANDS handler
- prevent duplicate spec generation tasks on repeated onboard
- trigger immediate auth probe on login token change
- eliminate DM duplication with per-recipient acknowledgment tracking
- resolve circular DI in ListCommandsHandler
- improve SEARCH_CODE and READ_FILE agent commands
- serialize test classes sharing WorkspaceRuntime static state
- add conventional commit prefix to MERGE_TASK handler
- eliminate task metadata contamination via write-once branch identity
- clamp workspace subtitle to 3 lines with scroll overflow
- add CANCEL_TASK to Planner and Reviewer allowed commands
- clamp task description to 3 lines with expand/collapse toggle
- persist human/user messages across session boundaries
- use ArgumentList instead of EscapeArg in command handlers
- re-enable breakout rooms with correct command processing order
- add Source column migration for command_audits
- disable breakout room creation in task assignments
- restore legacy task visibility after workspace scoping
- Correct branch workflow invariants and descriptions
- address review findings — crash detection and auth recovery timing
- implement branch-per-breakout review workflow
- add missing migration Designer file for AddTaskTypeAndComments
- scope tasks to active workspace, increase executor timeout
- DMs to human post as channel messages, not threads
- fall back to active workspace for Discord category names
- DM messages post to agent channel without thread
- Discord categories use Pascal Case names with regression tests
- route DMs to Discord Messages category, suppress room echo
- clean Discord channel names and descriptive topics
- workspace default room named 'Main Room' not '{project} — Main Room'
- retire legacy main room when backfilled into workspace
- prevent duplicate main rooms when workspace is active
- default room always first in room ordering + regression tests
- send full message content to Discord with multi-part chunking
- default room always listed first in room ordering
- wire Discord messages to orchestrator so agents respond
- graceful fallback when Discord bot lacks channel creation permissions
- suppress noisy EF Core SQL query logging
- improve Discord missing-permissions error message
- stop orchestrator retry loop on stub offline responses
- remove misleading Copilot:GitHubToken from appsettings
- persist workspaces to SQLite so they survive server restart
- soft-delete breakout rooms and enforce agent location exclusivity
- use agent locations for room participants instead of role heuristic
- replace StubExecutor canned responses with offline notice
- align room agent list to left edge in sidebar
- use system copilot CLI instead of bundled binary
- persist OAuth token across server restarts
- auto-join agents into task rooms and add multi-round conversation loop
- retry auth status check on startup (backend race condition)
- redirect to frontend after OAuth callback
- auto-create spec task on onboard when project has no specs
- fix unicode display, title clipping, and startup resilience
- resolve critical integration issues from parallel development
- include notification API exports for NotificationSetupWizard

### Documentation

- audit and resolve known gaps across 6 specs (#19)
- triage 14 known gaps in spec 003 agent system (#11)
- add setup script for git hooks and dependencies (#9)
- complete spec 001 entity and model inventory
- update specs 003 and 011 for token refresh feature
- spec accuracy audit — fix 14 HIGH and 12 MEDIUM discrepancies across 11 specs
- document consultant command execution API in spec 012
- clean up stale PR merge gap in spec 010
- document config encryption in spec 004
- mark notification setup wizard as resolved in frontend spec
- update stale spec gaps in 005 and fix RoomTopicHandler gap clarification
- update spec 300 and changelog for task review panel
- update specs 007 and 010 for rate limit config and REJECT_TASK
- resolve stale Known Gaps in specs 006 and 011
- update spec 007 status to Implemented — all Tier 1 phases complete
- add INVITE_TO_ROOM and RETURN_TO_MAIN to agent system prompts
- document LIST_ROOMS status filter parameter in spec 007
- reconcile spec 010 (Task Management) with code — Partial → Implemented
- reconcile spec 006 orchestrator with current code
- fix spec index status for 011 and 012
- update spec 007 for CREATE_ROOM, REOPEN_ROOM, and room lifecycle
- update changelog for auth probe fix and error recovery
- update spec 007 and changelog for command rate limiting
- update spec 007 and changelog for ErrorCode audit persistence
- add autonomous operation section to copilot instructions
- update specs for SEARCH_CODE and SHOW_REVIEW_QUEUE improvements
- update specs for DM acknowledgment and READ_FILE truncation
- add code-intelligence skill for codebase-memory-mcp
- verify MERGE_TASK authorization enforcement in spec 007
- update spec 012 status to Phase 1 & 2 Complete
- add spec 012 — Consultant API for CLI-to-agent communication
- remove unimplemented PR workflow from spec 010
- anvil architecture pattern analysis
- fix specs 001 and 010 to match delivered branch workflow
- document state recovery in spec 011 with corrected retry values
- update specs 003, 007, 011 and CHANGELOG for P0 features
- update specs for branch-per-breakout and MERGE_TASK
- reconcile recovery and orchestrator specs
- fix spec documentation gaps
- update agent prompts, permissions, and specs for new commands
- add unmerged branch check and session_log table to session protocol
- update notification and runtime specs for Discord integration
- update notification system spec and changelog for activity bridge
- update command system spec and changelog from previous session
- add command system reference to agent startup prompts
- add agent command system and memory system specs
- update specs for auth CLI path, stub offline notice, and room participants
- update specs for orchestrator fixes, SignalR client, and sidebar UI
- add GitHub OAuth to changelog, add 300-frontend-ui to spec index, update 010 status to Partial
- update changelog for task management phases 1-3
- add task management and git workflow spec

### Refactoring

- convert SpecManager to async file I/O
- move crash recovery trigger to bootstrap, consolidate close reasons
- extract portable conventions to user-level copilot-instructions
- replace filter bar with dropdown menu in chat panel
- clean up Discord category/channel naming conventions

### Tests

- add Human role coverage for CancelTaskHandler
- add E2E tests for circuit breaker banner, sparklines, and fix flaky command palette
- E2E tests for audit log panel
- add Playwright E2E tests for ErrorsPanel, UsagePanel, RestartHistoryPanel, CommandsPanel, AgentSessionPanel
- add Playwright E2E tests for ChatPanel, DmPanel, OverviewPanel, PlanPanel
- add Playwright E2E tests for dashboard, timeline, task list, and settings
- add Playwright E2E smoke tests for workspace and command palette
- add ReadFileHandler tests for truncation, ranges, directories, and security

### Other

- style: unify visual hierarchy and component consistency across workspace panels
- ci: enhance pipeline with caching, commit validation, and CODEOWNERS
- ci: add branching strategy, CI, versioning, hooks, and PR templates

