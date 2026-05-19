# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Absolute veiligheidsregels — nooit omzeilen

Deze regels gelden altijd, zonder uitzondering:

1. **Na elke push of commit: CI-status controleren.** Nooit aan de gebruiker melden dat iets klaar of succesvol is zonder eerst te verifiëren dat alle GitHub Actions checks geslaagd zijn (`gh pr checks <nr>` of `gh run list`).

2. **Na elke PR-merge: ook de deploy/build-workflow op `main` controleren.** Na merge direct `gh run list --branch main --limit 3` uitvoeren en wachten op voltooiing van `deploy.yml`. Als de build faalt: direct proberen te fixen. Lukt dit niet: onmiddellijk melden aan de gebruiker. Pas daarna melden dat de PR succesvol is afgerond.

3. **Bij een gefaalde of onduidelijke check: direct stoppen en melden.** Niet stilzwijgend doorgaan, niet zelf "oplossen" zonder de gebruiker te informeren. Elke falende security check is een alarmsignaal.

4. **Persoonsgegevens, wachtwoorden en tokens nooit in bestanden schrijven.** Ook niet tijdelijk, ook niet in commentaar, ook niet in documentatie. Bij twijfel: het gaat niet in git.

5. **De Security Gate job is leidend.** Zolang `Security Gate — blokkeert merge bij fout` rood is, mag er niets gemerged worden — ook al zijn andere checks groen.

6. **Sessie-isolatie — werk uitsluitend op de toegewezen branch.** Claude Code op het web draait in een ephemere container; meerdere sessies kunnen parallel actief zijn (bijv. één per open PR). Voorkom kruisbesmetting:
   - Commit en push **alleen** naar de branch die in de sessie-instructies is genoemd. Nooit naar `main`, nooit naar de branch van een andere sessie.
   - Vóór elke push: controleer `git branch --show-current` en vergelijk met de toegewezen branch.
   - Maak **geen** wijzigingen aan bestanden buiten de repository en deel **geen** geheimen tussen sessies. Wat in deze container staat blijft in deze container.
   - Bij twijfel over welke branch correct is: stop en vraag de gebruiker.

Zie [SECURITY.md](SECURITY.md) voor het volledige protocol.

## Build & Run

```bash
# Build
dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug

# Run locally (requires Azurite running for storage emulation)
cd FunctionApp && func start --port 7094

# Manual sync trigger while running
# GET http://localhost:7094/api/sync?weekOffsetFrom=X&weekOffsetTo=Y
```

**Prerequisites:** .NET 10.0 SDK, Azure Functions Core Tools v4, Azurite (Azure Storage Emulator), SQL Server with `SportlinkSqlDb` database.

**Configuration:** Copy `FunctionApp/local.settings.template.json` to `local.settings.json` and set `SqlConnectionString` to your SQL Server instance.

No automated tests or linter configured.

## Security Setup (eenmalig per developer/machine)

**Git hooks activeren** (verplicht — blokkeert secrets en AVG-data bij commit én push):
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
# Vul sensitive-patterns.txt aan met project-specifieke secrets (clientId, server, etc.)
```

**Optioneel: gitleaks installeren** voor diepere secret-detectie in hooks:
- Windows: `winget install gitleaks`
- macOS: `brew install gitleaks`
- De hooks werken ook zonder gitleaks (dan alleen patroon-scan)

## Architecture

Serverless ETL pipeline: **Sportlink REST API -> Azure Function -> SQL Server**

**Two trigger functions** in `FunctionApp/Function1.cs`:
- `FetchAndStoreApiData` — Timer trigger (schedule via `%FETCH_SCHEDULE%` app setting, default `0 0 4 * * *`), fetches teams, matches, and match details
- `SyncMatchesHttp` — HTTP GET `/api/sync`, manual trigger with optional weekoffset params

**Data flow:** Sportlink JSON -> C# entity models -> staging tables (`stg.*`) -> stored procedure MERGE -> history tables (`his.*`) -> public views (`pub.*`)

**Database schemas:**
- `stg` — transient staging tables, truncated each run
- `his` — persistent history with `mta_inserted`/`mta_modified` metadata columns
- `mta` — `source_target_mapping` table drives dynamic table creation and merge operations
- `pub` — read-only views for consumers
- `dbo` — `AppSettings` (API URL, client ID, fetch schedule), `Season`, `DateTable`, `Speeltijden`

**Key stored procedures:** `sp_CreateTargetTableFromSource` (dynamic DDL), `sp_MergeStgToHis` (UPSERT via MERGE)

## README.md en SECURITY.md bijhouden

Deze twee bestanden zijn het publieke gezicht van het project op GitHub en moeten altijd de actuele stand weergeven.

**Wanneer updaten:**

| Situatie | README.md | SECURITY.md |
|---|---|---|
| Nieuwe functionaliteit (endpoint, scherm, integratie) | ✅ Beschrijf wat de functie doet voor gebruikers | Alleen als er een nieuw security-aspect aan zit |
| Nieuwe beveiligingsmaatregel of -bevinding | — | ✅ Beschrijf de maatregel (niet de aanvalsvector) |
| Nieuwe AVG-vereiste of gegevensverwerking | ✅ Vermeld in de AVG-sectie | ✅ Voeg toe aan de relevante beveiligingslaag |
| Architectuurwijziging (nieuwe laag, Azure-service) | ✅ Pas het architectuurdiagram aan | Alleen als de aanvalsoppervlakte wijzigt |
| Nieuwe club sluit aan | ✅ Pas "Geschikt voor" en het aantal clubs aan | — |

**Regel:** README.md en SECURITY.md gaan mee in dezelfde PR als de functionaliteitswijziging. Ze worden nooit afzonderlijk achterwege gelaten omdat "het later wel kan".

## Toekomstige versie: v2.0 Admin GUI (gepland — nog niet geïmplementeerd)

> **Status:** Volledig uitgewerkt in GitHub. Implementatie nog niet gestart. Alle issues gelabeld met `fase: N`. Epic: #26.

### Architectuur v2.0

```
Azure Static Web Apps (Free) — Blazor WebAssembly
  └── 5 schermen: Dashboard | Instellingen | E-mailtemplates | Voorkeurstijden | Email-tester
      │ SWA proxying (geen CORS, Function-sleutel veilig)
      ▼
Azure Functions (Consumption — bestaand)
  FunctionApp/Admin/           ← nieuwe map, Fase 3
  FunctionApp/Email/EmailTemplateService.cs  ← nieuw, Fase 2
      │
  Azure SQL (bestaand)
  dbo.EmailTemplateInstellingen  ← nieuw, Fase 1
  dbo.TeamVoorkeurTijden         ← nieuw, Fase 1
  dbo.AppSettingsAudit           ← nieuw, Fase 1 (CISO-eis)
```

### Technologiekeuze

| Laag | Keuze | Reden |
|---|---|---|
| Frontend | **Blazor WebAssembly** | .NET/C# stack, browser-native, geen server nodig |
| Hosting | **Azure Static Web Apps (Free)** | €0 gegarandeerd, ingebouwde Entra ID auth |
| Auth | **Entra ID** via SWA (admin / user rollen) | Zelfde tenant als Graph API mailbox |

### Geplande Admin API-endpoints (`FunctionApp/Admin/` — Fase 3)

| Endpoint | Bestand | Doel |
|---|---|---|
| `GET/PUT /api/admin/settings` | `AdminSettingsFunction.cs` | Instellingen lezen/opslaan + Function App restart bij schedule-wijziging |
| `GET /api/admin/sync/status` | `AdminSyncFunction.cs` | Synchronisatiestatus |
| `POST /api/admin/sync/trigger` | `AdminSyncFunction.cs` | Handmatige sync triggeren |
| `GET/PUT/POST /api/admin/templates` | `AdminTemplatesFunction.cs` | E-mailtemplates beheren |
| `GET/POST/PUT/DELETE /api/admin/voorkeurstijden` | `AdminVoorkeurTijdenFunction.cs` | Teamvoorkeurstijden |
| `GET /api/admin/email-log` | `AdminEmailLogFunction.cs` | Verwerkte emails (AVG: geen bodies) |
| `POST /api/test/email` | `EmailTestFunction.cs` | Dry-run AI-classificatietest (geen verzending, geen opslag) |

### Pre-version werk vereist vóór start

- **#30** — ClubCode toevoegen aan alle bestaande tabellen (multi-club fundament)
- **#85** — Architectuurdocumentatie bijwerken (dit issue)
- **#86** — AppSettings schema uitbreiden (Accommodatie, GPS, InternDomein, HerplanDeadlineDagen, BufferMinuten)

### Fasering (±25 uur totaal)

| Fase | Issues | Inhoud |
|---|---|---|
| Pre-version | #30, #85, #86, #63, #67 | ClubCode, AppSettings schema, domeinfilter, herplan-deadline |
| Fase 1 | #88, #62, #84 | DB-tabellen: AppSettingsAudit, TeamVoorkeurTijden, EmailTemplateInstellingen |
| Fase 2 | #84 | EmailTemplateService + EmailResponseGenerator refactor |
| Fase 3 | #27, #87, #89, #90, #91, #92, #93 | Admin API endpoints (7 endpoints) |
| Fase 4 | #94, #95 | Blazor WASM project + SWA aanmaken + routing config |
| Fase 5 | #96, #97, #98, #62 | Blazor Admin-schermen: Dashboard, Instellingen, Templates, Voorkeurstijden |
| Fase 6 | #99 | Blazor Email-tester scherm |
| Fase 7 | #100 | Entra ID app-registratie + roltoewijzing |
| Fase 8 | #101 | deploy.yml uitbreiden + smoke tests |

### CISO-aandachtspunten voor v2.0

- `SportlinkClientId` en Graph-secrets nooit zichtbaar in UI of API-responses
- `dbo.AppSettingsAudit`: append-only auditlog voor alle instellings- en templatewijzigingen
- Rate limiting op `/api/test/email`: max 10/minuut (OpenAI-kosten)
- AVG: `GET /api/admin/email-log` geeft nooit volledige e-mailbodies terug
- Defense in depth: rollen gehandhaafd door zowel SWA-routing als Function-endpoints

---

## Solution Structure

Two projects in `sportlink-wedstrijdzaken.sln`:

1. **FunctionApp/** (`fa-dev-sportlink-01.csproj`) — .NET 10 isolated worker Azure Function
   - `Function1.cs` — trigger functions and API fetch/store orchestration
   - `Utilities.cs` — AppSettings loader, DatabaseConfig, SeasonHelper, retry logic (5 retries, 5s delay)
   - `Enitities.cs` — Team, Match, MatchDetail models (note: filename typo is intentional legacy)
   - `CreateTable.cs` — dynamic staging table DDL
   - `MergeStgToHis.cs` — merge orchestration
   - Namespace: `SportlinkFunction`

2. **Database/** (`SportlinkSqlDb.sqlproj`) — SQL Server Database Project with schemas, tables, stored procedures, views

## Code Conventions

- Entity properties use **camelCase** matching Sportlink API JSON field names
- SQL column names use **exact casing** as defined in schema (e.g., `SportlinkApiUrl`, not `sportlinkApiUrl`)
- Async/await for all I/O; exception handling at function entry points
- App configuration lives in `dbo.AppSettings` table, not in code/config files

## Sportlink API

Base URL: `https://data.sportlink.com`, auth via `?clientId=` query param (from `dbo.AppSettings`).

Documentatie:
- **Alle endpoints:** https://sportlinkservices.freshdesk.com/nl/support/solutions/articles/9000062942-lijst-met-artikelen-van-club-dataservice
- **Online API test-tool:** https://sportlinkservices.github.io/navajofeeds-json-parser/article/?programma
- **JSON parser docs:** https://sportlinkservices.github.io/navajofeeds-json-parser/article/

| Endpoint | Path | Notes |
|---|---|---|
| Teams | `/teams?clientId=` | All club teams |
| **Programma** | `/programma?clientId=&weekoffset=` | **Primaire bron** voor alle wedstrijden (competitie, beker, oefenwedstrijden). Bevat scheidsrechter, veld, kleedkamers, logos |
| Uitslagen | `/uitslagen?clientId=&weekoffset=` | Alleen scoreverrijking voor verleden wedstrijden. Mag geen toekomstige wedstrijden toevoegen of programma-velden overschrijven |
| Match details | `/wedstrijd-informatie?clientId=&wedstrijdcode=` | Per-match detail |

See `FunctionApp/CLAUDE.md` for detailed field reference including all `/programma` fields.

## Exports — Teambegeleiding

De `exports/` map bevat **scripts** voor data-exports. De databestanden zelf (CSV, Excel) zijn **uitgesloten van git** vanwege AVG/GDPR.

**🚨 AVG/GDPR — ABSOLUTE REGELS (voor Claude én alle automation):**
- `exports/*.csv` en `exports/*.xlsx` bevatten persoonsgegevens (namen, e-mails, telefoonnummers, geboortedatums van clubleden)
- **NOOIT een CSV of Excel-bestand committen of pushen** — `.gitignore` blokkeert dit, maar controleer altijd
- De databestanden staan alleen lokaal en zijn alleen beschikbaar voor de applicatie zelf
- Alleen `.ps1` scripts, `README.md` en `HANDLEIDING-teambegeleiding-export.md` mogen in git

**Scripts in exports/:**
- `import-teambegeleiding-to-sql.ps1` — importeert CSV naar `avg.Teambegeleiding` in SQL Server (TRUNCATE + bulk insert)

**Workflow:**
1. Download CSV via club.sportlink.com (zie `exports/HANDLEIDING-teambegeleiding-export.md` voor exacte stappen)
2. Sla op in de lokale `exports/` map — nooit committen
3. Voer `.\exports\import-teambegeleiding-to-sql.ps1` uit om de data in SQL te laden
