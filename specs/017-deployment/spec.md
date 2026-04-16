# 017 — Deployment & Operations

## Purpose

Document how to build, configure, deploy, and operate Agent Academy — from local development through to production. This spec is the authoritative reference for deployment topology, configuration surface, operational procedures, and troubleshooting.

## Scope

Covers the full-stack application: ASP.NET Core 8 backend, React 19 frontend, SQLite database, git worktree management, and CI/CD pipelines. Does **not** cover the internal workings of the GitHub Copilot SDK or GitHub's OAuth infrastructure — only how Agent Academy configures and depends on them.

---

## 1. System Requirements

### Runtime Dependencies

| Component | Version | Purpose |
|-----------|---------|---------|
| .NET SDK | 8.0.x | Build and run the backend |
| Node.js | 20.x | Build the frontend |
| npm | 10.x+ | Frontend dependency management |
| Git | 2.30+ | Worktree management, version control |
| SQLite | 3.35+ | Embedded database (ships with .NET) |

### Optional Dependencies

| Component | Purpose |
|-----------|---------|
| GitHub Copilot CLI (`copilot`) | Agent execution via Copilot SDK |
| GitHub CLI (`gh`) | GitHub API operations |
| Discord bot token | Notification delivery |

### Hardware

Agent Academy is designed as a single-machine deployment. Resource requirements scale with the number of concurrent agents:

- **Minimum**: 2 CPU cores, 4 GB RAM, 10 GB disk
- **Recommended** (6 agents): 4 CPU cores, 8 GB RAM, 20 GB disk
- **Disk note**: Each agent worktree is a shallow clone (~50–200 MB depending on repo size). The `.worktrees/` directory grows with active tasks.

---

## 2. Architecture Overview

```
┌──────────────────────────────────────────────────┐
│              Single Machine                       │
│                                                   │
│  ┌───────────────────────────────────────────┐   │
│  │  ASP.NET Core 8 (Kestrel)                 │   │
│  │  ├── REST API (Controllers)               │   │
│  │  ├── SignalR Hub (real-time)               │   │
│  │  ├── SSE Streams (agent activity)         │   │
│  │  └── Static files (Vite build output)     │   │
│  └───────────────┬───────────────────────────┘   │
│                  │                                │
│  ┌───────────────┼───────────────────────────┐   │
│  │  SQLite DB    │   .worktrees/             │   │
│  │  (agent-      │   ├── task-T-1/           │   │
│  │   academy.db) │   ├── task-T-2/           │   │
│  │               │   └── ...                 │   │
│  └───────────────┴───────────────────────────┘   │
│                                                   │
│  ┌───────────────────────────────────────────┐   │
│  │  Agent Processes (Copilot CLI)             │   │
│  │  ├── Agent 1 (worktree: task-T-1/)        │   │
│  │  ├── Agent 2 (worktree: task-T-2/)        │   │
│  │  └── ...                                  │   │
│  └───────────────────────────────────────────┘   │
└──────────────────────────────────────────────────┘
```

**Key design choice**: SQLite + single-process means no distributed coordination, no connection pooling, no replication lag. The tradeoff is vertical scaling only — if you need multi-machine, you'd need to replace SQLite with PostgreSQL and add a message bus. That's not on the roadmap.

---

## 3. Configuration

### Configuration Sources (precedence, highest first)

1. **Environment variables** — override everything (production secrets)
2. **`appsettings.{Environment}.json`** — environment-specific overrides
3. **`appsettings.json`** — defaults

ASP.NET Core's standard configuration binding applies. Environment variables use `__` (double underscore) as the section separator: `ConsultantApi__SharedSecret` maps to `ConsultantApi:SharedSecret`.

### Configuration Reference

#### Core Settings (`appsettings.json`)

```jsonc
{
  "ConnectionStrings": {
    // Path to SQLite database file. Relative paths resolve from the working directory.
    "DefaultConnection": "Data Source=agent-academy.db"
  },
  "GitHub": {
    "AppId": "",           // GitHub App ID (required for OAuth)
    "ClientId": "",        // GitHub OAuth Client ID
    "ClientSecret": "",    // GitHub OAuth Client Secret (🔐 use env var in production)
    "CallbackPath": "/api/auth/callback",
    "FrontendUrl": "http://localhost:5173"   // Redirect target after OAuth
  },
  "Copilot": {
    "CliPath": "copilot"   // Path to the Copilot CLI executable
  },
  "ConsultantApi": {
    "SharedSecret": "",    // Pre-shared key for Consultant API auth (🔐 use env var)
    "RateLimiting": {
      "Enabled": true,
      "WritePermitLimit": 20,    // Write operations per window
      "ReadPermitLimit": 60,     // Read operations per window
      "WindowSeconds": 60,       // Sliding window duration
      "SegmentsPerWindow": 6     // Fixed-window segments within the sliding window
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning",
      "Microsoft.EntityFrameworkCore.Infrastructure": "Warning"
    }
  }
}
```

#### Secret Configuration

| Secret | Config Key | Env Var | Purpose |
|--------|-----------|---------|---------|
| GitHub Client Secret | `GitHub:ClientSecret` | `GitHub__ClientSecret` | OAuth authentication |
| Consultant API Key | `ConsultantApi:SharedSecret` | `ConsultantApi__SharedSecret` | Headless API access |
| Discord Bot Token | Stored in DB (encrypted) | N/A | Notification delivery |

**Encryption at rest**: Discord bot tokens and other notification provider secrets are encrypted via `ConfigEncryptionService`, which wraps ASP.NET Core Data Protection API. Keys are persisted to `~/.local/share/AgentAcademy/DataProtection-Keys/`.

> ⚠️ **Data Protection key management**: If you move the application to a different machine, you must also migrate the Data Protection key ring. Without it, encrypted notification secrets become unreadable. See [Troubleshooting § Encrypted secrets](#8-troubleshooting) for recovery steps.

#### Notification Provider Configuration

Notification providers (Discord, future Slack) are configured at runtime through the Settings panel or REST API (`POST /api/notifications/providers/{providerId}/config`). Secrets are encrypted before storage. See [004 — Notification System](../004-notification-system/spec.md) for the full configuration schema.

---

## 4. Local Development

### First-Time Setup

```bash
# 1. Clone and enter the repo
git clone <repo-url> agent-academy
cd agent-academy

# 2. Configure git hooks (conventional commit enforcement, push protection)
git config core.hooksPath .githooks

# 3. Build the backend
dotnet restore
dotnet build

# 4. Build the frontend
cd src/agent-academy-client
npm ci
npm run build
cd ../..

# 5. Run database migrations (automatic on first startup)
# The server runs EF Core migrations on boot — no manual step needed.

# 6. Start the server
dotnet run --project src/AgentAcademy.Server
# Listens on http://localhost:5066 (configured in launchSettings.json)

# 7. Start the frontend dev server (separate terminal, for hot-reload)
cd src/agent-academy-client
npm run dev
# Listens on http://localhost:5173, proxies API calls to :5066
```

### Development Environment Variables

```bash
# Required for Consultant API access (operator/automation)
export ConsultantApi__SharedSecret="your-secret-here"

# Optional: override the default port
dotnet run --project src/AgentAcademy.Server --urls "http://localhost:5066"

# Optional: set environment
export ASPNETCORE_ENVIRONMENT=Development
```

### Git Hooks

The `.githooks/` directory contains:
- **`commit-msg`**: Enforces conventional commit format (`feat:`, `fix:`, `docs:`, etc.)
- **`pre-push`**: Blocks direct pushes to `main` or `master`

After cloning, run `git config core.hooksPath .githooks` to activate them.

---

## 5. Build & Test

### Backend

```bash
# Build
dotnet build AgentAcademy.sln

# Test (xUnit, in-memory SQLite)
dotnet test AgentAcademy.sln

# Build + test in one command
dotnet build AgentAcademy.sln && dotnet test AgentAcademy.sln
```

### Frontend

```bash
cd src/agent-academy-client

# Build (Vite production bundle)
npm run build

# Type check (catches errors the bundler misses)
npx tsc --noEmit

# Test (Vitest)
npm test

# Full verification
npm run build && npx tsc --noEmit && npm test
```

### Solution Structure

```
AgentAcademy.sln
├── src/AgentAcademy.Server/          # ASP.NET Core Web API
├── src/AgentAcademy.Shared/          # Shared models (referenced by Server)
├── src/agent-academy-client/         # React 19 + Vite frontend
└── tests/AgentAcademy.Server.Tests/  # xUnit backend tests
```

---

## 6. CI/CD Pipeline

### Continuous Integration (`ci.yml`)

Triggered on pushes to `develop` and `main`, and on pull requests targeting either.

| Job | Trigger | Steps |
|-----|---------|-------|
| `spec-drift` | PR only | Runs `scripts/check-spec-drift.sh` — detects code changes without corresponding spec updates |
| `commit-lint` | PR only | Validates all PR commits follow conventional commit format |
| `build-and-test` | All | .NET restore → build → test → npm ci → build → test → tsc --noEmit |

### Version Bump (`version-bump.yml`)

Triggered on push to `main` only (i.e., after a PR merge).

1. **Determine bump type** from conventional commits since last tag:
   - `BREAKING CHANGE` or `feat!:` → major
   - `feat:` → minor
   - Everything else → patch
2. **Update version** in `Directory.Build.props` and `package.json`
3. **Generate changelog** via `scripts/generate-changelog.sh`
4. **Commit and tag** `v{major}.{minor}.{patch}`

### Merge Workflow

```
feature branch ──PR──→ develop ──PR──→ main ──auto──→ version bump + tag
```

- Feature branches merge to `develop` via PR (squash merge preferred)
- `develop` merges to `main` for releases
- Direct pushes to `main` are blocked by git hooks and branch protection

---

## 7. Production Deployment

### Deployment Topology

Agent Academy runs as a single long-lived process. The recommended production setup:

```
┌─────────────────────────────────────────────┐
│  Linux Server (Ubuntu 22.04+ recommended)    │
│                                              │
│  ┌────────────────────────────────────────┐  │
│  │  systemd service: agent-academy        │  │
│  │  WorkingDirectory=/opt/agent-academy   │  │
│  │  ExecStart=dotnet AgentAcademy.Server  │  │
│  │  Restart=always                        │  │
│  └──────────────────┬─────────────────────┘  │
│                     │                        │
│  ┌──────────────────┼─────────────────────┐  │
│  │  /opt/agent-academy/                   │  │
│  │  ├── agent-academy.db  (SQLite)        │  │
│  │  ├── .worktrees/       (agent trees)   │  │
│  │  ├── wwwroot/          (Vite build)    │  │
│  │  └── Config/agents.json               │  │
│  └────────────────────────────────────────┘  │
│                                              │
│  ┌────────────────────────────────────────┐  │
│  │  Reverse proxy (nginx/caddy)           │  │
│  │  ├── TLS termination                   │  │
│  │  ├── WebSocket upgrade (SignalR)       │  │
│  │  └── Static asset caching              │  │
│  └────────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

### Publishing the Application

```bash
# Backend: publish a self-contained single-file binary
dotnet publish src/AgentAcademy.Server \
  -c Release \
  -o ./publish \
  --self-contained \
  -r linux-x64

# Frontend: build and copy to wwwroot
cd src/agent-academy-client
npm ci && npm run build
cp -r dist/ ../../publish/wwwroot/
```

### systemd Service Unit

```ini
# /etc/systemd/system/agent-academy.service
[Unit]
Description=Agent Academy
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/agent-academy
ExecStart=/opt/agent-academy/AgentAcademy.Server --urls "http://localhost:5066"
Restart=always
RestartSec=10
User=agent-academy
Group=agent-academy

# Environment
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
Environment=ConsultantApi__SharedSecret=<secret>
Environment=GitHub__ClientId=<client-id>
Environment=GitHub__ClientSecret=<client-secret>

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/opt/agent-academy
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

```bash
# Enable and start
sudo systemctl daemon-reload
sudo systemctl enable agent-academy
sudo systemctl start agent-academy
sudo systemctl status agent-academy
```

### Containerized Deployment (Docker)

An alternative to systemd deployment. The multi-stage `Dockerfile` builds both frontend and backend into a single image that serves the SPA from the .NET process.

**Architecture:**

```
┌──────────────────────────────────────────────────┐
│  Docker Container (agent-academy)                 │
│                                                   │
│  ┌───────────────────────────────────────────┐   │
│  │  ASP.NET Core 8 (Kestrel, port 8080)      │   │
│  │  ├── REST API (/api/*)                    │   │
│  │  ├── SignalR Hub (/hubs/activity)         │   │
│  │  ├── Static files (/wwwroot — Vite build) │   │
│  │  ├── SPA fallback (index.html)            │   │
│  │  └── Health checks (/health)              │   │
│  └───────────────┬───────────────────────────┘   │
│                  │                                │
│  Volume: /data   │                                │
│  ├── agent-academy.db  (SQLite + WAL/SHM)        │
│  └── data-protection-keys/                        │
└──────────────────────────────────────────────────┘
```

**Build and run:**

```bash
# Build the image
docker build -t agent-academy .

# Run with docker-compose (recommended)
docker compose up -d

# Or run directly
docker run -d \
  --name agent-academy \
  -p 8080:8080 \
  -v aa-data:/data \
  -e ConsultantApi__SharedSecret="your-secret" \
  agent-academy
```

**Multi-stage build process:**

1. `build-frontend` (node:20-alpine): `npm ci` + `npm run build` → produces `dist/`
2. `build-backend` (dotnet/sdk:8.0): `dotnet publish` → produces published DLLs
3. `runtime` (dotnet/aspnet:8.0): copies published output + frontend dist into wwwroot

**Image size:** ~250 MB (aspnet runtime + curl for healthcheck)

**docker-compose.yml** provides:
- Named volume `aa-data` for SQLite database and DataProtection keys
- Environment variable configuration for GitHub OAuth, Consultant API, Discord
- Health check using the `/health` endpoint
- Restart policy: `unless-stopped`

**Data persistence:**

| Path | Purpose | Persistence |
|------|---------|-------------|
| `/data/agent-academy.db` | SQLite database (+ WAL/SHM sidecars) | Named volume `aa-data` |
| `/data/data-protection-keys/` | ASP.NET DataProtection keys (auth cookies) | Named volume `aa-data` |

**Limitations:**

This is an **app-only container** — it runs the web API and serves the frontend, but does **not** include agent execution capabilities:

- No .NET SDK (cannot run `dotnet build`/`dotnet test` inside the container)
- No Node.js (cannot run `npm test`)
- No Copilot CLI (cannot execute agents)
- No git worktree management

Agent execution requires a host machine with these tools, communicating with the containerized API via the Consultant API. A future "runner" profile may package these tools for fully containerized agent execution.

### Reverse Proxy (nginx)

```nginx
# /etc/nginx/sites-available/agent-academy
server {
    listen 443 ssl http2;
    server_name academy.example.com;

    ssl_certificate     /etc/letsencrypt/live/academy.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/academy.example.com/privkey.pem;

    # SignalR requires WebSocket upgrades
    location /hubs/ {
        proxy_pass http://localhost:5066;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 3600s;   # Keep WebSocket alive
    }

    # SSE streams need long-lived connections
    location /api/stream/ {
        proxy_pass http://localhost:5066;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;         # SSE must not be buffered
        proxy_read_timeout 3600s;
    }

    # Everything else (REST + static assets)
    location / {
        proxy_pass http://localhost:5066;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

> **CORS note**: When deploying behind a reverse proxy, set `Cors:Origins` in appsettings (or `Cors__Origins__0` env var) to the public frontend URL, and update `GitHub:FrontendUrl` to match.

### Database Considerations

- **SQLite WAL mode**: EF Core enables WAL by default. This allows concurrent reads during writes — important when multiple agents are active.
- **Backup**: Copy `agent-academy.db`, `agent-academy.db-wal`, and `agent-academy.db-shm` together while the server is running (SQLite WAL supports hot backup). Alternatively, use the SQLite `.backup` command for a consistent snapshot.
- **Size**: A typical installation with months of agent conversation history stays under 500 MB.
- **Migration**: Migrations run automatically on startup via `db.Database.Migrate()`.

### Data Protection Keys

ASP.NET Core Data Protection keys are stored at:
```
~/.local/share/AgentAcademy/DataProtection-Keys/
```

These keys encrypt notification provider secrets (Discord bot tokens, etc.). **Back up this directory alongside the database.** If keys are lost, encrypted secrets must be re-entered.

---

## 8. Operational Procedures

### Server Lifecycle

**Start**:
```bash
dotnet run --project src/AgentAcademy.Server --urls "http://localhost:5066"
# or via systemd:
sudo systemctl start agent-academy
```

**Stop gracefully**:
```bash
# The server registers a shutdown hook that records the shutdown timestamp
# and marks active agent sessions as interrupted (for crash recovery).
sudo systemctl stop agent-academy
# or:
kill -SIGTERM <pid>
```

**Restart with crash recovery**:
If the server crashes (unclean shutdown), the next startup:
1. Detects the crash via `CrashRecoveryService.CurrentCrashDetected`
2. Posts system messages to active rooms noting the interruption
3. Re-enqueues rooms with unanswered human messages
4. Agents resume from their last known state

### Worktree Management

Agent worktrees live under `.worktrees/` relative to the repository root. Each active task gets its own worktree on a `task/{task-id}` branch.

```bash
# List active worktrees
git worktree list

# Clean up stale worktrees (completed/cancelled tasks)
# Use the CLEANUP_WORKTREES command via the agent system, or manually:
git worktree prune
```

> ⚠️ **Do not start the server while making manual git operations.** The `GitService` actively manages branches and worktrees — concurrent modifications may corrupt state.

### Monitoring

**Health indicators**:
- Readiness probe: `GET /health` — checks database connectivity and agent executor status, returns JSON with per-component details
- Liveness probe: `GET /healthz` — lightweight check confirming the server process is responding
- Instance identity: `GET /api/health/instance` — includes instance ID, crash detection, circuit breaker state
- Server process is running: `systemctl is-active agent-academy`
- Database is accessible: check `agent-academy.db` file size and last-modified time
- Agents are responding: check the main room for recent agent messages via `GET /api/rooms/{mainRoomId}/messages?count=5`
- Copilot CLI is reachable: `CopilotAuthProbe` checks token validity on agent execution

**Log output**: Standard ASP.NET Core logging to stdout/stderr. Use `journalctl -u agent-academy -f` with systemd.

**LLM usage tracking**: The `LlmUsageTracker` service records token consumption per agent per model. Query via `GET /api/settings/llm-usage` or directly from the `llm_usage_records` table.

### Backup & Restore

**What to back up**:
1. `agent-academy.db` + WAL files (database)
2. `~/.local/share/AgentAcademy/DataProtection-Keys/` (encryption keys)
3. `Config/agents.json` (agent catalog — if customized)
4. `appsettings.Production.json` (if using file-based overrides)

**Backup script** (example):
```bash
#!/bin/bash
BACKUP_DIR="/backups/agent-academy/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$BACKUP_DIR"

# Database (hot backup via sqlite3)
sqlite3 /opt/agent-academy/agent-academy.db ".backup '$BACKUP_DIR/agent-academy.db'"

# Encryption keys
cp -r ~/.local/share/AgentAcademy/DataProtection-Keys/ "$BACKUP_DIR/dp-keys/"

# Config
cp /opt/agent-academy/Config/agents.json "$BACKUP_DIR/" 2>/dev/null || true
```

**Restore**:
1. Stop the server
2. Replace `agent-academy.db` with the backup
3. Restore Data Protection keys to the same path
4. Start the server — migrations will apply any schema changes

---

## 9. Troubleshooting

### Common Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| Server fails to start with migration error | Schema conflict from manual DB edits | Delete `agent-academy.db` and restart (data loss), or apply the migration manually |
| "Data Protection key not found" in logs | Missing key ring after machine migration | Restore `DataProtection-Keys/` from backup, or re-enter notification secrets via the Settings panel |
| Agents don't respond | Copilot CLI not on PATH, or OAuth token expired | Check `Copilot:CliPath` config, verify `copilot auth status` |
| SignalR connections drop behind proxy | Proxy not configured for WebSocket upgrade | Add `Upgrade` and `Connection` headers in proxy config (see nginx example above) |
| SSE streams cut off after 60 seconds | Proxy buffering or timeout too short | Disable `proxy_buffering`, increase `proxy_read_timeout` |
| "Database is locked" errors | Long-running transaction blocking WAL | Restart the server; check for orphaned sqlite3 processes |
| Worktree conflicts | Manual git operations while server is running | Stop the server, run `git worktree prune`, then `git worktree repair`, restart |
| Encrypted secrets unreadable after deploy | Data Protection keys not migrated | Restore key ring from backup, or re-configure notification providers |

### Diagnostic Commands

```bash
# Check server status
systemctl status agent-academy

# Tail logs
journalctl -u agent-academy -f --no-pager

# Check database integrity
sqlite3 agent-academy.db "PRAGMA integrity_check;"

# Check database size
du -sh agent-academy.db*

# List active worktrees
git worktree list

# Check Copilot CLI auth
copilot auth status

# Test Consultant API connectivity
curl -s -H "X-Consultant-Key: $SECRET" http://localhost:5066/api/rooms | head -c 200
```

---

## 10. Versioning

Agent Academy follows semantic versioning (`major.minor.patch`).

- **Current version**: Defined in `Directory.Build.props` (backend) and `package.json` (frontend)
- **Automatic bumps**: The `version-bump.yml` workflow bumps on merge to `main`
- **Changelog**: Generated from conventional commits by `scripts/generate-changelog.sh`
- **Spec version**: Tracked separately in `specs/spec-version.json` — bumped via `scripts/bump-spec-version.sh`

See [002 — Development Workflow](../002-development-workflow/spec.md) for the full branching and release strategy.

---

## Known Gaps

1. ~~**No containerization**~~: ✅ Resolved — Dockerfile and docker-compose.yml added. App-only container (no agent execution in-container).
2. **No infrastructure-as-code**: Server provisioning is manual. Consider Terraform/Ansible for repeatable deployments.
3. **No automated production deployment**: CI builds and tests but does not deploy. The `main` branch triggers version bump + tag, but the actual deployment step is manual.
4. **Single-machine only**: SQLite constrains the architecture to one server. Scaling horizontally would require a database migration to PostgreSQL and a distributed message bus for SignalR.
5. **No agent-runner container**: The Docker image is app-only. A future "runner" profile with .NET SDK, Node.js, Git, and Copilot CLI would enable fully containerized agent execution.

---

## Revision History

| Date | Change | Author |
|------|--------|--------|
| 2026-04-14 | Added Dockerfile, docker-compose, containerized deployment section; resolved known gap #1; added gap #5 (runner profile) | Anvil (Copilot) |
| 2026-04-14 | Added /health readiness probe, updated monitoring section, resolved known gap #4 | Anvil (Copilot) |
| 2026-04-14 | Initial spec — prerequisites, configuration, local dev, CI/CD, production deployment, operations, troubleshooting | Anvil (Copilot) |
