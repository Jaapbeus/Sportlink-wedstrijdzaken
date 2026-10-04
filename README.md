# Sportlink Wedstrijdzaken

> **Minder klikken. Meer overzicht. Meer tijd voor voetbal.**

Herken je dat? Je wilt even de zaterdagplanning regelen, maar bent vooral bezig met schermen
openen, gegevens overtypen en mailtjes doorsturen. Sportlink Wedstrijdzaken brengt wedstrijdplanning,
veldbezetting en een deel van de Sportlink-acties samen in één webapp.

Je gebruikt de app naast Sportlink Club. Wedstrijdgegevens worden gesynchroniseerd naar je eigen
database; daar kun je mee plannen, vergelijken en delen. Voor acties die terugschrijven naar
Sportlink blijft de app afhankelijk van de Sportlink-verbinding.

[![Security](https://img.shields.io/badge/security-beleid-blue.svg)](SECURITY.md)
[![Platform](https://img.shields.io/badge/platform-.NET%2010%20%7C%20Blazor-0078d4.svg)](docs/DEVELOPER-SETUP.md)
[![Changelog](https://img.shields.io/badge/changelog-bekijk%20de%20wijzigingen-informational)](CHANGELOG.md)

## Wat heb je eraan?

### De zaterdag in één oogopslag

**Planning** laat de wedstrijden en veldbezetting zien in een tabel en een tijdlijn. Je ziet
welke teams spelen, op welk veld en wanneer. **Veld optimalisatie** berekent een voorstel op basis
van je velden, beschikbaarheid, speeltijden en teamvoorkeuren. Vergelijk dat met de bestaande
planning voordat je iets overneemt.

Met **Delen** maak je een HTML-overzicht voor anderen. PDF-export is ook beschikbaar als de
beheerder die heeft ingeschakeld en de licentievoorwaarden heeft gecontroleerd.

### Veelgebruikte Sportlink-acties bij de wedstrijd

Vanuit de webapp kun je wedstrijdinformatie openen, een veld of kleedkamers toewijzen en
scheidsrechters vastleggen. Ook oefenwedstrijden aanmaken en verwijderen is gebouwd. Welke actie
beschikbaar is, hangt af van je rol, de wedstrijd en de rechten van het gekoppelde Sportlink-account.

De koppeling begint in **dry-run**: de wijziging wordt gesimuleerd. Met een ingerichte koppeling en
dry-run uit kunnen deze acties echt naar Sportlink schrijven. Een verplicht wijzigingsverzoek voor
datum, tijd of accommodatie vraagt nog aandacht: de app heeft de validatiestap, maar de aparte
bevestigingsstap is nog niet gebouwd. Zie de [Sportlink-koppeling](docs/SPORTLINK-WEB-EXTENSION.md).

### Minder handwerk rond de mailbox

De optionele e-mailverwerking leest een Microsoft 365-mailbox, laat AI wedstrijdverzoeken
herkennen en bouwt antwoorden met je eigen templates en planningsgegevens. Teambegeleiding uit
een ledenexport helpt om de juiste contactpersoon te vinden.

Begin met **reviewmodus**: antwoorden gaan naar een ingestelde beoordelaar. De **E-mailtester**
laat je de classificatie proberen zonder berichten te versturen. AI kan zich vergissen; met
leermomenten en teamaliassen kun je herkenning bijsturen. Automatisch antwoorden aan afzenders is
een keuze die je zelf activeert na het testen.

### Je eigen club, je eigen inrichting

Stel velden, speeltijden en teamvoorkeuren in. Geef de app je clubkleuren en logo, kies licht of
donker en regel toegang via Microsoft Entra ID. Met de fictieve democlub **AllStars FC** kun je
Planning en Veld optimalisatie uitproberen op testwedstrijden.

## Past dit bij jouw club?

Dit project is interessant als je wedstrijdzaken doet én er iemand in de club is die graag met
GitHub, Docker en Azure werkt. Je hoeft niet meteen C# te schrijven, maar voor een eigen
installatie zijn technische inrichting en onderhoud wel nodig.

Voor echte wedstrijddata heb je toegang tot **Sportlink Club Dataservice** en een eigen
`clientId` nodig. Vraag je Sportlink-beheerder naar jullie abonnement en mogelijkheden. De
Sportlink Club-koppeling voor schrijfacties vraagt daarnaast een eigen inrichting.

Je host de applicatie zelf. Er is geen gedeelde aanmeldservice voor nieuwe clubs. Een lokaal
proefrondje met fictieve data kan zonder Azure-deployment of een echte Sportlink-synchronisatie.

## Eerst even rondkijken

| Je wilt… | Begin hier |
|---|---|
| Zien hoe een werkdag eruitziet | [Eerste rondje door de app](docs/BEHEERDER-HANDLEIDING.md#eerste-rondje-door-de-app) |
| Met fictieve wedstrijden spelen | [AllStars-testmodus](docs/TESTMODUS-ALLSTARS.md#een-eerste-proefrondje) |
| De app lokaal draaien | [Developer setup](docs/DEVELOPER-SETUP.md) — Postgres is de standaard |
| Een installatie voor je club maken | [Nieuwe club opzetten](SETUP-NIEUWE-CLUB.md) |
| Meehelpen of een idee aandragen | [Bijdragen](CONTRIBUTING.md) · [GitHub Issues](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues) |
| Een specifiek document vinden | [Documentatie-index](docs/INDEX.md) |

## Wat kost het?

Het project richt zich op lage gebruikskosten en maakt gebruik van gratis tiers en tegoeden.
**Een volledig gratis installatie is geen garantie.** Sportlink Dataservice, Microsoft 365,
AI-aanroepen en cloudopslag kunnen kosten met zich meebrengen; ook gratis tiers hebben grenzen.

Azure Functions draait op Flex Consumption, met een gratis maandtegoed voor on-demand gebruik.
Opslag en netwerk worden apart berekend. Controleer de actuele
[Azure-prijzen](https://azure.microsoft.com/en-us/pricing/details/functions/) en stel een kostenbudget
in voordat je resources aanmaakt. De [setupgids](SETUP-NIEUWE-CLUB.md#9-kosten) helpt je bij die afweging.

## Voor wie graag onder de motorkap kijkt

De stack bestaat uit **.NET 10**, **Azure Functions**, **Blazor WebAssembly** en **Microsoft Entra ID**.
Postgres is de standaarddatabase; SQL Server is de tweede ondersteunde keuze. Microsoft Graph
verzorgt de mailboxkoppeling. AI-classificatie gebruikt `IChatClient`, met een configureerbare modelnaam.

De webapp leest via de API uit de eigen database. Synchronisatie haalt Sportlink-data op volgens een
instelbaar schema; je kunt ook handmatig synchroniseren. De Sportlink Club-koppeling verzorgt de
schrijfacties. Een berekend planningsvoorstel wordt niet vanzelf een wijziging in Sportlink.

Meer weten? Lees de [architectuur](docs/ARCHITECTUUR.md),
[databasekeuze](docs/ARCHITECTUUR-DATABASE-TIERS.md) of [API-referentie](docs/API.md).
AI-agents vinden hun werkinstructies in [CLAUDE.md](CLAUDE.md) en de daaruit gegenereerde [AGENTS.md](AGENTS.md).

## Gegevens en privacy

De applicatie verwerkt onder meer contactgegevens van teambegeleiders. Er zijn maatregelen zoals
rolcontrole, afgeschermde opslag, BCC in relevante mailstromen en beveiligingschecks in CI. Bij een
eigen installatie hoort ook zorgvuldig beheer van toegang, imports en bewaartermijnen.

Lees het [beveiligings- en privacybeleid](SECURITY.md) voordat je echte persoonsgegevens invoert.

## Volg de ontwikkeling

Bekijk de [releases](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/releases) en
[CHANGELOG](CHANGELOG.md) voor nieuwe mogelijkheden en fixes.
Op GitHub zie je ook werk dat nog in ontwikkeling is: `main` hoort bij de releases,
`develop` is de integratiebranch. Een gemergede feature op `develop` staat dus nog niet automatisch live.

Een onhandig scherm of een goed idee? [Open een issue](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/new/choose)
met wat je wilt bereiken. Gebruik fictieve voorbeelden en laat clubgegevens, persoonsgegevens en
secrets weg. Voor kwetsbaarheden volg je de private meldroute in [SECURITY.md](SECURITY.md).

## Licentie

Er staat nog geen `LICENSE`-bestand in deze repository. Stem de gebruiks- en distributierechten
met de eigenaar af voordat je de software voor je club in gebruik neemt.
