#!/usr/bin/env bash
# check-tier-duplicatie.sh (#1262, root cause van #1248)
#
# Meet hoeveel betekenisvolle regels woordelijk identiek zijn tussen de SQL Server-tierboom
# (FunctionApp/) en de Postgres-tierboom (FunctionApp.Postgres/), en faalt zodra dat getal stijgt.
#
# WAAROM DIT SCRIPT BESTAAT
# -------------------------
# #1248: de volledige thema-logica — regex-extractie, hexvalidatie, SSRF-allowlist-orkestratie,
# standaardkleuren — stond woordelijk twee keer in de codebase. Niet per ongeluk: in het
# gedupliceerde bestand stond letterlijk dat de logica databasetier-onafhankelijk gekopieerd was.
# De review zág het dus. Er was alleen geen regel om het op af te wijzen, en geen getal dat liet
# zien dat het erger werd.
#
# Wat die duplicatie kostte, bleek bij #1252: een platformafhankelijke bug in de URL-resolutie zat
# in béíde kopieën, in geen van beide getest, en gaf op Unix stilzwijgend null terug. De fout kon
# onzichtbaar blijven precies omdat de logica nergens één plek had waar een test op aangreep.
#
# WAT DEZE GUARD WEL EN NIET AFDWINGT
# -----------------------------------
# WEL: het totaal mag nooit stijgen. Elke PR die een bestaand tierbestand kopieert in plaats van
#      de gedeelde kern in Planner.Shared/ uit te breiden, maakt dit getal hoger en wordt rood.
# NIET: het dwingt geen refactor af van wat er vandaag al staat. De bestaande duplicatie is erfenis
#      van de generieke 1-op-1-poort (#887/#952); die in één PR opruimen zou riskanter zijn dan het
#      probleem zelf. De ratchet zorgt dat het getal alleen nog kleiner wordt.
#
# Daalt het getal, dan faalt deze guard óók — met de instructie het plafond te verlagen. Winst die
# niet wordt vastgezet, lekt binnen een paar PR's weer weg.
#
# BETEKENISVOLLE REGEL = niet leeg, niet enkel een accolade/haakje, geen using-regel, geen
# commentaar. Anders domineert onvermijdelijke C#-boilerplate de meting en reageert het getal
# nauwelijks op echte logica-duplicatie.
#
# WAAROM PYTHON VOOR DE MEETSTAP
# ------------------------------
# De voor de hand liggende oplossing, `diff --unchanged-group-format`, is een GNU-uitbreiding. De
# BSD-diff van macOS kent hem niet. Dat is exact de val uit AGENTS.md "Windows én macOS (#800)": de
# guard was groen geweest op de Linux-CI en had lokaal een onbegrijpelijke fout gegeven. difflib
# hoort bij de standaardbibliotheek van Python 3 en gedraagt zich op beide platforms identiek.
#
# Draagbaarheid (#1155): bash 3.2 (macOS /bin/bash) én bash 5 (Linux-CI). Geen mapfile, geen
# declare -A, geen GNU-only vlaggen.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

# shellcheck source=scripts/ci/lib/plafond.sh
. scripts/ci/lib/plafond.sh

plafond="$(lees_plafond tier-duplicatie-regels)"

meting="$(git ls-files --cached --others --exclude-standard -z -- 'FunctionApp.Postgres/*.cs' | python3 -c '
import difflib, os, re, sys

NEGEER = re.compile(r"^(using [A-Za-z]|//|///|/\*|\*|\*/)")
BOILERPLATE = {"{", "}", ")", ");", "},", "{}", "})", "});"}

def betekenisvol(pad):
    with open(pad, encoding="utf-8", errors="replace") as fh:
        regels = []
        for regel in fh:
            regel = regel.strip()
            if not regel or regel in BOILERPLATE or NEGEER.match(regel):
                continue
            regels.append(regel)
        return regels

TIER_A, TIER_B = "FunctionApp", "FunctionApp.Postgres"
totaal = paren = 0
per_paar = []
gebruikte_bestanden = set()

def meet_paar(pad_a, pad_b, label):
    global totaal, paren
    if not os.path.isfile(pad_a) or not os.path.isfile(pad_b):
        raise SystemExit("tier-duplicatie mapping verwijst naar een ontbrekend bestand: %s | %s" % (pad_a, pad_b))
    if pad_a in gebruikte_bestanden or pad_b in gebruikte_bestanden:
        raise SystemExit("tier-duplicatie mapping koppelt een bestand meer dan één keer: %s | %s" % (pad_a, pad_b))
    gebruikte_bestanden.update((pad_a, pad_b))
    a, b = betekenisvol(pad_a), betekenisvol(pad_b)
    gelijk = sum(
        blok.size
        for blok in difflib.SequenceMatcher(None, a, b, autojunk=False).get_matching_blocks()
    )
    paren += 1
    totaal += gelijk
    per_paar.append((gelijk, label))

bestanden = {
    os.fsdecode(pad)
    for pad in sys.stdin.buffer.read().split(b"\0")
    if pad
}

for pad_b in sorted(bestanden):
    pad_a = os.path.join(TIER_A, os.path.relpath(pad_b, TIER_B))
    if not os.path.isfile(pad_a):
        continue
    meet_paar(pad_a, pad_b, os.path.relpath(pad_b, TIER_B))

with open("scripts/ci/tier-duplicatie-mapping.txt", encoding="utf-8") as manifest:
    for regelnummer, regel in enumerate(manifest, start=1):
        regel = regel.strip()
        if not regel or regel.startswith("#"):
            continue
        onderdelen = [deel.strip() for deel in regel.split("|")]
        if len(onderdelen) != 2:
            raise SystemExit("ongeldige tier-duplicatie mapping op regel %d" % regelnummer)
        pad_a, pad_b = onderdelen
        if not pad_a.startswith(TIER_A + "/") or not pad_b.startswith(TIER_B + "/"):
            raise SystemExit("tier-duplicatie mapping moet FunctionApp/ naar FunctionApp.Postgres/ koppelen (regel %d)" % regelnummer)
        meet_paar(pad_a, pad_b, "%s <-> %s" % (pad_a, pad_b))

per_paar.sort(reverse=True)
print(totaal)
print(paren)
for gelijk, naam in per_paar[:10]:
    print("%8d  %s" % (gelijk, naam))
')"

totaal="$(echo "$meting" | sed -n '1p')"
paren="$(echo "$meting" | sed -n '2p')"

if [ -z "$paren" ] || [ "$paren" -eq 0 ]; then
  echo "::error::Geen tierbestandsparen gevonden — deze guard bewijst dan niets. Zijn FunctionApp/ of FunctionApp.Postgres/ hernoemd?"
  exit 1
fi

echo "Tier-duplicatie: $totaal betekenisvolle regels woordelijk identiek over $paren bestandsparen (plafond: $plafond)."
echo
echo "Grootste bijdragers:"
echo "$meting" | sed -n '3,$p'

if [ "$totaal" -gt "$plafond" ]; then
  echo
  echo "::error::Tier-duplicatie gestegen naar $totaal regels (plafond $plafond, dus +$((totaal - plafond)))."
  echo "::error::Logica die niet over databasetoegang gaat, hoort in Planner.Shared/ — zie docs/ARCHITECTUUR-CODEKWALITEIT.md regel 1, met ThemeCore (#1248) en FeedbackCore (#1130) als precedent."
  echo "::error::Een tierbestand hoort alleen nog query's, parameterbinding en de vertaling van een kernstatus naar HTTP te bevatten."
  exit 1
fi

# Speling: een verbetering laat de build NIET falen. Een guard die rood wordt van vooruitgang
# wordt uitgezet, en dan bewaakt hij niets meer. Pas bij een flinke daling is het de moeite het
# plafond te verlagen — anders lekt de winst er binnen een paar PR's weer in.
SPELING=200
if [ "$totaal" -lt "$((plafond - SPELING))" ]; then
  echo
  echo "::error::Tier-duplicatie is gedaald naar $totaal, meer dan $SPELING onder het plafond ($plafond)."
  echo "::error::Zet de winst vast: wijzig 'tier-duplicatie-regels' in scripts/ci/codekwaliteit-plafonds.txt naar $totaal."
  exit 1
fi

if [ "$totaal" -lt "$plafond" ]; then
  echo
  echo "::notice::Tier-duplicatie ligt $((plafond - totaal)) regels onder het plafond. Overweeg 'tier-duplicatie-regels' te verlagen naar $totaal."
fi

echo
echo "OK — tier-duplicatie stijgt niet."
