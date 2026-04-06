# VRC Sportlink API Documentatie

**Basis-URL:** `http://localhost:7094/api`

## Beveiliging

Twee beveiligingsniveaus:

| Niveau | Sleutel | Wie | Endpoints |
|--------|---------|-----|-----------|
| **Function** | Function key (`?code=`) | Automate, integraties | Alle planner endpoints |
| **Admin** | Master key (`?code=`) | Alleen coördinator | sync-matches, populate-sunset |

Zonder geldige sleutel → 401 Unauthorized (kost niets, geen verwerking).

---

## Overzicht endpoints

| Methode | Endpoint | Niveau | Beschrijving |
|---------|----------|--------|-------------|
| `GET` | `/sync-matches` | **Admin** | Handmatige Sportlink data synchronisatie |
| `POST` | `/planner/check-availability` | Function | Veldbeschikbaarheid controleren |
| `POST` | `/planner/bevestig` | Function | Wedstrijdslot boeken |
| `POST` | `/planner/populate-sunset` | **Admin** | Zonsondergangtabel vullen |
| `POST` | `/planner/zoek-wedstrijd` | Function | Bestaande wedstrijd zoeken |
| `POST` | `/planner/herplan-check` | Function | Herplan-alternatieven simuleren |
| `POST` | `/planner/herplan-bevestig` | Function | Herplanverzoek registreren |
| `POST` | `/planner/optimaliseer` | Function | Planning optimaliseren (HTML/email/JSON) |

---

## GET /api/sync-matches

Handmatig een Sportlink API synchronisatie starten (teams, wedstrijden, wedstrijddetails).

### Queryparameters

| Parameter | Type | Verplicht | Beschrijving |
|-----------|------|-----------|-------------|
| `reset` | `boolean` | Nee | `true` = volledige seizoensynchronisatie in plaats van incrementeel |
| `season` | `integer` | Nee | Startjaar seizoen (bijv. `2024`). Gebruikt met `reset=true` |

### Voorbeeld

```
GET /api/sync-matches
GET /api/sync-matches?reset=true&season=2025
```

### Antwoord

```
200 OK
```

---

## POST /api/planner/check-availability

Controleer of een veld beschikbaar is voor een oefenwedstrijd. Geeft een specifieke slottoewijzing, beschikbare tijdvensters, of een teamconflict terug.

### Aanvraag

```json
{
  "datum": "2026-04-18",
  "aanvangsTijd": "12:00",
  "dagdeel": null,
  "leeftijdsCategorie": "JO13",
  "teamNaam": "VRC JO13-1",
  "tegenstander": "Ede JO13-2",
  "wedstrijdDuurMinuten": null
}
```

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `datum` | `string` | **Ja** | Datum in `yyyy-MM-dd` formaat |
| `aanvangsTijd` | `string` | Nee | Gewenste aftrapttijd `HH:mm`. Weglaten om beste slot te vinden |
| `dagdeel` | `string` | Nee | Dagdeelfilter: `"ochtend"`, `"middag"`, of `"avond"` |
| `leeftijdsCategorie` | `string` | Nee | Leeftijdscategorie (bijv. `JO11`, `MO17`, `VR`, `1-99`). Bepaalt wedstrijdduur en veldgrootte. Weglaten voor beschikbare vensters |
| `teamNaam` | `string` | Nee | Teamnaam voor conflictcontrole en teamspecifieke regels |
| `tegenstander` | `string` | Nee | Tegenstander (alleen voor administratie) |
| `wedstrijdDuurMinuten` | `integer` | Nee | Overschrijf wedstrijdduur in minuten (standaard uit Speeltijden) |

### Antwoord — Slot toegewezen (200)

Als `leeftijdsCategorie` is opgegeven en een slot beschikbaar is:

```json
{
  "beschikbaar": true,
  "toewijzing": {
    "datum": "2026-04-18",
    "aanvangsTijd": "12:00",
    "eindTijd": "13:15",
    "veldNummer": 3,
    "veldNaam": "veld 3",
    "veldDeelGebruik": 1.0,
    "wedstrijdDuurMinuten": 75
  },
  "teamConflict": null,
  "reden": null,
  "alternatieven": [],
  "beschikbareVensters": null,
  "waarschuwingen": []
}
```

### Antwoord — Niet beschikbaar met alternatieven (200)

Als de gevraagde tijd niet beschikbaar is:

```json
{
  "beschikbaar": false,
  "toewijzing": null,
  "teamConflict": null,
  "reden": "Gewenste tijd 12:00 is niet beschikbaar.",
  "alternatieven": [
    {
      "datum": "2026-04-11",
      "aanvangsTijd": "16:00",
      "eindTijd": "17:15",
      "veldNummer": 2,
      "veldNaam": "veld 2",
      "veldDeelGebruik": 1.0,
      "wedstrijdDuurMinuten": 75
    },
    {
      "datum": "2026-04-11",
      "aanvangsTijd": "18:00",
      "eindTijd": "19:15",
      "veldNummer": 1,
      "veldNaam": "veld 1",
      "veldDeelGebruik": 1.0,
      "wedstrijdDuurMinuten": 75
    }
  ],
  "beschikbareVensters": null,
  "waarschuwingen": []
}
```

### Antwoord — Beschikbare vensters (200)

Als `leeftijdsCategorie` niet is opgegeven — geeft open tijdvensters per veld:

```json
{
  "beschikbaar": true,
  "toewijzing": null,
  "teamConflict": null,
  "reden": null,
  "alternatieven": [],
  "beschikbareVensters": [
    {
      "veldNummer": 5,
      "veldNaam": "veld 5",
      "van": "17:00",
      "tot": "19:20",
      "maxDuurMinuten": 140,
      "opmerking": "Zonsondergang 21:28, geen kunstlicht"
    }
  ],
  "waarschuwingen": [
    "Monday: alleen veld 5 beschikbaar (veld 1-4 training)."
  ]
}
```

### Antwoord — Teamconflict (200)

Als het team al een wedstrijd heeft op de gevraagde datum:

```json
{
  "beschikbaar": false,
  "toewijzing": null,
  "teamConflict": {
    "wedstrijd": "VRC JO11-9JM - Valleivogels JO11-3",
    "aanvangsTijd": "11:30",
    "eindTijd": "12:45",
    "veldNaam": "veld 4"
  },
  "reden": "VRC JO11-9 heeft al een wedstrijd op 16 mei: VRC JO11-9JM - Valleivogels JO11-3 om 11:30 (veld 4).",
  "alternatieven": [],
  "beschikbareVensters": null,
  "waarschuwingen": []
}
```

### Antwoord — Geen wedstrijden toegestaan (200)

Als de gevraagde dag geen wedstrijden toelaat (vrijdag/zondag):

```json
{
  "beschikbaar": false,
  "toewijzing": null,
  "teamConflict": null,
  "reden": "Geen wedstrijden mogelijk op vrijdag.",
  "alternatieven": [],
  "beschikbareVensters": null,
  "waarschuwingen": []
}
```

### Antwoordvelden

| Veld | Type | Beschrijving |
|------|------|-------------|
| `beschikbaar` | `boolean` | Of een slot beschikbaar is |
| `toewijzing` | `object\|null` | Toegewezen slot (alleen Modus 1) |
| `teamConflict` | `object\|null` | Bestaande wedstrijd voor het team op deze datum |
| `reden` | `string\|null` | Reden als niet beschikbaar |
| `alternatieven` | `array` | Tot 3 alternatieve tijdsloten |
| `beschikbareVensters` | `array\|null` | Beschikbare vensters per veld (alleen Modus 2) |
| `waarschuwingen` | `array` | Waarschuwingen (zonsondergangmarge, doordeweekse beperkingen) |

### Foutantwoord (400)

```json
{
  "error": "Request body met 'datum' veld is verplicht."
}
```

---

## POST /api/planner/bevestig

Bevestig en boek een wedstrijdslot. Schrijft naar de `planner.GeplandeWedstrijden` tabel.

### Aanvraag

```json
{
  "datum": "2026-04-25",
  "aanvangsTijd": "12:00",
  "veldNummer": 3,
  "leeftijdsCategorie": "JO13",
  "teamNaam": "VRC JO13-1",
  "tegenstander": "Ede JO13-2",
  "aangevraagdDoor": "coach@example.com",
  "wedstrijdDuurMinuten": null
}
```

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|-------|------|----------|-------------|
| `datum` | `string` | **Ja** | Datum in `yyyy-MM-dd` formaat |
| `aanvangsTijd` | `string` | **Ja** | Aftrapttijd `HH:mm` |
| `veldNummer` | `integer` | **Ja** | Veldnummer (1-5) |
| `leeftijdsCategorie` | `string` | Nee | Leeftijdscategorie voor automatische duur/veldgrootte |
| `teamNaam` | `string` | Nee | Teamnaam |
| `tegenstander` | `string` | Nee | Tegenstander |
| `aangevraagdDoor` | `string` | Nee | Wie het verzoek heeft gedaan |
| `wedstrijdDuurMinuten` | `integer` | Nee | Overschrijf wedstrijdduur (standaard uit Speeltijden of 105) |

### Response (200)

```json
{
  "id": 1,
  "datum": "2026-04-25",
  "aanvangsTijd": "12:00",
  "eindTijd": "13:15",
  "veldNummer": 3,
  "status": "Gepland"
}
```

### Antwoordvelden

| Veld | Type | Beschrijving |
|-------|------|-------------|
| `id` | `integer` | Database-ID van de geboekte wedstrijd |
| `datum` | `string` | Bevestigde datum |
| `aanvangsTijd` | `string` | Bevestigde aftrapttijd |
| `eindTijd` | `string` | Berekende eindtijd |
| `veldNummer` | `integer` | Toegewezen veld |
| `status` | `string` | Altijd `"Gepland"` bij aanmaak |

### Foutantwoord (400)

```json
{
  "error": "Request body met 'datum', 'aanvangsTijd' en 'veldNummer' is verplicht."
}
```

---

## POST /api/planner/populate-sunset

Vul de zonsondergangtabel met NOAA-berekende tijden voor Veenendaal. Eenmalig uitvoeren na initiële setup, of wanneer het seizoen/datumbereik wordt uitgebreid.

### Aanvraag

Geen (lege POST).

### Antwoord (200)

```json
{
  "message": "Sunset data populated from 2026-01-01 to 2027-12-31."
}
```

---

## POST /api/planner/zoek-wedstrijd

Zoek een bestaande competitiewedstrijd op basis van teamnaam en datum.

### Request Body

```json
{
  "teamNaam": "VRC JO8-2",
  "datum": "2026-05-09"
}
```

### Request Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `teamNaam` | `string` | **Yes** | Team name (partial match supported) |
| `datum` | `string` | **Yes** | Date in `yyyy-MM-dd` format |

### Response — Found (200)

```json
{
  "gevonden": true,
  "wedstrijd": {
    "wedstrijdcode": 12345678,
    "wedstrijd": "VRC JO8-2 - Tegenstander JO8-1",
    "datum": "2026-05-09",
    "aanvangsTijd": "08:30",
    "eindTijd": "09:20",
    "veldNaam": "veld 3 A1",
    "leeftijdsCategorie": "Onder 8",
    "duurMinuten": 50,
    "veldDeelGebruik": 0.25
  }
}
```

### Response — Not Found (200)

```json
{
  "gevonden": false,
  "reden": "Geen wedstrijd gevonden voor VRC JO8-2 op 2026-05-09."
}
```

---

## POST /api/planner/herplan-check

Simulate rescheduling: calculate alternative time slots for an existing match. **Does NOT modify anything** — purely a calculation where the current slot is treated as free.

### Request Body

```json
{
  "wedstrijdcode": 12345678,
  "voorkeurTijd": "10:00",
  "dagdeel": "ochtend"
}
```

### Request Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `wedstrijdcode` | `integer` | **Yes** | Match code from `zoek-wedstrijd` response |
| `voorkeurTijd` | `string` | No | Preferred new time `HH:mm` |
| `dagdeel` | `string` | No | `"ochtend"`, `"middag"`, or `"avond"` |

### Response (200)

```json
{
  "huidigeWedstrijd": {
    "wedstrijdcode": 12345678,
    "wedstrijd": "VRC JO8-2 - Tegenstander JO8-1",
    "datum": "2026-05-09",
    "aanvangsTijd": "08:30",
    "eindTijd": "09:20",
    "veldNaam": "veld 3 A1",
    "leeftijdsCategorie": "Onder 8",
    "duurMinuten": 50,
    "veldDeelGebruik": 0.25
  },
  "beschikbaar": true,
  "alternatieven": [
    {
      "datum": "2026-05-09",
      "aanvangsTijd": "10:00",
      "eindTijd": "10:50",
      "veldNummer": 2,
      "veldNaam": "veld 2",
      "veldDeelGebruik": 0.25,
      "wedstrijdDuurMinuten": 50
    }
  ],
  "reden": null,
  "waarschuwingen": []
}
```

### Response — No Alternatives (200)

```json
{
  "huidigeWedstrijd": { ... },
  "beschikbaar": false,
  "alternatieven": [],
  "reden": "Geen alternatieve tijdsloten gevonden op zaterdag 9 mei.",
  "waarschuwingen": []
}
```

---

## POST /api/planner/herplan-bevestig

Register a reschedule request. **Does NOT modify the match** — only records the request with status "Aangevraagd". The actual change in Sportlink is a manual process.

### Request Body

```json
{
  "wedstrijdcode": 12345678,
  "gewensteAanvangsTijd": "10:00",
  "gewenstVeldNummer": 2,
  "aangevraagdDoor": "tegenstander via email",
  "opmerking": "08:30 is niet haalbaar"
}
```

### Request Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `wedstrijdcode` | `integer` | **Yes** | Match code |
| `gewensteAanvangsTijd` | `string` | **Yes** | Desired new time `HH:mm` |
| `gewenstVeldNummer` | `integer` | No | Desired field number |
| `aangevraagdDoor` | `string` | No | Who requested |
| `opmerking` | `string` | No | Reason / notes |

### Response (200)

```json
{
  "id": 1,
  "wedstrijdcode": 12345678,
  "huidigeWedstrijd": "VRC JO8-2 - Tegenstander JO8-1",
  "gewensteAanvangsTijd": "10:00",
  "gewenstVeldNummer": 2,
  "status": "Aangevraagd"
}
```

---

## POST /api/planner/optimaliseer

Analyseer de planning voor een datum en genereer optimalisatiesuggesties. Voert **niets** door — alleen advies. Drie output-formaten beschikbaar.

### Aanvraag

```json
{
  "datum": "2026-04-18",
  "doel": null
}
```

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `datum` | `string` | **Ja** | Datum in `yyyy-MM-dd` formaat |
| `doel` | `string` | Nee | Optimalisatiedoel (zie tabel). Leeg = beide combineren |
| `gewensteEindtijd` | `string` | Nee | Gewenste eindtijd `HH:mm`. Standaard `16:15`. Resterende ruimte wordt verdeeld als extra buffer |
| `bufferMinuten` | `integer` | Nee | Buffer tussen wedstrijden in minuten. Standaard 15. Overschrijft de standaard |

### Optimalisatiedoelen

| Doel | Beschrijving |
|------|-------------|
| *(leeg/niet opgegeven)* | **Standaard**: veld 5 ontlasten + strakker plannen gecombineerd |
| `veld5-ontlasten` | Verplaats wedstrijden van veld 5 naar kunstgras, alleen als het eerder kan |
| `strakker-plannen` | Minimaliseer gaten tussen wedstrijden op alle velden |

### Output-formaten (via querystring)

| Format | URL | Beschrijving |
|--------|-----|-------------|
| JSON | `POST /api/planner/optimaliseer` | Suggesties als JSON |
| Browser | `POST /api/planner/optimaliseer?format=html` | Interactieve HTML met grid, hover-interactie |
| Email | `POST /api/planner/optimaliseer?format=email` | Email-compatibele HTML met link naar browser-versie |

### Antwoord — JSON (200)

```json
{
  "datum": "2026-04-11",
  "huidigeEindtijd": "19:00",
  "aantalVerplaatsingen": 1,
  "aantalVanVeld5Verplaatst": 1,
  "suggesties": [
    {
      "wedstrijd": "VRC JO19-3 - Tegenstander JO19-4",
      "huidigVeldNummer": 5,
      "huidigVeld": "veld 5",
      "huidigeTijd": "16:45",
      "nieuwVeldNummer": 2,
      "nieuwVeld": "veld 2",
      "nieuweTijd": "16:00",
      "reden": "Verplaats van veld 5 (geen kunstlicht) naar veld 2"
    }
  ],
  "htmlPlanner": "<html>...</html>"
}
```

### Antwoord — Browser HTML

Interactieve veldplanner in Sportlink-stijl met:
- Donker grid met tijdlijn en gestapelde wedstrijdblokken
- Kleurcodes: grijs=ongewijzigd, blauw=vast, oranje=suggestie
- Hover-interactie: blok in grid ↔ rij in chronologische lijst
- Chronologisch overzicht met status per wedstrijd

### Antwoord — Email HTML

Versimpelde email-compatibele versie met:
- Bovenaan: link "Bekijk in browser (meer functies)"
- Chronologische tabel met kleurcodes (inline CSS)
- Werkt in alle email-clients (Outlook, Gmail, etc.)

---

## Overzicht planningsregels

### Veldbeschikbaarheid

| Dag | Velden | Tijdvenster | Opmerkingen |
|-----|--------|-------------|-------------|
| Maandag-Donderdag | Alleen veld 5 | 18:00 - zonsondergang | Geen kunstlicht, veld 1-4 training |
| Vrijdag | Geen | - | Geen wedstrijden |
| Zaterdag | Veld 1-5 | 08:30 - 22:00 (1-4) / 08:30 - 17:00 (5) | 10 min buffer |
| Zondag | Geen | - | Geen wedstrijden |

### Veldvoorkeur

Veld 1 > Veld 2 > Veld 3 > Veld 4 > Veld 5 (laatste keuze)

### Leeftijdscategorieën (Speeltijden)

| Categorie | Veldgrootte | Duur | Veld delen |
|----------|-----------|----------|---------------|
| JO7, JO8, JO9 | 0.25 (kwart) | 50 min | 4 per veld |
| JO10 | 0.25 (kwart) | 65 min | 4 per veld |
| JO11, JO12 | 0.50 (half) | 75 min | 2 per veld |
| JO13, MO13 | 1.00 (heel) | 75 min | 1 per veld |
| JO14, JO15 | 1.00 (heel) | 85 min | 1 per veld |
| MO15 | 1.00 (heel) | 85 min | 1 per veld |
| JO16, JO17, MO17 | 1.00 (heel) | 95 min | 1 per veld |
| G | 0.50 (half) | 75 min | 2 per veld |
| JO18, JO19, JO23, MO19, MO20, VR, 1-99 | 1.00 (heel) | 105 min | 1 per veld |

### Teamspecifieke regels (dbo.TeamRegels)

| Team | Regel | Waarde |
|------|------|-------|
| VRC 1 | BufferVoor | 60 min voor wedstrijd, geen andere wedstrijden op hetzelfde veld |
| VRC 1 | BufferNa | 30 min na wedstrijd, geen andere wedstrijden op hetzelfde veld |

---

## curl Voorbeelden

### Beschikbaarheid controleren voor JO13 op zaterdag

```bash
curl -X POST http://localhost:7094/api/planner/check-availability \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-04-25","aanvangsTijd":"12:00","leeftijdsCategorie":"JO13"}'
```

### Maandagavond beschikbaarheid controleren (zonder categorie)

```bash
curl -X POST http://localhost:7094/api/planner/check-availability \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-05-18","dagdeel":"avond"}'
```

### Controleren met teamconflictdetectie

```bash
curl -X POST http://localhost:7094/api/planner/check-availability \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-05-16","aanvangsTijd":"12:00","leeftijdsCategorie":"JO11","teamNaam":"VRC JO11-9"}'
```

### Wedstrijd boeken

```bash
curl -X POST http://localhost:7094/api/planner/bevestig \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-04-25","aanvangsTijd":"12:00","veldNummer":3,"leeftijdsCategorie":"JO13","teamNaam":"VRC JO13-1","tegenstander":"Ede JO13-2","aangevraagdDoor":"coach@vrc.nl"}'
```

### Zonsondergangtabel vullen

```bash
curl -X POST http://localhost:7094/api/planner/populate-sunset
```

### Bestaande wedstrijd zoeken

```bash
curl -X POST http://localhost:7094/api/planner/zoek-wedstrijd \
  -H "Content-Type: application/json" \
  -d '{"teamNaam":"VRC JO8-2","datum":"2026-05-09"}'
```

### Herplan-alternatieven controleren (simulatie)

```bash
curl -X POST http://localhost:7094/api/planner/herplan-check \
  -H "Content-Type: application/json" \
  -d '{"wedstrijdcode":12345678,"voorkeurTijd":"10:00","dagdeel":"ochtend"}'
```

### Herplanverzoek registreren

```bash
curl -X POST http://localhost:7094/api/planner/herplan-bevestig \
  -H "Content-Type: application/json" \
  -d '{"wedstrijdcode":12345678,"gewensteAanvangsTijd":"10:00","gewenstVeldNummer":2,"aangevraagdDoor":"tegenstander via email","opmerking":"Tijdstip is niet haalbaar"}'
```

### Planning optimaliseren (browser-versie)

```bash
curl -X POST "http://localhost:7094/api/planner/optimaliseer?format=html" \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-04-18"}' > optimalisatie.html
```

### Planning optimaliseren (email-versie)

```bash
curl -X POST "http://localhost:7094/api/planner/optimaliseer?format=email" \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-04-18"}'
```

### Handmatige Sportlink synchronisatie

```bash
curl http://localhost:7094/api/sync-matches
curl "http://localhost:7094/api/sync-matches?reset=true&season=2025"
```
