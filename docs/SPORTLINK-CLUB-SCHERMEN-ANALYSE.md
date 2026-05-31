# Sportlink Club - Schermen & Data Analyse

> Doel: Inventarisatie van alle schermen in Sportlink Club (club.sportlink.com) en de beschikbare data voor de Blazor app. Gebaseerd op analyse van de live omgeving.

## 1. Navigatiestructuur

### Personen
- `/member/search` — Zoeken (tabs: Algemeen, Functies, Commissies, Contributie, Diploma's, Contactdossiers, Teams, Organisaties)
- `/member/person-selections` — Persoonsselecties
- `/member/registrations` — Personen toevoegen
- `/member/notifications` — Persoonswijzigingen
- `/member/functionaries` — Controleer functies
- `/member/vskv2` — Spelregelbewijzen VSK
- `/member/anniversaries` — Verjaardagen
- `/member/search-persons-without-union-certificate` — Personen zonder diploma
- `/member/person-transfers` — Overschrijvingen

### Overig
- `/committees` — Commissies
- `/organization` — Organisaties
- `/calendar-activities` — Agenda

### Wedstrijdzaken
- `/competition-affairs/match-program` — Wedstrijdplanning
- `/competition-affairs/match-results` — Gespeelde wedstrijden
- `/competition-affairs/preferred-officials` — Voorkeursofficials
- `/competition-affairs/field-occupation-v2` — Veldplanner
- `/competition-affairs/dressing-room-occupation-v2` — Kleedkamerplanner
- `/competition-affairs/change-requests` — Wedstrijdwijzigingsverzoeken
- `/competition-affairs/discipline-cases` — Tuchtzaken (intern/gevoelig)
- `/competition-affairs/coach-license` — Controleer coachlicenties
- `/competition-affairs/dispensations` — Dispensaties

### Teams
- `/teams/union-teams` — Bondsteams
- `/teams/club-teams` — Lokale teams (rijkste databron)
- `/teams/offdays` — Snipperdagen
- `/teams/search-unassigned-players` — Zoek niet-ingedeelde spelers
- `/teams/team-registration` — Teaminschrijvingen
- `/teams/team-change-requests` — Verzoeken
- `/tournaments` — Toernooien

### Financieel
- `/financial/current-account` — Rekening courant
- `/financial/installments` — Bondsafdracht
- `/financial/accounting` — Boekhouding AFAS SB (extern link)
- `/financial/contribution` — Contributie (beschikbaarheid afhankelijk van abonnement)
- `/financial/invoicing` — Factuuroverzicht (beschikbaarheid afhankelijk van abonnement)
- `/financial/payment-requests` — Betaalverzoeken (beschikbaarheid afhankelijk van abonnement)

### Rapportages
- `/reports/competitions` — Uitslagen & standen (PDF-generator)
- `/reports/team-registration` — Teaminschrijvingen rapport
- `/reports/app-accounts` — App accounts overzicht
- `/reports/sportivity` — Spelplezier (Sportivity koppeling)
- `/reports/member-information` — Bondsleden rapport
- `/reports/geographic-overview` — Geografisch ledenoverzicht (kaart)

### Verenigingsbeheer
- `/club-maintenance/clubdata` — Algemeen/Clubdata
- `/club-maintenance/certificates` — Diploma's beheer
- `/club-maintenance/functions` — Functies beheer
- `/club-maintenance/users-roles` — Gebruikers & toegang
- `/club-maintenance/colors` — Kleuren (teamkleuren)
- `/club-maintenance/game-activity` — Sporten & activiteiten
- `/club-maintenance/free-fields` — Vrije invoervelden
- `/club-maintenance/facility` — Accommodaties (velden, kleedkamers)
- `/club-maintenance/communication-settings` — Communicatie instellingen
- `/club-maintenance/calendar-settings` — Kalenderinstellingen
- `/my-settings` — Mijn instellingen

---

## 2. Analyse per Module

### 2.1 Zoeken (/member/search)
Zoektabs: Algemeen, Functies, Commissies, Contributie, Diploma's & passen, Contactdossiers, Teams, Organisaties.
Data per persoon: naam, relatiecode, lidsoort (Bondslid/Verenigingslid), geslacht, leeftijd, status, email, telefoonnummer.
**Blazor-potentie: HOOG** — ledenlijst, zoekfunctie, profielpagina's

### 2.2 Persoonswijzigingen (/member/notifications)
Mutatielog: naam, relatiecode, actie (Toegevoegd/Verwijderd), omschrijving (Online aanmelding, Afgemeld bij team X), datum+tijd, door (Online/Bond/teamnaam).
**Blazor-potentie: GEMIDDELD** — secretariaat-widget recente aanmeldingen. Niet beschikbaar via Sportlink Dataservice.

### 2.3 Controleer Functies (/member/functionaries)
Alle functionarissen: functietitel, naam, email, telefoonnummer, mobiel, datum laatste controle. Filter: ouder dan 6 maanden.
Voorbeeldfuncties: Bestuurslid, Ledenadministrateur, Wedstrijdsecretaris, Scheidsrechterscoordinator, Verenigingsscheidsrechter, etc.
**Blazor-potentie: HOOG** — publieke bestuurspagina en contactpersonen. Niet beschikbaar via Sportlink Dataservice.

### 2.4 Verjaardagen (/member/anniversaries)
Leden binnenkort jarig: relatiecode, lidsoort, naam, geslacht, verjaardag, leeftijd huidig + nieuw. Filter: binnen X dagen (standaard 30). Export PDF/Excel.
**Blazor-potentie: GEMIDDELD** — verjaardagswidget intern bestuur. Niet beschikbaar via Sportlink Dataservice.

### 2.5 Overschrijvingen (/member/person-transfers)
Tabs: Overschrijvingen, Verleende verzoeken, Opwaarderen A-cat, Opwaarderingsverzoeken.
Kolommen: naam, relatiecode, aanvraagdatum, ingangsdatum, van/naar vereniging, status bond, categorie.
**Blazor-potentie: INTERN** — wedstrijdsecretariaat only

### 2.6 Commissies (/committees)
Naam, omschrijving, email, aantal leden, publiceer ja/nee.
**Blazor-potentie: HOOG** — commissiepagina clubsite. Niet beschikbaar via Sportlink Dataservice.

### 2.7 Agenda (/calendar-activities)
Kalender maand/week/dag weergave. Filters: agenda type, vrijwilligerstaken, locaties. Activiteiten aanmaken mogelijk.
**Blazor-potentie: HOOG** — evenementenkalender voor leden en publiek

### 2.8 Wedstrijdplanning (/competition-affairs/match-program)
Aankomende wedstrijden: datum/tijd, thuisploeg vs uitploeg, veld/locatie. Filters: datumbereik, thuiswedstrijden toggle, geen andere verenigingen toggle.
**Blazor-potentie: HOOGSTE PRIORITEIT** — core feature van het project.

### 2.9 Gespeelde Wedstrijden (/competition-affairs/match-results)
Uitslagen van gespeelde wedstrijden. Zelfde interface als wedstrijdplanning maar verleden periode. Filters identiek.
**Blazor-potentie: HOOGSTE PRIORITEIT** — uitslagenoverzicht is kernfunctionaliteit.

### 2.10 Veldplanner (/competition-affairs/field-occupation-v2)
Visuele Gantt-chart tijdlijn per veld. Per blok: team, wedstrijd, aanvangstijd. Filter: speeldatum, accommodatie.
Aantal velden varieert per club (typisch 4–8 velden zichtbaar in de planner).
**Blazor-potentie: HOOG** — veld-bezetting voor kantine/bardienst, wedstrijddag-schermen. Niet beschikbaar via Sportlink Dataservice.

### 2.11 Kleedkamerplanner (/competition-affairs/dressing-room-occupation-v2)
Tijdlijn per kleedkamer. Toewijzing per dag welk team welke kleedkamer krijgt. Status-indicator aanwezig. Accommodatie selecteerbaar.
**Blazor-potentie: HOOG** — kleedkameroverzicht voor wedstrijddag. Relevant voor bardienst en materiaalbeheer. Niet beschikbaar via Sportlink Dataservice.

### 2.12 Lokale teams (/teams/club-teams) — rijkste databron
Per team beschikbaar: naam (bijv. JO8-1, MO17-2), spelactiviteit (Veld-Zaterdag, G Team 7x7 etc.), geslacht (Mannen/Vrouwen/Gemengd), begindatum, einddatum, aantal spelers/teamleden, koppeling bondsteam (ja/nee), publiceer (ja/nee).
Typisch 20–50 teams per club: G-teams, jeugd JO/MO, senioren.
**Blazor-potentie: HOOG** — teamenoverzicht website, teamdetailpagina's. Via Sportlink Dataservice gedeeltelijk beschikbaar.

### 2.13 Rekening Courant (/financial/current-account)
Financieel overzicht per seizoen. Categorieën: Contributie Leden, Wedstrijdgelden, Tuchtzaken, Overige doorbelastingen, Doorbelasting derden, Periodieken/abonnementen, Overschrijvingen.
**Blazor-potentie: NIET PUBLIEK** — intern bestuur only, financieel gevoelig. Nooit in publieke Blazor-schermen.

---

## 3. Conclusie — Blazor Schermen Prioritering

### HOGE PRIORITEIT — Direct inzetbaar

| Module | Blazor-scherm | Data Bron |
|--------|---------------|-----------|
| Wedstrijdplanning | Aankomende wedstrijden per team | Sportlink Dataservice + eigen DB |
| Gespeelde wedstrijden | Uitslagenoverzicht per team | Sportlink Dataservice + eigen DB |
| Lokale teams | Teamoverzicht met details | Sportlink Dataservice + eigen DB |
| Veldplanner | Veld-bezetting wedstrijddag | Sportlink Club UI only — geen dataservice |
| Kleedkamerplanner | Kleedkamerverdeling per dag | Sportlink Club UI only — geen dataservice |
| Controleer Functies | Bestuurspagina & contacten | Sportlink Club UI only — geen dataservice |
| Verjaardagen | Verjaardagswidget bestuur | Sportlink Club UI only — geen dataservice |
| Persoonswijzigingen | Recente aanmeldingen widget | Sportlink Club UI only — geen dataservice |

### GEMIDDELDE PRIORITEIT

| Module | Blazor-scherm |
|--------|---------------|
| Agenda | Evenementenkalender leden & publiek |
| Commissies | Commissiepagina met contactmails |
| Bondsteams | Competitie-teams overzicht |
| Toernooien | Toernooipagina |
| Clubdata | Statische clubinfo (footer, contact) |
| Kleuren | Teamkleuren in UI |
| Accommodaties | Accommodatiepagina velden/kleedkamers |

### NIET GESCHIKT (intern of gevoelig)

Financieel (alle sub-pagina's), Tuchtzaken, Spelregelbewijzen VSK, Personen zonder diploma, Snipperdagen, PDF-rapportages, uitgeschakelde modules (Contributie/Factuuroverzicht/Betaalverzoeken), Persoonsselecties.

---

## 4. Beperkingen Sportlink Dataservice

### Endpoint beschikbaarheid

| Endpoint | Status | Probleem |
|----------|--------|---------|
| Teams ophalen | Beschikbaar | Incompleet — ontbrekende velden |
| Wedstrijdprogramma | Beschikbaar | Traag, soms leeg/onvolledig |
| Uitslagen | Beschikbaar | Niet altijd actueel |
| Standen | Beschikbaar | Alleen na KNVB-publicatie |
| Ledeninfo/profielen | NIET beschikbaar | — |
| Veldplanner data | NIET beschikbaar | — |
| Kleedkamerplanning | NIET beschikbaar | — |
| Verjaardagen | NIET beschikbaar | — |
| Agenda/activiteiten | NIET beschikbaar | — |
| Functionarissen | NIET beschikbaar | — |
| Commissies | NIET beschikbaar | — |
| Overschrijvingen | NIET beschikbaar | — |

### Bekende problemen

1. Trage responstijden — endpoints timeout regelmatig, cold starts Azure Functions nodig
2. Ontbrekende data — niet alle wedstrijden worden teruggegeven in responses
3. Geen CORS-headers — direct vanuit browser aanroepen lastig/onmogelijk
4. Geen webhooks/push — polling vereist, wat Sportlink-servers belast
5. Seizoenswisseling issues — data van vorig seizoen verdwijnt onverwacht
6. Beperkte ondersteuning — support reageert traag, oplossingen zijn onvolledig
7. Geen authenticatie voor clubspecifieke data — alles publiek of helemaal niet beschikbaar

### Huidig gekozen architectuur

Azure Functions pollen de Sportlink Dataservice en slaan data op in eigen SQL Database. Blazor leest altijd uit eigen DB, volledig onafhankelijk van Sportlink uptime.

Voordelen: geen Sportlink-afhankelijkheid bij live gebruik, historische data bewaren, Blazor leest snel uit eigen SQL, retries via Azure Functions, data-verrijking mogelijk (teamkleuren, foto's, beschrijvingen).

### Modules zonder dataservice — aanbevolen alternatieven

| Functionaliteit | Aanbevolen alternatief |
|----------------|----------------------|
| Veldplanner | Eigen veldplanner module in BlazorAdmin |
| Kleedkamerplanner | Eigen kleedkamer-toewijzing module in BlazorAdmin |
| Functionarissen/bestuur | Handmatig invoeren in BlazorAdmin |
| Commissie-leden | Handmatig invoeren in BlazorAdmin |
| Verjaardagen | Berekenen uit ledendata indien beschikbaar |
| Agenda/activiteiten | Eigen agendamodule in BlazorAdmin |
