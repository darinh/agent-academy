#!/usr/bin/env bash
# Bump the spec corpus version in specs/spec-version.json.
# Usage: scripts/bump-spec-version.sh [major|minor|patch]
# Defaults to patch if no argument given.

set -euo pipefail

SPEC_VERSION_FILE="specs/spec-version.json"

if [ ! -f "$SPEC_VERSION_FILE" ]; then
  echo "Error: $SPEC_VERSION_FILE not found" >&2
  exit 1
fi

BUMP_TYPE="${1:-patch}"

CURRENT_VERSION=$(grep -oP '"version"\s*:\s*"\K[^"]+' "$SPEC_VERSION_FILE")
if [ -z "$CURRENT_VERSION" ]; then
  echo "Error: Could not read current version from $SPEC_VERSION_FILE" >&2
  exit 1
fi

IFS='.' read -r MAJOR MINOR PATCH <<< "$CURRENT_VERSION"

case "$BUMP_TYPE" in
  major) MAJOR=$((MAJOR + 1)); MINOR=0; PATCH=0 ;;
  minor) MINOR=$((MINOR + 1)); PATCH=0 ;;
  patch) PATCH=$((PATCH + 1)) ;;
  *)
    echo "Error: Unknown bump type '$BUMP_TYPE'. Use major, minor, or patch." >&2
    exit 1
    ;;
esac

NEW_VERSION="$MAJOR.$MINOR.$PATCH"
TODAY=$(date +%Y-%m-%d)

cat > "$SPEC_VERSION_FILE" << EOF
{
  "version": "$NEW_VERSION",
  "lastUpdated": "$TODAY"
}
EOF

echo "Spec version bumped: $CURRENT_VERSION → $NEW_VERSION ($BUMP_TYPE)"
