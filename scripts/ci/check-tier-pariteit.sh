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
# Timerfuncties per tier. Apart van de routes, want een timer heeft geen route: hij is alleen
# zichtbaar als [Function("Naam")] met daaronder een [TimerTrigger(...)]-parameter. Python omdat
# dat "hoort dit TimerTrigger bij déze [Function]?" betrouwbaar kan beslissen; een grep-vensterhack
# zou bij een lange signatuur of een extra attribuut stilletjes de verkeerde kant op vallen.
timers_van() {
  git ls-files -- "$1/*.cs" | python3 -c '
import re, sys
namen = set()
for pad in (r.strip() for r in sys.stdin if r.strip()):
    try:
        tekst = open(pad, encoding="utf-8", errors="replace").read()
    except OSError:
        continue
    for m in re.finditer(r"\[Function\(\"([^\"]+)\"\)\]", tekst):
        # Alles tot aan het volgende [Function( hoort bij deze functie.
        rest = tekst[m.end():]
        volgende = rest.find("[Function(")
        blok = rest if volgende == -1 else rest[:volgende]
        if "TimerTrigger" in blok:
            namen.add(m.group(1))
for n in sorted(namen):
    print(n)
'
}

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

# ── Timers ──────────────────────────────────────────────────────────────────────────────────────
# Toegevoegd bij #1268. De guard keek tot dan alleen naar HTTP-routes, en zag daardoor niet dat de
# database-uitvalmonitor (#831) uitsluitend op de SQL Server-tier bestond — terwijl de Postgres-tier
# in productie draait. Een achtergrondtaak die maar op één tier bestaat is net zo goed een gat als
# een ontbrekend endpoint; hij is alleen minder zichtbaar, want niemand krijgt er een 404 van.
sql_t="$(mktemp)"; pg_t="$(mktemp)"
trap 'rm -f "$sql" "$pg" "$sql_t" "$pg_t"' EXIT
timers_van FunctionApp          > "$sql_t"
timers_van FunctionApp.Postgres > "$pg_t"

n_sql_t="$(grep -c '' "$sql_t" || true)"
n_pg_t="$(grep -c '' "$pg_t" || true)"
if [ "$n_sql_t" -eq 0 ] || [ "$n_pg_t" -eq 0 ]; then
  echo "::error::Geen timerfuncties gevonden in een van beide tiers (SQL Server: $n_sql_t, Postgres: $n_pg_t) — dat deel van deze guard bewijst dan niets."
  exit 1
fi

echo "Timers: SQL Server-tier $n_sql_t, Postgres-tier $n_pg_t."

while IFS= read -r timer; do
  [ -n "$timer" ] || continue
  if ! is_toegestaan alleen-postgres-timer "$timer"; then
    echo "::error::Timer '$timer' draait alleen op de Postgres-tier. Beide tiers zijn gelijkwaardig (#1266) — bouw de SQL Server-tegenhanger, of zet de timer met een reden in $ALLOWLIST."
    fail=1
  fi
done < <(comm -13 "$sql_t" "$pg_t")

while IFS= read -r timer; do
  [ -n "$timer" ] || continue
  if ! is_toegestaan alleen-sqlserver-timer "$timer"; then
    echo "::error::Timer '$timer' draait alleen op de SQL Server-tier. Bouw de Postgres-tegenhanger, of zet de timer met een reden in $ALLOWLIST."
    fail=1
  fi
done < <(comm -23 "$sql_t" "$pg_t")

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
echo "OK — geen ongemotiveerd route- of timerverschil tussen de tiers."
