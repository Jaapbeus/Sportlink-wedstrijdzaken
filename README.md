# Sportlink Wedstrijdzaken

> **Open-source automation layer for Dutch football clubs using Sportlink Club (KNVB).**  
> Automatisering voor voetbalverenigingen die genoeg hebben van handmatig werk in Sportlink.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Security](https://img.shields.io/badge/AVG%2FGDPR-compliant-green.svg)](SECURITY.md)
[![Platform](https://img.shields.io/badge/platform-Azure%20Functions%20%7C%20Blazor-0078d4.svg)](https://azure.microsoft.com)

---

## Het probleem

Sportlink is het dominante ledenbeheer- en wedstrijdplatform voor Nederlandse voetbalverenigingen (KNVB-aangesloten). Het werkt — maar het werkt traag, omslachtig en biedt nauwelijks automatisering. Voor een kleine club met vijf teams valt dat mee. Voor een grote vereniging met dertig teams of meer wordt het een wekelijks gevecht.

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

### 3 — Admin GUI (in ontwikkeling)
Een Blazor WebAssembly-applicatie geeft beheerders een overzicht van:
- Instellingen (API-verbinding, e-mailaccounts, herplan-deadlines)
- E-mailtemplates beheren
- Voorkeurstijden per team instellen
- Verwerkte e-mails inzien (AVG-conform: geen berichtteksten)

---

## Architectuur op één pagina

```
Sportlink Club API
        │  (nachtelijke sync via timer trigger)
        ▼
Azure Functions (.NET 10, isolated worker)
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

**Technologie:** .NET 10 · Azure Functions v4 · Blazor WebAssembly · Azure SQL · Microsoft Graph API · Azure OpenAI · Azure Static Web Apps · Entra ID

---

## Voor wie is dit interessant?

**Als bijdrager** ben je welkom als je ervaring hebt met een of meerdere van deze gebieden:
- C# / .NET (backend logic, Azure Functions)
- Blazor WebAssembly (admin GUI)
- SQL Server (stored procedures, schema-ontwerp)
- Azure (Functions, Static Web Apps, Entra ID, Graph API)
- Nederlandse voetbalwereld (domeinkennis om de juiste problemen op te lossen)

**Als eindgebruiker** is dit project bedoeld voor verenigingen die:
- Draaien op **Sportlink Club** (KNVB-aangesloten)
- Meer dan ~10 teams hebben en daardoor veel handmatig werk in wedstrijdplanning
- Bereid zijn een Azure-omgeving in te richten (kosten: enkele euro's per maand)

Het project is **multi-club**: één installatie kan meerdere verenigingen bedienen, elk met hun eigen Sportlink-koppeling en isolatie van elkaars data.

## Open source

Dit project is volledig open source (MIT-licentie). Alle code, configuratie en documentatie zijn vrij beschikbaar. Clubs, ontwikkelaars en andere Sportlink-gebruikers zijn uitgenodigd om mee te bouwen, issues te melden of het project te forken voor hun eigen omgeving.

**Huidige stand:** in actief gebruik bij VV VRC (Ridderkerk). Ontworpen om schaalbaar te zijn naar meerdere verenigingen.

---

## Lokaal aan de slag

**Vereisten:**
- .NET 10.0 SDK
- Azure Functions Core Tools v4
- Azurite (lokale Azure Storage emulator)
- SQL Server met database `SportlinkSqlDb`

**Bouwen en draaien:**
```bash
# Kopieer de settings-template en vul je verbindingsgegevens in
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json

# Build
dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug

# Starten (vereist Azurite actief)
cd FunctionApp && func start --port 7094

# Handmatige sync triggeren
# GET http://localhost:7094/api/sync?weekOffsetFrom=-1&weekOffsetTo=2
```

**Git hooks activeren** (verplicht — blokkeert secrets en persoonsgegevens bij commit):
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Zie [SECURITY.md](SECURITY.md) voor het volledige beveiligingsprotocol.

---

## AVG / Privacy

Deze applicatie verwerkt persoonsgegevens van clubleden (namen, e-mailadressen, telefoonnummers van teamleiders en trainers). Dit zijn bijzondere gegevens onder de AVG/GDPR.

Het project is zo gebouwd dat:
- Persoonsgegevens **nooit** in git belanden (meerdere onafhankelijke beveiligingslagen)
- E-mailadressen van leden uitsluitend via **BCC** worden gebruikt bij communicatie met derden
- De `avg`-database-schema is gescheiden van operationele data en bedoeld voor beperkte toegang
- Alle automatische beveiligingschecks geblokkeerd bij een merge als er een risico gedetecteerd wordt

Zie [SECURITY.md](SECURITY.md) voor de volledige beveiligingsarchitectuur en verantwoorde omgang met persoonsgegevens.

---

## Bijdragen

Pull requests zijn welkom. Kijk voor openstaand werk naar de [GitHub Issues](../../issues) — issues gelabeld `fase: N` zijn onderdeel van de geplande v2.0-roadmap.

Heb je een club die baat zou hebben bij deze oplossing, of wil je meedenken over de richting? Open een [Discussion](../../discussions) of stuur een issue.

---

## Licentie

Zie [LICENSE](LICENSE) voor de licentievoorwaarden.
