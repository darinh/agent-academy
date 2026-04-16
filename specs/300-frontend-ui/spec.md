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
        ├── Sidebar nav (overview, search, directMessages, plan, tasks, artifacts, timeline, activity, sprint, dashboard, commands, memories, knowledge, specs, digests, retrospectives)
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
            ├── SettingsPanel.tsx (tabbed settings: agents, templates, notifications, github, models, advanced)
            │   ├── ModelsTab.tsx (LLM model list with executor status)
            │   ├── NotificationDeliveriesSection.tsx (delivery history + stats)
            │   └── DataExportSection.tsx (agent config + usage export)
            ├── ActivityFeedPanel.tsx (standalone activity feed with severity badges)
            ├── SpecSearchPanel.tsx (full-text spec search with debounced input)
            ├── AgentKnowledgePanel.tsx (per-agent knowledge browser with agent selector)
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
| `/api/activity/recent` | GET | Recent activity events (optional `limit`, default 50) |
| `/api/models` | GET | Available LLM models and executor status |
| `/api/specs/search` | GET | Full-text spec search (required `q`, optional `limit`) |
| `/api/agents/{agentId}/knowledge` | GET | Agent knowledge entries |
| `/api/notifications/deliveries` | GET | Notification delivery history (optional `limit`) |
| `/api/notifications/deliveries/stats` | GET | Delivery status aggregates |
| `/api/export/agents` | GET | Export agent config (optional `format`: json/csv) — triggers file download |
| `/api/export/usage` | GET | Export usage analytics (optional `format`, `hoursBack`) — triggers file download |

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

### Context Window Visibility (`ContextMeter.tsx`)

Each agent in the sidebar displays a compact context window usage meter showing how full the agent's LLM context is.

**Data flow:**
- After every LLM API call, `CopilotSdkSender` broadcasts a `ContextUsageUpdated` activity event via `ActivityBroadcaster` (not persisted — real-time only). The event `Metadata` contains `currentTokens`, `maxTokens`, `percentage`, and `model`.
- `useWorkspace` subscribes to `ContextUsageUpdated` events and maintains `contextByRoom` state (Map<roomId, Map<agentId, AgentContextUsage>>).
- On room change, `useWorkspace` fetches initial context via `GET /api/rooms/{roomId}/context-usage` (best-effort — errors are swallowed silently).
- The backend endpoint queries `LlmUsageTracker.GetLatestContextPerAgentAsync(roomId)` which returns the most recent `InputTokens` per agent. Each LLM call includes the full conversation, so the latest input token count represents the current context size.

**Model context limits:**
- `ModelContextLimits` (static class) maps model name substrings to known context window sizes (e.g., `gpt-4o` → 128K, `claude-sonnet-4` → 200K). Case-insensitive substring matching. Falls back to 128K for unknown models.

**Visual design (`ContextMeter.tsx`):**
- Compact inline meter: 32px-wide progress bar + percentage label
- Color coding: grey (<50%), blue (50-74%), amber (75-89%), red (≥90%)
- Tooltip shows exact token count, model name, and warning if >90%
- Severity escalation: events with ≥80% usage are broadcast with `ActivitySeverity.Warning`
- Renders inside agent pills in `AgentActivityBar` and next to agent state badges in `SidebarPanel`

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
- **Compact button** (`SessionToolbar.tsx`): Labeled "⟳ Compact". Calls `POST /api/rooms/{roomId}/compact` to reset agent CLI sessions and free context window space. Shows "Compacting…" with reduced opacity while in flight. On success, displays a green status message (e.g., "Compacted 3 session(s)") for 4 seconds. On failure, displays a red "Failed to compact sessions" message. Fires optional `onCompacted` callback on success. The button has a `title` tooltip explaining its purpose.
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

The Settings panel is a full tabbed configuration page with seven tabs:

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

#### Delivery History (`NotificationDeliveriesSection.tsx`)

Embedded below the provider setup controls. Shows recent notification delivery records with aggregated status stats.

- **Stats summary**: Badge row showing delivery counts by status (e.g., `Delivered: 42`, `Failed: 3`). Color-coded — green for `Delivered`/`Sent`, red for `Failed`, muted for others.
- **Delivery list**: Grid layout per row — status badge, title/body (truncated with ellipsis), channel name, formatted timestamp. Max-height 400px with vertical scroll.
- **Refresh button**: Subtle button in section header to re-fetch.
- **Data**: `Promise.all([getNotificationDeliveries(30), getNotificationDeliveryStats()])` on mount.

API:
- `GET /api/notifications/deliveries?limit=30` → `NotificationDeliveryDto[]`
- `GET /api/notifications/deliveries/stats` → `NotificationDeliveryStats` (Record<string, number>)

Types:
```typescript
interface NotificationDeliveryDto {
  id: number; channel: string; title: string | null; body: string | null;
  roomId: string | null; agentId: string | null; providerId: string;
  status: string; error: string | null; attemptedAt: string;
}
type NotificationDeliveryStats = Record<string, number>;
```

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

### Models Tab (`ModelsTab.tsx`)

Displays available LLM models and executor operational status. Data loaded from `GET /api/models`.

- **Executor status badge**: Green `Operational` or red `Degraded` badge via `V3Badge`.
- **Model list**: Each model renders as a card showing display name (bold) and model ID (monospace, muted). Styled with dark background and subtle border.
- **Footer**: Model count text.
- **Empty state**: "No models configured" message when the models array is empty.
- **Error/loading states**: Spinner during fetch, error text on failure.

API:
- `GET /api/models` → `ModelsResponse`

Types:
```typescript
interface ModelInfo { id: string; name: string; }
interface ModelsResponse { models: ModelInfo[]; executorOperational: boolean; }
```

### Advanced Tab

System-level settings:
- **Main Room Epoch** and **Breakout Room Epoch**: Configure conversation session rotation thresholds (message count before automatic session archival).

#### Data Export (`DataExportSection.tsx`)

Embedded below the epoch settings in the Advanced tab. Allows downloading agent configuration and usage analytics for external analysis or backup.

- **Agent configuration**: Download as JSON or CSV via `GET /api/export/agents?format={format}`. Triggers browser file download (`agents-export.json` or `.csv`).
- **Usage analytics**: Download as JSON or CSV via `GET /api/export/usage?format={format}`. Triggers browser file download (`usage-export.json` or `.csv`).
- **State**: One operation at a time — buttons disable while any export is in progress.
- **Feedback**: Success message (green) or error message (red) shown inline after each attempt.

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

### Task Navigation from Sources

In the detail panel's source retrospectives section, each source's task ID is a clickable link (cyan color, open icon) that navigates to the Tasks tab and auto-expands the corresponding task. The link is keyboard-accessible (`tabIndex={0}`, Enter key handler). This follows the same cross-panel navigation pattern as RetrospectivePanel's task-title links.

- **Props**: `onNavigateToTask?: (taskId: string) => void` — when provided, task IDs in source cards render as styled links
- **Data flow**: DigestPanel source "Task: {taskId}" → `onNavigateToTask(taskId)` → App sets `focusTaskId` + switches to tasks tab

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

### Task Filter (Cross-Panel Navigation)

When the user navigates from a task detail's "View retrospectives" link, the Retrospectives tab opens pre-filtered to show only retrospectives for that task.

- **Props**: `filterTaskId?: string | null` — when set, passed as `taskId` param to `GET /api/retrospectives`; `onClearTaskFilter?: () => void` — fires when the dismiss button is clicked
- **Filter bar**: A styled chip (cyan border, translucent background) displays "Filtered by task: {taskId}" with a dismiss button that clears the filter
- **Auto-clear**: The filter is automatically cleared when the user navigates away from the Retrospectives tab (via `useEffect` on `tab` in App.tsx), so it never persists as a stale filter
- **Data flow**: TaskDetail "View retrospectives" link → TaskListPanel `onViewRetros(taskId)` → App sets `retroFilterTaskId` + switches to retro tab → RetrospectivePanel passes `filterTaskId` to API → user clears via dismiss or navigates away

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

## Server Instance History (`RestartHistoryPanel.tsx`)

Displays a paginated table of server instances with aggregated restart statistics. Embedded within `DashboardPanel` under the "Server Instance History" section heading.

### Data Flow

```
RestartHistoryPanel
  ├── fetchData → Promise.allSettled([getRestartHistory(limit, offset), getRestartStats(hoursBack)])
  │   ├── GET /api/system/restarts?limit=10&offset=N → RestartHistoryResponse
  │   └── GET /api/system/restarts/stats?hours=24   → RestartStatsDto
  ├── Stats cards row: Instances, Crashes, Restarts, Clean Stops, Running
  ├── Instance table: Status | Started | Duration | Version | Exit
  └── Pagination: ← Newer / N–M of Total / Older →
```

### Props

| Prop | Type | Default | Description |
|------|------|---------|-------------|
| `hoursBack` | `number?` | `24` | Time window for stats aggregation (passed from DashboardPanel time selector) |

### Stats Cards

Five stat cards in a responsive grid (`repeat(auto-fit, minmax(100px, 1fr))`):

| Card | Value Source | Color |
|------|-------------|-------|
| Instances (Nh) | `stats.totalInstances` | default |
| Crashes | `stats.crashRestarts` | `--aa-copper` |
| Restarts | `stats.intentionalRestarts` | `--aa-gold` |
| Clean Stops | `stats.cleanShutdowns` | `--aa-lime` |
| Running | `stats.stillRunning` | `--aa-cyan` |

### Instance Table

Paginated with `PAGE_SIZE = 10`. Columns:

| Column | Source | Rendering |
|--------|--------|-----------|
| Status | `shutdownReason` | Icon + `V3Badge` via `reasonBadge()`. Shows crash-recovery badge (`⚡ crash recovery`) when `crashDetected = true` |
| Started | `startedAt` | `formatTimestamp()` |
| Duration | `startedAt` → `shutdownAt` | `formatElapsed()` with `granularity: "seconds"`, shows `(running)` for active instance |
| Version | `version` | Monospace |
| Exit | `exitCode` | Monospace, `—` for null |

**Status badges**: `Running` → info/play, `CleanShutdown` → ok/checkmark, `IntentionalRestart` → warn/sync, `Crash` → err/error, default → bug/warning.

The currently running instance row has a subtle highlight (`rgba(108, 182, 255, 0.06)`).

### Error Handling

- `Promise.allSettled` fetches history and stats independently — stats failure is non-critical (shows stale stats if available)
- Stale-response protection via `fetchIdRef` (discards out-of-order responses)
- Offset clamping: if total shrinks below current page, auto-clamps to last valid page
- Failed refresh with cached data shows inline error banner with "showing cached data"
- Full failure (no cached data) shows error panel

### Types

```ts
interface ServerInstanceDto {
  id: string; startedAt: string; shutdownAt: string | null;
  exitCode: number | null; crashDetected: boolean;
  version: string; shutdownReason: string;
}
interface RestartHistoryResponse { instances: ServerInstanceDto[]; total: number; limit: number; offset: number; }
interface RestartStatsDto {
  totalInstances: number; crashRestarts: number; intentionalRestarts: number;
  cleanShutdowns: number; stillRunning: number; windowHours: number;
  maxRestartsPerWindow: number; restartWindowHours: number;
}
```

## Room Stats (`RoomStatsPanel.tsx`)

Displays room-scoped LLM usage aggregates, per-agent cost breakdown, and recent errors for the selected room. Embedded within `WorkspaceOverviewPanel` under "Room Stats — {room.name}".

### Data Flow

```
RoomStatsPanel({ roomId })
  ├── fetchData → Promise.allSettled([getRoomUsage(roomId), getRoomUsageByAgent(roomId), getRoomErrors(roomId, 20)])
  │   ├── GET /api/rooms/{roomId}/usage        → UsageSummary
  │   ├── GET /api/rooms/{roomId}/usage/agents  → AgentUsageSummary[]
  │   └── GET /api/rooms/{roomId}/errors?limit=20 → ErrorRecord[]
  ├── Usage cards: Input tokens | Output tokens | Cost | Calls
  ├── Per-agent table: Agent | In | Out | Cost | Calls
  └── Errors table: Agent | Type | Message | Time (max 5 shown)
```

### Props

| Prop | Type | Description |
|------|------|-------------|
| `roomId` | `string` | Room to fetch stats for (reactive — clears state on change) |

### Usage Cards

Four stat cards in a responsive grid:

| Card | Value Source | Color | Formatting |
|------|-------------|-------|------------|
| Input | `usage.totalInputTokens` | default | `formatTokenCount()` |
| Output | `usage.totalOutputTokens` | default | `formatTokenCount()` |
| Cost | `usage.totalCost` | `--aa-lime` | `formatCost()` |
| Calls | `usage.requestCount` | `--aa-gold` | raw number |

Usage cards only render when `usage.requestCount > 0`.

### Per-Agent Table

Columns: Agent (badge), In, Out, Cost, Calls. Right-aligned numeric columns. Agent IDs shown in `V3Badge` with `info` color.

### Errors Table

Shows up to 5 most recent errors. Columns: Agent (mono), Type (`V3Badge` via `errorTypeBadge()`), Message (truncated with ellipsis at 200px), Time (mono, formatted local timestamp without seconds). If more than 5 errors exist, shows "Showing 5 of N errors. See Dashboard for full details."

When no errors exist but usage is present, shows a green "No errors in this room" message.

### Room Switching

When `roomId` changes, the component clears all stale data (`usage`, `agents`, `errors`) before fetching new data. Tracked via `prevRoomRef` to detect switches.

### Error Handling

- Three independent error states (`usageError`, `agentsError`, `errorsError`) — each section degrades independently
- All-failed state shows single "Failed to load room stats" error
- `fetchIdRef` prevents stale response races
- Empty state (no usage, no errors, no agent data, no errors): "No activity recorded for this room yet" with checkmark icon

### Types

```ts
interface UsageSummary {
  totalInputTokens: number; totalOutputTokens: number;
  totalCost: number; requestCount: number; models: string[];
}
interface AgentUsageSummary {
  agentId: string; totalInputTokens: number; totalOutputTokens: number;
  totalCost: number; requestCount: number;
}
interface ErrorRecord {
  agentId: string; roomId: string; errorType: string;
  message: string; recoverable: boolean; timestamp: string;
}
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

## Artifacts Panel (`ArtifactsPanel.tsx`)

Displays agent file operations and quality evaluations for the selected room. Accessible via the "Artifacts" sidebar tab.

### Data Flow

```
ArtifactsPanel({ roomId, refreshTrigger })
  ├── fetchArtifacts → GET /api/rooms/{roomId}/artifacts → ArtifactRecord[]
  │   └── Independent loading state (fast — reads DB)
  ├── fetchEvaluations → GET /api/rooms/{roomId}/evaluations → { artifacts: EvaluationResult[], aggregateScore }
  │   └── Independent loading state (slow — reads files from disk)
  └── Race condition protection: cancelled flag on roomId change
```

### Quality Evaluations Section

Per-file evaluation cards in a responsive grid:

| Check | Points | Description |
|-------|--------|-------------|
| Exists | 40 | File is present on disk |
| Non-Empty | 20 | File has content |
| Syntax Valid | 25 | JSON/XML parses correctly (other formats always pass) |
| Complete | 15 | No TODO/FIXME/HACK markers |

- **Score bar**: Color-coded (green ≥80, yellow ≥50, red <50) with percentage
- **Check marks**: ✓/✗ for each criterion
- **Issues list**: Rendered below checks when evaluation finds problems
- **Aggregate score badge**: Top-right header shows overall room quality

### File Operations Log Section

Collapsible table of recent file operations (default: expanded):

| Column | Content |
|--------|---------|
| Time | Formatted timestamp (HH:MM:SS) |
| Agent | Agent ID that performed the operation |
| Operation | Created (green), Updated (blue), Committed (purple), Deleted (red) |
| File | File path (truncated at 50 chars with ellipsis) |

Labeled "Recent File Operations" — backend caps at 100 events by default (configurable via `limit` parameter).

### Types

```typescript
type ArtifactOperation = "Created" | "Updated" | "Committed" | "Deleted";

interface ArtifactRecord {
  agentId: string; roomId: string; filePath: string;
  operation: ArtifactOperation; timestamp: string;
}

interface EvaluationResult {
  filePath: string; score: number;
  exists: boolean; nonEmpty: boolean; syntaxValid: boolean; complete: boolean;
  issues: string[];
}

interface RoomEvaluationResponse {
  artifacts: EvaluationResult[]; aggregateScore: number;
}
```

### Tests

21 tests in `artifactsPanel.dom.test.tsx`:
- Empty states (no room, no artifacts)
- Data fetching (room ID, refresh trigger, room switch refetch)
- Evaluation display (scores, checks, issues, error handling)
- Log display (entries, operations, collapse/expand)
- Independent loading (artifacts visible while evaluations load)
- Refresh button behavior

## Activity Feed Panel (`ActivityFeedPanel.tsx`)

Standalone activity feed accessible via the "Activity" sidebar tab (`⚡`). Displays recent workspace events with severity badges, event categories, and relative timestamps.

### Data Flow

```
ActivityFeedPanel
  └── getRecentActivity(50) → GET /api/activity/recent?limit=50 → ActivityEvent[]
```

### UI

- **Header**: "Recent Activity" title with event count badge and refresh button.
- **Event list**: Each event renders as a row with:
  - Relative timestamp (right-aligned, monospace, 60px fixed width)
  - Severity badge via `V3Badge` — `Error` (red/err), `Warning` (amber/warn), default (muted)
  - Event category label derived from event type via `eventCategory()` utility
  - Event message text
- **Empty state**: 📭 "No activity yet" with guidance text.
- **Error state**: ⚡ "Failed to load activity" with error detail.
- **Loading**: Spinner with "Loading activity…" label.
- **Refresh**: Manual refresh via header button; no auto-polling (the panel fetches once on mount).

### Types

Reuses existing `ActivityEvent` type from the API layer.

## Spec Search Panel (`SpecSearchPanel.tsx`)

Full-text search across project specifications. Accessible via the "Specs" sidebar tab (`📜`).

### Data Flow

```
SpecSearchPanel
  └── searchSpecs(query, 20) → GET /api/specs/search?q={query}&limit=20 → SpecSearchResult[]
```

### UI

- **Search bar**: Debounced input (400ms) with `SearchRegular` icon. Pressing Enter triggers immediate search (cancels pending debounce). Fluent UI `Input` with `underline` appearance.
- **Result cards**: Each result shows:
  - Heading (bold, 13px)
  - Relevance score badge (`V3Badge`, percentage, e.g., "85%")
  - Summary text
  - File path (monospace, muted)
  - Matched terms (italic, muted) — if present
- **Initial state**: 📜 "Search specifications" with guidance text (shown before any search).
- **No results**: 🔍 "No results" with the query echoed back.
- **Error state**: Amber warning text inline.
- **Loading**: Spinner next to the search input (non-blocking — results area stays visible).

### Debounce Pattern

Uses a `useRef`-tracked `setTimeout` with 400ms delay. The ref is explicitly typed as `ReturnType<typeof setTimeout> | undefined`. On Enter keypress, the pending debounce is cancelled and search fires immediately.

### Types

```typescript
interface SpecSearchResult {
  id: string; heading: string; summary: string;
  filePath: string; score: number; matchedTerms: string;
}
```

## Agent Knowledge Panel (`AgentKnowledgePanel.tsx`)

Per-agent knowledge entry browser. Accessible via the "Knowledge" sidebar tab (`📖`).

### Props

| Prop | Type | Description |
|------|------|-------------|
| `agents` | `AgentDefinition[]` | Configured agents list (passed from App.tsx via WorkspaceContent) |

### Data Flow

```
AgentKnowledgePanel({ agents })
  └── getAgentKnowledge(agentId) → GET /api/agents/{agentId}/knowledge → AgentKnowledgeResponse
```

### UI

- **Header**: "Agent Knowledge" title with entry count badge and refresh button.
- **Agent selector**: Native `<select>` dropdown listing all configured agents by name and role. Auto-selects first agent on mount.
- **Entry list**: Each knowledge entry renders in a monospace card with dark background.
- **Empty state (no agents)**: 📖 "No agents configured" with guidance text.
- **Empty state (no entries)**: 📭 "No knowledge entries" with agent name echoed.
- **Error state**: Amber warning text with error detail.
- **Loading**: Spinner with "Loading knowledge…" label.
- **Refresh**: Manual refresh via header button; refetches when selected agent changes.

### Types

```typescript
interface AgentKnowledgeResponse { entries: string[]; }
```

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

## Known Gaps

- ~~Artifact panel: SignalR-driven auto-refresh when `ArtifactEvaluated` events fire (currently requires manual refresh)~~ **Resolved** — `useWorkspace.ts` handles `ArtifactEvaluated` events by incrementing `artifactVersion` counter. `WorkspaceContent.tsx` passes it to `ArtifactsPanel` as `refreshTrigger`, triggering automatic re-fetch of both artifacts and evaluations. Also added `"artifacts"` to `VALID_TABS` for tab persistence.
- ~~Real-time updates via SignalR~~ **Resolved** — implemented in `useActivityHub.ts`.
- ~~Sprint panel: SignalR integration for real-time stage/artifact updates~~ **Resolved** — Sprint events carry structured `metadata` payloads (sprintId, stage, action, status). `SprintPanel` applies optimistic updates for stage transitions, sign-off state, and completion. Artifact events trigger targeted fetch with stale-response protection. Debounced reconciliation (1.5s) replaces immediate full refetch. Event deduplication prevents replay issues on reconnect.
- ~~Sprint panel: Markdown/JSON rendering for artifact content (currently raw text)~~ **Resolved** — `react-markdown` + `remark-gfm` render artifact content as formatted markdown (`08a7447`).
- ~~Sprint panel: Sprint metrics (time per stage, artifact word counts)~~ **Resolved** — `SprintPanel.tsx` shows time-in-stage durations and artifact word counts (`9fe6d1f`).
- ~~SSE activity stream integration~~ **Resolved** — `useWorkspace.ts` integrates both SignalR and SSE transports via `aa-transport` localStorage key. `useActivitySSE.ts` hook connects to `/api/activity/stream` with auto-reconnect. Transport selection is transparent — both hooks always mount but only the active one connects (`enabled` parameter).
- ~~Notification setup wizard (component exists, not yet wired)~~ **Resolved** — `NotificationSetupWizard` refactored to multi-provider. Accepts `providerId` prop, fetches schema dynamically, supports Discord, Slack, and generic fallback.
- ~~TaskStatePanel integration~~ **Resolved** — `TaskListPanel.tsx` includes interactive review panel with filter tabs (All/Review Queue/Active/Completed), expandable task detail, task comments, and review action buttons wired through `executeCommand` API.
- ~~Human command metadata endpoint so the Commands tab can stop hardcoding command schemas~~ **Resolved** — `GET /api/commands/metadata` implemented. Frontend loads dynamically with fallback.
- ~~Session history / resume indicator~~ **Resolved** — `SessionHistoryPanel` in dashboard shows session stats, filterable session list with summaries. `ChatPanel` shows "Agents have context from a previous conversation session" banner when archived sessions exist for the current room.
- No frontend E2E coverage for the full OAuth login → SignalR connect happy path — **Accepted**: requires a browser (cannot be automated from the server-side test harness). HTTP-level authentication behavior is covered; browser-level SignalR connect is verified manually.
- No visual regression tests — **Accepted**: component DOM tests in Vitest cover rendering and interaction; no screenshot diffing is currently configured.

## Revision History

### 2026-04-16
- **Spec hygiene**: Renamed `Future Work` section to `Known Gaps` to match pattern across specs 000–018. Moved `Browser Desktop Notifications` component section ahead of `Known Gaps` so all component sections are contiguous. Added `Revision History` and documented currently accepted gaps (E2E OAuth flow, visual regression).

### 2026-04-15
- **Added**: Activity Feed, Spec Search, and Agent Knowledge sidebar panels (`4e59e7a`).
- **Added**: Models tab, notification delivery history, and data export to Settings (`7d82b7a`).
- **Added**: Frontend API coverage for 25+ uncovered backend endpoints (`ed643a3`).
- **Sync**: `ActivityEventType` aligned with backend enum (`6e17dd0`).
- **Docs**: Sidebar panels, settings sub-tabs, and new API endpoints documented (`ad302fe`).

### 2026-04-14
- **Added**: Artifacts Panel with quality evaluations and file operations log (`80e0516`); SignalR auto-refresh on `ArtifactEvaluated` (`d8f05d0`).
- **Added**: Context window visibility with real-time usage meters (`244bd8d`).
- **Added**: Phase transition prerequisites with server-side validation (`71a14f9`).
- **Added**: Manual session compaction button in `SessionToolbar` (`da38c6f`).
- **Added**: Task priority (Critical/High/Medium/Low) (`e7f03df`).
- **Added**: Missing error states, loading indicators, and empty states across panels (`d012e96`).
- **Added**: Bidirectional cross-panel navigation between Tasks and Retrospectives (`3c0cf2f`, `928b2ce`, `ed7fbc6`).
- **Added**: Real-time SignalR refresh for `MemoryBrowserPanel` (`6c2223f`).
- **Fixed**: Error responses standardized to RFC 7807 `ProblemDetails` (`83978a1`).
- **Fixed**: Keyboard accessibility on `RetrospectivePanel` task-title links (`c5e6293`).

### 2026-04-13 and earlier
- **Evolution**: Room-centric conversation model, direct messages, sprint system UI, digest panel, worktree status, retrospectives, workspace search, restart history, room stats, keyboard shortcuts dialog, browser desktop notifications. See git history on `src/agent-academy-client/` for the full timeline.

### 2026-04-16 (b) — OAuth / SignalR
- **Verified**: Unauthenticated `/hubs/activity/negotiate` and `/api/*` return `401` (not `302`) so the SignalR client fails fast instead of following an HTML redirect (`03cfce2`). Browser-level SignalR connect after login remains a manual smoke step.
