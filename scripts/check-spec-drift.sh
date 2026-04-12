#!/usr/bin/env bash
# check-spec-drift.sh — Detect code changes without corresponding spec updates.
#
# Usage:
#   scripts/check-spec-drift.sh <base-sha> <head-sha>
#   scripts/check-spec-drift.sh              # auto-detect from GITHUB env vars
#
# Exit codes:
#   0 — always (warnings only, never blocks CI)
#
# Dependencies: node (for JSON parsing and analysis)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v node &>/dev/null; then
  echo "::warning::Node.js not found — skipping spec drift check."
  exit 0
fi

# --- Resolve base and head SHAs ---

if [[ $# -ge 2 ]]; then
  BASE_SHA="$1"
  HEAD_SHA="$2"
elif [[ -n "${GITHUB_EVENT_PATH:-}" ]]; then
  BASE_SHA=$(node -e "console.log(require('$GITHUB_EVENT_PATH').pull_request?.base?.sha||'')" 2>/dev/null || true)
  HEAD_SHA=$(node -e "console.log(require('$GITHUB_EVENT_PATH').pull_request?.head?.sha||'')" 2>/dev/null || true)
  if [[ -z "$BASE_SHA" || -z "$HEAD_SHA" ]]; then
    echo "::notice::Spec drift check skipped — not a pull request event."
    exit 0
  fi
else
  BASE_SHA=$(git merge-base develop HEAD 2>/dev/null || git rev-parse develop 2>/dev/null || true)
  HEAD_SHA="HEAD"
  if [[ -z "$BASE_SHA" ]]; then
    echo "⚠️  Cannot determine base — skipping spec drift check."
    exit 0
  fi
fi

echo "📐 Spec drift detection: $BASE_SHA...$HEAD_SHA"

# --- Check for spec-exempt marker ---

if [[ -n "${GITHUB_EVENT_PATH:-}" ]]; then
  PR_BODY=$(node -e "console.log(require('$GITHUB_EVENT_PATH').pull_request?.body||'')" 2>/dev/null || true)
  if echo "$PR_BODY" | grep -qiP '^\s*spec-exempt:'; then
    REASON=$(echo "$PR_BODY" | grep -iP '^\s*spec-exempt:' | head -1 | sed 's/.*spec-exempt:\s*//')
    echo "✅ Spec drift check exempted: $REASON"
    exit 0
  fi
fi

if git log "$BASE_SHA".."$HEAD_SHA" --format="%B" 2>/dev/null | grep -qiP '^\s*spec-exempt:'; then
  echo "✅ Spec drift check exempted via commit trailer."
  exit 0
fi

# --- Get changed files and pipe to Node.js analyzer ---

git diff --name-only --diff-filter=ACMRD -M -C "$BASE_SHA"..."$HEAD_SHA" 2>/dev/null | \
  node "$SCRIPT_DIR/check-spec-drift.js" || true
