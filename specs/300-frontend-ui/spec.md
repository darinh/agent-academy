# 300 — Frontend UI

## Overview

The Agent Academy frontend is a single-page React application that provides the user interface for the multi-agent collaboration platform. It communicates with the ASP.NET Core backend via REST APIs and renders workspace state, room conversations, agent activity, and project management flows.

## Stack

- **React 19** with functional components and hooks
- **Vite 8** for build tooling
- **TypeScript 5.9** with strict mode and `verbatimModuleSyntax`
- **Fluent UI v9** (`@fluentui/react-components`) with `webDarkTheme`
- **Griffel** (`makeStyles`) for CSS-in-JS styling

## Component Architecture

```
App.tsx (FluentProvider + AppShell)
├── ProjectSelectorPage.tsx (when no active workspace)
│   ├── LoadExistingSection
│   ├── OnboardSection
│   └── CreateSection
└── Shell (when workspace is active)
    ├── SidebarPanel.tsx
    │   ├── Room list (each card shows agents in that room via agentLocations)
    │   ├── Inline room creation (+ button → name input → Enter to create)
    │   ├── Per-agent thinking spinner (spinning ring around status dot)
    │   └── Switch Project button
    └── Main workspace
        ├── Workspace header + phase pill + UserBadge
        ├── Tab bar (overview, directMessages, plan, tasks, timeline, sprint, dashboard [Metrics], commands)
        └── Tab content panels
            ├── ChatPanel.tsx (room-centric conversation with session management)
            ├── TaskListPanel.tsx
            ├── PlanPanel.tsx
            ├── CommandsPanel.tsx
            ├── TimelinePanel.tsx
            ├── DashboardPanel.tsx
            ├── WorkspaceOverviewPanel.tsx
            ├── DmPanel.tsx (Telegram-style DM conversations)
            ├── SprintPanel.tsx (sprint lifecycle viewer)
            ├── SettingsPanel.tsx (tabbed settings: agents, templates, notifications, advanced)
            ├── AgentSessionPanel.tsx (per-agent session inspector)
            ├── CommandPalette.tsx (Cmd+K overlay)
            ├── RecoveryBanner.tsx (crash recovery notification)
            └── CircuitBreakerBanner.tsx (auth degradation warning)
```

## State Management

### `useWorkspace.ts`

Central hook that owns all workspace state. Components receive state and callbacks via props from `App.tsx`.

**State:**
- `ov` — `WorkspaceOverview` from `/api/overview`
- `recentActivity` — activity feed
- `roomId` — selected room
- `thinkingByRoom` — `Map<roomId, Map<agentId, {name, role}>>` populated by SignalR `AgentThinking`/`AgentFinished` events
- `connectionStatus` — SignalR connection state (`"connected"` | `"connecting"` | `"reconnecting"` | `"disconnected"`)
- `err`, `busy` — error/loading state
- `tab` — active tab (persisted in localStorage)
- `sidebarOpen` — sidebar collapse state (persisted in localStorage)

**Derived:**
- `room` — selected room object (falls back to first room)
- `roster` — room participants or configured agents fallback
- `activity` — filtered activity for current room
- `thinkingAgentList` — thinking agents for the current room (derived from `thinkingByRoom`)

**Refresh:** Polls `/api/overview` every 120 seconds as a fallback. Primary updates arrive via SignalR (`useActivityHub` hook). On refresh, stale thinking entries are reconciled against `agentLocations` (agents no longer in Working state are cleared).

### Workspace Gating (App.tsx)

On mount, `App.tsx` calls `getActiveWorkspace()`:
- If an active workspace exists → show workspace shell
- If no active workspace → show `ProjectSelectorPage`

This gates the entire workspace UI behind explicit workspace selection, preventing the selector from being bypassed on page refresh.

### Authentication Gating (App.tsx + LoginPage.tsx)

On mount, `App.tsx` also calls `getAuthStatus()`:
- If `authEnabled = false` → the app skips login gating
- If `copilotStatus = "operational"` → the workspace can render normally
- If `copilotStatus = "unavailable"` → `LoginPage` renders the standard GitHub sign-in prompt
- If `copilotStatus = "degraded"` → the workspace shell stays visible in **limited mode** so rooms, plans, tasks, and prior output remain readable while new Copilot-driven actions stay paused
- A limited-mode banner appears inside the workspace shell with reconnect guidance, while the dedicated `LoginPage` remains reserved for the fully unavailable sign-in path
- After the initial load, the client polls `/api/auth/status` every 30 seconds while the tab is open
- If the client sees `copilotStatus` transition from `operational` to `degraded` while `user` is still populated, it immediately navigates to `/api/auth/login` so the existing OAuth flow can refresh the SDK token without a manual click
- Automatic re-authentication is debounced once per browser tab via `sessionStorage` and is suppressed after explicit logout so the sign-out flow remains stable

The backend still keeps `authenticated = false` whenever `copilotStatus != "operational"`, but frontend rendering is driven by `copilotStatus`: `unavailable` fail-closes to `LoginPage`, while `degraded` stays in-shell in read-only limited mode.

## Project Selection / Onboarding Flow

### Load Existing
1. `GET /api/workspaces` → list workspace cards
2. User clicks a workspace card
3. `PUT /api/workspace { path }` → activate on server
4. Hide selector, show workspace shell

### Onboard New Project
1. User enters directory path or uses filesystem browser
2. `POST /api/workspaces/scan { path }` → scan results (name, tech stack, git info, specs status)
3. User clicks "Onboard Project" → confirmation dialog
4. Dialog shows whether specs exist or will be auto-generated
5. `POST /api/workspaces/onboard { path }` → onboard result
6. Workspace activates, shell loads

### Switch Project
- Button in sidebar triggers `onSwitchProject()` callback
- Returns user to `ProjectSelectorPage`

## API Contract

All types are defined in `api.ts`. The client adapts to the server's response shapes:

### Key endpoints:
| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/workspace` | GET | Get active workspace |
| `/api/workspace` | PUT | Set active workspace |
| `/api/workspaces` | GET | List all workspaces |
| `/api/workspaces/scan` | POST | Scan a directory |
| `/api/workspaces/onboard` | POST | Onboard a project |
| `/api/overview` | GET | Full workspace overview |
| `/api/auth/status` | GET | Return `authEnabled`, fail-closed `authenticated`, and `copilotStatus` (`operational` / `degraded` / `unavailable`) used by the client render contract |
| `/api/commands/metadata` | GET | Return the full human command catalog with field schemas |
| `/api/commands/execute` | POST | Execute an allowlisted human command |
| `/api/commands/{correlationId}` | GET | Poll async human command status/results |
| `/api/filesystem/browse` | GET | Browse filesystem |
| `/api/rooms/{id}/human` | POST | Send human message |
| `/api/rooms/{id}/messages` | GET | Get room messages (supports `after`, `limit`, `sessionId` query params) |
| `/api/rooms/{id}/sessions` | POST | Create/rotate to a new conversation session |
| `/api/rooms/{id}/agents/{agentId}` | POST | Add agent to room |
| `/api/rooms/{id}/agents/{agentId}` | DELETE | Remove agent from room |
| `/api/rooms/{id}/phase` | POST | Transition phase |
| `/api/rooms` | POST | Create a new room |
| `/api/tasks` | POST | Create task |
| `/api/agents/custom` | POST | Create custom agent from prompt |
| `/api/agents/custom/{agentId}` | DELETE | Delete custom agent |
| `/api/agents/configured` | GET | List all agents (catalog + custom) |
| `/api/sprints` | GET | List sprints (supports `limit` and `offset` query params) |
| `/api/sprints` | POST | Start a new sprint for the active workspace |
| `/api/sprints/active` | GET | Get active sprint with artifacts (returns 204 if none) |
| `/api/sprints/{id}` | GET | Get sprint detail with artifacts |
| `/api/sprints/{id}/advance` | POST | Advance sprint to next stage |
| `/api/sprints/{id}/complete` | POST | Complete sprint (optional `force` query param) |
| `/api/sprints/{id}/cancel` | POST | Cancel an active sprint |
| `/api/sprints/{id}/artifacts` | GET | Get artifacts for a sprint (optional `stage` filter) |

### Browse response shape (from server):
```json
{ "current": "/path", "parent": "/parent", "entries": [{ "name": "dir", "path": "/path/dir", "isDirectory": true }] }
```

## Human Command UI

### Commands Tab (`CommandsPanel.tsx`)

The workspace shell includes a dedicated **Commands** tab for the human command surface.

- The command deck is **loaded dynamically** from `GET /api/commands/metadata` on mount. If the endpoint is unreachable, the panel falls back to a hardcoded 11-command catalog (`WEEK1_COMMANDS` in `commandCatalog.ts`).
- The server-side `HumanCommandRegistry` is the single source of truth. It returns metadata only for commands that are both allowlisted and have a registered handler.
- Commands are grouped visually by category: workspace, code, git, and operations
- The panel only submits **scalar** arguments (strings/numbers serialized as strings) to match `CommandController.NormalizeArgs()`
- `RUN_BUILD` and `RUN_TESTS` are treated as **async** commands and polled via `GET /api/commands/{correlationId}` every 2.5 seconds while status is `pending`
- The result rail keeps the latest 10 command runs visible, with the newest entry expanded as the primary detail surface
- Result rendering is generic by design: scalar fields become summary chips, known text payloads (`content`, `output`, `diff`) render in a monospace preview block, and known arrays (`matches`, `tasks`, `rooms`, `agents`, `commits`, `messages`) render as compact record lists

### Degraded Mode Behavior

When `copilotStatus = "degraded"`:

- The Commands tab remains visible inside the workspace shell
- Existing command history remains readable
- New command execution is disabled and the panel surfaces a limited-mode warning instead of pretending commands are available

This mirrors the broader frontend rule that degraded mode is still a readable workspace, not a hidden or redirected one.

## Theme / Role Colors

Global design tokens live in `src/agent-academy-client/src/index.css`, while role-specific accent mapping remains in `theme.ts`.

The current shell uses an **editorial war-room** visual system:

- Ink-dark canvas with layered radial gradients and a faint drafting-grid overlay
- Warm brass/copper accents paired with cool slate-blue highlights instead of generic neon gradients
- Serif display typography (`--heading`) for titles and metrics, with a cleaner sans serif body stack for controls and narrative text
- Shared panel treatment across the sidebar, workspace masthead, tabs, chat cards, and dashboard surfaces so the product reads as one cohesive interface

Each agent role still maps to accent/foreground/avatar colors:

| Role | Accent | Source |
|------|--------|--------|
| Planner | `#b794ff` | v1 original |
| Architect | `#ffbe70` | v1 original |
| SoftwareEngineer | `#48d67a` | v1 original |
| Reviewer | `#ff7187` | v1 original |
| Validator | `#d6a0ff` | v1 original |
| Human | `#6cb6ff` | v1 original |
| TechnicalWriter | `#7dd3fc` | added |

## Layout

- Full viewport: `100vh` with CSS grid
- Sidebar + main content: `356px minmax(0, 1fr)` (open) / `94px minmax(0, 1fr)` (collapsed)
- The main shell is split into a left navigation rail and a right workspace canvas with a prominent masthead, spotlight card, limited-mode banner, tab deck, and panel body
- Surfaces use layered dark panels with warm metallic borders and inset highlights rather than the previous generic glassmorphism treatment
- Authentication surfaces (`LoginPage.tsx`, `ProjectSelectorPage.tsx`) use a two-panel editorial layout: narrative rail on the left, actionable system/status card on the right
- `index.css` now owns the global color/typography tokens, background atmosphere, scrollbar styling, and shell overlay effects; component-level structure and treatments remain in Griffel `makeStyles`

## Real-Time Updates

### SignalR Integration (`useActivityHub.ts`)

The frontend connects to the server's SignalR hub at `/hubs/activity` via `@microsoft/signalr`. The Vite dev server proxies WebSocket connections to the backend on port 5066 (`ws: true` for `/hubs`).

**Connection lifecycle:**
- Auto-reconnect with backoff: `[0, 2s, 5s, 10s, 30s]`
- Initial connection retries on failure (same backoff schedule)
- Connection status exposed as `ConnectionStatus` type
- ChatPanel status bar shows live connection state with color indicators

**Event handling in `useWorkspace`:**

| Event Type | Action |
|------------|--------|
| `AgentThinking` | Add agent to `thinkingByRoom` map (scoped by `roomId`) |
| `AgentFinished` | Remove agent from `thinkingByRoom` map |
| `MessagePosted` | Trigger refresh |
| `RoomCreated` | Trigger refresh |
| `TaskCreated` | Trigger refresh |
| `PhaseChanged` | Trigger refresh |
| `PresenceUpdated` | Trigger refresh |

### Sidebar Agent Display

Each room card in the sidebar shows the agents currently located in that room, determined by `agentLocations` data (not the synthetic participant list). Agents are displayed below the room name, separated by a horizontal line, with a colored status dot and name.

When an agent is in the thinking state (populated via SignalR events), a spinning ring animates around their status dot in the same color. The spinning ring uses a CSS `@keyframes` animation (`aa-spin`) with a wrapper DOM element (Griffel does not support `::after` pseudo-elements with border properties).

Thinking state is tracked per room (`thinkingByRoom` map), so spinners appear correctly across all room cards — not just the selected room.

### Circuit Breaker Indicator (`CircuitBreakerBanner.tsx` + `useCircuitBreakerPolling.ts`)

The frontend monitors the Copilot circuit breaker state (see spec 003) and surfaces degradation to the operator.

**Polling hook (`useCircuitBreakerPolling.ts`):**
- Polls `GET /api/health/instance` for `circuitBreakerState` field
- Adaptive intervals: 60s when Closed (normal), 10s when Open/HalfOpen (degraded)
- Request ID guard prevents stale responses from overwriting current state
- Pauses polling when `document.visibilityState === "hidden"`, resumes on tab focus
- Exports: `useCircuitBreakerPolling()` hook, `CircuitBreakerState` type, `isDegraded()`, `parseCircuitBreakerState()`

**Visual indicators (three locations):**
1. **Floating banner** (`CircuitBreakerBanner.tsx`): Fixed-position banner at top center, visible when state is Open or HalfOpen. Uses `role="alert"` + `aria-live="assertive"`. Open state: red gradient, "Agent requests are temporarily blocked". HalfOpen state: amber gradient, "Testing backend recovery with a probe request".
2. **Header signal chip** (`App.tsx`): Inline chip in the workspace header signals row. Shows "Circuit open" or "Circuit probing" with warning styling. Hidden when Closed.
3. **ErrorsPanel status row**: Color-coded dot + label + detail text. Shows all states including Closed (green). Appears above error summary stats in the dashboard.

### Dashboard Sparklines (`Sparkline.tsx` + `sparklineUtils.ts`)

Mini SVG trend charts in the dashboard panels showing activity over time.

**Sparkline component (`Sparkline.tsx`):**
- Minimal SVG polyline with linear gradient fill, no axes or labels
- Accessible: `role="img"` + `aria-label="Sparkline trend"`
- Unique gradient IDs via React `useId()` (no cross-instance collisions)
- Configurable: color, dimensions, fill opacity, stroke width
- Renders nothing for fewer than 2 data points

**Bucketing utilities (`sparklineUtils.ts`):**
- `bucketByTime(records, getTimestamp, bucketCount, hoursBack?)` — counts records per bucket
- `bucketByTimeSum(records, getTimestamp, getValue, bucketCount, hoursBack?)` — sums a numeric value per bucket
- Invalid timestamps (`NaN`) are filtered before computation
- Auto-ranges from data when `hoursBack` is not provided

**Panel integration (24 buckets, computed via `useMemo`):**
- **UsagePanel**: Two sparklines — request count trend (amber) and token volume trend (blue)
- **ErrorsPanel**: Error rate trend (red)
- **AuditLogPanel**: Command count trend (purple), using a separate 200-record fetch for accurate trend data (not the paged table slice)

## Room-Centric Conversation (`ChatPanel.tsx`)

The Conversation panel provides the primary chat interface, centered on the selected room. Rather than being a standalone tab, it renders as the main content area when a room is selected in the sidebar.

### Session Management

Each room contains conversation sessions (epochs). The ChatPanel toolbar includes:

- **Sessions dropdown**: Always visible. Lists all sessions (active + archived) for the current room. Selecting an archived session loads its historical messages in read-only mode.
- **New Session button**: Creates a new conversation session via `POST /api/rooms/{roomId}/sessions`, archiving the current active session.
- **Agent management dropdown**: Shows agents currently in the room with remove buttons, plus a list of available agents with add buttons. Uses `POST /api/rooms/{roomId}/agents/{agentId}` and `DELETE /api/rooms/{roomId}/agents/{agentId}`.

### Message Display

- `displayMessages` switches between live room messages (from SignalR/polling) and session-scoped historical messages based on the selected session.
- When viewing an archived session, a banner reads: "Viewing archived session. Messages are read-only."
- Messages are loaded via `GET /api/rooms/{roomId}/messages?sessionId={id}&limit=200`.

### Connection Status Bar

The status bar shows live SignalR connection state with color indicators (connected/connecting/reconnecting/disconnected).

## Sidebar Room Creation (`SidebarPanel.tsx`)

The sidebar Rooms section includes inline room creation:

- A `+` button in the Rooms header opens a text input field.
- Pressing Enter creates a room via `POST /api/rooms` with the entered name.
- Pressing Escape cancels the input.
- The "Conversation" nav item has been removed from the sidebar navigation — room selection in the sidebar directly loads the ChatPanel.

## Settings Panel (`SettingsPanel.tsx`)

The Settings panel is a full tabbed configuration page with five tabs:

### Custom Agents Tab

Lists user-created custom agents with delete capability. Includes an "Add Custom Agent" form:

- **Agent Name**: Free-text input. A kebab-case ID preview is shown below (e.g., "My Agent" → `my-agent`).
- **Agent Prompt** (`agent.md`): Textarea for the agent's system prompt / instruction document.
- **Model** (optional): Text input for model override.
- Submit calls `POST /api/agents/custom`, which validates uniqueness against both built-in catalog agents and existing custom agents.
- Delete calls `DELETE /api/agents/custom/{agentId}`.

### Built-in Agents Tab

Displays agent configuration cards for catalog agents. Each card shows the agent's name, role, and current model/config overrides. Uses the existing `AgentConfigOverride` system.

### Templates Tab

Instruction template CRUD interface. Templates are reusable prompt fragments that can be referenced in agent configurations.

### Notifications Tab

Provider setup UI for notification integrations (Discord, Slack, etc.). Connect/disconnect controls per provider, with the `NotificationSetupWizard` handling provider-specific configuration.

### Advanced Tab

System-level settings:
- **Main Room Epoch** and **Breakout Room Epoch**: Configure conversation session rotation thresholds (message count before automatic session archival).

## Sprint Panel (`SprintPanel.tsx`)

The Sprint tab provides a lifecycle viewer for agent sprints — structured iterations that progress through defined stages.

### Stage Pipeline

A 6-column grid (responsive: 3-col at 900px, 1-col at 600px) visualizes the sprint lifecycle:

| Stage | Icon | Description |
|-------|------|-------------|
| Intake | 📥 | Requirements gathering and scope definition |
| Planning | 📋 | Sprint plan creation and phase breakdown |
| Discussion | 💬 | Team discussion and design decisions |
| Validation | ✅ | Plan validation and readiness check |
| Implementation | 🔨 | Active development and task execution |
| FinalSynthesis | 📊 | Sprint report and deliverable summary |

Each stage card shows:
- Visual state: **active** (cyan border + gradient), **completed** (green border + gradient), or **pending** (muted)
- Artifact count for that stage (or description text if no artifacts)
- Clickable — selecting a stage filters the artifact detail view below

### Artifact Viewer

When a stage is selected, artifacts for that stage are listed as expandable cards:
- Header: artifact type (PascalCase split to words), stage badge, agent badge, relative timestamp
- Content: monospace pre-wrapped text, truncated at 200 chars with "Show full content" toggle
- Types include: `DesignDoc`, `SprintPlan`, `CodeReview`, `SprintReport`, and others defined by the backend

### Sprint History

When multiple sprints exist, a history list appears below the artifacts:
- Each row shows sprint number, status badge (Active/Completed/Cancelled → active/done/cancel colors), current stage, and timestamp
- Clicking a sprint loads its detail via `GET /api/sprints/{id}`
- Active sprint data is cached to avoid redundant fetches

### Sprint Lifecycle Controls

The Sprint panel header includes context-aware action buttons:

| State | Available Actions |
|-------|-------------------|
| No active sprint | **Start Sprint** button (header + empty state) |
| Active, not final stage | **Advance Stage** + **Cancel** |
| Active, final stage (FinalSynthesis) | **Complete Sprint** + **Cancel** |
| Completed/Cancelled | No actions |

Actions call write endpoints (`POST /api/sprints`, `/advance`, `/complete`, `/cancel`) and refresh data on success. Error messages display in the error state. A busy flag disables buttons during async operations.

### Data Flow

- On mount: fetches active sprint + sprint list in parallel
- Defaults to showing the active sprint (if one exists)
- Manual refresh via header sync button
- SignalR `sprintVersion` prop triggers automatic refresh on sprint events

### API Types

```typescript
type SprintStage = "Intake" | "Planning" | "Discussion" | "Validation" | "Implementation" | "FinalSynthesis";
type SprintStatus = "Active" | "Completed" | "Cancelled";
type SprintArtifactType = "DesignDoc" | "SprintPlan" | "CodeReview" | "SprintReport";

interface SprintSnapshot {
  id: string; number: number; status: SprintStatus;
  currentStage: SprintStage; overflowFromSprintId: string | null;
  createdAt: string; completedAt: string | null;
}

interface SprintArtifact {
  id: number; sprintId: string; stage: SprintStage;
  type: string; content: string;
  createdByAgentId: string | null; createdAt: string;
}

interface SprintDetailResponse { sprint: SprintSnapshot; artifacts: SprintArtifact[]; }
interface SprintListResponse { sprints: SprintSnapshot[]; total: number; }
```

## Future Work

- Real-time updates via SignalR ✅ (implemented — `useActivityHub.ts`)
- Sprint panel: SignalR integration for real-time stage/artifact updates
- Sprint panel: Markdown/JSON rendering for artifact content (currently raw text)
- Sprint panel: Sprint metrics (time per stage, artifact word counts)
- SSE activity stream integration
- ~~Notification setup wizard (component exists, not yet wired)~~ **RESOLVED** — `NotificationSetupWizard` refactored to multi-provider. Accepts `providerId` prop, fetches schema dynamically, supports Discord, Slack, and generic fallback. Settings tab routes all providers to the wizard.
- ~~TaskStatePanel integration~~ **RESOLVED** — `TaskListPanel.tsx` now includes interactive review panel with filter tabs (All/Review Queue/Active/Completed), expandable task detail, task comments, and review action buttons (Approve/Request Changes/Reject/Merge) wired through `executeCommand` API.
- ~~Human command metadata endpoint so the Commands tab can stop hardcoding command schemas~~ **RESOLVED** — `GET /api/commands/metadata` implemented. Frontend loads dynamically with fallback.
- ~~Session history / resume indicator~~ **RESOLVED** — `SessionHistoryPanel` in dashboard shows session stats, filterable session list with summaries. `ChatPanel` shows "Agents have context from a previous conversation session" banner when archived sessions exist for the current room.
