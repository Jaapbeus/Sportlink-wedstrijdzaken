#!/usr/bin/env bash
# check-postgres-table-coverage.sh (#864, deel 2 — de "grootste deelopgave" die #908/deel 1 open liet)
#
# Vergelijkt de twee databasebomen op tabelniveau: elke tabel in de SQL Server-boom
# (Database/**/Tables/*.sql) moet een tegenhanger hebben op de Postgres-tier — óf via een
# CREATE TABLE in Database.Postgres/migrations/*.sql, óf via de drie dynamisch aangemaakte
# ETL-entiteiten uit Database.Postgres/KnownEntities.cs (#818) — óf staat expliciet en
# gemotiveerd in de EXCEPTIONS-lijst hieronder.
#
# Vertaalregel (empirisch afgeleid uit elke migratie die tot nu toe geschreven is, niet
# aangenomen): schema dbo -> public, elk ander schema ongewijzigd; tabelnaam PascalCase ->
# lowercase (docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 3). Dat is precies hoe elke bestaande
# migratie een SQL Server-tabelnaam heeft vertaald (AppSettings -> appsettings,
# TeamAliassen -> teamaliassen, GeplandeWedstrijden -> geplandewedstrijden, ...) — geen fuzzy
# matching nodig voor tabelnamen.
#
# Bewust NIET in dit script (zie docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 23/#864):
#   - Kolomniveau-vergelijking — een aanzienlijk grotere, apart te bouwen stap.
#   - Stored procedures/views — de Postgres-tier heeft geen procedures/views-bestanden op
#     dezelfde manier (#818/#861: procedurele logica leeft in C#), dus die vergelijking heeft
#     een ander karakter dan een bestandsvergelijking en hoort niet in dit tabellen-script.
#   - De omgekeerde richting (een Postgres-tabel zonder SQL Server-tegenhanger) — de Postgres-
#     boom is een vertaling VAN de SQL Server-boom; er is geen scenario waarin dat omgekeerd
#     zou moeten. Alleen "SQL Server heeft een tabel die Postgres mist" is een reëel risico.
#
# Geen database, geen secrets — draait ook op een fork.
#
# Draagbaarheid (#1155): dit script moet identiek werken op de Linux-CI-runner (bash 5, GNU grep)
# én lokaal op macOS met de standaard /bin/bash 3.2 en BSD grep/sed. Daarom géén `grep -P`,
# géén associatieve arrays (`declare -A`), geen `mapfile` en geen `${var,,}` — zie CLAUDE.md
# "Cross-platform scripts". Sets zijn newline-gescheiden strings met een exacte-regel-lookup.
set -euo pipefail

FOUT=0

# Lowercase zonder bash-4-syntax (`${var,,}` bestaat niet in bash 3.2).
lc() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

# Exacte-regel-lookup in een newline-gescheiden set: in_set <element> <set>.
in_set() { printf '%s\n' "$2" | grep -qxF -- "$1"; }

# ── EXCEPTIONS: SQL Server-tabellen die bewust (nog) geen Postgres-tegenhanger hebben ──────
# Formaat: "schema.tabel|reden". Elke rij hier is een bewuste beslissing, geen omissie — zie de
# reden en (waar van toepassing) het issue dat de rij ooit moet laten vervallen.
EXCEPTIONS=(
  "dbo.DateTable|zero consumenten binnen de applicatie (alleen de al vervallen pub.DateTable-view, issue 861); zie docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 21"
  "mta.source_target_mapping|architecturaal vervangen door Database.Postgres/KnownEntities.cs (#818) — geen stuurtabel nodig, de entiteitenlijst staat in C#"
)

# ── Dynamisch aangemaakte ETL-entiteiten (#818) — geen migratie, wel een echte tabel ───────
# PostgresMergeOrchestrator.EnsureHisTableAsync/RecreateStgTableAsync maakt deze aan op basis
# van Database.Postgres/KnownEntities.cs, net zoals sp_CreateTargetTableFromSource dat op de
# SQL Server-tier doet voor stg.*/his.* — geen statische DDL, dus geen CREATE TABLE-regel om
# hier te vinden. Hardcoded, met dezelfde bron als de SQL Server-tier se eigen allowlist in de
# schema-drift-guard hierboven in build.yml.
DYNAMISCH_AANGEMAAKT=("his.teams" "his.matches" "his.matchdetails" "stg.teams" "stg.matches" "stg.matchdetails")

is_uitzondering() {
  local obj="$1" e
  for e in "${EXCEPTIONS[@]}"; do
    [[ "${e%%|*}" == "$obj" ]] && return 0
  done
  for e in "${DYNAMISCH_AANGEMAAKT[@]}"; do
    [[ "$e" == "$obj" ]] && return 0
  done
  return 1
}

# ── Postgres-tabellenset opbouwen: elke CREATE TABLE [IF NOT EXISTS] <schema>.<naam> in elke
#    migratie, ongeacht volgorde — een tabel telt mee zodra hij ooit is aangemaakt. ──────────
# Eerst alles lowercase, dan pas de naam eruit knippen — zo is de sed-expressie hoofdletter-
# ongevoelig zonder de GNU-only `I`-vlag. De naam eindigt bij whitespace of een '(' (een
# `CREATE TABLE public.x(` zonder spatie zou anders het haakje meenemen en nooit matchen).
PG_TABLES="$(grep -rhiE 'CREATE[[:space:]]+TABLE' Database.Postgres/migrations/*.sql \
  | tr '[:upper:]' '[:lower:]' \
  | sed -nE 's/.*create[[:space:]]+table[[:space:]]+(if[[:space:]]+not[[:space:]]+exists[[:space:]]+)?([^[:space:](]+).*/\2/p')"

if [ -z "$PG_TABLES" ]; then
  echo "::error::Geen enkele CREATE TABLE geparseerd uit Database.Postgres/migrations/ — de parser in dit script is stuk, niet het schema. Een lege verzameling zou elke vergelijking hieronder ten onrechte laten slagen."
  exit 1
fi

# ── SQL Server-tabellen doorlopen, elke naam vertalen en tegen de Postgres-set + de
#    uitzonderingenlijst afzetten. ─────────────────────────────────────────────────────────
while IFS= read -r f; do
  # Laatste veld van de match is de objectnaam ("CREATE TABLE [dbo].[X]" -> "[dbo].[X]").
  obj=$(grep -ioE 'CREATE[[:space:]]+TABLE[[:space:]]+\[?[A-Za-z0-9_]+\]?\.\[?[A-Za-z0-9_]+' "$f" \
        | head -1 | awk '{print $NF}' | tr -d '[]')
  [ -z "$obj" ] && continue

  schema_naam="${obj%%.*}"
  tabel_naam="${obj##*.}"
  # Vertaalregel: dbo -> public, tabelnaam altijd lowercase.
  if [[ "$(lc "$schema_naam")" == "dbo" ]]; then
    verwacht_schema="public"
  else
    verwacht_schema="$(lc "$schema_naam")"
  fi
  verwacht="${verwacht_schema}.$(lc "$tabel_naam")"
  sqlserver_obj="${schema_naam}.${tabel_naam}"

  if is_uitzondering "$sqlserver_obj"; then
    continue
  fi

  if ! in_set "$verwacht" "$PG_TABLES"; then
    echo "::error file=$f::Tabel ${sqlserver_obj} (SQL Server-tier) heeft geen Postgres-tegenhanger ${verwacht} in Database.Postgres/migrations/, en staat niet in de EXCEPTIONS-lijst van dit script (#864)."
    FOUT=1
  fi
done < <(find Database -path '*/Tables/*.sql' -type f | sort)

if [ "$FOUT" -eq 1 ]; then
  echo "::error::Tabeldekking-schending gevonden tussen de SQL Server- en Postgres-boom (#864). Voeg de tabel toe aan een Postgres-migratie, of voeg hem — met reden — toe aan de EXCEPTIONS-lijst in dit script."
  exit 1
fi

echo "OK: elke tabel in de SQL Server-boom heeft een Postgres-tegenhanger, een dynamische ETL-tegenhanger (#818), of staat expliciet in de EXCEPTIONS-lijst (#864)."
