#!/usr/bin/env bash
# Test voor check-migratie-volgnummers.sh (#1485). bash 3.2-proof.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)"
G=scripts/ci/check-migratie-volgnummers.sh
ok=0; bad=0

verwacht() { # naam verwachte-exit uitvoer-grep(optioneel) -- cmd...
  local naam="$1" exp="$2" pat="$3"; shift 4
  local out; out="$("$@" 2>&1)"; local rc=$?
  if [ "$rc" = "$exp" ] && { [ -z "$pat" ] || printf '%s' "$out" | grep -qE "$pat"; }; then
    echo "OK   $naam"; ok=$((ok+1))
  else
    echo "FAIL $naam (exit $rc, verwacht $exp)"; printf '%s\n' "$out"; bad=$((bad+1))
  fi
}

# Test-fixtures in temp-map
F=$(mktemp -d)
trap 'rm -rf "$F"' EXIT

# Test 1: geen bestanden — faalt gesloten
verwacht "lege map" 1 "Geen migratie" -- bash $G "$F"

# Test 2: unieke volgnummers — succeeds
touch "$F"/001_first.sql "$F"/002_second.sql "$F"/003_third.sql
verwacht "unieke nummers" 0 "^OK:" -- bash $G "$F"

# Test 3: dubbele 003 — faalt open
rm "$F"/*
touch "$F"/001_first.sql "$F"/003_one.sql "$F"/003_two.sql
verwacht "dubbel 003" 1 "003 komt meer dan eens voor" -- bash $G "$F"

# Test 4: bestand zonder prefix — faalt open
rm "$F"/*
touch "$F"/001_first.sql "$F"/no_prefix.sql
verwacht "geen prefix" 1 "geen numeriek voorvoegsel" -- bash $G "$F"

# Test 5: grote nummers — succeeds
rm "$F"/*
touch "$F"/034_pdf.sql "$F"/035_feedback.sql "$F"/036_andere.sql
verwacht "grote nummers" 0 "^OK:" -- bash $G "$F"

# Test 6: dubbele 034 — faalt open
rm "$F"/*
touch "$F"/034_first.sql "$F"/034_second.sql
verwacht "dubbel 034" 1 "034 komt meer dan eens voor" -- bash $G "$F"

# Test 7: standaardmap (Database.Postgres/migrations) — faalt omdat daar dubbel 003 zit
verwacht "standaardmap dubbel 003" 1 "003 komt meer dan eens voor" -- bash $G

echo ""
echo "geslaagd=$ok mislukt=$bad"
[ "$bad" = 0 ]
