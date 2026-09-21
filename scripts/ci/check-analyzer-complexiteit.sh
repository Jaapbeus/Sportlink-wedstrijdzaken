#!/usr/bin/env bash
# check-analyzer-complexiteit.sh (#1300)
#
# Ratchet op de drie Roslyn maintainability-analyzers: CA1502 (cyclomatische complexiteit),
# CA1505 (maintainability index) en CA1506 (class coupling).
#
# WAAROM DEZE DRIE APART AANGEZET MOETEN WORDEN
# ---------------------------------------------
# Ze zitten wél in Microsoft.CodeAnalysis.NetAnalyzers (die sinds .NET 5 standaard meekomt), maar
# staan uit — ook bij <AnalysisMode>All</AnalysisMode>. Microsoft zet ze bewust uit omdat de
# drempels projectafhankelijk zijn. Ze aanzetten gebeurt per regel in .editorconfig; dat bestand
# in de repo-root doet dat, en zet ze op 'warning'.
#
# WAAROM EEN RATCHET EN NIET METEEN 'error'
# -----------------------------------------
# De nulmeting is 19 overtredingen (13x CA1502, 6x CA1506, 0x CA1505). Direct op 'error' zetten
# maakt de build rood tot alle negentien zijn herschreven — en daar zitten methodes bij met een
# cyclomatische complexiteit van 43, 46 en 58. Dat zijn echte refactors met productierisico
# (BerichtPipeline verwerkt binnenkomende e-mail), geen opruimwerk dat in een chore-PR past.
#
# Een gate die de eerstvolgende PR rood maakt zonder dat iemand die overtredingen heeft gezien,
# wordt binnen twee PR's weer uitgezet. Dan bewaakt hij niets meer. Vandaar hetzelfde patroon als
# check-bestandsgrootte.sh: bestaande code mag blijven, het AANTAL mag niet groeien.
#
# CA1505 STAAT OP NUL EN DAT IS GEEN TOEVAL OM TE NEGEREN
# -------------------------------------------------------
# De maintainability index is een samengestelde maat (Halstead-volume, cyclomatische complexiteit,
# regels code) die pas onder de 10 gaat klagen. Nul overtredingen betekent hier dus niet dat de
# regel niets doet, maar dat geen enkel type onder die ondergrens zit. Hij blijft aan staan: de
# ratchet vangt hem op zodra er wél een bijkomt.
#
# TESTBESTANDEN TELLEN NIET MEE
# -----------------------------
# Uitgesloten in .editorconfig zelf, niet hier. Zelfde redenering als regel 7 en 8: een testklasse
# die 96 typen aanraakt (SportlinkClubClientTests) is geen verstrengeling maar dekking, en een
# guard die het toevoegen van tests bestraft werkt averechts.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5. Geen mapfile, geen declare -A.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

# shellcheck source=scripts/ci/lib/plafond.sh
. scripts/ci/lib/plafond.sh

plafond="$(lees_plafond analyzer-overtredingen)"

if [ ! -f .editorconfig ]; then
  echo "::error::.editorconfig ontbreekt — zonder dat bestand staan CA1502/1505/1506 uit en telt deze guard altijd nul."
  exit 1
fi
for regel in CA1502 CA1505 CA1506; do
  if ! grep -q "dotnet_diagnostic.${regel}.severity" .editorconfig; then
    echo "::error::.editorconfig zet ${regel} niet — deze guard zou dan stilzwijgend nul tellen."
    exit 1
  fi
done

bouwlog="$(mktemp)"
trap 'rm -f "$bouwlog"' EXIT

# -v n is nodig: op 'quiet' onderdrukt MSBuild de waarschuwingsregels die we hier tellen.
# Een bouwfout is hier geen guard-uitspraak — dan faalt de gewone buildstap al.
if ! dotnet build sportlink-wedstrijdzaken.slnf --configuration Release --no-incremental -v n \
     > "$bouwlog" 2>&1; then
  echo "::error::De build faalde; deze guard kan dan niets meten. Zie de gewone buildstap."
  tail -30 "$bouwlog"
  exit 1
fi

# Elke overtreding staat TWEE keer in de log, in twee verschillende vormen:
#
#   2>/pad/Bestand.cs(546,29): warning CA1502: ...     <- inline, met knoop-voorvoegsel
#     /pad/Bestand.cs(546,29): warning CA1502: ...     <- samenvatting, met inspringing
#
# MSBuild bouwt parallel (vandaar '2>') en herhaalt alle waarschuwingen aan het eind. Wie alleen
# het knoop-voorvoegsel wegstript houdt de inspringing over en telt alles dubbel — precies wat hier
# eerst gebeurde: 38 in plaats van 19. Beide vormen moeten dus tot dezelfde sleutel leiden.
aantal="$(grep -E 'warning CA150[256]' "$bouwlog" \
  | sed -E 's/^[[:space:]]*[0-9]+>//' \
  | sed -E 's/^[[:space:]]+//' \
  | sed -E 's/^(.*\([0-9]+),[0-9]+\): warning (CA150[256]).*/\1 \2/' \
  | sort -u | grep -c '' || true)"

if [ "$aantal" -gt "$plafond" ]; then
  echo "::error::Maintainability-overtredingen gestegen naar $aantal (plafond $plafond)."
  echo "::error::Nieuwe code moet onder de drempel blijven, of ruim elders iets op."
  echo ""
  echo "Alle huidige overtredingen:"
  grep -E 'warning CA150[256]' "$bouwlog" \
    | sed -E 's/^[[:space:]]*[0-9]+>//' \
    | sed -E 's/^[[:space:]]+//' \
    | sed -E 's/\[\/.*$//' \
    | sort -u
  exit 1
fi

# Speling, net als in check-bestandsgrootte.sh: een refactor die er toevallig eentje meeneemt
# hoeft niet meteen de build rood te maken. Zakt het verder, dan wordt de winst vastgezet —
# anders kruipt het plafond ongemerkt omhoog ten opzichte van de werkelijkheid.
SPELING=2
if [ "$aantal" -lt "$((plafond - SPELING))" ]; then
  echo "::error::Nog maar $aantal maintainability-overtredingen (plafond $plafond)."
  echo "::error::Zet de winst vast: wijzig 'analyzer-overtredingen' in scripts/ci/codekwaliteit-plafonds.txt naar $aantal."
  exit 1
fi

echo "OK — $aantal maintainability-overtredingen (CA1502/1505/1506), plafond $plafond."
