#!/usr/bin/env bash
# check-planning-optimalisatie-scheiding.sh (#1960, WZ-ADR-014)
#
# Planning en Veld optimalisatie zijn gescheiden domeinen (docs/ARCHITECTUUR.md §9, WZ-ADR-014).
#
#   * Veld optimalisatie rekent alleen met app-instellingen. Code van de optimalisatie gebruikt dus
#     geen Sportlink-duur, geen Sportlink-veldplanner-overlay en geen Planning-duurregel.
#   * De Planning toont Sportlink zoals het is. Code van de Planning gebruikt geen optimalisatieregels
#     (scheduler, bufferregels, planningsdoel).
#
# WAAROM: #1559 liet de optimalisatie de duurregel van de Planning gebruiken. Daardoor kreeg elke
# gesynchroniseerde wedstrijd buffer 0 en deden de bufferinstelling en de teamregels niets meer, zonder
# dat iemand het merkte. Deze guard maakt zo'n vermenging een rode build in plaats van een stille fout.
#
# Twee bestanden bevatten beide domeinen: AutoPlanService (beide tiers) bevat ook VeldbezettingAsync,
# het endpoint van de Planning. Daar geldt de regel per methode: Sportlink-duur alleen binnen
# VeldbezettingAsync, optimalisatieregels alleen daarbuiten.
#
# Commentaar telt niet mee (een vermelding in commentaar is geen gebruik). Draagbaarheid (#1155):
# bash 3.2 én bash 5, POSIX awk.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

SPORTLINK_DUUR='VeldbezettingDuur|SportlinkSpeelduur|IsSportlinkLeidend|SportlinkLeidend|VeldplannerOverlay|VeldbezettingSportlinkOverlay|SportlinkVeldbezettingSamenvoeging'
OPTIMALISATIE='FieldScheduler|PlanningBufferRegels|PlanningConflictDetectie|AutoPlanExactApply|TeamBuffersVoorWedstrijden|BepaalPlanDoel'
PLANNING_METHODE='VeldbezettingAsync'

OPTIMALISATIE_BESTANDEN="
Planner.Shared/FieldScheduler.cs
Planner.Shared/FieldScheduler.Buffers.cs
Planner.Shared/AutoPlanExactApply.cs
Planner.Shared/AutoPlanRegels.cs
Planner.Shared/Planning/PlanningConflictRegels.cs
BlazorAdmin/Pages/VeldOptimalisatie.razor
BlazorAdmin/Pages/VeldOptimalisatie.razor.cs
BlazorAdmin/Services/VeldplanningConflictMeldingen.cs
FunctionApp/Planner/Services/AutoPlanService.ExactApply.cs
"
PLANNING_BESTANDEN="
Planner.Shared/Planning/SportlinkVeldbezettingSamenvoeging.cs
Planner.Endpoints/Sportlink/VeldplannerOverlayCore.cs
FunctionApp.Postgres/Planner/VeldbezettingSportlinkOverlay.cs
FunctionApp/Planner/VeldbezettingSportlinkOverlay.cs
BlazorAdmin/Pages/Planning.razor
BlazorAdmin/Pages/Planning.razor.cs
"
GEMENGDE_BESTANDEN="
FunctionApp.Postgres/Planner/AutoPlanService.cs
FunctionApp/Planner/Services/AutoPlanService.cs
"

# scan <bestand> <patroon> <modus>  — modus: overal | buiten | binnen (t.o.v. PLANNING_METHODE)
scan() {
  awk -v pat="$2" -v modus="$3" -v methode="$PLANNING_METHODE" '
    # Een methodekop op klasseniveau (4 spaties) zet de huidige methode.
    /^    (public|internal|private|protected|static)[^=;]*\(/ {
      kop = $0; sub(/\(.*/, "", kop); n = split(kop, delen, " "); huidig = delen[n]
    }
    {
      regel = $0
      sub(/\/\/.*$/, "", regel)           # C#-commentaar
      gsub(/@\*.*\*@/, "", regel)         # Razor-commentaar op één regel
      if (regel !~ pat) next
      binnen = (huidig == methode)
      if (modus == "overal" || (modus == "binnen" && binnen) || (modus == "buiten" && !binnen))
        printf "%s:%d: %s\n", FILENAME, FNR, $0
    }' "$1"
}

fail=0
meld() {  # meld <bestand> <uitvoer> <reden>
  if [ -n "$2" ]; then
    echo "::error file=$1::$3"
    printf '%s\n' "$2"
    fail=1
  fi
}

for f in $OPTIMALISATIE_BESTANDEN; do
  [ -f "$f" ] || { echo "::error file=$f::Optimalisatiebestand ontbreekt; werk de lijst in deze guard bij."; fail=1; continue; }
  meld "$f" "$(scan "$f" "$SPORTLINK_DUUR" overal)" \
    "Veld optimalisatie gebruikt Sportlink-duur of de Planning-duurregel (WZ-ADR-014)."
done

for f in $PLANNING_BESTANDEN; do
  [ -f "$f" ] || { echo "::error file=$f::Planningbestand ontbreekt; werk de lijst in deze guard bij."; fail=1; continue; }
  meld "$f" "$(scan "$f" "$OPTIMALISATIE" overal)" \
    "De Planning gebruikt optimalisatieregels (WZ-ADR-014)."
done

for f in $GEMENGDE_BESTANDEN; do
  [ -f "$f" ] || { echo "::error file=$f::Bestand ontbreekt; werk de lijst in deze guard bij."; fail=1; continue; }
  grep -q "$PLANNING_METHODE(" "$f" || { echo "::error file=$f::$PLANNING_METHODE niet gevonden; werk deze guard bij."; fail=1; }
  meld "$f" "$(scan "$f" "$SPORTLINK_DUUR" buiten)" \
    "Optimalisatiecode buiten $PLANNING_METHODE gebruikt Sportlink-duur (WZ-ADR-014)."
  meld "$f" "$(scan "$f" "$OPTIMALISATIE" binnen)" \
    "$PLANNING_METHODE (Planning) gebruikt optimalisatieregels (WZ-ADR-014)."
done

if [ "$fail" -ne 0 ]; then
  echo "Planning en Veld optimalisatie lopen door elkaar. Zie docs/ARCHITECTUUR.md, WZ-ADR-014."
  exit 1
fi
echo "OK — Planning en Veld optimalisatie zijn gescheiden (WZ-ADR-014)."
