#!/usr/bin/env bash
# check-egressguard-dekking.sh (#1676)
#
# Uitgaande integraties controleren EgressGuard.ExternalIntegrationsAllowed() (#857).
#
# WAT DE GUARD SCANT
# ------------------
# Elk .cs-bestand (behalve bin/ en obj/) in de serverprojecten FunctionApp/, FunctionApp.Postgres/
# (inclusief Infrastructure/), Planner.Shared/ en Planner.Endpoints/ waarvan de code, zonder
# commentaar en preprocessorregels, een token uit HTTP_TYPEN (typen voor uitgaand verkeer:
# HttpClient, handlers, WebClient/WebRequest, sockets, SmtpClient, ...) of HTTP_METHODEN
# (HttpClient-specifieke methoden zoals GetStringAsync) bevat. Tekst in stringliterals telt hier
# wél mee (strenger).
#
# WAT DE GUARD EIST
# -----------------
# 1. Zo'n bestand bevat de naam EgressGuard.ExternalIntegrationsAllowed als code (aanroep of
#    methodereferentie), of staat met een reden in scripts/ci/egressguard-allowlist.txt.
#    Niet als code tellen (zie ontleed_cs hieronder):
#    - commentaar: //..., /* ... */, ook over meerdere regels;
#    - stringliterals: gewoon ("..", \ is escape), verbatim (@"..", "" is escape, \ niet), raw
#      ("""..""", ook over meerdere regels), en interpolated ($"..", $@"..", $"""..""") —
#      inclusief de interpolatiegaten {..}, die dus ook niet als code tellen; een gat wordt wél
#      als code ontleed, zodat een string erin de omringende string niet afsluit (ook bij {{,
#      opmaak na ':', raw $$ en gaten over meerdere regels);
#    - karakterliterals ('"', '\'');
#    - preprocessorregels (#region, #pragma, ...) en alles tussen #if en de bijbehorende #endif,
#      in élke tak (#else/#elif inbegrepen);
#    - nameof(...), mits op één regel.
#    De naam moet als geheel woord voorkomen (geen MijnEgressGuard of ...AllowedOoit).
# 2. Geen allowlistregel zonder reden, dubbel, voor een bestand dat niet (meer) bestaat, geen
#    HTTP-token meer heeft, de poort zelf bevat of buiten de scan valt.
# 3. In SyncJobProcessor.cs (beide tiers) staat ná de signatuur "Task Run(" een "await <helper>("
#    vóór de eerste MarkRunningAsync (volgorde in de brontekst; of het resultaat tot een return
#    leidt, controleert de guard niet — dat bewijzen de unit-tests van de helper samen met review).
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
# - Ander uitgaand verkeer dan via deze typen (Graph-SDK, Azure-SDK-clients), generieke methoden
#   (SendAsync, GetAsync) op een client die in een ander bestand getypeerd is, en andere projecten
#   (BlazorAdmin praat met de eigen API, niet met derden).
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

# Typen voor uitgaand verkeer, plus de HttpClient-specifieke methoden: die vangen een client die
# elders getypeerd is (partial class, fabriekshelper met var) zonder typenaam in dit bestand.
HTTP_TYPEN='HttpClient|IHttpClientFactory|AddHttpClient|SocketsHttpHandler|HttpClientHandler|HttpMessageHandler|HttpMessageInvoker|HttpRequestMessage|WebClient|WebRequest|HttpWebRequest|TcpClient|UdpClient|Socket|ClientWebSocket|SmtpClient'
HTTP_METHODEN='GetStringAsync|GetByteArrayAsync|GetStreamAsync|GetFromJsonAsync|GetFromJsonAsAsyncEnumerable|PostAsJsonAsync|PutAsJsonAsync|PatchAsJsonAsync|DeleteFromJsonAsync'
HTTP_PATROON="(^|[^A-Za-z0-9_])($HTTP_TYPEN|$HTTP_METHODEN)([^A-Za-z0-9_]|$)"
EGRESS_PATROON='(^|[^A-Za-z0-9_])EgressGuard\.ExternalIntegrationsAllowed([^A-Za-z0-9_]|$)'
EGRESS_RUW='EgressGuard\.ExternalIntegrationsAllowed'

# Toestandsmachine over C#-brontekst, teken voor teken, ook over regelgrenzen (POSIX awk).
# modus=code    : commentaar en preprocessorregels weg, stringinhoud blijft staan (HTTP-tokens).
# modus=aanroep : ook stringinhoud, karakterliterals, #if-blokken en interpolatiegaten weg.
# Toestanden: C code, B blokcommentaar, S/IS gewone string, V/IV verbatim, R/IR raw (rawn
# aanhalingstekens; dol dollartekens), F opmaak na ':' in een interpolatiegat. De I-varianten
# zijn interpolated: een '{' (raw: dol of meer) opent een gat met gewone C#-code, die als code
# wordt ontleed (geneste strings, commentaar, karakterliterals, accolades) tot de bijbehorende
# '}'. De gaten staan op een stapel (gat = diepte), zodat een string in een gat de omringende
# string niet afsluit en een gat over meerdere regels (C# 11) de string niet beëindigt.
ONTLEED_AWK='
function reeks(s, p, ch,   r) { r = 0; while (substr(s, p + r, 1) == ch) r++; return r }
function kopie(t) { if (modus == "code") uit = uit t }
function code(t) { if (modus == "code" || gat == 0) uit = uit t }
function open_gat(terug) {
  gat++; gst[gat] = terug; grawn[gat] = rawn; gdol[gat] = dol; gbr[gat] = 0; gpar[gat] = 0; st = "C"
}
function sluit_gat(   r, m) {
  r = reeks(regel, i, "}"); m = (gst[gat] == "IR") ? gdol[gat] : 1
  i += (r < m) ? r : m
  st = gst[gat]; rawn = grawn[gat]; dol = gdol[gat]; gat--
}
BEGIN { st = "C"; diepte = 0; gat = 0 }
{
  regel = $0; uit = ""; i = 1; n = length(regel)
  if (st == "C" && regel ~ /^[[:space:]]*#/) {
    if (modus == "aanroep") {
      if (regel ~ /^[[:space:]]*#[[:space:]]*if([^A-Za-z0-9_]|$)/) diepte++
      else if (regel ~ /^[[:space:]]*#[[:space:]]*endif([^A-Za-z0-9_]|$)/ && diepte > 0) diepte--
    }
    print ""; next
  }
  if (modus == "aanroep" && st == "C" && diepte > 0) { print ""; next }
  while (i <= n) {
    c = substr(regel, i, 1); c2 = substr(regel, i + 1, 1)
    if (st == "B") {
      if (c == "*" && c2 == "/") { st = "C"; code(" "); i += 2 } else i++
      continue
    }
    if (st == "S" || st == "IS") {
      if (c == "\\") { kopie(substr(regel, i, 2)); i += 2; continue }
      if (c == "\"") { st = "C"; code("\""); i++; continue }
      if (st == "IS" && c == "{") {
        if (c2 == "{") { kopie("{{"); i += 2; continue }
        i++; open_gat("IS"); continue
      }
      kopie(c); i++; continue
    }
    if (st == "V" || st == "IV") {
      if (c == "\"") {
        if (c2 == "\"") { kopie("\"\""); i += 2; continue }
        st = "C"; code("\""); i++; continue
      }
      if (st == "IV" && c == "{") {
        if (c2 == "{") { kopie("{{"); i += 2; continue }
        i++; open_gat("IV"); continue
      }
      kopie(c); i++; continue
    }
    if (st == "R" || st == "IR") {
      if (c == "\"") {
        r = reeks(regel, i, "\"")
        if (r >= rawn) { st = "C"; code("\""); i += r; continue }
        kopie(substr(regel, i, r)); i += r; continue
      }
      if (st == "IR" && c == "{") {
        r = reeks(regel, i, "{")
        if (r >= dol) { kopie(substr(regel, i, r - dol)); i += r; open_gat("IR"); continue }
        kopie(substr(regel, i, r)); i += r; continue
      }
      kopie(c); i++; continue
    }
    if (st == "F") {
      if (c == "}") { sluit_gat(); continue }
      kopie(c); i++; continue
    }
    if (c == "/" && c2 == "/") break
    if (c == "/" && c2 == "*") { st = "B"; i += 2; continue }
    if (c == sq) {
      if (c2 == "\\") { j = index(substr(regel, i + 3), sq); i = (j > 0) ? i + 3 + j : n + 1 }
      else i += 3
      code(" "); continue
    }
    if (c == "\"" || c == "@" || c == "$") {
      k = i; verb = 0; ip = 0
      while (substr(regel, k, 1) == "@" || substr(regel, k, 1) == "$") {
        if (substr(regel, k, 1) == "@") verb = 1; else ip++
        k++
      }
      if (substr(regel, k, 1) == "\"") {
        q = reeks(regel, k, "\""); code("\"")
        if (verb) { st = ip ? "IV" : "V"; i = k + 1; continue }
        if (q >= 3) { st = ip ? "IR" : "R"; rawn = q; dol = ip; i = k + q; continue }
        if (q == 2) { code("\""); i = k + 2; continue }
        st = ip ? "IS" : "S"; i = k + 1; continue
      }
    }
    if (gat > 0) {
      if (c == "{") gbr[gat]++
      else if (c == "(" || c == "[") gpar[gat]++
      else if (c == ")" || c == "]") { if (gpar[gat] > 0) gpar[gat]-- }
      else if (c == "}") { if (gbr[gat] > 0) gbr[gat]--; else { sluit_gat(); continue } }
      else if (c == ":" && gbr[gat] == 0 && gpar[gat] == 0) {
        if (c2 == ":") { code("::"); i += 2; continue }
        st = "F"; i++; continue
      }
    }
    code(c); i++
  }
  if (st == "S" || st == "IS") st = "C"
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

# Volgorde in SyncJobProcessor.Run: ná de signatuur "Task Run(" moet "await <helper>(" (als heel
# woord, dus geen definitie, overload-definitie of naam met voorvoegsel) vóór de eerste
# MarkRunningAsync staan; een definitie of vermelding boven Run telt niet.
controleer_volgorde() {  # <bestand> <helpernaam>
  if [ ! -f "$1" ]; then
    echo "::error file=$1::Bestand ontbreekt; de volgordecontrole van SyncJobProcessor.Run bewijst dan niets."
    fail=1; return
  fi
  if ! alleen_aanroepen "$1" | awk -v h="$2" '
      !rs && /Task[[:space:]]+Run[[:space:]]*\(/ { rs = NR; next }
      rs && !hr && $0 ~ ("(^|[^A-Za-z0-9_])await[[:space:]]+" h "[[:space:]]*\\(") { hr = NR }
      rs && !mr && /(^|[^A-Za-z0-9_])MarkRunningAsync[[:space:]]*\(/ { mr = NR }
      END { exit (rs && hr && mr && hr < mr) ? 0 : 1 }'; then
    echo "::error file=$1::SyncJobProcessor.Run roept $2 niet aan vóór MarkRunningAsync (#1676)."
    fail=1
  fi
}
controleer_volgorde "FunctionApp/Sync/SyncJobProcessor.cs" "BlokkeerBuitenProductieAsync"
controleer_volgorde "FunctionApp.Postgres/Sync/SyncJobProcessor.cs" "OverslaanZonderUitgaandVerkeerAsync"

[ "$fail" -eq 0 ] || exit 1
echo "OK — $gecontroleerd bestanden met uitgaand HTTP-verkeer: $met_poort met EgressGuard-aanroep, $((gecontroleerd - met_poort)) met een gemotiveerde uitzondering; SyncJobProcessor-volgorde klopt op beide tiers."
