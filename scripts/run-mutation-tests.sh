#!/usr/bin/env bash
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Run Stryker.NET mutation testing on critical modules.

Options:
  --full            Run against all configured mutate targets (default)
  --security        Run only against security-critical files
  --since [REF]     Only mutate files changed since REF (default: develop)
  --open            Open HTML report after completion
  -h, --help        Show this help

Examples:
  $(basename "$0")                  # Full mutation run
  $(basename "$0") --security       # Security-critical modules only
  $(basename "$0") --since main     # Only changed files since main
  $(basename "$0") --open           # Full run + open report
EOF
}

MODE="full"
OPEN_REPORT=false
SINCE_REF=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --full)      MODE="full"; shift ;;
        --security)  MODE="security"; shift ;;
        --since)
            MODE="since"
            shift
            if [[ $# -gt 0 && ! "$1" =~ ^-- ]]; then
                SINCE_REF="$1"
                shift
            else
                SINCE_REF="develop"
            fi
            ;;
        --open)      OPEN_REPORT=true; shift ;;
        -h|--help)   usage; exit 0 ;;
        *)           echo "Unknown option: $1"; usage; exit 1 ;;
    esac
done

cd "$ROOT_DIR"

# Ensure tool is installed
dotnet tool restore --verbosity quiet

# Stryker must run from the test project directory for path resolution
TEST_DIR="$ROOT_DIR/tests/AgentAcademy.Server.Tests"
cd "$TEST_DIR"

STRYKER_ARGS=(
    --project src/AgentAcademy.Server/AgentAcademy.Server.csproj
    --output "$ROOT_DIR/StrykerOutput"
)

SECURITY_FILES=(
    "Commands/CommandParser.cs"
    "Commands/CommandAuthorizer.cs"
    "Commands/ShellCommand.cs"
    "Services/PromptSanitizer.cs"
)

case "$MODE" in
    full)
        echo "🧬 Running full mutation testing (configured targets)..."
        ;;
    security)
        echo "🔒 Running security-focused mutation testing..."
        for f in "${SECURITY_FILES[@]}"; do
            STRYKER_ARGS+=(--mutate "$f")
        done
        ;;
    since)
        echo "📊 Running mutation testing on changes since $SINCE_REF..."
        STRYKER_ARGS+=(--since "$SINCE_REF")
        ;;
esac

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Stryker.NET Mutation Testing"
echo "  Mode: $MODE"
echo "  Thresholds: break=50% low=60% high=80%"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""

dotnet stryker "${STRYKER_ARGS[@]}"
EXIT_CODE=$?

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
if [[ $EXIT_CODE -eq 0 ]]; then
    echo "  ✅ Mutation testing passed (above break threshold)"
else
    echo "  ❌ Mutation testing failed (below break threshold)"
fi
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

# Find and show report location
cd "$ROOT_DIR"
REPORT_DIR=$(find StrykerOutput -name "mutation-report.html" -type f 2>/dev/null | sort | tail -1)
if [[ -n "$REPORT_DIR" ]]; then
    echo "📄 HTML Report: $ROOT_DIR/$REPORT_DIR"
    if $OPEN_REPORT && command -v xdg-open &>/dev/null; then
        xdg-open "$ROOT_DIR/$REPORT_DIR"
    fi
fi

exit $EXIT_CODE
