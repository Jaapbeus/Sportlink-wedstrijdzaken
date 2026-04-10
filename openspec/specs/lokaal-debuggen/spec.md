# Lokaal Debuggen — Ontwikkelworkflow

Specificatie voor het lokaal draaien, debuggen en testen van de Sportlink Azure Function.

**Bronbestanden:** `FunctionApp/docs/LOCAL-DEBUG-README.md`, `FunctionApp/docs/QUICK-REFERENCE.md`

---

## Requirement: Azurite starten

Azure Functions VEREIST een actieve Azure Storage Emulator (Azurite) voor lokale ontwikkeling.

### Scenario: Azurite opstarten

- **GIVEN** Azurite is geïnstalleerd via `npm install -g azurite`
- **WHEN** de ontwikkelaar Azurite start
- **THEN** MOET het volgende commando succesvol draaien:
  ```powershell
  azurite --silent --location c:\azurite --debug c:\azurite\debug.log
  ```
- **AND** MOETEN de standaardpoorten beschikbaar zijn: 10000, 10001, 10002

### Scenario: Azurite op achtergrond

- **GIVEN** een PowerShell-terminal
- **WHEN** Azurite op de achtergrond gestart wordt
- **THEN** KAN het volgende commando gebruikt worden:
  ```powershell
  Start-Process -FilePath "azurite" -ArgumentList "--silent" -WindowStyle Hidden
  ```

---

## Requirement: Build-configuratie

De applicatie MOET gebuild worden in **Debug**-modus voor lokale ontwikkeling.

### Scenario: Correcte build

- **GIVEN** de solution `fa-dev-sportlink-01.sln` geopend in Visual Studio
- **WHEN** de ontwikkelaar bouwt (Ctrl+Shift+B)
- **THEN** MOET de build-configuratie op **Debug** staan (niet Release)
- **AND** MOET de build slagen zonder fouten

### Scenario: Release-waarschuwing

- **GIVEN** build-configuratie staat op Release
- **WHEN** de debugger gestart wordt
- **THEN** verschijnt de waarschuwing: `You are debugging a Release build... degraded debugging experience`
- **AND** MOET de ontwikkelaar wisselen naar Debug-configuratie

---

## Requirement: Debuggen starten

De applicatie MOET lokaal gestart kunnen worden met volledige debugmogelijkheden.

### Scenario: Start via Visual Studio

- **GIVEN** Azurite draait, build-configuratie is Debug
- **WHEN** de ontwikkelaar F5 drukt
- **THEN** MOET Azure Functions Core Tools starten op `http://localhost:7094`
- **AND** MOETEN de volgende functies geladen worden:
  - `FetchAndStoreApiData`: timerTrigger
  - `SyncMatchesHttp`: httpTrigger

### Scenario: Start via CLI

- **GIVEN** een terminal in de `FunctionApp/` directory
- **WHEN** de volgende commando's worden uitgevoerd:
  ```bash
  dotnet build -c Debug
  func start --port 7094
  ```
- **THEN** MOET de applicatie starten op poort 7094

---

## Requirement: Handmatige synchronisatie

De ontwikkelaar MOET handmatig een Sportlink-synchronisatie kunnen triggeren via HTTP.

### Scenario: Standaard sync

- **GIVEN** de applicatie draait lokaal
- **WHEN** een GET-request wordt gestuurd naar `http://localhost:7094/api/sync-matches`
- **THEN** MOET de volledige sync-cyclus uitgevoerd worden (teams → wedstrijden → wedstrijddetails)

### Scenario: Sync met weekoffset

- **GIVEN** de applicatie draait lokaal
- **WHEN** een GET-request wordt gestuurd met parameters: `?weekOffsetFrom=X&weekOffsetTo=Y`
- **THEN** MOET alleen het opgegeven weekbereik gesynchroniseerd worden

### Scenario: Volledige seizoensreset

- **GIVEN** de applicatie draait lokaal
- **WHEN** een GET-request wordt gestuurd met: `?reset=true&season=2025`
- **THEN** MOET een volledige seizoensynchronisatie plaatsvinden

---

## Requirement: Timer-trigger schema

De timer-trigger MOET configureerbaar zijn voor verschillende scenario's.

| Cron-expressie | Beschrijving | Gebruik |
|----------------|--------------|---------|
| `0 0 4 * * *` | Dagelijks om 04:00 | Productie (standaard) |
| `*/10 * * * * *` | Elke 10 seconden | Testen |
| `0 0 8 * * 1` | Maandag 08:00 | Wekelijks |
| `0 0 8 * * 1,4` | Ma en Do 08:00 | Tweewekelijks |

### Scenario: Timer wijzigen voor testen

- **GIVEN** het bestand `Function1.cs`
- **WHEN** de ontwikkelaar de timer wil testen
- **THEN** KAN de cron-expressie tijdelijk aangepast worden naar `*/10 * * * * *`
- **AND** MOET de timer na testen teruggezet worden naar de productiewaarde

---

## Requirement: Verbindingsstrings

De applicatie MOET de juiste verbindingsstrings gebruiken per omgeving.

| Instelling | Waarde | Omgeving |
|------------|--------|----------|
| `AzureWebJobsStorage` | `UseDevelopmentStorage=true` | Lokaal (Azurite) |
| `SqlConnectionString` | `Server=<JOUW_SERVER>;Database=SportlinkSqlDb;...` | Lokaal |
| `PRDSqlConnectionString` | (niet gebruikt lokaal) | Productie |

### Scenario: Lokale verbinding

- **GIVEN** `local.settings.json` met correcte `SqlConnectionString`
- **WHEN** de applicatie start
- **THEN** MOET de verbinding naar de lokale SQL Server succesvol zijn
- **AND** MOET `AzureWebJobsStorage` naar Azurite wijzen

---

## Requirement: Troubleshooting

Bij bekende fouten MOET de ontwikkelaar de juiste oplossing kunnen vinden.

### Scenario: 401 Unauthorized

- **GIVEN** foutmelding `Response status code does not indicate success: 401 (Unauthorized)`
- **WHEN** de ontwikkelaar het probleem analyseert
- **THEN** MOET de `AppSettings` tabel gecontroleerd worden op geldige credentials
- **AND** MOET de API-URL getest worden in een browser of Postman

### Scenario: Database niet bereikbaar

- **GIVEN** foutmelding `Cannot open database "SportlinkSqlDb"`
- **WHEN** de ontwikkelaar het probleem analyseert
- **THEN** MOET gecontroleerd worden:
  1. SQL Server service draait (`Get-Service -Name 'MSSQLSERVER'`)
  2. Database bestaat (`SELECT name FROM sys.databases WHERE name = 'SportlinkSqlDb'`)
  3. Verbindingsstring in `local.settings.json` klopt

### Scenario: Stored procedure niet gevonden

- **GIVEN** foutmelding `Could not find stored procedure 'sp_MergeStgToHis'`
- **WHEN** de ontwikkelaar het probleem analyseert
- **THEN** MOETEN de stored procedures gedeployd worden vanuit `Database/dbo/System Stored Procedures/`

### Scenario: Azurite niet actief

- **GIVEN** foutmelding `AzureWebJobsStorage connection failed`
- **WHEN** de ontwikkelaar het probleem analyseert
- **THEN** MOET Azurite gestart worden: `azurite --silent`

### Scenario: Metadata-tabel ontbreekt

- **GIVEN** foutmelding `Invalid object name 'mta.source_target_mapping'`
- **WHEN** de ontwikkelaar het probleem analyseert
- **THEN** MOET `setup-metadata-tables.sql` uitgevoerd worden

---

## Requirement: Debuggen stoppen

De debug-sessie MOET netjes beëindigd worden.

### Scenario: Stoppen

- **GIVEN** een actieve debug-sessie
- **WHEN** de ontwikkelaar stopt
- **THEN** MOET in Visual Studio Shift+F5 gedrukt worden
- **AND** KAN Azurite gestopt worden via: `Stop-Process -Name "azurite"`

### Scenario: Schone herstart

- **GIVEN** de behoefte om met een schone database te beginnen
- **WHEN** de ontwikkelaar alle data wil wissen
- **THEN** KUNNEN de staging- en history-tabellen getruncated worden:
  ```sql
  TRUNCATE TABLE [stg].[teams];
  TRUNCATE TABLE [stg].[matches];
  TRUNCATE TABLE [stg].[matchdetails];
  TRUNCATE TABLE [his].[teams];
  TRUNCATE TABLE [his].[matches];
  TRUNCATE TABLE [his].[matchdetails];
  ```
