#!/usr/bin/env bash
# check-interne-duplicatie.sh (#1263)
#
# Meet duplicatie BINNEN één boom — wat check-tier-duplicatie.sh niet ziet, omdat die alleen
# paren tussen FunctionApp/ en FunctionApp.Postgres/ vergelijkt. Twee identieke methodes in
# hetzelfde bestand, of in twee bestanden binnen dezelfde tier (of in Planner.Shared/,
# BlazorAdmin/, Database.Postgres/, ...), komen daar niet in voor.
#
# WAAROM FunctionApp/ VOLLEDIG UITGESLOTEN IS
# --------------------------------------------
# Een scan over alle productiecode zou de tier-paren die check-tier-duplicatie.sh al meet gewoon
# nog een keer tellen — twee guards die op dezelfde ~5.300 regels reageren, waarvan de ene bij een
# tier-poort alleen kan zeggen "verhoog het andere plafond ook maar". Door FunctionApp/ (de SQL
# Server-tier) volledig uit te sluiten, blijft precies over wat de tier-guard niet ziet:
# duplicatie binnen FunctionApp.Postgres/, Planner.Shared/, BlazorAdmin/ en de overige
# productiemappen. Zie issue #1271 voor de vraag of de FunctionApp.Postgres/-kant van diezelfde
# duplicatie ooit naar een gedeelde laag moet — dat is een aparte afweging.
#
# WAAROM ALLEEN PRODUCTIECODE
# ----------------------------
# Gemeten op #1263: 18,5% duplicatie in productiecode tegenover 16,6% over alle C#-code inclusief
# tests. Testcode drukt het gemiddelde dus omlaag — een guard op alle code zou de verkeerde kant
# meten. Zelfde redenering als check-bestandsgrootte.sh, dat testprojecten om dezelfde reden
# uitsluit: een testbestand groeit door losse gevallen naast elkaar te zetten, dat is geen
# verstrengeling.
#
# WAAROM HET TOTAAL UIT statistics.total KOMT, NIET DE SOM VAN d.lines
# -----------------------------------------------------------------------
# jscpd's duplicates-lijst bevat clone-PAREN; een regel die in meerdere overlappende clones
# voorkomt, telt in die lijst meermaals mee. jscpd's eigen statistics.total.duplicatedLines
# dedupliceert dat al (dat is ook het getal dat de console-reporter toont) — zelf optellen over
# d.lines gaf bij het schrijven van dit script 962 tegenover de correcte 849.
#
# jscpd is een npm-afhankelijkheid, alleen nodig in CI — geen wijziging aan de .NET-projecten.
# ubuntu-latest heeft node/npx al aan boord (zie check-theme-js-contract.js, dat ook zonder een
# aparte setup-node-stap draait).
#
# Draagbaarheid (#1155): bash 3.2 én bash 5.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

# shellcheck source=scripts/ci/lib/plafond.sh
. scripts/ci/lib/plafond.sh

plafond="$(lees_plafond interne-duplicatie-regels)"

rapport_dir="$(mktemp -d)"
trap 'rm -rf "$rapport_dir"' EXIT

npx --yes jscpd@5.3.0 \
  --pattern '**/*.cs' \
  --ignore '**/obj/**,**/bin/**,**/*.Tests/**,FunctionApp/**' \
  --min-lines 5 --min-tokens 50 \
  --reporters json \
  --output "$rapport_dir" \
  --silent \
  . > /dev/null

if [ ! -f "$rapport_dir/jscpd-report.json" ]; then
  echo "::error::jscpd heeft geen rapport geschreven — kan de interne duplicatie niet meten."
  exit 1
fi

meting="$(python3 -c '
import json, sys

with open(sys.argv[1], encoding="utf-8") as fh:
    data = json.load(fh)

totaal = data.get("statistics", {}).get("total", {}).get("duplicatedLines", 0)

per_bestand = {}
for d in data.get("duplicates", []):
    for kant in ("firstFile", "secondFile"):
        naam = d[kant]["name"]
        per_bestand[naam] = per_bestand.get(naam, 0) + d.get("lines", 0)

top = sorted(per_bestand.items(), key=lambda kv: kv[1], reverse=True)[:10]

print(totaal)
for naam, regels in top:
    print("  %5d  %s" % (regels, naam))
' "$rapport_dir/jscpd-report.json")"

totaal="$(echo "$meting" | sed -n '1p')"

echo "Interne duplicatie (productiecode, buiten FunctionApp/): $totaal regels (plafond: $plafond)."
echo
echo "Grootste bijdragers (bestanden met de meeste regels in een clone — dubbeltelling mogelijk"
echo "als een bestand in meerdere clones voorkomt):"
echo "$meting" | sed -n '2,$p'

if [ "$totaal" -gt "$plafond" ]; then
  echo
  echo "::error::Interne duplicatie gestegen naar $totaal regels (plafond $plafond, dus +$((totaal - plafond)))."
  echo "::error::Zie docs/ARCHITECTUUR-CODEKWALITEIT.md regel 1 — herhaalde logica binnen dezelfde boom hoort in een gedeelde methode of klasse, niet in twee losse kopieën naast elkaar."
  exit 1
fi

# Speling: een verbetering laat de build NIET falen. jscpd's clone-detectie schuift bovendien
# soms een paar regels door toeval (een methode die net iets anders geformatteerd wordt). Pas bij
# een flinke daling is het de moeite het plafond te verlagen.
SPELING=75
if [ "$totaal" -lt "$((plafond - SPELING))" ]; then
  echo
  echo "::error::Interne duplicatie is gedaald naar $totaal, meer dan $SPELING onder het plafond ($plafond)."
  echo "::error::Zet de winst vast: wijzig 'interne-duplicatie-regels' in scripts/ci/codekwaliteit-plafonds.txt naar $totaal."
  exit 1
fi

if [ "$totaal" -lt "$plafond" ]; then
  echo
  echo "::notice::Interne duplicatie ligt $((plafond - totaal)) regels onder het plafond. Overweeg 'interne-duplicatie-regels' te verlagen naar $totaal."
fi

echo
echo "OK — interne duplicatie stijgt niet."
