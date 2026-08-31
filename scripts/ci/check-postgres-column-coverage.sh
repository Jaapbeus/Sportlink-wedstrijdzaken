#!/usr/bin/env bash
# check-postgres-column-coverage.sh (#864, deel 3 — het kolomniveau dat deel 2 (#917) openliet)
#
# check-postgres-table-coverage.sh bewijst dat elke SQL Server-TABEL een Postgres-tegenhanger
# heeft. Dat zegt niets over de KOLOMMEN daarin — en precies daar zijn binnen deze epic al twee
# echte gaten gevallen, beide pas gevonden toen iemand toevallig functionaliteit vertaalde die de
# kolom nodig had:
#   - #893: public.speeltijden miste WedstrijdHelft/WedstrijdRust/StandaardVoorkeurTijd.
#   - sectie 21: planner.geplandewedstrijden miste mta_modified.
#
# Dit script vergelijkt per tabel de kolomverzamelingen van de twee bomen:
#   SQL Server : Database/**/Tables/*.sql            (één CREATE TABLE per bestand)
#   Postgres   : Database.Postgres/migrations/*.sql  (CREATE TABLE + cumulatieve ALTER TABLE
#                                                     ... ADD COLUMN over meerdere migraties)
#
# Vertaalregel, identiek aan het tabellenscript en empirisch afgeleid uit elke bestaande migratie:
# schema dbo -> public (elk ander schema ongewijzigd), identifier -> lowercase
# (docs/ARCHITECTUUR-DATABASE-TIERS.md §3). Voor kolommen geldt diezelfde regel: elke migratie tot
# nu toe schrijft de SQL Server-kolomnaam letterlijk in lowercase over (ClubCode -> clubcode,
# StandaardVoorkeurTijd -> standaardvoorkeurtijd, ...).
#
# Richting: alleen "SQL Server heeft een kolom die Postgres mist". De omgekeerde richting is geen
# risico — de Postgres-boom is een vertaling VAN de SQL Server-boom, nooit andersom (zelfde
# redenering als het tabellenscript).
#
# Bewust NIET in dit script:
#   - Kolom-TYPEN en nullability. Die hebben, anders dan namen, géén 1-op-1-vertaalregel
#     (NVARCHAR->VARCHAR/TEXT, BIT->BOOLEAN, DATETIME2->TIMESTAMPTZ, DECIMAL->DECIMAL, en per
#     kolom een bewuste afweging). Een naamvergelijking dekt de twee gaten hierboven volledig af;
#     een typevergelijking zou een eigen vertaaltabel vergen en is een aparte opgave.
#   - De zes dynamisch aangemaakte ETL-tabellen (his.*/stg.*). Die hebben geen CREATE TABLE in een
#     migratie: PostgresSchemaGenerator bouwt ze uit Database.Postgres/KnownEntities.cs (#818).
#     Hun kolomdekking wordt bewaakt door Database.Postgres.Tests/EtlKolomdekkingTests.cs, die de
#     ECHTE generator-output vergelijkt met dezelfde SQL Server-bestanden — sterker dan wat dit
#     script kan, want dat zou de C#-lijst opnieuw moeten parseren.
#
# Geen database, geen secrets — draait ook op een fork.
set -euo pipefail

FOUT=0

# ── Tabellen die dit script overslaat, met de reden en waar ze wél gedekt worden ────────────
# Formaat: "schema.tabel|reden". Twee categorieën:
#   (a) geen Postgres-tegenhanger — al gedekt door check-postgres-table-coverage.sh, dat de
#       EXCEPTIONS-lijst met de motivatie per tabel bijhoudt. Hier alleen overslaan.
#   (b) dynamisch aangemaakt — gedekt door EtlKolomdekkingTests.cs (zie kop).
OVERGESLAGEN_TABELLEN=(
  "dbo.DateTable|geen Postgres-tegenhanger (zie EXCEPTIONS in check-postgres-table-coverage.sh)"
  "dbo.KnvbKalenderDag|geen Postgres-tegenhanger (idem)"
  "dbo.Zonsondergang|geen Postgres-tegenhanger (idem)"
  "planner.HerplanVerzoeken|geen Postgres-tegenhanger (idem)"
  "mta.source_target_mapping|geen Postgres-tegenhanger (idem)"
  "his.Teams|dynamisch aangemaakt uit KnownEntities.cs — gedekt door Database.Postgres.Tests/EtlKolomdekkingTests.cs"
  "his.Matches|idem"
  "his.MatchDetails|idem"
  "stg.Teams|idem"
  "stg.Matches|idem"
  "stg.MatchDetails|idem"
)

# ── Kolommen die bewust (nog) geen Postgres-tegenhanger hebben ─────────────────────────────
# Formaat: "schema.tabel.kolom|reden". Elke rij is een bewuste beslissing met een issuenummer,
# geen omissie. Een rij hier verdwijnt zodra de bijbehorende functionaliteit vertaald is.
KOLOM_UITZONDERINGEN=(
  "planner.GeplandeWedstrijden.WedstrijdDuurMinuten|hoort bij BevestigWedstrijd, nog niet vertaald (issue 888); zie docs/ARCHITECTUUR-DATABASE-TIERS.md §21"
  "planner.GeplandeWedstrijden.AangevraagdDoor|idem (issue 888)"
  "planner.GeplandeWedstrijden.Opmerking|idem (issue 888)"
  "planner.GeplandeWedstrijden.mta_inserted|idem (issue 888) — alleen mta_modified was nodig voor MarkeerVervallenGeplandeWedstrijdenAsync, §21"
)

# Casing-ongevoelig: de SQL Server-bestanden zijn zelf niet consistent — Database/his/Tables/
# MatchDetails.sql declareert bijvoorbeeld [his].[matchdetails] (lowercase) terwijl het bestand
# PascalCase heet. Een casing-gevoelige vergelijking zou zo'n rij stil laten missen.
is_overgeslagen_tabel() {
  local obj="${1,,}" e
  for e in "${OVERGESLAGEN_TABELLEN[@]}"; do
    local sleutel="${e%%|*}"
    [[ "${sleutel,,}" == "$obj" ]] && return 0
  done
  return 1
}

is_kolom_uitzondering() {
  local obj="${1,,}" e
  for e in "${KOLOM_UITZONDERINGEN[@]}"; do
    local sleutel="${e%%|*}"
    [[ "${sleutel,,}" == "$obj" ]] && return 0
  done
  return 1
}

# ── Parser 1: kolommen per tabel uit de Postgres-migraties ─────────────────────────────────
# Levert regels "schema.tabel kolom" (alles lowercase). Verwerkt zowel CREATE TABLE-bodies als
# de cumulatieve ALTER TABLE ... ADD COLUMN-blokken; de laatste kunnen over meerdere migraties
# verspreid staan (een kolom telt mee zodra hij ooit is toegevoegd).
pg_kolommen() {
  awk '
    function strip(s) { sub(/--.*/, "", s); return s }
    BEGIN { in_create = 0; alter_tabel = "" }
    {
      regel = strip($0)

      # ── ALTER TABLE <schema>.<tabel> — blok loopt tot de afsluitende puntkomma ──
      if (regel ~ /^[ \t]*ALTER[ \t]+TABLE[ \t]/) {
        if (match(regel, /ALTER[ \t]+TABLE[ \t]+[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*/)) {
          t = substr(regel, RSTART, RLENGTH)
          sub(/ALTER[ \t]+TABLE[ \t]+/, "", t)
          alter_tabel = tolower(t)
        }
      }
      if (alter_tabel != "" && regel ~ /ADD[ \t]+COLUMN/) {
        r = regel
        sub(/.*ADD[ \t]+COLUMN[ \t]+/, "", r)
        sub(/^IF[ \t]+NOT[ \t]+EXISTS[ \t]+/, "", r)
        if (match(r, /^[A-Za-z_][A-Za-z0-9_]*/)) {
          print alter_tabel " " tolower(substr(r, RSTART, RLENGTH))
        }
      }
      if (alter_tabel != "" && regel ~ /;/) { alter_tabel = "" }

      # ── CREATE TABLE [IF NOT EXISTS] <schema>.<tabel> ( ... ) ──
      if (in_create == 0 && regel ~ /CREATE[ \t]+TABLE/) {
        if (match(regel, /CREATE[ \t]+TABLE[ \t]+(IF[ \t]+NOT[ \t]+EXISTS[ \t]+)?[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*/)) {
          t = substr(regel, RSTART, RLENGTH)
          sub(/CREATE[ \t]+TABLE[ \t]+/, "", t)
          sub(/^IF[ \t]+NOT[ \t]+EXISTS[ \t]+/, "", t)
          create_tabel = tolower(t)
          in_create = 1
        }
        next
      }
      if (in_create == 1) {
        if (regel ~ /^[ \t]*\)/) { in_create = 0; next }
        # Constraintregels zijn geen kolommen.
        if (regel ~ /^[ \t]*(PRIMARY|UNIQUE|CONSTRAINT|CHECK|FOREIGN|EXCLUDE)[ \t]/) next
        if (match(regel, /^[ \t]*[A-Za-z_][A-Za-z0-9_]*/)) {
          k = substr(regel, RSTART, RLENGTH)
          gsub(/[ \t]/, "", k)
          if (k != "") print create_tabel " " tolower(k)
        }
      }
    }
  ' Database.Postgres/migrations/*.sql
}

# ── Parser 2: kolommen van één SQL Server-tabelbestand ─────────────────────────────────────
# Levert de kolomnamen zoals ze in het bestand staan (originele casing — die is nodig voor een
# leesbare foutmelding en voor de KOLOM_UITZONDERINGEN-sleutel).
sqlserver_kolommen() {
  awk '
    function strip(s) { sub(/--.*/, "", s); return s }
    BEGIN { in_create = 0 }
    {
      regel = strip($0)
      if (in_create == 0) {
        if (regel ~ /CREATE[ \t]+TABLE/) in_create = 1
        next
      }
      if (regel ~ /^[ \t]*\)/) { in_create = 0; next }
      if (regel ~ /^[ \t]*(CONSTRAINT|PRIMARY|UNIQUE|CHECK|FOREIGN|INDEX)[ \t]/) next
      # Kolomnaam: eerste token op de regel, met of zonder blokhaken.
      if (match(regel, /^[ \t]*\[?[A-Za-z_][A-Za-z0-9_]*\]?/)) {
        k = substr(regel, RSTART, RLENGTH)
        gsub(/[ \t\[\]]/, "", k)
        if (k != "") print k
      }
    }
  ' "$1"
}

# ── Postgres-kolomverzameling opbouwen ─────────────────────────────────────────────────────
# Eerst tellen, dan pas vullen: onder `set -u` is ${#ARR[@]} op een nog lege associatieve array
# in oudere bash-versies zelf een fout, en een fout die "unbound variable" heet verbergt precies
# de bevinding die deze guard moet melden.
declare -A PG_KOLOMMEN
PG_AANTAL=0
while read -r tabel kolom; do
  [ -z "${tabel:-}" ] && continue
  PG_KOLOMMEN["${tabel}.${kolom}"]=1
  PG_AANTAL=$((PG_AANTAL + 1))
done < <(pg_kolommen)

if [ "$PG_AANTAL" -eq 0 ]; then
  echo "::error::Geen enkele Postgres-kolom geparseerd uit Database.Postgres/migrations/ — de parser in dit script is stuk, niet het schema. Een lege verzameling zou elke vergelijking hieronder ten onrechte laten slagen."
  exit 1
fi

# ── Vergelijken ────────────────────────────────────────────────────────────────────────────
GECONTROLEERD=0
KOLOMMEN_VERGELEKEN=0
while IFS= read -r f; do
  obj=$(grep -ioE 'CREATE[ ]+TABLE[ ]+\[?[A-Za-z_]+\]?\.\[?[A-Za-z_]+\]?' "$f" | head -1 \
        | sed -E 's/CREATE[ ]+TABLE[ ]+//I' | tr -d '[]')
  if [ -z "$obj" ]; then
    echo "::error file=$f::Geen CREATE TABLE <schema>.<tabel> herkend in dit bestand — de parser in dit script is stuk, of het bestand wijkt af van de conventie."
    FOUT=1
    continue
  fi

  schema_naam="${obj%%.*}"
  tabel_naam="${obj##*.}"

  if is_overgeslagen_tabel "${schema_naam}.${tabel_naam}"; then
    continue
  fi

  if [[ "${schema_naam,,}" == "dbo" ]]; then
    pg_schema="public"
  else
    pg_schema="${schema_naam,,}"
  fi
  pg_tabel="${pg_schema}.${tabel_naam,,}"

  # Elke SQL Server-tabel die hier langskomt MOET kolommen opleveren; nul kolommen betekent een
  # kapotte parser, niet een lege tabel. Zonder deze controle zou zo'n bestand stilzwijgend
  # slagen — de klassieke "nul asserties = groen"-val.
  mapfile -t kolommen < <(sqlserver_kolommen "$f")
  if [ "${#kolommen[@]}" -eq 0 ]; then
    echo "::error file=$f::Nul kolommen geparseerd uit ${schema_naam}.${tabel_naam} — de parser in dit script is stuk. Een tabel zonder kolommen bestaat niet."
    FOUT=1
    continue
  fi

  GECONTROLEERD=$((GECONTROLEERD + 1))
  for kolom in "${kolommen[@]}"; do
    if is_kolom_uitzondering "${schema_naam}.${tabel_naam}.${kolom}"; then
      continue
    fi
    KOLOMMEN_VERGELEKEN=$((KOLOMMEN_VERGELEKEN + 1))
    if [ -z "${PG_KOLOMMEN["${pg_tabel}.${kolom,,}"]+x}" ]; then
      echo "::error file=$f::Kolom ${schema_naam}.${tabel_naam}.${kolom} (SQL Server-tier) heeft geen tegenhanger ${pg_tabel}.${kolom,,} in Database.Postgres/migrations/, en staat niet in de KOLOM_UITZONDERINGEN-lijst van dit script (#864)."
      FOUT=1
    fi
  done
done < <(find Database -path '*/Tables/*.sql' -type f | sort)

if [ "$GECONTROLEERD" -eq 0 ]; then
  echo "::error::Nul tabellen daadwerkelijk vergeleken — dit script bewijst dan niets. Controleer de OVERGESLAGEN_TABELLEN-lijst en het zoekpad."
  exit 1
fi

if [ "$FOUT" -eq 1 ]; then
  echo "::error::Kolomdekking-schending gevonden tussen de SQL Server- en Postgres-boom (#864). Voeg de kolom toe aan een Postgres-migratie, of voeg hem — met reden en issuenummer — toe aan de KOLOM_UITZONDERINGEN-lijst in dit script."
  exit 1
fi

echo "OK: ${GECONTROLEERD} tabellen en ${KOLOMMEN_VERGELEKEN} kolommen vergeleken; elke SQL Server-kolom heeft een Postgres-tegenhanger of staat expliciet in de KOLOM_UITZONDERINGEN-lijst (#864)."
