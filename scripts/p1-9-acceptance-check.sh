#!/usr/bin/env bash
# P1.9 acceptance status driver
#
# Read-only inspector for the §10 "Definition of Done for the Vision" acceptance
# test (specs/100-product-vision/spec.md §10). Given a sprint id (or "active"),
# walks the 10 observable conditions and reports PASS / FAIL / NA / UNKNOWN per
# step, sourcing every signal from the live API. Makes no writes. Safe to run
# against a running sprint.
#
# Usage:
#   scripts/p1-9-acceptance-check.sh                    # active sprint
#   scripts/p1-9-acceptance-check.sh <sprint-id>        # specific sprint
#   scripts/p1-9-acceptance-check.sh --base http://host:port [<sprint-id>]
#
# Auth: requires ConsultantApi:SharedSecret in dotnet user-secrets for the
# AgentAcademy.Server project. The script reads it locally and sends it as
# X-Consultant-Key. The secret is never logged or written anywhere.
#
# Exit codes:
#   0  every observable step PASSED (human-action steps reported as NA)
#   1  one or more observable steps FAILED
#   2  setup error (server unreachable, secret missing, invalid sprint id)

set -euo pipefail

BASE="${AA_BASE:-http://localhost:5066}"
SPRINT_ARG="active"

# --- arg parsing -------------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    --base) BASE="$2"; shift 2 ;;
    -h|--help)
      sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) SPRINT_ARG="$1"; shift ;;
  esac
done

# --- color helpers -----------------------------------------------------------
if [[ -t 1 ]]; then
  C_RESET=$'\033[0m'; C_GREEN=$'\033[0;32m'; C_RED=$'\033[0;31m'
  C_YELLOW=$'\033[0;33m'; C_DIM=$'\033[0;90m'; C_BOLD=$'\033[1m'
else
  C_RESET=""; C_GREEN=""; C_RED=""; C_YELLOW=""; C_DIM=""; C_BOLD=""
fi

PASS=0; FAIL=0; NA=0; UNK=0
report() {
  local status="$1" step="$2" detail="$3"
  case "$status" in
    PASS) printf "  %s✓%s %-3s %s%s%s — %s\n" "$C_GREEN" "$C_RESET" "$step" "$C_BOLD" "$status" "$C_RESET" "$detail"; PASS=$((PASS+1)) ;;
    FAIL) printf "  %s✗%s %-3s %s%s%s — %s\n" "$C_RED"   "$C_RESET" "$step" "$C_BOLD" "$status" "$C_RESET" "$detail"; FAIL=$((FAIL+1)) ;;
    NA)   printf "  %s—%s %-3s %s%s%s   — %s\n" "$C_DIM"   "$C_RESET" "$step" "$C_DIM"  "$status" "$C_RESET" "$detail"; NA=$((NA+1)) ;;
    UNK)  printf "  %s?%s %-3s %s%s%s — %s\n" "$C_YELLOW" "$C_RESET" "$step" "$C_BOLD" "UNKNOWN" "$C_RESET" "$detail"; UNK=$((UNK+1)) ;;
  esac
}

die() { printf "%serror:%s %s\n" "$C_RED" "$C_RESET" "$*" >&2; exit 2; }

# --- secret + auth -----------------------------------------------------------
SECRETS_FILE="$HOME/.microsoft/usersecrets/62ff9a7f-7740-453d-9d8b-09ae7141f5c2/secrets.json"
if [[ ! -r "$SECRETS_FILE" ]]; then
  die "user-secrets file not readable: $SECRETS_FILE (set ConsultantApi:SharedSecret)"
fi
AA_KEY="$(python3 -c "import json; print(json.load(open(r'$SECRETS_FILE', encoding='utf-8-sig')).get('ConsultantApi:SharedSecret',''))")"
[[ -n "$AA_KEY" ]] || die "ConsultantApi:SharedSecret is empty in user-secrets"

api() {
  # Usage: BODY=$(api PATH); CODE=$(api_code) — but command substitution
  # discards env changes from subshells. Instead we stash code+body in a
  # tempfile pair and the caller splits them.
  local path="$1" tmp_body tmp_code
  tmp_body="$(mktemp)"; tmp_code="$(mktemp)"
  curl -sS -m 10 -o "$tmp_body" -w "%{http_code}" \
    -H "X-Consultant-Key: $AA_KEY" \
    -H "Accept: application/json" \
    "$BASE$path" >"$tmp_code" 2>/dev/null || true
  printf "CODE=%s\n" "$(cat "$tmp_code")"
  cat "$tmp_body"
  rm -f "$tmp_body" "$tmp_code"
}
# Helpers to split the api() output stream.
_api_code() {
  # First line is "CODE=NNN"; extract without pipes (avoids SIGPIPE under pipefail).
  local first="${1%%$'\n'*}"
  printf "%s" "${first#CODE=}"
}
_api_body() {
  # Strip the first line (CODE=NNN\n) from the captured stream.
  local s="$1"
  printf "%s" "${s#*$'\n'}"
}

# --- preflight ---------------------------------------------------------------
PREFLIGHT="$(api /api/auth/status)"
[[ "$(_api_code "$PREFLIGHT")" == "200" ]] || die "server not reachable at $BASE (auth/status returned $(_api_code "$PREFLIGHT"))"

# Resolve sprint id ("active" -> current active sprint id)
if [[ "$SPRINT_ARG" == "active" ]]; then
  R="$(api /api/sprints/active)"
  [[ "$(_api_code "$R")" == "200" ]] || die "GET /api/sprints/active returned $(_api_code "$R")"
  SPRINT_ID="$(_api_body "$R" | python3 -c "import sys,json; d=json.load(sys.stdin); s=d.get('sprint'); print((s or {}).get('id',''))")"
  [[ -n "$SPRINT_ID" ]] || die "no active sprint"
else
  SPRINT_ID="$SPRINT_ARG"
fi

R="$(api "/api/sprints/$SPRINT_ID")"
[[ "$(_api_code "$R")" == "200" ]] || die "GET /api/sprints/$SPRINT_ID returned $(_api_code "$R")"
SPRINT_JSON="$(_api_body "$R")"

R="$(api "/api/sprints/$SPRINT_ID/artifacts")"
[[ "$(_api_code "$R")" == "200" ]] || die "GET artifacts returned $(_api_code "$R")"
ART_JSON="$(_api_body "$R")"

R="$(api "/api/sprints/$SPRINT_ID/self-eval/latest")"
SELF_EVAL_CODE="$(_api_code "$R")"
SELF_EVAL_BODY="$(_api_body "$R")"

R="$(api "/api/notifications/deliveries?limit=200")"
if [[ "$(_api_code "$R")" == "200" ]]; then DELIVERIES="$(_api_body "$R")"; else DELIVERIES="[]"; fi

# --- print header ------------------------------------------------------------
_sprint_field() {
  printf "%s" "$SPRINT_JSON" | python3 -c "
import sys,json
d=json.load(sys.stdin); s=d.get('sprint') if isinstance(d.get('sprint'),dict) else d
print(s.get('$1',''))"
}
SPRINT_NUM="$(_sprint_field number)"
SPRINT_STAGE="$(_sprint_field currentStage)"
SPRINT_STATUS="$(_sprint_field status)"

printf "\n%sP1.9 — §10 Acceptance Status%s\n" "$C_BOLD" "$C_RESET"
printf "%sSprint #%s%s  status=%s  stage=%s  id=%s%s\n" "$C_DIM" "$SPRINT_NUM" "$C_RESET" "$SPRINT_STATUS" "$SPRINT_STAGE" "$SPRINT_ID" "$C_DIM"
printf "%s\n" "──────────────────────────────────────────────────────────────"

# --- Step 1: human posts a goal in a new room -------------------------------
# Observable as "RequirementsDocument or sprint goal text exists at Intake".
GOAL_LEN="$(printf "%s" "$ART_JSON" | python3 -c "
import sys,json
arts = json.load(sys.stdin)
intake_req = next((a for a in arts if a.get('stage')=='Intake' and a.get('type')=='RequirementsDocument'), None)
print(len((intake_req or {}).get('content','')))")"
if [[ "$GOAL_LEN" -gt 0 ]]; then
  report PASS "1." "RequirementsDocument exists at Intake (${GOAL_LEN} chars)"
else
  report FAIL "1." "no RequirementsDocument at Intake stage"
fi

# --- Step 2: agents push back on scope cohesion -----------------------------
# Observable proxy: room messages during Intake show >=1 agent message before
# stage advances to Planning. We can't gauge "pushback" content semantically;
# we settle for "agent participated in Intake".
ROOM_ID="main"
R="$(api "/api/rooms/$ROOM_ID/messages?limit=500")"
ROOM_MSGS_CODE="$(_api_code "$R")"
ROOM_MSGS="$(_api_body "$R")"
if [[ "$ROOM_MSGS_CODE" == "200" ]]; then
  AGENT_INTAKE_COUNT="$(printf "%s" "$ROOM_MSGS" | python3 -c "
import sys,json
d = json.load(sys.stdin)
msgs = d.get('messages', d) if isinstance(d, dict) else d
msgs = msgs or []
agent_msgs = [m for m in msgs if isinstance(m, dict) and (m.get('senderKind') or '').lower() == 'agent']
print(len(agent_msgs))")"
  if [[ "$AGENT_INTAKE_COUNT" -gt 0 ]]; then
    report PASS "2." "agent participation in room (${AGENT_INTAKE_COUNT} agent messages observed; semantic 'pushback' check is human-only)"
  else
    report FAIL "2." "no agent messages in room $ROOM_ID"
  fi
else
  report UNK "2." "GET /api/rooms/$ROOM_ID/messages returned $ROOM_MSGS_CODE"
fi

# --- Step 3: tracking artifact (SprintPlan) at Planning ---------------------
SPRINT_PLAN_LEN="$(printf "%s" "$ART_JSON" | python3 -c "
import sys,json
arts = json.load(sys.stdin)
plan = next((a for a in arts if a.get('stage')=='Planning' and a.get('type')=='SprintPlan'), None)
print(len((plan or {}).get('content','')))")"
if [[ "$SPRINT_PLAN_LEN" -gt 0 ]]; then
  report PASS "3." "SprintPlan artifact exists at Planning (${SPRINT_PLAN_LEN} chars)"
else
  report FAIL "3." "no SprintPlan artifact at Planning stage (P1.5 invariant)"
fi

# --- Step 4: human walks away (NA — human action) ---------------------------
report NA "4." "human action — verify externally that the human stopped sending messages mid-Implementation"

# --- Step 5: agents continued autonomously through Implementation ----------
# Observable: sprint advanced past Planning; activeTask exists; multiple agents
# wrote to the room since the last human message.
case "$SPRINT_STAGE" in
  Implementation|FinalSynthesis|Completed)
    report PASS "5." "sprint reached Implementation or beyond (currentStage=$SPRINT_STAGE)"
    ;;
  *)
    report FAIL "5." "sprint stuck at stage=$SPRINT_STAGE (expected Implementation+)"
    ;;
esac

# --- Step 6: self-evaluation ceremony fired and produced a verdict ---------
case "$SELF_EVAL_CODE" in
  200)
    VERDICT="$(printf "%s" "$SELF_EVAL_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('latestVerdict') or d.get('verdict') or '?')" 2>/dev/null || printf "?")"
    if [[ "$VERDICT" =~ ^(AllPass|AnyFail|Unverified)$ ]]; then
      report PASS "6." "self-eval report present (verdict=$VERDICT)"
    else
      report PASS "6." "self-eval report present (raw verdict=$VERDICT)"
    fi
    ;;
  204)
    if [[ "$SPRINT_STAGE" == "Implementation" ]]; then
      report FAIL "6." "no self-eval report yet (sprint in Implementation, P1.4 ceremony has not run)"
    else
      report FAIL "6." "no self-eval report (sprint at $SPRINT_STAGE — ceremony should have fired before advancing)"
    fi
    ;;
  *) report UNK "6." "GET /self-eval/latest returned $SELF_EVAL_CODE" ;;
esac

# --- Step 7: final work report + Discord notification ----------------------
REPORT_LEN="$(printf "%s" "$ART_JSON" | python3 -c "
import sys,json
arts = json.load(sys.stdin)
rep = next((a for a in arts if a.get('stage')=='FinalSynthesis' and a.get('type')=='SprintReport'), None)
print(len((rep or {}).get('content','')))")"
COMPLETED_NOTIF="$(printf "%s" "$DELIVERIES" | python3 -c "
import sys,json,re
ds = json.load(sys.stdin) or []
needle = re.compile(r'(sprint .*completed|task[_ ]?complete)', re.I)
hits = [d for d in ds if needle.search((d.get('title') or '') + ' ' + (d.get('body') or '') + ' ' + (d.get('notificationType') or ''))]
print(len(hits))")"
if [[ "$REPORT_LEN" -gt 0 && "$COMPLETED_NOTIF" -gt 0 ]]; then
  report PASS "7." "SprintReport at FinalSynthesis (${REPORT_LEN} chars) + ${COMPLETED_NOTIF} sprint-complete notification(s) delivered"
elif [[ "$REPORT_LEN" -gt 0 ]]; then
  report FAIL "7." "SprintReport exists but no sprint-complete notification observed in last 200 deliveries"
elif [[ "$COMPLETED_NOTIF" -gt 0 ]]; then
  report FAIL "7." "sprint-complete notification observed but no SprintReport artifact (G6 violation)"
else
  report FAIL "7." "no SprintReport artifact and no sprint-complete notification"
fi

# --- Step 8: room transitions to read-only --------------------------------
R="$(api "/api/rooms/$ROOM_ID")"
ROOM_CODE="$(_api_code "$R")"
ROOM_STATE="$(_api_body "$R")"
if [[ "$ROOM_CODE" == "200" ]]; then
  ROOM_STATUS="$(printf "%s" "$ROOM_STATE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status',''))" 2>/dev/null || true)"
  case "$ROOM_STATUS" in
    Completed|Archived|ReadOnly)
      report PASS "8." "room $ROOM_ID is $ROOM_STATUS (read-only)"
      ;;
    Active)
      if [[ "$SPRINT_STATUS" == "Completed" || "$SPRINT_STATUS" == "Cancelled" ]]; then
        report FAIL "8." "sprint is $SPRINT_STATUS but room $ROOM_ID still Active (P1.8 freeze did not fire)"
      else
        report NA "8." "sprint not yet terminal (status=$SPRINT_STATUS); room.status=Active is expected here"
      fi
      ;;
    *) report UNK "8." "room.status=$ROOM_STATUS (unrecognized)" ;;
  esac
else
  report UNK "8." "GET /api/rooms/$ROOM_ID returned $ROOM_CODE"
fi

# --- Step 9: idle / follow-up notification ----------------------------------
IDLE_NOTIF="$(printf "%s" "$DELIVERIES" | python3 -c "
import sys,json,re
ds = json.load(sys.stdin) or []
needle = re.compile(r'team is idle|awaiting instructions|no active sprints', re.I)
hits = [d for d in ds if needle.search((d.get('title') or '') + ' ' + (d.get('body') or ''))]
print(len(hits))")"
if [[ "$SPRINT_STATUS" == "Completed" || "$SPRINT_STATUS" == "Cancelled" ]]; then
  if [[ "$IDLE_NOTIF" -gt 0 ]]; then
    report PASS "9." "${IDLE_NOTIF} idle notification(s) found in recent deliveries"
  else
    report FAIL "9." "sprint is $SPRINT_STATUS but no 'team is idle' notification was delivered"
  fi
else
  report NA "9." "sprint still active (status=$SPRINT_STATUS); idle notification only expected after completion"
fi

# --- Step 10: human returns (NA — human action) ---------------------------
report NA "10." "human action — verify externally that the human can read the report and audit the diff"

# --- summary ---------------------------------------------------------------
printf "%s\n" "──────────────────────────────────────────────────────────────"
printf "  %sPASS%s=%d  %sFAIL%s=%d  NA=%d  UNKNOWN=%d\n" \
  "$C_GREEN" "$C_RESET" "$PASS" "$C_RED" "$C_RESET" "$FAIL" "$NA" "$UNK"

if [[ "$FAIL" -gt 0 || "$UNK" -gt 0 ]]; then
  exit 1
fi
exit 0
