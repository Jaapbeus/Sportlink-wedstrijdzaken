#!/usr/bin/env bash
# check-analyzer-complexiteit.sh (#1300, #1651, #1945)
#
# Bewaakt dat de drie Roslyn maintainability-analyzers aan blijven staan: CA1502 (cyclomatische
# complexiteit), CA1505 (maintainability index) en CA1506 (class coupling).
#
# STAND SINDS #1945 (#1900 fase 3): CONFIGURATIECONTROLE, GEEN TELLING MEER
# -------------------------------------------------------------------------
# De ratchet is bij #1651 op 0 afgerond (nulmeting #1300: 19 overtredingen) en de drie regels staan
# sindsdien in .editorconfig op 'error' (testprojecten: 'none'). Een nieuwe overtreding is dus een
# BUILDFOUT in de gewone solutionbuild van build.yml (job 'compile'). Deze guard bouwde de solution
# vroeger nog een tweede keer met --no-incremental om waarschuwingen te tellen tegen een plafond;
# bij severity 'error' voegde dat niets toe en kostte het ~30 s op het kritieke pad. Die herbouw en
# het plafond 'analyzer-overtredingen' zijn bij #1945 vervallen.
#
# WAT DEZE GUARD NOG DOET — EN WAAROM DAT NODIG BLIJFT
# ----------------------------------------------------
# Zonder deze guard kan iemand de severity in .editorconfig terugzetten naar 'warning' of 'none',
# waarna de build stilzwijgend groen blijft bij elke overtreding (het no-op-patroon uit §67 van
# ARCHITECTUUR-DATABASE-TIERS.md). Daarom eist hij STRIKT 'error' in de productiesectie [*.cs].
# 'warning' is niet meer genoeg: er is geen teller meer die dan als tweede slot dient.
#
# WAAROM DE DRIE APART AANGEZET MOETEN WORDEN
# -------------------------------------------
# Ze zitten wél in Microsoft.CodeAnalysis.NetAnalyzers (die sinds .NET 5 standaard meekomt), maar
# staan uit — ook bij <AnalysisMode>All</AnalysisMode>. Microsoft zet ze bewust uit omdat de
# drempels projectafhankelijk zijn. Ze aanzetten gebeurt per regel in .editorconfig in de repo-root.
#
# TESTBESTANDEN TELLEN NIET MEE
# -----------------------------
# Uitgesloten in .editorconfig zelf (testsectie op 'none'), niet hier: een testklasse die 96 typen
# aanraakt is dekking, geen verstrengeling. Daarom kijkt deze guard alleen naar de sectie [*.cs].
#
# Voor een proef kan EDITORCONFIG_PAD naar een ander bestand wijzen (check-codekwaliteit.test.sh).
# Draagbaarheid (#1155): bash 3.2 én bash 5. Geen mapfile, geen declare -A. Geen .NET nodig.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

editorconfig="${EDITORCONFIG_PAD:-.editorconfig}"
if [ ! -f "$editorconfig" ]; then
  echo "::error::.editorconfig ontbreekt — zonder dat bestand staan CA1502/1505/1506 uit en vangt de build geen enkele overtreding."
  exit 1
fi

# Alleen de productiesectie [*.cs] telt: de testsectie zet dezelfde regels bewust op 'none'.
productieblok="$(awk '/^\[/ { actief = ($0 == "[*.cs]"); next } actief' "$editorconfig")"
for regel in CA1502 CA1505 CA1506; do
  if ! printf '%s\n' "$productieblok" | grep -Eq "^dotnet_diagnostic\.${regel}\.severity[[:space:]]*=[[:space:]]*error[[:space:]]*$"; then
    echo "::error::.editorconfig zet ${regel} in de sectie [*.cs] niet op error. Sinds #1945 vangt alleen de gewone build (severity error) een overtreding; op warning of none glipt hij stilzwijgend door."
    exit 1
  fi
done

echo "OK — CA1502/1505/1506 staan in [*.cs] van .editorconfig op error; de solutionbuild faalt op elke overtreding."
