# Planner API — Architectuur & Richtlijnen

Dit document definieert de regels, beperkingen en API-contract voor de VRC Veldplanner API. Het is de enige bron van waarheid voor de planningslogica.

## Doel

Geautomatiseerde veldbeschikbaarheidscontrole voor oefenwedstrijden op **Sportpark Spitsbergen, Veenendaal**. Wanneer iemand (via email, WhatsApp of andere kanalen) een wedstrijd wil plannen, controleert de API de beschikbaarheid en geeft aan of de gewenste datum/tijd mogelijk is — of stelt alternatieven voor.

---

## Velddefinities

| Veld | Kunstlicht | Beschikbaar voor planner | Opmerkingen |
|------|-----------|-------------------------|-------------|
| Veld 1 | Ja | Alleen zaterdag | Training ma-do |
| Veld 2 | Ja | Alleen zaterdag | Training ma-do |
| Veld 3 | Ja | Alleen zaterdag | Training ma-do |
| Veld 4 | Ja | Alleen zaterdag | Training ma-do |
| Veld 5 | **Nee** | Ma-do + zaterdag | Geen licht, zonsondergang-limiet |
| Veld 6 | — | **Nooit** | Niet functioneel |

**Voorkeursvolgorde velden:** Veld 1 > Veld 2 > Veld 3 > Veld 4 > Veld 5

Veld 5 wordt alleen toegewezen als veld 1–4 volledig bezet zijn in het gevraagde tijdvenster.

---

## Beschikbaarheidsregels per dag

| Dag | Beschikbare velden | Tijdvenster | Bijzonderheden |
|-----|-------------------|-------------|----------------|
| Maandag–Donderdag | Veld 5 alleen | 18:00 – zonsondergang | Geen kunstlicht, sportpark overdag gesloten |
| Vrijdag | **Geen** | — | Geen wedstrijden toegestaan |
| Zaterdag | Veld 1–4 + Veld 5 | 08:30 – 22:00 (veld 1–4) / 08:30 – 17:00 (veld 5) | 10 min buffer tussen wedstrijden |
| Zondag | **Geen** | — | Geen wedstrijden toegestaan |

---

## Planningsregels

### Buffer tussen wedstrijden
- **Standaard buffer:** 15 minuten tussen opeenvolgende wedstrijden op hetzelfde veld
- **Uitzondering:** 10 minuten voor overvolle programma's (configureerbaar via `dbo.AppSettings` of API parameter)
- **Dynamische buffer:** bij optimalisatie wordt resterende ruimte tot de gewenste eindtijd (16:15) verdeeld als extra buffer (max 30 min)
- **Team-specifieke buffers:** Configureerbaar via `dbo.TeamRegels` tabel
- **Afronden:** alle aanvangstijden worden naar boven afgerond op 5 minuten (voetbalconventie)

### Teamspecifieke uitzonderingen (dbo.TeamRegels)

| Team | Regel | Waarde | Toelichting |
|------|-------|--------|-------------|
| VRC 1 | BufferVoor | 60 min | 1 uur voor de wedstrijd geen andere wedstrijden op hetzelfde veld |
| VRC 1 | BufferNa | 30 min | 30 min na de wedstrijd geen andere wedstrijden op hetzelfde veld |

Toekomstige regels (voorbeelden):
- `VoorkeurVeld`: team speelt altijd op een bepaald veld
- `VoorkeurTijd`: team speelt altijd rond een bepaalde tijd

### Zonsondergang-beperking (velden zonder kunstlicht)
- Wedstrijd moet eindigen **voor zonsondergang**
- Zonsondergang berekend via NOAA solar algorithm voor Veenendaal (52.0284°N, 5.5579°E)
- Opgeslagen in `dbo.Zonsondergang` tabel (handmatige overrides mogelijk)
- **Geen harde buffer**, wel een waarschuwing als marge < 20 minuten

### Ochtend-eerst voorkeur voor jeugd (zaterdag, zachte regel)
- JO7–JO10: voorkeur ochtend (08:30–11:00)
- JO11–JO12: voorkeur mid-ochtend (10:00–13:00)
- JO13+/Senioren: voorkeur middag (13:00+)

Dit zijn voorkeuren, geen harde beperkingen.

---

## Veldcapaciteit (deelveld-wedstrijden)

Een veld heeft capaciteit **1.00**. Wedstrijden gebruiken een fractie op basis van leeftijdscategorie:

| Veldafmeting | Betekenis | Gelijktijdig op 1 veld |
|-------------|-----------|----------------------|
| 0.25 | Kwart veld | 4 wedstrijden |
| 0.50 | Half veld | 2 wedstrijden |
| 1.00 | Heel veld | 1 wedstrijd |

**Regel:** som van overlappende `Veldafmeting` op hetzelfde veld op hetzelfde moment moet ≤ 1.00 zijn.

**Voorbeeld efficiënt plannen:**
- 2× JO9 (0.25) op één helft + 1× JO11 (0.50) op de andere helft = 1.00 → veld vol

### Speeltijden per leeftijdscategorie

| Categorie | Veldafmeting | Totaal (min) | Helft (min) | Rust (min) |
|-----------|-------------|-------------|-------------|------------|
| JO7 | 0.25 | 50 | 20 | 10 |
| JO8 | 0.25 | 50 | 20 | 10 |
| JO9 | 0.25 | 50 | 20 | 10 |
| JO10 | 0.25 | 65 | 25 | 15 |
| JO11 | 0.50 | 75 | 30 | 15 |
| JO12 | 0.50 | 75 | 30 | 15 |
| JO13 | 1.00 | 75 | 30 | 15 |
| JO14 | 1.00 | 85 | 35 | 15 |
| JO15 | 1.00 | 85 | 35 | 15 |
| JO16 | 1.00 | 95 | 40 | 15 |
| JO17 | 1.00 | 95 | 40 | 15 |
| JO18 | 1.00 | 105 | 45 | 15 |
| JO19 | 1.00 | 105 | 45 | 15 |
| JO23 | 1.00 | 105 | 45 | 15 |
| MO13 | 1.00 | 75 | 30 | 15 |
| MO15 | 1.00 | 85 | 35 | 15 |
| MO17 | 1.00 | 95 | 40 | 15 |
| MO19 | 1.00 | 105 | 45 | 15 |
| MO20 | 1.00 | 105 | 45 | 15 |
| VR | 1.00 | 105 | 45 | 15 |
| G | 0.50 | 75 | 30 | 15 |
| 1-99 | 1.00 | 105 | 45 | 15 |

---

## Algoritme (verwerkingsvolgorde)

```
1. Resolve match parameters from Speeltijden (duration, field fraction)
2. TEAM CONFLICT CHECK — does this team already have a match on this date?
   → If yes: return immediately with conflict info, stop.
3. Load available fields for this day-of-week from VeldBeschikbaarheid
   → If no fields (Friday/Sunday): return "no matches on this day", stop.
4. Load all existing field occupations on this date
   (competition matches from his.matches + planner bookings from planner.GeplandeWedstrijden)
5. Load team-specific rules from TeamRegels (buffers, preferences)
6. Get sunset time for fields without lights
7. If preferred time given:
   → Try to assign at that exact time on best available field
8. If no time, or preferred time fails:
   → Scan all time slots, return best options as alternatives
9. Apply scheduling preferences (morning-first for youth, field priority)
```

---

## API-contract

### Endpoint: `POST /api/planner/check-availability`

#### Request
```json
{
  "datum": "2026-04-18",
  "aanvangsTijd": "10:00",
  "dagdeel": null,
  "leeftijdsCategorie": "JO11",
  "teamNaam": "VRC JO11-1",
  "tegenstander": "Ede JO11-2",
  "wedstrijdDuurMinuten": null
}
```

| Veld | Verplicht | Beschrijving |
|------|-----------|-------------|
| datum | Ja | Gewenste datum (ISO format) |
| aanvangsTijd | Nee | Gewenste aanvangstijd ("HH:mm"), null = zoek beste slot |
| dagdeel | Nee | "ochtend", "middag", of "avond" — gebruikt als geen exact tijdstip |
| leeftijdsCategorie | Nee | Bepaalt duur + veldfractie. Zonder: retourneert beschikbare vensters |
| teamNaam | Nee | Voor team-conflictcheck en team-specifieke regels |
| tegenstander | Nee | Administratief |
| wedstrijdDuurMinuten | Nee | Overschrijft standaardduur uit Speeltijden |

#### Response Mode 1 — Met leeftijdsCategorie (specifieke toewijzing)

**Beschikbaar:**
```json
{
  "beschikbaar": true,
  "toewijzing": {
    "datum": "2026-04-18",
    "aanvangsTijd": "10:00",
    "eindTijd": "11:15",
    "veldNummer": 2,
    "veldNaam": "veld 2",
    "veldDeelGebruik": 0.50,
    "wedstrijdDuurMinuten": 75
  },
  "alternatieven": [],
  "waarschuwingen": []
}
```

**Niet beschikbaar:**
```json
{
  "beschikbaar": false,
  "toewijzing": null,
  "reden": "Alle velden bezet om 10:00 op zaterdag 18 april.",
  "alternatieven": [
    {
      "datum": "2026-04-18",
      "aanvangsTijd": "11:30",
      "eindTijd": "12:45",
      "veldNummer": 3,
      "veldNaam": "veld 3",
      "veldDeelGebruik": 0.50,
      "wedstrijdDuurMinuten": 75
    }
  ],
  "waarschuwingen": ["Veld 5 niet beschikbaar na 17:23 (zonsondergang)."]
}
```

#### Response Mode 2 — Zonder leeftijdsCategorie (beschikbare vensters)

```json
{
  "beschikbaar": true,
  "beschikbareVensters": [
    {
      "veldNummer": 5,
      "veldNaam": "veld 5",
      "van": "17:00",
      "tot": "19:20",
      "maxDuurMinuten": 140,
      "opmerking": "Zonsondergang 21:20, geen kunstlicht"
    }
  ],
  "waarschuwingen": ["Maandag: alleen veld 5 beschikbaar (veld 1-4 training)."]
}
```

#### Response — Team conflict

```json
{
  "beschikbaar": false,
  "teamConflict": {
    "wedstrijd": "JO11-9 - Valleivogels JO11-3",
    "aanvangsTijd": "11:30",
    "eindTijd": "12:45",
    "veldNaam": "veld 4"
  },
  "reden": "JO11-9 heeft al een wedstrijd op 16 mei: JO11-9 - Valleivogels JO11-3 om 11:30 (veld 4).",
  "alternatieven": [],
  "waarschuwingen": []
}
```

### Endpoint: `POST /api/planner/bevestig`

Bevestigt een slot en schrijft naar `planner.GeplandeWedstrijden`.

```json
{
  "datum": "2026-04-18",
  "aanvangsTijd": "11:30",
  "veldNummer": 3,
  "leeftijdsCategorie": "JO11",
  "teamNaam": "VRC JO11-1",
  "tegenstander": "Ede JO11-2",
  "aangevraagdDoor": "coach@example.com"
}
```

---

## Beveiliging

### Authorization levels

| Niveau | Sleutel | Wie heeft toegang |
|--------|---------|-------------------|
| **Admin** | Master key | Alleen de coördinator (via Azure Portal of CLI) |
| **Function** | Function key | Automate, email-integratie, externe systemen |

### Endpoint-indeling

| Endpoint | Niveau | Toelichting |
|----------|--------|-------------|
| `sync-matches` | Admin | Sportlink sync — alleen handmatig door coördinator |
| `populate-sunset` | Admin | Zonsondergangtabel vullen — eenmalig per seizoen |
| Alle planner endpoints | Function | Beschikbaar voor toekomstige Automate-integratie |

### Bescherming tegen misbruik

- Zonder geldige sleutel → 401 Unauthorized (geen verwerking, geen kosten)
- Function key = als een wachtwoord, alleen delen met vertrouwde integraties
- Master key = alleen de coördinator, nooit delen
- Gratis database heeft auto-pause — langdurig misbruik wordt vanzelf gestopt

---

## Database schema (nieuwe tabellen)

### dbo.Velden
Velddefinities met type (kunstgras/natuurgras) en verlichting. Elke vereniging configureert dit naar eigen situatie.

| Kolom | Beschrijving |
|-------|-------------|
| VeldNummer | Uniek nummer (PK) |
| VeldNaam | Weergavenaam (bijv. "veld 1") |
| VeldType | `kunstgras` of `natuurgras` — bepaalt welke velden ontlast worden |
| HeeftKunstlicht | Verlichting beschikbaar — bepaalt zonsondergang-beperking |
| Actief | Of het veld in gebruik is |

Seeddata VRC: veld 1–4 kunstgras + kunstlicht, veld 5 natuurgras zonder kunstlicht, veld 6 inactief.

### dbo.VeldBeschikbaarheid
Beschikbaarheidsvensters per dag van de week per veld. Bepaalt welke velden op welke dagen beschikbaar zijn.

### dbo.TeamRegels
Configureerbare teamspecifieke uitzonderingen (buffers, veld-/tijdvoorkeuren). Geen codewijzigingen nodig om regels toe te voegen.

### dbo.Zonsondergang
Vooraf berekende zonsondergangstijden per datum. Gevuld door NOAA-calculator, handmatig aan te passen.

### planner.GeplandeWedstrijden
Geplande wedstrijden geboekt via de API. Bevat status (Gepland/Bevestigd/Geannuleerd).

### planner.AlleWedstrijdenOpVeld (view)
Gecombineerde weergave van competitiewedstrijden (`his.matches`) met planner-boekingen. Enige bron voor veldbezettingsqueries.

### planner.HerplanVerzoeken
Reschedule requests with status tracking (Aangevraagd/InOverleg/Bevestigd/Afgewezen). Records the original match details and desired new time. Does NOT modify the actual match — that is a manual process in Sportlink.

---

## Reschedule Flow (Herplannen)

When an opponent requests to reschedule an existing competition match:

```
1. zoek-wedstrijd     → Find match by team + date
2. herplan-check      → Simulate: what slots are free if this match moves?
                         (current slot treated as free, nothing is modified)
3. [Human decision]   → Own team must confirm the change
4. herplan-bevestig   → Register the request (status: Aangevraagd)
5. [Manual in Sportlink] → Actually reschedule the match
```

**Key principle:** `herplan-check` is a pure simulation. `herplan-bevestig` only records the request. No match data is ever modified by the API.

---

## Email-integratie

Automatische verwerking van inkomende emails op de coordinator-mailbox. Leest verzoeken, interpreteert ze met AI, roept PlannerService aan, en stuurt een antwoord.

### Gekozen aanpak

| Component | Keuze | Reden |
|-----------|-------|-------|
| Email lezen/sturen | **Microsoft Graph API + Application Permissions** | Gratis (onderdeel M365), volledig unattended via client credentials flow |
| AI/LLM | **OpenAI GPT-4o-mini** (direct, niet Azure OpenAI) | ~EUR 0.03/maand, geen goedkeuringsproces, later migreerbaar naar Azure OpenAI |
| Trigger | **Timer (elke 5 min polling)** | Zelfde patroon als FetchAndStoreApiData, simpel en betrouwbaar |
| Secrets opslag | **Azure Function Application Settings** | Gratis, encrypted at rest (AES-256), standaard Azure Functions patroon |

**Verworpen alternatieven:**
- Power Automate: gratis tier te beperkt (600 runs/maand vs. ~8.640 polls/maand)
- Graph webhooks: subscription verloopt elke 3 dagen, complexe renewal + cold start problemen
- Azure OpenAI: langere setup door goedkeuringsproces, zelfde model/prijs
- Key Vault: niet gratis ($0.03/10K operaties), overkill voor 2 secrets in verenigingsproject

### Architectuur

```
Timer (elke 5 min via EMAIL_POLL_SCHEDULE)
       |
       v
EmailProcessorFunction
       |
  +----+----+
  |         |
  v         v
Graph API   OpenAI GPT-4o-mini
(inbox)     (classificeer email)
  |                |
  v                v
Ongelezen     Gestructureerd verzoek (JSON)
emails             |
                   v
            PlannerService (bestaand, directe C# call)
                   |
                   v
            OpenAI (genereer antwoord)
                   |
                   v
            Graph API (stuur email)
            Fase 1: naar review-mailbox
            Fase 2: naar afzender
```

### Verwerkingsflow per email

1. **Poll inbox** — `GET /users/{mailbox}/mailFolders/inbox/messages?$filter=isRead eq false`
2. **Deduplicatie** — check MessageId tegen `planner.EmailVerwerking` tabel
3. **Classificeer** — GPT-4o-mini structured output → type verzoek + parameters
4. **PlannerService aanroepen** — directe C# method call (geen HTTP roundtrip)
5. **Antwoord genereren** — GPT-4o-mini met communicatierichtlijnen als systeemprompt
6. **Verstuur** — Graph API sendMail (Fase 1: review-mailbox, Fase 2: afzender)
7. **Markeer als gelezen** — Graph API PATCH isRead=true

### Bestanden (in `FunctionApp/Email/`)

| Bestand | Verantwoordelijkheid |
|---------|---------------------|
| `EmailProcessorFunction.cs` | Timer trigger + orchestratie |
| `EmailGraphService.cs` | Graph API wrapper (lezen, sturen, markeren) |
| `EmailAiService.cs` | OpenAI classificatie + antwoord generatie |
| `EmailModels.cs` | DTO's en enums |
| `EmailResponseGenerator.cs` | Template-gebaseerde antwoord-opbouw |

### AI classificatie (structured output)

```json
{
  "type": "beschikbaarheid_check | herplan_verzoek | bevestiging | buiten_scope",
  "datum": "yyyy-MM-dd",
  "aanvangsTijd": "HH:mm",
  "teamNaam": "string",
  "leeftijdsCategorie": "string",
  "tegenstander": "string",
  "samenvatting": "korte samenvatting van het verzoek",
  "namensWie": "afzender | tegenstander | onbekend"
}
```

### Database: planner.EmailVerwerking

Audit trail en conversatie-tracking voor alle verwerkte emails.

| Kolom | Type | Beschrijving |
|-------|------|-------------|
| Id | INT IDENTITY | PK |
| MessageId | NVARCHAR(500) | Graph message ID (deduplicatie) |
| ConversationId | NVARCHAR(500) | Voor threading |
| Afzender | NVARCHAR(200) | Email afzender |
| Onderwerp | NVARCHAR(500) | Email onderwerp |
| OntvangstDatum | DATETIME2 | Ontvangstmoment |
| EmailBody | NVARCHAR(MAX) | Platte tekst van email |
| VerzoekType | NVARCHAR(50) | Classificatie door AI |
| GeextraheerdeData | NVARCHAR(MAX) | JSON van AI extractie |
| PlannerResponse | NVARCHAR(MAX) | JSON response van PlannerService |
| AntwoordEmail | NVARCHAR(MAX) | Gegenereerde antwoordtekst |
| VerstuurdNaar | NVARCHAR(200) | Ontvanger antwoord |
| Status | NVARCHAR(30) | Ontvangen → Geclassificeerd → Verwerkt → Antwoord_Verstuurd / Fout / Buiten_Scope |
| FoutMelding | NVARCHAR(1000) | Bij status Fout |

### Configuratie (Azure Function Application Settings)

| Setting | Beschrijving |
|---------|-------------|
| `GraphTenantId` | Azure AD tenant ID |
| `GraphClientId` | Application (client) ID |
| `GraphClientSecret` | Client secret (encrypted at rest in App Settings) |
| `GraphMailbox` | Coordinator mailbox adres |
| `OpenAiApiKey` | OpenAI API key (encrypted at rest in App Settings) |
| `EmailProcessorEnabled` | Kill-switch (`true`/`false`) |
| `EmailReviewMode` | `true` = antwoorden naar review-mailbox (Fase 1) |
| `EmailReviewRecipient` | Review-mailbox adres (Fase 1) |
| `EMAIL_POLL_SCHEDULE` | CRON expressie (default `0 */5 * * * *`) |

### Kostenanalyse (maandelijks)

| Component | Kosten |
|-----------|--------|
| Microsoft Graph API | EUR 0 (onderdeel M365) |
| Azure Functions | EUR 0 (binnen free tier) |
| OpenAI GPT-4o-mini | EUR ~0.03 (100 emails/maand) |
| **Totaal** | **EUR ~0.03/maand** |

### Azure AD / Entra ID vereisten (eenmalige configuratie)

1. **App Registration** `VRC-Veldplanner-EmailProcessor` (daemon, geen redirect URI)
2. **API Permissions** (Application type): `Mail.Read`, `Mail.ReadWrite`, `Mail.Send` + admin consent
3. **Client Secret** aanmaken (24 maanden geldigheid)
4. **Application Access Policy** — beperk tot coordinator-mailbox via mail-enabled security group

### Uitrolfasen

| Fase | Beschrijving | Email-bestemming |
|------|-------------|-----------------|
| Fase 1 | Review mode — controle door coördinator | Review-mailbox |
| Fase 2 | Productie — direct antwoord | Originele afzender |

### Thuis/uit-herkenning bij herplanverzoeken

Bij herplanverzoeken kan de email binnenkomen via een VRC-contactpersoon (doorgestuurd namens de tegenstander) of rechtstreeks van de tegenstander. De communicatie-flow verschilt per situatie.

#### Stap 1 — Afzender herkennen

| Aanwijzing | Conclusie |
|-----------|-----------|
| Emaildomein `@vv-vrc.nl` | VRC-intern (thuisteam-kant) |
| Ander emaildomein | Mogelijk tegenstander of ouder/coach tegenstander |

#### Stap 2 — Namens wie is het verzoek?

De AI-laag analyseert de tekst op patronen:

| Patroon in tekst | Conclusie |
|-----------------|-----------|
| "[Tegenstander] vraagt of...", "zij willen..." | Doorgestuurd door VRC-er, verzoek namens uitteam |
| "Wij kunnen niet om...", "Is het voor ons mogelijk..." | Afzender zelf is de vragende partij |
| "Kunnen we de wedstrijd verplaatsen" | Afzender = vragende partij |

#### Stap 3 — Thuis/uit bepalen uit wedstrijddata

Altijd betrouwbaar uit de database: `his.matches.teamnaam` = VRC-team (thuisteam). De andere partij in het `wedstrijd`-veld is het uitteam. Dit is harde data, geen interpretatie nodig.

#### Stap 4 — Communicatie-flow per scenario

| Afzender | Verzoek namens | Flow |
|----------|---------------|------|
| VRC-intern | Tegenstander | Check planning → Overleg eigen VRC-team → Antwoord via VRC-er terug naar tegenstander |
| Tegenstander direct | Zichzelf | Check planning → Overleg VRC-team → Antwoord naar tegenstander |
| VRC-intern | Eigen team | Check planning → Direct overleg met tegenstander |

#### Afzender geautomatiseerde berichten

Geautomatiseerde antwoorden worden verstuurd onder de naam **VRC Veldplanner** met vermelding dat het een automatisch bericht is. De handtekening verwijst naar de verantwoordelijke contactpersoon:

```
Met vriendelijke groet,

VRC Veldplanner
Geautomatiseerd antwoord namens [CoordinatorNaam]
[CoordinatorFunctie]
```

De afzendergegevens worden **niet hardcoded** maar opgeslagen in `dbo.AppSettings`:

| Instelling | Beschrijving | Voorbeeld |
|-----------|-------------|-----------|
| `PlannerAfzenderNaam` | Naam van het geautomatiseerde systeem | VRC Veldplanner |
| `CoordinatorNaam` | Naam verantwoordelijke contactpersoon | Uit database, niet in code |
| `CoordinatorFunctie` | Functietitel contactpersoon | Coördinator thuiswedstrijden |
| `PlannerEmailAdres` | Emailadres voor verzending | Configureerbaar per omgeving |

Zo blijven persoonsgegevens buiten de code (AVG/GDPR-conform) en zijn ze wijzigbaar zonder deployment.

#### Communicatiestijl geautomatiseerde berichten

Antwoorden moeten **kort en duidelijk** zijn, zonder technische details over het algoritme. De ontvanger hoeft niet te weten hoe de berekening werkt.

**Niet beschikbaar — voorbeeld:**

> Maandag 18 mei is er geen mogelijkheid omdat er al een andere wedstrijd op veld 5 staat gepland. Mogelijkheden om voor of na deze wedstrijd te spelen is niet mogelijk vanwege het ontbreken van verlichting en zonsondergang (21:31).

**Wel beschikbaar — voorbeeld:**

> Woensdag 20 mei is veld 5 beschikbaar om 18:30. De wedstrijd eindigt om 19:45, ruim voor zonsondergang (21:31).

**Richtlijnen:**
- Geef alleen aan of het wel of niet kan, niet waarom het algoritme bepaalde tijden heeft geprobeerd
- Bij "niet mogelijk": vermeld kort de reden (ander wedstrijd, geen verlichting, veld vol)
- Bij "wel mogelijk": vermeld het tijdstip, veld en eindtijd
- Vermeld zonsondergang alleen als het relevant is (velden zonder kunstlicht)
- Bij datum-discrepanties (bijv. "maandag 20 mei" terwijl dat een woensdag is): corrigeer vriendelijk en geef beide opties
- Geef niet meer dan 2-3 alternatieven — te veel keuze werkt verwarrend
- Beantwoord alleen de expliciete vraag — bied niet zelf extra opties aan tenzij het gevraagde niet mogelijk is
- Gebruik een tijdsgebonden aanhef: voor 12:00 = "Goedemorgen", 12:00-18:00 = "Goedemiddag", na 18:00 = "Goedenavond"
- Als de genoemde aanvangstijd afwijkt van het systeem (wedstrijd al verplaatst): meld dit en geef aan dat een nieuw verzoek ingediend kan worden

#### Verzoeken buiten scope

Niet alle verzoeken gaan over veldbeschikbaarheid. De volgende typen verzoeken vallen **buiten de scope** van de Veldplanner en worden ter beoordeling bij de coördinator neergelegd:

| Type verzoek | Voorbeeld | Actie |
|-------------|-----------|-------|
| Persoonlijk roosterconflict | "Ik coach twee teams die tegelijk spelen" | Ter beoordeling bij coördinator |
| Sportlink platform problemen | "De veldplanner laadt traag" | Doorsturen naar Sportlink support |
| Verzoeken over niet-thuiswedstrijden | "Kunnen we onze uitwedstrijd verplaatsen?" | Doorsturen naar tegenstander/KNVB |
| Verzoeken zonder duidelijke wedstrijd | "Kunnen we een keer oefenen?" | Ter beoordeling bij coördinator |
| Handmatige actie vereist | "Kun jij contact opnemen met Achterberg?" | Ter beoordeling bij coördinator |

**Standaard antwoord bij buiten-scope verzoeken:**

> Bedankt voor je bericht. Dit verzoek vereist handmatige afhandeling en is ter beoordeling bij de coördinator neergelegd.

**Standaard antwoord bij herplanverzoek met beschikbaar alternatief:**

> De wedstrijd [wedstrijd] staat gepland op [datum] om [tijd] op [veld].
>
> Er is een mogelijkheid om de wedstrijd te verplaatsen naar [nieuwe tijd] op [nieuw veld].
>
> Wil je dat wij dit voor je in gang zetten? De coördinator thuiswedstrijden beoordeelt het verzoek en je ontvangt daarna een bevestiging.

**Standaard antwoord bij herplanverzoek zonder beschikbaar alternatief:**

> De wedstrijd [wedstrijd] staat gepland op [datum] om [tijd] op [veld].
>
> Helaas is er op [datum] geen mogelijkheid om de wedstrijd te verplaatsen. [Korte reden: velden bezet / geen verlichting / etc.]

**Standaard antwoord bij bevestiging verplaatsing (aanvrager zegt "ja"):**

> Het verzoek om de wedstrijd [wedstrijd] te verplaatsen naar [nieuwe tijd] op [nieuw veld] is ingediend. De coördinator thuiswedstrijden beoordeelt het verzoek en je ontvangt daarna een bevestiging.

**Standaard antwoord bij afwijzing verplaatsing (aanvrager zegt "nee"):**

> Begrepen. De wedstrijd [wedstrijd] blijft staan op [datum] om [tijd] op [veld].
