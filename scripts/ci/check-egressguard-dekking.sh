#!/usr/bin/env bash
# check-egressguard-dekking.sh (#1676)
#
# Uitgaande integraties controleren EgressGuard.ExternalIntegrationsAllowed() (#857).
#
# WAT DE GUARD CONTROLEERT
# ------------------------
# Elk .cs-bestand in FunctionApp/ en FunctionApp.Postgres/, buiten een map Infrastructure/, dat
# HttpClient, IHttpClientFactory of AddHttpClient gebruikt, bevat ook een aanroep of methodereferentie van
# EgressGuard.ExternalIntegrationsAllowed (een naamvermelding of commentaar telt niet), of staat met reden in scripts/ci/egressguard-allowlist.txt.
#
# De controle is per bestand, niet per aanroep: hij bewijst dat de poort in het bestand
# voorkomt, niet dat elk codepad erlangs loopt. Een bestand waarvan de poort bij de aanroepers
# zit, hoort daarom met die reden in de allowlist. Een dode allowlistregel (bestand bestaat niet,
# of heeft geen HttpClient-gebruik meer) is een fout, zodat de lijst niet dichtgroeit.
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

HTTP_PATROON='(^|[^A-Za-z0-9_])(HttpClient|IHttpClientFactory|AddHttpClient)([^A-Za-z0-9_]|$)'
EGRESS_PATROON='EgressGuard\.ExternalIntegrationsAllowed'

# Zonder commentaarregels (//, ///, *, /*).
zonder_commentaar() {
  grep -vE '^[[:space:]]*(//|\*|/\*)' "$1" || true
}

fail=0
gecontroleerd=0
gebruikt_allowlist="$(mktemp)"
trap 'rm -f "$gebruikt_allowlist"' EXIT

in_allowlist() {
  awk -v pad="$1" '/^[[:space:]]*#/ || NF == 0 { next } $1 == pad { gevonden = 1 } END { exit gevonden ? 0 : 1 }' "$ALLOWLIST"
}

# -co: ook nog niet toegevoegde bestanden, zodat een nieuw bestand direct gemeten wordt.
for bestand in $(git ls-files -co --exclude-standard -- 'FunctionApp/*.cs' 'FunctionApp.Postgres/*.cs'); do
  case "$bestand" in
    */obj/*|*/bin/*|*/Infrastructure/*) continue ;;
  esac
  [ -f "$bestand" ] || continue

  inhoud="$(zonder_commentaar "$bestand")"
  printf '%s\n' "$inhoud" | grep -qE "$HTTP_PATROON" || continue
  gecontroleerd=$((gecontroleerd + 1))

  if printf '%s\n' "$inhoud" | grep -q "$EGRESS_PATROON"; then
    continue
  fi
  if in_allowlist "$bestand"; then
    echo "$bestand" >> "$gebruikt_allowlist"
    continue
  fi
  echo "::error file=$bestand::Gebruikt HttpClient/IHttpClientFactory zonder EgressGuard.ExternalIntegrationsAllowed() (#857). Voeg de controle toe, of zet het bestand met reden in $ALLOWLIST."
  fail=1
done

# Dode allowlistregels: bestand bestaat niet, heeft geen HttpClient meer, of heeft de poort zelf
# gekregen (dan is de uitzondering overbodig).
while IFS= read -r pad; do
  [ -n "$pad" ] || continue
  if [ ! -f "$pad" ]; then
    echo "::error file=$ALLOWLIST::Allowlistregel wijst naar een bestand dat niet bestaat: $pad"
    fail=1
  elif ! zonder_commentaar "$pad" | grep -qE "$HTTP_PATROON"; then
    echo "::error file=$ALLOWLIST::Allowlistregel voor $pad is overbodig: geen HttpClient-gebruik."
    fail=1
  elif zonder_commentaar "$pad" | grep -q "$EGRESS_PATROON"; then
    echo "::error file=$ALLOWLIST::Allowlistregel voor $pad is overbodig: het bestand controleert EgressGuard zelf."
    fail=1
  elif ! grep -qxF "$pad" "$gebruikt_allowlist"; then
    echo "::error file=$ALLOWLIST::Allowlistregel voor $pad staat in Infrastructure/ of buiten de scan."
    fail=1
  fi
done < <(awk '/^[[:space:]]*#/ || NF == 0 { next } { print $1 }' "$ALLOWLIST")

[ "$fail" -eq 0 ] || exit 1
echo "OK — $gecontroleerd bestanden met uitgaand HTTP-verkeer, elk met EgressGuard of een gemotiveerde uitzondering."
