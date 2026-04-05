# VRC Sportlink API Documentation

**Base URL:** `http://localhost:7094/api`

All endpoints require a function key via `x-functions-key` header or `?code=` query parameter (except local development).

---

## Endpoints Overview

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/sync-matches` | Manual Sportlink data sync |
| `POST` | `/planner/check-availability` | Check field availability |
| `POST` | `/planner/bevestig` | Book a match slot |
| `POST` | `/planner/populate-sunset` | Populate sunset lookup table |
| `POST` | `/planner/zoek-wedstrijd` | Search for an existing match |
| `POST` | `/planner/herplan-check` | Simulate reschedule alternatives |
| `POST` | `/planner/herplan-bevestig` | Register a reschedule request |

---

## GET /api/sync-matches

Manually trigger a Sportlink API sync (teams, matches, match details).

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `reset` | `boolean` | No | `true` = full season sync instead of incremental |
| `season` | `integer` | No | Season start year (e.g. `2024`). Used with `reset=true` |

### Example

```
GET /api/sync-matches
GET /api/sync-matches?reset=true&season=2025
```

### Response

```
200 OK
```

---

## POST /api/planner/check-availability

Check if a field is available for a friendly/practice match. Returns a specific slot assignment, available time windows, or a team conflict.

### Request Body

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

### Request Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `datum` | `string` | **Yes** | Date in `yyyy-MM-dd` format |
| `aanvangsTijd` | `string` | No | Preferred kick-off time `HH:mm`. Omit to find best slot |
| `dagdeel` | `string` | No | Time-of-day filter: `"ochtend"`, `"middag"`, or `"avond"` |
| `leeftijdsCategorie` | `string` | No | Age category (e.g. `JO11`, `MO17`, `VR`, `1-99`). Determines match duration and field size. Omit to get available windows |
| `teamNaam` | `string` | No | Team name for conflict check and team-specific rules |
| `tegenstander` | `string` | No | Opponent name (for records only) |
| `wedstrijdDuurMinuten` | `integer` | No | Override match duration in minutes (default from Speeltijden) |

### Response — Slot Assigned (200)

When `leeftijdsCategorie` is provided and a slot is available:

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

### Response — Unavailable with Alternatives (200)

When the requested time is not available:

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

### Response — Available Windows (200)

When `leeftijdsCategorie` is omitted — returns open time windows per field:

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

### Response — Team Conflict (200)

When the team already has a match on the requested date:

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

### Response — No Matches Allowed (200)

When the requested day does not allow matches (Friday/Sunday):

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

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| `beschikbaar` | `boolean` | Whether a slot is available |
| `toewijzing` | `object\|null` | Assigned slot (Mode 1 only) |
| `teamConflict` | `object\|null` | Existing match for the team on this date |
| `reden` | `string\|null` | Reason when unavailable |
| `alternatieven` | `array` | Up to 3 alternative time slots |
| `beschikbareVensters` | `array\|null` | Available windows per field (Mode 2 only) |
| `waarschuwingen` | `array` | Warnings (sunset margin, weekday restrictions) |

### Error Response (400)

```json
{
  "error": "Request body met 'datum' veld is verplicht."
}
```

---

## POST /api/planner/bevestig

Confirm and book a match slot. Writes to `planner.GeplandeWedstrijden` table.

### Request Body

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

### Request Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `datum` | `string` | **Yes** | Date in `yyyy-MM-dd` format |
| `aanvangsTijd` | `string` | **Yes** | Kick-off time `HH:mm` |
| `veldNummer` | `integer` | **Yes** | Field number (1-5) |
| `leeftijdsCategorie` | `string` | No | Age category for auto duration/field size |
| `teamNaam` | `string` | No | Team name |
| `tegenstander` | `string` | No | Opponent name |
| `aangevraagdDoor` | `string` | No | Who requested (email, phone, name) |
| `wedstrijdDuurMinuten` | `integer` | No | Override match duration (default from Speeltijden or 105) |

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

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| `id` | `integer` | Database ID of the booked match |
| `datum` | `string` | Confirmed date |
| `aanvangsTijd` | `string` | Confirmed kick-off time |
| `eindTijd` | `string` | Calculated end time |
| `veldNummer` | `integer` | Assigned field |
| `status` | `string` | Always `"Gepland"` on creation |

### Error Response (400)

```json
{
  "error": "Request body met 'datum', 'aanvangsTijd' en 'veldNummer' is verplicht."
}
```

---

## POST /api/planner/populate-sunset

Populate the sunset lookup table with NOAA-calculated sunset times for Veenendaal. Run once after initial setup, or when the season/date range expands.

### Request Body

None (empty POST).

### Response (200)

```json
{
  "message": "Sunset data populated from 2026-01-01 to 2027-12-31."
}
```

---

## POST /api/planner/zoek-wedstrijd

Search for an existing competition match by team name and date.

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

## Scheduling Rules Reference

### Field Availability

| Day | Fields | Hours | Notes |
|-----|--------|-------|-------|
| Monday-Thursday | Veld 5 only | 18:00 - sunset | No lights, veld 1-4 training |
| Friday | None | - | No matches |
| Saturday | Veld 1-5 | 08:30 - 22:00 (1-4) / 08:30 - 17:00 (5) | 10 min buffer |
| Sunday | None | - | No matches |

### Field Priority

Veld 1 > Veld 2 > Veld 3 > Veld 4 > Veld 5 (last resort)

### Age Categories (Speeltijden)

| Category | Field Size | Duration | Field Sharing |
|----------|-----------|----------|---------------|
| JO7, JO8, JO9 | 0.25 (quarter) | 50 min | 4 per field |
| JO10 | 0.25 (quarter) | 65 min | 4 per field |
| JO11, JO12 | 0.50 (half) | 75 min | 2 per field |
| JO13, MO13 | 1.00 (full) | 75 min | 1 per field |
| JO14, JO15 | 1.00 (full) | 85 min | 1 per field |
| MO15 | 1.00 (full) | 85 min | 1 per field |
| JO16, JO17, MO17 | 1.00 (full) | 95 min | 1 per field |
| JO18, JO19, JO23, MO19, MO20, VR, 1-99 | 1.00 (full) | 105 min | 1 per field |

### Team-Specific Rules (dbo.TeamRegels)

| Team | Rule | Value |
|------|------|-------|
| VRC 1 | BufferVoor | 60 min before match, no other matches on same field |
| VRC 1 | BufferNa | 30 min after match, no other matches on same field |

---

## curl Examples

### Check availability for JO13 on a Saturday

```bash
curl -X POST http://localhost:7094/api/planner/check-availability \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-04-25","aanvangsTijd":"12:00","leeftijdsCategorie":"JO13"}'
```

### Check Monday evening availability (no category)

```bash
curl -X POST http://localhost:7094/api/planner/check-availability \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-05-18","dagdeel":"avond"}'
```

### Check with team conflict detection

```bash
curl -X POST http://localhost:7094/api/planner/check-availability \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-05-16","aanvangsTijd":"12:00","leeftijdsCategorie":"JO11","teamNaam":"VRC JO11-9"}'
```

### Book a match

```bash
curl -X POST http://localhost:7094/api/planner/bevestig \
  -H "Content-Type: application/json" \
  -d '{"datum":"2026-04-25","aanvangsTijd":"12:00","veldNummer":3,"leeftijdsCategorie":"JO13","teamNaam":"VRC JO13-1","tegenstander":"Ede JO13-2","aangevraagdDoor":"coach@vrc.nl"}'
```

### Populate sunset table

```bash
curl -X POST http://localhost:7094/api/planner/populate-sunset
```

### Search for an existing match

```bash
curl -X POST http://localhost:7094/api/planner/zoek-wedstrijd \
  -H "Content-Type: application/json" \
  -d '{"teamNaam":"VRC JO8-2","datum":"2026-05-09"}'
```

### Check reschedule alternatives (simulation)

```bash
curl -X POST http://localhost:7094/api/planner/herplan-check \
  -H "Content-Type: application/json" \
  -d '{"wedstrijdcode":12345678,"voorkeurTijd":"10:00","dagdeel":"ochtend"}'
```

### Register reschedule request

```bash
curl -X POST http://localhost:7094/api/planner/herplan-bevestig \
  -H "Content-Type: application/json" \
  -d '{"wedstrijdcode":12345678,"gewensteAanvangsTijd":"10:00","gewenstVeldNummer":2,"aangevraagdDoor":"tegenstander via email","opmerking":"08:30 is niet haalbaar"}'
```

### Manual Sportlink sync

```bash
curl http://localhost:7094/api/sync-matches
curl "http://localhost:7094/api/sync-matches?reset=true&season=2025"
```
