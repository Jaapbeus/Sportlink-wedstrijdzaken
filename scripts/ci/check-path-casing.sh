#!/usr/bin/env bash
# check-path-casing.sh (#825)
#
# Bewaakt dat elke padverwijzing in PowerShell/Markdown/YAML/csproj-bestanden exact (case-
# sensitief) overeenkomt met het daadwerkelijk getrackte bestand. Git's core.ignorecase=true
# (gangbare default op Windows/macOS) merkt een casing-mismatch lokaal niet op; Linux-CI-runners
# (core.ignorecase=false) falen daar hard op — een reëel risico specifiek voor de nieuwe
# Database.Postgres/-boom, waar nog geen gevestigde conventie/spiergeheugen bestaat.
#
# Werkingsprincipe: git ls-files is de canonieke, case-sensitieve bron van waarheid voor welke
# bestanden daadwerkelijk getrackt zijn. Voor elke padachtige tekenreeks in een kandidaatbestand
# wordt gecontroleerd of er een getrackt pad bestaat dat er case-INsensitief mee overeenkomt maar
# niet exact — dat is de mismatch die lokaal onzichtbaar blijft en op Linux-CI/hosting breekt.
#
# Bewust geen eval, geen externe afhankelijkheden buiten git/grep/bash zelf.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

# Kandidaatbestanden: PowerShell, Markdown, YAML, .csproj — conform de vastgelegde, bewust smalle
# scope uit het issue (geen C#/SQL/JSON-dekking nodig).
mapfile -t candidates < <(git ls-files -- '*.ps1' '*.psm1' '*.md' '*.yml' '*.yaml' '*.csproj')

# Case-insensitieve lookup: lowercase-pad -> canonieke, exacte casing zoals git die kent.
declare -A canonical
while IFS= read -r tracked_path; do
  lower="$(printf '%s' "$tracked_path" | tr '[:upper:]' '[:lower:]')"
  canonical["$lower"]="$tracked_path"
done < <(git ls-files)

fail=0
checked=0

for file in "${candidates[@]}"; do
  # Padachtige tokens: minstens één '/'-gescheiden segmentpaar, alleen bestandsnaam-veilige
  # tekens. Matcht zowel 'Database.Postgres/migrations/001_baseline.sql' als
  # 'scripts/dev/Start-Debug.ps1'. Externe URL's (https://...) worden apart uitgesloten: de
  # domeinnaam+pad-vorm van een URL matcht ditzelfde patroon, dus zonder uitsluiting zou
  # 'learn.microsoft.com/sql/linux/...' als een (niet-bestaand) repo-pad worden gelezen.
  while IFS= read -r token; do
    [[ -z "$token" ]] && continue

    lower_token="$(printf '%s' "$token" | tr '[:upper:]' '[:lower:]')"
    match="${canonical[$lower_token]:-}"

    if [[ -n "$match" && "$match" != "$token" ]]; then
      echo "::error file=${file}::Padverwijzing '${token}' wijkt in hoofdlettering af van het daadwerkelijke bestand '${match}' — dit werkt lokaal (case-insensitief bestandssysteem) maar faalt op Linux-CI/hosting."
      fail=1
    fi
  done < <(grep -oE '(https?://)?[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)+' "$file" \
             | grep -vE '^https?://' \
             | sort -u)
  checked=$((checked + 1))
done

if [[ $fail -ne 0 ]]; then
  echo "::error::Een of meer padverwijzingen wijken in hoofdlettering af van het daadwerkelijke bestand op schijf (#825)."
  exit 1
fi

echo "Geen casing-mismatches gevonden in ${checked} bestanden (ps1/psm1/md/yml/yaml/csproj)."
