#!/usr/bin/env bash
# check-deploy-readiness.sh (#1810)
# Fail-closed source guards for production workflow ref and pre-publication job results.
set -euo pipefail

fail() {
  echo "::error::Production deployment preflight failed; no publication is authorized." >&2
  exit 1
}

MODE="${1:-}"
case "$MODE" in
  ref)
    [ "${DEPLOY_REF:-}" = "refs/heads/main" ] || fail
    echo "Production ref preflight passed."
    ;;
  ready)
    [ "${DEPLOY_REF:-}" = "refs/heads/main" ] || fail
    [ "${BUILD_RESULT:-}" = "success" ] || fail
    case "${DATABASE_TIER:-}" in
      SqlServer)
        [ "${DB_CHECK_RESULT:-}" = "success" ] || fail
        [ "${DB_MIGRATE_RESULT:-}" = "success" ] || fail
        [ "${DB_MIGRATE_POSTGRES_RESULT:-}" = "skipped" ] || fail
        ;;
      Postgres)
        [ "${DB_CHECK_RESULT:-}" = "skipped" ] || fail
        [ "${DB_MIGRATE_RESULT:-}" = "skipped" ] || fail
        [ "${DB_MIGRATE_POSTGRES_RESULT:-}" = "success" ] || fail
        ;;
      *)
        fail
        ;;
    esac
    echo "Production deployment readiness passed."
    ;;
  *)
    fail
    ;;
esac
