# VRC Sportlink API Documentatie

**Basis-URL:** `http://localhost:7094/api`

Alle endpoints vereisen een functiesleutel via `x-functions-key` header of `?code=` queryparameter (behalve lokale ontwikkeling).

---

## Overzicht endpoints

| Methode | Endpoint | Beschrijving |
|---------|----------|-------------|
| `GET` | `/sync-matches` | Handmatige Sportlink data synchronisatie |
| `POST` | `/planner/check-availability` | Veldbeschikbaarheid controleren |
| `POST` | `/planner/bevestig` | Wedstrijdslot boeken |
| `POST` | `/planner/populate-sunset` | Zonsondergangtabel vullen |

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

### Handmatige Sportlink synchronisatie

```bash
curl http://localhost:7094/api/sync-matches
curl "http://localhost:7094/api/sync-matches?reset=true&season=2025"
```
