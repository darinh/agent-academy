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
            │   ├── AgentAnalyticsPanel.tsx (per-agent performance metrics)
            │   └── WorktreeStatusPanel.tsx (live worktree health widget)
            ├── MemoryBrowserPanel.tsx (per-agent memory browser with search, categories, delete)
            ├── DigestPanel.tsx (learning digest history and detail)
            ├── RetrospectivePanel.tsx (retrospective history, agent filter, detail view)
            ├── WorkspaceOverviewPanel.tsx
            ├── DmPanel.tsx (Telegram-style DM conversations)
            ├── SprintPanel.tsx (sprint lifecycle viewer)
            ├── SettingsPanel.tsx (tabbed settings: agents, templates, notifications, github, advanced)
            ├── AgentSessionPanel.tsx (per-agent session inspector)
            ├── CommandPalette.tsx (Cmd+K overlay)
            ├── KeyboardShortcutsDialog.tsx (? key shortcut help)
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
| `/api/analytics/agents` | GET | Per-agent performance metrics (optional `hoursBack` query param, 1–8760) |
| `/api/analytics/agents/{agentId}` | GET | Agent detail drill-down (optional `hoursBack`, `requestLimit`, `errorLimit`, `taskLimit`) |
| `/api/search` | GET | Full-text search across messages and tasks (required `q`, optional `scope`, `messageLimit`, `taskLimit`) |

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
| `SprintStarted` | Extract metadata → optimistic update + reconcile |
| `SprintStageAdvanced` | Extract metadata → optimistic update + reconcile |
| `SprintArtifactStored` | Extract metadata → targeted artifact fetch + reconcile |
| `SprintCompleted` | Extract metadata → optimistic update + reconcile |

### Sprint Real-Time Updates

Sprint events carry structured `metadata` payloads on `ActivityEvent` (added as `Dictionary<string, object?>? Metadata` on the shared model, persisted as `MetadataJson` on the entity). The `SprintService` queues events before `SaveChangesAsync` but only broadcasts them **after** the commit succeeds (`QueueEvent` + `FlushEvents` pattern), preventing subscribers from seeing uncommitted state.

**Event metadata payloads:**

| Event | Key Fields |
|-------|-----------|
| `SprintStarted` | `sprintId`, `sprintNumber`, `status`, `currentStage` |
| `SprintStageAdvanced` | `sprintId`, `action` (`advanced`/`signoff_requested`/`approved`/`rejected`), `currentStage`, `pendingStage`, `awaitingSignOff` |
| `SprintArtifactStored` | `sprintId`, `stage`, `artifactType`, `createdByAgentId`, `isUpdate` |
| `SprintCompleted` | `sprintId`, `status` (`Completed`/`Cancelled`) |

**Frontend handling (`SprintPanel`):**
- **Optimistic updates**: Stage transitions, sign-off state, and completion status are applied immediately from metadata without waiting for an API response.
- **Targeted artifact fetch**: Artifact events trigger `getSprintArtifacts(sprintId, stage)` for just the affected stage, with a sequence counter to discard stale responses from out-of-order fetches.
- **Debounced reconciliation**: A 1.5-second trailing reconciliation fetch replaces the previous immediate full-refetch-on-every-event pattern, reducing API load during rapid sprint activity.
- **Event deduplication**: Processed event IDs are tracked in a `Set` (capped at 200 entries) to prevent duplicate processing on SSE reconnect replay.
- **Fallback**: If an event arrives without metadata (e.g., from a pre-upgrade server), the fallback `sprintVersion` path triggers a full refetch.

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
- **AgentAnalyticsPanel**: Per-agent token volume trend (default blue sparkline)

### Agent Analytics Panel (`AgentAnalyticsPanel.tsx`)

Per-agent performance dashboard showing LLM usage, errors, and task completion aggregated over a configurable time window.

**Data source:** `GET /api/analytics/agents?hoursBack={N}` → `AgentAnalyticsSummary`. Receives `hoursBack` prop from the shared `DashboardPanel` time range selector.

**Summary row:** Four cards at the top — active agent count, total requests, total cost, total errors. Values formatted via shared `formatCost()` and `formatTokenCount()` utilities.

**Sort toolbar:** Dropdown to sort agent cards by Requests (default), Tokens, Cost, Errors, or Tasks. Manual refresh button with loading state. Export CSV button triggers `exportAgentAnalytics(hoursBack, "csv")` and downloads the file via the `downloadFile()` helper in `api.ts`.

**Agent cards:** Responsive grid (`minmax(280px, 1fr)`). Each card shows:
- Agent name + ID
- Badges: error count (red V3Badge when > 0), task completion percentage (ok/warn/err based on 80%/50% thresholds)
- Metrics grid: requests, tokens, cost, average response time (seconds)
- Error/task row: error count with color coding (green < 5%, yellow < 20%, red ≥ 20% error rate), tasks completed/assigned
- Token trend sparkline (12 equal-sized buckets spanning the window, newest last)

**Refresh behavior:** Auto-refreshes every 60 seconds. Stale-response protection via `fetchIdRef` counter — concurrent fetches are discarded if a newer request has been issued.

**Empty state:** Icon + message when no agent activity exists for the time window.

**API types (`api.ts`):**
```typescript
interface AgentPerformanceMetrics {
  agentId: string;
  agentName: string;
  totalRequests: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCost: number;
  averageResponseTimeMs: number | null;
  totalErrors: number;
  recoverableErrors: number;
  unrecoverableErrors: number;
  tasksAssigned: number;
  tasksCompleted: number;
  tokenTrend: number[];  // 12 buckets
}

interface AgentAnalyticsSummary {
  agents: AgentPerformanceMetrics[];
  windowStart: string;
  windowEnd: string;
  totalRequests: number;
  totalCost: number;
  totalErrors: number;
}
```

**Card selection:** Clicking an agent card toggles selection (highlighted border). Selected card expands an inline `AgentDetailView` below the grid.

### Agent Detail View (`AgentDetailView.tsx`)

Drill-down panel showing detailed per-agent analytics when a card is selected.

**Data source:** `GET /api/analytics/agents/{agentId}?hoursBack={N}` → `AgentAnalyticsDetail`. Fetches on mount and when `agentId` or `hoursBack` changes. Stale-response protection via `fetchIdRef`.

**Layout:**
- Header: agent name/ID, refresh button, close button (×)
- KPI row: 6 cards — requests, tokens, cost, avg response time, errors (red when > 0), tasks done
- Activity trend: 24-bucket sparkline (wider than card sparklines, 400×40)
- Model breakdown: responsive grid of cards showing per-model request count, tokens, cost, and proportional bar
- Recent requests: scrollable table (time, model, tokens in/out, cost, duration)
- Recent errors: table with error type badge (`errorTypeBadge` from `panelUtils`), truncated message with tooltip, recovery status badge
- Tasks: list with title, status badge (color-coded), branch icon tooltip, PR number, creation date

**Empty states:** Per-section "No X in this window" messages.

**API types (`api.ts`):** `AgentAnalyticsDetail`, `AgentUsageRecord`, `AgentErrorRecord`, `AgentTaskRecord`, `AgentModelBreakdown`, `AgentActivityBucket`.

### Task Analytics Panel (`TaskAnalyticsPanel.tsx`)

Task cycle effectiveness dashboard showing completion rates, cycle times, review effort, and per-agent breakdown over a configurable time window.

**Data source:** `GET /api/analytics/tasks?hoursBack={N}` → `TaskCycleAnalytics`. Receives `hoursBack` prop from the shared `DashboardPanel` time range selector.

**Summary row:** Six KPI cards — completion rate (%), avg cycle time, avg queue time, avg review rounds, rework rate (%), total commits. Time values formatted adaptively (minutes/hours/days).

**Status badges:** Non-zero status counts rendered as colored `V3Badge` chips (Active=blue, InReview=gold, Completed=green, Blocked=warn, Cancelled=muted, etc.).

**Throughput sparkline:** 12-bucket sparkline of completed tasks over time. Hidden when all buckets are zero.

**Type breakdown:** Chip row showing task counts by type (Feature, Bug, Chore, Spike). Zero-count types are hidden.

**Agent effectiveness table:** Sortable table with columns: Agent (name + assigned count), Done, Rate, Cycle, 1st Pass, Rework. Click column headers to sort (toggle asc/desc). Default sort: completed count descending.

**Refresh behavior:** Auto-refreshes every 60 seconds. Stale-response protection via `seqRef` counter. Manual refresh button in toolbar.

**Dashboard integration:** Rendered in `DashboardPanel` between Agent Performance and LLM Usage sections.

## Room-Centric Conversation (`ChatPanel.tsx`)

The Conversation panel provides the primary chat interface, centered on the selected room. Rather than being a standalone tab, it renders as the main content area when a room is selected in the sidebar.

### Session Management

Each room contains conversation sessions (epochs). The ChatPanel toolbar includes:

- **Sessions dropdown**: Always visible. Lists all sessions (active + archived) for the current room. Selecting an archived session loads its historical messages in read-only mode.
- **New Session button**: Creates a new conversation session via `POST /api/rooms/{roomId}/sessions`, archiving the current active session.
- **Agent management dropdown**: Shows agents currently in the room with remove buttons, plus a list of available agents with add buttons. Uses `POST /api/rooms/{roomId}/agents/{agentId}` and `DELETE /api/rooms/{roomId}/agents/{agentId}`.
- **Export dropdown** (`SessionToolbar.tsx`): Button labeled "Export ▾" (right-aligned, `marginLeft: auto`) that reveals JSON and Markdown options on click. Calls `exportRoomMessages(roomId, format)` from `api/rooms.ts`. Shows "Exporting…" with reduced opacity while the download is in progress. Closes on outside click. Uses `downloadFile()` helper which reads blob from fetch response and triggers browser download via temporary anchor element.

### Message Display

- `displayMessages` switches between live room messages (from SignalR/polling) and session-scoped historical messages based on the selected session.
- When viewing an archived session, a banner reads: "Viewing archived session. Messages are read-only."
- Messages are loaded via `GET /api/rooms/{roomId}/messages?sessionId={id}&limit=200`.

### Connection Status Bar

The status bar shows live SignalR connection state with color indicators (connected/connecting/reconnecting/disconnected).

## Direct Messages (`DmPanel.tsx`)

Telegram-style DM interface for human-to-agent private conversations, accessible via the "directMessages" tab.

### Thread Sidebar

Left column lists all agents with DM threads. Selecting an agent loads the conversation. Thread list shows agent name, last message preview, and timestamp. Thread list updates in real-time via `useDmThreadSSE` hook — connects to `GET /api/dm/threads/stream` SSE endpoint and triggers a debounced (500ms) refetch of `GET /api/dm/threads` on `thread-updated`, `resync`, and `connected` events. Refetch on `connected` ensures missed updates during disconnects are caught. Read-only mode disables the SSE connection.

### Chat Area

Right column displays the selected DM thread. Messages are loaded via `GET /api/dm/threads/{agentId}`. Human messages align right, agent messages align left. Consultant messages show a distinct copper-colored "Consultant" label.

### Export

`<select>` element in the DM chat header offers "Export as JSON" and "Export as Markdown" options. Selecting a format calls `exportDmMessages(agentId, format)` from `api/system.ts`. Shows "Exporting…" with reduced opacity during download. Resets to default label after completion or error.

## Sidebar Room Creation (`SidebarPanel.tsx`)

The sidebar Rooms section includes inline room creation:

- A `+` button in the Rooms header opens a text input field.
- Pressing Enter creates a room via `POST /api/rooms` with the entered name.
- Pressing Escape cancels the input.
- The "Conversation" nav item has been removed from the sidebar navigation — room selection in the sidebar directly loads the ChatPanel.

## Settings Panel (`SettingsPanel.tsx`)

The Settings panel is a full tabbed configuration page with six tabs:

### Custom Agents Tab

Lists user-created custom agents with delete capability. Includes an "Add Custom Agent" form:

- **Agent Name**: Free-text input. A kebab-case ID preview is shown below (e.g., "My Agent" → `my-agent`).
- **Agent Prompt** (`agent.md`): Textarea for the agent's system prompt / instruction document.
- **Model** (optional): Text input for model override.
- Submit calls `POST /api/agents/custom`, which validates uniqueness against both built-in catalog agents and existing custom agents.
- Delete calls `DELETE /api/agents/custom/{agentId}`.

### Built-in Agents Tab

Displays agent configuration cards for catalog agents. Each card shows the agent's name, role, and current model/config overrides. Uses the existing `AgentConfigOverride` system.

Each expanded card includes a **Resource Quotas** section:
- **Max Requests / Hour**: Integer input. Limits the agent's LLM API call rate (authoritative, in-memory sliding window).
- **Max Tokens / Hour**: Integer input. Best-effort limit on total tokens consumed per hour (DB aggregation).
- **Max Cost / Hour ($)**: Decimal input. Best-effort limit on hourly spend.
- Current usage counters shown below each field (requests, tokens, cost this hour).
- "Quota" badge displayed in the card header when any limit is configured.
- "Remove Limits" button with confirmation dialog resets to unlimited.
- Quota loading is independent of config loading — quota endpoint failure does not block config editing.
- Input validation: non-negative numbers; requests/tokens must be integers. Invalid input shows inline error, does not submit.
- API: `GET/PUT/DELETE /api/agents/{id}/quota` (see §003).

### Templates Tab

Instruction template CRUD interface. Templates are reusable prompt fragments that can be referenced in agent configurations.

### Notifications Tab

Provider setup UI for notification integrations (Discord, Slack, etc.). Connect/disconnect controls per provider, with the `NotificationSetupWizard` handling provider-specific configuration.

### GitHub Tab

GitHub integration status and PR capability overview. Data loaded from `GET /api/github/status` (see §010).

- **Status card**: Shows connected/not-connected status with refresh button, repository slug (monospace), and auth source badge.
- **Auth source badge**: Color-coded — green for `oauth`, blue for `cli`, red for `none`.
- **Auth source explanation**: Contextual guidance based on current auth method:
  - `oauth`: Confirms PR operations are available through browser session.
  - `cli`: Notes server-side authentication; suggests browser login for OAuth.
  - `none`: Shows error state with "Login with GitHub" button linking to `/api/auth/login`.
- **PR Capabilities grid**: 2×2 grid showing create/review/merge/status-sync capabilities. All enabled when `isConfigured = true`, all disabled otherwise.
- **Error state**: Connection errors show error message with retry button.
- **Loading state**: Spinner with "Checking GitHub status…" text.

API type: `GitHubStatus { isConfigured: boolean; repository: string | null; authSource: "oauth" | "cli" | "none" }` — exported from `api.ts`.

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
- Stage timing: `⏱ {duration}` and word count `· {N}w` for stages with artifacts
- Clickable — selecting a stage filters the artifact detail view below

### Sprint Metrics

A summary metrics bar displays below the pipeline:
- **Total duration** — elapsed time from sprint creation to completion (or now for active)
- **Selected stage duration** — time in the currently selected stage
- **Total words** — cumulative word count across all artifacts
- **Selected stage words** — word count for the selected stage's artifacts
- **Artifact count** — total number of artifacts

Stage durations are estimated from artifact timestamps: each stage starts at its first artifact (or sprint start for Intake) and ends at the first artifact of the next stage.

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

### User Sign-Off Gates

Certain stage transitions require human approval before advancing:

- When agents request advancement from **Intake** or **Planning**, the sprint enters an `awaitingSignOff` state
- A yellow banner appears: "User sign-off required — agents want to advance from {current} to {pending}"
- Two action buttons replace the normal Advance Stage button: **Approve → {pendingStage}** and **Reject**
- Approve calls `POST /api/sprints/{id}/approve`, Reject calls `POST /api/sprints/{id}/reject`
- On reject, the sprint stays at the current stage; on approve, it advances to the pending stage

### Data Flow

- On mount: fetches active sprint + sprint list in parallel
- Defaults to showing the active sprint (if one exists)
- Manual refresh via header sync button
- SignalR `sprintVersion` prop triggers automatic refresh on sprint events

### API Types

```typescript
type SprintStage = "Intake" | "Planning" | "Discussion" | "Validation" | "Implementation" | "FinalSynthesis";
type SprintStatus = "Active" | "Completed" | "Cancelled";
type ArtifactType = "RequirementsDocument" | "SprintPlan" | "ValidationReport" | "SprintReport" | "OverflowRequirements";

interface SprintSnapshot {
  id: string; number: number; status: SprintStatus;
  currentStage: SprintStage; overflowFromSprintId: string | null;
  awaitingSignOff: boolean; pendingStage: SprintStage | null;
  createdAt: string; completedAt: string | null;
}

interface SprintArtifact {
  id: number; sprintId: string; stage: SprintStage;
  type: string; content: string;
  createdByAgentId: string | null; createdAt: string;
  updatedAt: string | null;
}

interface SprintDetailResponse { sprint: SprintSnapshot; artifacts: SprintArtifact[]; stages: string[]; }
interface SprintListResponse { sprints: SprintSnapshot[]; totalCount: number; }
```

## Agent Memory Browser (`MemoryBrowserPanel.tsx`)

Displays and manages per-agent memories — key-value pairs stored by agents during task execution, organized by category. Accessible as a top-level tab (🧠 Memory) in the sidebar.

### UI Layout

1. **Header**: Title "Agent Memory" with active/expired count badges and a manual refresh button
2. **Controls row**: Agent selector dropdown (auto-selects first agent), search input (debounced 300ms), "Include expired" checkbox
3. **Category chips**: Rendered from stats — shows categories with active memory counts. Click to filter; click again to clear. "all" chip shown first.
4. **Memory list**: Each row shows category badge (color-coded), memory key (clickable when value exceeds 120 chars), value preview (truncated at 120 chars with "…"), timestamp, and a delete button
5. **Expanded value**: Clicking the key toggles expansion when the value is truncated — the full memory value is shown below the row

### Data Flow

```
MemoryBrowserPanel
  ├── fetchData → Promise.allSettled([browseMemories({agentId, category, search, includeExpired}), getMemoryStats(agentId)])
  │   → GET /api/memories/browse?agentId=X&category=Y&search=Z&includeExpired=true → BrowseMemoriesResponse
  │   → GET /api/memories/stats?agentId=X → MemoryStatsResponse
  └── handleDelete(agentId, key) → deleteMemory(agentId, key)
      → DELETE /api/memories?agentId=X&key=Y
```

### Race Condition Guards

List fetches use a `fetchIdRef` counter. Each fetch increments the counter before starting; on completion, the result is discarded if the counter has moved. Stats refresh after delete also checks that `selectedAgent` hasn't changed since the delete was initiated.

### Real-Time Refresh

When a `LearningDigestCompleted` activity event arrives via SignalR, `useWorkspace` increments its `memoryVersion` counter. This flows through `WorkspaceContent` → `MemoryBrowserPanel.refreshTrigger`. A `useEffect` with a `useRef` guard (`prevTrigger`) detects when the trigger changes and calls `fetchData()` to refresh the memory list and stats without user interaction. The same `LearningDigestCompleted` event also increments `digestVersion` for `DigestPanel`.

### Category Color Mapping

| Category | Badge Color |
|----------|-------------|
| decision | `active` (blue) |
| lesson | `ok` (green) |
| pattern | `feat` (purple) |
| preference | `info` (cyan) |
| invariant / gotcha | `warn` (amber) |
| risk / incident | `err` (red) |
| constraint | `review` (yellow) |
| finding | `info` (cyan) |
| spec-drift | `warn` (amber) |
| verification | `ok` (green) |
| shared | `tool` (teal) |
| (other) | `muted` (gray) |

### Types

```typescript
interface MemoryBrowserPanelProps { agents: AgentDefinition[]; refreshTrigger?: number; }

interface MemoryDto {
  agentId: string; category: string; key: string; value: string;
  createdAt: string; updatedAt: string | null;
  lastAccessedAt: string | null; expiresAt: string | null;
}
interface BrowseMemoriesResponse { total: number; memories: MemoryDto[]; }
interface MemoryCategoryStat { category: string; total: number; active: number; expired: number; lastUpdated: string; }
interface MemoryStatsResponse {
  agentId: string; totalMemories: number; activeMemories: number;
  expiredMemories: number; categories: MemoryCategoryStat[];
}
```

### Empty State

When no memories match the current filters: contextual message showing agent name and active filters. When no agent is selected (no agents loaded): no initial fetch.

## Learning Digests (`DigestPanel.tsx`)

Displays the history of AI-generated learning digests — periodic syntheses of agent retrospectives into shared cross-cutting memories. Accessible as a top-level tab (📚 Digests) in the sidebar.

### UI Layout

1. **Header**: Title "Learning Digests" with total-count badge and controls (status filter dropdown, refresh button)
2. **Stats row**: Aggregate cards — total digests, memories created, retros processed, undigested retros (highlighted in gold when > 0), last completed timestamp
3. **Digest list**: Paginated rows (20 per page). Each row shows status badge (Completed/Pending/Failed), truncated summary (120 chars), memory count, retro count, and created timestamp. Click to expand detail.
4. **Pagination**: Prev/Next buttons with "Page N of M" indicator
5. **Detail panel**: Expands below the list when a digest is selected. Shows digest ID, status badge, created timestamp, memory/retro counts, full summary text, and source retrospectives (each with agent ID, task ID, timestamp, and full content)

### Data Flow

```
DigestPanel
  ├── fetchList → Promise.all([listDigests({status, limit, offset}), getDigestStats()])
  │   → GET /api/digests?status=X&limit=20&offset=N → DigestListResponse
  │   → GET /api/digests/stats → DigestStatsResponse
  └── fetchDetail(id) → getDigest(id)
      → GET /api/digests/{id} → DigestDetailResponse (includes sources[])
```

### Race Condition Guards

Both list and detail fetches use `useRef` counters (`fetchIdRef`, `detailFetchIdRef`). Each fetch increments the counter before starting; on completion, the result is discarded if the counter has moved past the request's ID. This prevents stale responses from overwriting newer data when the user rapidly changes filters or selects different digests.

### Real-Time Refresh

DigestPanel accepts an optional `refreshTrigger` prop (defaults to `0`). When `useWorkspace` receives a `LearningDigestCompleted` activity event via SignalR, it increments its `digestVersion` state counter. This value flows through `WorkspaceContent` → `DigestPanel.refreshTrigger`. A `useEffect` with a `useRef` guard detects when the trigger changes and calls `fetchList()` to refresh the digest list and stats without user interaction. The `LearningDigestCompleted` event is also included in `TOAST_EVENT_TYPES` so the user sees a notification toast.

### Types

```typescript
interface DigestListItem {
  id: number; createdAt: string; summary: string;
  memoriesCreated: number; retrospectivesProcessed: number; status: string;
}
interface DigestListResponse { digests: DigestListItem[]; total: number; limit: number; offset: number; }
interface DigestSourceItem {
  commentId: string; taskId: string; agentId: string; content: string; createdAt: string;
}
interface DigestDetailResponse {
  id: number; createdAt: string; summary: string;
  memoriesCreated: number; retrospectivesProcessed: number; status: string;
  sources: DigestSourceItem[];
}
interface DigestStatsResponse {
  totalDigests: number; byStatus: Record<string, number>;
  totalMemoriesCreated: number; totalRetrospectivesProcessed: number;
  undigestedRetrospectives: number; lastCompletedAt: string | null;
}
```

### Status Badges

| Status | Badge Color | Meaning |
|--------|-------------|---------|
| Completed | `done` (green) | Digest finished, memories stored |
| Pending | `review` (amber) | Digest generation in progress |
| Failed | `err` (red) | Digest generation failed; retrospective claims released |

### Empty State

When no digests exist: 📚 "No digests yet" with guidance to use `GENERATE_DIGEST` for manual creation.

## Worktree Status Widget (`WorktreeStatusPanel.tsx`)

Embedded in `DashboardPanel` — shows live status of all active agent worktrees. Auto-refreshes every 30 seconds via `setInterval`.

## Retrospectives (`RetrospectivePanel.tsx`)

Displays the history of agent post-task retrospectives — reflections agents produce after completing tasks, capturing lessons learned, patterns discovered, and improvement opportunities. Accessible as a top-level tab (🔬 Retros) in the sidebar.

### UI Layout

1. **Header**: Title "Retrospectives" with total-count badge and controls (agent filter dropdown populated from stats, refresh button)
2. **Stats row**: Aggregate cards — total retrospectives, agent count, average content length, latest retrospective timestamp
3. **Agent breakdown**: Chip row showing each agent's retrospective count (sorted by count descending)
4. **Retrospective list**: Paginated rows (20 per page). Each row shows agent name badge, task title, truncated content preview (100 chars), and created timestamp. Click to expand detail.
5. **Pagination**: Prev/Next buttons with "Page N of M" indicator
6. **Detail panel**: Expands below the list when a retrospective is selected. Shows task title, task status badge, agent name, task ID, created timestamp, task completed timestamp (when available), and full retrospective content

### Data Flow

```
RetrospectivePanel
  ├── fetchList → Promise.all([listRetrospectives({agentId, taskId, limit, offset}), getRetrospectiveStats()])
  │   → GET /api/retrospectives?agentId=X&taskId=Y&limit=20&offset=N → RetrospectiveListResponse
  │   → GET /api/retrospectives/stats → RetrospectiveStatsResponse
  └── fetchDetail(commentId) → getRetrospective(commentId)
      → GET /api/retrospectives/{commentId} → RetrospectiveDetailResponse
```

### Race Condition Guards

Both list and detail fetches use `useRef` counters (`fetchIdRef`, `detailFetchIdRef`). Each fetch increments the counter before starting; on completion, the result is discarded if the counter has moved past the request's ID. This prevents stale responses from overwriting newer data when the user rapidly changes filters or selects different retrospectives.

### Types

```typescript
interface RetrospectiveListItem {
  id: string; taskId: string; taskTitle: string;
  agentId: string; agentName: string;
  contentPreview: string; createdAt: string;
}
interface RetrospectiveListResponse { retrospectives: RetrospectiveListItem[]; total: number; limit: number; offset: number; }
interface RetrospectiveDetailResponse {
  id: string; taskId: string; taskTitle: string; taskStatus: string;
  agentId: string; agentName: string;
  content: string; createdAt: string; taskCompletedAt: string | null;
}
interface RetrospectiveAgentStat { agentId: string; agentName: string; count: number; }
interface RetrospectiveStatsResponse {
  totalRetrospectives: number; byAgent: RetrospectiveAgentStat[];
  averageContentLength: number; latestRetrospectiveAt: string | null;
}
```

### Task Status Badges

| Status | Badge Color | Meaning |
|--------|-------------|---------|
| Completed | `done` (green) | Task finished successfully |
| InProgress | `active` (blue) | Task still running |
| Failed | `err` (red) | Task failed |
| Blocked | `warn` (amber) | Task blocked |

### Empty State

When no retrospectives exist: 🔬 "No retrospectives yet" with guidance that retrospectives are created automatically after agents complete tasks.

### Task Navigation

Task titles in both the list rows and the detail panel are clickable links that navigate to the Tasks tab and auto-expand the corresponding task. This enables tracing from a retrospective back to the task that produced it.

- **Props**: `onNavigateToTask?: (taskId: string) => void` — when provided, task titles render as styled links (cyan color, underline on hover, open icon)
- **Click behavior**: `stopPropagation` prevents the click from toggling row selection — only the navigation fires
- **TaskListPanel integration**: Receives `focusTaskId` and `onFocusHandled` props. When a matching task exists in the list, it resets filters to "all", disables sprint-only mode, and auto-expands the task. The focus is consumed (cleared) after expansion to prevent stale re-triggers on later tab visits.
- **Data flow**: RetrospectivePanel → `onNavigateToTask(taskId)` → App sets `focusTaskId` + switches to tasks tab → TaskListPanel expands task → calls `onFocusHandled` → App clears `focusTaskId`

### Real-Time Refresh

When a `TaskRetrospectiveCompleted` activity event arrives via SignalR/SSE, `useWorkspace` increments a `retroVersion` counter. This flows through `WorkspaceContent` as a `refreshTrigger` prop. RetrospectivePanel detects the change via a `useRef`-tracked previous value and re-fetches the list and stats. Follows the same pattern as `sprintVersion` → `SprintPanel`. A toast notification is also shown via `TOAST_EVENT_TYPES`.

### UI Layout

Each worktree renders as a card with:
- **Branch row**: Branch icon (cyan), branch name, dirty-files badge (green=clean, amber=1-5, red=6+), task status badge
- **Meta row**: Agent name (with tooltip showing agent ID), task title (truncated, tooltip for full text)
- **Last commit**: Short SHA + commit message (mono font), author tooltip
- **Dirty files preview**: List of dirty file paths (truncated list with "…and N more" overflow)
- **Diff stats** (right column): Files changed count, insertions (green +N), deletions (red −N)

### Data Flow

```
WorktreeStatusPanel → getWorktreeStatus() → GET /api/worktrees → WorktreeStatusSnapshot[]
  (auto-refresh every 30s)
```

### Types

```typescript
interface WorktreeStatusSnapshot {
  branch: string; relativePath: string; createdAt: string;
  statusAvailable: boolean; error: string | null;
  totalDirtyFiles: number; dirtyFilesPreview: string[];
  filesChanged: number; insertions: number; deletions: number;
  lastCommitSha: string | null; lastCommitMessage: string | null;
  lastCommitAuthor: string | null; lastCommitDate: string | null;
  taskId: string | null; taskTitle: string | null; taskStatus: string | null;
  agentId: string | null; agentName: string | null;
}
```

### Error States

- **Load failure**: Error card with icon and message
- **Worktree unavailable** (`statusAvailable: false`): Shows error text from `wt.error` inline
- **No worktrees**: 🌳 "No active worktrees" empty state

## Workspace Search (`SearchPanel.tsx`)

Full-text search across workspace messages (room + breakout) and tasks. Powered by SQLite FTS5 virtual tables with BM25 ranking.

### API

- **Endpoint**: `GET /api/search?q=term&scope=all|messages|tasks&messageLimit=25&taskLimit=25`
- **Backend**: `SearchService` (scoped) queries FTS5 tables `messages_fts`, `breakout_messages_fts`, `tasks_fts`
- **Workspace-scoped**: Results filtered to the active workspace
- **FTS5 safety**: Query terms are quoted and escaped (reuses `RecallHandler` pattern)
- **LIKE fallback**: If FTS5 tables are unavailable, falls back to `LIKE` queries
- **System messages excluded**: Only `Agent` and `User` messages are searched

### UI

- **Access**: 🔍 Search item in sidebar navigation, or press `/` to open
- **Search bar**: Debounced input (300ms) with Fluent UI Input
- **Scope filter**: All / Messages / Tasks toggle buttons
- **Message results**: Sender name, role pill, room name, breakout badge, FTS5 snippet with `«highlighted»` terms, timestamp. Click navigates to the room.
- **Task results**: Title, status badge, assigned agent, FTS5 snippet, created date. Click navigates to tasks tab.
- **Status bar**: Shows total result count and query term
- **Empty states**: Initial help text, and "no results" feedback

### Data Flow

```
SearchPanel → searchWorkspace(q, {scope}) → GET /api/search?q=...
  → SearchController → SearchService.SearchAsync()
    → FTS5 MATCH on messages_fts / breakout_messages_fts / tasks_fts
    → JOIN to entity tables for metadata
    → snippet() for highlighted excerpts
    → Merge room + breakout results, ordered by recency
  → SearchResults { messages[], tasks[], totalCount, query }
```

### Types

```typescript
type SearchScope = "all" | "messages" | "tasks";
interface MessageSearchResult {
  messageId: string; roomId: string; roomName: string;
  senderName: string; senderKind: MessageSenderKind; senderRole: string | null;
  snippet: string; sentAt: string; sessionId: string | null; source: "room" | "breakout";
}
interface TaskSearchResult {
  taskId: string; title: string; status: string;
  assignedAgentName: string | null; snippet: string; createdAt: string; roomId: string | null;
}
interface SearchResults { messages: MessageSearchResult[]; tasks: TaskSearchResult[]; totalCount: number; query: string; }
```

## Keyboard Shortcuts (`KeyboardShortcutsDialog.tsx`)

A help overlay listing all application keyboard shortcuts. Triggered by pressing `?` (when focus is not in an input/textarea/select).

### Shortcuts

| Shortcut | Action |
|----------|--------|
| `⌘/Ctrl + K` | Open command palette |
| `/` | Open search |
| `?` | Toggle keyboard shortcuts overlay |
| `Enter` | Send message in chat/DM |
| `Shift + Enter` | New line in message |
| `Esc` | Close settings / command palette |

### Behavior

- **Input guard**: All global shortcuts (`/`, `?`, `⌘K`) are suppressed when focus is in `INPUT`, `TEXTAREA`, `SELECT`, or `contentEditable` elements.
- **Platform-aware**: Displays `⌘` on macOS/iOS, `Ctrl` on other platforms (detected via `navigator.userAgent`).
- **Toggle**: Pressing `?` again closes the dialog.
- **Lazy-loaded**: Component is loaded via `React.lazy()` — zero cost until first open.

## Future Work

- Real-time updates via SignalR ✅ (implemented — `useActivityHub.ts`)
- ~~Sprint panel: SignalR integration for real-time stage/artifact updates~~ **RESOLVED** — Sprint events carry structured `metadata` payloads (sprintId, stage, action, status). `SprintPanel` applies optimistic updates for stage transitions, sign-off state, and completion. Artifact events trigger targeted fetch with stale-response protection. Debounced reconciliation (1.5s) replaces immediate full refetch. Event deduplication prevents replay issues on reconnect. Committed in this session.
- ~~Sprint panel: Markdown/JSON rendering for artifact content (currently raw text)~~ **RESOLVED** — `react-markdown` + `remark-gfm` render artifact content as formatted markdown. Committed in `08a7447`.
- ~~Sprint panel: Sprint metrics (time per stage, artifact word counts)~~ **RESOLVED** — `SprintPanel.tsx` shows time-in-stage durations and artifact word counts. Committed in `9fe6d1f`.
- ~~SSE activity stream integration~~ **RESOLVED** — `useWorkspace.ts` integrates both SignalR and SSE transports via `aa-transport` localStorage key. `useActivitySSE.ts` hook connects to `/api/activity/stream` with auto-reconnect. `ActivityController.cs` SSE endpoint serializes full `ActivityEvent` including `Metadata` field. Transport selection is transparent — both hooks always mount but only the active one connects (`enabled` parameter). Tests added for both backend (13 tests in `ActivityControllerTests.cs`) and frontend (10 tests in `useActivitySSE.test.ts`).
- ~~Notification setup wizard (component exists, not yet wired)~~ **RESOLVED** — `NotificationSetupWizard` refactored to multi-provider. Accepts `providerId` prop, fetches schema dynamically, supports Discord, Slack, and generic fallback. Settings tab routes all providers to the wizard.
- ~~TaskStatePanel integration~~ **RESOLVED** — `TaskListPanel.tsx` now includes interactive review panel with filter tabs (All/Review Queue/Active/Completed), expandable task detail, task comments, and review action buttons (Approve/Request Changes/Reject/Merge) wired through `executeCommand` API.
- ~~Human command metadata endpoint so the Commands tab can stop hardcoding command schemas~~ **RESOLVED** — `GET /api/commands/metadata` implemented. Frontend loads dynamically with fallback.
- ~~Session history / resume indicator~~ **RESOLVED** — `SessionHistoryPanel` in dashboard shows session stats, filterable session list with summaries. `ChatPanel` shows "Agents have context from a previous conversation session" banner when archived sessions exist for the current room.

## Browser Desktop Notifications (`useDesktopNotifications.ts`)

Alerts the human operator via the browser Notification API when the tab is hidden and important activity events occur.

### Behavior

- **Opt-in**: User enables via Settings → Advanced → Desktop Notifications toggle
- **Permission**: Requested on first enable; handles denial and revocation gracefully
- **Tab gating**: Notifications fire only when `document.hidden === true` (tab backgrounded)
- **Deduplication**: Event IDs tracked in a capped `Set` to prevent replay on SSE reconnect
- **Auto-close**: Notifications dismiss after 8 seconds
- **Click-to-focus**: Clicking a notification calls `window.focus()` and closes it
- **Persistence**: Preference stored in `localStorage` key `aa-desktop-notifications`

### Trigger Events

| Event Type | Notification Title |
|------------|-------------------|
| `DirectMessageSent` | "New message" |
| `AgentErrorOccurred` | "Agent error" |
| `SubagentFailed` | "Subagent failed" |
| `SprintCompleted` | "Sprint completed" |
| `SprintCancelled` | "Sprint cancelled" |
| `TaskCreated` | "Task created" |
| `TaskUnblocked` | "Task unblocked" |

### Integration

```
App.tsx (AppShell)
  └── useDesktopNotifications() → { enabled, setEnabled, permission, supported, notify }
      └── handleActivityToast callback → desktopNotif.notify(evt)
  └── SettingsPanel (desktopNotifications prop)
      └── Advanced tab → checkbox toggle with permission status display
```

### Types

```typescript
interface DesktopNotificationControls {
  enabled: boolean;
  setEnabled: (on: boolean) => void;
  permission: NotificationPermission | "unsupported";
  supported: boolean;
  notify: (evt: ActivityEvent) => void;
}
```
