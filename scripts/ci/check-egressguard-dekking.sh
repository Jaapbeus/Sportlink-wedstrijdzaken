#!/usr/bin/env bash
# check-egressguard-dekking.sh (#1676)
#
# Uitgaande integraties controleren EgressGuard.ExternalIntegrationsAllowed() (#857).
#
# WAT DE GUARD SCANT
# ------------------
# Elk .cs-bestand (behalve bin/ en obj/) in de serverprojecten FunctionApp/, FunctionApp.Postgres/
# (inclusief Infrastructure/), Planner.Shared/ en Planner.Endpoints/ waarvan de code, zonder
# commentaar, een van deze tokens bevat: HttpClient, IHttpClientFactory, AddHttpClient,
# SocketsHttpHandler of HttpClientHandler. Tekst in stringliterals telt hier wél mee (strenger).
#
# WAT DE GUARD EIST
# -----------------
# 1. Zo'n bestand bevat de naam EgressGuard.ExternalIntegrationsAllowed als code (aanroep of
#    methodereferentie), of staat met een reden in scripts/ci/egressguard-allowlist.txt.
#    Niet als code tellen (zie ontleed_cs hieronder):
#    - commentaar: //..., /* ... */, ook over meerdere regels;
#    - stringliterals: gewoon ("..", \ is escape), verbatim (@"..", "" is escape, \ niet), raw
#      ("""..""", ook over meerdere regels), en interpolated ($"..", $@"..", $"""..""") —
#      inclusief de interpolatiegaten {..}, die dus ook niet als code tellen;
#    - karakterliterals ('"', '\'');
#    - preprocessorregels (#region, #pragma, ...) en alles tussen #if en de bijbehorende #endif,
#      in élke tak (#else/#elif inbegrepen);
#    - nameof(...), mits op één regel.
#    De naam moet als geheel woord voorkomen (geen MijnEgressGuard of ...AllowedOoit).
# 2. Geen allowlistregel zonder reden, dubbel, voor een bestand dat niet (meer) bestaat, geen
#    HTTP-token meer heeft, de poort zelf bevat of buiten de scan valt.
# 3. In SyncJobProcessor.cs (beide tiers) roept Run de poortbeslissing aan vóór de eerste
#    MarkRunningAsync (volgorde in de brontekst; of het resultaat tot een return leidt, controleert
#    de guard niet — dat bewijzen de unit-tests van de helper samen met review).
#
# WAT DE GUARD NIET BEWIJST
# -------------------------
# - De guard ontleedt geen C#: hij controleert de naam, niet welk type EgressGuard is, en niet of
#   de aanroep bereikbaar is (if (false), ongebruikte lambda).
# - Dekking per codepad: de controle is per bestand. Een bestand met één poort en een tweede,
#   ongepoorte HTTP-aanroep blijft groen; dat vraagt een test of review.
# - Poorten in bestanden zonder HTTP-token: een endpoint dat een pipeline of client in een ánder
#   bestand aanroept (zoals Function1.cs, AdminSyncFunction.cs, SyncFunction.cs en
#   AdminThemeFunction.cs) wordt niet gescand. Dat zo'n poort bestaat, volgt uit de reden in de
#   allowlist voor het bestand mét het token, en voor een deel uit unit-tests (Geocode, Extract,
#   de herstartaanroep, de SyncJobProcessor-helper); AdminSyncFunction.Trigger en SyncMatchesHttp
#   hebben geen route-test, omdat AdminEndpoint.ExecuteAsync eerst op de database wacht.
# - Ander uitgaand verkeer dan via deze typen (Graph-SDK, Azure-SDK-clients, SMTP) en andere
#   projecten (BlazorAdmin praat met de eigen API, niet met derden).
#
# Draagbaarheid (#1155): bash 3.2 én bash 5, POSIX awk/ERE (geen declare -A, mapfile, ${x,,},
# grep -P, gawk-extensies).

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
if ! awk '/^[[:space:]]*#/ || NF == 0 { next } ($1 in gezien) { print "dubbel: "$1; dubbel = 1 } { gezien[$1] = 1 }
          END { exit dubbel ? 1 : 0 }' "$ALLOWLIST"; then
  echo "::error file=$ALLOWLIST::Elk bestand staat hoogstens één keer in de allowlist."
  exit 1
fi

HTTP_PATROON='(^|[^A-Za-z0-9_])(HttpClient|IHttpClientFactory|AddHttpClient|SocketsHttpHandler|HttpClientHandler)([^A-Za-z0-9_]|$)'
EGRESS_PATROON='(^|[^A-Za-z0-9_])EgressGuard\.ExternalIntegrationsAllowed([^A-Za-z0-9_]|$)'
EGRESS_RUW='EgressGuard\.ExternalIntegrationsAllowed'

# Toestandsmachine over C#-brontekst, teken voor teken, ook over regelgrenzen (POSIX awk).
# modus=code    : commentaar weg, stringinhoud blijft staan (voor de HTTP-tokens).
# modus=aanroep : ook stringinhoud, karakterliterals, preprocessorregels en #if-blokken weg.
# Toestanden: C code, B blokcommentaar, S gewone string, V verbatim, R raw (rawn aanhalingstekens).
ONTLEED_AWK='
function quotes(s, p,   r) { r = 0; while (substr(s, p + r, 1) == "\"") r++; return r }
function kopie(t) { if (modus == "code") uit = uit t }
BEGIN { st = "C"; diepte = 0 }
{
  regel = $0; uit = ""; i = 1; n = length(regel)
  if (st == "C" && regel ~ /^[[:space:]]*#/) {
    if (modus == "aanroep") {
      if (regel ~ /^[[:space:]]*#[[:space:]]*if([^A-Za-z0-9_]|$)/) diepte++
      else if (regel ~ /^[[:space:]]*#[[:space:]]*endif([^A-Za-z0-9_]|$)/ && diepte > 0) diepte--
      print ""; next
    }
  }
  if (modus == "aanroep" && st == "C" && diepte > 0) { print ""; next }
  while (i <= n) {
    c = substr(regel, i, 1)
    if (st == "B") {
      if (c == "*" && substr(regel, i + 1, 1) == "/") { st = "C"; uit = uit " "; i += 2 } else i++
      continue
    }
    if (st == "S") {
      if (c == "\\") { kopie(substr(regel, i, 2)); i += 2; continue }
      if (c == "\"") { st = "C"; uit = uit "\""; i++; continue }
      kopie(c); i++; continue
    }
    if (st == "V") {
      if (c == "\"") {
        if (substr(regel, i + 1, 1) == "\"") { kopie("\"\""); i += 2; continue }
        st = "C"; uit = uit "\""; i++; continue
      }
      kopie(c); i++; continue
    }
    if (st == "R") {
      if (c == "\"") {
        r = quotes(regel, i)
        if (r >= rawn) { st = "C"; uit = uit "\""; i += r; continue }
        kopie(substr(regel, i, r)); i += r; continue
      }
      kopie(c); i++; continue
    }
    if (c == "/" && substr(regel, i + 1, 1) == "/") break
    if (c == "/" && substr(regel, i + 1, 1) == "*") { st = "B"; i += 2; continue }
    if (c == sq) {
      if (substr(regel, i + 1, 1) == "\\") { j = index(substr(regel, i + 3), sq); i = (j > 0) ? i + 3 + j : n + 1 }
      else i += 3
      uit = uit " "; continue
    }
    if (c == "\"" || c == "@" || c == "$") {
      k = i; verb = 0
      while (substr(regel, k, 1) == "@" || substr(regel, k, 1) == "$") { if (substr(regel, k, 1) == "@") verb = 1; k++ }
      if (substr(regel, k, 1) == "\"") {
        q = quotes(regel, k)
        if (verb) { st = "V"; uit = uit "\""; i = k + 1; continue }
        if (q >= 3) { st = "R"; rawn = q; uit = uit "\""; i = k + q; continue }
        if (q == 2) { uit = uit "\"\""; i = k + 2; continue }
        st = "S"; uit = uit "\""; i = k + 1; continue
      }
    }
    uit = uit c; i++
  }
  if (st == "S") st = "C"
  print uit
}'

# sq: het enkele aanhalingsteken, via -v omdat het programma zelf tussen enkele quotes staat.
ontleed_cs() { awk -v modus="$1" -v sq="'" "$ONTLEED_AWK" "$2"; }

# Geen grep -q achter een pipe: die stopt bij de eerste treffer, de schrijver krijgt SIGPIPE en
# met pipefail telt een treffer dan als "niet gevonden" (wisselend per bestandsgrootte).
# De goedkope voorselectie op de ruwe tekst is veilig: ontleden haalt alleen tekst weg.
heeft_http() {
  grep -E "$HTTP_PATROON" "$1" >/dev/null || return 1
  ontleed_cs code "$1" | grep -E "$HTTP_PATROON" >/dev/null
}
alleen_aanroepen() { ontleed_cs aanroep "$1" | sed -E 's/nameof[[:space:]]*\([^)]*\)//g'; }
heeft_poort() {
  grep -E "$EGRESS_RUW" "$1" >/dev/null || return 1
  alleen_aanroepen "$1" | grep -E "$EGRESS_PATROON" >/dev/null
}

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

# Volgorde in SyncJobProcessor.Run: de poortbeslissing vóór de eerste MarkRunningAsync. De eerste
# vermelding van de helper is de aanroep in Run (de definitie staat erna); ontbreekt die aanroep,
# dan is de eerste vermelding de definitie, ná MarkRunningAsync.
controleer_volgorde() {  # <bestand> <helpernaam>
  if [ ! -f "$1" ]; then
    echo "::error file=$1::Bestand ontbreekt; de volgordecontrole van SyncJobProcessor.Run bewijst dan niets."
    fail=1; return
  fi
  if ! alleen_aanroepen "$1" | awk -v h="$2(" '
      index($0, h) && !hr { hr = NR }
      /MarkRunningAsync[[:space:]]*\(/ && !mr { mr = NR }
      END { exit (hr && mr && hr < mr) ? 0 : 1 }'; then
    echo "::error file=$1::SyncJobProcessor.Run roept $2 niet aan vóór MarkRunningAsync (#1676)."
    fail=1
  fi
}
controleer_volgorde "FunctionApp/Sync/SyncJobProcessor.cs" "BlokkeerBuitenProductieAsync"
controleer_volgorde "FunctionApp.Postgres/Sync/SyncJobProcessor.cs" "OverslaanZonderUitgaandVerkeerAsync"

[ "$fail" -eq 0 ] || exit 1
echo "OK — $gecontroleerd bestanden met uitgaand HTTP-verkeer: $met_poort met EgressGuard-aanroep, $((gecontroleerd - met_poort)) met een gemotiveerde uitzondering; SyncJobProcessor-volgorde klopt op beide tiers."
