# Planner API — Architecture & Guidelines

This document defines the rules, constraints, and API contract for the VRC Veldplanner API. It is the single source of truth for scheduling logic.

## Purpose

Automated field availability checking for friendly/practice matches at **Sportpark Spitsbergen, Veenendaal**. When someone asks (via email, WhatsApp, or other channels) to schedule a match, the API checks availability and returns whether the requested date/time works — or suggests alternatives.

---

## Field Definitions

| Veld | Kunstlicht | Beschikbaar voor planner | Opmerkingen |
|------|-----------|-------------------------|-------------|
| Veld 1 | Ja | Alleen zaterdag | Training ma-do |
| Veld 2 | Ja | Alleen zaterdag | Training ma-do |
| Veld 3 | Ja | Alleen zaterdag | Training ma-do |
| Veld 4 | Ja | Alleen zaterdag | Training ma-do |
| Veld 5 | **Nee** | Ma-do + zaterdag | Geen licht, zonsondergang-limiet |
| Veld 6 | — | **Nooit** | Niet functioneel |

**Field preference order:** Veld 1 > Veld 2 > Veld 3 > Veld 4 > Veld 5

Veld 5 is only assigned when veld 1–4 are fully occupied in the requested time window.

---

## Availability Rules per Day

| Dag | Beschikbare velden | Tijdvenster | Bijzonderheden |
|-----|-------------------|-------------|----------------|
| Maandag–Donderdag | Veld 5 alleen | 09:00 – zonsondergang | Geen kunstlicht |
| Vrijdag | **Geen** | — | Geen wedstrijden toegestaan |
| Zaterdag | Veld 1–4 + Veld 5 | 08:30 – 22:00 (veld 1–4) / 08:30 – 17:00 (veld 5) | 10 min buffer tussen wedstrijden |
| Zondag | **Geen** | — | Geen wedstrijden toegestaan |

---

## Scheduling Rules

### Buffer between matches
- **Standard buffer:** 10 minuten tussen opeenvolgende wedstrijden op hetzelfde veld (zaterdag)
- **Team-specifieke buffers:** Configureerbaar via `dbo.TeamRegels` tabel

### Team-specific exceptions (dbo.TeamRegels)

| Team | Regel | Waarde | Toelichting |
|------|-------|--------|-------------|
| VRC 1 | BufferVoor | 60 min | 1 uur voor de wedstrijd geen andere wedstrijden op hetzelfde veld |
| VRC 1 | BufferNa | 30 min | 30 min na de wedstrijd geen andere wedstrijden op hetzelfde veld |

Toekomstige regels (voorbeelden):
- `VoorkeurVeld`: team speelt altijd op een bepaald veld
- `VoorkeurTijd`: team speelt altijd rond een bepaalde tijd

### Sunset constraint (velden zonder kunstlicht)
- Wedstrijd moet eindigen **voor zonsondergang**
- Zonsondergang berekend via NOAA solar algorithm voor Veenendaal (52.0284°N, 5.5579°E)
- Opgeslagen in `dbo.Zonsondergang` tabel (handmatige overrides mogelijk)
- **Geen harde buffer**, wel een waarschuwing als marge < 20 minuten

### Morning-first preference for youth (zaterdag, soft rule)
- JO7–JO10: voorkeur ochtend (08:30–11:00)
- JO11–JO12: voorkeur mid-ochtend (10:00–13:00)
- JO13+/Senioren: voorkeur middag (13:00+)

Dit zijn voorkeuren, geen harde beperkingen.

---

## Field Capacity (deelveld-wedstrijden)

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

## Algorithm (processing order)

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

## API Contract

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

## Database Schema (new tables)

### dbo.Velden
Field definitions. Seed: veld 1–4 (lights), veld 5 (no lights), veld 6 (inactive).

### dbo.VeldBeschikbaarheid
Per day-of-week availability windows per field. Drives which fields are available on which days.

### dbo.TeamRegels
Configurable team-specific exceptions (buffers, field/time preferences). No code changes needed to add rules.

### dbo.Zonsondergang
Pre-computed sunset times per date. Populated by NOAA calculator, manually overridable.

### planner.GeplandeWedstrijden
Planned matches booked through the API. Includes status (Gepland/Bevestigd/Geannuleerd).

### planner.AlleWedstrijdenOpVeld (view)
Unified view combining competition matches (`his.matches`) with planner bookings. Single source for field occupation queries.

---

## Future: Messaging Integration

The API is designed as a standalone REST endpoint. Two integration options are documented:

### Option A: Power Automate
Trigger on email → AI Builder parses text → HTTP POST to API → auto-reply. Low-code, extends easily to Teams/WhatsApp.

### Option B: Azure Function + Microsoft Graph API
Custom function polls/receives emails via Graph API → LLM extracts parameters → calls PlannerService directly → sends reply via Graph. Full control, same codebase.

**AI choice deferred** — any LLM that parses Dutch natural language to the request JSON works. The API contract is the stable interface.
