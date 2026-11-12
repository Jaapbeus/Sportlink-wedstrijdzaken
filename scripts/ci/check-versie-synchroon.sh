#!/usr/bin/env bash
# check-versie-synchroon.sh (#1717)
#
# AGENTS.md ("Versiebeheer"): de versie staat in alle drie de csproj's (FunctionApp, BlazorAdmin,
# FunctionApp.Postgres) en moet na elke rebase hetzelfde zijn; openapi.json komt uit de YAML en
# heeft dus dezelfde info.version. Een versiebump die bij een rebase stil verdwijnt (zie memory
# "versiebump-verdwijnt-stil-bij-rebase") valt hier op.
#
# --staged controleert dezelfde waarden uit de Git-index voor de commit-hook.
#
# Per csproj moeten Version, AssemblyVersion en FileVersion gelijk zijn, en alle drie de csproj's
# dezelfde waarde hebben. info.version van openapi.yaml en openapi.json moet gelijk zijn (zij hoeft
# niet gelijk te zijn aan de appversie: die volgt alleen bij een endpointwijziging).
#
# Draagbaarheid (#1155): bash 3.2 én bash 5, POSIX ERE.

set -euo pipefail

modus="werkboom"
if [ "$#" -gt 1 ]; then
  echo "Gebruik: $0 [--staged]" >&2
  exit 2
elif [ "$#" -eq 1 ]; then
  if [ "$1" != "--staged" ]; then
    echo "Onbekende optie: $1" >&2
    exit 2
  fi
  modus="index"
fi

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

fail=0
referentie=""

# --staged leest alleen de numerieke versietags rechtstreeks uit de index. Daardoor kan een
# niet-gestagede werkboomcorrectie geen foutieve gedeeltelijke staging maskeren.
lees_versieveld() {
  bestand="$1"
  veld="$2"
  if [ "$modus" = "index" ]; then
    if ! git cat-file -e ":$bestand" 2>/dev/null; then
      return 1
    fi
    git show ":$bestand" | sed -n "s|.*<$veld>\([^<]*\)</$veld>.*|\1|p" | head -n 1
  else
    sed -n "s|.*<$veld>\([^<]*\)</$veld>.*|\1|p" "$bestand" | head -n 1
  fi
}

lees_openapi_versie() {
  bestand="$1"
  type="$2"
  if [ "$modus" = "index" ] && ! git cat-file -e ":$bestand" 2>/dev/null; then
    return 1
  fi
  if [ "$type" = "yaml" ]; then
    if [ "$modus" = "index" ]; then
      git show ":$bestand" | sed -n 's/^  version: *\([0-9][0-9.]*\).*/\1/p' | head -n 1
    else
      sed -n 's/^  version: *\([0-9][0-9.]*\).*/\1/p' "$bestand" | head -n 1
    fi
  elif [ "$modus" = "index" ]; then
    git show ":$bestand" | sed -n 's/^ *"version": *"\([0-9][0-9.]*\)".*/\1/p' | head -n 1
  else
    sed -n 's/^ *"version": *"\([0-9][0-9.]*\)".*/\1/p' "$bestand" | head -n 1
  fi
}

for csproj in FunctionApp/fa-dev-sportlink-01.csproj BlazorAdmin/BlazorAdmin.csproj FunctionApp.Postgres/FunctionApp.Postgres.csproj; do
  if [ "$modus" = "werkboom" ] && [ ! -f "$csproj" ]; then
    echo "::error file=$csproj::csproj ontbreekt; de versiecontrole kan niet draaien."
    fail=1
    continue
  fi
  for tag in Version AssemblyVersion FileVersion; do
    if ! waarde="$(lees_versieveld "$csproj" "$tag")"; then
      echo "::error file=$csproj::kan de csproj-versie uit de index niet lezen."
      fail=1
      continue
    fi
    if [ -z "$waarde" ]; then
      echo "::error file=$csproj::<$tag> ontbreekt."
      fail=1
      continue
    fi
    if [ -z "$referentie" ]; then
      referentie="$waarde"
    fi
    if [ "$waarde" != "$referentie" ]; then
      echo "::error file=$csproj::<$tag> is '$waarde', verwacht '$referentie' (alle drie de csproj's en alle drie de tags synchroon)."
      fail=1
    fi
  done
done

if ! yaml_versie="$(lees_openapi_versie docs/api-standaarden/openapi.yaml yaml)"; then
  yaml_versie=""
fi
if ! json_versie="$(lees_openapi_versie docs/api-standaarden/openapi.json json)"; then
  json_versie=""
fi
if [ -z "$yaml_versie" ] || [ -z "$json_versie" ]; then
  echo "::error::info.version niet gevonden in openapi.yaml of openapi.json."
  fail=1
elif [ "$yaml_versie" != "$json_versie" ]; then
  echo "::error file=docs/api-standaarden/openapi.json::info.version ($json_versie) wijkt af van openapi.yaml ($yaml_versie); genereer de JSON opnieuw uit de YAML (docs/API.md)."
  fail=1
fi

[ "$fail" -eq 0 ] || exit 1
echo "OK — csproj-versies synchroon ($referentie); OpenAPI-versie YAML = JSON ($yaml_versie)."
