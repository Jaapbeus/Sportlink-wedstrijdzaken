#!/usr/bin/env bash
# check-postgres-identifier-casing.sh (#864)
#
# Bewaakt de identifier-casing-conventie uit docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 3: de
# Postgres-boom gebruikt consequent lowercase snake_case voor eigen identifiers, nooit
# PascalCase/camelCase, en nooit een gequote naam — Postgres vouwt een ongequote identifier
# automatisch naar lowercase, dus een gequote naam bevriest een casing die bij de eerstvolgende
# ongequote referentie alweer niet meer matcht (empirisch bevestigd, zie dat document).
#
# scripts/ci/check-path-casing.sh (#825) bewaakt bestandsnaam-casing; dit script gaat over de
# identifiers BINNEN de migratiebestanden zelf (#864) — een ander risico.
#
# Scant Database.Postgres/migrations/*.sql op CREATE TABLE- en kolomdefinities. Geen database,
# geen secrets — draait ook op een fork.
set -euo pipefail

FOUT=0
TYPE_RE='(INTEGER|BIGINT|SMALLINT|VARCHAR|CHARACTER VARYING|TEXT|BOOLEAN|TIMESTAMPTZ|TIMESTAMP|DATE|TIME|DOUBLE PRECISION|NUMERIC|SERIAL|BIGSERIAL)'

meld_indien_ongeldig() {
  local f="$1" soort="$2" naam="$3"
  if [[ "$naam" == \"*\" ]]; then
    echo "::error file=$f::Gequote ${soort}naam '${naam}' — de Postgres-boom quote't nooit eigen identifiers (docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 3, #864)."
    FOUT=1
  elif [[ "$naam" =~ [A-Z] ]]; then
    echo "::error file=$f::${soort^}naam '${naam}' bevat een hoofdletter — de Postgres-boom gebruikt consequent lowercase snake_case (docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 3, #864)."
    FOUT=1
  fi
}

while IFS= read -r f; do
  # Tabelnamen uit CREATE TABLE [IF NOT EXISTS] <schema>.<naam>
  while IFS= read -r regel; do
    schema_naam=$(echo "$regel" | grep -ioP 'CREATE\s+TABLE\s+(IF\s+NOT\s+EXISTS\s+)?\K\S+')
    naam="${schema_naam##*.}"
    meld_indien_ongeldig "$f" "tabel" "$naam"
  done < <(grep -inP 'CREATE\s+TABLE' "$f" || true)

  # Kolomnamen: regel begint (evt. na 'ADD COLUMN [IF NOT EXISTS]') met een identifier, gevolgd
  # door een bekend Postgres-kolomtype. Dekt zowel inline CREATE TABLE-kolomdefinities als
  # losse ALTER TABLE ... ADD COLUMN-statements (allebei één kolom per regel in dit repo).
  while IFS= read -r kolom; do
    [ -z "$kolom" ] && continue
    meld_indien_ongeldig "$f" "kolom" "$kolom"
  done < <(grep -ioP "^\s*(ADD COLUMN(\s+IF NOT EXISTS)?\s+)?\K[\w\"]+(?=\s+${TYPE_RE}\b)" "$f" | sort -u)
done < <(find Database.Postgres/migrations -name '*.sql' -type f | sort)

if [ "$FOUT" -eq 1 ]; then
  echo "::error::Identifier-casing-schending gevonden in Database.Postgres/migrations/ (#864)."
  exit 1
fi

echo "OK: alle geïnspecteerde tabel- en kolomidentifiers in Database.Postgres/migrations/ zijn lowercase en ongequote."
