# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Rollen van Claude in dit project

Claude vervult in dit project vier gecombineerde rollen. Elke taak wordt vanuit alle toepasselijke
perspectieven benaderd:

| Rol | Verantwoordelijkheid |
|---|---|
| **Senior Software Architect** | Codestructuur, naamgeving, abstractieniveau, onderhoudbaarheid — geen onnodige complexiteit |
| **Senior Solution Architect** | End-to-end ontwerp: Functions + Blazor + SWA + SQL + Entra ID in samenhang; kostenmodel bewaken |
| **CISO** | Security gate leidend; secrets nooit in code/logs/responses; AVG-compliance; dependency vulnerabilities |
| **Senior Application Tester** | `dotnet build` ≠ werkt; altijd smoke test vóór oplevering; runtime-issues detecteren die compiler mist |

Bij spanning tussen rollen (bijv. snelheid vs. security): altijd melden.

## Autonome ontwikkelcyclus — zelfhelende lus

Claude werkt autonoom: van GitHub issue tot groen CI, zonder tussenkomst van de gebruiker. De lus hieronder is **verplicht** bij elke taak, niet optioneel.

### Stap 0 — Issue ophalen en branch aanmaken
```powershell
gh issue list --label "fase: N" --state open --limit 10  # haal prioriteit op
gh issue view <nr>                                         # lees volledig + gelinkte issues
git checkout -b feature/#<nr>-<slug> v2/develop
```

### Stap 1 — Implementeer (altijd alle lagen synchroon)
- DB-schema eerst → dan API-endpoint → dan Blazor GUI — nooit één laag zonder de andere
- Check: ClubCode discriminator aanwezig? UTC in DB? GUI bijgewerkt? CISO-regels?

### Stap 2 — Verificatielus (herhaal tot exit 0, max 3 iteraties)

```
ITERATIE:
  a. dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug
     → fouten? Fix, ga terug naar a.

  b. dotnet build BlazorAdmin/BlazorAdmin.csproj  (als Blazor bestaat)
     → fouten? Fix, ga terug naar a.

  c. .\Test-App.ps1 -Fix
     → exit 1 zonder -Fix te herstellen? Fix code, ga terug naar a.

  d. Start services (achtergrond, volgorde is verplicht):
       # 1. Azurite (vereist door func start)
       $azuriteRunning = [bool](Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue)
       if (-not $azuriteRunning) {
           $azuriteDir = Join-Path $env:TEMP 'azurite'
           if (-not (Test-Path $azuriteDir)) { New-Item -ItemType Directory -Path $azuriteDir | Out-Null }
           Start-Process powershell -ArgumentList "-NoExit -Command azurite --location '$azuriteDir'"
           Start-Sleep -Seconds 3
       }
       # 2. FunctionApp
       Start-Process powershell -ArgumentList "-NoExit -Command Set-Location FunctionApp; func start --port 7094"
       # 3. BlazorAdmin (alleen als project bestaat)
       if (Test-Path "BlazorAdmin/BlazorAdmin.csproj") {
           Start-Process powershell -ArgumentList "-NoExit -Command Set-Location BlazorAdmin; dotnet run --launch-profile http"
       }
       Start-Sleep -Seconds 15

  e. Controleer FunctionApp health:
       Invoke-RestMethod http://localhost:7094/api/health
       → niet 200? Fix, kill services, ga terug naar a.

  f. .\Test-App.ps1 (met live services — secties 4+5 worden nu uitgevoerd)
     → exit 1? Fix, kill services, ga terug naar a.

  g. Controleer Blazor-pagina's:
       Invoke-WebRequest http://localhost:5242/ -UseBasicParsing
       Invoke-WebRequest http://localhost:5242/instellingen -UseBasicParsing
       (herhaal voor elke gewijzigde route)
       → fout of HTML bevat "An unhandled error"? Fix, kill services, ga terug naar a.

  h. Kill services:
       Stop-Process -Name "func" -ErrorAction SilentlyContinue
       Stop-Process -Name "dotnet" -ErrorAction SilentlyContinue

GESLAAGD als: alle stappen exit 0 of 2xx, geen foutindicatoren
```

### Stap 3 — Commit en PR
```powershell
git add <specifieke bestanden>          # nooit git add -A of git add .
git commit -m "feat(#<nr>): ..."
git push -u origin feature/#<nr>-<slug>
gh pr create --base v2/develop --title "..." --body "..."
```

### Stap 4 — CI bewaken
```powershell
gh pr checks <pr-nr> --watch           # wacht op groen
```
- CI rood door build/code-fout? Fix → push → herhaal stap 4.
- **Security Gate rood? → STOP. Meld aan gebruiker. Nooit mergen.**

### Stap 5 — Rapporteer aan gebruiker
Alleen als alles groen: PR-URL, issue-nr, samenvatting van wijzigingen.

### Escaleer naar gebruiker bij (en alleen bij):
- Security Gate blijft rood na fixpoging
- > 3 iteraties in verificatielus zonder voortgang
- Architectuurkeuze met meerdere gelijkwaardige paden
- AVG/CISO-blokkade die codekeuze vereist

---

## Absolute veiligheidsregels — nooit omzeilen

Deze regels gelden altijd, zonder uitzondering:

1. **Na elke push of commit: CI-status controleren.** Nooit aan de gebruiker melden dat iets klaar of succesvol is zonder eerst te verifiëren dat alle GitHub Actions checks geslaagd zijn (`gh pr checks <nr>` of `gh run list`).

2. **Na elke PR-merge: ook de deploy/build-workflow op `main` controleren.** Na merge direct `gh run list --branch main --limit 3` uitvoeren en wachten op voltooiing van `deploy.yml`. Als de build faalt: direct proberen te fixen. Lukt dit niet: onmiddellijk melden aan de gebruiker. Pas daarna melden dat de PR succesvol is afgerond.

3. **Build- en runtime-fouten zijn zelfherstelbaar — Security Gate niet.** Bij een build-fout, startup-fout of testfout: fix het zelf en herhaal de verificatielus (zie "Autonome ontwikkelcyclus"). Bij een **Security Gate-fout of AVG-schending**: stop direct en meld aan de gebruiker — nooit stilzwijgend doorgaan of zelf mergen.

4. **Persoonsgegevens, wachtwoorden en tokens nooit in bestanden schrijven.** Ook niet tijdelijk, ook niet in commentaar, ook niet in documentatie. Bij twijfel: het gaat niet in git.

5. **De Security Gate job is leidend.** Zolang `Security Gate — blokkeert merge bij fout` rood is, mag er niets gemerged worden — ook al zijn andere checks groen.

Zie [SECURITY.md](SECURITY.md) voor het volledige protocol.

## Architectuurregels — altijd van toepassing

### UTC in database, lokale tijd in GUI

- **Database:** alle `DateTime` kolommen opslaan in **UTC** (GETUTCDATE(), geen GETDATE()).
- **API (FunctionApp):** SQL Server levert `DateTimeKind.Unspecified` via `Convert.ToDateTime()` → altijd omzetten naar UTC met `DateTime.SpecifyKind(dt, DateTimeKind.Utc)` zodat de JSON-serializer een `Z`-suffix toevoegt.
- **Blazor WASM:** gebruik altijd `ToLocalTime()` voor weergave. De browser converteert UTC naar de tijdzone van de gebruiker (Nederland = CET winter / CEST zomer). Nooit een UTC-tijd tonen zonder conversie.
- Reden: Nederlanders zien anders 02:00 in de ochtend als "04:00" en andersom bij zomertijdwissel.

### GUI en code altijd synchroon

- Als er een placeholder, template-key, enum-waarde of regeltype wordt toegevoegd aan de **code of database**, dan wordt de **GUI** in dezelfde commit bijgewerkt.
- Als er een UI-veld wordt toegevoegd, wordt ook gecontroleerd of de API en het datamodel meegegroeid zijn.
- Nooit de GUI laten achterlopen op de code, en nooit de code laten achterlopen op de GUI.

### Geen club-specifieke strings in code — nooit

- Fallback-waarden (`?? "..."`) in C#-code mogen **nooit** een clubnaam, domeinnaam, persoonsnaam, plaatsnaam of adres bevatten.
- Als een verplichte instelling ontbreekt in `dbo.AppSettings` → gooi een `InvalidOperationException`. Een stille fallback maskeert misconfiguratie en breekt multi-club ondersteuning.
- **Correct:** `GetSetting("clubCode") ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings")`
- **Fout:** `GetSetting("clubCode") ?? "VRC"` — nooit een clubnaam als default
- **Fout:** `GetSetting("plannerAfzenderNaam") ?? "VRC Veldplanner"` — nooit
- Documentatie-voorbeelden bevatten `[ClubNaam]` als placeholder, nooit echte club-specifieke waarden die in code kunnen terechtkomen.
- Check bij codereview: scan op `?? "` gevolgd door een eigennaam, clubnaam, of adres.

### Microsoft Learn MCP server

- Gebruik de Microsoft Learn MCP server proactief voor C#, .NET, Blazor, Azure Functions en Azure best practices.
- Tools: `mcp__claude_ai_Microsoft_Learn__microsoft_docs_search` (snel overzicht), `mcp__claude_ai_Microsoft_Learn__microsoft_code_sample_search` (codevoorbeelden), `mcp__claude_ai_Microsoft_Learn__microsoft_docs_fetch` (volledige pagina).
- Workflow: zoek eerst → haal diepere docs op bij twijfel → gebruik officiële bronnen als grond voor architectuurbeslissingen.
- Combineer met eigen kennis als architect; MCP-resultaten zijn leidend bij conflicten met training-data.

## Versiebeheer en Release-protocol

### Semantic Versioning (semver)

Versienummering volgt `MAJOR.MINOR.PATCH`:

| Type | Wanneer | Voorbeeld |
|---|---|---|
| **MAJOR** (x.0.0) | Nieuwe architectuurlaag, breaking API-wijziging, grote nieuwe functie-set | v2.0.0 — Admin GUI toegevoegd |
| **MINOR** (2.x.0) | Nieuwe feature, backwards compatible (nieuw endpoint, nieuw scherm) | v2.1.0 — WhatsApp-kanaal toegevoegd |
| **PATCH** (2.0.x) | Bugfix, beveiligingspatch, documentatie zonder gedragswijziging | v2.0.1 — 500-error op teams-endpoint |

> Volledige definities (bug vs. issue vs. feature vs. enhancement, wat in changelog hoort):
> zie [docs/VERSIONING.md](docs/VERSIONING.md).

### Conventional Commits → versie-bump

Commit-type bepaalt de minimum versie-bump:
- `feat:` → MINOR bump
- `fix:` → PATCH bump
- `security:` → PATCH bump
- `BREAKING CHANGE:` in commit-body → MAJOR bump
- `chore:`, `docs:`, `refactor:` → geen versie-bump (tenzij er ook een `fix:`/`feat:` bij zit)

### CHANGELOG.md bijhouden

**Verplicht bij elke commit die een feature of fix bevat:**

1. Voeg de wijziging toe onder `## [Unreleased]` in `CHANGELOG.md`
2. Gebruik de secties `### Added`, `### Changed`, `### Fixed`, `### Security`, `### Removed`
3. Schrijf voor de gebruiker, niet voor de developer: "Beheerders kunnen nu X" i.p.v. "Methode Y refactored"

**Verplicht vóór een release:**
1. Verplaats alles van `## [Unreleased]` naar `## [x.y.z] — YYYY-MM-DD`
2. Voeg een lege `## [Unreleased]` terug bovenaan
3. Bump de versie in `FunctionApp/fa-dev-sportlink-01.csproj` en `BlazorAdmin/BlazorAdmin.csproj`

### Release-workflow

```powershell
# 1. Zorg dat v2/develop up-to-date en groen is
git checkout v2/develop
.\Test-App.ps1           # moet exit 0

# 2. PR aanmaken en mergen naar main (via GitHub)
gh pr create --base main --title "release: v2.0.1" ...

# 3. Na merge: tag aanmaken op main
git checkout main && git pull
git tag v2.0.1 -m "Release v2.0.1"
git push origin v2.0.1  # triggert release.yml workflow automatisch

# 4. GitHub Release wordt automatisch aangemaakt door release.yml
# Body komt uit CHANGELOG.md — sectie [2.0.1]
```

Of via GitHub Actions UI (workflow_dispatch in release.yml) zonder lokale tag.

### Versienummer ophalen in code

```csharp
// Versie is beschikbaar via assembly-metadata (gezet in .csproj):
var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "onbekend";
// → "2.0.0"
```

Gebruik dit bijv. in de health-endpoint response of in de Admin GUI footer.

## Build & Run

> **`dotnet build` slagen ≠ werkt.** De enige definitie van "werkt" is: build groen + func start zonder crashes + health endpoint 200 + Test-App.ps1 exit 0. Volg altijd de autonome verificatielus hierboven.

```powershell
# Stap 1: Build
dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug

# Stap 2: Start alle services tegelijk (of gebruik Start-Debug.ps1)
.\Start-Debug.ps1                     # start Azurite + FunctionApp + BlazorAdmin in aparte vensters
# Poorten: Azurite :10000, FunctionApp :7094, BlazorAdmin :5242

# Stap 3: Verificatie (wacht 15s na Start-Debug)
.\Test-App.ps1                        # controleert schema, build, endpoints, Blazor-pagina's
.\Test-App.ps1 -Fix                   # herstelt schema-drift automatisch

# Handmatige sync
# GET http://localhost:7094/api/sync?weekOffsetFrom=X&weekOffsetTo=Y
```

**Prerequisites:** .NET 10.0 SDK, Azure Functions Core Tools v4, Azurite (Azure Storage Emulator), SQL Server met `SportlinkSqlDb` database.

**Configuration:** Kopieer `FunctionApp/local.settings.template.json` naar `local.settings.json` en stel `SqlConnectionString` in op je SQL Server.

**Verificatiescripts:** `Test-App.ps1` (schema + build + endpoints + Blazor), `Start-Debug.ps1` (alle services).  
Zie [FunctionApp/docs/TESTING.md](FunctionApp/docs/TESTING.md) voor volledig overzicht.

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
