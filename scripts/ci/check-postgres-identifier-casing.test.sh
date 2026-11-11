#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/check-postgres-identifier-casing.sh"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEMP_ROOT"' EXIT

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

REPO="$TEMP_ROOT/repo"
MIGRATIONS="$REPO/Database.Postgres/migrations"
mkdir -p "$MIGRATIONS"

run_guard() {
  (cd "$REPO" && /bin/bash "$SCRIPT") > "$TEMP_ROOT/stdout" 2> "$TEMP_ROOT/stderr"
}

assert_pass() {
  if ! run_guard; then
    fail "$1 should pass, but guard exited nonzero"
  fi
  grep -q '^OK:' "$TEMP_ROOT/stdout" || fail "$1 did not report success"
  [ ! -s "$TEMP_ROOT/stderr" ] || fail "$1 wrote an unexpected scanner error"
}

assert_blocked() {
  if run_guard; then
    fail "$1 should be rejected"
  fi
  if grep -q '^OK:' "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
    fail "$1 printed success after a failed check"
  fi
  grep -q 'Identifier-casing-schending gevonden\|scannervereiste\|SQL-migratiebestand' \
    "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr" || fail "$1 did not report a blocking reason"
}

cat > "$MIGRATIONS/001_synthetic.sql" <<'SQL'
-- CREATE TABLE public.CommentedBadName (ignored_column TEXT);
CREATE   TABLE   IF NOT EXISTS   public.clean_table (
    clean_column VARCHAR(40),
    another_column CHARACTER VARYING (80),
    third_column DOUBLE PRECISION
);
ALTER TABLE public.clean_table
    ADD COLUMN IF NOT EXISTS added_column TIMESTAMPTZ;
SQL
assert_pass "clean, spaced, commented DDL with supported types"

cat > "$MIGRATIONS/001_synthetic.sql" <<'SQL'
CREATE TABLE IF NOT EXISTS public.MixedTable (
    clean_column TEXT
);
SQL
assert_blocked "mixed-case CREATE TABLE IF NOT EXISTS identifier"

cat > "$MIGRATIONS/001_synthetic.sql" <<'SQL'
CREATE TABLE public.clean_table (
    mixedColumn TEXT
);
SQL
assert_blocked "mixed-case inline column identifier"

cat > "$MIGRATIONS/001_synthetic.sql" <<'SQL'
ALTER TABLE public.clean_table ADD COLUMN IF NOT EXISTS mixedColumn BIGINT;
SQL
assert_blocked "mixed-case ALTER TABLE ADD COLUMN IF NOT EXISTS identifier"

cat > "$MIGRATIONS/001_synthetic.sql" <<'SQL'
CREATE TABLE public."quoted_lowercase" (
    clean_column TEXT
);
SQL
assert_blocked "quoted lowercase table identifier"

cat > "$MIGRATIONS/001_synthetic.sql" <<'SQL'
CREATE TABLE public.clean_table (
    "quoted_lowercase" TEXT
);
SQL
assert_blocked "quoted lowercase column identifier"

cat > "$MIGRATIONS/001_synthetic.sql" <<'SQL'
-- CREATE TABLE public.MixedTable (mixedColumn TEXT);
--     ADD COLUMN IF NOT EXISTS MixedAgain TEXT;
SQL
assert_pass "commented-out DDL is ignored"

EMPTY_REPO="$TEMP_ROOT/empty"
mkdir -p "$EMPTY_REPO/Database.Postgres/migrations"
if (cd "$EMPTY_REPO" && /bin/bash "$SCRIPT") > "$TEMP_ROOT/stdout" 2> "$TEMP_ROOT/stderr"; then
  fail "empty migration directory should be rejected"
fi
if grep -q '^OK:' "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
  fail "empty migration directory printed success"
fi
grep -q 'Geen SQL-migratiebestand' "$TEMP_ROOT/stderr" || fail "empty migration directory did not name the missing prerequisite"

MISSING_REPO="$TEMP_ROOT/missing"
mkdir -p "$MISSING_REPO"
if (cd "$MISSING_REPO" && /bin/bash "$SCRIPT") > "$TEMP_ROOT/stdout" 2> "$TEMP_ROOT/stderr"; then
  fail "missing migration directory should be rejected"
fi
if grep -q '^OK:' "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
  fail "missing migration directory printed success"
fi
grep -q 'Migratiemap ontbreekt' "$TEMP_ROOT/stderr" || fail "missing migration directory did not name the missing prerequisite"

# Simulate a missing scanner dependency without changing the host PATH or installing tools.
NO_AWK_PATH="$TEMP_ROOT/no-awk-bin"
mkdir -p "$NO_AWK_PATH"
if (cd "$REPO" && PATH="$NO_AWK_PATH" /bin/bash "$SCRIPT") > "$TEMP_ROOT/stdout" 2> "$TEMP_ROOT/stderr"; then
  fail "missing awk prerequisite should be rejected"
fi
if grep -q '^OK:' "$TEMP_ROOT/stdout" "$TEMP_ROOT/stderr"; then
  fail "missing awk prerequisite printed success"
fi
grep -q 'Scannervereiste ontbreekt: awk' "$TEMP_ROOT/stderr" || fail "missing awk did not report the missing scanner prerequisite"

echo "PASS: PostgreSQL identifier guard rejects casing violations and fails closed on missing prerequisites."
