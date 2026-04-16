#!/usr/bin/env bash
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Run Stryker.NET mutation testing on critical modules.
Uses per-module test-case-filter to prevent VsTest hanging with large suites.

Options:
  --full            Run all modules sequentially (default)
  --security        Run only security-critical files
  --since [REF]     Only mutate files changed since REF (default: develop)
  --open            Open HTML report after completion
  -h, --help        Show this help

Examples:
  $(basename "$0")                  # Full mutation run (~25 min)
  $(basename "$0") --security       # Security modules only (~5 min)
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

TEST_DIR="$ROOT_DIR/tests/AgentAcademy.Server.Tests"
PROJECT_ARG="--project src/AgentAcademy.Server/AgentAcademy.Server.csproj"
OUTPUT_ARG="--output $ROOT_DIR/StrykerOutput"

# Module definitions: mutate files + test-case-filter
# VsTest hangs when running all 5,222 tests under Stryker's mutation framework.
# Scoping tests per module prevents this while still achieving accurate scores.
declare -A MODULE_FILTERS=(
    ["command-parser"]="Commands/CommandParser.cs|FullyQualifiedName~CommandParser"
    ["command-authorizer"]="Commands/CommandAuthorizer.cs|FullyQualifiedName~CommandAuthorizer|FullyQualifiedName~Security"
    ["shell-command"]="Commands/ShellCommand.cs|FullyQualifiedName~ShellCommand|FullyQualifiedName~Security"
    ["prompt-sanitizer"]="Services/PromptSanitizer.cs|FullyQualifiedName~PromptSanitizer|FullyQualifiedName~Security"
    ["task-lifecycle"]="Services/TaskLifecycleService.cs,Services/TaskLifecycleService.Review.cs,Services/TaskLifecycleService.SpecLinks.cs|FullyQualifiedName~TaskLifecycle"
)

SECURITY_MODULES=("command-parser" "command-authorizer" "shell-command" "prompt-sanitizer")
ALL_MODULES=("command-parser" "command-authorizer" "shell-command" "prompt-sanitizer" "task-lifecycle")

run_module() {
    local name="$1"
    local extra_args="${2:-}"
    local spec="${MODULE_FILTERS[$name]}"
    local mutate_files="${spec%%|*}"
    local test_filter="${spec#*|}"

    echo ""
    echo "  ▸ Module: $name"

    # Build config with test-case-filter
    local config_file
    config_file=$(mktemp /tmp/stryker-XXXXXX.json)

    local mutate_json=""
    IFS=',' read -ra files <<< "$mutate_files"
    for f in "${files[@]}"; do
        [[ -n "$mutate_json" ]] && mutate_json+=","
        mutate_json+="\"$f\""
    done

    cat > "$config_file" << CONF
{
  "stryker-config": {
    "mutate": [$mutate_json],
    "reporters": ["json", "cleartext"],
    "verbosity": "info",
    "thresholds": { "high": 80, "low": 60, "break": 0 },
    "concurrency": 2,
    "additional-timeout": 30000,
    "test-case-filter": "$test_filter"
  }
}
CONF

    cd "$TEST_DIR"
    # shellcheck disable=SC2086
    dotnet stryker --config-file "$config_file" $PROJECT_ARG $OUTPUT_ARG $extra_args 2>&1 | \
        grep -E "mutation score|Killed|Survived|INF.*Time Elapsed|failed"
    local exit_code=${PIPESTATUS[0]}

    rm -f "$config_file"
    return $exit_code
}

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Stryker.NET Mutation Testing"
echo "  Mode: $MODE"
echo "  Thresholds: break=50% low=60% high=80%"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

FINAL_EXIT=0

case "$MODE" in
    full)
        echo "🧬 Running all modules sequentially..."
        for mod in "${ALL_MODULES[@]}"; do
            run_module "$mod" || FINAL_EXIT=1
        done
        ;;
    security)
        echo "🔒 Running security modules..."
        for mod in "${SECURITY_MODULES[@]}"; do
            run_module "$mod" || FINAL_EXIT=1
        done
        ;;
    since)
        echo "📊 Running mutation testing on changes since $SINCE_REF..."

        # Validate the ref exists
        if ! git rev-parse --verify "$SINCE_REF" >/dev/null 2>&1; then
            echo "  ❌ Invalid git ref: $SINCE_REF"
            exit 1
        fi

        # Find changed C# files in the server project
        changed_files=$(git diff --name-only "$SINCE_REF" -- 'src/AgentAcademy.Server/**/*.cs' | \
            sed 's|src/AgentAcademy.Server/||')

        if [[ -z "$changed_files" ]]; then
            echo "  No C# server files changed since $SINCE_REF"
            exit 0
        fi

        echo "  Changed files:"
        echo "$changed_files" | sed 's/^/    /'

        # Map changed files to known modules
        matched_modules=()
        unmatched_files=()

        while IFS= read -r file; do
            found=false
            for mod in "${ALL_MODULES[@]}"; do
                spec="${MODULE_FILTERS[$mod]}"
                mutate_files="${spec%%|*}"
                if echo "$mutate_files" | tr ',' '\n' | grep -qF "$file"; then
                    # Add module only if not already in list
                    already=false
                    for m in "${matched_modules[@]+"${matched_modules[@]}"}"; do
                        [[ "$m" == "$mod" ]] && already=true && break
                    done
                    $already || matched_modules+=("$mod")
                    found=true
                    break
                fi
            done
            $found || unmatched_files+=("$file")
        done <<< "$changed_files"

        if [[ ${#unmatched_files[@]} -gt 0 ]]; then
            echo ""
            echo "  ⚠ Files not covered by any module filter (skipped):"
            printf '    %s\n' "${unmatched_files[@]}"
        fi

        if [[ ${#matched_modules[@]} -eq 0 ]]; then
            echo "  No changed files match known modules. Nothing to mutate."
            exit 0
        fi

        echo ""
        echo "  Running modules: ${matched_modules[*]}"
        for mod in "${matched_modules[@]}"; do
            run_module "$mod" "--since $SINCE_REF" || FINAL_EXIT=1
        done
        ;;
esac

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
if [[ $FINAL_EXIT -eq 0 ]]; then
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

exit $FINAL_EXIT
