#!/usr/bin/env bash
# check-bestandsgrootte.sh (#1262, regel 7 en 8)
#
# Houdt twee getallen in de gaten: hoeveel productiebestanden boven de 500 regels uitkomen, en
# hoeveel methodes boven de 80 regels. Beide zijn ratchets — ze mogen niet stijgen.
#
# WAAROM EEN AANTAL EN GEEN HARDE GRENS
# -------------------------------------
# Er is geen gezaghebbende drempel om naar te wijzen. Google's eigen reviewrichtlijnen noemen
# bewust geen bestandsgrens en zeggen expliciet dat "smallness" geen simpele functie van
# regelaantal is. SonarSource hanteert cognitieve complexiteit 15 per functie, Microsofts CA1502
# staat op cyclomatische complexiteit 25. Drie gerenommeerde bronnen, drie andere antwoorden.
#
# Een zelfgekozen harde grens zou zesentwintig bestaande bestanden meteen illegaal maken. Zo'n
# guard wordt binnen twee PR's uitgezet, en dan bewaakt hij niets meer. Daarom telt dit script
# hoeveel bestanden en methodes boven de grens zitten en eist het dat dat aantal niet groeit:
# bestaande code mag blijven, nieuwe code moet er onder blijven of iets anders opruimen.
#
# De grenzen zelf — 500 regels per bestand, 80 per methode — zijn bewust ruim. Ze markeren niet
# "goed" maar "dit wordt moeilijk te lezen en te testen".
#
# TESTBESTANDEN TELLEN NIET MEE
# -----------------------------
# Een testbestand groeit door losse gevallen naast elkaar te zetten; dat is geen verstrengeling
# en leest ook bij tweeduizend regels prima van boven naar beneden. De leesbaarheidsvraag is daar
# een andere, en een guard die het toevoegen van tests bestraft, werkt averechts.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

# shellcheck source=scripts/ci/lib/plafond.sh
. scripts/ci/lib/plafond.sh

GRENS_BESTAND=500
GRENS_METHODE=80

plafond_bestanden="$(lees_plafond bestanden-boven-grens)"
plafond_methodes="$(lees_plafond methodes-boven-grens)"

meting="$(git ls-files -- '*.cs' '*.razor' \
    ':(exclude)*.Tests/*' ':(exclude)*/obj/*' ':(exclude)*/bin/*' \
  | python3 -c '
import re, sys

GRENS_BESTAND, GRENS_METHODE = 500, 80

# Een methodesignatuur: toegankelijkheid/modifiers, retourtype, naam, haakjes, dan een accolade.
SIGNATUUR = re.compile(
    r"^\s*(?:public|private|protected|internal|static|async|override|virtual|sealed|partial|\s)+"
    r"[\w<>\[\],\.\?\s]+\s+(\w+)\s*\([^;]*$"
)

grote_bestanden = []
grote_methodes = []

for pad in (r.strip() for r in sys.stdin if r.strip()):
    try:
        with open(pad, encoding="utf-8", errors="replace") as fh:
            regels = fh.readlines()
    except OSError:
        continue

    if len(regels) > GRENS_BESTAND:
        grote_bestanden.append((len(regels), pad))

    # Methodelengte via accoladediepte vanaf de signatuur. Ruwe heuristiek, maar consistent:
    # het gaat om een getal dat niet mag stijgen, niet om een exacte meting.
    i = 0
    while i < len(regels):
        treffer = SIGNATUUR.match(regels[i])
        if not treffer:
            i += 1
            continue
        # Zoek de openende accolade (kan op de volgende regel staan).
        j, diepte, gestart = i, 0, False
        while j < len(regels) and j < i + 6 and not gestart:
            diepte += regels[j].count("{") - regels[j].count("}")
            if "{" in regels[j]:
                gestart = True
            j += 1
        if not gestart:
            i += 1
            continue
        start = j
        while j < len(regels) and diepte > 0:
            diepte += regels[j].count("{") - regels[j].count("}")
            j += 1
        lengte = j - start
        if lengte > GRENS_METHODE:
            grote_methodes.append((lengte, "%s:%d %s" % (pad, i + 1, treffer.group(1))))
        i = max(j, i + 1)

grote_bestanden.sort(reverse=True)
grote_methodes.sort(reverse=True)
print(len(grote_bestanden))
print(len(grote_methodes))
print("BESTANDEN")
for n, pad in grote_bestanden[:8]:
    print("  %5d  %s" % (n, pad))
print("METHODES")
for n, naam in grote_methodes[:8]:
    print("  %5d  %s" % (n, naam))
')"

aantal_bestanden="$(echo "$meting" | sed -n '1p')"
aantal_methodes="$(echo "$meting" | sed -n '2p')"

echo "Productiebestanden boven $GRENS_BESTAND regels: $aantal_bestanden (plafond: $plafond_bestanden)"
echo "Methodes boven $GRENS_METHODE regels:           $aantal_methodes (plafond: $plafond_methodes)"
echo
echo "$meting" | sed -n '3,$p'

fail=0

if [ "$aantal_bestanden" -gt "$plafond_bestanden" ]; then
  echo
  echo "::error::Aantal bestanden boven $GRENS_BESTAND regels gestegen naar $aantal_bestanden (plafond $plafond_bestanden)."
  echo "::error::Splits het bestand op langs een naad die er al is — een aparte klasse, een eigen bestand per verantwoordelijkheid. Zie docs/ARCHITECTUUR-CODEKWALITEIT.md regel 7."
  fail=1
fi

if [ "$aantal_methodes" -gt "$plafond_methodes" ]; then
  echo
  echo "::error::Aantal methodes boven $GRENS_METHODE regels gestegen naar $aantal_methodes (plafond $plafond_methodes)."
  echo "::error::Haal een samenhangend blok uit de methode en geef het een naam. Zie docs/ARCHITECTUUR-CODEKWALITEIT.md regel 8."
  fail=1
fi

SPELING=2
if [ "$aantal_bestanden" -lt "$((plafond_bestanden - SPELING))" ]; then
  echo "::error::Nog maar $aantal_bestanden bestanden boven de grens. Zet de winst vast: 'bestanden-boven-grens' naar $aantal_bestanden."
  fail=1
fi
if [ "$aantal_methodes" -lt "$((plafond_methodes - SPELING))" ]; then
  echo "::error::Nog maar $aantal_methodes methodes boven de grens. Zet de winst vast: 'methodes-boven-grens' naar $aantal_methodes."
  fail=1
fi

[ "$fail" -eq 0 ] || exit 1
echo
echo "OK — bestandsgrootte en methodelengte stijgen niet."
