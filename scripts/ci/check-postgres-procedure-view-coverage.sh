#!/usr/bin/env bash
# check-postgres-procedure-view-coverage.sh (#864, deel 4 — de laatste van de drie
# bomen-vergelijkingen die #908/deel 1 en #917/#922 (deel 2/3) openlieten)
#
# Vergelijkt de twee databasebomen op procedures/views: elke stored procedure en view uit de
# SQL Server-boom (Database/**/System Stored Procedures/*.sql, Database/**/Views/*.sql) moet
# een aanwijsbare Postgres-tegenhanger hebben — óf een C#-symbool dat de procedurele logica
# daadwerkelijk levert (#818/#861: die logica leeft in C#, niet in PL/pgSQL, dus een
# bestandsvergelijking zoals bij tabellen kan hier niet), óf staat expliciet en gemotiveerd in de
# EXCEPTIONS-lijst hieronder.
#
# Waarom dit GEEN mechanische naamvertaling kan zijn, in tegenstelling tot
# check-postgres-table-coverage.sh: een tabelnaam vertaalt voorspelbaar (dbo -> public,
# PascalCase -> lowercase). Een procedurenaam wordt een willekeurige C#-methodenaam
# (sp_CleanupAppSettingsAudit -> CleanupAppSettingsAuditAsync, sp_MergeStgToHis ->
# MergeStgToHisAsync op een generieke klasse). Er is dus een expliciete MAPPING nodig — dit
# script controleert vervolgens dat het genoemde C#-symbool ook echt bestaat, zodat de mapping
# zelf niet ongemerkt kan verweesd raken (bijv. een hernoemde of verwijderde methode).
#
# Geen database, geen secrets — draait ook op een fork.
set -euo pipefail

FOUT=0

# ── MAPPING: SQL Server-object -> C#-symbool dat de tegenhanger levert ────────────────────
# Formaat: "schema.object|bestand|regex-op-het-symbool". Het bestand wordt met een simpele grep
# doorzocht — geen AST. De regex wordt met woordgrenzen (\b...\b) toegepast, dus een hernoeming
# die de bestaande naam alleen als voorvoegsel behoudt (EnsureSeasonsAsync -> EnsureSeasonsAsyncV2)
# wordt wél als verweesd herkend — empirisch bevestigd tijdens het bouwen van dit script: zonder
# woordgrenzen matchte de oude regex een dergelijke hernoeming stilzwijgend.
MAPPING=(
  "dbo.sp_CleanupAppSettingsAudit|Database.Postgres/PostgresCleanupProcedures.cs|CleanupAppSettingsAuditAsync"
  "planner.sp_CleanupEmailVerwerking|Database.Postgres/PostgresCleanupProcedures.cs|CleanupEmailVerwerkingAsync"
  "planner.sp_CleanupClassificatieCorrectie|Database.Postgres/PostgresCleanupProcedures.cs|CleanupClassificatieCorrectieAsync"
  "avg.sp_CleanupTeambegeleiding|Database.Postgres/PostgresCleanupProcedures.cs|CleanupTeambegeleidingAsync"
  "avg.sp_CleanupImportLog|Database.Postgres/PostgresCleanupProcedures.cs|CleanupImportLogAsync"
  "dbo.sp_CreateTargetTableFromSource|Database.Postgres/PostgresMergeOrchestrator.cs|EnsureHisTableAsync"
  "dbo.sp_MergeStgToHis|Database.Postgres/PostgresMergeOrchestrator.cs|MergeStgToHisAsync"
  "dbo.sp_UpdateSeasonTable|Database.Postgres/PostgresSeasonProcedures.cs|EnsureSeasonsAsync"
  "planner.AlleWedstrijdenOpVeld|Database.Postgres/PostgresPlannerViewGenerator.cs|ViewName"
)

# ── EXCEPTIONS: SQL Server-objecten die bewust (nog) geen Postgres-tegenhanger hebben ─────
# Zelfde format en discipline als check-postgres-table-coverage.sh: elke rij is een bewuste
# beslissing met reden, geen omissie.
EXCEPTIONS=(
  "dbo.sp_CreateDateTable|zero consumenten — dbo.DateTable zelf staat om dezelfde reden al in check-postgres-table-coverage.sh; zie docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 21/34"
  "pub.DateTable|consumentgerichte view op de SQL Server-ETL-boom, geen enkele regel applicatiecode raadpleegt hem; zie docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 34"
  "pub.Matches|idem"
  "pub.Teams|idem"
)

vind_object_naam() {
  # Haalt 'schema.object' uit een CREATE (OR ALTER) PROCEDURE/VIEW-statement.
  grep -iohE "CREATE\s+(OR\s+ALTER\s+)?(PROCEDURE|VIEW)\s+\[?[a-zA-Z_]+\]?\.\[?[A-Za-z_]+\]?" "$1" \
    | head -1 \
    | grep -ioE "\[?[a-zA-Z_]+\]?\.\[?[A-Za-z_]+\]?\$" \
    | tr -d '[]'
}

while IFS= read -r -d '' f; do
  obj=$(vind_object_naam "$f")
  if [ -z "$obj" ]; then
    echo "::error file=$f::Kon geen 'CREATE (OR ALTER) PROCEDURE/VIEW schema.naam' herkennen in dit bestand."
    FOUT=1
    continue
  fi

  gevonden_in_mapping=0
  for regel in "${MAPPING[@]}"; do
    IFS='|' read -r m_obj m_bestand m_regex <<< "$regel"
    if [ "$m_obj" = "$obj" ]; then
      gevonden_in_mapping=1
      if [ ! -f "$m_bestand" ]; then
        echo "::error file=$f::Mapping voor $obj wijst naar $m_bestand, maar dat bestand bestaat niet meer."
        FOUT=1
      elif ! grep -qE "\b${m_regex}\b" "$m_bestand"; then
        echo "::error file=$f::Mapping voor $obj wijst naar '$m_regex' in $m_bestand, maar dat symbool is daar niet (meer) te vinden — de mapping is verweesd geraakt."
        FOUT=1
      fi
      break
    fi
  done
  [ "$gevonden_in_mapping" -eq 1 ] && continue

  gevonden_in_exceptions=0
  for regel in "${EXCEPTIONS[@]}"; do
    IFS='|' read -r e_obj _reden <<< "$regel"
    if [ "$e_obj" = "$obj" ]; then
      gevonden_in_exceptions=1
      break
    fi
  done
  [ "$gevonden_in_exceptions" -eq 1 ] && continue

  echo "::error file=$f::Object $obj staat niet in de MAPPING en niet in de EXCEPTIONS van dit script — voeg een van beide toe (#864)."
  FOUT=1
done < <(find Database \( -path '*Stored Procedures*' -o -path '*Views*' \) -name '*.sql' -print0 | sort -z)

if [ "$FOUT" -eq 1 ]; then
  echo "::error::Procedures/views-dekkingscontrole gefaald (#864, deel 4)."
  exit 1
fi

echo "OK: elke SQL Server-procedure/view heeft een aanwijsbare Postgres-tegenhanger of een gemotiveerde uitzondering."
