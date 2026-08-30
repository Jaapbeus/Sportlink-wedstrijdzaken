#!/usr/bin/env bash
# resolve-database-tier.sh (#816)
#
# Canonieke tier-resolver: bepaalt welk .csproj gebouwd/gedeployed wordt op basis van de
# GitHub repository-variabele DatabaseTier (Settings -> Secrets and variables -> Actions ->
# Variables). Dit is de ENIGE plek waar tier-naam -> projectpad wordt vertaald; .github/workflows
# roept dit script aan in plaats van zelf een switch te herhalen.
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

if [ -z "$TIER" ]; then
  echo "::error::Repository-variabele 'DatabaseTier' ontbreekt of is leeg."
  echo "::error::Zet 'm in GitHub Settings -> Secrets and variables -> Actions -> Variables."
  echo "::error::Bestaande forks: zet 'm op 'SqlServer' (de huidige, enige geimplementeerde tier) — zie CHANGELOG.md [Unreleased] BREAKING CHANGE voor de volledige migratie-instructie."
  exit 1
fi

case "$TIER" in
  SqlServer)
    echo "Tier: SqlServer -> FunctionApp/fa-dev-sportlink-01.csproj"
    echo "csproj_path=FunctionApp/fa-dev-sportlink-01.csproj" >> "$GITHUB_OUTPUT"
    ;;
  Postgres|Sqlite)
    echo "::error::DatabaseTier='$TIER' is een geldige toekomstige waarde, maar de bijbehorende implementatieboom bestaat nog niet in deze repository (epic #815, zie docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 5 voor de bouwvolgorde)."
    echo "::error::Zet DatabaseTier terug op 'SqlServer' totdat die tier daadwerkelijk gebouwd is."
    exit 1
    ;;
  *)
    echo "::error::Onbekende DatabaseTier-waarde: '$TIER'. Geldige waarden: SqlServer, Postgres, Sqlite."
    exit 1
    ;;
esac
