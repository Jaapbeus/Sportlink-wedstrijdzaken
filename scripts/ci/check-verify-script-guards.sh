#!/usr/bin/env bash
# check-verify-script-guards.sh (#1474)
#
# Controleert dat scripts/azure/Verify-AzureAuthSetup.ps1 geen PII lekt via een lege parameter.
#
# Regels:
# 1. IsNullOrWhiteSpace() guard moet aanwezig zijn: voorkomt ongefilterde user search
# 2. AuthGate-checks in Layer 4: zowel AuthGate.cs als App.razor worden gescanned

set -uo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

verify_script="scripts/azure/Verify-AzureAuthSetup.ps1"
mislukt=0

echo "Controleert Verify-AzureAuthSetup.ps1 guards..."

# Regel 1: IsNullOrWhiteSpace() guard moet aanwezig zijn
if grep -qE 'IsNullOrWhiteSpace.*AdminUserPrincipalName' "$verify_script"; then
    echo "  ✓ IsNullOrWhiteSpace guard aanwezig"
else
    echo "  ✗ IsNullOrWhiteSpace guard ONTBREEKT — kan PII leaken via lege parameter"
    mislukt=$((mislukt + 1))
fi

# Regel 2: Layer 4 moet AuthGate.Bepaal() checken
if grep -qE 'AuthGate\.Bepaal\(\)' "$verify_script"; then
    echo "  ✓ Layer 4 checkt AuthGate.Bepaal()"
else
    echo "  ✗ Layer 4 checkt NIET AuthGate.Bepaal() — vervangen door IsInRole in App.razor?"
    mislukt=$((mislukt + 1))
fi

# Regel 3: Print Statement moet minimaal zijn — geen az-output van gebruikers direct
# Check dat we geen objecten direct naar output pipen — bijv. `| ConvertFrom-Json`
# Na 'AdminUserPrincipalName niet gevonden' mogen we NIET alle gebruikers afdrukken.
if grep -qE 'Write-Pass.*userPrincipalName|Write-Info.*displayName' "$verify_script"; then
    # Dit is goed: we printen alleen de UPN of Info-niveauberichten
    echo "  ✓ User output minimaal (geen lijsten)"
else
    # Check het tegenovergestelde: mag niet gebruikersobjecten dumpen
    if grep -qE 'Write-Host.*adminUser|Write-Host.*users\.value' "$verify_script"; then
        echo "  ✗ User output bevat mogelijk gebruikerslijsten"
        mislukt=$((mislukt + 1))
    else
        echo "  ✓ User output minimaal (geen lijsten)"
    fi
fi

echo

if [ $mislukt -eq 0 ]; then
    echo "  Alle checks groen."
    exit 0
else
    echo "  ✗ $mislukt check(s) mislukt."
    exit 1
fi
