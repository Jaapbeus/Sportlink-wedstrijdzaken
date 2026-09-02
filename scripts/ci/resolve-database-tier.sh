#!/usr/bin/env bash
# resolve-database-tier.sh (#816, datagedreven sinds #865)
#
# Canonieke tier-resolver: bepaalt welk .csproj gebouwd/gedeployed wordt op basis van de
# GitHub repository-variabele DatabaseTier (Settings -> Secrets and variables -> Actions ->
# Variables).
#
# De vertaling tier-naam -> projectpad staat NIET meer in dit script maar in
# scripts/ci/database-tiers.json. Reden (#865): de lokale dev-scripts zijn PowerShell en hadden
# de mapping anders moeten dupliceren, wat exact de belofte van #816 zou breken dat er één
# vertaalpunt is. Get-DatabaseTierProject in scripts/dev/DevServices.psm1 leest hetzelfde bestand.
#
# Nooit een stille default: een ontbrekende, lege of onbekende waarde faalt hard (#816,
# architectuurbesluit "geen gedeelde abstractie, geen runtime-engine-detectie", zie
# docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 2).
#
# Gebruik (in een workflow-step met id, zodat GITHUB_OUTPUT gevuld wordt):
#   - name: Tier resolven
#     id: tier
#     env:
#       DatabaseTier: ${{ vars.DatabaseTier }}
#     run: bash scripts/ci/resolve-database-tier.sh
#   - run: dotnet publish ${{ steps.tier.outputs.csproj_path }} ...
set -euo pipefail

TIER="${DatabaseTier:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TIERS_JSON="${SCRIPT_DIR}/database-tiers.json"

if [ -z "$TIER" ]; then
  echo "::error::Repository-variabele 'DatabaseTier' ontbreekt of is leeg."
  echo "::error::Zet 'm in GitHub Settings -> Secrets and variables -> Actions -> Variables."
  echo "::error::Bestaande forks: zet 'm op 'SqlServer' (de huidige, enige geimplementeerde tier) — zie CHANGELOG.md [Unreleased] BREAKING CHANGE voor de volledige migratie-instructie."
  exit 1
fi

if [ ! -f "$TIERS_JSON" ]; then
  echo "::error::${TIERS_JSON} ontbreekt — de tier-tabel is de enige bron van de mapping."
  exit 1
fi

# python3 staat op elke GitHub-runner en wordt in build.yml al gebruikt; zo hoeft jq geen
# afhankelijkheid te worden. Uitvoer is één regel: "<gevonden> <built> <csproj> <issue>".
read -r FOUND BUILT CSPROJ ISSUE <<EOF
$(TIER="$TIER" python3 - "$TIERS_JSON" <<'PY'
import json, os, sys
tier = os.environ["TIER"]
with open(sys.argv[1], encoding="utf-8") as fh:
    data = json.load(fh)
for entry in data["tiers"]:
    if entry["name"] == tier:
        print("yes", str(entry["built"]).lower(), entry["csproj"], entry.get("epicIssue") or "-")
        break
else:
    names = ",".join(e["name"] for e in data["tiers"])
    print("no", "-", "-", names)
PY
)
EOF

if [ "$FOUND" != "yes" ]; then
  echo "::error::Onbekende DatabaseTier-waarde: '${TIER}'. Geldige waarden: ${ISSUE//,/, }."
  exit 1
fi

if [ "$BUILT" != "true" ]; then
  echo "::error::DatabaseTier='${TIER}' is een geldige toekomstige waarde, maar de bijbehorende implementatieboom bestaat nog niet in deze repository (epic #815, zie issue #${ISSUE} en docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 6 voor de bouwvolgorde)."
  echo "::error::Zet DatabaseTier terug op 'SqlServer' totdat die tier daadwerkelijk gebouwd is."
  # Exitcode 2 = "geldig, nog niet gebouwd" — te onderscheiden van 1 = "onbruikbare waarde".
  # De zelftest (#851) gebruikt dat verschil om netjes af te breken in plaats van te falen.
  exit 2
fi

echo "Tier: ${TIER} -> ${CSPROJ}"
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  echo "csproj_path=${CSPROJ}" >> "$GITHUB_OUTPUT"
fi
