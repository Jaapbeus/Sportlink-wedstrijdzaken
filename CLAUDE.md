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
| **Data Protection Officer (DPO)** | persoonsgegevens rechtmatig, veilig en transparant verwerkt wordt |

Bij spanning tussen rollen (bijv. snelheid vs. security): altijd melden.

## Sessie-isolatie — verplichte branch-check bij elke sessiestart

Meerdere Claude Code-sessies werken als onafhankelijke senior developers op hetzelfde project. **Dit is de eerste actie bij elke sessie, vóór elke code-wijziging of bestandsbewerking.** Claude lost dit volledig autonoom op — de gebruiker wordt hier nooit over bevraagd.

### Stap S0 — Branch valideren en zo nodig aanmaken (volledig autonoom)

```powershell
$branch = git branch --show-current   # leeg = detached HEAD
$safePrefix = 'feature/', 'hotfix/', 'chore/', 'docs/'

# Al op een geïsoleerde branch? Meteen doorgaan.
if ($safePrefix | Where-Object { $branch.StartsWith($_) }) { <# doorgaan #> }

# Op 'main', 'v2/develop' of detached HEAD → autonoom branch aanmaken:
#
# 1. Bepaal issue-nummer (volgorde, zonder te vragen):
#    a. Uit conversatiecontext ("werk aan #42", "issue #42", etc.)
#    b. gh issue list --state open --limit 20  →  kies meest relevante open issue
#    c. Geen passend issue?  →  gh issue create --title "..." --body "..."
#                                gebruik het nieuwe nummer
#
# 2. Bepaal branch-type en basisbranch:
#    - Urgente productiefix (bug zichtbaar op live/main):
#        git checkout -b hotfix/#<nr>-<slug> main
#    - Alle andere gevallen (features, fixes, docs, chores):
#        git checkout -b feature/#<nr>-<slug> v2/develop
```

**Overzicht branch-types:**

| Type | Basis | PR naar | Wanneer |
|---|---|---|---|
| `feature/#<nr>-<slug>` | `v2/develop` | `v2/develop` | Alles wat niet direct naar productie moet |
| `hotfix/#<nr>-<slug>` | `main` | `main` + daarna PR `main → v2/develop` | Urgente bug zichtbaar op live |

**Nooit committen of pushen naar `v2/develop` of `main` — uitsluitend via PR.**

---

## Autonome ontwikkelcyclus — zelfhelende lus

Claude werkt autonoom: van GitHub issue tot groen CI, zonder tussenkomst van de gebruiker. De lus hieronder is **verplicht** bij elke taak, niet optioneel.

### Stap 0 — Issue ophalen en branch aanmaken
```powershell
gh issue list --label "fase: N" --state open --limit 10  # haal prioriteit op
gh issue view <nr>                                         # lees volledig + gelinkte issues

# Branch aanmaken alleen als Stap S0 dit nog niet deed:
$branch = git branch --show-current
if ($branch -in @('main', 'v2/develop') -or [string]::IsNullOrEmpty($branch)) {
    git checkout -b feature/#<nr>-<slug> v2/develop
}
# Zit je al op feature/#<nr>-... → gewoon doorgaan
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

  f. .\Test-App.ps1 (met live services — secties 4+5+6 worden nu uitgevoerd)
     → exit 1? Fix, kill services, ga terug naar a.

  g. Controleer Blazor-pagina's:
       Invoke-WebRequest http://localhost:5242/ -UseBasicParsing
       Invoke-WebRequest http://localhost:5242/instellingen -UseBasicParsing
       (herhaal voor elke gewijzigde route)
       → fout of HTML bevat "An unhandled error"? Fix, kill services, ga terug naar a.

  h. Kill services:
       Stop-Process -Name "func" -ErrorAction SilentlyContinue
       Stop-Process -Name "dotnet" -ErrorAction SilentlyContinue
       Stop-Process -Name "node" -ErrorAction SilentlyContinue  # SWA CLI

GESLAAGD als: alle stappen exit 0 of 2xx, geen foutindicatoren
```

**SWA emulator (optioneel — voor auth-flow testen):**
```powershell
# Start alles inclusief SWA CLI op poort 4280:
.\Start-Debug.ps1 -Swa
# Admin GUI met auth-emulatie: http://localhost:4280
# Test-App.ps1 controleert automatisch poort 4280 als SWA draait (sectie 6)
```

### Stap 3 — Commit en PR
```powershell
git add <specifieke bestanden>          # nooit git add -A of git add .
git commit -m "feat(#<nr>): ..."
git push -u origin <huidige-branch>

# PR-target bepalen op basis van branch-type (zie Stap S0):
$branch = git branch --show-current
if ($branch.StartsWith('hotfix/')) {
    gh pr create --base main      --title "hotfix(#<nr>): ..." --body "..."
    # Na merge: ook PR van main → v2/develop aanmaken zodat develop gesynchroniseerd blijft
} else {
    gh pr create --base v2/develop --title "feat(#<nr>): ..."  --body "..."
}
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

6. **Elke sessie begint op een geïsoleerde branch — volledig autonoom geregeld.** Voer bij sessiestart altijd Stap S0 uit (zie "Sessie-isolatie" hierboven). Zit je op `v2/develop`, `main` of detached HEAD? Maak direct autonoom een branch aan — nooit vragen aan de gebruiker, nooit wachten, nooit een bestandswijziging vóór de branch bestaat. Issue-nummer bepaal je uit de conversatiecontext of via `gh issue list`; ontbreekt een passend issue, maak er dan zelf één aan.

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

## v2.0 Architectuur (live sinds 2026-05-17)

```
Browser (beheerder)
  └── Azure Static Web Apps (Free) — Blazor WebAssembly
        SWA dient alleen statische bestanden; geen SWA-proxying
        MSAL: Bearer token automatisch meegestuurd naar Function App
        URL: lively-field-03c896603.7.azurestaticapps.net
        │
        │ HTTPS + Bearer token (Entra ID)
        ▼
  Azure Functions (Consumption) — func-vrc-sportlink.azurewebsites.net
    Easy Auth: valideert Bearer token, injecteert X-MS-CLIENT-PRINCIPAL
    EasyAuthHelper: checkt 'admin' rol op alle /api/beheer/*, /api/test/*, /api/feedback/*
    FunctionApp/Admin/       → 9 bestanden, 18+ endpoints op /api/beheer/
    FunctionApp/Processing/  → BerichtPipeline (kanaal-agnostisch)
    FunctionApp/Feedback/    → Intelligente feedback widget → GitHub Issues
        │
        ▼
  Azure SQL — SportlinkSqlDb
    dbo.AppSettings + AppSettingsAudit
    dbo.EmailTemplateInstellingen, TeamVoorkeurTijden, TeamRegels
    dbo.UitgeslotenEmailAdressen, Velden, VeldBeschikbaarheid
    planner.EmailVerwerking (email-log)
    his.* / stg.* / pub.* (ETL pipeline)
```

### Auth-architectuur

| Laag | Mechanisme |
|---|---|
| Frontend | MSAL (`AddMsalAuthentication`) + `AuthorizationMessageHandler` |
| Transport | Bearer token in `Authorization` header |
| Function App | Azure Easy Auth (AllowAnonymous mode) + `EasyAuthHelper.RequireAdmin()` |
| Lokaal | Bypass: `WEBSITE_SITE_NAME` afwezig → altijd toestaan |

### Admin API-endpoints (`/api/beheer/`)

| Endpoints | Bestand |
|---|---|
| `GET/PUT /api/beheer/settings`, `GET /api/beheer/geocode` | `AdminSettingsFunction.cs` |
| `GET /api/beheer/sync/status`, `POST /api/beheer/sync/trigger` | `AdminSyncFunction.cs` |
| `GET /api/beheer/teams` | `AdminTeamsFunction.cs` |
| `GET/PUT/POST/DELETE /api/beheer/templates` | `AdminTemplatesFunction.cs` |
| `GET/POST/DELETE /api/beheer/uitgesloten-emails` | `AdminUitgeslotenEmailFunction.cs` |
| `GET/PUT/POST/DELETE /api/beheer/velden`, `/veldbeschikbaarheid` | `AdminVeldBeschikbaarheidFunction.cs` |
| `GET/POST/PUT/DELETE /api/beheer/voorkeurstijden`, `/teamregels` | `AdminVoorkeurTijdenFunction.cs` |
| `GET /api/beheer/email-log` | `AdminEmailLogFunction.cs` |
| `POST /api/test/email` | `EmailTestFunction.cs` |
| `POST /api/feedback/validate`, `/submit` | `FeedbackFunction.cs` |

### v2.1 backlog (epic #102)

Zelfherstellend systeem: auto-heal via GitHub Issues + Claude Code automatie (#107, #108, #109).

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
