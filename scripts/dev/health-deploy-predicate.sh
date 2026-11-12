#!/usr/bin/env bash
set -euo pipefail

body="$(cat)"
if ! printf '%s' "$body" | jq -e 'type == "object"' >/dev/null 2>&1; then
  echo "FOUT: health-respons is geen geldig JSON-object."
  exit 1
fi

database="$(printf '%s' "$body" | jq -r '.database // empty')"
settings_ready="$(printf '%s' "$body" | jq -r 'if has("settingsReady") then (.settingsReady | tostring) elif .settingsLoaded == false then "false" else empty end')"
migrations_current="$(printf '%s' "$body" | jq -r 'if has("migrationsCurrent") then (.migrationsCurrent | tostring) else empty end')"
pending_migrations="$(printf '%s' "$body" | jq -r 'if has("migrationsCurrent") then empty else ((.pendingMigrations // []) | join(", ")) end')"
read_paths_ok="$(printf '%s' "$body" | jq -r 'if has("readPathsOk") and .readPathsOk != null then (.readPathsOk | tostring) else empty end')"
schema_warning="$(printf '%s' "$body" | jq -r 'if has("schemaWarningPresent") then (.schemaWarningPresent | tostring) elif ((.schemaWarning // "") != "") then "true" else "false" end')"
tls_warning="$(printf '%s' "$body" | jq -r 'if has("tlsWarningPresent") then (.tlsWarningPresent | tostring) elif ((.tlsWarning // "") != "") then "true" else "false" end')"

# Equivalent to the previous deploy gate: optional missing fields from older hosts remain skipped.
if [[ -n "$database" && "$database" != "online" ]]; then
  echo "FOUT: health meldt een niet-online database."
  exit 1
fi
if [[ "$settings_ready" == "false" ]]; then
  echo "FOUT: health meldt dat de instellingen niet gereed zijn."
  exit 1
fi
if [[ "$migrations_current" == "false" || -n "$pending_migrations" ]]; then
  echo "FOUT: health meldt dat migraties ontbreken na de migratiestap."
  exit 1
fi
if [[ "$read_paths_ok" == "false" ]]; then
  echo "FOUT: health meldt dat een planner-leespad faalt (leesproef, #1911); de deployment is niet geslaagd."
  exit 1
fi
if [[ "$schema_warning" == "true" ]]; then
  echo "::warning title=Schemawaarschuwing uit /api/health::Een schemawaarschuwing is aanwezig; details zijn beschikbaar voor beheerders."
fi
if [[ "$tls_warning" == "true" ]]; then
  echo "::warning title=TLS-beleid nog niet volledig (#1096)::Een TLS-waarschuwing is aanwezig; details zijn beschikbaar voor beheerders."
fi
echo "health OK: settingsReady=${settings_ready:-onbekend}, migrationsCurrent=${migrations_current:-onbekend}, readPathsOk=${read_paths_ok:-niet gemeten}"
