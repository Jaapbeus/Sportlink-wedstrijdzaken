#!/usr/bin/env bash
# check-codekwaliteit.test.sh (#1262)
#
# Negatieve tests voor de codekwaliteitsguards: bewijst dat ze rood worden van een overtreding.
#
# WAAROM DIT BESTAAT
# ------------------
# Een guard die groen is, bewijst niets — hij kan ook groen zijn omdat hij niets ziet. Dat is in
# dit project al twee keer gebeurd: de splinter-lints waren stille no-ops zolang de rollen anon en
# authenticated ontbraken, en een databaseguard was lokaal niet negatief te testen omdat de
# herstelde productiedump Supabase's eigen ensure_rls-trigger meebrengt.
#
# Elke test hieronder maakt tijdelijk een overtreding, draait de guard, en eist exit 1. Daarna
# wordt de werkboom hersteld. Er wordt nooit iets gecommit.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5.

set -uo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

geslaagd=0
mislukt=0
TMP_B="$(mktemp)"
trap 'rm -f "$TMP_B"' EXIT

# verwacht_falen <omschrijving> <commando...>
verwacht_falen() {
  omschrijving="$1"; shift
  if "$@" >/dev/null 2>&1; then
    echo "::error::NEGATIEVE TEST MISLUKT — $omschrijving: de guard bleef groen bij een overtreding."
    mislukt=$((mislukt + 1))
  else
    echo "  ok — $omschrijving"
    geslaagd=$((geslaagd + 1))
  fi
}

verwacht_slagen() {
  omschrijving="$1"; shift
  if "$@" >/dev/null 2>&1; then
    echo "  ok — $omschrijving"
    geslaagd=$((geslaagd + 1))
  else
    echo "::error::TEST MISLUKT — $omschrijving: de guard werd rood op een schone werkboom."
    mislukt=$((mislukt + 1))
  fi
}

herstel() {
  git checkout -- "$@" 2>/dev/null || true
}

echo "Positieve tests (schone werkboom moet groen zijn):"
verwacht_slagen "tier-duplicatie"      bash scripts/ci/check-tier-duplicatie.sh
verwacht_slagen "interne duplicatie"   bash scripts/ci/check-interne-duplicatie.sh
verwacht_slagen "blazor-codebehind"    bash scripts/ci/check-blazor-codebehind.sh
verwacht_slagen "valkuilen"            bash scripts/ci/check-codekwaliteit-valkuilen.sh
verwacht_slagen "bestandsgrootte"      bash scripts/ci/check-bestandsgrootte.sh
verwacht_slagen "regelregister"        bash scripts/ci/check-regelregister.sh
verwacht_slagen "AGENTS.md afgeleid"   python3 scripts/ci/genereer-agents-md.py
verwacht_slagen "tier-pariteit"        bash scripts/ci/check-tier-pariteit.sh

echo
echo "Negatieve tests (een overtreding moet rood zijn):"

# 1. Tier-duplicatie: maak het ene tierbestand een woordelijke kopie van het andere — precies
#    wat er bij een 1-op-1-poort gebeurt. Een paar losse regels erbij plakken is géén geldige test:
#    de meting telt de langste gemeenschappelijke deelrijen, en losse regels vallen daar vaak al
#    binnen. Dat heeft deze test bij het schrijven ervan ook eerst ten onrechte laten slagen.
proef_a="FunctionApp/Admin/AdminVoorkeurTijdenFunction.cs"
proef_b="FunctionApp.Postgres/Admin/AdminVoorkeurTijdenFunction.cs"
if [ -f "$proef_a" ] && [ -f "$proef_b" ]; then
  cp "$proef_b" "$TMP_B"
  cp "$proef_a" "$proef_b"
  verwacht_falen "tier-duplicatie stijgt" bash scripts/ci/check-tier-duplicatie.sh
  cp "$TMP_B" "$proef_b"
else
  echo "::error::Proefbestanden voor de tier-test ontbreken — de negatieve test is overgeslagen."
  mislukt=$((mislukt + 1))
fi

# 2. Blazor: een pagina mét code-behind krijgt er een @code-blok bij.
proef_razor="BlazorAdmin/Pages/Dagplanning.razor"
if [ -f "$proef_razor" ] && [ -f "$proef_razor.cs" ]; then
  printf '\n@code {\n    private int Proef => 1;\n}\n' >> "$proef_razor"
  verwacht_falen "@code naast een code-behind" bash scripts/ci/check-blazor-codebehind.sh
  herstel "$proef_razor"
else
  echo "::error::Proefpagina met code-behind ontbreekt — de negatieve test is overgeslagen."
  mislukt=$((mislukt + 1))
fi

# 2b. Interne duplicatie: kopieer een bestaand Postgres-tierbestand woordelijk naar een nieuw
#     bestand in dezelfde tier. check-tier-duplicatie.sh ziet dit niet (geen paar met FunctionApp/);
#     check-interne-duplicatie.sh moet het wel zien, want FunctionApp.Postgres/ blijft in de scan.
proef_intern="FunctionApp.Postgres/Admin/ProefInterneDuplicatie1263.cs"
cp "FunctionApp.Postgres/Admin/SportlinkExtensieRollenFunction.cs" "$proef_intern"
verwacht_falen "interne duplicatie stijgt" bash scripts/ci/check-interne-duplicatie.sh
rm -f "$proef_intern"

# 3. Valkuilen: elk van de vier patronen, op een plek die niet op de allowlist staat.
proef_cs="Planner.Shared/Proef1262.cs"
cat > "$proef_cs" <<'CS'
namespace Planner.Shared;
internal static class Proef1262
{
    internal static string? Resolve(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs)) return abs.ToString();
        return null;
    }
    internal static DateTime Nu() => DateTime.Now;
    internal const string Query = "SELECT GETDATE()";
}
CS
# git grep doorzoekt alleen getrackte paden. In CI is een nieuw bestand altijd gecommit, dus
# getrackt; lokaal moet het bestand in de index staan om gezien te worden. `git add -N` zet het
# erin zonder de inhoud te stagen — zonder deze regel bleef de guard groen bij een overtreding.
git add -N "$proef_cs" >/dev/null 2>&1 || true
verwacht_falen "valkuilpatronen in nieuwe code" bash scripts/ci/check-codekwaliteit-valkuilen.sh
git rm -q --cached "$proef_cs" >/dev/null 2>&1 || true
rm -f "$proef_cs"

# 4. Bestandsgrootte: een nieuw productiebestand van ruim 500 regels met een methode van ruim 80.
proef_groot="Planner.Shared/ProefGroot1262.cs"
{
  echo "namespace Planner.Shared;"
  echo "internal static class ProefGroot1262"
  echo "{"
  echo "    internal static int Lang()"
  echo "    {"
  echo "        var n = 0;"
  i=0
  while [ "$i" -lt 100 ]; do echo "        n += $i;"; i=$((i + 1)); done
  echo "        return n;"
  echo "    }"
  i=0
  while [ "$i" -lt 450 ]; do echo "    // opvulregel $i"; i=$((i + 1)); done
  echo "}"
} > "$proef_groot"
git add -N "$proef_groot" >/dev/null 2>&1 || true
verwacht_falen "te groot bestand en te lange methode" bash scripts/ci/check-bestandsgrootte.sh
git rm -q --cached "$proef_groot" >/dev/null 2>&1 || true
rm -f "$proef_groot"

# 5. Regelregister: een guard die niet in het register staat.
proef_guard="scripts/ci/check-proef1262.sh"
printf '#!/usr/bin/env bash\nexit 0\n' > "$proef_guard"
chmod +x "$proef_guard"
git add -N "$proef_guard" >/dev/null 2>&1 || true
verwacht_falen "guard buiten het register" bash scripts/ci/check-regelregister.sh
git rm -q --cached "$proef_guard" >/dev/null 2>&1 || true
rm -f "$proef_guard"

# 6. Tier-pariteit op TIMERS: een achtergrondtaak die maar op één tier bestaat.
#    Toegevoegd bij #1268. Die richting was tot dan ongedekt: de guard keek alleen naar HTTP-routes,
#    en juist daardoor kon de database-uitvalmonitor jarenlang op één tier staan zonder dat iets het
#    merkte — een timer geeft niemand een 404.
proef_timer="FunctionApp.Postgres/ProefTimer1268.cs"
cat > "$proef_timer" <<'CS'
using Microsoft.Azure.Functions.Worker;

namespace FunctionApp.Postgres;

internal static class ProefTimer1268
{
    [Function("ProefTimer1268")]
    public static void Run([TimerTrigger("0 0 3 * * *")] TimerInfo timer) { }
}
CS
git add -N "$proef_timer" >/dev/null 2>&1 || true
verwacht_falen "timer op maar één tier" bash scripts/ci/check-tier-pariteit.sh
git rm -q --cached "$proef_timer" >/dev/null 2>&1 || true
rm -f "$proef_timer"

# 7. AGENTS.md: een handmatige bewerking moet gezien worden.
printf '\n<!-- handmatige proefbewerking -->\n' >> AGENTS.md
verwacht_falen "handmatige bewerking van AGENTS.md" python3 scripts/ci/genereer-agents-md.py
# Herstel via de generator, niet via git checkout: AGENTS.md is een afgeleid bestand, en een
# checkout zou het terugzetten naar de laatste commit in plaats van naar de huidige CLAUDE.md.
python3 scripts/ci/genereer-agents-md.py --schrijf >/dev/null

# 8. Analyzers (#1300): de guard moet weigeren te meten zodra .editorconfig de drie regels niet
#    meer aanzet. Dat is de gevaarlijke faalwijze — zonder die controle telt hij stilzwijgend nul
#    en staat hij voor altijd groen, precies het patroon van de splinter-no-op uit §67.
#
#    Alleen DEZE negatieve test staat hier, en niet die op het aantal overtredingen: die vraagt een
#    volledige solution-build, en deze zelftest draait in CI vóór de buildstappen. De teltest is
#    handmatig uitgevoerd bij #1300 (een methode met complexiteit 40 toegevoegd → 20 > 19 → rood).
#    De controle hieronder breekt bewust af vóór de build, dus hij kost niets.
cp .editorconfig "$TMP_B"
grep -v 'CA1506' "$TMP_B" > .editorconfig
verwacht_falen "analyzers niet aangezet in .editorconfig" bash scripts/ci/check-analyzer-complexiteit.sh
cp "$TMP_B" .editorconfig

echo
echo "Resultaat: $geslaagd geslaagd, $mislukt mislukt."
[ "$mislukt" -eq 0 ] || exit 1
