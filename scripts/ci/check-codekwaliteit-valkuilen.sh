#!/usr/bin/env bash
# check-codekwaliteit-valkuilen.sh (#1262)
#
# Vangt vier patronen die in dit project aantoonbaar stille fouten hebben opgeleverd. Alle vier
# stonden al als harde regel in CLAUDE.md; geen van vier werd door iets gecontroleerd.
#
#   uri-absolute   Uri.TryCreate(..., UriKind.Absolute, ...) als "is dit een URL"-test. Op Unix —
#                  en dus op het Linux Consumption Plan én op een macOS-ontwikkelmachine — parseert
#                  "/favicon.ico" daarmee succesvol als file:-URI. Op Windows niet. #1252 maakte zo
#                  maandenlang élke favicon- en logo-extractie null, zonder één foutmelding.
#   datetime-now   DateTime.Now waar UtcNow hoort. PR #246: lokale tijd opgeslagen, als UTC
#                  gemarkeerd, in de GUI nog eens omgerekend — een tijdstip in de toekomst.
#   getdate        GETDATE() waar GETUTCDATE() hoort. Zelfde incident, de databasekant ervan.
#   time-input     <input type="time"> in plaats van het <TimeInput>-component, dat "830" en
#                  "8:30" normaliseert. Een bare input laat beide vormen ongemoeid doorlopen.
#
# Uitzonderingen staan in scripts/ci/codekwaliteit-valkuilen-allowlist.txt, per pad én met reden.
# Een pad zonder reden laat deze guard falen: een uitzondering zonder motivatie is niet te
# beoordelen en groeit vanzelf uit tot "we zetten het gewoon in de lijst".
#
# Commentaarregels tellen niet mee — anders zou dit bestand zichzelf laten falen, en zouden de
# CLAUDE.md-citaten in de codebase de guard permanent rood houden. Testprojecten tellen evenmin
# mee: een test die het patroon noemt, tóétst het juist — daar is het aanwezig zijn het doel.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5, POSIX ERE (grep -E, nooit -P).

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

ALLOWLIST="scripts/ci/codekwaliteit-valkuilen-allowlist.txt"
[ -f "$ALLOWLIST" ] || { echo "::error::$ALLOWLIST ontbreekt — deze guard bewijst dan niets."; exit 1; }

# Regel zonder reden = fout. Veld 1 sleutel, veld 2 pad, veld 3+ reden.
if awk '!/^[[:space:]]*#/ && NF > 0 && NF < 3 { print NR": "$0; gevonden = 1 }
        END { exit gevonden ? 1 : 0 }' "$ALLOWLIST"; then
  :
else
  echo "::error file=$ALLOWLIST::Elke uitzondering heeft een reden nodig (<sleutel> <pad> <reden>)."
  exit 1
fi

# is_toegestaan <sleutel> <pad> → 0 als het pad onder een allowlist-regel valt.
is_toegestaan() {
  awk -v sleutel="$1" -v pad="$2" '
    /^[[:space:]]*#/ || NF == 0 { next }
    $1 != sleutel { next }
    index(pad, $2) == 1 { gevonden = 1 }
    END { exit gevonden ? 0 : 1 }
  ' "$ALLOWLIST"
}

fail=0

# zoek <sleutel> <regex> <uitleg> -- <git-grep-pathspec...>
zoek() {
  sleutel="$1"; patroon="$2"; uitleg="$3"; shift 4
  treffers=0
  while IFS= read -r treffer; do
    [ -n "$treffer" ] || continue
    bestand="${treffer%%:*}"
    rest="${treffer#*:}"
    regelnr="${rest%%:*}"
    inhoud="${rest#*:}"

    # Commentaar telt niet mee.
    case "$(printf '%s' "$inhoud" | sed 's/^[[:space:]]*//')" in
      '//'*|'///'*|'*'*|'/*'*|'--'*|'@*'*) continue ;;
    esac

    if is_toegestaan "$sleutel" "$bestand"; then
      continue
    fi

    echo "::error file=$bestand,line=$regelnr::$uitleg"
    treffers=$((treffers + 1))
  done < <(git grep -nE "$patroon" -- "$@" 2>/dev/null || true)

  if [ "$treffers" -gt 0 ]; then
    echo "::error::$treffers niet-toegestane treffer(s) voor '$sleutel'. Lost de code het patroon niet op, voeg dan een gemotiveerde regel toe aan $ALLOWLIST."
    fail=1
  fi
  echo "  $sleutel: $treffers niet-toegestane treffer(s)"
}

echo "Valkuilen-scan:"

zoek uri-absolute 'UriKind\.Absolute' \
  'UriKind.Absolute als URL-test: op Unix parseert "/pad" hiermee succesvol als file:-URI (#1252). Gebruik UriKind.RelativeOrAbsolute en beslis daarna op IsAbsoluteUri.' \
  -- '*.cs' ':(exclude)*.Tests/*'

zoek datetime-now 'DateTime\.Now' \
  'DateTime.Now schrijft lokale tijd weg; de database slaat UTC op (#246). Gebruik DateTime.UtcNow, en converteer pas in de GUI met ToLocalTime().' \
  -- '*.cs' ':(exclude)*.Tests/*'

zoek getdate '[^C]GETDATE\(\)' \
  'GETDATE() slaat lokale servertijd op terwijl de API het als UTC markeert (#246). Gebruik GETUTCDATE().' \
  -- '*.sql' '*.cs' ':(exclude)*.Tests/*'

zoek time-input 'type="time"' \
  'Gebruik <TimeInput @bind-Value="..." />; dat normaliseert "830" en "8:30" via TimeHelper. Een bare <input type="time"> doet dat niet.' \
  -- '*.razor'

[ "$fail" -eq 0 ] || exit 1
echo
echo "OK — geen niet-toegestane valkuilpatronen."
