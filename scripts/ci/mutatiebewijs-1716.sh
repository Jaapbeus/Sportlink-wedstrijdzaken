#!/usr/bin/env bash
# mutatiebewijs-1716.sh (#1716, slice 1)
#
# Reproduceerbaar mutatiebewijs voor BerichtDatumHeuristiek en de twee tieromhulsels. Past elke
# mutant uit mutatiebewijs-1716.tsv afzonderlijk toe op precies één bronbestand, draait alleen de
# bijbehorende tests (dotnet test --filter) en zet het bestand daarna terug met `git checkout --`.
# Per mutant: KILLED (tests rood, gewenst) of SURVIVED (tests groen, tests te zwak). Eindcode 1
# zodra een mutant overleeft of niet toepasbaar/bouwbaar is; eindcode 0 alleen als alle mutanten
# gedood zijn. Vooraf een nulmeting: de gefilterde tests moeten zonder mutant groen zijn.
#
# Veiligheid: draait alleen vanuit een schone werkboom voor de te muteren bestanden (anders stop,
# want `git checkout --` zou ongecommitte wijzigingen wissen). Commit eerst.
# bash 3.2 compatibel, macOS + Linux; geen DB, Docker of runtime nodig. Gebruik:
#   scripts/ci/mutatiebewijs-1716.sh            # alle mutanten
#   scripts/ci/mutatiebewijs-1716.sh M3 M6      # alleen deze
set -u
root=$(git rev-parse --show-toplevel) || exit 2
cd "$root" || exit 2
lijst="scripts/ci/mutatiebewijs-1716.tsv"
selectie=" $* "
tmp=$(mktemp -d "${TMPDIR:-/tmp}/mutatie.XXXXXX") || exit 2
huidig=""
herstel() {
  if [ -n "$huidig" ]; then git checkout -- "$huidig" 2>/dev/null; huidig=""; fi
  rm -rf "$tmp"
}
trap herstel EXIT INT TERM

# Alle te muteren bestanden moeten schoon zijn.
dirty=0
for b in $(grep -v '^#' "$lijst" | cut -f3 | sort -u); do
  if [ -n "$(git status --porcelain -- "$b")" ]; then
    echo "STOP: $b heeft ongecommitte wijzigingen; commit eerst (herstel zou ze wissen)." >&2
    dirty=1
  fi
done
[ "$dirty" -eq 0 ] || exit 2

test_run() { # project filter -> logbestand, exitcode
  dotnet test "$1" --filter "$2" --nologo -v q >"$3" 2>&1
}
rode_namen() { sed -n -E 's/^\[xUnit\.net [0-9:.]+\][[:space:]]+([A-Za-z0-9_.]+\.[A-Za-z0-9_]+)(\(.*)?$/\1/p' "$1" | sort -u; }
rood_aantal() { sed -n -E 's/^Failed!.*Failed:[[:space:]]+([0-9]+),.*/\1/p' "$1" | head -1; }

echo "== Nulmeting (zonder mutant, tests moeten groen zijn) =="
basis_ok=1
for p in $(grep -v '^#' "$lijst" | cut -f6,7 | sort -u | tr '\t' '|'); do
  proj=${p%%|*}; filt=${p#*|}
  if test_run "$proj" "$filt" "$tmp/basis.log"; then echo "OK    $proj [$filt]"
  else echo "ROOD  $proj [$filt] (nulmeting faalt; mutatiebewijs onbetrouwbaar)"; basis_ok=0; fi
done
[ "$basis_ok" -eq 1 ] || exit 1

echo "== Mutanten =="
totaal=0; gedood=0; fout=0
n=0
grep -v '^#' "$lijst" | grep -v '^$' > "$tmp/mutanten.tsv"
while IFS= read -r regel; do
  id=$(printf '%s' "$regel" | cut -f1)
  case "$selectie" in " "|"  ") ;; *" $id "*) ;; *) continue ;; esac
  om=$(printf '%s' "$regel" | cut -f2); bestand=$(printf '%s' "$regel" | cut -f3)
  oud=$(printf '%s' "$regel" | cut -f4); nieuw=$(printf '%s' "$regel" | cut -f5)
  proj=$(printf '%s' "$regel" | cut -f6); filt=$(printf '%s' "$regel" | cut -f7)
  totaal=$((totaal+1)); n=$((n+1))
  printf '%s\n' "$oud" > "$tmp/oud.txt"; printf '%s\n' "$nieuw" > "$tmp/nieuw.txt"
  huidig="$bestand"
  if ! python3 -I - "$bestand" "$tmp/oud.txt" "$tmp/nieuw.txt" <<'PY'
import sys
pad, oudp, nieuwp = sys.argv[1:4]
oud = open(oudp, encoding="utf-8").read()[:-1]
nieuw = open(nieuwp, encoding="utf-8").read()[:-1]
tekst = open(pad, encoding="utf-8", newline="").read()
if tekst.count(oud) != 1:
    sys.stderr.write("oude tekst komt %d keer voor (moet precies 1)\n" % tekst.count(oud))
    sys.exit(1)
open(pad, "w", encoding="utf-8", newline="").write(tekst.replace(oud, nieuw))
PY
  then echo "ERROR $id $om: mutant niet toepasbaar"; fout=$((fout+1)); git checkout -- "$bestand"; huidig=""; continue; fi
  log="$tmp/$id.log"
  if test_run "$proj" "$filt" "$log"; then
    echo "FAIL  $id SURVIVED  $om (tests bleven groen)"; fout=$((fout+1))
  elif grep -qE 'error (CS|MSB)[0-9]+' "$log"; then
    echo "ERROR $id $om: mutant bouwt niet"; fout=$((fout+1))
  else
    rood=$(rode_namen "$log")
    aantal=$(rood_aantal "$log")
    echo "PASS  $id KILLED    $om ($aantal rode tests)"
    printf '%s\n' "$rood" | sed 's/^/        /'
    gedood=$((gedood+1))
  fi
  git checkout -- "$bestand"; huidig=""
done < "$tmp/mutanten.tsv"

echo "== Resultaat: $gedood van $totaal mutanten gedood =="
[ "$totaal" -gt 0 ] && [ "$gedood" -eq "$totaal" ] && [ "$fout" -eq 0 ]
