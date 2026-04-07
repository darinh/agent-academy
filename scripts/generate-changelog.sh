#!/usr/bin/env bash
# Agent Academy — Generate release changelog from conventional commits
#
# Usage:
#   ./scripts/generate-changelog.sh              # all commits since last tag
#   ./scripts/generate-changelog.sh v0.1.0       # commits since specific tag
#   ./scripts/generate-changelog.sh --all        # full history grouped by tag
#
# Output: writes CHANGELOG.md to repo root

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

OUTPUT="$REPO_ROOT/CHANGELOG.md"

# Collect tags in reverse chronological order
mapfile -t TAGS < <(git tag --sort=-version:refname 2>/dev/null)

format_section() {
    local ref_range="$1"
    local version_label="$2"
    local date_label="$3"
    local has_content=false
    local features="" fixes="" docs="" refactors="" tests="" others=""

    while IFS= read -r line; do
        [[ -z "$line" ]] && continue
        has_content=true
        case "$line" in
            feat:*|feat\(*) features+="- ${line#*: }"$'\n' ;;
            fix:*|fix\(*)   fixes+="- ${line#*: }"$'\n' ;;
            docs:*|docs\(*) docs+="- ${line#*: }"$'\n' ;;
            refactor:*|refactor\(*) refactors+="- ${line#*: }"$'\n' ;;
            test:*|test\(*) tests+="- ${line#*: }"$'\n' ;;
            chore:*|chore\(*) ;; # skip chore commits (version bumps, etc.)
            *) others+="- $line"$'\n' ;;
        esac
    done < <(git log "$ref_range" --format="%s" --no-merges 2>/dev/null)

    if [[ "$has_content" == false ]]; then
        return
    fi

    echo "## $version_label ($date_label)"
    echo ""

    [[ -n "$features" ]] && echo "### Features" && echo "" && echo -n "$features" && echo ""
    [[ -n "$fixes" ]] && echo "### Fixes" && echo "" && echo -n "$fixes" && echo ""
    [[ -n "$docs" ]] && echo "### Documentation" && echo "" && echo -n "$docs" && echo ""
    [[ -n "$refactors" ]] && echo "### Refactoring" && echo "" && echo -n "$refactors" && echo ""
    [[ -n "$tests" ]] && echo "### Tests" && echo "" && echo -n "$tests" && echo ""
    [[ -n "$others" ]] && echo "### Other" && echo "" && echo -n "$others" && echo ""
}

{
    echo "# Changelog"
    echo ""
    echo "All notable changes to Agent Academy are documented here."
    echo "Generated from [conventional commits](https://www.conventionalcommits.org/)."
    echo ""

    if [[ ${#TAGS[@]} -eq 0 ]]; then
        # No tags — generate from all commits
        latest_date=$(git log -1 --format="%cd" --date=short 2>/dev/null || date +%Y-%m-%d)
        format_section "HEAD" "Unreleased" "$latest_date"
    else
        # Unreleased section (commits since latest tag)
        unreleased_count=$(git rev-list "${TAGS[0]}..HEAD" --count 2>/dev/null || echo "0")
        if [[ "$unreleased_count" -gt 0 ]]; then
            latest_date=$(git log -1 --format="%cd" --date=short)
            format_section "${TAGS[0]}..HEAD" "Unreleased" "$latest_date"
        fi

        # Each tag
        for i in "${!TAGS[@]}"; do
            tag="${TAGS[$i]}"
            tag_date=$(git log -1 --format="%cd" --date=short "$tag" 2>/dev/null || echo "unknown")

            if [[ $((i + 1)) -lt ${#TAGS[@]} ]]; then
                prev_tag="${TAGS[$((i + 1))]}"
                ref_range="$prev_tag..$tag"
            else
                ref_range="$tag"
            fi

            format_section "$ref_range" "$tag" "$tag_date"
        done
    fi
} > "$OUTPUT"

echo "✅ Generated $OUTPUT ($(wc -l < "$OUTPUT") lines)"
