#!/usr/bin/env bash
# check-blazor-codebehind.sh (#1262)
#
# Dwingt af dat er geen logica in Blazor-pagina's staat: elke .razor onder BlazorAdmin/Pages/ hoort
# zijn C# in een code-behind (<Pagina>.razor.cs, `public partial class`) te hebben, niet in een
# @code-blok in de pagina zelf.
#
# WAAROM DIT SCRIPT BESTAAT
# -------------------------
# De regel bestond al — CLAUDE.md legt hem sinds #1122 op aan de vier Sportlink-extensiepagina's —
# maar er was niets dat hem controleerde. Resultaat: vier pagina's volgen hem, dertien niet, samen
# ruim 1.600 regels logica die buiten elk testproject valt. Een @code-blok is niet los te testen:
# BlazorAdmin.Tests kan een partial class instantiëren, een @code-blok niet.
#
# Dit is geen Microsoft-voorschrift. Microsoft beschrijft beide vormen als ondersteund en noemt
# geen grens (learn.microsoft.com/aspnet/core/blazor/components — "Partial class support"). Het is
# een eigen architectuurkeuze van dit project, gemaakt omdat testbaarheid hier zwaarder weegt.
#
# TWEE REGELS, VERSCHILLEND VAN HARDHEID
# --------------------------------------
# 1. HARD: een pagina die al een .razor.cs heeft, mag daarnaast géén @code-blok hebben. Logica op
#    twee plekken in hetzelfde scherm is erger dan logica op één verkeerde plek.
# 2. RATCHET: het totaal aantal @code-regels over alle pagina's mag niet stijgen. Nieuwe pagina's
#    beginnen dus per definitie met een code-behind; bestaande pagina's migreren op hun eigen
#    moment, en elke migratie zet de winst vast.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5. Geen mapfile, geen declare -A.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

# shellcheck source=scripts/ci/lib/plafond.sh
. scripts/ci/lib/plafond.sh

plafond="$(lees_plafond blazor-code-regels-in-pages)"

meting="$(git ls-files -- 'BlazorAdmin/Pages/*.razor' | python3 -c '
import os, sys

totaal = 0
regels = []
dubbel = []

for pad in (r.strip() for r in sys.stdin if r.strip()):
    with open(pad, encoding="utf-8", errors="replace") as fh:
        inhoud = fh.readlines()

    # Tel de regels binnen het @code-blok door accoladediepte te volgen vanaf de openingsregel.
    aantal = diepte = 0
    binnen = False
    for regel in inhoud:
        if not binnen:
            if regel.lstrip().startswith("@code"):
                binnen = True
                diepte = regel.count("{") - regel.count("}")
            continue
        diepte += regel.count("{") - regel.count("}")
        if diepte <= 0:
            binnen = False
            continue
        if regel.strip():
            aantal += 1

    if aantal:
        totaal += aantal
        regels.append((aantal, pad))
        if os.path.isfile(pad + ".cs"):
            dubbel.append(pad)

regels.sort(reverse=True)
print(totaal)
print(";".join(dubbel))
for aantal, pad in regels:
    print("%6d  %s" % (aantal, pad))
')"

totaal="$(echo "$meting" | sed -n '1p')"
dubbel="$(echo "$meting" | sed -n '2p')"

echo "Logica in @code-blokken van Blazor-pagina's: $totaal regels (plafond: $plafond)."
echo
echo "$meting" | sed -n '3,$p'

fail=0

if [ -n "$dubbel" ]; then
  echo
  echo "$dubbel" | tr ';' '\n' | while IFS= read -r pad; do
    [ -n "$pad" ] || continue
    echo "::error file=$pad::Deze pagina heeft zowel een code-behind ($pad.cs) als een @code-blok. Verplaats de inhoud van het @code-blok naar de partial class en haal het blok weg."
  done
  fail=1
fi

if [ "$totaal" -gt "$plafond" ]; then
  echo
  echo "::error::Logica in Blazor-pagina's gestegen naar $totaal regels (plafond $plafond, dus +$((totaal - plafond)))."
  echo "::error::Zet de C# van deze pagina in een code-behind: <Pagina>.razor.cs met 'public partial class', [Inject] in plaats van @inject. Zie docs/ARCHITECTUUR-CODEKWALITEIT.md regel 3 en Dagplanning.razor.cs als voorbeeld."
  fail=1
fi

SPELING=100
if [ "$totaal" -lt "$((plafond - SPELING))" ]; then
  echo
  echo "::error::Logica in Blazor-pagina's is gedaald naar $totaal, meer dan $SPELING onder het plafond ($plafond)."
  echo "::error::Zet de winst vast: wijzig 'blazor-code-regels-in-pages' in scripts/ci/codekwaliteit-plafonds.txt naar $totaal."
  fail=1
elif [ "$totaal" -lt "$plafond" ]; then
  echo
  echo "::notice::Nog $((plafond - totaal)) regels onder het plafond. Overweeg 'blazor-code-regels-in-pages' te verlagen naar $totaal."
fi

[ "$fail" -eq 0 ] || exit 1
echo
echo "OK — geen logica bijgekomen in Blazor-pagina's."
