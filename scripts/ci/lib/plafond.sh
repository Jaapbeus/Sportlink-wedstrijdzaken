#!/usr/bin/env bash
# plafond.sh (#1262) — gedeelde lezer voor scripts/ci/codekwaliteit-plafonds.txt.
#
# Bestaat zodat de drie ratchet-guards niet elk hun eigen kopie van dezelfde awk-lookup krijgen.
# Dat klinkt overdreven voor zes regels code — maar dit is precies de guard-set die duplicatie
# moet tegenhouden, en een set die zichzelf niet aan de regel houdt wordt niet serieus genomen.
#
# Bash 3.2-compatibel (macOS /bin/bash): geen declare -A, geen mapfile.

PLAFOND_BESTAND="${PLAFOND_BESTAND:-scripts/ci/codekwaliteit-plafonds.txt}"

# lees_plafond <sleutel> → schrijft de plafondwaarde naar stdout, of faalt met exit 1.
lees_plafond() {
  sleutel="$1"
  if [ ! -f "$PLAFOND_BESTAND" ]; then
    echo "::error::Plafondbestand $PLAFOND_BESTAND ontbreekt — deze guard kan niets bewijzen." >&2
    return 1
  fi
  waarde="$(awk -v s="$sleutel" '$1 == s { print $2; gevonden = 1 } END { exit gevonden ? 0 : 1 }' \
    "$PLAFOND_BESTAND" 2>/dev/null)" || {
    echo "::error::Sleutel '$sleutel' staat niet in $PLAFOND_BESTAND." >&2
    return 1
  }
  case "$waarde" in
    ''|*[!0-9]*)
      echo "::error::Plafond voor '$sleutel' is geen geheel getal: '$waarde'." >&2
      return 1
      ;;
  esac
  echo "$waarde"
}
