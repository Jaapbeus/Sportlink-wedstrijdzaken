# Sportlink Wedstrijdzaken — Developer Setup (v2.7)

Volledige setupgids voor een nieuwe developer die de v2.7-stack lokaal wil draaien — op
**Windows** en op **macOS (Apple Silicon)**. Waar een commando platform-specifiek is, staan de
Windows- en de macOS-variant naast elkaar (#800).

> **Pad-notatie:** de `.\scripts\dev\...`-commando's in dit document staan in Windows-stijl
> (backslash). Op macOS werkt exact hetzelfde commando met forward slashes:
> `./scripts/dev/Start-Debug.ps1` in plaats van `.\scripts\dev\Start-Debug.ps1` — PowerShell 7
> herkent op macOS geen backslash als pad-scheidingsteken. Dit geldt voor elk script-pad in dit
> document (`scripts/dev/*.ps1`, `scripts/azure/*.ps1`), niet alleen voor Start-Debug.ps1.

---

## Snelstart (TL;DR)

```bash
# 0. Start de lokale database (identiek op Windows en macOS, zie sectie 4.1)
echo 'MSSQL_SA_PASSWORD=<jouw-sterke-wachtwoord>' > .env
docker compose up -d
```
```powershell
# 1. Kopieer en configureer local.settings.json
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
# Stel SqlConnectionString in op jouw SQL Server (zie sectie 5)

# 2. Configureer Sportlink API-credentials in dbo.AppSettings (zie sectie 4)

# 3. Start alle services
.\scripts\dev\Start-Debug.ps1

# 4. Verificeer (Start-Debug wacht zelf tot de services klaar zijn)
.\scripts\dev\Test-App.ps1
# exit 0 = alles werkt

# 5. Stoppen
.\scripts\dev\Stop-Debug.ps1
```

---

## Inhoudsopgave

1. [Vereisten](#1-vereisten)
2. [Software installeren](#2-software-installeren)
3. [Git hooks activeren](#3-git-hooks-activeren)
4. [Database opzetten](#4-database-opzetten)
5. [local.settings.json configureren](#5-localsettingsjson-configureren)
6. [Services starten (Start-Debug.ps1)](#6-services-starten)
7. [Verificatie (Test-App.ps1)](#7-verificatie)
8. [Projectstructuur](#8-projectstructuur)
9. [GitHub Actions — productie-deployment configureren](#9-github-actions-productie-deployment-configureren)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Vereisten

Dekt **Windows** en **macOS (Apple Silicon)**. Sla het platform over dat niet van toepassing is.

### Software

- [ ] **PowerShell 7** — alle scripts in `scripts/dev/` en `scripts/azure/` vereisen PowerShell 7
  (niet Windows PowerShell 5.1, niet zsh/bash) — cross-platform sinds #800.
  ```powershell
  # Windows
  winget install Microsoft.PowerShell
  ```
  ```bash
  # macOS — community Homebrew-formule (bouwt uit source; niet door Microsoft zelf onderhouden,
  # maar werkt prima). Alternatief: de officieel gesigneerde .pkg via de releases-pagina van
  # PowerShell.
  brew install powershell
  ```
- [ ] **.NET 9 Runtime** — vereist voor FunctionApp (Linux Consumption Plan ondersteunt net10.0 niet)
  ```powershell
  # Windows
  winget install Microsoft.DotNet.Runtime.9
  ```
  ```bash
  # macOS — dotnet-install script; --runtime dotnet installeert alleen de runtime
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && chmod +x /tmp/dotnet-install.sh && /tmp/dotnet-install.sh --channel 9.0 --runtime dotnet
  ```
- [ ] **.NET 10 SDK** — vereist voor BlazorAdmin
  ```powershell
  # Windows
  winget install Microsoft.DotNet.SDK.10
  ```
  ```bash
  # macOS — zonder --runtime installeert het script de SDK (incl. bijpassende .NET 10 runtime)
  /tmp/dotnet-install.sh --channel 10.0
  ```
  Beide macOS-installs schrijven naar dezelfde map `~/.dotnet` — .NET-runtimes van verschillende
  major-versies staan daar altijd side-by-side (`~/.dotnet/shared/Microsoft.NETCore.App/9.x.x` én
  `/10.x.x`); dat is standaardgedrag, geen speciale configuratie nodig. Voeg wel toe aan je
  shell-profiel, want een scriptinstallatie doet dat niet automatisch:
  ```bash
  echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.zshrc && echo 'export PATH=$PATH:$HOME/.dotnet:$HOME/.dotnet/tools' >> ~/.zshrc && source ~/.zshrc
  ```
  `DOTNET_ROOT` is alleen nodig bij deze scriptinstallatie. De macOS `.pkg`-installer (download via
  https://dotnet.microsoft.com/download/dotnet) regelt dit zelf, maar is een grafische installer
  per versie — voor scriptbare installatie van twee versies naast elkaar is `dotnet-install.sh` de
  aangewezen weg.
- [ ] **Azure Functions Core Tools v4**
  ```powershell
  # Windows
  npm install -g azure-functions-core-tools@4 --unsafe-perm true
  ```
  ```bash
  # macOS — Homebrew is de door Microsoft gedocumenteerde methode
  brew tap azure/functions && brew install azure-functions-core-tools@4
  ```
  > **Niet volledig geverifieerd:** er is geen officiële Microsoft-bevestiging gevonden dat dit
  > pakket een `dotnet-isolated net9.0`-app expliciet ondersteunt op Apple Silicon (arm64).
  > Installeer op deze manier en controleer bij de eerste `func start` of de FunctionApp
  > daadwerkelijk opstart — faalt dat op Apple Silicon, dan is dit de meest waarschijnlijke
  > oorzaak.
- [ ] **Node.js** (LTS) — voor Azurite
  ```powershell
  # Windows — download van https://nodejs.org/
  ```
  ```bash
  # macOS
  brew install node
  ```
- [ ] **Azurite** (Azure Storage Emulator) — cross-platform via npm, ongewijzigd op beide platforms
  ```
  npm install -g azurite
  ```
- [ ] **Docker Desktop** — voor de lokale database. SQL Server draait op beide platforms in een
  container (`docker-compose.yml` in de repo-root); een rechtstreeks geïnstalleerde SQL
  Server-service wordt niet meer ondersteund — zie sectie 4.1.

### Toegang en credentials

- [ ] Sportlink API URL en Client ID
- [ ] Een SQL-login voor de lokale database: gebruikersnaam `sa` plus het wachtwoord dat je aan de
  omgevingsvariabele `MSSQL_SA_PASSWORD` geeft (zie sectie 4.1) — of instantienaam/inloggegevens
  als je in plaats daarvan een bestaande externe SQL Server gebruikt

---

## 2. Software installeren

### Versies controleren

```powershell
dotnet --list-runtimes   # moet 'Microsoft.NETCore.App 9.x.x' bevatten
dotnet --version         # moet 10.x.x zijn (SDK)
func --version           # moet 4.x.x zijn
azurite --version        # moet aanwezig zijn
node --version           # moet LTS zijn
```

### Azure Functions Core Tools installeren (indien ontbreekt)

```powershell
# Windows
npm install -g azure-functions-core-tools@4 --unsafe-perm true
func --version  # verwacht: 4.x.x
```
```bash
# macOS — zie sectie 1 voor de aanbevolen Homebrew-installatie en de arm64-kanttekening
brew tap azure/functions && brew install azure-functions-core-tools@4
func --version
```

---

## 3. Git hooks activeren

De repository bevat pre-commit en pre-push hooks die scannen op gevoelige data (wachtwoorden, API-keys, servernamen) vóórdat een commit of push naar GitHub kan.

### 3.1 Hooks inschakelen

```bash
git config core.hooksPath .githooks
```

### 3.2 Gevoelige patronen configureren

```bash
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Vul `sensitive-patterns.txt` aan met je eigen waarden: servernaam, SQL-login, Sportlink Client ID, Azure resource namen.

### 3.3 Verificatie

```bash
git commit --allow-empty -m "test hooks"
# Verwacht: "Scanning staged files for sensitive data..." → "No sensitive data detected."
```

---

## 4. Database opzetten

> **Vandaag is SQL Server de enige bestaande tier.** Er is een vastgelegde multi-tier-strategie
> (Postgres → SQLite → Cosmos DB voor het e-maillog) — zie
> **[docs/ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md)** voor de bouwvolgorde en
> het waarom. Zodra een andere tier daadwerkelijk gebouwd is, komt de bijbehorende lokale
> setupinstructie in een eigen sectie hieronder — tot die tijd is de SQL Server-instructie in dit
> hoofdstuk voor élke fork van toepassing.
>
> **Welke tier een fork daadwerkelijk deployt, is een CI/deploy-tijd-keuze, geen lokale keuze**
> (#816): de GitHub repository-variabele `DatabaseTier` (Settings → Secrets and variables →
> Actions → Variables) bepaalt welk `.csproj` `deploy.yml` bouwt en publiceert naar de Function
> App — vandaag altijd `SqlServer`, de enige geïmplementeerde waarde. Ontbreekt de variabele of
> staat hij op een onbekende waarde, dan faalt de deploy-workflow hard (zie
> `scripts/ci/resolve-database-tier.sh`) — er is bewust geen stille default.

### 4.1 Lokale database starten (Docker — identiek op Windows en macOS)

Sinds #800 is Docker de **enige ondersteunde manier** om lokaal een database te draaien. Een
rechtstreeks op Windows geïnstalleerde SQL Server-service (named instance, Windows-authenticatie)
wordt niet meer ondersteund — dat pad werkte alleen op Windows en dwong een aparte code- en
documentatievariant af naast macOS. Docker gebruikt op beide platforms exact dezelfde image, poort
en verbindingsreeks. Vereist: Docker Desktop.

> Op Apple Silicon draait het image onder Rosetta-emulatie (Microsoft levert geen native
> ARM64-build van SQL Server); op Windows/Intel draait het native. Dat is het enige verschil, geen
> aparte procedure. Microsoft test en ondersteunt deze Rosetta-combinatie zelf niet officieel — de
> release notes van SQL Server on Linux noemen "emulation or translation environments" expliciet
> als buiten scope — maar in de praktijk draait de Developer-editie er stabiel genoeg voor lokale
> ontwikkeling.

De repository bevat `docker-compose.yml` in de root met de complete configuratie (image
`mcr.microsoft.com/mssql/server:2022-latest`, poort 1433, Developer-editie). Het SA-wachtwoord
staat bewust **niet** in dat bestand — dit is een publieke repo — maar komt uit de
omgevingsvariabele `MSSQL_SA_PASSWORD`. Zet die eenmalig, bijvoorbeeld in een `.env`-bestand naast
`docker-compose.yml` (staat in `.gitignore`):

```powershell
Set-Content -Path .env -Value "MSSQL_SA_PASSWORD=<jouw-sterke-wachtwoord>" -Encoding ascii
```
```bash
echo 'MSSQL_SA_PASSWORD=<jouw-sterke-wachtwoord>' > .env
```

Gebruik op Windows de PowerShell-variant en niet `echo ... > .env`: Windows PowerShell 5.1
schrijft dan een UTF-16-bestand, en `docker compose` leest dat niet als een geldig `.env`.

Wachtwoordeisen (SQL Server weigert de container anders stilletjes op te starten): minimaal 8
tekens, met hoofdletters, kleine letters en cijfers of leestekens. Ontbreekt de variabele, dan
weigert `docker compose` te starten met een expliciete foutmelding.

Starten en stoppen — identiek op beide platforms:

```bash
docker compose up -d
```
```bash
docker compose down
```

`docker compose down` laat het volume (en dus je data) staan; `docker compose down -v` verwijdert
de database definitief. `docker compose ps` toont of de container gezond is — de healthcheck erin
wacht tot `sqlcmd` daadwerkelijk verbinding kan maken, niet alleen tot het proces start.

De bijbehorende connection string voor `FunctionApp/local.settings.json` (zie sectie 5) en voor
handmatige `sqlcmd`-aanroepen:

```
Server=localhost,1433;Database=SportlinkSqlDb;User Id=sa;Password=<zelfde-wachtwoord-als-MSSQL_SA_PASSWORD>;TrustServerCertificate=True;
```

`TrustServerCertificate=True` is verplicht: `Microsoft.Data.SqlClient` 4.0+ verwacht standaard
`Encrypt=true`, en de container heeft alleen een self-signed certificaat.
`Microsoft.Data.SqlClient` zelf heeft op macOS geen aparte OpenSSL/unixODBC-installatie nodig — die
gebruikt Apple's eigen crypto-library.

**Alternatief:** heb je al toegang tot een bestaande, bereikbare SQL Server (bijv. Azure SQL)? Dan
is er geen lokale database nodig — vul die connection string gewoon in bij sectie 5. Dat werkt
identiek op Windows en macOS, want het is altijd dezelfde TDS-verbindingsstring.

### 4.2 Schema aanmaken

Het volledige schema komt uit **één script**: `Database/Script.PostDeployment1.sql`. Dat is
idempotent en bouwt een verse database in één keer compleet op — dezelfde weg die de
productie-deploy gebruikt, en die bij elke PR wordt bewezen door de CI-job *"PostDeployment op
verse database"*. De losse scripts in `FunctionApp/setup/` zijn ouder en hiervoor niet nodig.

De commando's hieronder draaien `sqlcmd` **binnen de container**. Dat scheelt een installatie van
`mssql-tools18` op je eigen machine, en het wachtwoord blijft in de omgevingsvariabele van de
container in plaats van in je opdrachtregelgeschiedenis.

```powershell
docker exec sportlink-sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -Q "IF DB_ID(''SportlinkSqlDb'') IS NULL CREATE DATABASE SportlinkSqlDb;"'
```
```powershell
docker cp Database/Script.PostDeployment1.sql sportlink-sqlserver:/tmp/postdeployment.sql
```
```powershell
docker exec sportlink-sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d SportlinkSqlDb -b -V 11 -i /tmp/postdeployment.sql'
```

`-b -V 11` zorgt dat `sqlcmd` een exitcode ≠ 0 teruggeeft zodra er een fout van severity 11 of
hoger optreedt. Zonder die vlaggen meldt `sqlcmd` exitcode 0 terwijl er severity-16-fouten in het
log staan — dat is eerder twee releases lang onopgemerkt gebleven. **Controleer het log dus ook
zelf op regels die met `Msg <nummer>, Level` beginnen; een exitcode 0 alleen is geen bewijs.**

Draai het script gerust een tweede keer: het hoort dan exitcode 0 te geven zonder fouten. Dat is
meteen de idempotentiecheck.

Verbinden kan verder met elke client: **Azure Data Studio** of de **MSSQL-extensie voor VS Code**
werken op Windows én macOS. SSMS bestaat alleen op Windows en is puur optioneel — handig om ín de
container te kijken, geen installatiestap. Wil je `sqlcmd` liever vanaf de host gebruiken, zie dan
de installatie-instructies in sectie 1; vergeet `-C` niet (self-signed certificaat).

**Controleren of het gelukt is:**

```powershell
docker exec sportlink-sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d SportlinkSqlDb -h -1 -W -Q "SELECT CONCAT((SELECT COUNT(*) FROM sys.tables), '' tabellen, '', (SELECT COUNT(*) FROM sys.procedures), '' procedures'');"'
```

Op een verse database hoort daar ruwweg **23 tabellen en 8 procedures** uit te komen, met de
schemas `dbo`, `stg`, `his`, `mta`, `pub`, `planner` en `avg`. `dbo.AppSettings` bevat dan twee
rijen: een lege placeholder-club en de demoklub `ALLSTARS`. Vul je eigen clubgegevens in via
sectie 4.3.

Het Database-project (`.sqlproj`) bevat alle actuele schemadefinities, maar is een legacy
SSDT-project dat **niet buiten Windows/Visual Studio gebouwd kan worden** — daarom staat het niet
in `sportlink-wedstrijdzaken.slnf` (zie sectie 8). Op macOS sla je publiceren via het
Database-project dus over: de `.sql`-bestanden onder `Database/dbo/Tables/` zijn ook de bron van
waarheid voor `Test-App.ps1 -Fix` (sectie 7), dat schema-drift herstelt zonder het `.sqlproj` te
bouwen. Publiceren via SqlPackage (of SSMS) blijft mogelijk, maar het `.dacpac` bouwen kan alleen
vanaf Windows:

```powershell
# Optioneel, vanaf Windows — het .dacpac bouwen vereist het .sqlproj
cd Database
sqlpackage /Action:Publish /SourceFile:SportlinkSqlDb.dacpac /TargetServerName:localhost,1433 /TargetDatabaseName:SportlinkSqlDb
```

`SqlPackage` zelf is cross-platform (`dotnet tool install -g microsoft.sqlpackage`), maar heeft
zonder een `.dacpac` niets te publiceren.

### 4.3 Sportlink API-credentials instellen

```sql
USE SportlinkSqlDb;
GO

UPDATE [dbo].[AppSettings]
SET
    [SportlinkApiUrl]    = 'https://data.sportlink.com',
    [SportlinkClientId]  = 'YOUR_ACTUAL_CLIENT_ID'   -- ⚠️ vervang door echte waarde
WHERE Id = 1;

SELECT * FROM [dbo].[AppSettings];   -- controleer resultaat
GO
```

### 4.4 Database verificatie

```sql
USE SportlinkSqlDb;
GO

-- Schemas aanwezig?
SELECT name FROM sys.schemas WHERE name IN ('stg','his','mta','dbo','planner','avg','pub');

-- Stored procedures aanwezig?
SELECT name FROM sys.procedures WHERE name IN ('sp_MergeStgToHis','sp_CreateTargetTableFromSource');

-- AppSettings correct?
SELECT [SportlinkApiUrl], [SportlinkClientId] FROM [dbo].[AppSettings];
```

---

## 5. local.settings.json configureren

```powershell
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
```

Stel de `SqlConnectionString` in — identiek op Windows en macOS, want de lokale database is in
beide gevallen dezelfde Docker-container (sectie 4.1) met een SQL-login:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SqlConnectionString": "Server=localhost,1433;Database=SportlinkSqlDb;User Id=sa;Password=<zelfde-wachtwoord-als-MSSQL_SA_PASSWORD>;TrustServerCertificate=True;"
  }
}
```

Gebruik je in plaats daarvan een bestaande externe SQL Server (bijv. Azure SQL)? Vervang dan
`Server=`/`User Id=`/`Password=` door de gegevens van die server; de rest van de string blijft
gelijk. `Test-App.ps1` leidt de `sqlcmd`-authenticatie automatisch af uit deze connection string
(#800) — een SQL-login met `User Id=`/`Password=` wordt herkend en het wachtwoord gaat naar
`sqlcmd` via de omgevingsvariabele `SQLCMDPASSWORD`, nooit als zichtbaar CLI-argument.

> `local.settings.json` staat in `.gitignore` en wordt nooit gecommit.

---

## 6. Services starten

De aanbevolen manier is via het Start-Debug.ps1-script. Dit start Azurite, FunctionApp en BlazorAdmin, en wacht daarna tot elke service daadwerkelijk reageert.

```powershell
.\scripts\dev\Start-Debug.ps1            # losse vensters per service
.\scripts\dev\Start-Debug.ps1 -Tail      # één samengevoegde logstroom in dit venster
.\scripts\dev\Start-Debug.ps1 -Clean     # dotnet clean BlazorAdmin vóór het starten
```

Het script pollt `GET /api/health` voor de FunctionApp en `GET /` voor BlazorAdmin, en meldt
de gemeten opstarttijd plus het versienummer. Je hoeft dus **niet** meer zelf een aantal
seconden af te wachten. Komt een service niet op, dan is de exit code 1.

Met `-Tail` gaat alle output naar één venster met een prefix per service — handig om snel te
zien welke service klaagt:

```
[FUNC]    [2026-07-27T17:31:21.396Z] Worker process started and initialized.
[BLAZOR]  Now listening on: http://localhost:5242
```

**Poorten:**

| Service | Poort | Opmerkingen |
|---------|-------|-------------|
| Azurite (blob/queue/table) | 10000–10002 | Azure Storage Emulator |
| FunctionApp | 7094 | `func start` — géén hot reload |
| BlazorAdmin | 5242 | `dotnet watch run` — hot reload actief |

**BlazorAdmin hot reload:** wijzigingen in `.razor`, `.cs` en `.css` worden automatisch doorgevoerd zonder herstart. Voor FunctionApp-wijzigingen moet je de services stoppen en `Start-Debug.ps1` opnieuw uitvoeren.

**Windows vs. macOS:** op Windows opent elke service een eigen consolevenster. Op macOS kan
`Start-Process` geen apart venster openen (gedocumenteerde beperking van PowerShell op dat
platform), dus daar schrijft `Start-Debug.ps1` de output altijd naar een logbestand onder
`<tijdelijke map>/sportlink-debug-logs` — hetzelfde gedrag als `-Tail` op Windows. Gebruik
`-Tail` op macOS om die logs direct samengevoegd in het huidige venster te zien.

### Handmatig starten (als Start-Debug.ps1 niet beschikbaar is)

**Windows** — elke service in een eigen venster:

```powershell
# 1. Azurite
$azuriteDir = Join-Path ([System.IO.Path]::GetTempPath()) 'azurite'
if (-not (Test-Path $azuriteDir)) { New-Item -ItemType Directory -Path $azuriteDir | Out-Null }
Start-Process powershell -ArgumentList "-NoExit -Command azurite --location '$azuriteDir'"
Start-Sleep -Seconds 3

# 2. FunctionApp (geen hot reload)
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location FunctionApp; func start --port 7094"

# 3. BlazorAdmin met hot reload
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location BlazorAdmin; dotnet watch run --launch-profile http"
```

**macOS** — `Start-Process` kan hier geen apart venster openen; open in plaats daarvan drie
Terminal-tabbladen en voer in elk tabblad één van deze commando's uit:

```bash
# Tab 1 — Azurite
mkdir -p /tmp/azurite-sportlink && azurite --location /tmp/azurite-sportlink
```
```bash
# Tab 2 — FunctionApp (geen hot reload)
cd FunctionApp && func start --port 7094
```
```bash
# Tab 3 — BlazorAdmin met hot reload
cd BlazorAdmin && dotnet watch run --launch-profile http
```

### Services stoppen

```powershell
.\scripts\dev\Stop-Debug.ps1           # FunctionApp + BlazorAdmin + SWA (Azurite blijft draaien)
.\scripts\dev\Stop-Debug.ps1 -All      # inclusief Azurite
.\scripts\dev\Stop-Debug.ps1 -Clean    # stop + dotnet clean BlazorAdmin
```

Gebruik dit script in plaats van `Stop-Process -Name "func","dotnet","node"`. Dat laatste is
zowel te grof (het sloopt élk `dotnet`-proces op je machine, ook onverwante projecten) als te
grof-korrelig: `dotnet watch` start zijn kindproces opnieuw op zodra dat wegvalt, dus alleen
de poort-eigenaar killen laat poort 5242 opnieuw bezet raken. `Stop-Debug.ps1` stopt hele
process-trees en wacht tot de poorten echt vrij zijn.

> **Fingerprint-regel:** roep NOOIT `dotnet build BlazorAdmin` aan terwijl de BlazorAdmin dev server draait.
> Twee compilatiepassen genereren twee sets content-hash fingerprints, wat leidt tot 404's op framework-JS.
> Voor build-foutdetectie: eerst `Stop-Debug.ps1`, dan bouwen. `Test-App.ps1` slaat de
> BlazorAdmin-build automatisch over zolang er iets op :5242 luistert.
> Herstellen na een mismatch: `.\scripts\dev\Stop-Debug.ps1 -Clean` en daarna opnieuw starten.

---

## 7. Verificatie

`Start-Debug.ps1` wacht zelf tot de services klaar zijn, dus je kunt direct doorgaan:

```powershell
# Basis verificatie
.\scripts\dev\Test-App.ps1

# Met automatisch herstel van schema-drift
.\scripts\dev\Test-App.ps1 -Fix
```

**Test-App.ps1 controleert:**
- Database-schema (tabellen, kolommen, stored procedures)
- FunctionApp gezondheid (`GET /api/health`)
- Admin API-endpoints
- BlazorAdmin pagina's (Blazor WASM laden zonder foutbanner)

**Geslaagd als:** exit code 0, health-endpoint 200, geen "An unhandled error has occurred" in Blazor.

### Handmatige health-check

```powershell
# FunctionApp
Invoke-RestMethod http://localhost:7094/api/health
# Verwacht: { "status": "ok", "version": "2.x.x" }

# BlazorAdmin
Invoke-WebRequest http://localhost:5242/ -UseBasicParsing
# Verwacht: HTTP 200 + Blazor WASM HTML (index.html)
```

### Bruno API-collectie (handmatig testen)

De map `bruno/` bevat een [Bruno](https://usebruno.com)-collectie met alle 72 endpoints uit
`docs/api-standaarden/openapi.yaml`, gegenereerd en gecommit zodat hij in git reviewbaar blijft en
in sync loopt met de spec. Open de map in de Bruno-app en kies de omgeving `local`
(`http://localhost:7094`).

**Twee beveiligingsschema's, niet automatisch per request gewisseld:**
- `core`/`planner`/`testdata`-endpoints (functionKey): de collectie is standaard op dit schema
  ingesteld (`?code={{apiKey}}`). Lokaal (`func start`) is dit niet verplicht.
- `beheer`/`feedback`-endpoints (Easy Auth Bearer/Entra ID): zet in Bruno de auth van dat specifieke
  request handmatig op "Bearer Token" met een geldig token, of laat leeg — lokaal wordt de
  admin-rolcheck overgeslagen wanneer `WEBSITE_SITE_NAME` afwezig is (zie `EasyAuthHelper.cs`).

Regenereren na een spec-wijziging: de `bruno-gen-collection`-skill (`ingest` → `plan` → `apply`),
uitgevoerd tegen `docs/api-standaarden/openapi.yaml`. `bruno-gen.json` legt het project en de
`local`-omgeving vast zodat dit zonder handmatige keuzes herhaalbaar is.

---

## 8. Projectstructuur

```
sportlink-wedstrijdzaken/
├── sportlink-wedstrijdzaken.sln       # Volledige solution (incl. Database/SportlinkSqlDb.sqlproj — alleen op Windows te bouwen)
├── sportlink-wedstrijdzaken.slnf      # Solution filter zonder het .sqlproj — gebruik dit op macOS (#800)
├── .gitattributes                     # Regeleindes vastgelegd (LF voor .sh/.githooks) zodat git-hooks op macOS werken (#800)
├── docker-compose.yml                 # Lokale SQL Server 2022 — enige ondersteunde manier, identiek op Windows/macOS (#800)
├── FunctionApp/
│   ├── fa-dev-sportlink-01.csproj     # .NET 9 Azure Functions isolated worker
│   ├── Function1.cs                   # Timer + HTTP sync triggers
│   ├── Utilities.cs                   # AppSettings, DatabaseConfig, SeasonHelper
│   ├── Admin/                         # 12 Admin-endpoint bestanden (beheer/*)
│   ├── Planner/                       # Planner-endpoints (check-availability, auto-plan, ...)
│   ├── Email/                         # Email-verwerkingspipeline
│   ├── Feedback/                      # Feedback-widget (→ GitHub Issues)
│   ├── local.settings.json            # NIET in git — bevat SqlConnectionString
│   └── local.settings.template.json   # Template, wél in git
├── BlazorAdmin/
│   ├── BlazorAdmin.csproj             # .NET 10 Blazor WebAssembly
│   ├── Pages/                         # Razor-pagina's
│   ├── Shared/                        # Gedeelde componenten (TimeInput, MainLayout, ...)
│   └── wwwroot/
│       ├── appsettings.json           # Localhost-config (in git)
│       ├── appsettings.Production.template.json  # CI-template (in git)
│       └── appsettings.Production.json           # NIET in git — gegenereerd door CI
├── Database/
│   └── SportlinkSqlDb.sqlproj         # SQL Server Database Project
├── scripts/
│   ├── dev/
│   │   ├── Start-Debug.ps1            # Start alle lokale services + wacht op readiness
│   │   ├── Stop-Debug.ps1             # Stopt de services (process-trees, -Clean voor fingerprints)
│   │   ├── DevServices.psm1           # Gedeelde helpers: readiness-polling, teardown, cross-platform poort-/procesdetectie (#800)
│   │   └── Test-App.ps1               # Verificatie na opstarten
│   ├── azure/
│   │   ├── Verify-AzureAuthSetup.ps1  # Diagnose Entra-configuratie (read-only)
│   │   └── Configure-EntraApp.ps1     # Idempotente Entra-configuratie (apply)
│   └── db/
│       └── setup-local-database.sql   # Database-initialisatie
└── docs/                              # Documentatie
```

---

## 9. GitHub Actions — Productie-deployment configureren

De CI/CD-pipeline in `.github/workflows/deploy.yml` deployt automatisch naar Azure bij elke push naar `main`. Hiervoor zijn twee soorten GitHub-configuratie nodig:

- **Secrets** — versleuteld opgeslagen, nooit zichtbaar in logs
- **Variables** — leesbaar in logs, niet bedoeld voor gevoelige waarden

Navigeer naar: **GitHub → jouw fork → Settings → Secrets and variables → Actions**

### 9.1 Secrets instellen

Klik op **New repository secret** voor elk van de volgende:

| Naam | Beschrijving | Waar te vinden |
|------|-------------|----------------|
| `AZURE_CREDENTIALS` | JSON van Azure service principal | Zie stap 9.2 hieronder |
| `AZURE_FUNCTION_KEY` | Host key van de Function App | Azure Portal → Function App → App keys → Host keys → `default` |
| `SQL_CONNECTION_STRING` | Productie SQL-verbindingsstring | Azure Portal → SQL Database → Connection strings → ADO.NET |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | SWA deployment token | Azure Portal → Static Web App → Manage deployment token |

**`AZURE_CREDENTIALS` aanmaken via Azure CLI:**

```bash
az ad sp create-for-rbac \
  --name "sp-[clubcode]-sportlink-deploy" \
  --role contributor \
  --scopes /subscriptions/<subscription-id>/resourceGroups/<resource-group> \
  --sdk-auth
```

Kopieer de volledige JSON-output (inclusief accolades) als waarde voor het secret.

**`SQL_CONNECTION_STRING` formaat:**

```
Server=tcp:[sql-servernaam].database.windows.net,1433;Initial Catalog=[database-naam];
Persist Security Info=False;User ID=[username];Password=[password];
Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
```

### 9.2 Variables instellen

Klik op het tabblad **Variables** → **New repository variable** voor elk van de volgende:

| Naam | Voorbeeld | Beschrijving |
|------|-----------|-------------|
| `AZURE_FUNCTIONAPP_NAME` | `func-[clubcode]-sportlink` | Naam van de Function App (zonder `.azurewebsites.net`) |
| `AZURE_FUNCTIONAPP_URL` | `https://func-[clubcode]-sportlink.azurewebsites.net` | Volledige URL inclusief `https://` — voor Blazor-configuratie |
| `AZURE_SQL_SERVER_NAME` | `[sql-servernaam]` | SQL-servernaam **zonder** `.database.windows.net` |
| `AZURE_SQL_DATABASE_NAME` | `[database-naam]` | Naam van de SQL-database |
| `AZURE_SQL_RESOURCE_GROUP` | `rg-[clubcode]-sportlink` | Azure resource group van de SQL-server |
| `AZURE_STATIC_WEB_APP_HOSTNAME` | `[naam].azurestaticapps.net` | Hostname van de Static Web App **zonder** `https://` |
| `AZURE_AD_TENANT_ID` | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | Azure Entra tenant ID (GUID) |
| `AZURE_AD_CLIENT_ID` | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | App Registration client ID (GUID) |
| `POST_LOGOUT_REDIRECT_URL` | `https://[naam].azurestaticapps.net/` | URL na uitloggen (inclusief trailing slash) |

### 9.3 Welke configuratie is optioneel?

| Jobs | Vereiste configuratie | Gedrag zonder configuratie |
|------|-----------------------|---------------------------|
| `db-check` + `db-migrate` | `AZURE_SQL_SERVER_NAME`, `AZURE_SQL_DATABASE_NAME`, `AZURE_SQL_RESOURCE_GROUP`, `SQL_CONNECTION_STRING` | Jobs worden overgeslagen |
| `blazor-deploy` + SWA smoke test | `AZURE_STATIC_WEB_APPS_API_TOKEN`, `AZURE_STATIC_WEB_APP_HOSTNAME` | Job wordt overgeslagen |
| `build` + `test` | `AZURE_CREDENTIALS`, `AZURE_FUNCTIONAPP_NAME`, `AZURE_FUNCTION_KEY` | Verplicht — mislukken bij ontbreken |

### 9.4 Verificatie na instellen

```powershell
# Haal run-ID op
gh run list --branch main --limit 3

# Controleer alle jobs
gh run view <run-id> --json jobs --jq '.jobs[] | {name: .name, conclusion: .conclusion}'
# Alle jobs moeten "success" of "skipped" zijn
```

---

## 10. Troubleshooting

### FunctionApp start niet — 503 of "Function host is not running"

Controleer de .NET runtime-versie:

```powershell
dotnet --list-runtimes
# Moet bevatten: Microsoft.NETCore.App 9.x.x
```

Ontbreekt .NET 9?

```powershell
# Windows
winget install Microsoft.DotNet.Runtime.9
```
```bash
# macOS
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && chmod +x /tmp/dotnet-install.sh && /tmp/dotnet-install.sh --channel 9.0 --runtime dotnet
```

> .NET 10 als runtime voor FunctionApp geeft een 503 op Azure Consumption Plan. Zie CLAUDE.md voor
> details. Op Apple Silicon: faalt `func start` al bij de eerste run, controleer dan eerst de
> arm64-kanttekening bij Azure Functions Core Tools in sectie 1 voordat je verder zoekt.

### "Cannot connect to database"

Identiek op Windows en macOS — de lokale database is in beide gevallen de Docker-container uit
sectie 4.1. Controleer eerst of de container gezond is:

```bash
docker compose ps
```

Staat `sqlserver` er niet gezond (`healthy`) bij, bekijk dan de logs:

```bash
docker compose logs sqlserver
```

Test de verbinding zelf (vraagt om het wachtwoord als je `-P` weglaat):

```bash
sqlcmd -S localhost,1433 -U sa -d SportlinkSqlDb -C -Q "SELECT @@VERSION"
```

1. Controleer `SqlConnectionString` in `local.settings.json`
2. Controleer of `MSSQL_SA_PASSWORD` gezet was vóór `docker compose up -d` (zonder die variabele weigert de container te starten)
3. Controleer of `SportlinkSqlDb` bestaat (zie sectie 4.2 — is het schema al aangemaakt?)

### "401 Unauthorized" op Sportlink API

```sql
SELECT [SportlinkApiUrl], [SportlinkClientId] FROM [dbo].[AppSettings];
```

Controleer of de waarden niet de placeholder `YOUR_ACTUAL_CLIENT_ID` bevatten.

### "Azurite connection failed"

Controleer of Azurite draait op poort 10000:

```powershell
# Windows
Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue
```
```bash
# macOS
lsof -nP -iTCP:10000 -sTCP:LISTEN
```

Start Azurite handmatig (cross-platform, werkt op beide platforms in PowerShell 7):

```powershell
azurite --silent --location ([System.IO.Path]::GetTempPath() + 'azurite')
```

### Blazor toont "An unhandled error has occurred"

Dit is bijna altijd een fingerprint-mismatch. Oplossing:

```powershell
# Stop de services én ruim de stale fingerprints op
.\scripts\dev\Stop-Debug.ps1 -Clean

# Herstart
.\scripts\dev\Start-Debug.ps1
```

Kortere variant, in één commando:

```powershell
.\scripts\dev\Start-Debug.ps1 -Clean
```

Open daarna `http://localhost:5242` in een **nieuw Incognito-venster** (Ctrl+Shift+F5 werkt soms niet voldoende).

### Stored procedure niet gevonden

```sql
SELECT name FROM sys.procedures WHERE name IN ('sp_MergeStgToHis','sp_CreateTargetTableFromSource');
```

Publiceer het Database-project opnieuw (zie sectie 4.2).

---

**Versie:** 2.7 — bijgewerkt 2026-08-29 (macOS/Apple Silicon-ondersteuning + Docker als enige lokale-database-optie, #800)
