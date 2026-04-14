#!/usr/bin/env bash
# Run coverage collection for both backend and frontend.
# Usage: scripts/coverage.sh [--backend|--frontend]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCOPE="${1:-all}"

run_backend() {
  echo "📊 Backend coverage (coverlet)..."
  cd "$REPO_ROOT"
  rm -rf TestResults
  dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults

  if command -v reportgenerator &>/dev/null; then
    reportgenerator \
      -reports:"TestResults/**/coverage.cobertura.xml" \
      -targetdir:"TestResults/report" \
      -reporttypes:"Cobertura;TextSummary;Html"
    echo ""
    cat TestResults/report/Summary.txt
    echo ""
    echo "HTML report: TestResults/report/index.html"
  else
    echo "ℹ️  Install ReportGenerator for merged reports:"
    echo "   dotnet tool install --global dotnet-reportgenerator-globaltool"
    echo "Raw Cobertura XML in TestResults/**/coverage.cobertura.xml"
  fi
}

run_frontend() {
  echo "📊 Frontend coverage (vitest + v8)..."
  cd "$REPO_ROOT/src/agent-academy-client"
  npm run test:coverage
  echo ""
  echo "HTML report: src/agent-academy-client/coverage/index.html"
}

case "$SCOPE" in
  --backend)  run_backend ;;
  --frontend) run_frontend ;;
  all)        run_backend; echo ""; run_frontend ;;
  *)
    echo "Usage: scripts/coverage.sh [--backend|--frontend|all]"
    echo "  (no args)    Run both backend and frontend"
    echo "  --backend    Backend only"
    echo "  --frontend   Frontend only"
    exit 2
    ;;
esac
