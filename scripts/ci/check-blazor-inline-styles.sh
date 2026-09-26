#!/usr/bin/env bash
# check-blazor-inline-styles.sh (#1329)
#
# Dwingt af dat presentatie in Blazor-pagina's via CSS isolation (<Pagina>.razor.css) loopt, niet
# via een <style>-blok of een statische style="..."-declaratie in de markup zelf.
#
# TWEE REGELS
# -----------
# 1. HARD: geen <style>-blok in een .razor-pagina onder BlazorAdmin/Pages/.
# 2. HARD: elke resterende style="..."-declaratie op zo'n pagina mag uitsluitend CSS custom
#    properties bevatten (elke deeldeclaratie begint met "--"). Dat is de enige vorm die een
#    dynamische waarde (berekende positie, gekozen kleur) nog inline mag zetten — de daadwerkelijke
#    CSS-eigenschap (left, background, ...) hoort in de .razor.css via var(--naam).
#
# Een enkele regel is geen ratchet: dit is een nieuwe, harde regel (#1262 regel 6), geen meting die
# geleidelijk mag dalen. Een uitzondering kan alleen via scripts/ci/blazor-inline-style-allowlist.txt
# — expliciet, per regel, met een reden. Dat bestand hoort leeg te lopen; het is openstaande schuld,
# geen vrijstelling voor een hele pagina.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5. Geen mapfile, geen declare -A.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

ALLOWLIST="scripts/ci/blazor-inline-style-allowlist.txt"

is_allowed() {
  # is_allowed <pad> <regelnummer>
  [ -f "$ALLOWLIST" ] || return 1
  awk -v p="$1" -v l="$2" '
    /^[[:space:]]*#/ { next }
    NF == 0 { next }
    {
      split($1, delen, ":")
      if (delen[1] == p && delen[2] == l) { found = 1; exit }
    }
    END { exit found ? 0 : 1 }
  ' "$ALLOWLIST"
}

bevindingen="$(git ls-files -- 'BlazorAdmin/Pages/*.razor' | python3 -c '
import re
import sys

STYLE_BLOK = re.compile(r"<style[ >]", re.IGNORECASE)
STYLE_ATTR = re.compile(r"style\s*=\s*\"(.*?)\"", re.DOTALL)

for pad in (r.strip() for r in sys.stdin if r.strip()):
    with open(pad, encoding="utf-8", errors="replace") as fh:
        inhoud = fh.read()
    regels = inhoud.split("\n")

    for i, regel in enumerate(regels, start=1):
        if STYLE_BLOK.search(regel):
            print(f"BLOK\t{pad}\t{i}\t<style>-blok hoort in {pad.replace(chr(46) + chr(114) + chr(97) + chr(122) + chr(111) + chr(114), chr(46) + chr(114) + chr(97) + chr(122) + chr(111) + chr(114) + chr(46) + chr(99) + chr(115) + chr(115))}")

    for m in STYLE_ATTR.finditer(inhoud):
        waarde = m.group(1)
        regelnr = inhoud.count("\n", 0, m.start()) + 1
        declaraties = [d.strip() for d in waarde.split(";") if d.strip()]
        for d in declaraties:
            eigenschap = d.split(":", 1)[0].strip()
            if not eigenschap.startswith("--"):
                print(f"ATTR\t{pad}\t{regelnr}\t{d}")
                break
' 2>&1)"

fail=0

if [ -n "$bevindingen" ]; then
  while IFS=$'\t' read -r soort pad regelnr detail; do
    [ -n "$soort" ] || continue
    if is_allowed "$pad" "$regelnr"; then
      continue
    fi
    if [ "$soort" = "BLOK" ]; then
      echo "::error file=$pad,line=$regelnr::<style>-blok in een Blazor-pagina. Verplaats de inhoud naar $(echo "$pad" | sed 's/\.razor$/.razor.css/') (CSS isolation)."
    else
      echo "::error file=$pad,line=$regelnr::Statische of niet-custom-property style=\"...\" gevonden ($detail). Verplaats vaste CSS naar een class in $(echo "$pad" | sed 's/\.razor$/.razor.css/'); een dynamische waarde hoort als CSS custom property (--naam:@expressie), met de eigenlijke eigenschap als var(--naam) in het stylesheet."
    fi
    fail=1
  done <<< "$bevindingen"
fi

if [ "$fail" -ne 0 ]; then
  echo
  echo "::error::Zie docs/ARCHITECTUUR-CODEKWALITEIT.md (CSS isolation, #1329) en $ALLOWLIST voor de uitzonderingsprocedure."
  exit 1
fi

echo "OK — geen <style>-blokken of statische inline styles in Blazor-pagina's."
