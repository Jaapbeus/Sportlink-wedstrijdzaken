# Sportlink API Documentatie

**Basis-URL:** `http://localhost:7094/api`

## Beveiliging

Twee beveiligingsniveaus:

| Niveau | Sleutel | Wie | Endpoints |
|--------|---------|-----|-----------|
| **Function** | Function key (`?code=`) | Automate, integraties | Alle planner endpoints |
| **Admin** | Easy Auth Bearer + admin-rol | Alleen coördinator | Alle `/api/beheer/*` endpoints |
| **Admin+User** | Easy Auth Bearer + admin- of user-rol | Coördinator + club-gebruikers | `/api/beheer/teambegeleiding/*` |

Zonder geldige sleutel → 401 Unauthorized (kost niets, geen verwerking).

---

## Overzicht endpoints

| Methode | Endpoint | Niveau | Beschrijving |
|---------|----------|--------|-------------|
| `GET` | `/health` | Anoniem | Status, versie, tier-herkomst (#863) — zie hieronder |
| `GET` | `/sync-matches` | **Admin** | Handmatige Sportlink data synchronisatie |
| `POST` | `/planner/check-availability` | Function | Veldbeschikbaarheid controleren — gescoped op `X-Club-Code` header |
| `POST` | `/planner/bevestig` | Function | Wedstrijdslot boeken |
| `POST` | `/planner/populate-sunset` | **Admin** | Zonsondergangtabel vullen |
| `POST` | `/planner/zoek-wedstrijd` | Function | Bestaande wedstrijd zoeken — gescoped op `X-Club-Code` header |
| `POST` | `/planner/herplan-check` | Function | Herplan-alternatieven simuleren — gescoped op `X-Club-Code` header |
| `POST` | `/planner/herplan-bevestig` | Function | Herplanverzoek registreren |
| `POST` | `/planner/auto-plan` | Easy Auth (admin) | **Dagplanning optimaliseren** — regels → voorkeurstijden → leeftijdsdefaults |
| `POST` | `/planner/auto-plan/toepassen` | Easy Auth (admin) | Berekende planning wegschrijven (alleen testmodus ALLSTARS) |
| `GET` | `/planner/veldbezetting?datum=` | Easy Auth (admin) | Wedstrijden op een datum, zonder optimalisatie-berekening |
| `GET` | `/beheer/teambegeleiding` | **Admin+User** | Alle teams met begeleiding in database |
| `GET` | `/beheer/teambegeleiding/{team}` | **Admin+User** | Begeleiders van team (naam + rol, nooit e-mail) |
| `POST` | `/beheer/teambegeleiding/doorsturen` | **Admin+User** | Vraag doorsturen (BCC coördinator). `ontvangers` bepaalt de ontvangers (max 15, gevalideerd, uitsluitingslijst gecontroleerd); leeg → server-side coach-lookup (#765) |
| `POST` | `/beheer/teambegeleiding/import` | **Admin** | CSV-import van begeleiders (vervangt de rijen van de club). CSV wordt in-memory verwerkt en nooit opgeslagen; `avg.ImportLog` bevat alleen metadata — geen PII |
| `GET/POST/PUT/DELETE` | `/beheer/speeltijden` en `/{leeftijd}` | **Admin** | Speeltijden per leeftijdscategorie beheren |
| `GET` | `/beheer/leermomenten` | **Admin** | Classificatie-leermomenten ophalen (`?status=pending\|validated\|rejected`) |
| `GET` | `/beheer/leermomenten/stats` | **Admin** | Aantallen leermomenten per status |
| `PUT` | `/beheer/leermomenten/{id}/valideer` | **Admin** | Leermoment valideren of afwijzen (`{ "actie": "valideer"\|"afwijzen" }`) |
| `GET` | `/beheer/teamaliassen` | **Admin** | Teamnaam-aliassen ophalen (`?status=pending\|validated\|rejected&limit=100`) — inclusief canonieke teamnaam |
| `POST` | `/beheer/teams/herstel` | **Admin** | Canonieke teamlijst opnieuw opbouwen uit `his.Teams`: volledige canonicalisatie + sleutelmigratie (#766). Idempotent. `409` als er nog niets gesynchroniseerd is — "niets te doen" is bewust geen `200` (#946) |
| `PUT` | `/beheer/teamaliassen/{id}/valideer` | **Admin** | Alias goedkeuren of afwijzen (`{ "status": "validated"\|"rejected" }`) |
| `DELETE` | `/beheer/teamaliassen/{id}` | **Admin** | Alias definitief verwijderen |
| `GET` | `/beheer/theme` | **Admin** | Club-thema ophalen (kleuren + website-URL) — gefilterd op `X-Club-Code` header |
| `PUT` | `/beheer/theme` | **Admin** | Club-thema opslaan (`{ primary, secondary, accent, textOnPrimary, clubWebsiteUrl }`) — gefilterd op `X-Club-Code` header |
| `POST` | `/beheer/theme/extract?url=` | **Admin** | Kleuren extraheren uit club-website (SSRF-beschermd) |
| `GET` | `/beheer/clubs` | **Admin** | Lijst van beschikbare clubs (`[{ clubCode, clubName }]`) voor de GUI-selector |
| `GET/POST/PUT` | `/beheer/velden` en `/{veldNummer}` | **Admin** | Velden beheren: naam, type (vrije tekst), kunstlicht, actief — per club vrij instelbaar (#679) |
| `GET/POST/PUT/DELETE` | `/beheer/veldbeschikbaarheid` en `/{id}` | **Admin** | Openingsvenster per veld per weekdag beheren, optioneel gekoppeld aan een periode (`PeriodeId`, #581) |
| `GET/POST/PUT/DELETE` | `/beheer/veldtraining` en `/{id}` | **Admin** | Terugkerende trainingsbezetting per veld per weekdag — telt mee als bezetting in planner en e-mailreacties (#679) |
| `GET/POST/PUT/DELETE` | `/beheer/veldperiodes` en `/{id}` | **Admin** | Herbruikbare regimes (bijv. "Zomerstop", "Competitie") met een geldigheidsrange; koppel een veldbeschikbaarheid-venster eraan om het alleen tijdens die periode te laten gelden (#581) |
| `GET` | `/beheer/testdata/wedstrijden` | **Admin** | Test-wedstrijden ophalen (`ClubCode='ALLSTARS'`) — altijd leeg voor echte clubs |
| `GET` | `/beheer/testdata/teams` | **Admin** | Echte clubteams ophalen voor testdata-dropdown (filtert `ClubCode!='ALLSTARS'`) |
| `POST` | `/beheer/testdata/wedstrijden` | **Admin** | Test-wedstrijd aanmaken of bijwerken (upsert op `bk_matches`) — forceert `ClubCode='ALLSTARS'` |
| `DELETE` | `/beheer/testdata/wedstrijden/{bk}` | **Admin** | Één test-wedstrijd verwijderen op `bk_matches` |
| `DELETE` | `/beheer/testdata/wedstrijden?van=YYYY-MM-DD&tot=YYYY-MM-DD` | **Admin** | Test-wedstrijden verwijderen voor datumbereik (beide params optioneel; zonder params: alles verwijderen) |
| `POST` | `/beheer/testdata/wedstrijden/verplaats-datum` | **Admin** | Alle ALLSTARS-wedstrijden van `oudeDatum` naar `nieuweDatum` verplaatsen — raakt uitsluitend `ClubCode='ALLSTARS'` |

---

## GET /api/health

Status, versie en databaseherkomst van de API. Geen authenticatie vereist.

`tier` en `provider` komen uit build-time metadata van het gebouwde artefact — nooit een
runtime-gok — en zijn daarom altijd gevuld, ook als `database` niet `"online"` is. `serverVersion`
komt aantoonbaar uit de database zelf en is alleen gevuld wanneer `database` `"online"` is (#863).

### Antwoord

```json
{
  "status": "ok",
  "version": "3.0.9.0",
  "timestamp": "2026-08-30T15:00:00Z",
  "database": "online",
  "tier": "SqlServer",
  "provider": "Microsoft.Data.SqlClient",
  "serverVersion": "16.0.4265.3"
}
```

| Veld | Type | Beschrijving |
|---|---|---|
| `status` | `string` | `"ok"` als `database` `"online"` is, anders `"degraded"` |
| `database` | `string` | `online`, `paused`, `timeout`, `unavailable` of `unconfigured` |
| `tier` | `string` | De databasetier waarmee dit artefact gebouwd is — zie `scripts/ci/database-tiers.json` |
| `provider` | `string` | De gebruikte databasedriver |
| `serverVersion` | `string \| null` | Versienummer van de databaseserver zelf; `null` als niet bereikbaar |

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

> **Clubscope (#573, #580):** de optionele header `X-Club-Code` bepaalt welke club wordt
> doorzocht. Zonder header valt het endpoint terug op de primaire club van deze deployment.
> Wedstrijden, bezetting, velden, speeltijden en teamregels van andere clubs (inclusief de
> `ALLSTARS`-demodata) worden nooit meegenomen. Dit geldt ook voor
> `/planner/zoek-wedstrijd`, `/planner/herplan-check`, `/planner/doordeweeks-beschikbaar`
> en `/planner/team-schedule`.

### Aanvraag

```json
{
  "datum": "2026-04-18",
  "aanvangsTijd": "12:00",
  "dagdeel": null,
  "leeftijdsCategorie": "JO13",
  "teamNaam": "[ClubCode] JO13-1",
  "tegenstander": "[Tegenstander] JO13-2",
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
    "veldType": "kunstgras",
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
      "veldType": "kunstgras",
      "veldDeelGebruik": 1.0,
      "wedstrijdDuurMinuten": 75
    },
    {
      "datum": "2026-04-11",
      "aanvangsTijd": "18:00",
      "eindTijd": "19:15",
      "veldNummer": 1,
      "veldNaam": "veld 1",
      "veldType": "kunstgras",
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
      "veldType": "natuurgras",
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
    "wedstrijd": "[ClubCode] JO11-9JM - [Tegenstander] JO11-3",
    "aanvangsTijd": "11:30",
    "eindTijd": "12:45",
    "veldNaam": "veld 4"
  },
  "reden": "[ClubCode] JO11-9 heeft al een wedstrijd op 16 mei: [ClubCode] JO11-9JM - [Tegenstander] JO11-3 om 11:30 (veld 4).",
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
  "teamNaam": "[ClubCode] JO13-1",
  "tegenstander": "[Tegenstander] JO13-2",
  "aangevraagdDoor": "trainer@voorbeeld.nl",
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

Vul de zonsondergangtabel met NOAA-berekende tijden voor de clublocatie. Eenmalig uitvoeren na initiële setup, of wanneer het seizoen/datumbereik wordt uitgebreid.

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
  "teamNaam": "[ClubCode] JO8-2",
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
    "wedstrijd": "[ClubCode] JO8-2 - Tegenstander JO8-1",
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
  "reden": "Geen wedstrijd gevonden voor [ClubCode] JO8-2 op 2026-05-09."
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
    "wedstrijd": "[ClubCode] JO8-2 - Tegenstander JO8-1",
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
  "huidigeWedstrijd": "[ClubCode] JO8-2 - Tegenstander JO8-1",
  "gewensteAanvangsTijd": "10:00",
  "gewenstVeldNummer": 2,
  "status": "Aangevraagd"
}
```

---

## POST /api/planner/auto-plan

Berekent de optimale dagplanning voor één datum en geeft per wedstrijd het voorgestelde veld en tijdslot
terug. Voert **niets** door — `/planner/auto-plan/toepassen` schrijft de planning weg (alleen testmodus).

Sinds #666 is dit de enige dagplanning-optimalisatie.

### Aanvraag

```json
{ "datum": "2026-08-22", "bufferMinuten": 15 }
```

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `datum` | `string` | **Ja** | Datum in `yyyy-MM-dd` formaat |
| `bufferMinuten` | `integer` | Nee | Buffer tussen wedstrijden. Standaard 15. Teamspecifieke buffers uit `dbo.TeamRegels` gaan vóór als die groter zijn |

### Rangorde van het planningsdoel

Per wedstrijd wordt de streeftijd bepaald in deze vaste volgorde:

| Laag | Bron | `voorkeurBron` |
|---|---|---|
| 0 | `dbo.TeamRegels`, `RegelType = 'VoorkeurVeld'` (veld + optioneel tijd) | `regel` |
| 1 | `dbo.TeamVoorkeurTijden` voor de betreffende dag van de week | `team` |
| 2 | `dbo.Speeltijden.StandaardVoorkeurTijd` van de leeftijdscategorie | `leeftijd` |
| 3 | geen streeftijd → eerst beschikbare slot | `null` |

Binnen elke laag beslist `Prioriteit` oplopend (**laag getal = belangrijker**) welk team zijn plek als
eerste claimt. `BufferVoor`/`BufferNa` zijn geen laag maar gelden altijd.

### Antwoord — JSON (200)

```json
{
  "datum": "2026-08-22",
  "totaalWedstrijden": 14,
  "zonderVeld": 0,
  "zonderTijd": 0,
  "teWijzigen": 12,
  "nietInplanbaar": 0,
  "geschatteEindTijd": "16:25",
  "wedstrijden": [
    {
      "wedstrijdCode": 12345678,
      "wedstrijd": "[Team] - [Tegenstander]",
      "teamNaam": "[Team]",
      "leeftijdsCategorie": "JO15",
      "duurMinuten": 85,
      "veldafmeting": 1.00,
      "huidigeVeld": "veld 2",
      "huidigeTijd": "11:00",
      "optimaalVeld": "veld 3",
      "optimaalTijd": "11:15",
      "status": "wijziging",
      "voorkeurTijd": "11:00",
      "voorkeurAfwijkingMinuten": 15,
      "voorkeurBron": "leeftijd",
      "voorkeurStatus": "kleine-afwijking",
      "voorkeurVeldNummer": null,
      "voorkeurVeldToegepast": null
    }
  ],
  "huidigeHtml": "<html>...</html>",
  "optimaleHtml": "<html>...</html>"
}
```

### Twee gescheiden statussen

| Veld | Waarden | Betekenis |
|---|---|---|
| `status` | `ongewijzigd`, `wijziging`, `nieuw-slot`, `niet-inplanbaar`, `onbekend-team` | Verplaatst de planner deze wedstrijd t.o.v. de huidige stand? |
| `voorkeurStatus` | `op-tijd`, `kleine-afwijking` (≤15 min), `grote-afwijking` (>15 min), `geen-voorkeur` | Staat de wedstrijd op de gewenste tijd? |

Deze twee zijn bewust gescheiden: een wedstrijd kan ongewijzigd blijven én tóch ver van de voorkeurstijd
liggen. Vóór #666 kwam de groene "OK"-badge uit `status == "ongewijzigd"`, waardoor een afwijking van 60
minuten als "OK" werd gepresenteerd.

`voorkeurVeldToegepast` is `false` als er een voorkeursveld was maar de planner een ander veld moest
kiezen; `null` als er geen voorkeursveld-regel is.

---

## POST /api/planner/optimaliseer — VERVALLEN (#666)

Dit endpoint bestaat niet meer. Gebruik `POST /api/planner/auto-plan` hierboven.

Het oude endpoint negeerde voorkeurstijden en prioriteiten volledig, waardoor twee knoppen in de Admin
GUI verschillende planningen opleverden. De HTML-weergaven zitten nu in de auto-plan-response
(`huidigeHtml` / `optimaleHtml`).

---

## GET /api/planner/veldbezetting

Geeft de wedstrijden terug die op een datum al gepland staan, rechtstreeks uit de laatst
gesynchroniseerde Sportlink-data — **zonder** de scheduling-optimalisatie te draaien die
`/planner/auto-plan` uitvoert. Bedoeld als snelle, goedkope
"wat staat er nu al gepland"-weergave (zie Dagplanning in de Admin GUI).

### Query-parameters

| Parameter | Type | Verplicht | Beschrijving |
|-----------|------|-----------|-------------|
| `datum` | `string` | **Ja** | Datum in `yyyy-MM-dd` formaat |

### Antwoord — JSON (200)

```json
[
  {
    "wedstrijdCode": 20672784,
    "wedstrijd": "[ClubCode] 6 - Tegenstander 8",
    "teamNaam": "[ClubCode] 6",
    "uitteam": "Tegenstander 8",
    "aanvangsTijd": "13:00",
    "veld": "veld 3",
    "competitiesoort": "Oefenwedstrijd",
    "leeftijdsCategorie": null,
    "duurMinuten": 90,
    "veldafmeting": 1.00
  }
]
```

Resultaat is gesorteerd op `aanvangsTijd`. Wedstrijden zonder aanvangstijd staan achteraan.

---

## Beheer — Teamaliassen

Aliassen zijn afwijkende schrijfwijzen van een teamnaam (bijvoorbeeld `13-1` in plaats van
`JO13-1`). Ze worden vastgelegd in `dbo.TeamAliassen` met status `pending`. Alleen een alias met
status `validated` mag bij teamnaam-resolutie als vertrouwde exacte match gelden — een foutieve
AI-keuze kan zich zo niet zelfversterken. Alles is gescoped op de club uit de `X-Club-Code` header.

### GET /api/beheer/teamaliassen

| Parameter | Type | Verplicht | Beschrijving |
|-----------|------|-----------|-------------|
| `status` | `string` | Nee | `pending`, `validated` of `rejected`. Leeg = alle statussen |
| `limit` | `integer` | Nee | Max. aantal rijen (default 100, max 500) |

```bash
curl "http://localhost:7094/api/beheer/teamaliassen?status=pending&limit=50"
```

```json
{
  "count": 1,
  "limit": 50,
  "pending": 1,
  "validated": 4,
  "rejected": 0,
  "items": [
    {
      "id": 12,
      "ruweTekst": "13-1",
      "ruweTekstGenormaliseerd": "131",
      "teamId": 7,
      "teamnaam": "[ClubCode] JO13-1",
      "leeftijdsCategorie": "JO13",
      "bron": "AiDisambiguatie",
      "status": "pending",
      "aantalKeerGebruikt": 3,
      "mtaInserted": "2026-07-26T09:12:00Z",
      "mtaModified": "2026-07-27T07:03:00Z"
    }
  ]
}
```

`pending`/`validated`/`rejected` zijn de totalen per status voor de hele club — onafhankelijk van
het `status`-filter en de `limit`. Datums zijn UTC (`Z`-suffix); de GUI toont ze in lokale tijd.
Bestaat de tabel nog niet (post-deployment script niet uitgevoerd), dan volgt een lege lijst
met nullen in plaats van een fout.

### PUT /api/beheer/teamaliassen/{id}/valideer

```bash
curl -X PUT http://localhost:7094/api/beheer/teamaliassen/12/valideer -H "Content-Type: application/json" -d '{"status":"validated"}'
```

```json
{ "id": 12, "status": "validated" }
```

Alleen `validated` of `rejected` zijn toegestaan → anders `400`. Onbekende id (of een id van een
andere club) → `404`.

### DELETE /api/beheer/teamaliassen/{id}

```bash
curl -X DELETE http://localhost:7094/api/beheer/teamaliassen/12
```

```json
{ "deleted": true, "id": 12 }
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
| [Heren 1] | BufferVoor | 60 min voor wedstrijd, geen andere wedstrijden op hetzelfde veld |
| [Heren 1] | BufferNa | 30 min na wedstrijd, geen andere wedstrijden op hetzelfde veld |

---

## curl Voorbeelden

### Beschikbaarheid controleren voor JO13 op zaterdag

```bash
curl -X POST http://localhost:7094/api/planner/check-availability -H "Content-Type: application/json" -d '{"datum":"2026-04-25","aanvangsTijd":"12:00","leeftijdsCategorie":"JO13"}'
```

### Maandagavond beschikbaarheid controleren (zonder categorie)

```bash
curl -X POST http://localhost:7094/api/planner/check-availability -H "Content-Type: application/json" -d '{"datum":"2026-05-18","dagdeel":"avond"}'
```

### Controleren met teamconflictdetectie

```bash
curl -X POST http://localhost:7094/api/planner/check-availability -H "Content-Type: application/json" -d '{"datum":"2026-05-16","aanvangsTijd":"12:00","leeftijdsCategorie":"JO11","teamNaam":"[ClubCode] JO11-9"}'
```

### Wedstrijd boeken

```bash
curl -X POST http://localhost:7094/api/planner/bevestig -H "Content-Type: application/json" -d '{"datum":"2026-04-25","aanvangsTijd":"12:00","veldNummer":3,"leeftijdsCategorie":"JO13","teamNaam":"[ClubCode] JO13-1","tegenstander":"[Tegenstander] JO13-2","aangevraagdDoor":"trainer@voorbeeld.nl"}'
```

### Zonsondergangtabel vullen

```bash
curl -X POST http://localhost:7094/api/planner/populate-sunset
```

### Bestaande wedstrijd zoeken

```bash
curl -X POST http://localhost:7094/api/planner/zoek-wedstrijd -H "Content-Type: application/json" -d '{"teamNaam":"[ClubCode] JO8-2","datum":"2026-05-09"}'
```

### Herplan-alternatieven controleren (simulatie)

```bash
curl -X POST http://localhost:7094/api/planner/herplan-check -H "Content-Type: application/json" -d '{"wedstrijdcode":12345678,"voorkeurTijd":"10:00","dagdeel":"ochtend"}'
```

### Herplanverzoek registreren

```bash
curl -X POST http://localhost:7094/api/planner/herplan-bevestig -H "Content-Type: application/json" -d '{"wedstrijdcode":12345678,"gewensteAanvangsTijd":"10:00","gewenstVeldNummer":2,"aangevraagdDoor":"tegenstander via email","opmerking":"Tijdstip is niet haalbaar"}'
```

### Dagplanning optimaliseren

```bash
curl -X POST http://localhost:7094/api/planner/auto-plan -H "Content-Type: application/json" -d '{"datum":"2026-04-18","bufferMinuten":15}'
```

De response bevat per wedstrijd het optimale veld en tijdslot, plus `voorkeurTijd`,
`voorkeurAfwijkingMinuten`, `voorkeurBron` en `voorkeurStatus`. De HTML-weergaven zitten in
`huidigeHtml` en `optimaleHtml` — die kun je direct als e-mail versturen of naar een bestand schrijven.

### Handmatige Sportlink synchronisatie

```bash
curl http://localhost:7094/api/sync-matches
curl "http://localhost:7094/api/sync-matches?reset=true&season=2025"
```
