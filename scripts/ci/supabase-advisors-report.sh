#!/usr/bin/env bash
# supabase-advisors-report.sh (#1221, onderdeel van #1219)
#
# Vergelijkt de opgehaalde advisor-bevindingen met de baseline van geaccepteerde risico's en
# schrijft, als er iets nieuws is, een issue-tekst. Praat zelf niet met GitHub.
#
# Gebruik: supabase-advisors-report.sh <advisors.json> <baseline.json> <uitvoer-body.md>
#          Schrijft <uitvoer-body.md> en <uitvoer-body.md>.count (aantal nieuwe bevindingen).
#
# ── Waarom een baseline, en waarom op cache_key ─────────────────────────────────────────────────
# Zonder onderdrukking meldt een dagelijkse controle elke dag dezelfde al beoordeelde bevinding.
# Dan is het binnen een week ruis en kijkt niemand er nog naar — het failure-mode van elke
# periodieke scan. De baseline staat in git, zodat "wij accepteren dit risico" een PR-diff is met
# een reden en een issuenummer erbij, in plaats van een vinkje in een dashboard dat niemand ziet.
# Dat is exact het principe uit §65: platformconfiguratie die geen diff achterlaat is onzichtbaar
# voor codereview.
#
# De sleutel is Supabase's eigen `cache_key`. Die levert splinter juist voor uitsluitingslijsten en
# hij is stabiel per (lint, object) — anders dan de vrije tekst in `detail`.
#
# ── Draagbaarheid (#1155) ───────────────────────────────────────────────────────────────────────
# bash 3.2-compatibel.
set -euo pipefail

ADVISORS="${1:-}"
BASELINE="${2:-}"
BODY="${3:-}"

if [ -z "$ADVISORS" ] || [ -z "$BASELINE" ] || [ -z "$BODY" ]; then
  echo "::error::Gebruik: $0 <advisors.json> <baseline.json> <uitvoer-body.md>"
  exit 2
fi

if ! jq -e 'type == "object" and (.geaccepteerd | type == "array") and all(.geaccepteerd[]; type == "object" and (.cache_key | type == "string"))' "$BASELINE" >/dev/null 2>&1; then
  echo "::error::Baseline heeft geen geldige geaccepteerde lijst."
  exit 1
fi

if ! jq -e 'type == "array" and all(.[]; type == "object" and (.cache_key | type == "string") and (.cache_key != ""))' "$ADVISORS" >/dev/null 2>&1; then
  echo "::error::Advisorresultaat heeft geen geldige bevindingenlijst."
  exit 1
fi

# Elke baseline-regel moet een reden hebben. Een onderdrukking zonder reden is precies het soort
# stille uitzondering waar dit hele issue tegen bestaat.
ZONDER_REDEN=$(jq '[.geaccepteerd[] | select((.reden // "") == "")] | length' "$BASELINE")
if [ "$ZONDER_REDEN" != "0" ]; then
  echo "::error::$ZONDER_REDEN baseline-regel(s) zonder 'reden'. Elke geaccepteerde bevinding moet gemotiveerd zijn."
  exit 1
fi

TOTAAL=$(jq 'length' "$ADVISORS")
GEACCEPTEERD=$(jq '.geaccepteerd | length' "$BASELINE")

# Vergelijk privé-baselinesleutels intern, maar schrijf uitsluitend ernstklassen en aantallen weg.
# Advisorvelden met namen, locaties, details, hersteladvies of cache-sleutels worden niet
# opgenomen in het bestand dat de workflow in een issue of runsamenvatting publiceert.
jq -s '
  .[0] as $bevindingen | .[1].geaccepteerd as $ok
  | ($ok | map(.cache_key)) as $keys
  | $bevindingen
  | map(select(.cache_key as $k | ($keys | index($k)) | not))
  | map({level: ((.level // "") | if type == "string" then ascii_upcase else "OTHER" end)})
  | map(if .level == "ERROR" or .level == "WARN" or .level == "INFO" then . else {level: "OTHER"} end)
  | group_by(.level)
  | map({level: .[0].level, count: length})
  | sort_by(.level)
' "$ADVISORS" "$BASELINE" > "${BODY}.nieuw.json"

NIEUW=$(jq '[.[].count] | add // 0' "${BODY}.nieuw.json")

echo "Advisor-bevindingen: $TOTAAL na filter, $GEACCEPTEERD geaccepteerd in de baseline, $NIEUW nieuw."
printf '%s' "$NIEUW" > "${BODY}.count"

if [ "$NIEUW" = "0" ]; then
  : > "$BODY"
  echo "Geen nieuwe bevindingen — er wordt niets gemeld."
  exit 0
fi

{
  echo "| Ernstklasse | Aantal |"
  echo "|---|---:|"
  jq -r '.[] | "| \(.level) | \(.count) |"' "${BODY}.nieuw.json"
} > "$BODY"

# ── Redactie-gate, tweede keer ──────────────────────────────────────────────────────────────────
# fetch-supabase-advisors.sh controleerde de ruwe data al. Dit is de tekst die daadwerkelijk op een
# publieke repository terechtkomt, dus die wordt apart gecontroleerd. Twee gates, want de kosten van
# een fout zijn onomkeerbaar: een issue op een publieke repo is permanent en binnen minuten
# geïndexeerd.
if [ -n "${SUPABASE_PROJECT_REF:-}" ] && grep -qiF "$SUPABASE_PROJECT_REF" "$BODY"; then
  echo "::error::Publicatieredactie mislukt. Niets gepubliceerd."
  exit 1
fi
if grep -qiE "pooler\.supabase\.com|sbp_[A-Za-z0-9_]+" "$BODY"; then
  echo "::error::Publicatieredactie mislukt. Niets gepubliceerd."
  exit 1
fi

echo "Issue-tekst geschreven naar $BODY ($NIEUW bevinding(en))."
