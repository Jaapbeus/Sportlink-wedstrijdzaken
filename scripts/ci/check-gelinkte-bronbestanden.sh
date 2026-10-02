#!/usr/bin/env bash
# check-gelinkte-bronbestanden.sh (#1461)
#
# BlazorAdmin (WASM) refereert bewust niet aan Planner.Shared; code die toch gedeeld moet worden
# komt binnen als gelinkt bronbestand: <Compile Include="../Planner.Shared/..." Link="..." />.
# Zo'n bestand wordt in de browser gecompileerd en mag dus uitsluitend van de BCL afhangen.
#
# TWEE REGELS per gelinkt bestand (BlazorAdmin/*.csproj)
# 1. HARD: alle using-directives zijn `using System...` — geen ander Planner.Shared-type, geen NuGet.
# 2. HARD: geen RegexOptions.Compiled (faalt in Blazor WebAssembly met een NullReferenceException
#    tijdens het renderen, zonder buildfout — zie DeelHtmlHelper).
# Een link naar een bestand dat niet bestaat is ook een fout. Commentaarregels tellen niet mee.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5. Geen mapfile, geen declare -A, geen grep -P.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

fouten=0
aantal=0

for csproj in BlazorAdmin/*.csproj; do
  [ -f "$csproj" ] || continue
  csproj_dir="$(dirname "$csproj")"
  links="$(grep -E '<Compile[[:space:]]+Include="[^"]*\.\.[^"]*"' "$csproj" \
    | sed -E 's/.*<Compile[[:space:]]+Include="([^"]*)".*/\1/' || true)"
  [ -n "$links" ] || continue
  while IFS= read -r rel; do
    [ -n "$rel" ] || continue
    aantal=$((aantal + 1))
    pad="$csproj_dir/$rel"
    if [ ! -f "$pad" ]; then
      echo "::error file=$csproj::Gelinkt bronbestand bestaat niet: $rel"
      fouten=$((fouten + 1))
      continue
    fi
    # Alleen niet-commentaarregels beoordelen.
    code="$(grep -nvE '^[[:space:]]*(//|\*|/\*)' "$pad" || true)"
    vreemd="$(printf '%s\n' "$code" \
      | grep -E '^[0-9]+:[[:space:]]*(global[[:space:]]+)?using[[:space:]]+[^(]' \
      | grep -vE '^[0-9]+:[[:space:]]*(global[[:space:]]+)?using[[:space:]]+(static[[:space:]]+)?System(\.[A-Za-z0-9_.]+)?;' || true)"
    if [ -n "$vreemd" ]; then
      echo "::error file=$pad::Gelinkt bronbestand (BlazorAdmin) mag uitsluitend 'using System*' hebben:"
      printf '%s\n' "$vreemd"
      fouten=$((fouten + 1))
    fi
    compiled="$(printf '%s\n' "$code" | grep -E 'RegexOptions\.Compiled' || true)"
    if [ -n "$compiled" ]; then
      echo "::error file=$pad::RegexOptions.Compiled faalt in Blazor WebAssembly:"
      printf '%s\n' "$compiled"
      fouten=$((fouten + 1))
    fi
  done <<EOF
$links
EOF
done

if [ "$fouten" -gt 0 ]; then
  echo "Gelinkte bronbestanden: $fouten overtreding(en)."
  exit 1
fi
echo "Gelinkte bronbestanden: $aantal gecontroleerd, schoon."
