#!/usr/bin/env bash
# fetch-supabase-advisors.sh (#1221, onderdeel van #1219)
#
# Haalt de Security- en Performance Advisor van het Supabase-project op via de Management API,
# filtert op wat er werkelijk toe doet, en schrijft het resultaat als JSON naar stdout-bestand.
# Maakt zelf GEEN issue aan en praat niet met GitHub — dat doet de workflow. Eén ding per script.
#
# ── Waarom dit bestaat ──────────────────────────────────────────────────────────────────────────
# Regel 3 van de Supabase-RLS-sectie in CLAUDE.md schrijft voor om het dashboard "periodiek" onder
# Advisors → Security te controleren. Die regel leunde volledig op een mens die eraan denkt. #1198
# liet zien wat dat kost: het besluit stond correct gedocumenteerd, de blootstelling stond twaalf
# dagen open. #1220 dekt af wat vóór de merge zichtbaar is; dit script dekt af wat alleen de
# levende productiedatabase weet — gebruiksafhankelijke lints (ongebruikte index, bloat, trage
# policies) en platformconfiguratie die geen diff in git achterlaat.
#
# ── Vereiste omgeving ───────────────────────────────────────────────────────────────────────────
#   SUPABASE_ACCESS_TOKEN   Personal access token. Scoped (aanbevolen): Advisors=Read,
#                           Logs=Read, Database Security=Read. Classic werkt ook maar draagt
#                           volledige accounttoegang — zie de afweging in #1221.
#   SUPABASE_PROJECT_REF    Project ref. MOET een GitHub Secret zijn, geen Variable: deze
#                           repository is publiek, Actions-logs zijn publiek, en de ref
#                           identificeert de club (CLAUDE.md regel 4a — hetzelfde lek dat #1204
#                           voor zes andere waarden dichtte).
#
#   ADVISOR_TESTLINT        Optioneel, alleen om de MELDKETEN te verifiëren. Vervangt het
#                           ERROR/WARN-filter door "precies dit ene lint", zodat er gegarandeerd
#                           bevindingen zijn en het issue-aanmaakpad daadwerkelijk doorlopen wordt.
#
#                           Waarom dit blijvend bestaat en geen wegwerp-commit was: zolang het
#                           project schoon is levert de normale run nul bevindingen op, en dan
#                           blijft het issue-pad per definitie ongetest. Een meldketen die nooit
#                           heeft gemeld, is geen geverifieerde meldketen — dezelfde redenering als
#                           bij de guards van #1220. Met `no_primary_key` (2 bevindingen) is de
#                           proef klein en goedkoop.
#
#                           De redactie-gates hieronder draaien onverkort door; dit verandert
#                           uitsluitend de selectie, nooit de veiligheidscontroles.
#
# ── Uitvoer ─────────────────────────────────────────────────────────────────────────────────────
#   $1 (verplicht)  pad voor het JSON-resultaat: een array van bevindingen, elk met
#                   categorie/name/level/facing/title/detail/remediation/cache_key.
#
# ── Experimenteel endpoint ──────────────────────────────────────────────────────────────────────
# Supabase merkt /advisors/security en /advisors/performance aan als **experimental**: ze kunnen
# wijzigen of verdwijnen. Daarom staat alle kennis van de responsevorm in dít bestand, en faalt het
# LUID bij een onverwachte vorm in plaats van stil nul bevindingen te melden. Een advisorcontrole
# die stilletjes niets rapporteert is gevaarlijker dan geen advisorcontrole: hij wekt vertrouwen.
#
# ── Draagbaarheid (#1155) ───────────────────────────────────────────────────────────────────────
# bash 3.2-compatibel; geen associatieve arrays, geen mapfile, geen ${var,,}.
set -euo pipefail

UITVOERPAD="${1:-}"
if [ -z "$UITVOERPAD" ]; then
  echo "::error::Gebruik: $0 <uitvoerpad.json>"
  exit 2
fi

: "${SUPABASE_ACCESS_TOKEN:?SUPABASE_ACCESS_TOKEN ontbreekt}"
: "${SUPABASE_PROJECT_REF:?SUPABASE_PROJECT_REF ontbreekt}"

API="https://api.supabase.com/v1/projects/${SUPABASE_PROJECT_REF}/advisors"

WERKMAP=$(mktemp -d)
trap 'rm -rf "$WERKMAP"' EXIT

haal_op() {
  SOORT="$1"
  DOEL="$2"
  HTTP=$(curl -sS --retry 3 --retry-delay 5 --max-time 60 \
    -o "$DOEL" -w '%{http_code}' \
    -H "Authorization: Bearer ${SUPABASE_ACCESS_TOKEN}" \
    -H "Accept: application/json" \
    "${API}/${SOORT}") || {
      echo "::error::curl faalde voor de ${SOORT}-advisor (netwerk of TLS)."
      exit 1
    }

  if [ "$HTTP" != "200" ]; then
    # Het responsebody kan een foutmelding bevatten; die mag geen token of ref lekken, dus
    # alleen de statuscode en een korte, gefilterde hint tonen.
    echo "::error::Management API gaf HTTP ${HTTP} voor de ${SOORT}-advisor."
    case "$HTTP" in
      401) echo "  401 = token ongeldig of verlopen. Maak een nieuw token aan en werk het secret bij." ;;
      403) echo "  403 = token mist de benodigde permissie. Scoped token nodig met Advisors=Read." ;;
      404) echo "  404 = project ref onbekend voor dit token, of het endpoint is gewijzigd (experimenteel)." ;;
      429) echo "  429 = rate limit geraakt. De workflow draait normaal 2x per dag; controleer op een lus." ;;
    esac
    exit 1
  fi

  if ! jq -e 'has("lints") and (.lints | type == "array")' "$DOEL" >/dev/null 2>&1; then
    echo "::error::Onverwachte responsevorm voor de ${SOORT}-advisor: geen .lints-array."
    echo "  Dit endpoint is door Supabase als experimenteel aangemerkt en kan gewijzigd zijn."
    echo "  Gevonden sleutels op het hoogste niveau: $(jq -r 'if type=="object" then (keys|join(", ")) else type end' "$DOEL" 2>/dev/null || echo '(geen geldige JSON)')"
    exit 1
  fi
}

if [ -n "${ADVISOR_TESTLINT:-}" ]; then
  echo "::warning::ADVISOR_TESTLINT staat aan (${ADVISOR_TESTLINT}) — filter vervangen om de meldketen te verifiëren. Dit is geen normale run."
fi

echo "Supabase-advisors ophalen..."
haal_op security "$WERKMAP/security.json"
haal_op performance "$WERKMAP/performance.json"

TOTAAL_SEC=$(jq '.lints | length' "$WERKMAP/security.json")
TOTAAL_PERF=$(jq '.lints | length' "$WERKMAP/performance.json")
echo "  security: $TOTAAL_SEC bevinding(en) ruw, performance: $TOTAAL_PERF"

# ── Filteren ────────────────────────────────────────────────────────────────────────────────────
# level ERROR/WARN en facing EXTERNAL. INFO is bewust uitgesloten: dat is de laag waar
# rls_enabled_no_policy (onze architectuur, 29x) en unindexed_foreign_keys (#1211 heeft die
# getoetst) in zitten. Die horen in een triageronde, niet in een dagelijkse melding — anders is
# het signaal onmiddellijk ruis en kijkt niemand er meer naar.
# INTERNAL-bevindingen gaan over Supabase's eigen objecten en zijn niet door ons op te lossen.
jq -s --arg testlint "${ADVISOR_TESTLINT:-}" '
  [ (.[0].lints[] | . + {categorie: "security"}),
    (.[1].lints[] | . + {categorie: "performance"}) ]
  | (if $testlint == "" then
       map(select((.level // "") | ascii_upcase | . == "ERROR" or . == "WARN"))
     else
       map(select(.name == $testlint))
     end)
  | map(select((.facing // "EXTERNAL") | ascii_upcase == "EXTERNAL"))
  | map({
      categorie,
      name:        (.name // "onbekend"),
      level:       ((.level // "") | ascii_upcase),
      title:       (.title // ""),
      detail:      (.detail // ""),
      remediation: (.remediation // ""),
      cache_key:   (.cache_key // "")
    })
  | sort_by(.level, .name, .cache_key)
' "$WERKMAP/security.json" "$WERKMAP/performance.json" > "$WERKMAP/gefilterd.json"

AANTAL=$(jq 'length' "$WERKMAP/gefilterd.json")
echo "  na filter (ERROR/WARN, EXTERNAL): $AANTAL"

# Elke bevinding moet een cache_key hebben — dat is de sleutel waarop de baseline werkt. Zonder
# sleutel kan een geaccepteerd risico niet stabiel worden onderdrukt en zou dezelfde bevinding
# elke dag opnieuw een melding geven.
ZONDER_KEY=$(jq '[.[] | select(.cache_key == "")] | length' "$WERKMAP/gefilterd.json")
if [ "$ZONDER_KEY" != "0" ]; then
  echo "::error::$ZONDER_KEY bevinding(en) zonder cache_key — baseline-onderdrukking zou onbetrouwbaar worden."
  echo "  Supabase levert cache_key juist voor uitsluitingslijsten; ontbreekt hij, dan is het"
  echo "  responseformaat gewijzigd (experimenteel endpoint)."
  exit 1
fi

# ── Redactie-gate ───────────────────────────────────────────────────────────────────────────────
# Wat hier uitkomt belandt in een GitHub-issue op een PUBLIEKE repository — permanent en binnen
# minuten geïndexeerd (CLAUDE.md regel 4a). Tabel- en kolomnamen zijn toegestaan: die staan al
# publiek in Database.Postgres/migrations/. De project-ref, de pooler-hostname en het token niet.
if grep -qiF "$SUPABASE_PROJECT_REF" "$WERKMAP/gefilterd.json"; then
  echo "::error::Redactie-gate: de project-ref staat in de advisor-uitvoer. Niets gepubliceerd."
  exit 1
fi
if grep -qiE "pooler\.supabase\.com|supabase\.co[^m]|sbp_[A-Za-z0-9_]+" "$WERKMAP/gefilterd.json"; then
  echo "::error::Redactie-gate: hostname of tokenpatroon in de advisor-uitvoer. Niets gepubliceerd."
  exit 1
fi
echo "  redactie-gate: geen identificerende waarden in de uitvoer."

cp "$WERKMAP/gefilterd.json" "$UITVOERPAD"
echo "Geschreven naar $UITVOERPAD"
