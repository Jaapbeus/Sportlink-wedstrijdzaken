#!/usr/bin/env bash
# check-egressguard-dekking.sh (#1676)
#
# Uitgaande integraties controleren EgressGuard.ExternalIntegrationsAllowed() (#857).
#
# WAT DE GUARD SCANT
# ------------------
# Elk .cs-bestand (behalve bin/ en obj/) in de serverprojecten FunctionApp/, FunctionApp.Postgres/
# (inclusief Infrastructure/), Planner.Shared/ en Planner.Endpoints/ waarvan de code (zonder
# commentaar) een van deze tokens bevat: HttpClient, IHttpClientFactory, AddHttpClient,
# SocketsHttpHandler of HttpClientHandler.
#
# WAT DE GUARD EIST
# -----------------
# Zo'n bestand bevat een aanroep of methodereferentie van EgressGuard.ExternalIntegrationsAllowed,
# of staat met een reden in scripts/ci/egressguard-allowlist.txt. Niet als aanroep tellen:
# commentaar (hele regel, //-staart na code, /* ... */ op één regel), nameof(...) en tekst in een
# stringliteral. Een allowlistregel zonder reden, voor een bestand dat niet (meer) bestaat, geen
# HTTP-token meer heeft, de poort zelf bevat of buiten de scan valt, laat de guard ook falen.
#
# WAT DE GUARD NIET BEWIJST
# -------------------------
# - Dekking per codepad: de controle is per bestand. Een bestand met één poort en een tweede,
#   ongepoorte HTTP-aanroep blijft groen; dat vraagt een test of review.
# - Poorten in bestanden zonder HTTP-token: een endpoint dat een pipeline of client in een ánder
#   bestand aanroept (zoals Function1.cs, AdminSyncFunction.cs, SyncFunction.cs, SyncJobProcessor.cs
#   en AdminThemeFunction.cs) wordt niet gescand. Dat zo'n poort bestaat, volgt uit de reden in de
#   allowlist voor het bestand mét het token, en voor een deel uit unit-tests (Geocode, Extract,
#   de herstartaanroep, SyncJobProcessor); AdminSyncFunction.Trigger en SyncMatchesHttp hebben
#   geen route-test, omdat AdminEndpoint.ExecuteAsync eerst op de database wacht.
# - Ander uitgaand verkeer dan via deze typen (Graph-SDK, Azure-SDK-clients, SMTP) en andere
#   projecten (BlazorAdmin praat met de eigen API, niet met derden).
#
# Draagbaarheid (#1155): bash 3.2 én bash 5, POSIX ERE (geen declare -A, mapfile, ${x,,}, grep -P).

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

ALLOWLIST="scripts/ci/egressguard-allowlist.txt"
[ -f "$ALLOWLIST" ] || { echo "::error::$ALLOWLIST ontbreekt — deze guard bewijst dan niets."; exit 1; }

# Veld 1 bestandspad, veld 2+ reden.
if ! awk '!/^[[:space:]]*#/ && NF > 0 && NF < 2 { print NR": "$0; gevonden = 1 }
          END { exit gevonden ? 1 : 0 }' "$ALLOWLIST"; then
  echo "::error file=$ALLOWLIST::Elke uitzondering heeft een reden nodig (<bestandspad> <reden>)."
  exit 1
fi

HTTP_PATROON='(^|[^A-Za-z0-9_])(HttpClient|IHttpClientFactory|AddHttpClient|SocketsHttpHandler|HttpClientHandler)([^A-Za-z0-9_]|$)'
EGRESS_PATROON='EgressGuard\.ExternalIntegrationsAllowed'

# Code zonder commentaar: hele commentaarregels (//, ///, *, /*), /* ... */ binnen een regel en een
# //-staart na code. Een // direct na ':' (zoals in "https://") is geen commentaar.
zonder_commentaar() {
  { grep -vE '^[[:space:]]*(//|\*|/\*)' "$1" || true; } \
    | sed -E -e 's#/\*.*\*/##g' -e 's#(^|[^:])//.*$#\1#'
}

# Voor de poortcontrole ook zonder stringliterals en nameof(...): daar is de naam geen aanroep.
# Strings eerst weg, zodat een // in een string de regel niet afkapt.
alleen_aanroepen() {
  { grep -vE '^[[:space:]]*(//|\*|/\*)' "$1" || true; } \
    | sed -E -e 's/"([^"\\]|\\.)*"//g' -e 's#/\*.*\*/##g' -e 's#//.*$##' -e 's/nameof[[:space:]]*\([^)]*\)//g'
}

# Geen grep -q achter een pipe: die stopt bij de eerste treffer, sed krijgt SIGPIPE en met
# pipefail telt een treffer dan als "niet gevonden" (wisselend per bestandsgrootte).
heeft_http() { zonder_commentaar "$1" | grep -E "$HTTP_PATROON" >/dev/null; }
heeft_poort() { alleen_aanroepen "$1" | grep -E "$EGRESS_PATROON" >/dev/null; }

fail=0
gecontroleerd=0
met_poort=0
gebruikt_allowlist="$(mktemp)"
trap 'rm -f "$gebruikt_allowlist"' EXIT

in_allowlist() {
  awk -v pad="$1" '/^[[:space:]]*#/ || NF == 0 { next } $1 == pad { gevonden = 1 } END { exit gevonden ? 0 : 1 }' "$ALLOWLIST"
}

# -co: ook nog niet toegevoegde bestanden, zodat een nieuw bestand direct gemeten wordt.
for bestand in $(git ls-files -co --exclude-standard -- \
    'FunctionApp/*.cs' 'FunctionApp.Postgres/*.cs' 'Planner.Shared/*.cs' 'Planner.Endpoints/*.cs'); do
  case "$bestand" in
    */obj/*|*/bin/*) continue ;;
  esac
  [ -f "$bestand" ] || continue

  heeft_http "$bestand" || continue
  gecontroleerd=$((gecontroleerd + 1))

  if heeft_poort "$bestand"; then
    met_poort=$((met_poort + 1))
    continue
  fi
  if in_allowlist "$bestand"; then
    echo "$bestand" >> "$gebruikt_allowlist"
    continue
  fi
  echo "::error file=$bestand::Gebruikt HttpClient/IHttpClientFactory/HTTP-handler zonder EgressGuard.ExternalIntegrationsAllowed() (#857). Voeg de controle toe, of zet het bestand met een reden (welke aanroepers de poort hebben) in $ALLOWLIST."
  fail=1
done

# Dode of overbodige allowlistregels.
while IFS= read -r pad; do
  [ -n "$pad" ] || continue
  if [ ! -f "$pad" ]; then
    echo "::error file=$ALLOWLIST::Allowlistregel wijst naar een bestand dat niet bestaat: $pad"
    fail=1
  elif ! heeft_http "$pad"; then
    echo "::error file=$ALLOWLIST::Allowlistregel voor $pad is overbodig: geen HttpClient-/handlergebruik."
    fail=1
  elif heeft_poort "$pad"; then
    echo "::error file=$ALLOWLIST::Allowlistregel voor $pad is overbodig: het bestand controleert EgressGuard zelf."
    fail=1
  elif ! grep -qxF "$pad" "$gebruikt_allowlist"; then
    echo "::error file=$ALLOWLIST::Allowlistregel voor $pad valt buiten de scan (ander project, bin/ of obj/)."
    fail=1
  fi
done < <(awk '/^[[:space:]]*#/ || NF == 0 { next } { print $1 }' "$ALLOWLIST")

[ "$fail" -eq 0 ] || exit 1
echo "OK — $gecontroleerd bestanden met uitgaand HTTP-verkeer: $met_poort met EgressGuard-aanroep, $((gecontroleerd - met_poort)) met een gemotiveerde uitzondering."
