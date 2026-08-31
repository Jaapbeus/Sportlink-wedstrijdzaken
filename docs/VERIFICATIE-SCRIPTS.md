# Verificatie & zelfherstellende tests

Geldt voor **Windows** en **macOS (Apple Silicon)** (#800) — beide scripts en `DevServices.psm1`
zijn cross-platform; waar een commando toch platform-specifiek is staat de macOS-variant
ernaast.

## Test-App.ps1

`scripts/dev/scripts/dev/Test-App.ps1` is het centrale verificatie- en herstelscript.
Het controleert schema, build en runtime in één doorloop.

**sqlcmd-authenticatie, cross-platform afgeleid (#800):** het script hardcodeerde vroeger `-E`
(Windows Integrated Authentication) — dat werkte toevallig omdat de lokale database vroeger een
Windows SQL Server-service was. Sinds de lokale database uitsluitend nog de Docker-container uit
`docker-compose.yml` is (identiek op Windows en macOS, zie DEVELOPER-SETUP.md sectie 4.1), gebruikt
iedereen een SQL-login, en leidt het script de authenticatie af uit `SqlConnectionString` in
`local.settings.json` in plaats van `-E` te hardcoden:
- Staat er `User Id=`/`Password=` in (de standaardsituatie)? → `-U <user>` plus het wachtwoord via
  de omgevingsvariabele `SQLCMDPASSWORD` (nooit via `-P`: argumenten zijn op beide platforms
  zichtbaar in de procesenlijst, een omgevingsvariabele van het kindproces niet).
- Staat er `TrustServerCertificate=True` in (zoals tegen de lokale container altijd het geval is)?
  → `-C` wordt toegevoegd (nodig vanwege het self-signed certificaat van de container).
- Staat er toch nog `Integrated Security=True`/`Trusted_Connection=True` in — een restant van een
  oude, niet meer ondersteunde lokale SQL Server-installatie? Dan werkt dat alléén nog op Windows
  (`-E`); op macOS/Linux **stopt het script direct** met een duidelijke foutmelding. Dit is geen
  aanbevolen configuratie, alleen defensief afgevangen.

Ook de server/database-parsing accepteert nu zowel `Data Source=`/`Initial Catalog=` als
`Server=`/`Database=` (dat laatste gebruikt `local.settings.template.json`).

### Gebruik

```powershell
# Alleen checken (geen wijzigingen)
.\scripts\dev\Test-App.ps1

# Automatisch herstellen waar mogelijk
.\scripts\dev\Test-App.ps1 -Fix

# Alles tonen, ook successen
.\scripts\dev\Test-App.ps1 -Verbose
.\scripts\dev\Test-App.ps1 -Fix -Verbose
```

### Wat wordt gecontroleerd

| Sectie | Controle | -Fix |
|--------|----------|------|
| 1. DB-verbinding | `local.settings.json` aanwezig en geldig | nee |
| 2. Schema | Alle 8 tabellen én hun kolommen | ja — ALTER TABLE / CREATE TABLE |
| 3. Build | `dotnet build` FunctionApp + BlazorAdmin | nee |
| 4. API smoke | 11 endpoints op `:7094` | nee (2xx verwacht) |
| 5. Blazor pagina's | 8 routes op `:5242` | nee (geen Blazor-foutindicatoren) |

Secties 4 en 5 worden automatisch overgeslagen als de services niet draaien.

### Bewake tabellen

```
dbo.AppSettings              — ClubName, ClubCode, Accommodatie, GPS, ...
dbo.TeamVoorkeurTijden       — Id, TeamNaam, DagVanWeek, VoorkeurTijd, ...
dbo.VeldBeschikbaarheid      — Id, VeldNummer, DagVanWeek, BeschikbaarVanaf, ...
dbo.UitgeslotenEmailAdressen — Id, EmailAdres, Omschrijving, Actief, ClubCode
dbo.EmailTemplateInstellingen— Id, TemplateKey, Onderwerp, BodyTemplate, ...
dbo.AppSettingsAudit         — Id, GewijzigdDoor, Veld, OudeWaarde, ...
dbo.TeamRegels               — Id, TeamNaam, RegelType, ...
dbo.Velden                   — VeldNummer, VeldNaam, VeldType, ...
```

### Verplicht workflow

1. Wijziging in code, API-contract of database-schema?  
   → `.\scripts\dev\Test-App.ps1 -Fix` uitvoeren
2. Alle checks groen (exit 0)?  
   → Pas dan committen
3. Voor volledige coverage (inclusief API + Blazor smoke tests):  
   → Eerst `.\scripts\dev\Start-Debug.ps1` (dit wacht zelf tot de services klaar zijn),
   daarna `.\scripts\dev\Test-App.ps1`

### Exit codes

- `0` — alles in orde
- `1` — fouten gevonden (of niet automatisch te herstellen)

### Schema-drift detectie (#684)

De verwachte kolommen per tabel staan in `$expectedColumns` in `Test-App.ps1`, maar de **bron
van waarheid** is `Database/dbo/Tables/<Tabel>.sql`. Het script parseert die bestanden en
faalt wanneer een schema-bestand een kolom declareert die niet in `$expectedColumns` staat.

Waarom dit nodig was: zonder deze check meldde `Test-App.ps1` groen terwijl acht kolommen
(`ClubCode` op `Velden`, plus `UseRealtimeApi`, `SyncEnabled` en de vijf `Theme*`-kolommen op
`AppSettings`) al lang in het schema stonden maar nooit werden gecontroleerd.

De `-Fix`-paden lezen de kolomdefinitie nu óók uit het schema-bestand in plaats van uit een
eigen lijst met typen. Daardoor kan `-Fix` niet meer afwijken van het schema — dat ging eerder
mis met `DEFAULT GETDATE()` waar het schema `GETUTCDATE()` voorschrijft.

`-Fix` weigert een kolom toe te voegen die `IDENTITY`/`PRIMARY KEY` is, of `NOT NULL` zonder
`DEFAULT`: dat kan niet veilig via `ALTER TABLE ADD` op een tabel met bestaande rijen.
Publiceer in dat geval het DB-project.

### CI-guards op het PostDeployment-script (#595, #734, #739)

De deploy publiceert **geen dacpac**: alleen `Database/Script.PostDeployment1.sql` draait tegen de
database. Alles wat het DB-project definieert maar niet in dat script staat, ontbreekt dus op een
verse clubinstallatie. Drie guards in [.github/workflows/build.yml](../.github/workflows/build.yml)
houden dat tegen, en één in [.github/workflows/deploy.yml](../.github/workflows/deploy.yml):

| Guard | Wat het controleert | Waarom |
|---|---|---|
| **Schema-drift check** — tabellen | Elke tabel uit het DB-project heeft een echte `CREATE TABLE` in het PostDeployment-script | De check accepteerde eerder élke vermelding, ook een `INSERT`. Daardoor passeerde `dbo.KnvbKalenderDag` met acht INSERT-blokken en nul CREATE's (#738); zeven andere tabellen zaten in hetzelfde geval, waaronder `dbo.AppSettings` |
| **Schema-drift check** — kolommen | Elke kolom uit het DB-project komt voor in het PostDeployment-script | Een kolom die alleen aan het DB-project wordt toegevoegd, kwam nooit in productie. Zo ontbraken `KnvbPdfBijlageIngeschakeld` en `KnvbStandaardRegio` terwijl de Instellingen-pagina ze onvoorwaardelijk uitleest |
| **`PostDeployment op verse database`** | Voert het script twee keer uit tegen een lege SQL Server in een wegwerpcontainer, met `-b -V 11`, en controleert daarna dat 22 kernobjecten bestaan en gevuld zijn | Het enige wat een verse clubinstallatie écht bewijst. Tekstchecks vergelijken tekens; deze job voert de migratie uit. Kost niets: runner-container, geen Azure-resource, geen secret |
| **`PostDeployment op verse Postgres-database`** (#823) | Postgres-tegenhanger: past `Database.Postgres/migrations/` twee keer toe via `Database.Postgres.Cli`/`MigrationRunner` (#821) tegen een verse Postgres 16-`services:`-container, en controleert daarna kernobjecten, ClubCode-dekking, exact één ledger-rij (geen duplicaat na de tweede run) en dat geen enkele kolomnaam afwijkt van lowercase (ARCHITECTUUR-DATABASE-TIERS.md §3) | Bewijst dat het Postgres-migratiepad idempotent is, net als de SQL Server-tegenhanger. Gebruikt het native `services:`-blok i.p.v. een rauwe `docker run`: GitHub Actions regelt zelf container-lifecycle/health-check. Credentials zijn een vast, niet-geheim wegwerpwachtwoord — geen GitHub Secret, want `services:`-containers provisioneren vóór elke step (een in een step gegenereerd wachtwoord zou hier te laat zijn) en secrets falen sowieso op fork-PR's |
| **`db-migrate` in deploy.yml** | `arguments: '-b -V 11'` op `azure/sql-action` | Zonder die vlaggen geeft sqlcmd exitcode 0 bij fouten van severity 16 en meldt de action "Successfully executed". Bij twee releases stonden er zo tien echte fouten in het log terwijl de job groen was (#739) |

**Uitzonderingen in de allowlist**, met reden: `stg.*` (dynamisch aangemaakt door
`FunctionApp/CreateTable.cs` uit het actuele Sportlink API-schema), `his.*` (dynamisch door
`sp_CreateTargetTableFromSource` uit `stg.*`) en `dbo.DateTable` (DROP + CREATE door
`sp_CreateDateTable`). Voor deze tabellen zou een statische DDL juist gaan driften.

**Gevolg voor het script:** alles wat `his.*` aanraakt staat achter een
`IF OBJECT_ID('his.…') IS NOT NULL`-guard, en de vier views die op `his.*` of `dbo.DateTable`
leunen worden via dynamische SQL aangemaakt. Op een database waar de eerste sync nog niet gelopen
heeft, worden die blokken netjes overgeslagen; de volgende deploy maakt ze aan.

### BlazorAdmin-build wordt overgeslagen bij een draaiende dev server

Draait er iets op poort 5242, dan slaat sectie 3 de BlazorAdmin-build over. Een tweede
compilatiepas naast `dotnet watch` levert een tweede set content-hash fingerprints op, wat
een 404 geeft op framework-JS ("An unhandled error has occurred. Reload"). Voor
build-foutdetectie: eerst `Stop-Debug.ps1`, dan `Test-App.ps1`.

---

## Start-Debug.ps1

Start Azurite, FunctionApp en BlazorAdmin, en **wacht tot ze daadwerkelijk reageren** —
geen vaste `Start-Sleep` meer (#684).

```powershell
.\scripts\dev\Start-Debug.ps1            # losse vensters per service
.\scripts\dev\Start-Debug.ps1 -Tail      # één samengevoegde logstroom
.\scripts\dev\Start-Debug.ps1 -Swa       # inclusief SWA emulator op :4280
.\scripts\dev\Start-Debug.ps1 -NoWatch   # BlazorAdmin zonder hot reload
.\scripts\dev\Start-Debug.ps1 -Clean     # dotnet clean BlazorAdmin vóór het starten
```

Readiness-detectie:

| Service | Signaal | Timeout |
|---|---|---|
| Azurite | poort 10000 luistert | 30s |
| FunctionApp | `GET /api/health` → 200 | 120s (koude start ~20s, #175) |
| BlazorAdmin | `GET /` → status < 500 | 180s |
| SWA emulator | `GET /` → status < 500 | 90s |

Het script rapporteert de gemeten opstarttijd en het versienummer uit `/api/health`, en geeft
**exit 1** wanneer een service niet opkomt. Poorten: Azurite :10000, FunctionApp :7094,
BlazorAdmin :5242, SWA :4280.

> HTTP 200 op een Blazor-route bewijst **niet** dat de app rendert — elke route levert
> dezelfde `index.html`. De browsercheck blijft verplicht.

---

## Stop-Debug.ps1

```powershell
.\scripts\dev\Stop-Debug.ps1           # FunctionApp + BlazorAdmin + SWA (Azurite blijft draaien)
.\scripts\dev\Stop-Debug.ps1 -All      # inclusief Azurite
.\scripts\dev\Stop-Debug.ps1 -Clean    # stop + dotnet clean BlazorAdmin (ruimt fingerprints op)
```

Stopt hele process-trees, niet losse processen. Dat is nodig omdat `dotnet watch` zijn
kindproces herstart zodra dat wegvalt: alleen de poort-eigenaar killen liet de watcher leven,
die poort 5242 daarna opnieuw bond. Het script wacht tot de poorten echt vrij zijn en geeft
exit 1 als dat niet lukt. Idempotent: draaien zonder actieve services doet niets.

Gedeelde logica staat in `scripts/dev/DevServices.psm1`, cross-platform sinds #800:

| Functie | Doel |
|---|---|
| `Get-DebugTempDir` | Tijdelijke map via `[System.IO.Path]::GetTempPath()` — nooit `$env:TEMP`, dat bestaat niet op macOS |
| `Get-DebugPidFile`, `Get-DebugPorts` | Pad naar het PID-bestand resp. de vaste poorttoewijzing (Azurite/FunctionApp/BlazorAdmin/SWA) |
| `Test-PortListening` | Luistert er iets op een poort — via de .NET BCL (`IPGlobalProperties`), niet via `Get-NetTCPConnection` (alleen Windows) |
| `Get-PortOwner`, `Get-PortOwnerId` | PID/proces dat op een poort luistert — Windows via `Get-NetTCPConnection`, macOS via `lsof` |
| `Get-ParentProcessId`, `Get-ChildProcessId` | Proceshiërarchie opvragen — Windows via CIM (`Win32_Process`), macOS via `ps` |
| `Get-ProcessTree`, `Stop-ProcessTree` | Een proces plus al zijn nakomelingen opsommen resp. stoppen (leaf-first, nodig omdat `dotnet watch` zijn kind herstart) |
| `Wait-ForPort`, `Wait-ForHealth`, `Wait-ForHttp` | Readiness-polling (poort/health-endpoint/HTTP-status) |
| `Stop-DebugServices` | Volledige teardown — combineert het PID-bestand met een fallback op poort-eigenaren |

---

## Test-PostgresTier.ps1 — zelftest van een databasetier (#851)

`scripts/dev/Test-PostgresTier.ps1` bewijst dat een **tier** werkt, niet dat de onderdelen
compileren. Het zet een wegwerpdatabase op, rolt het schema uit, laadt de demodata, start de
applicatie en controleert per stap of het resultaat klopt.

```powershell
./scripts/dev/Test-PostgresTier.ps1 -ListPhases                        # toont de poorten, raakt niets aan
./scripts/dev/Test-PostgresTier.ps1 -Tier SqlServer -Mode Baseline     # basismeting van de bestaande tier
./scripts/dev/Test-PostgresTier.ps1 -Tier Postgres  -Mode Verify       # meet de nieuwe tier
./scripts/dev/Test-PostgresTier.ps1 -Teardown                          # alleen opruimen
```

### Waarin dit verschilt van Test-App.ps1

`Test-App.ps1` controleert of de ontwikkelomgeving gezond is. Deze zelftest controleert of een
*omzetting* geslaagd is, en hanteert daarom drie strengere regels:

| Regel | Waarom |
|---|---|
| **Overslaan is falen** | `Test-App.ps1` slaat secties over als een poort niet luistert en meldt daarna "alles in orde". Een niet-uitgevoerde meting is hier rood |
| **Nul asserties is falen** | Anders is "niets gemeten" niet te onderscheiden van "alles goed" |
| **De routelijst wordt geverifieerd** | De verwachtingen worden bij elke run vergeleken met de `@page`-directives in de broncode. Een verschil in beide richtingen is een fout |

Die laatste regel lost een bestaand probleem op: `Test-App.ps1` test vandaag `/veldbeschikbaarheid`
en `/uitgesloten-emails`. Die routes bestaan niet meer, maar omdat Blazor WebAssembly op élke
route dezelfde pagina met statuscode 200 teruggeeft, staan ze al maanden op groen.

### G5/G6 starten een echte functiehost (#909)

Vanaf de Postgres-tier start de zelftest `FunctionApp.Postgres` zelf op en meet daar tegenaan:

- **G5** — bewijst dat die host met de bedóelde databaseserver praat. Drie onafhankelijke
  bewijzen: de tier-herkomst uit `/api/health` (build-time metadata, #863), de serverversie die de
  applicatie meldt tegenover wat de container zelf op `SHOW server_version` antwoordt, en — het
  enige bewijs dat niet uit de applicatie komt — een verbinding met
  `application_name = 'SportlinkFunctionAppPostgres'` in `pg_stat_activity`. G5 controleert
  bovendien dat **geen enkele functie in foutstatus staat**: een indexeringsfout maakt de host niet
  onbereikbaar, dus zonder die controle blijft zo'n functie onzichtbaar kapot.
- **G6** — roept de endpoints uit `selftest-expectations.psd1` aan en toetst **inhoud**, niet de
  statuscode. Staat een endpoint in dat bestand zonder geïmplementeerde assertie in het script, dan
  is dat een fout — geen stilzwijgende overslag.

**Wat je nodig hebt:** Docker (wegwerp-Postgres én, als er nog geen draait, een wegwerp-Azurite) en
Azure Functions Core Tools v4. Er is **geen** `local.settings.json` nodig: de host wordt volledig
via omgevingsvariabelen geconfigureerd en het bestand van de ontwikkelaar wordt niet aangeraakt.

**Je dev-omgeving mag blijven draaien.** De functiehost van de zelftest luistert op **7098**, niet
op 7094. Alleen de browsersweep (G7/G8, de skill) heeft 7094 nodig, omdat BlazorAdmin die URL
hardcodeert. De teardown stopt de functiehost op PID — nooit op poort — en ruimt een zelf gestarte
Azurite op; een Azurite die er al stond blijft staan.

**In Baseline-modus (SQL Server) melden G5/G6 zich als `blocked`, met opzet:** een volledige
functiehost tegen de levende ontwikkeldatabase kan die database wijzigen (achtergrondtaken lopen bij
het opstarten alsnog als hun moment al verstreken is), terwijl G4 de basismeting juist bewust
alleen-lezen houdt.

### Exitcodes

| Code | Betekenis |
|---|---|
| 0 | Alle uitgevoerde poorten geslaagd |
| 1 | Een poort gefaald |
| 2 | De implementatieboom van deze tier bestaat nog niet — een geplande situatie, geen defect |

Code 2 is er bewust: een verificatiescript moet "nog niet gebouwd" anders kunnen behandelen dan
"kapot". `scripts/ci/resolve-database-tier.sh` gebruikt dezelfde codes en leest dezelfde
tier-tabel (`scripts/ci/database-tiers.json`).

### Tier-tabel: shell en PowerShell moeten hetzelfde antwoord geven

`scripts/ci/database-tiers.json` heeft precies één lezer aan de CI-kant
(`scripts/ci/resolve-database-tier.sh`, bash) en één lezer aan de lokale-devkant
(`Get-DatabaseTierProject` in `scripts/dev/DevServices.psm1`, PowerShell) — nooit een derde,
losse vertaling ergens anders (#816/#865).

`scripts/ci/Test-TierMappingConsistency.ps1` bewijst dat die twee lezers voor elke tier in de
tabel, plus een bewust onbekende naam, exact dezelfde uitkomst geven (gevonden/niet-gevonden,
gebouwd/nog-niet-gebouwd, hetzelfde csproj-pad). Draait zonder database en zonder secrets, en zit
als stap in `.github/workflows/build.yml` (job "Build FunctionApp + BlazorAdmin").

**Lokaal op Windows, als `bash` naar WSL wijst:** het script detecteert dit automatisch (`bash`
resolvend naar `System32\bash.exe` i.p.v. Git Bash) en compenseert twee WSL-eigenaardigheden die
tijdens het bouwen van deze test zijn ontdekt — geen bug in de repo zelf, maar in hoe WSL vanuit
PowerShell wordt aangeroepen:
- WSL forwardt Windows-omgevingsvariabelen alleen als ze in `WSLENV` staan.
- WSL's launcher vertaalt een los meegegeven Windows-pad (`C:\...` of `C:/...`) niet automatisch
  naar `/mnt/c/...` — zonder vertaling faalt elke aanroep met exitcode 127.

### G2-G4 zijn nu echte metingen (#860, vervolg op #851)

Tot deze ronde stonden G2 (schema, eerste run), G3 (idempotentie, tweede run) en G4 (demodata en
rijtellingen) allemaal op `blocked`, in afwachting van de applicatie-datalaag (#860). Die datalaag
bestaat inmiddels (deels) — G2-G4 zijn daarom nu echte metingen:

- **G2/G3** draaien `Database.Postgres.Cli` tweemaal tegen de wegwerpcontainer en controleren de
  kernobjecten en het aantal rijen in `public.schema_migrations` — zelfde asserties als de
  CI-job `fresh-db-postgres`, hier lokaal herhaalbaar.
- **G4** seedt de AllStars-demodata (dezelfde volgorde als die CI-job: bronrij voor de primaire
  club → `006_allstars_demodata.sql` → de gesimuleerde `his.teams`/`his.matches`-DDL →
  `003-seed-allstars-demo-matches-postgres.sql`, tweemaal) en toetst de rijtellingen **altijd**
  tegen het contract in `selftest-expectations.psd1` — niet tegen een `-BaselinePath`-meting. Een
  levende ontwikkeldatabase (Baseline, SQL Server) mag van dat contract afwijken zonder dat de run
  faalt: dat IS precies waarom een deel van de rijen in het contract `Min` is in plaats van
  `Exact` (speeltijden/teamregels hopen zich op door jarenlang handmatig testen). Baseline-metingen
  worden daarom altijd als geslaagd vastgelegd, met een informatieve notitie als ze van het
  contract afwijken.
- **G5/G6 blijven bewust `blocked`** — vereisen een daadwerkelijk draaiende functiehost
  (`func start`), een aanzienlijk grotere en risicovollere stap dan G2-G4. Zie issue #909 voor de
  vervolgopgave en de reden waarom dit geen kleine aanvulling op G2-G4 is.

**Bijkomende fix tijdens deze ronde:** `Wait-ForPostgres` kon "gereed" melden (via `pg_isready -d`)
vlak vóórdat de server daadwerkelijk queries accepteerde — empirisch aangetroffen als "gereed na 3
pogingen" gevolgd door `FATAL: the database system is starting up` op de eerstvolgende échte query.
De functie doet nu, ná een geslaagde `pg_isready`, ook een `SELECT 1` als de `postgres`-OS-gebruiker
(peer-auth via het Unix-socket, geen wachtwoord nodig) en blijft pollen tot die ook slaagt.

### Wat het script niet doet

De browsersweep over alle beheerpagina's en de schrijfpaden door de GUI zitten in de skill
`.claude/skills/zelftest/SKILL.md`. Een client-side gerenderde pagina is niet met een HTTP-aanroep
te beoordelen — daar is een echte browser voor nodig. Het script schrijft per run een opdracht weg
(`artifacts/selftest/<run>/skill-opdracht.json`) zodat de skill niets zelf hoeft te verzinnen.

Alle verwachtingen — routes, asserties, rijaantallen, schrijfrondes — staan in één bestand:
`scripts/dev/selftest-expectations.psd1`. Voeg je een pagina toe, zet hem daar dan mét een
inhoudelijke assertie in; "geen foutmelding" volstaat niet, want een lege pagina geeft die ook niet.

### Isolatie

De zelftest raakt de ontwikkelomgeving niet: eigen compose-projectnaam, eigen containernaam, eigen
poort, geen opslagvolume. De ontwikkeldatabase wordt hooguit **gestopt** (nooit verwijderd) en aan
het eind exact teruggezet in de staat van vóór de run — stond hij uit, dan blijft hij uit. Het
opruimen gebeurt in een `finally`-blok en draait dus ook na een fout of onderbreking.

De wegwerpdatabase draait bewust op Europese tijd en niet op UTC: een tijdzonefout in de
tijdstempels valt op een UTC-server samen met correct gedrag en is dan onmeetbaar.

Bewijsmateriaal komt in `artifacts/selftest/<tijdstempel>/` en staat in `.gitignore`.

---

## Database.Postgres.Tests — integratietests, env-gestuurd (#866)

De integratietests in `Database.Postgres.Tests` (`PostgresMergeOrchestratorIntegrationTests`,
`MigrationRunnerIntegrationTests`, `PostgresPlannerViewIntegrationTests`,
`TeambegeleidingImporterIntegrationTests`, `PostgresAuditTimestampUtcIntegrationTests`) gebruiken
`[PostgresFact]`/`[PostgresTheory]` (`PostgresIntegrationTestAttributes.cs`) in plaats van een
onvoorwaardelijke `[Fact(Skip=...)]`. Die twee attributen zetten `Skip` alleen als
`POSTGRES_TEST_CONNECTION_STRING` ontbreekt — zonder die variabele slaan de tests zichzelf
**zichtbaar** over (de reden staat in de testuitvoer), met de variabele draaien ze onveranderd,
zonder enige codewijziging.

**Lokaal, tegen een eigen wegwerpcontainer:**

```powershell
docker run -d --name pg866 -e POSTGRES_PASSWORD=devonly -e POSTGRES_DB=sportlink_test -p 5432:5432 postgres:16
$env:POSTGRES_TEST_CONNECTION_STRING = "Host=localhost;Port=5432;Username=postgres;Password=devonly;Database=sportlink_test"
dotnet test Database.Postgres.Tests --filter FullyQualifiedName~IntegrationTests
docker rm -f pg866
```

**In CI:** de job "PostDeployment op verse Postgres-database" (`fresh-db-postgres` in
`.github/workflows/build.yml`) zet dezelfde variabele en draait het volledige
`Database.Postgres.Tests`-project na de migratiestap, tegen de Postgres-instantie die de job zelf al
opzet — geen aparte container nodig. Elke integratietestklasse dropt en hermaakt zijn eigen
afhankelijkheden onvoorwaardelijk in `InitializeAsync`, dus de reeds door de migratie aangemaakte
tabellen (`public.appsettings` e.a.) vormen daarbij geen belemmering.

Testparallellisme staat projectbreed uit (`Database.Postgres.Tests/AssemblyInfo.cs`, #854) — de
tests delen een live databaseverbinding en racen anders op `CREATE SCHEMA IF NOT EXISTS`.

---

## Achtergrond: schema-drift

Het project gebruikt **SSDT** (SQL Server Database Project) voor declaratief schemabeheer.
De `.sql` bestanden in `Database/dbo/Tables/` definiëren de _target state_.

Lokaal wordt de live database **niet automatisch geüpdatet** bij een git pull.
`Test-App.ps1 -Fix` vervangt dit voor lokale ontwikkeling door:
- Ontbrekende tabellen aanmaken vanuit de `.sql` bestanden
- Ontbrekende kolommen toevoegen via `ALTER TABLE`

Voor productie-deploys: gebruik de SSDT publish-diff workflow of een migratiescript.

---

## CI: schema-drift guard (`.github/workflows/build.yml`)

> Toegevoegd bij #595/#599.

De deploy-pipeline publiceert **geen dacpac** — de `db-migrate`-job runt uitsluitend
`Database/Script.PostDeployment1.sql`. Elk object uit het DB-project moet daarom óók idempotent in
dat script staan, anders ontbreekt het op een verse deploy en falen functies met
`Invalid object name`.

Dat ging structureel mis: **12 objecten** stonden alleen in het DB-project — waaronder alle vier de
ETL-procedures (`sp_CreateTargetTableFromSource`, `sp_MergeStgToHis`, `sp_UpdateSeasonTable`,
`sp_CreateDateTable`), de stuurtabel `mta.source_target_mapping` (inclusief de seed-rijen, die alleen
als SQL-comment bestonden), `dbo.Season`, de drie `pub`-views, `avg.ImportLog`,
`planner.HerplanVerzoeken` en `dbo.Zonsondergang`.

De job `build` controleert nu op elke PR dat elke `CREATE TABLE` / `CREATE PROCEDURE` / `CREATE VIEW`
uit `Database/**` voorkomt in het PostDeployment-script. Voegt iemand een object toe zonder guard,
dan faalt de PR.

**Bewuste uitzonderingen** (allowlist in de workflow, met reden):

| Object | Waarom uitgezonderd |
|---|---|
| `stg.teams`, `stg.matches`, `stg.matchdetails` | Dynamisch aangemaakt door `FunctionApp/CreateTable.cs` op basis van het actuele Sportlink API-schema |
| `dbo.DateTable` | Aangemaakt (DROP + CREATE) door `dbo.sp_CreateDateTable`, die zelf wél in PostDeployment staat |

**Bij een nieuw database-object:** voeg een `IF NOT EXISTS ... CREATE TABLE`-guard toe voor tabellen,
of `CREATE OR ALTER` voor procedures en views. Houd de definitie gelijk aan het bronbestand onder
`Database/`.

> **Nog open:** een echte `SqlPackage /Action:Publish` in de pipeline zou dit dubbel onderhoud
> overbodig maken. Dat is bewust niet in deze wijziging meegenomen — een schema-publish tegen de
> live Free-tier database kan destructieve ALTER/DROP-operaties uitvoeren en vraagt een expliciete
> afweging van de eigenaar. Zie #595.

### Lokaal narekenen

Tegen de lokale Docker-container (identiek op Windows en macOS, zie DEVELOPER-SETUP.md sectie
4.1) met een database die al meerdere clubs bevat. Voer het PostDeployment-script twee keer uit —
idempotentie-fouten worden pas bij de tweede run zichtbaar (#564):

```powershell
$env:SQLCMDPASSWORD = '<zelfde-wachtwoord-als-MSSQL_SA_PASSWORD>'
sqlcmd -S localhost,1433 -U sa -d SportlinkSqlDb -C -i Database/Script.PostDeployment1.sql -b
sqlcmd -S localhost,1433 -U sa -d SportlinkSqlDb -C -i Database/Script.PostDeployment1.sql -b
```

Alleen parsen is niet genoeg: batch-binding-fouten (Msg 207/1911) en `Msg 512` verschijnen pas bij
echte uitvoering.
