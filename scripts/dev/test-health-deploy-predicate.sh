#!/usr/bin/env bash
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
predicate="$root/scripts/dev/health-deploy-predicate.sh"

expect_pass() {
  local label="$1" json="$2"
  if ! printf '%s' "$json" | bash "$predicate" >/dev/null; then
    echo "FAIL: expected deploy gate pass: $label"
    exit 1
  fi
}
expect_fail() {
  local label="$1" json="$2"
  if printf '%s' "$json" | bash "$predicate" >/dev/null 2>&1; then
    echo "FAIL: expected deploy gate failure: $label"
    exit 1
  fi
}

expect_fail 'database offline' '{"database":"unavailable","settingsReady":true,"migrationsCurrent":true}'
expect_fail 'settings not ready' '{"database":"online","settingsReady":false,"migrationsCurrent":true}'
expect_fail 'migrations pending' '{"database":"online","settingsReady":true,"migrationsCurrent":false}'
expect_fail 'planner read path failing (#1911)' '{"database":"online","settingsReady":true,"migrationsCurrent":true,"readPathsOk":false}'
expect_pass 'planner read path ok' '{"database":"online","settingsReady":true,"migrationsCurrent":true,"readPathsOk":true}'
expect_pass 'planner read path not measured' '{"database":"online","settingsReady":true,"migrationsCurrent":true,"readPathsOk":null}'
expect_pass 'stale sync remains non-fatal' '{"status":"degraded","database":"online","settingsReady":true,"migrationsCurrent":true,"syncStale":true}'
expect_pass 'warning flags remain warnings' '{"database":"online","settingsReady":true,"migrationsCurrent":true,"schemaWarningPresent":true,"tlsWarningPresent":true}'
warning_output="$(printf '%s' '{"database":"online","settingsReady":true,"migrationsCurrent":true,"schemaWarningPresent":true,"tlsWarningPresent":true}' | bash "$predicate")"
case "$warning_output" in
  *"::warning title=Schemawaarschuwing"*"::warning title=TLS-beleid"*) ;;
  *) echo 'FAIL: warning flags did not produce both non-fatal workflow warnings.'; exit 1 ;;
esac
expect_pass 'unknown migrations are skipped' '{"database":"online","settingsReady":true,"migrationsCurrent":null}'
expect_pass 'legacy fields preserve old predicates' '{"database":"online","settingsLoaded":true,"pendingMigrations":[],"schemaWarning":"present","tlsWarning":"present"}'
expect_fail 'legacy pending migrations still fail' '{"database":"online","settingsLoaded":true,"pendingMigrations":["x"]}'
expect_pass 'legacy optional checks remain skipped' '{"status":"degraded"}'

echo 'OK: deploy health predicates preserve fatal and warning behavior.'
