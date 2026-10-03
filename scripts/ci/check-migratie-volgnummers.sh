#!/usr/bin/env bash
# check-migratie-volgnummers.sh (#1485)
#
# Bewaakt dat elke Postgres-migratie in Database.Postgres/migrations/ een uniek volgnummer
# (numerieke voorvoegsel vóór de eerste underscore) heeft. Twee parallel gebouwde PR's kunnen
# elk hetzelfde volgnummer toevoegen (bijv. 034_pdf_switch.sql en 034_feedback_storage.sql);
# technisch werkt het omdat MigrationRunner op bestandsnaam sorteert, maar numerieke uniciteit
# hoort dwingend af te worden gehandhaafd.
#
# Accepteert optioneel een mapargument; standaard Database.Postgres/migrations.
# Exit 0 als alle volgnummers uniek en alle bestanden numeriek voorzien.
# Exit 1 als dubbelen of bestanden zonder numeric prefix.
#
# Draagbaarheid (#1155): bash 3.2-compatible. Geen mapfile, geen `declare -a`, geen
# `${var,,}`. Newline-gescheiden reeks als "set".

set -euo pipefail

migratie_dir="${1:-Database.Postgres/migrations}"
if [ ! -d "$migratie_dir" ]; then
  echo "::error::Mapargument '$migratie_dir' bestaat niet."
  exit 1
fi

# Verzamel volgnummers en bestandsnamen in newline-gescheiden format
entries=""
count=0
has_error=0

for filepath in $(find "$migratie_dir" -maxdepth 1 -name '*.sql' -type f | sort); do
  basename=$(basename "$filepath")
  # Extraheer volgnummer: führende digits vóór de eerste underscore
  # Bijv. "034_feedback.sql" → "034"
  nummer=$(printf '%s' "$basename" | grep -oE '^[0-9]+' || true)
  if [ -z "$nummer" ]; then
    echo "::error file=${filepath}::Migratiebestand '$(basename "$filepath")' heeft geen numeriek voorvoegsel (#1485)."
    has_error=1
    continue
  fi
  entries="${entries}${nummer}	${basename}"$'\n'
  count=$((count + 1))
done

if [ "$has_error" -eq 1 ]; then
  exit 1
fi

if [ "$count" -eq 0 ]; then
  echo "::error::Geen migratie .sql-bestanden gevonden in $migratie_dir"
  exit 1
fi

# Controleer op dubbele volgnummers: voor elk volgnummer, tel voorkomen
# Als een volgnummer meer dan eens voorkomt, rapporteer het
dups_raw=$(printf '%s' "$entries" | cut -f1 | sort | uniq -d || true)

if [ -n "$dups_raw" ]; then
  # Voor elke dubbele, toon welke bestanden ermee geassocieerd zijn
  printf '%s\n' "$dups_raw" | while IFS= read -r dup; do
    [ -z "$dup" ] && continue
    echo "::error::Volgnummer $dup komt meer dan eens voor in Database.Postgres/migrations/ (#1485):"
    printf '%s' "$entries" | while IFS=$'\t' read -r nr bestand; do
      if [ "$nr" = "$dup" ]; then
        echo "  - $bestand"
      fi
    done
  done
  exit 1
fi

echo "OK: alle ${count} migratievolgnummers in $(basename "$migratie_dir") zijn uniek."
