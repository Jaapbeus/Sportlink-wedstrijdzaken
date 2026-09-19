#!/usr/bin/env bash
# check-tier-pariteit.sh (#1266)
#
# Bewaakt dat de twee database-tiers dezelfde HTTP-routes aanbieden. Beide tiers zijn volwaardig:
# een club die de SQL Server-tier kiest, hoort dezelfde functionaliteit te krijgen als een club op
# Postgres. Een endpoint dat maar op één tier bestaat, is een regressie.
#
# WAAROM DIT SCRIPT BESTAAT
# -------------------------
# Er bestonden al drie pariteitsguards — check-postgres-table-coverage.sh en twee zusters — maar
# die kijken maar ÉÉN KANT OP: staat elk SQL Server-object ook in Postgres. Ze zijn geschreven toen
# SQL Server de leidende tier was en Postgres de achterstand moest inhalen. Toen die rollen
# omdraaiden bij de cutover, is de richting van de guards niet meegedraaid. Sindsdien groeide de
# Postgres-tier vrij, zonder dat iets de SQL Server-tier bijhield.
#
# Het resultaat, gemeten op 2026-09-19: twaalf routes van de Sportlink Web Extension bestaan alleen
# op de Postgres-tier. Dat is niet als regressie opgemerkt maar als keuze vastgelegd — op basis van
# de premisse "de SQL Server-tier is rollback-only", die als beschrijving van de situatie na de
# cutover binnensloop en daarna als norm werd gebruikt. Die premisse is nooit als architectuurbesluit
# voorgelegd, en is inmiddels ingetrokken: beide tiers zijn gelijkwaardig (#1266).
#
# WAAROM ROUTES EN GEEN BESTANDSNAMEN
# -----------------------------------
# Een bestandsvergelijking geeft vals alarm: de Postgres-tier noemt zijn implementaties
# PostgresAppSettings.cs, PostgresStagingRepository.cs enzovoort, waar de SQL Server-tier andere
# namen gebruikt voor hetzelfde. Routes zijn het contract dat de buitenwereld ziet, en daarmee de
# enige vergelijking die betekent wat hij lijkt te betekenen.
#
# UITZONDERINGEN
# --------------
# scripts/ci/tier-pariteit-allowlist.txt, per route en met een verplichte reden. Elke regel daar is
# een openstaande schuld, geen vrijstelling: de bedoeling is dat het bestand leegloopt. Een regel
# zonder reden laat deze guard falen.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5, POSIX ERE.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

ALLOWLIST="scripts/ci/tier-pariteit-allowlist.txt"
[ -f "$ALLOWLIST" ] || { echo "::error::$ALLOWLIST ontbreekt — deze guard bewijst dan niets."; exit 1; }

if awk '!/^[[:space:]]*#/ && NF > 0 && NF < 3 { gevonden = 1; print "  regel " NR ": " $0 }
        END { exit gevonden ? 1 : 0 }' "$ALLOWLIST"; then
  :
else
  echo "::error file=$ALLOWLIST::Elke uitzondering heeft een reden nodig (<tier> <route> <reden>)."
  exit 1
fi

routes_van() {
  git grep -hoE 'Route *= *"[^"]*"|route: *"[^"]*"' -- "$1/**/*.cs" "$1/*.cs" 2>/dev/null \
    | sed 's/.*"\(.*\)"/\1/' | sort -u
}

sql="$(mktemp)"; pg="$(mktemp)"
trap 'rm -f "$sql" "$pg"' EXIT
routes_van FunctionApp            > "$sql"
routes_van FunctionApp.Postgres   > "$pg"

n_sql="$(grep -c '' "$sql" || true)"
n_pg="$(grep -c '' "$pg" || true)"
if [ "$n_sql" -eq 0 ] || [ "$n_pg" -eq 0 ]; then
  echo "::error::Geen routes gevonden in een van beide tiers (SQL Server: $n_sql, Postgres: $n_pg) — deze guard bewijst dan niets."
  exit 1
fi

# is_toegestaan <tier> <route>
is_toegestaan() {
  awk -v tier="$1" -v route="$2" '
    /^[[:space:]]*#/ || NF == 0 { next }
    $1 == tier && $2 == route { gevonden = 1 }
    END { exit gevonden ? 0 : 1 }
  ' "$ALLOWLIST"
}

fail=0
ontbreekt_sql=0
ontbreekt_pg=0

echo "Routes: SQL Server-tier $n_sql, Postgres-tier $n_pg."
echo

while IFS= read -r route; do
  [ -n "$route" ] || continue
  if ! is_toegestaan alleen-postgres "$route"; then
    echo "::error::Route '$route' bestaat alleen op de Postgres-tier. Beide tiers zijn gelijkwaardig (#1266) — bouw de SQL Server-tegenhanger in FunctionApp/, of zet de route met een reden in $ALLOWLIST."
    fail=1
  fi
  ontbreekt_sql=$((ontbreekt_sql + 1))
done < <(comm -13 "$sql" "$pg")

while IFS= read -r route; do
  [ -n "$route" ] || continue
  if ! is_toegestaan alleen-sqlserver "$route"; then
    echo "::error::Route '$route' bestaat alleen op de SQL Server-tier. Bouw de Postgres-tegenhanger in FunctionApp.Postgres/, of zet de route met een reden in $ALLOWLIST."
    fail=1
  fi
  ontbreekt_pg=$((ontbreekt_pg + 1))
done < <(comm -23 "$sql" "$pg")

openstaand="$(awk '!/^[[:space:]]*#/ && NF >= 3' "$ALLOWLIST" | grep -c '' || true)"
echo "Alleen op Postgres: $ontbreekt_sql route(s). Alleen op SQL Server: $ontbreekt_pg route(s)."
echo "Openstaande, gemotiveerde uitzonderingen in $ALLOWLIST: $openstaand."

if [ "$fail" -ne 0 ]; then
  exit 1
fi

if [ "$openstaand" -gt 0 ]; then
  echo "::notice::$openstaand route(s) staan nog als openstaande schuld in $ALLOWLIST. Dat bestand hoort leeg te lopen — haal een regel weg zodra de tegenhanger gebouwd is."
fi

echo
echo "OK — geen ongemotiveerd routeverschil tussen de tiers."
