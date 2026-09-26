#!/usr/bin/env bash
# check-regelregister.sh (#1262)
#
# De meta-guard: controleert dat het register in docs/ARCHITECTUUR-CODEKWALITEIT.md klopt met de
# werkelijkheid. Voor elke genoemde guard:
#
#   * bestaat het script?
#   * is het uitvoerbaar (alleen .sh — git bewaart de executable-bit, en een niet-uitvoerbare
#     guard wordt op macOS stilzwijgend overgeslagen, zie CLAUDE.md over git-hooks)?
#   * wordt het daadwerkelijk aangeroepen in de workflow die het register noemt?
#
# En omgekeerd: elke guard in scripts/ci/ staat in het register.
#
# WAAROM DIT SCRIPT BESTAAT
# -------------------------
# Bij het onderzoek naar #1248 bleken eenentwintig harde regels in CLAUDE.md geen enkele
# geautomatiseerde controle te hebben. Niet omdat iemand besloot ze niet te bewaken, maar omdat
# "regel opschrijven" en "controle bouwen" twee losse handelingen zijn waarvan alleen de eerste
# vanzelf gebeurt. Dit script maakt de tweede zichtbaar: een regel toevoegen zonder guard, of een
# guard toevoegen zonder hem in een workflow te zetten, laat de build falen.
#
# Het bewaakt ook de omgekeerde stilte. Een guard die uit een workflow wordt gehaald — bij een
# refactor, of omdat hij een keer in de weg zat — verdwijnt zonder spoor: er is geen foutmelding,
# alleen een controle die niet meer draait. Dat is precies de vorm waarin #1248 kon ontstaan.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5, POSIX ERE.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

DOC="docs/ARCHITECTUUR-CODEKWALITEIT.md"
[ -f "$DOC" ] || { echo "::error::$DOC ontbreekt — het register is dan onvindbaar."; exit 1; }

register="$(mktemp)"
trap 'rm -f "$register"' EXIT

# Regels tussen de twee markers, alleen tabelrijen met een scriptpad.
awk '/REGELREGISTER-BEGIN/ { in_reg = 1; next }
     /REGELREGISTER-EINDE/ { in_reg = 0 }
     in_reg && /^\|/ && /scripts\/ci\// { print }' "$DOC" > "$register"

aantal="$(grep -c '' "$register" || true)"
if [ "$aantal" -eq 0 ]; then
  echo "::error file=$DOC::Geen registerregels gevonden tussen REGELREGISTER-BEGIN en -EINDE."
  exit 1
fi

fail=0
genoemd="$(mktemp)"
trap 'rm -f "$register" "$genoemd"' EXIT

while IFS= read -r rij; do
  script="$(printf '%s' "$rij" | sed -n 's/.*`\(scripts\/ci\/[A-Za-z0-9_.-]*\)`.*/\1/p')"
  workflow="$(printf '%s' "$rij" | awk -F'|' '{ print $4 }' | sed -n 's/.*`\([A-Za-z0-9_.-]*\.yml\)`.*/\1/p')"

  if [ -z "$script" ]; then
    echo "::error file=$DOC::Registerregel zonder herkenbaar scriptpad: $rij"
    fail=1
    continue
  fi
  echo "$script" >> "$genoemd"

  if [ ! -f "$script" ]; then
    echo "::error file=$DOC::Het register noemt $script, maar dat bestand bestaat niet."
    fail=1
    continue
  fi

  case "$script" in
    *.sh)
      if [ ! -x "$script" ]; then
        echo "::error file=$script::Guard is niet uitvoerbaar. Herstel met: git update-index --chmod=+x $script"
        fail=1
      fi
      ;;
  esac

  if [ -z "$workflow" ]; then
    echo "::error file=$DOC::Registerregel voor $script noemt geen workflow in de derde kolom."
    fail=1
    continue
  fi

  if [ ! -f ".github/workflows/$workflow" ]; then
    echo "::error file=$DOC::Het register verwijst naar .github/workflows/$workflow, maar die workflow bestaat niet."
    fail=1
    continue
  fi

  if ! grep -q -- "$(basename "$script")" ".github/workflows/$workflow"; then
    echo "::error file=.github/workflows/$workflow::$script staat in het register als draaiend in deze workflow, maar wordt er niet aangeroepen. Een guard die niet draait, bewaakt niets."
    fail=1
  fi
done < "$register"

# Omgekeerd: elke guard in scripts/ci/ moet in het register staan.
for script in $(git ls-files -- 'scripts/ci/check-*.sh' 'scripts/ci/genereer-*.py'); do
  if ! grep -qxF "$script" "$genoemd"; then
    echo "::error file=$script::Deze guard staat niet in het register van $DOC. Voeg hem toe met de regel die hij afdwingt en de workflow waarin hij draait."
    fail=1
  fi
done

[ "$fail" -eq 0 ] || exit 1
echo "OK — $aantal registerregels, alle guards bestaan, zijn uitvoerbaar en draaien in de genoemde workflow."
