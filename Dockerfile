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

# ── Stage 3: Runtime ────────────────────────────────────────────────────────
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
