#!/usr/bin/env bash
# check-versie-synchroon.sh (#1717)
#
# AGENTS.md ("Versiebeheer"): de versie staat in alle drie de csproj's (FunctionApp, BlazorAdmin,
# FunctionApp.Postgres) en moet na elke rebase hetzelfde zijn; openapi.json komt uit de YAML en
# heeft dus dezelfde info.version. Een versiebump die bij een rebase stil verdwijnt (zie memory
# "versiebump-verdwijnt-stil-bij-rebase") valt hier op.
#
# Per csproj moeten Version, AssemblyVersion en FileVersion gelijk zijn, en alle drie de csproj's
# dezelfde waarde hebben. info.version van openapi.yaml en openapi.json moet gelijk zijn (zij hoeft
# niet gelijk te zijn aan de appversie: die volgt alleen bij een endpointwijziging).
#
# Draagbaarheid (#1155): bash 3.2 én bash 5, POSIX ERE.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

fail=0
referentie=""
for csproj in FunctionApp/fa-dev-sportlink-01.csproj BlazorAdmin/BlazorAdmin.csproj FunctionApp.Postgres/FunctionApp.Postgres.csproj; do
  if [ ! -f "$csproj" ]; then
    echo "::error file=$csproj::csproj ontbreekt; de versiecontrole kan niet draaien."
    fail=1
    continue
  fi
  for tag in Version AssemblyVersion FileVersion; do
    waarde="$(sed -n "s|.*<$tag>\([^<]*\)</$tag>.*|\1|p" "$csproj" | head -n 1)"
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

yaml_versie="$(sed -n 's/^  version: *\([0-9][0-9.]*\).*/\1/p' docs/api-standaarden/openapi.yaml | head -n 1)"
json_versie="$(sed -n 's/^ *"version": *"\([0-9][0-9.]*\)".*/\1/p' docs/api-standaarden/openapi.json | head -n 1)"
if [ -z "$yaml_versie" ] || [ -z "$json_versie" ]; then
  echo "::error::info.version niet gevonden in openapi.yaml of openapi.json."
  fail=1
elif [ "$yaml_versie" != "$json_versie" ]; then
  echo "::error file=docs/api-standaarden/openapi.json::info.version ($json_versie) wijkt af van openapi.yaml ($yaml_versie); genereer de JSON opnieuw uit de YAML (docs/API.md)."
  fail=1
fi

[ "$fail" -eq 0 ] || exit 1
echo "OK — csproj-versies synchroon ($referentie); OpenAPI-versie YAML = JSON ($yaml_versie)."
