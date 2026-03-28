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
    │   ├── Per-agent thinking spinner (spinning ring around status dot)
    │   └── Switch Project button
    └── Main workspace
        ├── Workspace header + phase pill + UserBadge
        ├── Tab bar (chat, tasks, plan, timeline, dashboard, overview)
        └── Tab content panels
            ├── ChatPanel.tsx (with SignalR connection status bar)
            ├── TaskListPanel.tsx
            ├── PlanPanel.tsx
            ├── TimelinePanel.tsx
            ├── DashboardPanel.tsx
            └── WorkspaceOverviewPanel.tsx
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
| `/api/filesystem/browse` | GET | Browse filesystem |
| `/api/rooms/{id}/human` | POST | Send human message |
| `/api/rooms/{id}/phase` | POST | Transition phase |
| `/api/tasks` | POST | Create task |

### Browse response shape (from server):
```json
{ "current": "/path", "parent": "/parent", "entries": [{ "name": "dir", "path": "/path/dir", "isDirectory": true }] }
```

## Theme / Role Colors

Defined in `theme.ts`. Each agent role maps to accent/foreground/avatar colors:

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
- Sidebar + main content: `320px minmax(0, 1fr)` (open) / `88px minmax(0, 1fr)` (collapsed)
- Dark gradient background with glassmorphism panels
- `index.css` provides minimal resets; all component styles use Griffel `makeStyles`

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

## Future Work

- Real-time updates via SignalR ✅ (implemented — `useActivityHub.ts`)
- SSE activity stream integration
- Notification setup wizard (component exists, not yet wired)
- TaskStatePanel integration
