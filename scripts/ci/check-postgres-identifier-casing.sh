#!/usr/bin/env bash
# check-postgres-identifier-casing.sh (#864)
#
# Bewaakt lowercase snake_case en ongequote identifiers in Postgres-migraties. Dit script gebruikt
# alleen POSIX-awk/shellfuncties zodat het ook op Bash 3.2 en BSD grep werkt.
set -euo pipefail

MIGRATION_DIR="Database.Postgres/migrations"
FOUT=0

fail_closed() {
  echo "::error::$1" >&2
  exit 1
}

for tool in awk find sort; do
  command -v "$tool" >/dev/null 2>&1 || fail_closed "Scannervereiste ontbreekt: $tool."
done

[ -d "$MIGRATION_DIR" ] || fail_closed "Migratiemap ontbreekt: Database.Postgres/migrations/."
if ! MIGRATION_FILES=$(find "$MIGRATION_DIR" -type f -name '*.sql' -print 2>/dev/null | sort); then
  fail_closed "SQL-migratiebestanden konden niet worden geïnventariseerd."
fi
[ -n "$MIGRATION_FILES" ] || fail_closed "Geen SQL-migratiebestand gevonden in Database.Postgres/migrations/."

meld_indien_ongeldig() {
  local f="$1" soort="$2" naam="$3" soortnaam soortnaam_lower
  case "$soort" in
    tabel) soortnaam="Tabel"; soortnaam_lower="tabel" ;;
    kolom) soortnaam="Kolom"; soortnaam_lower="kolom" ;;
    *) fail_closed "Interne scannerfout: onbekend identifier-type." ;;
  esac

  if [[ "$naam" == \"* ]]; then
    echo "::error file=$f::Gequote ${soortnaam_lower}naam '${naam}' — eigen Postgres-identifiers blijven ongequote." >&2
    FOUT=1
  elif [[ "$naam" =~ [A-Z] ]]; then
    echo "::error file=$f::${soortnaam}naam '${naam}' bevat een hoofdletter — gebruik lowercase snake_case." >&2
    FOUT=1
  fi
}

# Extract the table token after CREATE TABLE [IF NOT EXISTS], preserving identifier spelling.
lees_tabelidentifiers() {
  awk '
    {
      regel = $0
      sub(/--.*/, "", regel)
      sub(/^[[:space:]]+/, "", regel)
      aantal = split(regel, deel, /[[:space:]]+/)
      for (i = 1; i < aantal; i++) {
        if (tolower(deel[i]) != "create" || tolower(deel[i + 1]) != "table") continue
        naam_index = i + 2
        if (tolower(deel[naam_index]) == "if" && tolower(deel[naam_index + 1]) == "not" && tolower(deel[naam_index + 2]) == "exists") naam_index += 3
        if (naam_index > aantal || deel[naam_index] == "") {
          exit 2
        }
        naam = deel[naam_index]
        sub(/[(),;]+$/, "", naam)
        sub(/^.*\./, "", naam)
        print naam
      }
    }
  ' "$1"
}

# Extract only column definitions whose following type belongs to the guard's existing type set.
lees_kolomidentifiers() {
  awk '
    function kolomtype(i, type) {
      type = toupper($i)
      sub(/\(.*/, "", type)
      sub(/[(),;]+$/, "", type)
      if (type == "INTEGER" || type == "BIGINT" || type == "SMALLINT" ||
          type == "VARCHAR" || type == "TEXT" || type == "BOOLEAN" ||
          type == "TIMESTAMPTZ" || type == "TIMESTAMP" || type == "DATE" ||
          type == "TIME" || type == "NUMERIC" || type == "SERIAL" ||
          type == "BIGSERIAL") return 1
      if ((type == "CHARACTER" && toupper($(i + 1)) == "VARYING") ||
          (type == "DOUBLE" && toupper($(i + 1)) == "PRECISION")) return 1
      return 0
    }
    {
      regel = $0
      sub(/--.*/, "", regel)
      sub(/^[[:space:]]+/, "", regel)
      if (regel == "" || regel ~ /^\/\*/) next
      aantal = split(regel, deel, /[[:space:]]+/)
      naam_index = 1
      for (i = 1; i < aantal; i++) {
        if (tolower(deel[i]) == "add" && tolower(deel[i + 1]) == "column") {
          naam_index = i + 2
          if (tolower(deel[naam_index]) == "if" && tolower(deel[naam_index + 1]) == "not" && tolower(deel[naam_index + 2]) == "exists") naam_index += 3
          break
        }
      }
      if (naam_index < aantal && kolomtype(naam_index + 1)) {
        naam = deel[naam_index]
        sub(/[(),;]+$/, "", naam)
        print naam
      }
    }
  ' "$1"
}

while IFS= read -r f; do
  [ -z "$f" ] && continue

  if ! tabelnamen=$(lees_tabelidentifiers "$f"); then
    fail_closed "Tabelidentifiers konden niet worden gecontroleerd."
  fi
  while IFS= read -r naam; do
    [ -z "$naam" ] && continue
    meld_indien_ongeldig "$f" "tabel" "$naam"
  done <<< "$tabelnamen"

  if ! kolomnamen=$(lees_kolomidentifiers "$f"); then
    fail_closed "Kolomidentifiers konden niet worden gecontroleerd."
  fi
  while IFS= read -r naam; do
    [ -z "$naam" ] && continue
    meld_indien_ongeldig "$f" "kolom" "$naam"
  done <<< "$(printf '%s\n' "$kolomnamen" | sort -u)"
done <<< "$MIGRATION_FILES"

if [ "$FOUT" -eq 1 ]; then
  echo "::error::Identifier-casing-schending gevonden in Database.Postgres/migrations/ (#864)." >&2
  exit 1
fi

echo "OK: alle geïnspecteerde tabel- en kolomidentifiers in Database.Postgres/migrations/ zijn lowercase en ongequote."
