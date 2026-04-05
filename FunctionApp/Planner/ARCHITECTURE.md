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
- **Standard buffer:** 10 minuten tussen opeenvolgende wedstrijden op hetzelfde veld (zaterdag)
- **Team-specifieke buffers:** Configureerbaar via `dbo.TeamRegels` tabel

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

## Database schema (nieuwe tabellen)

### dbo.Velden
Velddefinities. Seeddata: veld 1–4 (kunstlicht), veld 5 (geen kunstlicht), veld 6 (inactief).

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

---

## Toekomst: Berichtenintegratie

De API is ontworpen als een zelfstandig REST-endpoint zodat elke integratielaag het kan aanroepen. Twee opties zijn gedocumenteerd:

### Optie A: Power Automate
Trigger bij email → AI Builder interpreteert tekst → HTTP POST naar API → automatisch antwoord. Low-code, eenvoudig uit te breiden naar Teams/WhatsApp.

### Optie B: Azure Function + Microsoft Graph API
Eigen functie pollt/ontvangt emails via Graph API → LLM extraheert parameters → roept PlannerService direct aan → stuurt antwoord via Graph. Volledige controle, zelfde codebase.

**AI-keuze uitgesteld** — elke LLM die Nederlandse natuurlijke taal kan omzetten naar het API-request JSON formaat werkt. Het API-contract is de stabiele interface.

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
