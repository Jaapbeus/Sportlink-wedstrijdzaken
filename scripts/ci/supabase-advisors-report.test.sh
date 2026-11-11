#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/supabase-advisors-report.sh"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEMP_ROOT"' EXIT

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

ADVISORS="$TEMP_ROOT/advisors.json"
BASELINE="$TEMP_ROOT/baseline.json"
BODY="$TEMP_ROOT/body.md"
SUMMARY="$TEMP_ROOT/step-summary.md"
STDOUT="$TEMP_ROOT/stdout.txt"
STDERR="$TEMP_ROOT/stderr.txt"

cat > "$ADVISORS" <<'JSON'
[
  {"level":"ERROR","name":"SYNTHETIC_RULE_NAME_A","title":"SYNTHETIC_TITLE_A","detail":"SYNTHETIC_OBJECT_MARKER_A","remediation":"SYNTHETIC_REMEDIATION_A","cache_key":"SYNTHETIC_PRIVATE_KEY_A"},
  {"level":"ERROR","name":"SYNTHETIC_RULE_NAME_B","title":"SYNTHETIC_TITLE_B","detail":"SYNTHETIC_OBJECT_MARKER_B","remediation":"SYNTHETIC_REMEDIATION_B","cache_key":"SYNTHETIC_PRIVATE_KEY_B"},
  {"level":"WARN","name":"SYNTHETIC_RULE_NAME_C","title":"SYNTHETIC_TITLE_C","detail":"SYNTHETIC_OBJECT_MARKER_C","remediation":"SYNTHETIC_REMEDIATION_C","cache_key":"SYNTHETIC_PRIVATE_KEY_C"},
  {"level":"INFO","name":"SYNTHETIC_RULE_NAME_D","title":"SYNTHETIC_TITLE_D","detail":"SYNTHETIC_OBJECT_MARKER_D","remediation":"SYNTHETIC_REMEDIATION_D","cache_key":"SYNTHETIC_PRIVATE_KEY_D"},
  {"level":"SYNTHETIC_LEVEL_MARKER","name":"SYNTHETIC_RULE_NAME_E","title":"SYNTHETIC_TITLE_E","detail":"SYNTHETIC_OBJECT_MARKER_E","remediation":"SYNTHETIC_REMEDIATION_E","cache_key":"SYNTHETIC_PRIVATE_KEY_E"},
  {"level":"ERROR","name":"SYNTHETIC_ACCEPTED_RULE","title":"SYNTHETIC_ACCEPTED_TITLE","detail":"SYNTHETIC_ACCEPTED_DETAIL","remediation":"SYNTHETIC_ACCEPTED_REMEDIATION","cache_key":"SYNTHETIC_PRIVATE_KEY_ACCEPTED"}
]
JSON
printf '{"geaccepteerd":[{"cache_key":"SYNTHETIC_PRIVATE_KEY_ACCEPTED","reden":"SYNTHETIC_PRIVATE_REASON"}]}' > "$BASELINE"

"$SCRIPT" "$ADVISORS" "$BASELINE" "$BODY" > "$STDOUT" 2> "$STDERR" \
  || fail "valid synthetic advisor input was rejected"
{
  echo "# Supabase-advisors"
  echo ""
  echo "**$(cat "$BODY.count")** nieuwe bevinding(en) buiten de baseline."
  echo ""
  cat "$BODY"
} > "$SUMMARY"

EXPECTED_BODY=$(cat <<'EXPECTED'
| Ernstklasse | Aantal |
|---|---:|
| ERROR | 2 |
| INFO | 1 |
| OTHER | 1 |
| WARN | 1 |
EXPECTED
)
[ "$(cat "$BODY")" = "$EXPECTED_BODY" ] || fail "public issue body did not match the class-only report"
[ "$(cat "$SUMMARY")" = "# Supabase-advisors

**5** nieuwe bevinding(en) buiten de baseline.

$EXPECTED_BODY" ] || fail "workflow step summary did not match the count-only report"
[ "$(cat "$BODY.count")" = "5" ] || fail "count output did not preserve all unmatched findings"

if grep -E 'SYNTHETIC_(RULE_NAME|TITLE|OBJECT_MARKER|REMEDIATION|PRIVATE_KEY|PRIVATE_REASON|ACCEPTED_|LEVEL_MARKER)' \
    "$BODY" "$SUMMARY" "$STDOUT" "$STDERR" >/dev/null; then
  fail "synthetic advisor detail escaped to a public sink or log"
fi

# A fully accepted synthetic input remains a successful empty report.
printf '[{"level":"ERROR","cache_key":"SYNTHETIC_PRIVATE_KEY_ACCEPTED"}]' > "$ADVISORS"
"$SCRIPT" "$ADVISORS" "$BASELINE" "$BODY" > "$STDOUT" 2> "$STDERR" \
  || fail "fully accepted synthetic input was rejected"
[ ! -s "$BODY" ] || fail "fully accepted input produced a public report"
[ "$(cat "$BODY.count")" = "0" ] || fail "fully accepted input did not report zero new findings"

# Invalid input must fail closed without echoing payload content or leaving a publishable body.
printf '{"broken":"SYNTHETIC_ERROR_PAYLOAD_MARKER"' > "$ADVISORS"
rm -f "$BODY" "$BODY.count"
if "$SCRIPT" "$ADVISORS" "$BASELINE" "$BODY" > "$STDOUT" 2> "$STDERR"; then
  fail "malformed advisor JSON was accepted"
fi
if grep -q 'SYNTHETIC_ERROR_PAYLOAD_MARKER' "$STDOUT" "$STDERR"; then
  fail "malformed payload content appeared in error output"
fi
[ ! -e "$BODY" ] || fail "malformed input left a publishable issue body"
[ ! -e "$BODY.count" ] || fail "malformed input left a count for publication"

printf '[{"cache_key":"SYNTHETIC_ERROR_PAYLOAD_MARKER"}]' > "$ADVISORS"
printf '{"invalid":"SYNTHETIC_BASELINE_ERROR_MARKER"}' > "$BASELINE"
if "$SCRIPT" "$ADVISORS" "$BASELINE" "$BODY" > "$STDOUT" 2> "$STDERR"; then
  fail "malformed baseline JSON was accepted"
fi
if grep -E 'SYNTHETIC_(ERROR_PAYLOAD|BASELINE_ERROR)_MARKER' "$STDOUT" "$STDERR"; then
  fail "malformed input content appeared in error output"
fi
[ ! -e "$BODY" ] || fail "malformed baseline left a publishable issue body"
[ ! -e "$BODY.count" ] || fail "malformed baseline left a count for publication"

echo "PASS: advisor report and summary expose only severity-class counts; malformed input fails closed."
