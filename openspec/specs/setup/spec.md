# Setup — Ontwikkelomgeving

Specificatie voor het opzetten van de lokale ontwikkelomgeving voor het Sportlink Wedstrijdzaken project.

**Bronbestanden:** `FunctionApp/docs/SETUP.md`, `FunctionApp/docs/SETUP-CHECKLIST.md`

---

## Requirement: Software-vereisten

Het systeem MOET draaien op een werkstation met de volgende software geïnstalleerd.

| Software | Versie |
|----------|--------|
| Visual Studio | 2022 of 2026 (Community, Professional of Enterprise) |
| SQL Server | Lokale of bereikbare instantie |
| SQL Server Management Studio (SSMS) | 18.0+ |
| Node.js | Laatste LTS-versie |
| .NET SDK | 10.0 |
| Azure Functions Core Tools | v4.x |
| Azurite | Laatste versie (via npm) |

### Scenario: Verificatie van geïnstalleerde software

- **GIVEN** een nieuw werkstation
- **WHEN** de ontwikkelaar de installatie controleert
- **THEN** MOETEN de volgende commando's succesvol uitvoeren:
  - `func --version` → `4.x.x`
  - `azurite --version` → versienummer
  - `node --version` → LTS-versie
  - `dotnet --version` → `10.x.x`

---

## Requirement: Git Hooks activeren

Het project MOET pre-commit en pre-push hooks actief hebben die scannen op gevoelige data (wachtwoorden, API-sleutels, servernamen, credentials) vóór commits of pushes naar GitHub.

### Scenario: Hooks activeren na klonen

- **GIVEN** een vers gekloonde repository
- **WHEN** de ontwikkelaar hooks activeert
- **THEN** MOET het volgende commando uitgevoerd worden:
  ```bash
  git config core.hooksPath .githooks
  ```

### Scenario: Gevoelige patronen configureren

- **GIVEN** geactiveerde git hooks
- **WHEN** de ontwikkelaar patronen configureert
- **THEN** MOET `.githooks/sensitive-patterns.txt` aangemaakt worden vanaf het template:
  ```bash
  cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
  ```
- **AND** MOETEN projectspecifieke patronen (wachtwoorden, servernamen, client-ID's) toegevoegd worden

### Scenario: Hooks verificatie

- **GIVEN** geconfigureerde hooks
- **WHEN** een commit wordt uitgevoerd
- **THEN** MOET de melding verschijnen: `🔍 Scanning staged files for sensitive data...`
- **AND** bij schone bestanden: `✅ No sensitive data detected.`

---

## Requirement: Database aanmaken

Het systeem MOET een `SportlinkSqlDb` database hebben met de juiste schema's en tabellen.

### Scenario: Database initialisatie via script

- **GIVEN** een actieve SQL Server instantie
- **WHEN** de ontwikkelaar `setup-local-database.sql` uitvoert in SSMS
- **THEN** MOETEN de volgende objecten aangemaakt worden:
  - Database `SportlinkSqlDb`
  - Tabel `dbo.AppSettings`
  - Schema's: `stg`, `his`
  - Staging tabellen: `stg.teams`, `stg.matches`, `stg.matchdetails`

### Scenario: Database verificatie

- **GIVEN** een aangemaakte database
- **WHEN** de ontwikkelaar de database controleert
- **THEN** MOET de volgende query 3 rijen retourneren:
  ```sql
  SELECT * FROM sys.schemas WHERE name IN ('stg', 'his', 'mta');
  ```

---

## Requirement: Sportlink API-credentials configureren

De applicatie MOET geldige Sportlink API-credentials bevatten in de `dbo.AppSettings` tabel. Placeholder-waarden zijn NIET toegestaan.

### Scenario: Credentials instellen

- **GIVEN** een aangemaakte `AppSettings` tabel
- **WHEN** de ontwikkelaar credentials ontvangt (via Sportlink Portal, Azure Portal, of beheerder)
- **THEN** MOET de tabel bijgewerkt worden:
  ```sql
  UPDATE [dbo].[AppSettings]
  SET sportlinkApiUrl = 'https://data.sportlink.com',
      sportlinkClientId = '<ECHTE_CLIENT_ID>'
  WHERE Id = 1;
  ```

### Scenario: Credentials verificatie

- **GIVEN** geconfigureerde credentials
- **WHEN** `SELECT * FROM [dbo].[AppSettings]` wordt uitgevoerd
- **THEN** MAG het resultaat NIET bevatten: `'your-client-id-here'` of andere placeholders
- **AND** MOET `sportlinkApiUrl` een geldige URL zijn

---

## Requirement: Lokale instellingen configureren

Het bestand `local.settings.json` MOET aanwezig zijn met correcte verbindingsgegevens.

### Scenario: Correcte configuratie

- **GIVEN** het project `FunctionApp/`
- **WHEN** de ontwikkelaar `local.settings.json` aanmaakt vanuit het template
- **THEN** MOET het bestand de volgende waarden bevatten:
  ```json
  {
    "IsEncrypted": false,
    "Values": {
      "AzureWebJobsStorage": "UseDevelopmentStorage=true",
      "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
      "SqlConnectionString": "Server=<JOUW_SERVER>;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;"
    }
  }
  ```

### Scenario: Beveiliging

- **GIVEN** een `local.settings.json` bestand
- **WHEN** het bestand wordt opgeslagen
- **THEN** MOET het bestand in `.gitignore` staan en MAG het NOOIT gecommit worden naar versiebeheer

---

## Requirement: Stored procedures deployen

De applicatie VEREIST twee stored procedures voor data-verwerking.

| Stored Procedure | Doel |
|------------------|------|
| `sp_CreateTargetTableFromSource` | Maakt history-tabellen aan op basis van staging-tabelstructuur |
| `sp_MergeStgToHis` | Voegt data samen van staging naar history-tabellen |

### Scenario: Deployment via Visual Studio

- **GIVEN** het database-project `Database/SportlinkSqlDb.sqlproj`
- **WHEN** de ontwikkelaar het project publiceert naar de lokale SQL Server
- **THEN** MOETEN beide stored procedures aanwezig zijn

### Scenario: Handmatige deployment

- **GIVEN** de scripts in `Database/dbo/System Stored Procedures/`
- **WHEN** de ontwikkelaar de scripts uitvoert in SSMS
- **THEN** MOETEN beide stored procedures aangemaakt worden

### Scenario: Verificatie

- **GIVEN** gedeployde stored procedures
- **WHEN** de volgende query wordt uitgevoerd
- **THEN** MOET deze 2 rijen retourneren:
  ```sql
  SELECT name FROM sys.procedures
  WHERE name IN ('sp_CreateTargetTableFromSource', 'sp_MergeStgToHis');
  ```

---

## Requirement: Metadata-tabel aanmaken

De stored procedures VEREISEN een metadata-tabel die bron- en doeltabellen mapped.

### Scenario: Schema en tabel aanmaken

- **GIVEN** een `SportlinkSqlDb` database
- **WHEN** `setup-metadata-tables.sql` wordt uitgevoerd
- **THEN** MOET het schema `mta` bestaan
- **AND** MOET de tabel `mta.source_target_mapping` aangemaakt worden

### Scenario: Mapping-data invoegen

- **GIVEN** een aangemaakte `mta.source_target_mapping` tabel
- **WHEN** de mapping-data wordt ingevoegd
- **THEN** MOET de tabel exact 3 rijen bevatten:

| source_schema | source_entity | source_pk | target_schema | target_entity | target_pk | merge_type |
|---------------|---------------|-----------|---------------|---------------|-----------|------------|
| `stg` | `teams` | `teamcode` | `his` | `teams` | `bk_teams` | `IUD` |
| `stg` | `matches` | `wedstrijdcode` | `his` | `matches` | `bk_matches` | `IUD` |
| `stg` | `matchdetails` | `WedstrijdCode` | `his` | `matchdetails` | `bk_matchdetails` | `IUD` |

---

## Requirement: Omgevingsverificatie

Vóór de eerste run MOET de volledige omgeving geverifieerd worden.

### Scenario: Geautomatiseerde verificatie

- **GIVEN** een volledig geconfigureerde omgeving
- **WHEN** `setup-local-debug.ps1` wordt uitgevoerd
- **THEN** MOETEN de volgende controles slagen:
  - SQL Server connectiviteit: ✓
  - Database bestaan: ✓
  - Azurite installatie: ✓
  - Azurite draait: ✓
  - `local.settings.json` configuratie: ✓

### Scenario: Handmatige verificatie

- **GIVEN** een volledig geconfigureerde omgeving
- **WHEN** de ontwikkelaar de volgende queries uitvoert
- **THEN** MOETEN de resultaten als volgt zijn:

| Query | Verwacht resultaat |
|-------|-------------------|
| `SELECT * FROM [dbo].[AppSettings]` | Echte credentials (geen placeholders) |
| `SELECT * FROM [mta].[source_target_mapping]` | 3 rijen |
| `SELECT name FROM sys.procedures WHERE name LIKE 'sp_%'` | 2 stored procedures |
| `SELECT name FROM sys.schemas WHERE name IN ('stg','his','mta')` | 3 schema's |

---

## Requirement: Eerste succesvolle run

Na volledige setup MOET de applicatie foutloos data synchroniseren.

### Scenario: Eerste run met debugger

- **GIVEN** Azurite draait, Visual Studio in Debug-modus, alle setup stappen voltooid
- **WHEN** de ontwikkelaar F5 drukt
- **THEN** MOET de console de volgende meldingen tonen:
  - `Database connection established.`
  - `App settings loaded successfully.`
  - `TEAMS - GET: https://...`
  - `TEAMS - X count.` (X > 0)
  - `TEAMS - Data inserted into staging table.`
  - `TEAMS - Merged into his table`

### Scenario: Dataverificatie na eerste run

- **GIVEN** een succesvolle eerste run
- **WHEN** de staging- en history-tabellen gecontroleerd worden
- **THEN** MOETEN alle tabellen rijen bevatten:
  ```sql
  SELECT COUNT(*) FROM [stg].[teams];       -- > 0
  SELECT COUNT(*) FROM [stg].[matches];     -- > 0
  SELECT COUNT(*) FROM [his].[teams];       -- > 0
  SELECT COUNT(*) FROM [his].[matches];     -- > 0
  ```
