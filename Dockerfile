# ── Stage 1: Build frontend ──────────────────────────────────────────────────
FROM node:20-alpine AS build-frontend

WORKDIR /src/agent-academy-client
COPY src/agent-academy-client/package.json src/agent-academy-client/package-lock.json ./
RUN npm ci --ignore-scripts

COPY src/agent-academy-client/ ./
RUN npm run build

# ── Stage 2: Build backend ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-backend

WORKDIR /src
COPY Directory.Build.props ./
COPY src/AgentAcademy.Server/AgentAcademy.Server.csproj src/AgentAcademy.Server/
COPY src/AgentAcademy.Shared/AgentAcademy.Shared.csproj src/AgentAcademy.Shared/
RUN dotnet restore src/AgentAcademy.Server/AgentAcademy.Server.csproj

COPY src/ src/
RUN dotnet publish src/AgentAcademy.Server/AgentAcademy.Server.csproj \
    -c Release -o /app/publish --no-restore

# Copy frontend build output into wwwroot
COPY --from=build-frontend /src/agent-academy-client/dist /app/publish/wwwroot

# ── Stage 3: Runner (full agent execution) ──────────────────────────────────
# Build with: docker build --target runner -t agent-academy-runner .
# Includes .NET SDK, Node.js, and Git for fully containerized agent execution.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runner

# Copy Node.js from official image (avoids curl|bash install scripts)
COPY --from=node:20-slim /usr/local/bin/node /usr/local/bin/node
COPY --from=node:20-slim /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

RUN apt-get update && apt-get install -y --no-install-recommends curl git \
    && rm -rf /var/lib/apt/lists/*

# Non-root user with home directory (needed for Copilot CLI auth state)
RUN groupadd -r appuser && useradd -r -g appuser -m -s /bin/bash appuser

# App binaries
WORKDIR /app
COPY --from=build-backend /app/publish .
COPY --from=build-frontend /src/agent-academy-client/dist ./wwwroot

# Data and workspace directories
RUN mkdir -p /data /data/data-protection-keys /workspace \
    && chown -R appuser:appuser /data /workspace /app

# Allow git operations in mounted workspace directories
RUN git config --system --add safe.directory /workspace

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ConnectionStrings__DefaultConnection="Data Source=/data/agent-academy.db"
ENV DataProtection__KeysPath=/data/data-protection-keys

USER appuser

# CWD must be the workspace so FindProjectRoot() discovers AgentAcademy.sln
WORKDIR /workspace

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "/app/AgentAcademy.Server.dll"]

# ── Stage 4: Runtime (app-only, default) ────────────────────────────────────
# This is the LAST stage, so `docker build .` produces the lightweight app image.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd -r appuser && useradd -r -g appuser -s /sbin/nologin appuser

WORKDIR /app
COPY --from=build-backend /app/publish .

# Data directory for SQLite and DataProtection keys
RUN mkdir -p /data /data/data-protection-keys \
    && chown -R appuser:appuser /data

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ConnectionStrings__DefaultConnection="Data Source=/data/agent-academy.db"
ENV DataProtection__KeysPath=/data/data-protection-keys

USER appuser

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "AgentAcademy.Server.dll"]
