#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
GUARD="$ROOT/scripts/ci/check-deploy-readiness.sh"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEMP_ROOT"' EXIT

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

run_guard() {
  mode="$1"
  shift
  if env "$@" /bin/bash "$GUARD" "$mode" > "$TEMP_ROOT/stdout" 2> "$TEMP_ROOT/stderr"; then
    GUARD_STATUS=0
  else
    GUARD_STATUS=$?
  fi
}

assert_denied() {
  [ "$GUARD_STATUS" -ne 0 ] || fail "expected production preflight denial"
  grep -Fq 'Production deployment preflight failed; no publication is authorized.' \
    "$TEMP_ROOT/stderr" || fail "denial did not use the generic failure message"
  [ ! -s "$TEMP_ROOT/stdout" ] || fail "denial unexpectedly printed success output"
  for marker in 'feature/synthetic' 'DatabaseTier-unknown-synthetic' 'secrets'; do
    if grep -Fq -- "$marker" "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
      fail "preflight denial exposed input detail"
    fi
  done
}

assert_allowed() {
  [ "$GUARD_STATUS" -eq 0 ] || fail "expected production preflight success"
  grep -Fq "$1" "$TEMP_ROOT/stdout" || fail "success output was missing"
  [ ! -s "$TEMP_ROOT/stderr" ] || fail "success wrote diagnostics"
}

# A source-level main-ref gate rejects other refs and missing values.
run_guard ref DEPLOY_REF=refs/heads/main
assert_allowed 'Production ref preflight passed.'
run_guard ref DEPLOY_REF=refs/heads/feature/synthetic
assert_denied
run_guard ref DEPLOY_REF=
assert_denied

sql_good=(DEPLOY_REF=refs/heads/main DATABASE_TIER=SqlServer BUILD_RESULT=success \
  DB_CHECK_RESULT=success DB_MIGRATE_RESULT=success DB_MIGRATE_POSTGRES_RESULT=skipped)
pg_good=(DEPLOY_REF=refs/heads/main DATABASE_TIER=Postgres BUILD_RESULT=success \
  DB_CHECK_RESULT=skipped DB_MIGRATE_RESULT=skipped DB_MIGRATE_POSTGRES_RESULT=success)

# Both configured active-tier combinations pass, with only the inactive tier skipped.
run_guard ready "${sql_good[@]}"
assert_allowed 'Production deployment readiness passed.'
run_guard ready "${pg_good[@]}"
assert_allowed 'Production deployment readiness passed.'

# Wrong ref, missing/unknown tier, and failed build all fail closed.
run_guard ready "${sql_good[@]/refs\/heads\/main/refs\/heads\/feature\/synthetic}"
assert_denied
run_guard ready DEPLOY_REF=refs/heads/main DATABASE_TIER=DatabaseTier-unknown-synthetic \
  BUILD_RESULT=success DB_CHECK_RESULT=skipped DB_MIGRATE_RESULT=skipped DB_MIGRATE_POSTGRES_RESULT=skipped
assert_denied
run_guard ready DEPLOY_REF=refs/heads/main BUILD_RESULT=success DB_CHECK_RESULT=skipped \
  DB_MIGRATE_RESULT=skipped DB_MIGRATE_POSTGRES_RESULT=skipped
assert_denied
run_guard ready "${sql_good[@]/BUILD_RESULT=success/BUILD_RESULT=failure}"
assert_denied

# SQL Server requires the online check and its own migration; Postgres must be the skipped tier.
for result in skipped failure cancelled; do
  run_guard ready "${sql_good[@]/DB_CHECK_RESULT=success/DB_CHECK_RESULT=$result}"
  assert_denied
  run_guard ready "${sql_good[@]/DB_MIGRATE_RESULT=success/DB_MIGRATE_RESULT=$result}"
  assert_denied
done
run_guard ready "${sql_good[@]/DB_MIGRATE_POSTGRES_RESULT=skipped/DB_MIGRATE_POSTGRES_RESULT=success}"
assert_denied

# Postgres requires its own migration; SQL Server checks and migration must be skipped.
for result in skipped failure cancelled; do
  run_guard ready "${pg_good[@]/DB_MIGRATE_POSTGRES_RESULT=success/DB_MIGRATE_POSTGRES_RESULT=$result}"
  assert_denied
done
run_guard ready "${pg_good[@]/DB_CHECK_RESULT=skipped/DB_CHECK_RESULT=success}"
assert_denied
run_guard ready "${pg_good[@]/DB_MIGRATE_RESULT=skipped/DB_MIGRATE_RESULT=success}"
assert_denied

# Cancelled and otherwise non-success build results cannot authorize publication.
run_guard ready "${pg_good[@]/BUILD_RESULT=success/BUILD_RESULT=cancelled}"
assert_denied

# Missing statuses are rejected even when other values look valid.
run_guard ready DEPLOY_REF=refs/heads/main DATABASE_TIER=Postgres BUILD_RESULT=success \
  DB_CHECK_RESULT=skipped DB_MIGRATE_POSTGRES_RESULT=success
assert_denied

# Unknown invocation modes fail closed without revealing their argument.
run_guard invalid-mode DEPLOY_REF=refs/heads/main
assert_denied

echo "PASS: deploy preflight accepts only main with successful active-tier readiness and inactive-tier skips."
