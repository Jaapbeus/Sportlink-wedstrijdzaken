# Sportlink Wedstrijdzaken

> **Automatisering voor voetbalverenigingen die genoeg hebben van handmatig werk in Sportlink.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Security](https://img.shields.io/badge/AVG%2FGDPR-compliant-green.svg)](SECURITY.md)
[![Platform](https://img.shields.io/badge/platform-Azure%20Functions%20%7C%20Blazor-0078d4.svg)](https://azure.microsoft.com)
[![Changelog](https://img.shields.io/badge/changelog-CHANGELOG.md-informational)](CHANGELOG.md)

---

## Het probleem

Sportlink is het dominante ledenbeheer- en wedstrijdplatform voor Nederlandse voetbalverenigingen. Het werkt — maar het werkt traag, omslachtig en biedt nauwelijks automatisering. Voor een kleine club met vijf teams valt dat mee. Voor een grote vereniging met dertig teams of meer wordt het een wekelijks gevecht.

**Herkenbare pijnpunten:**

- Een tegenstander vraagt een wedstrijd te verzetten. Jij moet handmatig de juiste leider en trainer opzoeken, een e-mail opstellen, en wachten op goedkeuring — terwijl het veld al geboekt is en de spelersbus al gepland staat.
- Sportlink heeft nauwelijks een API die je zelf kunt aansturen. Nieuwe functies wachten jarenlang in de wachtrij.
- Wijzigingen worden niet automatisch gecommuniceerd naar betrokkenen. Iemand moet altijd iets doosturen.

Dit project bouwt die automatiseringslaag zelf.

---

## Wat deze applicatie doet

Een serverless pipeline die Sportlink-data synchroniseert, verwerkt en omzet in acties:

### 1 — Wedstrijddata automatisch ophalen
Elke nacht haalt een Azure Function alle wedstrijden, teams en details op via de Sportlink Club API. De data wordt opgeslagen in een lokale SQL Server — zodat je er zelf query's op kunt draaien, rapporten van kunt bouwen, of koppelen aan andere systemen.

### 2 — AI-gestuurde e-mailverwerking
Binnenkomende e-mails over wedstrijdwijzigingen (verplaatsverzoeken, afzeggingen) worden automatisch geclassificeerd via Azure OpenAI. Op basis van de classificatie stuurt de planner een standaardantwoord terug — met de leider en trainer van het betrokken team automatisch in BCC.

**Geen handmatig zoekwerk meer.** De juiste contactpersonen worden automatisch gevonden via de koppeling met de ledenexport.

### 3 — Admin GUI
Een Blazor WebAssembly-applicatie geeft beheerders via de browser volledig beheer over:
- **Instellingen** — API-verbinding, e-mailaccounts, herplan-deadlines, GPS-coördinaten, club-thema
- **E-mailtemplates** — AI-antwoordtemplates per berichttype; gedeelde e-mailvoetnoot
- **Voorkeurstijden** — per team gewenste speeltijden instellen
- **Speeltijden** — wedstrijdduur en veldfractie per leeftijdscategorie
- **Veldbeschikbaarheid** — tijdvensters per veld configureren
- **Velden** — velddefinities (type, verlichting, actief/inactief)
- **Teambegeleiding** — begeleiders per team raadplegen; contactverzoeken doorsturen
- **Leermomenten** — AI-classificatiefouten inzien en corrigeren voor betere toekomstige classificaties
- **E-mail tester** — AI-classificatie dry-run zonder e-mail te versturen
- **E-maillog** — verwerkte e-mails inzien (AVG-conform: geen berichtteksten)
- **Testmodus (ALLSTARS)** — fictieve wedstrijden invoeren om planner te testen zonder echte data

---

## Architectuur op één pagina

```
Sportlink Club API
        │  (nachtelijke sync via timer trigger)
        ▼
Azure Functions (.NET 9, isolated worker)
  ├── FetchAndStoreApiData    — haalt teams, wedstrijden en details op
  ├── EmailProcessorFunction  — leest mailbox via Microsoft Graph
  ├── BerichtAiService        — classifieert binnenkomende e-mails met AI
  └── Admin API               — REST endpoints voor de beheer-GUI
        │
        ▼
Azure SQL Server
  ├── stg.*   — staging (tijdelijk, elke run geleegd)
  ├── his.*   — history (persistent, met audit-timestamps)
  ├── pub.*   — public views (alleen-lezen voor consumers)
  ├── dbo.*   — configuratie (AppSettings, Speeltijden, Seizoen)
  └── avg.*   — AVG-beschermde data (teambegeleiding — toegang beperkt)
        │
        ▼
Azure Static Web Apps (gratis tier)
  └── Blazor WebAssembly Admin GUI
        └── Entra ID authenticatie (admin / user rollen)
```

**Technologie:** .NET 9 (FunctionApp) · .NET 10 (Blazor) · Azure Functions v4 · Blazor WebAssembly · Azure SQL · Microsoft Graph API · Azure OpenAI · Azure Static Web Apps · Entra ID

---

## Voor wie is dit interessant?

**Als bijdrager** ben je welkom als je ervaring hebt met een of meerdere van deze gebieden:
- C# / .NET (backend logic, Azure Functions)
- Blazor WebAssembly (admin GUI)
- SQL Server (stored procedures, schema-ontwerp)
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
| **Developers — opzet** | [Setup](docs/SETUP.md) · [Setup checklist](docs/SETUP-CHECKLIST.md) · [Lokaal debuggen](docs/LOKAAL-DEBUGGEN.md) · [Quick reference](docs/QUICK-REFERENCE.md) |
| **Developers — architectuur** | [API referentie](docs/API.md) · [Planner architectuur](docs/ARCHITECTURE-PLANNER.md) · [E-mailverwerking](docs/EMAIL-VERWERKING.md) |
| **Azure & auth** | [Entra auth & beheer](docs/ENTRA-AUTH-BEHEER.md) · [Versiebeheer](docs/VERSIONING.md) |
| **Kwaliteit & security** | [Verificatie-scripts](docs/VERIFICATIE-SCRIPTS.md) · [Security](SECURITY.md) |

**→ [Volledige inhoudsopgave: docs/INDEX.md](docs/INDEX.md)**

---

## Lokaal aan de slag

**Vereisten:** .NET 10.0 SDK + .NET 9 Runtime · Azure Functions Core Tools v4 · Azurite · SQL Server met `SportlinkSqlDb`

```powershell
# Settings-template kopiëren en verbindingsgegevens invullen
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json

# Alle services starten (Azurite + FunctionApp :7094 + BlazorAdmin :5242)
.\scripts\dev\Start-Debug.ps1

# Verificatie (wacht 15s na Start-Debug)
.\scripts\dev\Test-App.ps1
```

**Git hooks activeren** (verplicht — blokkeert secrets en persoonsgegevens bij commit):
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Volledige lokale setupbeschrijving: [SETUP.md § Lokale ontwikkelomgeving](SETUP.md#8-lokale-ontwikkelomgeving)  
Beveiligingsprotocol: [SECURITY.md](SECURITY.md)

---

## AVG / Privacy

Deze applicatie verwerkt persoonsgegevens van clubleden (namen, e-mailadressen, telefoonnummers van teamleiders en trainers). Dit zijn bijzondere gegevens onder de AVG/GDPR.

Het project is zo gebouwd dat:
- Persoonsgegevens **nooit** in git belanden (meerdere onafhankelijke beveiligingslagen)
- E-mailadressen van leden uitsluitend via **BCC** worden gebruikt bij communicatie met derden
- De `avg`-database-schema is gescheiden van operationele data en bedoeld voor beperkte toegang
- Alle automatische beveiligingschecks geblokkeerd bij een merge als er een risico gedetecteerd wordt

Zie [SECURITY.md](SECURITY.md) voor de volledige beveiligingsarchitectuur en verantwoorde omgang met persoonsgegevens.

Zie [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) voor de architectuurprincipes: tijdzones, multi-club isolatie, secrets en AVG.

---

## Releases en changelog

Alle noemenswaardige wijzigingen staan in [CHANGELOG.md](CHANGELOG.md).  
Releases zijn beschikbaar via [GitHub Releases](../../releases).

Versienummering volgt een vier-delig schema: `MAJOR.MINOR.PATCH.REVISION` (huidig: v2.7.0.x).  
Definitie van bug, feature en enhancement: zie [docs/VERSIONING.md](docs/VERSIONING.md).

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
| Azure SQL Database | Free tier (32 GB) | €0 |
| Azure Static Web Apps | Free | €0 |

---

## Bijdragen

Pull requests zijn welkom. Kijk voor openstaand werk naar de [GitHub Issues](../../issues).

Lees voor je begint: **[CONTRIBUTING.md](CONTRIBUTING.md)** — beschrijft de branch-strategie, commit-conventies, en Security Gate.

Heb je een club die baat zou hebben bij deze oplossing, of wil je meedenken over de richting? Open een [Discussion](../../discussions) of stuur een issue.

---

## Licentie

Zie [LICENSE](LICENSE) voor de licentievoorwaarden.
