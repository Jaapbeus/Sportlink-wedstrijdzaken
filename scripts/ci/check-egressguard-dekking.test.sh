#!/usr/bin/env bash
# check-egressguard-dekking.test.sh (#1676)
#
# Bewijst dat check-egressguard-dekking.sh rood kan worden: elke test maakt tijdelijk een
# overtreding, eist exit 1 en herstelt daarna. Er wordt nooit iets gecommit.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5.

set -uo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

GUARD="scripts/ci/check-egressguard-dekking.sh"
ALLOWLIST="scripts/ci/egressguard-allowlist.txt"
PROEF="FunctionApp/Admin/ProefEgressDekking1676.cs"
PROEF_PG="FunctionApp.Postgres/Admin/ProefEgressDekking1676.cs"
TMP_B="$(mktemp)"
cp "$ALLOWLIST" "$TMP_B"
opruimen() { rm -f "$PROEF" "$PROEF_PG"; cp "$TMP_B" "$ALLOWLIST"; rm -f "$TMP_B"; }
trap opruimen EXIT

mislukt=0
verwacht() {  # verwacht <slagen|falen> <omschrijving>
  bash "$GUARD" >/dev/null 2>&1; rc=$?
  if [ "$1" = "slagen" ] && [ "$rc" -eq 0 ]; then echo "  ok — $2"
  elif [ "$1" = "falen" ] && [ "$rc" -eq 1 ]; then echo "  ok — $2"
  else echo "::error::TEST MISLUKT — $2 (exit $rc, verwacht $1)"; mislukt=$((mislukt + 1)); fi
}

verwacht slagen "schone werkboom is groen"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); }\n' > "$PROEF"
verwacht falen "HttpClient zonder EgressGuard (SQL Server-tier)"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); }\n' > "$PROEF_PG"
verwacht falen "HttpClient zonder EgressGuard (Postgres-tier)"
rm -f "$PROEF_PG"

printf 'namespace Proef;\npublic static class P { static void M(IHttpClientFactory f) { } }\n' > "$PROEF"
verwacht falen "IHttpClientFactory zonder EgressGuard"
rm -f "$PROEF"

# Een vermelding in commentaar is geen controle.
printf '// EgressGuard hoort hier\nnamespace Proef;\npublic static class P { static readonly HttpClient C = new(); }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in commentaar"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static bool Ok() => EgressGuard.ExternalIntegrationsAllowed(); }\n' > "$PROEF"
verwacht slagen "HttpClient mét EgressGuard-aanroep"
rm -f "$PROEF"

printf 'FunctionApp/Admin/BestaatNiet1676.cs  proefregel voor een bestand dat niet bestaat\n' >> "$ALLOWLIST"
verwacht falen "dode regel in de allowlist"
cp "$TMP_B" "$ALLOWLIST"

{ cat "$TMP_B"; printf 'FunctionApp/Admin/AdminSyncFunction.cs\n'; } > "$ALLOWLIST"
verwacht falen "allowlistregel zonder reden"
cp "$TMP_B" "$ALLOWLIST"

verwacht slagen "hersteld: schone werkboom is weer groen"

[ "$mislukt" -eq 0 ] || { echo "$mislukt test(s) mislukt."; exit 1; }
echo "OK — alle negatieve tests van de EgressGuard-dekkingsguard slaagden."
