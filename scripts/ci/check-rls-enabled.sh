#!/usr/bin/env bash
# check-rls-enabled.sh (#1220, onderdeel van #1219)
#
# Dwingt de Supabase-regel (#1198) in AGENTS.md af: "elke nieuwe tabel in een
# Postgres-migratie krijgt in dezelfde migratie een ALTER TABLE ... ENABLE ROW LEVEL SECURITY".
#
# WAAROM DIT SCRIPT BESTAAT: die regel was tot nu toe door niets afgedwongen. Een migratie die het
# vergeet komt door élke bestaande check heen — er is geen bestand dat ontbreekt, geen kolom die
# mist, geen diff om op te reageren. Precies de vorm van het probleem dat #1198 beschrijft:
#
#   "Dit is geen codefout die aan een reviewer voorbij is gegaan — het is een *afwezigheid* die
#    nooit in de vorm van code heeft bestaan."  (docs/ARCHITECTUUR-DATABASE-TIERS.md §65)
#
# Zonder deze guard komt zo'n omissie pas aan het licht wanneer Supabase's Security Advisor hem in
# PRODUCTIE meldt. Bij #1198 duurde dat twaalf dagen. Deze guard ziet hem bij de PR.
#
# ── Reikwijdte: public, avg, planner, his, stg ──────────────────────────────────────────────────
# public/avg/planner: exact de schema's die migratie 021 dekt. Alleen `public` is standaard door
# Supabase via PostgREST ontsloten, maar 021 zette RLS bewust ook op `avg` en `planner` aan (defense
# in depth: de "Exposed schemas"-instelling is platformconfiguratie die geen diff in git achterlaat,
# dus de code mag er niet op vertrouwen dat die op `public` blijft staan).
#
# his/stg (#1698): die tabellen maakt geen migratie aan, maar PostgresSchemaGenerator tijdens de
# sync en Database.Postgres.Cli --ensure-his-tables bij de deploy (#818, #1060). Sinds #1698 zet de
# generator er zelf RLS op; daarvoor hing het af van Supabase's platformtrigger `ensure_rls`. Op een
# database met alleen migraties bestaan ze nog niet en telt deze guard ze dus niet; build.yml draait
# hem daarom ook na --ensure-his-tables (job fresh-db-postgres, stap #1698), inclusief een negatieve
# controle die bewijst dat de guard op een his-tabel zonder RLS rood wordt.

# ── Extensie-tabellen worden overgeslagen ───────────────────────────────────────────────────────
# Een tabel die bij een Postgres-extensie hoort (pg_depend deptype 'e') is niet van ons en mag niet
# gewijzigd worden. Supabase's eigen lint doet dezelfde uitsluiting; zonder deze filter zou een
# toekomstige extensie deze guard vals laten afgaan.
#
# ── Verbinding ──────────────────────────────────────────────────────────────────────────────────
# Via de standaard libpq-omgevingsvariabelen (PGHOST/PGPORT/PGUSER/PGPASSWORD/PGDATABASE), of met
# een conninfo-string als eerste argument. BEWUST NIET via POSTGRES_CONNECTION_STRING: dat is de
# Npgsql-keyword-vorm (Host=...;Username=...), die psql niet begrijpt. Een wachtwoord gaat nooit als
# argument mee — argumenten zijn op beide platforms zichtbaar in de processenlijst (zelfde regel als
# SQLCMDPASSWORD in AGENTS.md).
#
#   Lokaal:  PGHOST=localhost PGUSER=$POSTGRES_USER PGPASSWORD=... PGDATABASE=sportlink \
#              ./scripts/ci/check-rls-enabled.sh
#
# ── Draagbaarheid (#1155) ───────────────────────────────────────────────────────────────────────
# Moet identiek werken op de Linux-CI-runner (bash 5) en lokaal op macOS met /bin/bash 3.2. Dus
# geen associatieve arrays, geen mapfile, geen ${var,,}, en een lege array uitlezen onder `set -u`
# via de ${arr[@]+"${arr[@]}"}-vorm.
set -euo pipefail

CONNINFO="${1:-}"

psql_q() {
  if [ -n "$CONNINFO" ]; then
    psql "$CONNINFO" -t -A -v ON_ERROR_STOP=1 -c "$1"
  else
    psql -t -A -v ON_ERROR_STOP=1 -c "$1"
  fi
}

# ── UITZONDERINGEN ──────────────────────────────────────────────────────────────────────────────
# Formaat: "schema.tabel|reden". Elke regel hier is een bewuste, navolgbare beslissing.
#
# Vandaag is deze lijst LEEG, en dat hoort zo: migratie 021 en later dekken alle tabellen in
# public/avg/planner, en de generator dekt his/stg (#1698). Een nieuwe regel hier betekent dat een tabel bewust extern benaderbaar mag zijn via
# Supabase's PostgREST-API — dat is een CISO-beslissing, geen implementatiedetail. Motiveer hem
# hier én in docs/ARCHITECTUUR-DATABASE-TIERS.md, met issuenummer.
UITZONDERINGEN=()

# Eén plek voor de reikwijdte, zodat de controle- en de telquery niet uit de pas kunnen lopen.
SCHEMAS_SQL="'public','avg','planner','his','stg'"
SCHEMA_LABEL="public/avg/planner/his/stg"

echo "RLS-invariant controleren op schema's $SCHEMA_LABEL..."

ZONDER_RLS=$(psql_q "
  SELECT n.nspname || '.' || c.relname
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind IN ('r','p')
    AND n.nspname IN (${SCHEMAS_SQL})
    AND NOT c.relrowsecurity
    AND NOT EXISTS (
      SELECT 1 FROM pg_depend d
      WHERE d.objid = c.oid AND d.deptype = 'e'
    )
  ORDER BY 1;
")

# Uitzonderingen eruit filteren. Newline-gescheiden string als set + exacte-regel-lookup, conform
# de andere scripts in deze map (geen bash-4-constructies).
OVERTREDERS=""
for TABEL in $ZONDER_RLS; do
  TOEGESTAAN=0
  for REGEL in ${UITZONDERINGEN[@]+"${UITZONDERINGEN[@]}"}; do
    if [ "${REGEL%%|*}" = "$TABEL" ]; then
      echo "  overgeslagen (uitzondering): $TABEL — ${REGEL#*|}"
      TOEGESTAAN=1
      break
    fi
  done
  if [ "$TOEGESTAAN" -eq 0 ]; then
    OVERTREDERS="${OVERTREDERS}${TABEL}
"
  fi
done

OVERTREDERS=$(printf '%s' "$OVERTREDERS" | sed '/^$/d')

if [ -n "$OVERTREDERS" ]; then
  echo "::error::Tabellen zonder Row-Level Security (AGENTS.md, Supabase #1198): $(printf '%s' "$OVERTREDERS" | tr '\n' ' ')"
  echo ""
  echo "Zonder RLS is een tabel in een door Supabase ontsloten schema extern leesbaar, schrijfbaar"
  echo "en verwijderbaar via de automatisch gegenereerde PostgREST-API — met de bewust publieke"
  echo "anon-key, ongeacht of deze applicatie die API ooit gebruikt. Zie #1198 en"
  echo "docs/ARCHITECTUUR-DATABASE-TIERS.md §65."
  echo ""
  echo "Oplossing: voeg in DEZELFDE migratie als de CREATE TABLE toe (his/stg: in PostgresSchemaGenerator):"
  printf '%s\n' "$OVERTREDERS" | while IFS= read -r T; do
    [ -n "$T" ] && echo "    ALTER TABLE ${T} ENABLE ROW LEVEL SECURITY;"
  done
  echo ""
  echo "Policies zijn NIET nodig en NIET gewenst — zie de toelichting in migratie 021."
  exit 1
fi

AANTAL=$(psql_q "
  SELECT COUNT(*)
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind IN ('r','p')
    AND n.nspname IN (${SCHEMAS_SQL})
    AND c.relrowsecurity;
")

echo "RLS-invariant bevestigd: alle $AANTAL tabellen in $SCHEMA_LABEL hebben Row-Level Security aan."
