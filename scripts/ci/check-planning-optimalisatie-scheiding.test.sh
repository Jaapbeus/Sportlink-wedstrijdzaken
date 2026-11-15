#!/usr/bin/env bash
# check-planning-optimalisatie-scheiding.test.sh (#1960)
#
# Bewijst dat check-planning-optimalisatie-scheiding.sh rood kan worden: elke test maakt tijdelijk een
# overtreding, eist exit 1 en herstelt daarna. Er wordt nooit iets gecommit.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5.

set -uo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

GUARD="scripts/ci/check-planning-optimalisatie-scheiding.sh"
OPT="Planner.Shared/AutoPlanRegels.cs"
PLAN="Planner.Shared/Planning/SportlinkVeldbezettingSamenvoeging.cs"
GEM="FunctionApp.Postgres/Planner/AutoPlanService.cs"
TMP_OPT="$(mktemp)"; TMP_PLAN="$(mktemp)"; TMP_GEM="$(mktemp)"
cp "$OPT" "$TMP_OPT"; cp "$PLAN" "$TMP_PLAN"; cp "$GEM" "$TMP_GEM"
herstel() { cp "$TMP_OPT" "$OPT"; cp "$TMP_PLAN" "$PLAN"; cp "$TMP_GEM" "$GEM"; }
opruimen() { herstel; rm -f "$TMP_OPT" "$TMP_PLAN" "$TMP_GEM"; }
trap opruimen EXIT

mislukt=0
verwacht() {  # verwacht <slagen|falen> <omschrijving>
  bash "$GUARD" >/dev/null 2>&1; rc=$?
  if [ "$1" = "slagen" ] && [ "$rc" -eq 0 ]; then echo "  ok — $2"
  elif [ "$1" = "falen" ] && [ "$rc" -eq 1 ]; then echo "  ok — $2"
  else echo "::error::TEST MISLUKT — $2 (exit $rc, verwacht $1)"; mislukt=$((mislukt + 1)); fi
}

verwacht slagen "schone werkboom is groen"

printf '    public static int Duur(int? d) => VeldbezettingDuur.Bepaal(d, 0);\n' >> "$OPT"
verwacht falen "optimalisatiebestand gebruikt de duurregel van de Planning"
herstel

printf '    // VeldbezettingDuur in commentaar is geen gebruik\n' >> "$OPT"
verwacht slagen "vermelding in commentaar telt niet"
herstel

printf '    private static object S() => new FieldScheduler(null!, null!, 0);\n' >> "$PLAN"
verwacht falen "Planning-bestand gebruikt de scheduler van de optimalisatie"
herstel

# Optimalisatiecode in het gemengde bestand (buiten VeldbezettingAsync) met Sportlink-duur.
awk '{ print } /^    internal static List<AutoPlanWedstrijdItem> PlanWedstrijden\(/ { zet = 1 }
     zet && /^    \{/ { print "        var x = VeldbezettingDuur.Bepaal(null, 0);"; zet = 0 }' "$TMP_GEM" > "$GEM"
grep -q "var x = VeldbezettingDuur" "$GEM" || { echo "::error::TEST MISLUKT — proefregel niet geplaatst (PlanWedstrijden)"; mislukt=$((mislukt + 1)); }
verwacht falen "PlanWedstrijden gebruikt Sportlink-duur"
herstel

# Planning-methode in het gemengde bestand met een optimalisatieregel.
awk '{ print } /^    internal static async Task<List<VeldbezettingItem>> VeldbezettingAsync\(/ { zet = 1 }
     zet && /^    \{/ { print "        var s = PlanningBufferRegels.Effectief(0, null);"; zet = 0 }' "$TMP_GEM" > "$GEM"
grep -q "var s = PlanningBufferRegels" "$GEM" || { echo "::error::TEST MISLUKT — proefregel niet geplaatst (VeldbezettingAsync)"; mislukt=$((mislukt + 1)); }
verwacht falen "VeldbezettingAsync (Planning) gebruikt een optimalisatieregel"
herstel

# Dezelfde Sportlink-duur bínnen VeldbezettingAsync is de Planning en blijft toegestaan.
awk '{ print } /^    internal static async Task<List<VeldbezettingItem>> VeldbezettingAsync\(/ { zet = 1 }
     zet && /^    \{/ { print "        var d = VeldbezettingDuur.Bepaal(null, 0);"; zet = 0 }' "$TMP_GEM" > "$GEM"
verwacht slagen "Sportlink-duur binnen VeldbezettingAsync (Planning) is toegestaan"
herstel

if [ "$mislukt" -ne 0 ]; then echo "$mislukt test(s) mislukt"; exit 1; fi
echo "OK — de scheidingsguard kan rood worden."
