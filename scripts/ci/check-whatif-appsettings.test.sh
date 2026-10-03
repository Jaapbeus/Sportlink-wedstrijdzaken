#!/usr/bin/env bash
# Test voor check-whatif-appsettings.sh (#1455). Fixtures zijn geanonimiseerd. bash 3.2-proof.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)"
G=scripts/ci/check-whatif-appsettings.sh
F=scripts/ci/fixtures/whatif
ok=0; bad=0
verwacht() { # naam verwachte-exit uitvoer-grep(optioneel) -- cmd...
    local naam="$1" exp="$2" pat="$3"; shift 4
    local out; out="$("$@" 2>&1)"; local rc=$?
    if [ "$rc" = "$exp" ] && { [ -z "$pat" ] || printf '%s' "$out" | grep -qE "$pat"; }; then
        echo "OK   $naam"; ok=$((ok+1))
    else
        echo "FAIL $naam (exit $rc, verwacht $exp)"; printf '%s\n' "$out"; bad=$((bad+1))
    fi
}
verwacht "veilige wijziging slaagt"            0 "^OK"            -- bash $G $F/veilig.json
verwacht "verwijderde setting faalt + naam"    1 "EXTRA_SETTING"  -- bash $G $F/verwijdert.json
verwacht "onleesbare JSON faalt gesloten"      2 "geen geldige JSON" -- bash $G $F/onleesbaar.json
verwacht "onbekend formaat faalt gesloten"     2 "changes"        -- bash $G $F/onbekend-formaat.json
verwacht "ontbrekend bestand faalt gesloten"   2 ""               -- bash $G $F/bestaat-niet.json
verwacht "zonder before en zonder live faalt"  2 "live-lijst"     -- bash $G $F/zonder-before.json
verwacht "live-lijst met extra setting faalt"  1 "LIVE_ONLY"      -- bash $G $F/zonder-before.json $F/live-met-extra.json
verwacht "live-lijst veilig slaagt"            0 "^OK|OK:"        -- bash $G $F/zonder-before.json $F/live-veilig.json
verwacht "--sites geeft sitenaam"              0 "func-\\[clubcode\\]-sportlink" -- bash $G --sites $F/veilig.json
# waarden mogen nooit in de uitvoer staan
if bash $G $F/verwijdert.json 2>&1 | grep -q "waarde"; then echo "FAIL waarde gelekt"; bad=$((bad+1)); else echo "OK   geen waarden in uitvoer"; ok=$((ok+1)); fi
echo "geslaagd=$ok mislukt=$bad"
[ "$bad" = 0 ]
