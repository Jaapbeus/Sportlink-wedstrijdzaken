# API — Endpoints en Planningsregels

Specificatie voor alle HTTP-endpoints van de VRC Sportlink Wedstrijdzaken API, inclusief beveiliging en planningsregels.

**Bronbestand:** `FunctionApp/docs/API.md`

---

## Requirement: API-beveiliging

Alle endpoints MOETEN beveiligd zijn met Azure Function keys op twee niveaus.

| Niveau | Sleutel | Doelgroep | Endpoints |
|--------|---------|-----------|-----------|
| **Function** | Function key (`?code=`) | Automaties, integraties | Alle planner endpoints |
| **Admin** | Master key (`?code=`) | Alleen coördinator | sync-matches, populate-sunset |

### Scenario: Geen sleutel meegegeven

- **GIVEN** een endpoint met beveiligingsvereiste
- **WHEN** een request binnenkomt zonder geldige `?code=` parameter
- **THEN** MOET het antwoord `401 Unauthorized` zijn
- **AND** MAG er GEEN verwerking plaatsvinden

### Scenario: Geldige function key

- **GIVEN** een planner endpoint
- **WHEN** een request binnenkomt met geldige function key
- **THEN** MOET het request verwerkt worden

### Scenario: Admin endpoint met function key

- **GIVEN** een admin endpoint (sync-matches, populate-sunset)
- **WHEN** een request binnenkomt met alleen een function key (geen master key)
- **THEN** MOET het antwoord `401 Unauthorized` zijn

---

## Requirement: GET /api/sync-matches

Het endpoint MOET handmatig een Sportlink API-synchronisatie starten (teams, wedstrijden, wedstrijddetails).

**Beveiligingsniveau:** Admin

### Scenario: Standaard synchronisatie

- **GIVEN** geldige API-credentials in `dbo.AppSettings`
- **WHEN** `GET /api/sync-matches` wordt aangeroepen
- **THEN** MOETEN teams, wedstrijden en wedstrijddetails gesynchroniseerd worden
- **AND** MOET het antwoord `200 OK` zijn

### Scenario: Volledige seizoensreset

- **GIVEN** geldige API-credentials
- **WHEN** `GET /api/sync-matches?reset=true&season=2025` wordt aangeroepen
- **THEN** MOET een volledige seizoensynchronisatie plaatsvinden in plaats van incrementeel

### Aanvraagvelden

| Parameter | Type | Verplicht | Beschrijving |
|-----------|------|-----------|-------------|
| `reset` | `boolean` | Nee | `true` = volledige seizoensynchronisatie |
| `season` | `integer` | Nee | Startjaar seizoen (bijv. `2024`). Gebruikt met `reset=true` |

---

## Requirement: POST /api/planner/check-availability

Het endpoint MOET veldbeschikbaarheid controleren voor oefenwedstrijden. Het systeem ondersteunt drie modi afhankelijk van de invoerparameters.

**Beveiligingsniveau:** Function

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `datum` | `string` | **Ja** | Datum in `yyyy-MM-dd` formaat |
| `aanvangsTijd` | `string` | Nee | Gewenste aftrapttijd `HH:mm` |
| `dagdeel` | `string` | Nee | `"ochtend"`, `"middag"`, of `"avond"` |
| `leeftijdsCategorie` | `string` | Nee | Bijv. `JO11`, `MO17`, `VR`, `1-99` |
| `teamNaam` | `string` | Nee | Teamnaam voor conflictcontrole |
| `tegenstander` | `string` | Nee | Tegenstander (administratie) |
| `wedstrijdDuurMinuten` | `integer` | Nee | Overschrijf wedstrijdduur |

### Antwoordvelden

| Veld | Type | Beschrijving |
|------|------|-------------|
| `beschikbaar` | `boolean` | Of een slot beschikbaar is |
| `toewijzing` | `object\|null` | Toegewezen slot |
| `teamConflict` | `object\|null` | Bestaande wedstrijd voor het team op deze datum |
| `reden` | `string\|null` | Reden als niet beschikbaar |
| `alternatieven` | `array` | Tot 3 alternatieve tijdsloten |
| `beschikbareVensters` | `array\|null` | Beschikbare vensters per veld |
| `waarschuwingen` | `array` | Waarschuwingen (zonsondergang, beperkingen) |

### Scenario: Slot toegewezen (Modus 1)

- **GIVEN** `leeftijdsCategorie` is opgegeven en een slot is beschikbaar
- **WHEN** een check-availability request binnenkomt
- **THEN** MOET het antwoord `beschikbaar: true` bevatten
- **AND** MOET `toewijzing` een object zijn met `datum`, `aanvangsTijd`, `eindTijd`, `veldNummer`, `veldNaam`, `veldDeelGebruik`, `wedstrijdDuurMinuten`

### Scenario: Niet beschikbaar met alternatieven

- **GIVEN** de gevraagde tijd is niet beschikbaar
- **WHEN** een check-availability request binnenkomt
- **THEN** MOET het antwoord `beschikbaar: false` bevatten
- **AND** MOET `reden` een beschrijving bevatten
- **AND** MOET `alternatieven` tot 3 alternatieve tijdsloten bevatten

### Scenario: Beschikbare vensters (Modus 2)

- **GIVEN** `leeftijdsCategorie` is NIET opgegeven
- **WHEN** een check-availability request binnenkomt
- **THEN** MOET `beschikbareVensters` open tijdvensters per veld bevatten
- **AND** MOET elk venster `veldNummer`, `veldNaam`, `van`, `tot`, `maxDuurMinuten` bevatten

### Scenario: Teamconflict

- **GIVEN** `teamNaam` is opgegeven en het team heeft al een wedstrijd op die datum
- **WHEN** een check-availability request binnenkomt
- **THEN** MOET `beschikbaar: false` zijn
- **AND** MOET `teamConflict` de bestaande wedstrijdgegevens bevatten

### Scenario: Geen wedstrijden toegestaan

- **GIVEN** de gevraagde datum valt op vrijdag of zondag
- **WHEN** een check-availability request binnenkomt
- **THEN** MOET `beschikbaar: false` zijn
- **AND** MOET `reden` bevatten: `"Geen wedstrijden mogelijk op <dag>."`

### Scenario: Ontbrekend verplicht veld

- **GIVEN** een request zonder `datum` veld
- **WHEN** het request binnenkomt
- **THEN** MOET het antwoord `400 Bad Request` zijn
- **AND** MOET de foutmelding zijn: `"Request body met 'datum' veld is verplicht."`

---

## Requirement: POST /api/planner/bevestig

Het endpoint MOET een wedstrijdslot boeken door te schrijven naar `planner.GeplandeWedstrijden`.

**Beveiligingsniveau:** Function

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `datum` | `string` | **Ja** | Datum `yyyy-MM-dd` |
| `aanvangsTijd` | `string` | **Ja** | Aftrapttijd `HH:mm` |
| `veldNummer` | `integer` | **Ja** | Veldnummer (1-5) |
| `leeftijdsCategorie` | `string` | Nee | Voor automatische duur/veldgrootte |
| `teamNaam` | `string` | Nee | Teamnaam |
| `tegenstander` | `string` | Nee | Tegenstander |
| `aangevraagdDoor` | `string` | Nee | Aanvrager |
| `wedstrijdDuurMinuten` | `integer` | Nee | Overschrijf duur (standaard uit Speeltijden of 105) |

### Scenario: Succesvolle boeking

- **GIVEN** geldige invoergegevens met `datum`, `aanvangsTijd` en `veldNummer`
- **WHEN** een bevestig-request binnenkomt
- **THEN** MOET een rij aangemaakt worden in `planner.GeplandeWedstrijden`
- **AND** MOET het antwoord `200 OK` bevatten met `id`, `datum`, `aanvangsTijd`, `eindTijd`, `veldNummer`, `status`
- **AND** MOET `status` altijd `"Gepland"` zijn bij aanmaak

### Scenario: Ontbrekende verplichte velden

- **GIVEN** een request zonder `datum`, `aanvangsTijd` of `veldNummer`
- **WHEN** het request binnenkomt
- **THEN** MOET het antwoord `400 Bad Request` zijn
- **AND** MOET de foutmelding zijn: `"Request body met 'datum', 'aanvangsTijd' en 'veldNummer' is verplicht."`

---

## Requirement: POST /api/planner/populate-sunset

Het endpoint MOET de zonsondergangtabel vullen met NOAA-berekende tijden voor Veenendaal.

**Beveiligingsniveau:** Admin

### Scenario: Tabel vullen

- **GIVEN** een lege POST-aanvraag
- **WHEN** het endpoint wordt aangeroepen
- **THEN** MOET de zonsondergangtabel gevuld worden voor het volledige datumbereik
- **AND** MOET het antwoord bevatten: `"Sunset data populated from <start> to <eind>."`

---

## Requirement: POST /api/planner/zoek-wedstrijd

Het endpoint MOET een bestaande competitiewedstrijd vinden op basis van teamnaam en datum.

**Beveiligingsniveau:** Function

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `teamNaam` | `string` | **Ja** | Teamnaam (gedeeltelijke match ondersteund) |
| `datum` | `string` | **Ja** | Datum `yyyy-MM-dd` |

### Scenario: Wedstrijd gevonden

- **GIVEN** een team met een wedstrijd op de opgegeven datum
- **WHEN** een zoek-wedstrijd request binnenkomt
- **THEN** MOET `gevonden: true` zijn
- **AND** MOET `wedstrijd` een object bevatten met `wedstrijdcode`, `wedstrijd`, `datum`, `aanvangsTijd`, `eindTijd`, `veldNaam`, `leeftijdsCategorie`, `duurMinuten`, `veldDeelGebruik`

### Scenario: Wedstrijd niet gevonden

- **GIVEN** geen wedstrijd voor het team op de datum
- **WHEN** een zoek-wedstrijd request binnenkomt
- **THEN** MOET `gevonden: false` zijn
- **AND** MOET `reden` een beschrijving bevatten

---

## Requirement: POST /api/planner/herplan-check

Het endpoint MOET herplan-alternatieven simuleren voor een bestaande wedstrijd. Het WIJZIGT NIETS — puur een berekening waarbij het huidige slot als vrij wordt beschouwd.

**Beveiligingsniveau:** Function

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `wedstrijdcode` | `integer` | **Ja** | Wedstrijdcode uit zoek-wedstrijd |
| `voorkeurTijd` | `string` | Nee | Gewenste nieuwe tijd `HH:mm` |
| `dagdeel` | `string` | Nee | `"ochtend"`, `"middag"`, of `"avond"` |

### Scenario: Alternatieven beschikbaar

- **GIVEN** een bestaande wedstrijd met `wedstrijdcode`
- **WHEN** een herplan-check request binnenkomt
- **THEN** MOET `huidigeWedstrijd` de details van de huidige wedstrijd bevatten
- **AND** MOET `beschikbaar: true` zijn als alternatieven gevonden worden
- **AND** MOET `alternatieven` de beschikbare slots bevatten

### Scenario: Geen alternatieven

- **GIVEN** een bestaande wedstrijd
- **WHEN** geen alternatieve slots beschikbaar zijn
- **THEN** MOET `beschikbaar: false` zijn
- **AND** MOET `alternatieven` een lege array zijn
- **AND** MOET `reden` een beschrijving bevatten

---

## Requirement: POST /api/planner/herplan-bevestig

Het endpoint MOET een herplanverzoek registreren. Het WIJZIGT de wedstrijd NIET — het registreert alleen het verzoek met status `"Aangevraagd"`.

**Beveiligingsniveau:** Function

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `wedstrijdcode` | `integer` | **Ja** | Wedstrijdcode |
| `gewensteAanvangsTijd` | `string` | **Ja** | Gewenste nieuwe tijd `HH:mm` |
| `gewenstVeldNummer` | `integer` | Nee | Gewenst veldnummer |
| `aangevraagdDoor` | `string` | Nee | Aanvrager |
| `opmerking` | `string` | Nee | Reden/opmerkingen |

### Scenario: Verzoek registreren

- **GIVEN** geldige invoer met `wedstrijdcode` en `gewensteAanvangsTijd`
- **WHEN** een herplan-bevestig request binnenkomt
- **THEN** MOET het verzoek opgeslagen worden
- **AND** MOET het antwoord bevatten: `id`, `wedstrijdcode`, `huidigeWedstrijd`, `gewensteAanvangsTijd`, `gewenstVeldNummer`, `status`
- **AND** MOET `status` altijd `"Aangevraagd"` zijn

---

## Requirement: POST /api/planner/optimaliseer

Het endpoint MOET de planning voor een datum analyseren en optimalisatiesuggesties genereren. Het voert NIETS door — alleen advies.

**Beveiligingsniveau:** Function

### Aanvraagvelden

| Veld | Type | Verplicht | Beschrijving |
|------|------|-----------|-------------|
| `datum` | `string` | **Ja** | Datum `yyyy-MM-dd` |
| `doel` | `string` | Nee | Optimalisatiedoel (zie tabel) |
| `gewensteEindtijd` | `string` | Nee | Gewenste eindtijd `HH:mm` (standaard `16:15`) |
| `bufferMinuten` | `integer` | Nee | Buffer tussen wedstrijden (standaard 15) |

### Optimalisatiedoelen

| Doel | Beschrijving |
|------|-------------|
| *(leeg)* | Standaard: veld 5 ontlasten + strakker plannen gecombineerd |
| `veld5-ontlasten` | Verplaats wedstrijden van veld 5 naar kunstgras, alleen als eerder kan |
| `strakker-plannen` | Minimaliseer gaten tussen wedstrijden op alle velden |

### Output-formaten (via querystring `?format=`)

| Format | URL | Beschrijving |
|--------|-----|-------------|
| JSON | `POST /api/planner/optimaliseer` | Suggesties als JSON |
| Browser | `POST /api/planner/optimaliseer?format=html` | Interactieve HTML met grid |
| Email | `POST /api/planner/optimaliseer?format=email` | Email-compatibele HTML |

### Scenario: JSON-output

- **GIVEN** een datum met geplande wedstrijden
- **WHEN** `POST /api/planner/optimaliseer` wordt aangeroepen (zonder format-parameter)
- **THEN** MOET het antwoord JSON bevatten met `datum`, `huidigeEindtijd`, `aantalVerplaatsingen`, `aantalVanVeld5Verplaatst`, `suggesties[]`
- **AND** MOET elke suggestie bevatten: `wedstrijd`, `huidigVeldNummer`, `huidigVeld`, `huidigeTijd`, `nieuwVeldNummer`, `nieuwVeld`, `nieuweTijd`, `reden`

### Scenario: Browser HTML-output

- **GIVEN** een datum met geplande wedstrijden
- **WHEN** `POST /api/planner/optimaliseer?format=html` wordt aangeroepen
- **THEN** MOET een interactieve HTML-pagina geretourneerd worden
- **AND** MOET de pagina bevatten: donker grid met tijdlijn, kleurcodes (grijs/blauw/oranje), hover-interactie, chronologisch overzicht

### Scenario: Email HTML-output

- **GIVEN** een datum met geplande wedstrijden
- **WHEN** `POST /api/planner/optimaliseer?format=email` wordt aangeroepen
- **THEN** MOET een versimpelde email-compatibele HTML geretourneerd worden
- **AND** MOET een link "Bekijk in browser" bovenaan staan
- **AND** MOET het werken in alle email-clients (Outlook, Gmail, etc.)

---

## Requirement: Veldbeschikbaarheid per dag

Het systeem MOET veldbeschikbaarheid bepalen op basis van de dag van de week.

| Dag | Velden | Tijdvenster | Opmerkingen |
|-----|--------|-------------|-------------|
| Maandag t/m Donderdag | Alleen veld 5 | 18:00 – zonsondergang | Geen kunstlicht, veld 1-4 training |
| Vrijdag | Geen | – | Geen wedstrijden |
| Zaterdag | Veld 1-5 | 08:30 – 22:00 (1-4) / 08:30 – 17:00 (5) | 10 min buffer |
| Zondag | Geen | – | Geen wedstrijden |

### Scenario: Zaterdagwedstrijd op veld 1-4

- **GIVEN** een request voor een zaterdag
- **WHEN** beschikbaarheid wordt gecontroleerd
- **THEN** MOETEN velden 1 t/m 4 beschikbaar zijn van 08:30 tot 22:00
- **AND** MOET veld 5 beschikbaar zijn van 08:30 tot 17:00

### Scenario: Doordeweekse wedstrijd

- **GIVEN** een request voor maandag t/m donderdag
- **WHEN** beschikbaarheid wordt gecontroleerd
- **THEN** MOET alleen veld 5 beschikbaar zijn van 18:00 tot zonsondergang

### Scenario: Vrijdag of zondag

- **GIVEN** een request voor vrijdag of zondag
- **WHEN** beschikbaarheid wordt gecontroleerd
- **THEN** MOET het antwoord `beschikbaar: false` zijn
- **AND** MOET `reden` bevatten dat er geen wedstrijden mogelijk zijn op die dag

---

## Requirement: Veldvoorkeur

Bij toewijzing MOET het systeem velden toewijzen in volgorde van voorkeur.

**Voorkeurvolgorde:** Veld 1 > Veld 2 > Veld 3 > Veld 4 > Veld 5

### Scenario: Meerdere velden beschikbaar

- **GIVEN** meerdere velden beschikbaar op het gevraagde tijdstip
- **WHEN** een slot wordt toegewezen
- **THEN** MOET het veld met het laagste nummer (hoogste voorkeur) gekozen worden

---

## Requirement: Leeftijdscategorieën (Speeltijden)

Het systeem MOET wedstrijdduur en veldgrootte bepalen op basis van de leeftijdscategorie.

| Categorie | Veldgrootte | Duur | Wedstrijden per veld |
|-----------|-------------|------|---------------------|
| JO7, JO8, JO9 | 0.25 (kwart) | 50 min | 4 |
| JO10 | 0.25 (kwart) | 65 min | 4 |
| JO11, JO12 | 0.50 (half) | 75 min | 2 |
| JO13, MO13 | 1.00 (heel) | 75 min | 1 |
| JO14, JO15, MO15 | 1.00 (heel) | 85 min | 1 |
| JO16, JO17, MO17 | 1.00 (heel) | 95 min | 1 |
| G | 0.50 (half) | 75 min | 2 |
| JO18, JO19, JO23, MO19, MO20, VR, 1-99 | 1.00 (heel) | 105 min | 1 |

### Scenario: Kwartveldwedstrijd

- **GIVEN** een check-availability met `leeftijdsCategorie: "JO8"`
- **WHEN** een slot wordt toegewezen
- **THEN** MOET `veldDeelGebruik` gelijk zijn aan `0.25`
- **AND** MOET `wedstrijdDuurMinuten` gelijk zijn aan `50`

### Scenario: Heelveldwedstrijd

- **GIVEN** een check-availability met `leeftijdsCategorie: "JO13"`
- **WHEN** een slot wordt toegewezen
- **THEN** MOET `veldDeelGebruik` gelijk zijn aan `1.0`
- **AND** MOET `wedstrijdDuurMinuten` gelijk zijn aan `75`

---

## Requirement: Teamspecifieke regels

Het systeem MOET teamspecifieke regels uit `dbo.TeamRegels` toepassen bij beschikbaarheidscontroles.

| Team | Regel | Waarde |
|------|-------|--------|
| VRC 1 | BufferVoor | 60 min vóór wedstrijd, geen andere wedstrijden op hetzelfde veld |
| VRC 1 | BufferNa | 30 min na wedstrijd, geen andere wedstrijden op hetzelfde veld |

### Scenario: VRC 1 thuiswedstrijd

- **GIVEN** VRC 1 speelt op veld X om 14:00
- **WHEN** beschikbaarheid gecontroleerd wordt voor hetzelfde veld
- **THEN** MAG er GEEN wedstrijd gepland worden van 13:00 tot 14:00 (60 min buffer vóór)
- **AND** MAG er GEEN wedstrijd gepland worden direct na de wedstrijd + 30 min buffer
