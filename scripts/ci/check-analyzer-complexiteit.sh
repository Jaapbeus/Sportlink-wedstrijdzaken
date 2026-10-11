#!/usr/bin/env bash
# check-analyzer-complexiteit.sh (#1300)
#
# Tweede slot op de drie Roslyn maintainability-analyzers: CA1502 (cyclomatische complexiteit),
# CA1505 (maintainability index) en CA1506 (class coupling).
#
# STAND: AFGEROND BIJ #1651 (epic #1632)
# --------------------------------------
# De ratchet is op 0 afgerond: de nulmeting van #1300 (19 overtredingen) is in tien PR's verholpen.
# Sinds #1651 staan CA1502/CA1505/CA1506 in .editorconfig op 'error' (testprojecten: 'none'). Een
# nieuwe overtreding is dus een BUILDFOUT; deze guard meldt dan "De build faalde" (exit 1) en toont
# de CA150x-regels. Dat is gewenst gedrag, geen guard-defect.
#
# WAAROM DE GUARD BLIJFT
# ----------------------
# - Hij faalt ook als .editorconfig de drie regels niet meer zet (dan telt de build stilzwijgend nul).
# - Hij bewaakt dat het aantal waarschuwingen 0 blijft als iemand de severity ooit terugzet naar
#   'warning': het plafond (analyzer-overtredingen in codekwaliteit-plafonds.txt) is dan het slot.
#
# WAAROM DE DRIE APART AANGEZET MOETEN WORDEN
# -------------------------------------------
# Ze zitten wél in Microsoft.CodeAnalysis.NetAnalyzers (die sinds .NET 5 standaard meekomt), maar
# staan uit — ook bij <AnalysisMode>All</AnalysisMode>. Microsoft zet ze bewust uit omdat de
# drempels projectafhankelijk zijn. Ze aanzetten gebeurt per regel in .editorconfig in de repo-root.
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

editorconfig="${EDITORCONFIG_PAD:-.editorconfig}"
if [ ! -f "$editorconfig" ]; then
  echo "::error::.editorconfig ontbreekt — zonder dat bestand staan CA1502/1505/1506 uit en telt deze guard altijd nul."
  exit 1
fi
# Alleen de productiesectie [*.cs] telt: de testsectie zet dezelfde regels bewust op 'none'.
productieblok="$(awk '/^\[/ { actief = ($0 == "[*.cs]"); next } actief' "$editorconfig")"
for regel in CA1502 CA1505 CA1506; do
  if ! printf '%s\n' "$productieblok" | grep -Eq "^dotnet_diagnostic\.${regel}\.severity[[:space:]]*=[[:space:]]*(error|warning)[[:space:]]*$"; then
    echo "::error::.editorconfig zet ${regel} in de sectie [*.cs] niet op error/warning — de regel staat dan uit voor productiecode en deze guard zou stilzwijgend nul tellen."
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
  echo "::error::Staat hieronder een CA1502/CA1505/CA1506-fout, dan is een methode te complex of een type te sterk gekoppeld (severity error sinds #1651)."
  grep -E 'error CA150[256]' "$bouwlog" | sed -E 's/^[[:space:]]*[0-9]+>//' | sed -E 's/^[[:space:]]+//' | sed -E 's/\[\/.*$//' | sort -u || true
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
