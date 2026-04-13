# 012 — Consultant API

## Purpose

Enable an external CLI agent (e.g., Copilot CLI / Anvil) to communicate with Agent Academy agents via the server API. The consultant acts as a senior engineer who can advise, diagnose issues, and make code changes on behalf of agents.

## Vision

The goal of Agent Academy is to build the best possible multi-agent collaboration environment — one where a team of agents working together becomes more efficient than any single agent operating alone. The consultant's role is to accelerate this by:

- **Unblocking agents** when they're stuck on code issues, broken infrastructure, or unclear requirements
- **Improving the system** by adding commands, fixing bugs (e.g., broken breakout rooms), and tuning agent definitions based on direct observation of how agents struggle
- **Closing the feedback loop** — agents can articulate what they need, and the consultant can deliver it without a human middleman

## Status: Phase 1 & 2 Complete

## Problem Statement

The human operator currently acts as a middleman between the CLI agent (which has deep codebase expertise and can edit code) and the Agent Academy agents (which collaborate on tasks but need guidance). The consultant API removes this bottleneck by letting the CLI agent participate directly in main room conversations.

## Design Decisions

### Authentication: Shared Secret Header

- **Mechanism**: `X-Consultant-Key` header containing a pre-shared secret
- **Configuration**: `ConsultantApi:SharedSecret` in `appsettings.json` or `CONSULTANT_API__SHARED_SECRET` environment variable
- **Identity**: Authenticated as a `Consultant` principal with `SenderKind = User`, `SenderId = "consultant"`, `SenderName = "Consultant"`
- **Scope**: Grants access to the same endpoints as an authenticated human user
- **Migration path**: The auth handler is a standard ASP.NET Core authentication scheme. When a real OAuth2 provider (e.g., Entra ID) is needed, add it as another scheme alongside this one. No endpoint changes required.

**Why not OAuth2 now**: YAGNI. The system runs locally. A shared secret is simple, secure enough for localhost, and the authentication scheme abstraction provides the migration path for free.

### Interaction Model: Main Room Group Conversation

The consultant posts messages to the main room and reads responses from all agents, rather than DMing agents individually. This is because:

1. Agents have varying reliability — hearing from multiple agents provides signal averaging
2. The orchestrator already handles turn-taking and multi-agent coordination
3. It mirrors how the human operator interacts with agents

### Message Polling

After sending a message, the consultant polls for new responses. The design accounts for:

- **Multiple responders**: Several agents may respond to a single message at different times
- **Variable latency**: Some agents respond quickly, others take time (especially if running Copilot SDK calls)
- **Round completion**: The orchestrator runs up to `MaxRoundsPerTrigger` conversation rounds per human message trigger

## API Surface

### Authentication

All endpoints below require either:
- Cookie auth (existing GitHub OAuth flow), OR
- `X-Consultant-Key` header matching the configured shared secret

If `ConsultantApi:SharedSecret` is not configured, the consultant auth scheme is not registered.

### Endpoints

#### Send Message to Room

```
POST /api/rooms/{roomId}/human
Content-Type: application/json
X-Consultant-Key: {secret}

{
  "content": "string"
}
```

**Existing endpoint** — no changes needed. Posts a human message and triggers orchestrator rounds.

Returns: `ChatEnvelope` with the posted message.

#### Get Room Messages (NEW)

```
GET /api/rooms/{roomId}/messages?after={messageId}&limit={n}
X-Consultant-Key: {secret}
```

Returns messages in the room, optionally filtered:
- `after` (optional): Only return messages sent after this message ID (cursor-based pagination)
- `limit` (optional, default 50, max 200): Maximum messages to return

Returns:
```json
{
  "messages": [ChatEnvelope],
  "hasMore": false
}
```

Messages are ordered chronologically (oldest first). This endpoint includes sessionless messages and messages with `SenderKind = User` from any session, so consultant/human messages are always visible regardless of session boundaries.

#### Stream Room Messages (SSE)

```
GET /api/rooms/{roomId}/messages/stream?after={messageId}
X-Consultant-Key: {secret}
```

Server-Sent Events stream delivering room messages in real-time. Replaces polling for the primary consultant workflow.

**Query parameters:**
- `after` (optional): Cursor — only replay messages after this ID, then stream live

**SSE event format:**
```
id: {messageId}
event: message
data: {full ChatEnvelope JSON}

```

**Overflow handling:** If the client falls behind (internal buffer full), the server emits a `resync` event with the last successfully sent message ID, then closes the connection. Client should reconnect with `after={lastId}`.

```
event: resync
data: {"lastId": "{messageId}"}
```

**Delivery semantics:** At-least-once. Messages that arrive during the replay window may be delivered both in the replay batch and as a live event. Clients should deduplicate by message ID.

**Coverage:** Streams messages posted via `MessageService` methods (agent messages, human messages, system messages, system status messages, DM room notifications). Messages inserted directly by other services (e.g., task lifecycle review messages) are not streamed but are available via the REST endpoint on reconnect.

**Implementation:**
- `MessageBroadcaster` (singleton, `src/AgentAcademy.Server/Services/MessageBroadcaster.cs`): Per-room pub/sub for live message delivery. No buffer — the SSE endpoint handles replay from DB.
- SSE endpoint on `RoomController` (`src/AgentAcademy.Server/Controllers/RoomController.cs`): Subscribe-first pattern (avoids race window between DB replay and subscription).
- `MessageService` broadcasts to `MessageBroadcaster` after `SaveChangesAsync` in all message-posting methods.

#### List Rooms

```
GET /api/rooms
X-Consultant-Key: {secret}
```

**Existing endpoint** — no changes needed. Returns all non-archived rooms with recent messages. Use to discover the main room ID. Pass `includeArchived=true` to also return archived rooms.

#### Get Room Details

```
GET /api/rooms/{roomId}
X-Consultant-Key: {secret}
```

**Existing endpoint** — no changes needed. Returns room snapshot with participants and recent messages.

### DM Endpoints (Secondary)

These existing endpoints also work with consultant auth, for targeted agent communication:

```
POST /api/dm/threads/{agentId}    # Send DM to specific agent
GET  /api/dm/threads/{agentId}    # Read DM thread
GET  /api/dm/threads              # List DM threads
```

#### Stream DM Thread Messages (SSE)

```
GET /api/dm/threads/{agentId}/stream?after={messageId}
```

Server-Sent Events stream delivering DM messages for a specific agent thread in real-time.

**Parameters:**
- `agentId` (path, required): Agent ID for the DM thread
- `after` (query, optional): Cursor — only replay messages after this ID, then stream live

**SSE event format:**
```
id: {messageId}
event: message
data: {"id":"...","senderId":"...","senderName":"...","senderRole":"...","content":"...","sentAt":"...","isFromHuman":true}
```

**Overflow/resync:** If the client falls behind, emits `event: resync` with `{"lastId":"..."}` and closes the connection. Client should reconnect with `?after={lastId}`.

**Implementation:**
- Reuses `MessageBroadcaster` with DM-specific subscriptions (`SubscribeDm`/`BroadcastDm`), keyed by agent ID (case-insensitive).
- Subscribe-first pattern (same as room SSE) avoids race conditions between DB replay and live subscription.
- `SendDirectMessageAsync` broadcasts `DmMessage` after commit, covering both human→agent and agent→human directions.
- Returns 404 if agent ID is not found in catalog.

### Command Execution

The consultant can execute commands through the human command API:

```
POST /api/commands/execute
Content-Type: application/json
X-Consultant-Key: {secret}

{
  "command": "COMMAND_NAME",
  "args": { "key": "value" }
}
```

Returns: `ExecuteCommandResponse` with status, result, and correlationId.

**Command catalog**: `GET /api/commands/metadata` returns the allowlisted and implemented commands with field schemas for dynamic UI rendering. Commands not in the allowlist or not backed by an implemented handler are filtered out.

**Allowlisted commands** (subset of agent commands safe for human/consultant use):

| Category | Commands |
|----------|----------|
| Code | `READ_FILE`, `SEARCH_CODE` |
| Workspace | `LIST_ROOMS`, `LIST_AGENTS`, `LIST_TASKS`, `LIST_COMMANDS`, `SHOW_REVIEW_QUEUE`, `ROOM_HISTORY`, `ROOM_TOPIC`, `CREATE_ROOM`, `REOPEN_ROOM`, `CLOSE_ROOM`, `CLEANUP_ROOMS`, `INVITE_TO_ROOM`, `SHOW_UNLINKED_CHANGES`, `LINK_TASK_TO_SPEC` |
| Task mgmt | `UPDATE_TASK`, `CANCEL_TASK`, `APPROVE_TASK` |
| Git | `SHOW_DIFF`, `GIT_LOG`, `CREATE_PR`, `POST_PR_REVIEW`, `GET_PR_REVIEWS`, `MERGE_PR` |
| Operations | `RUN_BUILD`, `RUN_TESTS` |
| Memory | `EXPORT_MEMORIES` |

Async commands (`RUN_BUILD`, `RUN_TESTS`, `CREATE_PR`, `MERGE_PR`) return `status: "pending"` with a `correlationId`. Poll `GET /api/commands/{correlationId}` for completion.

**Identity**: Commands execute with `agentId = "human"`, `agentRole = "Human"`. Role gates on handlers respect this (e.g., `APPROVE_TASK` accepts Human role).

### Analytics

```
GET /api/analytics/agents?hoursBack={N}
X-Consultant-Key: {secret}
```

Per-agent performance metrics aggregated over a time window. Returns `AgentAnalyticsSummary` with per-agent LLM usage (requests, tokens, cost, avg response time), errors (total, recoverable, unrecoverable), tasks (assigned, completed), and a 12-bucket token trend.

- `hoursBack` (optional, 1–8760): Time window. Omit for all-time data.
- Token trend is capped at 30 days max regardless of `hoursBack` to avoid unbounded materialization.
- Agents sorted by total requests descending.

Response:
```json
{
  "agents": [{
    "agentId": "string",
    "agentName": "string",
    "totalRequests": 0,
    "totalInputTokens": 0,
    "totalOutputTokens": 0,
    "totalCost": 0.0,
    "averageResponseTimeMs": null,
    "totalErrors": 0,
    "recoverableErrors": 0,
    "unrecoverableErrors": 0,
    "tasksAssigned": 0,
    "tasksCompleted": 0,
    "tokenTrend": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
  }],
  "windowStart": "ISO8601",
  "windowEnd": "ISO8601",
  "totalRequests": 0,
  "totalCost": 0.0,
  "totalErrors": 0
}
```

#### Agent Detail

```
GET /api/analytics/agents/{agentId}?hoursBack={N}&requestLimit=50&errorLimit=20&taskLimit=50
X-Consultant-Key: {secret}
```

Detailed analytics for a single agent: recent LLM requests, errors, tasks, model breakdown, and 24-bucket activity trend.

- `hoursBack` (optional, 1–8760): Time window. Omit for all-time.
- `requestLimit` (optional, 1–200, default 50): Max recent requests returned.
- `errorLimit` (optional, 1–200, default 20): Max recent errors returned.
- `taskLimit` (optional, 1–200, default 50): Max tasks returned.
- Tasks are "active in window" — included if created OR completed within the time range.
- Non-catalog agents (from telemetry) are accepted — agentId used as name fallback.
- Always returns 200 with zeroed metrics if agent has no data.

Response:
```json
{
  "agent": { /* AgentPerformanceMetrics */ },
  "windowStart": "ISO8601",
  "windowEnd": "ISO8601",
  "recentRequests": [{
    "id": "string", "roomId": "string|null", "model": "string|null",
    "inputTokens": 0, "outputTokens": 0, "cost": 0.0,
    "durationMs": 0.0, "reasoningEffort": "string|null", "recordedAt": "ISO8601"
  }],
  "recentErrors": [{
    "id": "string", "roomId": "string|null", "errorType": "string",
    "message": "string", "recoverable": true, "retried": false, "occurredAt": "ISO8601"
  }],
  "tasks": [{
    "id": "string", "title": "string", "status": "string",
    "roomId": "string|null", "branchName": "string|null",
    "pullRequestUrl": "string|null", "pullRequestNumber": null,
    "createdAt": "ISO8601", "completedAt": "ISO8601|null"
  }],
  "modelBreakdown": [{
    "model": "string", "requests": 0, "totalTokens": 0, "totalCost": 0.0
  }],
  "activityBuckets": [{
    "bucketStart": "ISO8601", "bucketEnd": "ISO8601", "requests": 0, "tokens": 0
  }]
}
```

#### Analytics Export

> **Source**: `src/AgentAcademy.Server/Controllers/ExportController.cs`, `src/AgentAcademy.Server/Services/CsvExportService.cs`

Downloadable analytics data in CSV or JSON format.

```
GET /api/export/agents?hoursBack={N}&format=csv|json
GET /api/export/usage?hoursBack={N}&agentId={id}&limit=10000&format=csv|json
```

**Agent summary export** returns one row per agent with flat metrics (no token trend). **Usage records export** returns individual LLM API call records with optional agent and time filters.

- `format` (optional, default `csv`): `csv` or `json`. CSV uses RFC 4180 with CRLF line endings, InvariantCulture formatting, ISO 8601 timestamps, and formula injection protection (cells starting with `=`, `+`, `-`, `@` are prefixed with `'`).
- `limit` (optional, 1–50000, default 10000): Max records for usage export. Server fetches `limit+1` to detect truncation accurately.
- Response headers: `X-Record-Count` (actual count returned), `X-Truncated: true` (only when more records exist beyond limit).
- Content-Disposition: `attachment; filename="agent-analytics-{timestamp}.{csv|json}"` (agents) or `"usage-records[-{agentId}]-{timestamp}.{csv|json}"` (usage). Triggers browser download.

Frontend: Export CSV button on `AgentAnalyticsPanel` toolbar. Uses `downloadFile()` helper in `api.ts` which reads blob from fetch response and triggers download via temporary anchor element.

> **Tests**: `CsvExportTests` (20 tests — formatting, escaping, formula injection, edge cases), `ExportControllerTests` (14 tests — validation, content types, truncation, time filtering, integration with real DB).

#### Conversation Export

> **Source**: `src/AgentAcademy.Server/Controllers/ExportController.cs`, `src/AgentAcademy.Server/Services/ConversationExportService.cs`

Downloadable room and DM conversation history in JSON or Markdown format.

```
GET /api/export/rooms/{roomId}/messages?format=json|markdown
GET /api/export/dm/{agentId}/messages?format=json|markdown
```

**Room export** fetches all non-DM messages in a room (`RecipientId == null`), ordered by `SentAt` then `Id`. **DM export** fetches all messages between the human/consultant and a specific agent — both directions — using the `HumanSideSenderIds` array (`["human", "consultant"]`) to match the human side.

- `format` (optional, default `json`): `json` or `markdown`.
- Message cap: 10,000 per export. Server fetches `MaxExportMessages + 1` to detect truncation.
- Response headers: `X-Record-Count` (actual count returned), `X-Truncated: true` (when more messages exist beyond cap).
- Content-Disposition: `attachment; filename="room-{sanitized-name}-{timestamp}.{json|md}"` (rooms) or `"dm-{sanitized-agentId}-{timestamp}.{json|md}"` (DMs). Triggers browser download.
- Filename sanitization: only `[a-zA-Z0-9_-]` characters preserved, lowercased.
- Returns `404` with `{ code, message }` if the room doesn't exist (rooms) or no DM thread exists for the agent (DMs — checked by zero messages).

**JSON format** (`ConversationExportService.FormatAsJson`): Envelope object with `exportedAt` (UTC), `roomName` or `agentId`, `messageCount`, and `messages` array. Each message includes: `id`, `senderId`, `senderName`, `senderRole`, `senderKind`, `kind`, `content`, `sentAt`, `replyToMessageId`. Serialized with `WriteIndented = true`, `camelCase` naming.

**Markdown format** (`ConversationExportService.FormatAsMarkdown`): Human-readable document with heading (`# Room: {name}` or `# DM Thread: {agentId}`), export timestamp, message count, date range, then each message as `**{SenderName}** ({Role}) — {timestamp}` followed by content. Messages separated by horizontal rules.

Frontend: Export dropdown in `SessionToolbar` (room chat view) — button with `▾` that reveals JSON/Markdown options on click. DM export via `<select>` element in `DmPanel` thread header. Both use `downloadFile()` helper from `api/core.ts` which reads blob from fetch response and triggers download via temporary anchor element. API client functions: `exportRoomMessages(roomId, format)` in `api/rooms.ts`, `exportDmMessages(agentId, format)` in `api/system.ts`.

> **Tests**: `ConversationExportServiceTests` (21 tests — room/DM fetching, JSON/Markdown formatting, truncation, empty threads, null room), `ExportControllerTests` (extended with conversation export endpoint tests — format validation, 404 handling, content types, truncation headers).

#### Task Cycle Analytics

> **Source**: `src/AgentAcademy.Server/Services/TaskAnalyticsService.cs`, `src/AgentAcademy.Server/Controllers/AnalyticsController.cs`

Task effectiveness metrics computed from `TaskEntity` lifecycle data.

```
GET /api/analytics/tasks?hoursBack={N}
```

Returns `TaskCycleAnalytics`:
- **Overview**: Total tasks, status counts, completion rate, average cycle time (created→completed), average queue time (created→started), average execution span (started→completed), average review rounds, rework rate (tasks needing >1 review round), total commits.
- **AgentEffectiveness[]**: Per-agent metrics — assigned, completed, cancelled, completion rate, cycle/queue/execution times, review rounds, commits per task, first-pass approval rate, rework rate. Attribution based on current assignee (not full reassignment history).
- **ThroughputBuckets[]**: 12 time-series buckets with completed/created counts for sparkline visualization.
- **TypeBreakdown**: Counts by task type (Feature, Bug, Chore, Spike).

Completion rate uses a union cohort (created-in-window ∪ completed-in-window) as the denominator to prevent >100% when tasks are created before the window but completed inside it. Database indexes on `CreatedAt` and `CompletedAt` support efficient time-window queries.

Frontend: `TaskAnalyticsPanel` in Dashboard — summary KPIs (completion rate, avg cycle, avg queue, avg reviews, rework rate, commits), status badges, throughput sparkline, type breakdown chips, sortable agent effectiveness table. Auto-refreshes every 60s.

> **Tests**: `TaskAnalyticsTests` (19 tests — empty state, status counts, metrics computation, time windowing, rate capping, per-agent breakdown, type breakdown, throughput buckets, controller validation). Frontend: `taskAnalyticsPanel.dom.test.tsx` (14 tests — loading, error, empty, KPIs, badges, sparkline, table, sort, refresh, auto-refresh).

## Implementation Plan

### Phase 1: Auth Handler

**Files:**
- `src/AgentAcademy.Server/Auth/ConsultantKeyAuthHandler.cs` (NEW)
- `src/AgentAcademy.Server/Program.cs` (MODIFY — register scheme)
- `src/AgentAcademy.Server/appsettings.json` (MODIFY — add config section)

Create a `ConsultantKeyAuthenticationHandler` that:
1. Reads the `X-Consultant-Key` header
2. Compares against the configured secret (constant-time comparison)
3. On match, creates a `ClaimsPrincipal` with claims: `Name = "Consultant"`, `NameIdentifier = "consultant"`, `Role = "Consultant"`
4. Register as authentication scheme `"ConsultantKey"`

Wire into Program.cs:
- Register the scheme alongside existing cookie auth
- Use a `PolicyScheme` or configure the default to try both schemes
- The FallbackPolicy's `RequireAuthenticatedUser()` will accept either

### Phase 2: Messages Endpoint

**Files:**
- `src/AgentAcademy.Server/Controllers/RoomController.cs` (MODIFY — add GET messages action)
- `src/AgentAcademy.Server/Services/RoomService.cs` (already has GetRoomMessagesAsync with cursor)

`RoomService.GetRoomMessagesAsync(string roomId, string? afterMessageId, int limit)` returns paginated messages:
```csharp
public async Task<(List<ChatEnvelope> Messages, bool HasMore)> GetRoomMessagesAsync(
    string roomId, string? afterMessageId = null, int limit = 50)
```

Query logic:
- Filter by room, non-DM (`RecipientId == null`), active session
- If `afterMessageId` provided, find that message's `SentAt` and filter `> SentAt` (with tiebreaker on ID)
- Order by `SentAt` ascending
- Take `limit + 1` to determine `HasMore`

### Phase 3: Tests

**Files:**
- `tests/AgentAcademy.Server.Tests/ConsultantKeyAuthTests.cs` (NEW)
- `tests/AgentAcademy.Server.Tests/RoomMessagesEndpointTests.cs` (NEW)

Test cases:
- Auth: valid key → 200, invalid key → 401, missing key + no cookie → 401, missing config → scheme not registered
- Messages: returns messages after cursor, respects limit, empty room, chronological order

## Polling Strategy (for CLI consumer)

**Preferred: SSE streaming** — connect to `GET /api/rooms/{roomId}/messages/stream` for real-time delivery. See "Stream Room Messages (SSE)" above.

**Fallback: polling** — use when SSE is unavailable (proxy limitations, etc.):

```
1. GET /api/rooms → find main room ID
2. POST /api/rooms/{roomId}/human → send message, note returned message ID
3. Wait 5 seconds (let orchestrator trigger agents)
4. Loop:
   a. GET /api/rooms/{roomId}/messages?after={lastSeenId}
   b. If new messages: update lastSeenId, reset idle timer
   c. If no new messages and idle > 30 seconds: round is likely complete
   d. If idle > 60 seconds: definitely done, exit loop
5. Process all collected responses
```

This handles:
- Fast agents (responses within seconds)
- Slow agents (responses after 10-20 seconds)
- Multi-round conversations (orchestrator chains rounds)
- Agent silence (no response = pass, handled by timeout)

## Rate Limiting

> **Source**: `src/AgentAcademy.Server/Auth/ConsultantRateLimitExtensions.cs`, `src/AgentAcademy.Server/Auth/ConsultantRateLimitSettings.cs`

Prevents the consultant from overwhelming agents with rapid-fire requests. Uses ASP.NET Core's built-in `AddRateLimiter()` with a global `PartitionedRateLimiter`.

### Behavior

- **Only consultant requests are rate-limited.** Regular users (cookie auth) and anonymous requests pass through with no limit. The partitioner checks `User.IsInRole("Consultant")`.
- **Separate buckets for reads and writes.** Exhausting the write limit does not affect read requests, and vice versa.
- **Sliding window algorithm** with configurable segments for smooth rate enforcement.
- **429 response** on rejection with `Retry-After` header (seconds) and JSON body:

```json
{
  "type": "https://tools.ietf.org/html/rfc6585#section-4",
  "title": "Rate limit exceeded",
  "status": 429,
  "detail": "Too many requests. Try again in {N} seconds."
}
```

### Configuration

`ConsultantApi:RateLimiting` section in `appsettings.json`:

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable/disable rate limiting (only applies when consultant auth is configured) |
| `WritePermitLimit` | `20` | Max POST/PUT/DELETE/PATCH requests per window |
| `ReadPermitLimit` | `60` | Max GET/HEAD/OPTIONS requests per window |
| `WindowSeconds` | `60` | Sliding window duration |
| `SegmentsPerWindow` | `6` | Segments for sliding window granularity |

### Middleware Ordering

Rate limiting is placed between authentication and authorization:

```
UseAuthentication() → UseRateLimiter() → UseAuthorization()
```

This ensures user claims are available for the role check, and rate-limited requests are rejected before authorization evaluation.

> **Tests**: `ConsultantRateLimitTests` (15 tests — defaults, config binding, write/read limits, separate buckets, method classification, non-consultant passthrough, anonymous passthrough, rejection behavior).

## Risk Assessment

| Component | Risk | Rationale |
|-----------|------|-----------|
| Auth handler | 🔴 | New authentication mechanism |
| Messages endpoint | 🟡 | New read-only query on existing data |
| Program.cs auth config | 🔴 | Modifying auth pipeline |
| appsettings.json | 🟢 | Additive config |
| Tests | 🟢 | New test files |

## Future Considerations (Not in scope)

- **OAuth2 provider**: Replace shared secret with Entra ID or similar. The auth scheme abstraction makes this a swap.
- ~~**WebSocket/SSE streaming**: Replace polling with server-sent events for real-time responses. Would require a new hub method or SSE endpoint.~~ — **Resolved**: `GET /api/rooms/{roomId}/messages/stream` SSE endpoint streams room messages in real-time. `MessageBroadcaster` (singleton) provides per-room pub/sub. Subscribe-first pattern avoids race conditions. Overflow detection emits `resync` event and closes connection. At-least-once delivery with client-side dedup by message ID. Covers all `MessageService` message-posting methods. 18 tests (13 MessageBroadcaster + 5 MessageService broadcast integration).
- ~~**Consultant identity in UI**: Show consultant messages differently from human messages in the frontend.~~ — **Resolved**: Consultant messages now carry `SenderRole = "Consultant"` (derived from `User.IsInRole("Consultant")` claim). Frontend renders a copper-colored "Consultant" role pill in ChatPanel and SearchPanel. DmPanel shows consultant label + distinct bubble styling. DM service includes consultant messages in the human inbox via `HumanSideSenderIds` array. `DmMessage` model includes `SenderRole` field.
- **Rate limiting**: Prevent the consultant from overwhelming agents with rapid-fire messages. **Resolved**: ASP.NET Core built-in rate limiting middleware with `PartitionedRateLimiter`. Separate sliding window buckets for write operations (POST/PUT/DELETE/PATCH: 20/min default) and read operations (GET/HEAD/OPTIONS: 60/min default). Only applies to consultant-authenticated requests — regular users and cookie-auth pass through unthrottled. Returns 429 with `Retry-After` header and ProblemDetails-style JSON body. Configurable via `ConsultantApi:RateLimiting` section (`Enabled`, `WritePermitLimit`, `ReadPermitLimit`, `WindowSeconds`, `SegmentsPerWindow`). Middleware placed between `UseAuthentication()` and `UseAuthorization()` so user claims are available for role check. 15 tests.
- ~~**Analytics export**: CSV/JSON download endpoints for agent performance and LLM usage records.~~ — **Resolved** (implemented previously).
