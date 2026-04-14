# 016 — API Reference

## Purpose

Unified catalog of all REST endpoints, SSE streams, and the SignalR hub exposed by Agent Academy Server. Organized by functional domain for discoverability. For detailed behavioral contracts, see the domain-specific specs referenced in each section.

## Current Behavior

> **Status: Implemented** — 145 endpoints across 26 controllers, 3 SSE streams, 1 SignalR hub.

## Authentication

All endpoints (except `/`, `/healthz`, and `/api/auth/*`) require one of:

| Method | Header / Mechanism | Identity |
|--------|--------------------|----------|
| GitHub OAuth | Cookie (`aspnetcore.auth`) | Authenticated GitHub user |
| Consultant Key | `X-Consultant-Key: {secret}` | `SenderKind = User`, `SenderId = "consultant"` |

If `ConsultantApi:SharedSecret` is not configured, the consultant auth scheme is not registered. See [012 — Consultant API](../012-consultant-api/spec.md) for details.

## Conventions

- All routes are prefixed with `/api/` unless noted otherwise.
- Route parameters use `{param}` syntax.
- Query parameters are optional unless marked **(required)**.
- Error responses use `ProblemDetails` format (RFC 7807).
- List endpoints support pagination via `limit`/`offset` query parameters where noted.
- SSE streams use `text/event-stream` content type with `data:` JSON payloads.

### Error Response Format

All error responses use [RFC 7807 ProblemDetails](https://www.rfc-editor.org/rfc/rfc7807). The response body is:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Human-readable explanation of what went wrong.",
  "code": "machine_readable_code"
}
```

| Field | Type | Always Present | Description |
|-------|------|----------------|-------------|
| `type` | string | Yes | URI identifying the error type |
| `title` | string | Yes | Short HTTP status phrase |
| `status` | integer | Yes | HTTP status code |
| `detail` | string | Yes | Specific error message |
| `code` | string | No | Machine-readable error code (in `extensions`) |

**Backend**: All controllers use the `ApiProblem` factory (`Controllers/ApiProblem.cs`) which produces `ProblemDetails` instances with an optional `code` extension. Controllers return these via `BadRequest(pd)`, `NotFound(pd)`, `Conflict(pd)`, etc.

**Frontend**: The `extractApiError()` helper in `api/core.ts` reads `body.detail ?? body.error ?? body.title` to extract messages, supporting both ProblemDetails and any legacy format.

---

## 1. System & Health

> Controller: `SystemController` — no route prefix

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/` | Service info and endpoint list | — | `200` JSON |
| GET | `/healthz` | Liveness probe (basic) | — | `200 "Healthy"` |
| GET | `/health` | Readiness probe (checks DB + executor) | — | `200` / `503` JSON |
| GET | `/api/health/instance` | Instance-level health and status | — | `InstanceHealth` |
| GET | `/api/overview` | Workspace overview (agents, rooms, tasks, sprint) | — | `WorkspaceOverview` |
| GET | `/api/agents/configured` | All configured agents (catalog + custom) | — | `List<AgentDefinition>` |
| GET | `/api/models` | Available LLM models and executor status | — | model list |
| POST | `/api/system/reload-catalog` | Trigger manual agent catalog reload | — | `200` / error |
| GET | `/api/system/restarts` | Restart history | `?limit` `?offset` | paginated list |
| GET | `/api/system/restarts/stats` | Restart statistics | `?hours` | stats object |

## 2. Authentication

> Controller: `AuthController` — route: `api/auth`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/auth/status` | Auth enabled/authenticated state, current user | — | auth status |
| GET | `/api/auth/login` | Start GitHub OAuth flow (redirect) | — | `302` redirect |
| POST | `/api/auth/logout` | Sign out and clear tokens | — | `200` |

## 3. Workspace Management

> Controller: `WorkspaceController` — route: `api`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/workspace` | Active workspace metadata | — | workspace object |
| GET | `/api/workspaces` | List known workspaces | — | `List<WorkspaceMeta>` |
| PUT | `/api/workspace` | Switch active workspace | body: `{ path }` | workspace object |
| POST | `/api/workspaces/scan` | Scan directory for project metadata | body: `{ path }` | `ProjectScanResult` |
| POST | `/api/workspaces/onboard` | Scan, upsert workspace, ensure room | body: `{ path }` | `OnboardResult` |

See [005 — Domain Services Layer](../005-workspace-runtime/spec.md) for workspace lifecycle.

## 4. Rooms

> Controller: `RoomController` — route: `api/rooms`

### 4.1 Room CRUD

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms` | List rooms | `?includeArchived` | `List<RoomSnapshot>` |
| GET | `/api/rooms/{roomId}` | Get room details | — | `RoomSnapshot` |
| POST | `/api/rooms` | Create a room | body: `{ name, ... }` | `RoomSnapshot` |
| PUT | `/api/rooms/{roomId}/name` | Rename room | body: `{ name }` | `RoomSnapshot` |
| POST | `/api/rooms/cleanup` | Archive stale rooms (all tasks complete) | — | result |

### 4.2 Room Membership

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| POST | `/api/rooms/{roomId}/agents/{agentId}` | Add agent to room | — | `AgentLocation` |
| DELETE | `/api/rooms/{roomId}/agents/{agentId}` | Remove agent from room | — | `AgentLocation` |

### 4.3 Messages

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms/{roomId}/messages` | Get room messages | `?after` `?limit` `?sessionId` | `RoomMessagesResponse` |
| POST | `/api/rooms/{roomId}/messages` | Post agent/system message | body: `PostMessageRequest` | `ChatEnvelope` |
| POST | `/api/rooms/{roomId}/human` | Post human message (triggers orchestrator) | body: `HumanMessageRequest` | `ChatEnvelope` |
| GET | `/api/rooms/{roomId}/messages/stream` | **SSE** — stream room messages | `?after` | `text/event-stream` |

> **SSE stream**: Replays missed messages since `after`, then streams new messages as `data:` JSON events. Connection stays open until client disconnects.

### 4.4 Room Operations

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| POST | `/api/rooms/{roomId}/phase` | Transition room phase | body: `PhaseTransitionRequest` | `RoomSnapshot` |
| POST | `/api/rooms/{roomId}/compact` | Compact room (invalidate agent sessions) | — | `200` |

### 4.5 Room Plan

> Controller: `PlanController`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms/{roomId}/plan` | Get current room plan | — | `PlanContent` |
| PUT | `/api/rooms/{roomId}/plan` | Create or update plan | body: `PlanContent` | `200` |
| DELETE | `/api/rooms/{roomId}/plan` | Delete plan | — | `200` |

### 4.6 Room Analytics

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms/{roomId}/usage` | Room token usage summary | — | `UsageSummary` |
| GET | `/api/rooms/{roomId}/usage/agents` | Per-agent usage breakdown | — | `List<AgentUsageSummary>` |
| GET | `/api/rooms/{roomId}/usage/records` | Recent usage records | `?agentId` `?limit` | `List<LlmUsageRecord>` |
| GET | `/api/rooms/{roomId}/errors` | Room error records | `?limit` | `List<ErrorRecord>` |
| GET | `/api/rooms/{roomId}/artifacts` | Room artifacts (placeholder) | — | `[]` |
| GET | `/api/rooms/{roomId}/evaluations` | Room evaluations (placeholder) | — | `[]` |

### 4.7 Sessions

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/rooms/{roomId}/sessions` | List room sessions | `?status` `?limit` `?offset` | `SessionListResponse` |
| POST | `/api/rooms/{roomId}/sessions` | Create new conversation session | — | `ConversationSessionSnapshot` |

## 5. Direct Messages

> Controller: `DmController` — route: `api/dm`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/dm/threads` | List DM threads | — | `List<DmThreadSummary>` |
| GET | `/api/dm/threads/{agentId}` | Get thread messages | — | `List<DmMessage>` |
| POST | `/api/dm/threads/{agentId}` | Send DM to agent (triggers response) | body: `SendDmRequest` | `DmMessage` |
| GET | `/api/dm/threads/{agentId}/stream` | **SSE** — stream DM messages | `?after` | `text/event-stream` |
| GET | `/api/dm/threads/stream` | **SSE** — stream thread list updates | — | `text/event-stream` |

> **SSE streams**: The per-agent stream replays missed messages since `after`, then pushes new messages. The thread-list stream pushes invalidation events when any thread changes.

## 6. Agents

> Controllers: `AgentController`, `AgentConfigController` — route: `api/agents`

### 6.1 Agent State

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/agents/locations` | Current agent locations across rooms | — | `List<AgentLocation>` |
| PUT | `/api/agents/{agentId}/location` | Move agent to room/state | body: `UpdateLocationRequest` | `AgentLocation` |
| GET | `/api/agents/{agentId}/sessions` | Active and archived breakout sessions | — | `List<BreakoutRoom>` |
| POST | `/api/agents/{agentId}/run` | Execute a prompt against the agent | body: raw text | execution result |

### 6.2 Agent Knowledge

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/agents/{agentId}/knowledge` | Non-expired agent knowledge entries | — | knowledge list |
| POST | `/api/agents/{agentId}/knowledge` | Upsert knowledge entry | body: `AppendKnowledgeRequest` | knowledge entry |
| GET | `/api/knowledge` | Shared knowledge grouped by agent | — | grouped knowledge |

See [008 — Agent Memory System](../008-agent-memory/spec.md) for knowledge lifecycle.

### 6.3 Agent Quota

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/agents/{agentId}/quota` | Current quota status | — | `QuotaStatus` |
| PUT | `/api/agents/{agentId}/quota` | Update quota limits | body: `UpdateQuotaRequest` | `QuotaStatus` |
| DELETE | `/api/agents/{agentId}/quota` | Remove quota (unlimited) | — | `200` |

### 6.4 Agent Configuration

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/agents/{agentId}/config` | Effective config + stored override | — | `AgentConfigResponse` |
| PUT | `/api/agents/{agentId}/config` | Create/update config override | body: `UpsertAgentConfigRequest` | `AgentConfigResponse` |
| POST | `/api/agents/{agentId}/config/reset` | Reset to catalog defaults | — | `AgentConfigResponse` |
| POST | `/api/agents/custom` | Create custom agent from prompt | body: `CreateCustomAgentRequest` | `AgentDefinition` |
| DELETE | `/api/agents/custom/{agentId}` | Delete custom agent | — | `200` |

See [003 — Agent Execution System](../003-agent-system/spec.md) for agent lifecycle.

## 7. Tasks

> Controller: `CollaborationController` — various routes

### 7.1 Task CRUD

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/tasks` | List tasks | `?sprintId` | `List<TaskSnapshot>` |
| GET | `/api/tasks/{taskId}` | Get single task | — | `TaskSnapshot` |
| POST | `/api/tasks` | Submit task (creates + starts orchestration) | body: `TaskAssignmentRequest` | `TaskAssignmentResult` |

### 7.2 Task Lifecycle

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| PUT | `/api/tasks/{taskId}/assign` | Assign agent to task | body: `AssignTaskRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/status` | Update task status | body: `UpdateTaskStatusRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/branch` | Record branch name | body: `UpdateTaskBranchRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/pr` | Record PR info | body: `UpdateTaskPrRequest` | `TaskSnapshot` |
| PUT | `/api/tasks/{taskId}/complete` | Mark task complete | body: `CompleteTaskRequest` | `TaskSnapshot` |

### 7.3 Task Dependencies

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/tasks/{taskId}/dependencies` | Get dependency info | — | `TaskDependencyInfo` |
| POST | `/api/tasks/{taskId}/dependencies` | Add dependency | body: `AddDependencyRequest` | `TaskDependencyInfo` |
| DELETE | `/api/tasks/{taskId}/dependencies/{dependsOnTaskId}` | Remove dependency | — | `TaskDependencyInfo` |

### 7.4 Task Metadata

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/tasks/{taskId}/comments` | Task comments | — | `List<TaskComment>` |
| GET | `/api/tasks/{taskId}/specs` | Spec links for task | — | `List<SpecTaskLink>` |

### 7.5 Bulk Operations

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| POST | `/api/tasks/bulk/status` | Bulk-update task statuses | body: `BulkUpdateStatusRequest` | `BulkOperationResult` |
| POST | `/api/tasks/bulk/assign` | Bulk-assign tasks to agent | body: `BulkAssignRequest` | `BulkOperationResult` |

See [010 — Task Management](../010-task-management/spec.md) for task lifecycle and git workflow.

## 8. Sprints

> Controller: `SprintController` — route: `api/sprints`

### 8.1 Sprint CRUD

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/sprints` | List sprints | `?limit` `?offset` | paginated list |
| GET | `/api/sprints/active` | Get active sprint | — | sprint or `204` |
| GET | `/api/sprints/{id}` | Get sprint with artifacts | — | sprint object |
| POST | `/api/sprints` | Start new sprint | — | sprint object |

### 8.2 Sprint Lifecycle

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| POST | `/api/sprints/{id}/advance` | Advance to next stage | `?force` | sprint object |
| POST | `/api/sprints/{id}/complete` | Complete sprint | `?force` | sprint object |
| POST | `/api/sprints/{id}/cancel` | Cancel sprint | — | sprint object |
| POST | `/api/sprints/{id}/approve-advance` | Approve pending stage advancement | — | sprint object |
| POST | `/api/sprints/{id}/reject-advance` | Reject pending stage advancement | — | sprint object |

### 8.3 Sprint Artifacts & Metrics

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/sprints/{id}/artifacts` | Sprint artifacts | `?stage` | artifact list |
| GET | `/api/sprints/{id}/metrics` | Sprint metrics | — | metrics object |
| GET | `/api/sprints/metrics/summary` | Workspace-level metrics summary | — | summary object |

### 8.4 Sprint Schedule

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/sprints/schedule` | Get sprint schedule | — | schedule object |
| PUT | `/api/sprints/schedule` | Create/update schedule | body: schedule config | schedule object |
| DELETE | `/api/sprints/schedule` | Delete schedule | — | `200` |

See [013 — Sprint System](../013-sprint-system/spec.md) for sprint lifecycle and stage gates.

## 9. Commands

> Controllers: `CommandController`, `CommandAuditController`

### 9.1 Command Execution

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/commands/metadata` | Allowlisted command catalog for human UI | — | command metadata |
| POST | `/api/commands/execute` | Execute a command (sync or async) | body: `ExecuteCommandRequest` | execution result |
| GET | `/api/commands/{correlationId}` | Get async command status | — | command status |

### 9.2 Command Audit

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/commands/audit` | Paginated audit log | `?agentId` `?command` `?status` `?hoursBack` `?limit` `?offset` | audit entries |
| GET | `/api/commands/audit/stats` | Aggregate audit statistics | `?hoursBack` | stats by status/agent/command |

See [007 — Agent Command System](../007-agent-commands/spec.md) for command definitions and permissions.

## 10. Specs

> Controller: `CollaborationController`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/specs/version` | Spec corpus version/hash/section count | — | version info |
| GET | `/api/specs/search` | Search spec sections by keyword | `?q` **(required)** `?limit` (1–20, default 5) | `List<SpecSearchResult>` |
| GET | `/api/specs/{sectionId}/tasks` | Tasks linked to a spec section | — | `List<SpecTaskLink>` |

See [009 — Spec Management](../009-spec-management/spec.md) for the spec system.

## 11. Analytics & Usage

> Controllers: `AnalyticsController`, `SystemController`

### 11.1 Agent Analytics

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/analytics/agents` | Per-agent performance metrics | `?hoursBack` | `AgentAnalyticsSummary` |
| GET | `/api/analytics/agents/{agentId}` | Detailed analytics for one agent | `?hoursBack` `?requestLimit` `?errorLimit` `?taskLimit` | `AgentAnalyticsDetail` |
| GET | `/api/analytics/tasks` | Task-cycle analytics | `?hoursBack` | `TaskCycleAnalytics` |

### 11.2 Global Usage

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/usage` | Global LLM usage summary | `?hoursBack` | `UsageSummary` |
| GET | `/api/usage/records` | Recent global usage records | `?agentId` `?limit` | `List<LlmUsageRecord>` |

### 11.3 Global Errors

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/errors` | Global error summary | `?hoursBack` | `ErrorSummary` |
| GET | `/api/errors/records` | Recent error records | `?agentId` `?hoursBack` `?limit` | `List<ErrorRecord>` |

## 12. Memory System

> Controller: `MemoryController` — route: `api/memories`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/memories/browse` | Browse memories with filters | `?agentId` `?category` `?search` `?includeExpired` | memory list |
| GET | `/api/memories/stats` | Per-category memory stats | `?agentId` | stats object |
| GET | `/api/memories/export` | Export memories as JSON | `?agentId` `?category` | JSON download |
| POST | `/api/memories/import` | Bulk import/upsert memories | body: `MemoryImportRequest` | import result |
| DELETE | `/api/memories` | Delete one memory entry | `?agentId` **(required)** `?key` **(required)** | `200` |
| DELETE | `/api/memories/expired` | Delete expired memories | `?agentId` | deletion count |

See [008 — Agent Memory System](../008-agent-memory/spec.md) for memory categories, expiry, and injection.

## 13. Retrospectives & Digests

> Controllers: `RetrospectiveController`, `DigestController`

### 13.1 Retrospectives

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/retrospectives` | List retrospectives | `?agentId` `?taskId` `?limit` `?offset` | paginated list |
| GET | `/api/retrospectives/{commentId}` | Get one retrospective with task metadata | — | retrospective |
| GET | `/api/retrospectives/stats` | Aggregate retrospective statistics | — | stats object |

### 13.2 Digests

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/digests` | List digests | `?status` `?limit` `?offset` | paginated list |
| GET | `/api/digests/{id}` | Get digest with source retrospectives | — | digest object |
| GET | `/api/digests/stats` | Aggregate digest statistics | — | stats object |

## 14. Sessions

> Controller: `SessionController` — route: `api/sessions`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/sessions` | List conversation sessions | `?status` `?limit` `?offset` `?hoursBack` `?workspace` | `SessionListResponse` |
| GET | `/api/sessions/stats` | Session statistics | `?hoursBack` `?workspace` | `SessionStats` |

## 15. Notifications

> Controller: `NotificationController` — route: `api/notifications`

### 15.1 Provider Management

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/notifications/providers` | List providers and status | — | `List<ProviderStatusDto>` |
| GET | `/api/notifications/providers/{id}/schema` | Provider config schema | — | `ProviderConfigSchema` |
| POST | `/api/notifications/providers/{id}/configure` | Apply provider configuration | body: `Dict<string,string>` | `200` |
| POST | `/api/notifications/providers/{id}/connect` | Connect provider | — | `200` |
| POST | `/api/notifications/providers/{id}/disconnect` | Disconnect provider | — | `200` |
| POST | `/api/notifications/test` | Send test notification to all connected | — | `200` |

### 15.2 Delivery History

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/notifications/deliveries` | Query delivery history | `?channel` `?providerId` `?status` `?roomId` `?limit` `?offset` | `List<NotificationDeliveryDto>` |
| GET | `/api/notifications/deliveries/stats` | Delivery statistics | `?hours` | stats by status |

See [004 — Notification System](../004-notification-system/spec.md) for provider architecture.

## 16. Instruction Templates

> Controller: `InstructionTemplateController` — route: `api/instruction-templates`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/instruction-templates` | List all templates | — | `List<InstructionTemplateResponse>` |
| GET | `/api/instruction-templates/{id}` | Get one template | — | `InstructionTemplateResponse` |
| POST | `/api/instruction-templates` | Create template | body: `InstructionTemplateRequest` | `InstructionTemplateResponse` |
| PUT | `/api/instruction-templates/{id}` | Update template | body: `InstructionTemplateRequest` | `InstructionTemplateResponse` |
| DELETE | `/api/instruction-templates/{id}` | Delete template | — | `200` |

## 17. Data Export

> Controller: `ExportController` — route: `api/export`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/export/agents` | Export agent analytics | `?hoursBack` `?format=csv\|json` | CSV or JSON download |
| GET | `/api/export/usage` | Export LLM usage records | `?hoursBack` `?agentId` `?limit` `?format=csv\|json` | CSV or JSON download |
| GET | `/api/export/rooms/{roomId}/messages` | Export room messages | `?format=json\|md` | JSON or Markdown download |
| GET | `/api/export/dm/{agentId}/messages` | Export DM thread | `?format=json\|md` | JSON or Markdown download |

## 18. Search

> Controller: `SearchController` — route: `api/search`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/search` | Search messages and tasks | `?q` **(required)** `?scope` `?messageLimit` `?taskLimit` | `SearchResults` |

## 19. Filesystem

> Controller: `FilesystemController`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/filesystem/browse` | Browse subdirectories | `?path` `?showHidden` | directory listing |

## 20. GitHub Integration

> Controller: `GitHubController`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/github/status` | GitHub integration/auth status | — | `GitHubStatusResponse` |

## 21. Settings

> Controller: `SettingsController` — route: `api/settings`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/settings` | All settings with defaults | — | `Dict<string,string>` |
| GET | `/api/settings/{key}` | One setting value | — | `SettingResponse` |
| PUT | `/api/settings` | Bulk upsert settings | body: settings map | `200` |

## 22. Worktrees

> Controller: `WorktreeController` — route: `api/worktrees`

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/worktrees` | List active worktrees with git/task info | — | `List<WorktreeStatusSnapshot>` |

## 23. Activity & Real-Time

> Controller: `ActivityController` — route: `api/activity`
> Hub: `ActivityHub` — path: `/hubs/activity`

### 23.1 REST + SSE

| Method | Route | Description | Params | Returns |
|--------|-------|-------------|--------|---------|
| GET | `/api/activity/recent` | Recent activity events | — | `List<ActivityEvent>` |
| GET | `/api/activity/stream` | **SSE** — stream activity events | — | `text/event-stream` |

> **SSE stream**: Replays recent events on connect, then pushes new `ActivityEvent` payloads as they occur.

### 23.2 SignalR Hub

**Endpoint**: `ws://localhost:{port}/hubs/activity`

**Server → Client Events**:

| Event | Payload | Description |
|-------|---------|-------------|
| `activityEvent` | `ActivityEvent` | All activity events (messages, task changes, sprint transitions, errors, etc.) |

The hub is thin — broadcasting is handled by `ActivityHubBroadcaster` which wraps `IHubContext<ActivityHub>`. All server-side services emit `ActivityEvent` objects that are pushed to all connected clients.

**Client → Server**: No client-to-server methods. The hub is broadcast-only.

---

## Endpoint Count by Domain

| Domain | Endpoints |
|--------|-----------|
| System & Health | 9 |
| Authentication | 3 |
| Workspace | 5 |
| Rooms (CRUD, membership, messages, operations, plan, analytics, sessions) | 22 |
| Direct Messages | 5 |
| Agents (state, knowledge, quota, config) | 15 |
| Tasks (CRUD, lifecycle, dependencies, metadata, bulk) | 13 |
| Sprints (CRUD, lifecycle, artifacts, metrics, schedule) | 15 |
| Commands (execution, audit) | 5 |
| Specs | 2 |
| Analytics & Usage | 7 |
| Memory System | 6 |
| Retrospectives & Digests | 6 |
| Sessions | 2 |
| Notifications | 8 |
| Instruction Templates | 5 |
| Data Export | 4 |
| Search | 1 |
| Filesystem | 1 |
| GitHub | 1 |
| Settings | 3 |
| Worktrees | 1 |
| Activity & Real-Time | 2 + SignalR |
| **Total** | **141 REST + 3 SSE + 1 SignalR hub** |

## Known Gaps

1. **Request/response schemas** — Type names are listed but full property definitions are not included. See domain-specific specs for detailed contracts.
2. **Rate limiting** — The server supports configurable rate limiting via settings, but rate limit headers and behavior are not documented here.
3. **Pagination consistency** — Most list endpoints use `limit`/`offset`, but some (room messages) use cursor-based `after` parameter.
4. **Room artifacts and evaluations** — Endpoints exist but return empty arrays (placeholder).

## Revision History

| Date | Change |
|------|--------|
| 2026-04-14 | Initial catalog — 145 endpoints across 26 controllers |
