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

if ! jq -e 'has("geaccepteerd") and (.geaccepteerd | type == "array")' "$BASELINE" >/dev/null 2>&1; then
  echo "::error::$BASELINE heeft geen geldige .geaccepteerd-array."
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

# Sorteren op ECHTE ernst, niet alfabetisch: alfabetisch levert ERROR, INFO, WARN op, waardoor de
# minst ernstige bevindingen boven de ernstigere komen te staan. De lezer moet bovenaan beginnen.
jq -s '
  .[0] as $bevindingen | .[1].geaccepteerd as $ok
  | ($ok | map(.cache_key)) as $keys
  | $bevindingen
  | map(select(.cache_key as $k | ($keys | index($k)) | not))
  | sort_by(
      (if   .level == "ERROR" then 0
       elif .level == "WARN"  then 1
       elif .level == "INFO"  then 2
       else 3 end),
      .name, .cache_key
    )
' "$ADVISORS" "$BASELINE" > "${BODY}.nieuw.json"

NIEUW=$(jq 'length' "${BODY}.nieuw.json")

echo "Advisor-bevindingen: $TOTAAL na filter, $GEACCEPTEERD geaccepteerd in de baseline, $NIEUW nieuw."
printf '%s' "$NIEUW" > "${BODY}.count"

if [ "$NIEUW" = "0" ]; then
  : > "$BODY"
  echo "Geen nieuwe bevindingen — er wordt niets gemeld."
  exit 0
fi

{
  echo "De dagelijkse controle van Supabase's Security- en Performance Advisor vond **$NIEUW bevinding(en)** die niet in \`.github/supabase-advisors-baseline.json\` staan."
  echo ""
  echo "| Niveau | Aantal |"
  echo "|---|---|"
  # Alleen niveaus die daadwerkelijk voorkomen. Een vaste ERROR/WARN-tabel toont "0 | 0" zodra er
  # via ADVISOR_TESTLINT op een INFO-lint wordt geverifieerd, en dat leest als "niets gevonden"
  # terwijl er acht bevindingen onder staan.
  jq -r 'group_by(.level) | map("| \(.[0].level) | \(length) |") | .[]' "${BODY}.nieuw.json"
  echo ""
  echo "## Bevindingen"
  echo ""
  # Supabase levert `detail` met ontsnapte backticks (\`public.foo\`). In GitHub-markdown rendert
  # dat als een zichtbaar backtick-teken in plaats van als code, wat de tekst rommelig maakt.
  # Terugzetten naar een gewone backtick zodat objectnamen als code worden getoond.
  jq -r '.[] | "### \(.level) — \(.title)\n\n- **Lint:** `\(.name)` (\(.categorie))\n- **Detail:** \(.detail | gsub("\\\\`"; "`"))\n- **Uitleg:** \(.remediation)\n- **Baseline-sleutel:** `\(.cache_key)`\n"' "${BODY}.nieuw.json"
  echo "## Wat nu"
  echo ""
  echo "Per bevinding één van twee dingen, allebei een PR-diff:"
  echo ""
  echo "1. **Oplossen** — meestal een nieuwe migratie in \`Database.Postgres/migrations/\`. Nooit rechtstreeks in het Supabase-dashboard of via MCP \`apply_migration\`: dat omzeilt \`MigrationRunner\` (geen \`schema_migrations\`-rij, geen checksum) en laat \`/api/health\`'s \`pendingMigrations\` uit de pas lopen met de werkelijkheid."
  echo "2. **Accepteren** — voeg de baseline-sleutel toe aan \`.github/supabase-advisors-baseline.json\` mét \`reden\` en \`issue\`. Daarna meldt deze controle hem niet opnieuw."
  echo ""
  echo "> Automatisch aangemaakt door \`.github/workflows/supabase-advisors.yml\` (#1221). Bevindingen zijn geaggregeerd; er staan bewust geen e-mailadressen, IP's of gebruikers-id's in — zie CLAUDE.md regel 4a."
} > "$BODY"

# ── Redactie-gate, tweede keer ──────────────────────────────────────────────────────────────────
# fetch-supabase-advisors.sh controleerde de ruwe data al. Dit is de tekst die daadwerkelijk op een
# publieke repository terechtkomt, dus die wordt apart gecontroleerd. Twee gates, want de kosten van
# een fout zijn onomkeerbaar: een issue op een publieke repo is permanent en binnen minuten
# geïndexeerd.
if [ -n "${SUPABASE_PROJECT_REF:-}" ] && grep -qiF "$SUPABASE_PROJECT_REF" "$BODY"; then
  echo "::error::Redactie-gate: project-ref in de issue-tekst. Niets gepubliceerd."
  exit 1
fi
if grep -qiE "pooler\.supabase\.com|sbp_[A-Za-z0-9_]+" "$BODY"; then
  echo "::error::Redactie-gate: hostname of tokenpatroon in de issue-tekst. Niets gepubliceerd."
  exit 1
fi

echo "Issue-tekst geschreven naar $BODY ($NIEUW bevinding(en))."
