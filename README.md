# Sportlink Wedstrijdzaken

> **Automatisering voor voetbalverenigingen die genoeg hebben van handmatig werk in Sportlink.**

[![Security](https://img.shields.io/badge/AVG%2FGDPR-compliant-green.svg)](SECURITY.md)
[![Platform](https://img.shields.io/badge/platform-Azure%20Functions%20%7C%20Blazor-0078d4.svg)](https://azure.microsoft.com)
[![Changelog](https://img.shields.io/badge/changelog-CHANGELOG.md-informational)](CHANGELOG.md)

---

## Waar moet ik zijn?

| Ik ben… | Begin hier |
|---|---|
| **benieuwd wat dit is** | lees gewoon verder — twee minuten |
| **bestuurder of beheerder van een club die dit wil gaan gebruiken** | [Voor wie is dit interessant?](#voor-wie-is-dit-interessant) → [SETUP-NIEUWE-CLUB.md](SETUP-NIEUWE-CLUB.md) |
| **beheerder van een draaiende installatie** | [docs/BEHEERDER-HANDLEIDING.md](docs/BEHEERDER-HANDLEIDING.md) |
| **developer die wil bijdragen** | [CONTRIBUTING.md](CONTRIBUTING.md) → [docs/DEVELOPER-SETUP.md](docs/DEVELOPER-SETUP.md) |
| **AI-agent of nieuwe developer die de code moet begrijpen** | [CLAUDE.md](CLAUDE.md) — de harde architectuurregels, het kostenbeleid en de tierstrategie. [AGENTS.md](AGENTS.md) is dezelfde inhoud voor niet-Claude-agents en wordt uit `CLAUDE.md` gegenereerd. |
| **op zoek naar één specifiek document** | [docs/INDEX.md](docs/INDEX.md) |

---

## Het probleem

Sportlink is het dominante ledenbeheer- en wedstrijdplatform voor Nederlandse voetbalverenigingen. Het werkt — maar het werkt traag, omslachtig en biedt nauwelijks automatisering. Voor een kleine club met vijf teams valt dat mee. Voor een grote vereniging met dertig teams of meer wordt het een wekelijks gevecht.

**Herkenbare pijnpunten:**

- Een tegenstander vraagt een wedstrijd te verzetten. Jij moet handmatig de juiste leider en trainer opzoeken, een e-mail opstellen, en wachten op goedkeuring — terwijl het veld al geboekt is en de spelersbus al gepland staat.
- Sportlink heeft nauwelijks een API die je zelf kunt aansturen. Nieuwe functies wachten jarenlang in de wachtrij.
- Wijzigingen worden niet automatisch gecommuniceerd naar betrokkenen. Iemand moet altijd iets doorsturen.

Dit project bouwt die automatiseringslaag zelf.

---

## Wat deze applicatie doet

Een serverless pipeline die Sportlink-data synchroniseert, verwerkt en omzet in acties:

### 1 — Wedstrijddata automatisch ophalen
Elke nacht haalt een Azure Function alle wedstrijden, teams en details op via de Sportlink Club API. De data wordt opgeslagen in een database (Postgres of SQL Server — twee gelijkwaardige tiers, zie de architectuursectie hieronder) — zodat je er zelf query's op kunt draaien, rapporten van kunt bouwen, of koppelen aan andere systemen.

### 2 — AI-gestuurde e-mailverwerking
Binnenkomende e-mails over wedstrijdwijzigingen (verplaatsverzoeken, afzeggingen) worden automatisch geclassificeerd via OpenAI (gpt-4o-mini, direct via OpenAI API). Op basis van de classificatie stuurt de planner een standaardantwoord terug — met de leider en trainer van het betrokken team automatisch in BCC.

**Geen handmatig zoekwerk meer.** De juiste contactpersonen worden automatisch gevonden via de koppeling met de ledenexport.

### 3 — Admin GUI
Een Blazor WebAssembly-applicatie geeft beheerders via de browser volledig beheer over:
- **Instellingen** — API-verbinding, e-mailaccounts, herplan-deadlines, GPS-coördinaten, club-thema
- **E-mailtemplates** — AI-antwoordtemplates per berichttype; gedeelde e-mailvoetnoot
- **Voorkeurstijden** — per team gewenste speeltijden instellen
- **Speeltijden** — wedstrijdduur en veldfractie per leeftijdscategorie
- **Veldbeschikbaarheid** — tijdvensters per veld configureren
- **Velden** — velddefinities (type, verlichting, actief/inactief)
- **Dagplanning** — wedstrijden en velden per speeldag plannen
- **Teambegeleiding** — begeleiders per team raadplegen; contactverzoeken doorsturen
- **Leermomenten** — AI-classificatiefouten inzien en corrigeren voor betere toekomstige classificaties
- **Teamaliassen** — geleerde teamnaam-varianten goedkeuren of afwijzen
- **E-mail tester** — AI-classificatie dry-run zonder e-mail te versturen
- **E-maillog** — verwerkte e-mails inzien (AVG-conform: geen berichtteksten)
- **Testmodus (ALLSTARS)** — fictieve wedstrijden invoeren om planner te testen zonder echte data
- **Wijzigingsverzoeken** — inkomende Sportlink-wijzigingsverzoeken van tegenstanders goedkeuren of afwijzen
- **Oefenwedstrijd aanmaken** — een oefenwedstrijd rechtstreeks in Sportlink Club aanmaken
- **Thema** — clubkleuren, logo en favicon, met een aparte licht- en donkervariant en een schakelaar in de header
- **Sportlink Web Extension** (in opbouw) — kleedkamers en veld van een wedstrijd rechtstreeks vanuit Dagplanning naar Sportlink Club terugschrijven, met een deep-link "Open in Sportlink" per wedstrijd

---

## Architectuur op één pagina

```
Sportlink Club API
        │  (nachtelijke sync via timer trigger)
        ▼
Azure Functions (.NET 9, isolated worker) — één van twee volledig gescheiden tier-implementaties
  ├── FetchAndStoreApiData    — nachtelijke sync van teams, wedstrijden en details
  │                             (op de Postgres-tier: `PostgresFetchAndStoreApiData`)
  ├── EmailProcessorFunction  — leest mailbox via Microsoft Graph
  ├── BerichtAiService        — classifieert binnenkomende e-mails met AI
  ├── Sportlink Web Extension — schrijft wedstrijdwijzigingen terug naar Sportlink Club (in opbouw)
  └── Admin API               — REST endpoints voor de beheer-GUI
        │
        ▼
Postgres (via Supabase) of Azure SQL Server — twee gelijkwaardige tiers; je fork kiest er één
  ├── stg.*   — staging (tijdelijk, elke run geleegd)
  ├── his.*   — history (persistent, met audit-timestamps)
  ├── pub.*   — public views (alleen-lezen voor consumers)
  ├── public.* / dbo.* — configuratie (AppSettings, Speeltijden, Seizoen — naamgeving per tier)
  └── avg.*   — AVG-beschermde data (teambegeleiding — toegang beperkt)
        │
        ▼
Azure Static Web Apps (gratis tier)
  └── Blazor WebAssembly Admin GUI
        └── Entra ID authenticatie (admin / user / Wedstrijdzaken rollen)
```

Welke tier een fork daadwerkelijk gebruikt is een bewuste, expliciete keuze op build/deploytijd (repository-variabele `DatabaseTier`) — geen gedeelde runtime-abstractie. Zie [docs/ARCHITECTUUR-DATABASE-TIERS.md](docs/ARCHITECTUUR-DATABASE-TIERS.md).

**Technologie:** .NET 9 (FunctionApp) · .NET 10 (Blazor) · Azure Functions v4 · Blazor WebAssembly · Postgres (Supabase) / Azure SQL · Microsoft Graph API · OpenAI (gpt-4o-mini, direct) · Azure Static Web Apps · Entra ID

### Projectkaart

De repository bevat 13 .NET-projecten en één SQL Server-databaseproject (SSDT):

| Project | Rol |
|---|---|
| `FunctionApp.Postgres/` | Azure Functions, Postgres-tier (draait in productie, `net9.0`) |
| `FunctionApp/` | Azure Functions, SQL Server-tier (`fa-dev-sportlink-01.csproj`, `net9.0`) |
| `Planner.Shared/` | Tier-onafhankelijke domeinlogica: teamnaam-normalisatie, veldresolutie, planner-regels, thema (`Theming/ThemeCore.cs`), feedback en SSRF-bescherming. Nieuwe gedeelde logica hoort hier — nooit als tweede kopie in een tierboom. |
| `Database.Postgres/` | Postgres-schema, migraties (`migrations/`) en de checksum-bewaakte migratierunner |
| `Database.Postgres.Cli/` | CLI om die migraties toe te passen (gebruikt door CI en lokaal) |
| `Database/` | SQL Server-databaseproject (SSDT, `SportlinkSqlDb.sqlproj`) |
| `BlazorAdmin/` | Blazor WebAssembly Admin GUI (`net10.0`) |
| `MigrationTools/SqlServerToPostgresCopy/` | Eenmalige kopieertool SQL Server → Postgres |
| `Tools/SportlinkTokenCapture/` | Hulpprogramma voor het koppelen van een Sportlink-serviceaccount |
| `BlazorAdmin.Tests/`, `FunctionApp.Tests/`, `FunctionApp.Postgres.Tests/`, `Database.Postgres.Tests/`, `Planner.Shared.Tests/` | Unit- en integratietests |

Bouw altijd via `sportlink-wedstrijdzaken.slnf` of per project. De volledige `sportlink-wedstrijdzaken.sln` bevat het SSDT-project en bouwt daardoor niet op macOS.

---

## Voor wie is dit interessant?

**Als bijdrager** ben je welkom als je ervaring hebt met een of meerdere van deze gebieden:
- C# / .NET (backend logic, Azure Functions)
- Blazor WebAssembly (admin GUI)
- SQL — Postgres (migraties en schema-ontwerp) en SQL Server (stored procedures) voor de tweede tier
- Azure (Functions, Static Web Apps, Entra ID, Graph API)
- Nederlandse voetbalwereld (domeinkennis om de juiste problemen op te lossen)

**Als eindgebruiker** is dit project bedoeld voor verenigingen die:
- Draaien op Sportlink Club (KNVB-aangesloten) **en beschikken over een actief [Club Dataservice](https://www.sportlink.nl/producten/club-dataservice/)-abonnement**
- Meer dan ~10 teams hebben en daardoor veel handmatig werk in wedstrijdplanning
- Bereid zijn een Azure-omgeving in te richten (kosten: €0 — de volledige stack draait op Azure Free tiers)

---

## Documentatie

Alle documentatie staat in de [`docs/`](docs/) map, georganiseerd op doelgroep.

| Categorie | Documenten |
|---|---|
| **Beheerders** | [Beheerder handleiding](docs/BEHEERDER-HANDLEIDING.md) · [Testmodus ALLSTARS](docs/TESTMODUS-ALLSTARS.md) · [Teambegeleiding import](docs/ADMIN-TEAMBEGELEIDING-IMPORT.md) |
| **Developers — opzet** | [Nieuwe club opzetten](SETUP-NIEUWE-CLUB.md) · [Developer setup](docs/DEVELOPER-SETUP.md) · [Setup checklist](docs/SETUP-CHECKLIST.md) · [Lokaal debuggen](docs/LOKAAL-DEBUGGEN.md) · [Quick reference](docs/QUICK-REFERENCE.md) |
| **Developers — architectuur** | [Architectuurbeschrijving](docs/ARCHITECTUUR.md) · [API referentie](docs/API.md) · [Planner architectuur](docs/ARCHITECTUUR-PLANNER.md) · [E-mailverwerking](docs/EMAIL-VERWERKING.md) |
| **Azure & auth** | [Entra auth & beheer](docs/ENTRA-AUTH-BEHEER.md) · [Versiebeheer](docs/VERSIONING.md) |
| **Kwaliteit & security** | [Verificatie-scripts](docs/VERIFICATIE-SCRIPTS.md) · [Security](SECURITY.md) |

**→ [Volledige inhoudsopgave: docs/INDEX.md](docs/INDEX.md)**

---

## Lokaal aan de slag

**Vereisten:** .NET 10.0 SDK · .NET 9 Runtime — beide frameworks, `Microsoft.NETCore.App` én `Microsoft.AspNetCore.App` · Azure Functions Core Tools v4 · Azurite · Docker voor de lokale database (zie [docs/DEVELOPER-SETUP.md](docs/DEVELOPER-SETUP.md) §4)

```powershell
# 1. Lokale database starten — 'docker compose up -d' start Postgres, de tier die in
#    productie draait. SQL Server staat achter een profile:
#    docker compose --profile sqlserver up -d sqlserver
docker compose up -d

# 2. Settings-template kopiëren en POSTGRES_CONNECTION_STRING invullen
cp FunctionApp.Postgres/local.settings.template.json FunctionApp.Postgres/local.settings.json

# 3. Alle services starten (Azurite + FunctionApp :7094 + BlazorAdmin :5242)
.\scripts\dev\Start-Debug.ps1            # -Tier SqlServer voor de andere tier

# 4. Verificatie (Start-Debug wacht zelf tot de services klaar zijn)
.\scripts\dev\Test-App.ps1
```

**Git hooks activeren** (verplicht — blokkeert secrets en persoonsgegevens bij commit):
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Volledige lokale setupbeschrijving: [docs/DEVELOPER-SETUP.md](docs/DEVELOPER-SETUP.md)  
Beveiligingsprotocol: [SECURITY.md](SECURITY.md)

---

## AVG / Privacy

Deze applicatie verwerkt persoonsgegevens van clubleden (namen, e-mailadressen, telefoonnummers van teamleiders en trainers). Dit zijn gewone persoonsgegevens onder de AVG (artikel 4 lid 1) — deels van minderjarigen, wat extra zorgvuldigheid vraagt. Zie [SECURITY.md](SECURITY.md) voor de classificatie en de datalekprocedure.

Het project is zo gebouwd dat:
- Persoonsgegevens **nooit** in git belanden (meerdere onafhankelijke beveiligingslagen)
- E-mailadressen van leden uitsluitend via **BCC** worden gebruikt bij communicatie met derden
- De `avg`-database-schema is gescheiden van operationele data en bedoeld voor beperkte toegang
- Automatische beveiligingschecks blokkeren een merge zodra er een risico wordt gedetecteerd

Zie [SECURITY.md](SECURITY.md) voor de volledige beveiligingsarchitectuur en verantwoorde omgang met persoonsgegevens.

Zie [docs/ARCHITECTUUR.md](docs/ARCHITECTUUR.md) voor de volledige, leidende
architectuurbeschrijving — kwaliteitsdoelen, architectuurbesluiten, de concrete uitwerking per
onderwerp (tijdzones, multi-club isolatie, secrets, AVG, auth-lagen) en het toetsregister waarin
per regel staat welke externe standaard eraan ten grondslag ligt en hoe je hem controleert.

---

## Releases en changelog

Alle noemenswaardige wijzigingen staan in [CHANGELOG.md](CHANGELOG.md).  
Releases zijn beschikbaar via [GitHub Releases](../../releases).

Versienummering volgt een vier-delig schema: `MAJOR.MINOR.PATCH.REVISION`. Het actuele nummer
staat bovenaan [CHANGELOG.md](CHANGELOG.md) en op de [releases-pagina](../../releases); de Admin GUI
toont het in de header en `/api/health` in het veld `version`.  
Definitie van bug, feature en enhancement, en de twee versie-bumpfasen: zie [docs/VERSIONING.md](docs/VERSIONING.md).

---

## Jouw club aan de slag

Sportlink Wedstrijdzaken is ontworpen voor gebruik door meerdere clubs. Je forkt de repository, richt je eigen Azure-resources in, en configureert je eigen Entra ID-tenant — **er komen geen club-specifieke waarden in de code**.

Volg de stap-voor-stap installatiehandleiding: **[SETUP-NIEUWE-CLUB.md](SETUP-NIEUWE-CLUB.md)**

### Vereiste: Sportlink Club Dataservice

Deze applicatie haalt alle wedstrijddata op via de **[Sportlink Club Dataservice](https://www.sportlink.nl/producten/club-dataservice/)** — een betaald product van Sportlink. Zonder dit abonnement is er geen toegang tot de Sportlink API en is de applicatie niet bruikbaar.

De Club Dataservice wordt aangeboden in drie bundels met het Sportlink mediaplatform:

| Bundel | Kosten |
|---|---|
| Goed (app + tv + sponsoring) | €1,95/lid/jaar + €375 eenmalig |
| Beter (integratie bestaande website) | €2,70/lid/jaar + €375 eenmalig |
| Ideaal (nieuwe website + integratie) | €2,80/lid/jaar + €375 eenmalig |

Facturering is gemaximeerd op 800 leden. Neem contact op met jouw Sportlink-contactpersoon voor de actuele tarieven en beschikbaarheid.

Bij een actief abonnement ontvang je een `clientId` waarmee de applicatie de API aanroept. Dit `clientId` wordt per club geconfigureerd in de applicatie-instellingen — het staat nooit in de broncode.

### Wat je verder nodig hebt

| Resource | Tier | Kosten |
|---|---|---|
| Sportlink Club Dataservice | Betaald abonnement (zie hierboven) | Varieert |
| Microsoft 365 / Entra ID tenant | Gratis (inbegrepen bij M365) | €0 |
| Azure Functions | Consumption (1M requests/maand gratis) | €0 |
| Database | Postgres (bijv. Supabase free tier) of Azure SQL Database (free tier, 32 GB) — één bewuste keuze per fork | €0 |
| Azure Static Web Apps | Free | €0 |

---

## Bijdragen

Pull requests zijn welkom. Kijk voor openstaand werk naar de [GitHub Issues](../../issues).

Lees voor je begint: **[CONTRIBUTING.md](CONTRIBUTING.md)** — beschrijft de branch-strategie, commit-conventies, en Security Gate.

Heb je een club die baat zou hebben bij deze oplossing, of wil je meedenken over de richting? Open een [Discussion](../../discussions) of stuur een issue.

---

## Licentie

⚠️ Er is nog geen `LICENSE`-bestand in de repository — de badge en link hiernaartoe zijn daarom
verwijderd totdat dat is toegevoegd. Neem contact op met de eigenaar voor de beoogde licentie.
