# Spec Changelog

All changes to specifications are documented here.

## [Unreleased]

### Added
- **010-task-management (Phases 1-3)**: Task model extension, API endpoints, and frontend task list
  - Phase 1: Fixed auto-spec task creation on onboard when `!hasSpecs`
  - Phase 2: Extended TaskSnapshot/TaskEntity with 15 new fields; TaskSize, PullRequestStatus enums; AgentGitIdentity; EF migration; 7 API endpoints; InProgressStatuses for room queries
  - Phase 3: TaskListPanel component with status grouping, collapsible completed, URL-safe PR links

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
