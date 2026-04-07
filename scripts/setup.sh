#!/usr/bin/env bash
# Agent Academy — developer environment setup
# Run once after cloning: ./scripts/setup.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

echo "🔧 Agent Academy Setup"
echo "━━━━━━━━━━━━━━━━━━━━━━"

# 1. Configure git hooks
echo "→ Configuring git hooks..."
git config core.hooksPath .githooks
echo "  ✓ Git hooks path set to .githooks/"

# 2. Install .NET dependencies
if command -v dotnet &>/dev/null; then
  echo "→ Restoring .NET packages..."
  dotnet restore --verbosity quiet
  echo "  ✓ .NET packages restored"
else
  echo "  ⚠ dotnet not found — skipping .NET restore"
fi

# 3. Install frontend dependencies
if command -v npm &>/dev/null; then
  echo "→ Installing frontend dependencies..."
  (cd src/agent-academy-client && npm ci --silent 2>/dev/null || npm install --silent)
  echo "  ✓ Frontend dependencies installed"
else
  echo "  ⚠ npm not found — skipping frontend install"
fi

echo ""
echo "✅ Setup complete. Run 'dotnet run --project src/AgentAcademy.Server' to start."
