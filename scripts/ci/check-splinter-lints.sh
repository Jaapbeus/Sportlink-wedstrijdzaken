#!/usr/bin/env bash
# check-splinter-lints.sh (#1220, onderdeel van #1219)
#
# Draait Supabase's eigen databaselinter (splinter) tegen de CI-database en faalt op een
# ZORGVULDIG AFGEBAKENDE deelverzameling lints. Dit is de PR-helft van #1219: schema-statische
# bevindingen worden hier gevangen, vóór de merge. De gebruiksafhankelijke lints (ongebruikte
# index, bloat) horen bij de dagelijkse run tegen productie (#1221) — die heeft statistieken,
# een verse CI-database niet.
#
# ── Waarom splinter niet in dit repo staat ──────────────────────────────────────────────────────
# supabase/splinter levert GEEN LICENSE-bestand (GitHub rapporteert de licentie als null,
# gecontroleerd op commit e74a9e3). Code zonder expliciete licentie herdistribueren in een publieke
# repository is een onnodig risico. Daarom halen we het bestand op tijdens de CI-run, vastgepind op
# een commit-SHA én geverifieerd op SHA-256: reproduceerbaar en manipulatiebestendig, zonder dat wij
# code van derden met onduidelijke licentie verspreiden.
#
# Bijwerken: SPLINTER_SHA en SPLINTER_SHA256 samen wijzigen, en de lintselectie hieronder opnieuw
# langslopen — upstream voegt lints toe en hernoemt ze.
#
# ── De rollen anon en authenticated ─────────────────────────────────────────────────────────────
# Splinter weigert te draaien zonder deze twee rollen ("role \"anon\" does not exist"). Het zijn
# CLUSTER-rollen die Supabase's controlplane bij projectaanmaak aanmaakt; ze zitten niet in een
# database-dump en bestaan dus niet lokaal of in CI. Dat is precies de blinde vlek die §67 van
# docs/ARCHITECTUUR-DATABASE-TIERS.md beschrijft, en waar migratie 023 door nodig was:
#
#   "geen fout bij het toepassen" bewijst niet "het probleem is opgelost" wanneer de omgeving
#    waartegen je test de rollen die het probleem veroorzaken niet eens kent.
#
# Door ze hier aan te maken (NOLOGIN, zonder rechten) doet dit script exact wat §67 als les
# voorschrijft: de rol simuleren en het scenario herhalen. Daarmee worden grant-gebaseerde lints in
# CI voor het eerst betekenisvol in plaats van een stille no-op.
#
# ── Verbinding ──────────────────────────────────────────────────────────────────────────────────
# Standaard libpq-omgevingsvariabelen (PGHOST/PGPORT/PGUSER/PGPASSWORD/PGDATABASE). Nooit een
# wachtwoord als argument — argumenten staan op beide platforms in de processenlijst.
#
# ── Draagbaarheid (#1155) ───────────────────────────────────────────────────────────────────────
# bash 3.2-compatibel: geen associatieve arrays, geen mapfile, geen ${var,,}.
set -euo pipefail

SPLINTER_SHA="e74a9e36cb12258cb67d1464bc1cb196e9cd8446"
SPLINTER_SHA256="d8d558baad3e03832e521c527907fa50a9a172fabd899dd0f5c2504a5a0e9349"
SPLINTER_URL="https://raw.githubusercontent.com/supabase/splinter/${SPLINTER_SHA}/splinter.sql"

# ── LINTS DIE DE BUILD LATEN FALEN ──────────────────────────────────────────────────────────────
# Alleen lints die (a) schema-statisch zijn — bepaalbaar uit de structuur van een verse database,
# zonder gebruiksstatistieken — én (b) waarvan het juiste antwoord niet van het gebruikspatroon
# afhangt. Newline-gescheiden set + exacte-regel-lookup (geen bash-4-constructies).
GATED_LINTS='rls_disabled_in_public
policy_exists_rls_disabled
security_definer_view
function_search_path_mutable
duplicate_index'

# ── BEWUST NIET IN DE GATE ──────────────────────────────────────────────────────────────────────
#
#   unindexed_foreign_keys  Schema-statisch, maar de vraag "heeft deze foreign key een index nodig?"
#                           is een GEBRUIKSVRAAG, geen structuurvraag. Een index kost schrijftijd en
#                           ruimte; of dat loont hangt af van hoe er daadwerkelijk gequeryd wordt.
#                           #1211 liet dat precies zien: 22 Performance Advisor-bevindingen,
#                           getoetst tegen productie, waarvan er 3 een index kregen (migratie 024)
#                           en de rest bewust bleef staan. Een gate op een verse CI-database zou die
#                           afweging afdwingen zonder de gegevens die ervoor nodig zijn — en zou
#                           vandaag meteen op 6 bestaande bevindingen falen die allemaal al bewust
#                           getoetst zijn. Hoort dus bij de dagelijkse run tegen productie (#1221),
#                           die wél statistieken heeft. Wordt hieronder wel informatief gerapporteerd.
#   rls_enabled_no_policy   Zou op ELKE tabel afgaan — dat is onze architectuur, geen fout. #1198
#                           zette RLS bewust aan zonder policies: de FunctionApp verbindt via de
#                           tabeleigenaar-rol, die RLS onvoorwaardelijk omzeilt. Zie migratie 021.
#                           Dit lint hier gaten zou betekenen dat we #985/#1198 terugdraaien.
#   unused_index            Vereist pg_stat_user_indexes-historie. Een verse CI-database heeft nul
#                           scans op élke index, dus dit zou op elke PR vals alarm geven.
#   table_bloat             Idem: vereist gebruik en autovacuum-historie.
#   auth_rls_initplan       Vereist policies; wij hebben er bewust geen.
#   multiple_permissive_policies  Idem.
#   no_primary_key          Te opiniërend voor een harde gate; stg./his.-tabellen zijn legitiem
#                           zonder primaire sleutel.
#   extension_in_public     Zou afgaan op extensies die het platform zelf plaatst.
#   auth_users_exposed, sensitive_columns_exposed, public_bucket_allows_listing,
#   pg_graphql_*, insecure_queue_exposed_in_api, foreign_table_in_api,
#   materialized_view_in_api, rls_references_user_metadata, fkey_to_auth_unique,
#   extension_versions_outdated, unsupported_reg_types, autovacuum_disabled,
#   rls_policy_always_true, anon/authenticated_security_definer_function_executable
#                           Deze hangen af van Supabase-platformobjecten (auth./storage./graphql)
#                           of van runtime-toestand die in CI niet bestaat. Ze worden gedekt door
#                           de dagelijkse advisorcontrole tegen productie (#1221) — daar draaien ze
#                           tegen de échte database mét die objecten.

psql_q() { psql -q -t -A -v ON_ERROR_STOP=1 "$@"; }

echo "Splinter-lints controleren (upstream commit ${SPLINTER_SHA})..."

# ── 1. Splinter ophalen en verifiëren ───────────────────────────────────────────────────────────
WERKMAP=$(mktemp -d)
trap 'rm -rf "$WERKMAP"' EXIT
SPLINTER_SQL="$WERKMAP/splinter.sql"

if [ -n "${SPLINTER_SQL_PATH:-}" ]; then
  echo "  lokaal bestand gebruikt: $SPLINTER_SQL_PATH"
  cp "$SPLINTER_SQL_PATH" "$SPLINTER_SQL"
else
  curl -sSL --fail --retry 3 --retry-delay 2 "$SPLINTER_URL" -o "$SPLINTER_SQL"
fi

if command -v sha256sum >/dev/null 2>&1; then
  GEVONDEN=$(sha256sum "$SPLINTER_SQL" | cut -d' ' -f1)
else
  GEVONDEN=$(shasum -a 256 "$SPLINTER_SQL" | cut -d' ' -f1)
fi

if [ "$GEVONDEN" != "$SPLINTER_SHA256" ]; then
  echo "::error::splinter.sql komt niet overeen met de vastgepinde SHA-256."
  echo "  verwacht : $SPLINTER_SHA256"
  echo "  gevonden : $GEVONDEN"
  echo "Dit betekent dat de inhoud op de vastgepinde commit is gewijzigd, of dat de download is"
  echo "gemanipuleerd. Niet omzeilen: controleer upstream en werk SPLINTER_SHA + SPLINTER_SHA256"
  echo "samen bij in dit script."
  exit 1
fi
echo "  checksum bevestigd."

# ── 2. anon/authenticated simuleren (zie kop) ───────────────────────────────────────────────────
psql_q -c "DO \$\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
    CREATE ROLE anon NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
    CREATE ROLE authenticated NOLOGIN;
  END IF;
END \$\$;" >/dev/null
echo "  rollen anon/authenticated aanwezig."

# ── 3. Splinter draaien ─────────────────────────────────────────────────────────────────────────
# Kolomvolgorde: name, title, level, facing, categories, description, detail, remediation,
# metadata, cache_key. De WARNING over 'SET LOCAL' buiten een transactie is onschadelijk.
UITVOER="$WERKMAP/bevindingen.tsv"
psql -q -t -A -F'	' -v ON_ERROR_STOP=1 -f "$SPLINTER_SQL" > "$UITVOER" 2>"$WERKMAP/stderr.txt" || {
  echo "::error::Splinter kon niet draaien."
  cat "$WERKMAP/stderr.txt"
  exit 1
}

TOTAAL=$(grep -c . "$UITVOER" || true)
echo "  splinter gaf $TOTAAL bevinding(en) in totaal (alle lints)."

# Informatief overzicht per lint. Niet-gated bevindingen blokkeren niets, maar mogen ook niet
# onzichtbaar zijn: een plotselinge sprong hier is een signaal, ook al is het geen bouwfout.
echo ""
echo "  Bevindingen per lint (informatief — alleen de bewaakte lints laten de build falen):"
cut -f1 "$UITVOER" | sort | uniq -c | sort -rn | while read -r N LINT; do
  if printf '%s\n' "$GATED_LINTS" | grep -qxF -- "$LINT"; then
    echo "      $N  $LINT  [BEWAAKT]"
  else
    echo "      $N  $LINT"
  fi
done
echo ""

# ── 4. Filteren op de gated deelverzameling ─────────────────────────────────────────────────────
OVERTREDINGEN="$WERKMAP/overtredingen.tsv"
: > "$OVERTREDINGEN"

while IFS=$'\t' read -r NAAM TITEL NIVEAU REST; do
  [ -n "$NAAM" ] || continue
  if printf '%s\n' "$GATED_LINTS" | grep -qxF -- "$NAAM"; then
    printf '%s\t%s\t%s\t%s\n' "$NAAM" "$NIVEAU" "$TITEL" "$REST" >> "$OVERTREDINGEN"
  fi
done < "$UITVOER"

AANTAL=$(grep -c . "$OVERTREDINGEN" || true)

if [ "$AANTAL" -gt 0 ]; then
  echo "::error::Splinter vond $AANTAL blokkerende bevinding(en) in de bewaakte lints."
  echo ""
  while IFS=$'\t' read -r NAAM NIVEAU TITEL REST; do
    echo "  [$NIVEAU] $NAAM — $TITEL"
    echo "      $REST" | cut -c1-400
  done < "$OVERTREDINGEN"
  echo ""
  echo "Bewaakte lints: $(printf '%s' "$GATED_LINTS" | tr '\n' ' ')"
  echo "Toelichting per lint: https://supabase.github.io/splinter/"
  exit 1
fi

echo "Splinter-lints bevestigd: geen bevindingen in de bewaakte deelverzameling."
