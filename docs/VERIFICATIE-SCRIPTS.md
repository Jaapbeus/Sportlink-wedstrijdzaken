# Verificatie & zelfherstellende tests

## Test-App.ps1

`scripts/dev/scripts/dev/Test-App.ps1` is het centrale verificatie- en herstelscript.
Het controleert schema, build en runtime in één doorloop.

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
| 4. API smoke | 10 endpoints op `:7094` | nee (2xx verwacht) |
| 5. Blazor pagina's | 7 routes op `:5242` | nee (geen Blazor-foutindicatoren) |

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

Gedeelde logica staat in `scripts/dev/DevServices.psm1` (`Wait-ForPort`, `Wait-ForHealth`,
`Wait-ForHttp`, `Stop-DebugServices`).

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

```powershell
# Voer het PostDeployment-script twee keer uit tegen een database met >1 club.
# Twee keer, omdat idempotentie-fouten pas bij de tweede run zichtbaar worden (#564).
sqlcmd -S <server> -d SportlinkSqlDb -E -C -i Database\Script.PostDeployment1.sql -b
sqlcmd -S <server> -d SportlinkSqlDb -E -C -i Database\Script.PostDeployment1.sql -b
```

Alleen parsen is niet genoeg: batch-binding-fouten (Msg 207/1911) en `Msg 512` verschijnen pas bij
echte uitvoering.
