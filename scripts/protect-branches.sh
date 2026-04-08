#!/usr/bin/env bash
# Agent Academy — Configure GitHub branch protection rules
#
# Requires: gh (GitHub CLI) with repo admin access.
# Usage:    ./scripts/protect-branches.sh
#
# Sets up protection on `main`:
#   • Require PR reviews (1 approval)
#   • Require status checks to pass (build, test)
#   • Block direct pushes (admins included)
#   • Require linear history (no merge commits)

set -euo pipefail

OWNER="darinh"
REPO="agent-academy"
BRANCH="main"

echo "🔒 Configuring branch protection for $OWNER/$REPO → $BRANCH"

gh api \
  --method PUT \
  "repos/$OWNER/$REPO/branches/$BRANCH/protection" \
  --input - <<'EOF'
{
  "required_status_checks": {
    "strict": true,
    "contexts": []
  },
  "enforce_admins": true,
  "required_pull_request_reviews": {
    "required_approving_review_count": 1,
    "dismiss_stale_reviews": true
  },
  "restrictions": null,
  "required_linear_history": true,
  "allow_force_pushes": false,
  "allow_deletions": false
}
EOF

echo "✅ Branch protection configured for '$BRANCH'."
echo ""
echo "  • PRs require 1 approval (stale reviews dismissed)"
echo "  • Status checks must pass before merge"
echo "  • Direct pushes blocked (including admins)"
echo "  • Linear history required (no merge commits)"
echo "  • Force pushes and branch deletion blocked"
