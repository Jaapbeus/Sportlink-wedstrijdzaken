#!/usr/bin/env bash
# check-endpoint-autorisatie.sh (#1350)
#
# Elk HTTP-endpoint op beide tiers autoriseert via de centrale wrapper — nooit via een eigen,
# losse aanroep van EasyAuthHelper.
#
# WAAROM DIT SCRIPT BESTAAT
# -------------------------
# Tot #1350 liepen 49 van de 80 admin-endpoints via AdminEndpoint.ExecuteAsync en riepen de
# overige 31 EasyAuthHelper.RequireAdmin zelf aan, elk met een eigen kopie van de
# correlatie-id, de scope, de databasewacht en de 500-fallback. De SQL Server-tier had daar
# bovendien nog een derde patroon naast (PlannerFunction.HandleAsync). Drie manieren om dezelfde
# poort te bouwen betekent drie plekken waar een volgende wijziging er één kan vergeten — en een
# vergeten poort geeft niemand een foutmelding. De regel is daarom: één wrapper, en elke
# uitzondering met naam en reden in een allowlist.
#
# WAT DE GUARD CONTROLEERT
# ------------------------
# Per [Function("...")]-blok met een HttpTrigger in FunctionApp/ en FunctionApp.Postgres/:
#
#   1. Het blok roept NIET zelf EasyAuthHelper.RequireAdmin / RequireWedstrijdzaken / RequireRole
#      aan — tenzij <functienaam> <bestandspad> met reden in de allowlist staat.
#   2. Het blok gaat WEL langs een van de bekende poorten (AdminEndpoint.ExecuteAsync,
#      AdminEndpoint.ExecuteZonderDatabaseAsync of SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync),
#      óf staat als 'anoniem' in de allowlist, óf staat daar als toegestane directe aanroep.
#   3. De HttpTrigger staat op AuthorizationLevel.Anonymous: een Function- of Master key zou een
#      tweede, identiteitsloze toegangsweg naast Easy Auth openhouden (#1350, onderdeel 3).
#
# Een blok begint bij [Function("...")] en loopt tot het volgende [Function(...)] of het einde van
# het bestand — dezelfde knip als de Layer-5-scan in scripts/azure/Verify-AzureAuthSetup.ps1.
# Blokken zonder HttpTrigger (timers, queues) worden overgeslagen: die hebben geen aanroeper met
# een rol. Commentaarregels tellen niet mee als aanroep.
#
# De wrapperbestanden zelf (AdminEndpoint.cs, SportlinkEndpointSupport.cs, EasyAuthHelper.cs)
# bevatten geen [Function(...)] en vallen dus buiten de scan.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5, POSIX awk, POSIX ERE (grep -E, nooit -P).

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

ALLOWLIST="scripts/ci/endpoint-autorisatie-allowlist.txt"
[ -f "$ALLOWLIST" ] || { echo "::error::$ALLOWLIST ontbreekt — deze guard bewijst dan niets."; exit 1; }

# Regel zonder reden = fout. Veld 1 soort (direct|anoniem), veld 2 functienaam, veld 3 pad, veld 4+ reden.
if awk '!/^[[:space:]]*#/ && NF > 0 && NF < 4 { print NR": "$0; gevonden = 1 }
        END { exit gevonden ? 1 : 0 }' "$ALLOWLIST"; then
  :
else
  echo "::error file=$ALLOWLIST::Elke uitzondering heeft een reden nodig (<direct|anoniem> <functienaam> <pad> <reden>)."
  exit 1
fi

# is_toegestaan <soort> <functienaam> <pad>
is_toegestaan() {
  awk -v soort="$1" -v naam="$2" -v pad="$3" '
    /^[[:space:]]*#/ || NF == 0 { next }
    $1 == soort && $2 == naam && $3 == pad { gevonden = 1 }
    END { exit gevonden ? 0 : 1 }
  ' "$ALLOWLIST"
}

fail=0
gecontroleerd=0
direct_toegestaan=0
anoniem_toegestaan=0

# Per bestand: awk knipt in blokken en drukt per HTTP-blok één regel af:
#   <functienaam>|<regelnr>|<niveau>|<direct 0/1>|<wrapper 0/1>
# Commentaarregels (//, ///, *) worden voor de patroonzoektocht overgeslagen.
for bestand in $(git ls-files -- 'FunctionApp/*.cs' 'FunctionApp.Postgres/*.cs'); do
  case "$bestand" in
    */obj/*|*/bin/*) continue ;;
  esac
  grep -q '\[Function(' "$bestand" || continue

  while IFS='|' read -r naam regelnr niveau direct wrapper; do
    [ -n "$naam" ] || continue
    gecontroleerd=$((gecontroleerd + 1))

    if [ "$niveau" != "Anonymous" ]; then
      echo "::error file=$bestand,line=$regelnr::$naam staat op AuthorizationLevel.$niveau. Elk endpoint hoort op Anonymous + Easy Auth-rolcontrole (#1350): een Function- of Master key is een identiteitsloze tweede toegangsweg."
      fail=1
    fi

    if [ "$direct" = "1" ]; then
      if is_toegestaan direct "$naam" "$bestand"; then
        direct_toegestaan=$((direct_toegestaan + 1))
      else
        echo "::error file=$bestand,line=$regelnr::$naam roept EasyAuthHelper.Require* zelf aan. Gebruik AdminEndpoint.ExecuteAsync / ExecuteZonderDatabaseAsync (of SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync), of voeg een gemotiveerde 'direct'-regel toe aan $ALLOWLIST."
        fail=1
      fi
    elif [ "$wrapper" = "0" ]; then
      if is_toegestaan anoniem "$naam" "$bestand"; then
        anoniem_toegestaan=$((anoniem_toegestaan + 1))
      else
        echo "::error file=$bestand,line=$regelnr::$naam heeft GEEN autorisatiepoort: geen AdminEndpoint.Execute*, geen SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync. Een bewust anoniem endpoint hoort met reden als 'anoniem' in $ALLOWLIST."
        fail=1
      fi
    fi
  done < <(awk '
    function flush() {
      if (naam != "" && is_http) printf "%s|%d|%s|%d|%d\n", naam, regel, niveau, direct, wrapper
      naam = ""; is_http = 0; direct = 0; wrapper = 0; niveau = "?"
    }
    {
      if (match($0, /\[Function\("[^"]+"\)\]/)) {
        flush()
        s = substr($0, RSTART, RLENGTH)
        sub(/^\[Function\("/, "", s); sub(/"\)\]$/, "", s)
        naam = s; regel = NR
      }
      if (naam == "") next
      # Commentaar telt niet mee als aanroep.
      t = $0; sub(/^[[:space:]]+/, "", t)
      if (t ~ /^(\/\/|\*|\/\*)/) next
      if ($0 ~ /HttpTrigger\(/) {
        is_http = 1
        if (match($0, /AuthorizationLevel\.[A-Za-z]+/)) {
          n = substr($0, RSTART, RLENGTH); sub(/^AuthorizationLevel\./, "", n); niveau = n
        }
      }
      if ($0 ~ /EasyAuthHelper\.(RequireAdmin|RequireWedstrijdzaken|RequireRole|RequireAuthenticated)\(/) direct = 1
      if ($0 ~ /AdminEndpoint\.Execute(ZonderDatabase)?Async\(/) wrapper = 1
      if ($0 ~ /SportlinkEndpointSupport\.ExecuteWedstrijdzakenAsync\(/) wrapper = 1
    }
    END { flush() }
  ' "$bestand")
done

if [ "$gecontroleerd" -eq 0 ]; then
  echo "::error::Geen enkel HTTP-endpoint gevonden — deze guard bewijst dan niets. Zijn FunctionApp/ of FunctionApp.Postgres/ hernoemd?"
  exit 1
fi

# Omgekeerd: elke allowlist-regel moet nog naar een bestaand bestand met die functienaam wijzen —
# een dode regel is precies de rot die een allowlist onbetrouwbaar maakt.
while read -r soort naam pad rest; do
  case "$soort" in ''|'#'*) continue ;; esac
  if [ ! -f "$pad" ] || ! grep -q "\[Function(\"$naam\")\]" "$pad"; then
    echo "::error file=$ALLOWLIST::Regel voor '$naam' in $pad wijst niet naar een bestaand [Function(\"$naam\")] — verwijder de dode uitzondering."
    fail=1
  fi
done < "$ALLOWLIST"

echo "Endpoint-autorisatie: $gecontroleerd HTTP-endpoints gecontroleerd, $direct_toegestaan gemotiveerde directe aanroep(en), $anoniem_toegestaan gemotiveerd anoniem."

[ "$fail" -eq 0 ] || exit 1
echo "OK — elk HTTP-endpoint autoriseert via de wrapper, of staat met reden in de allowlist."
