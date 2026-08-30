# Planner API — Architectuur & Richtlijnen

Dit document definieert de regels, beperkingen en API-contract voor de Veldplanner API. Het is de enige bron van waarheid voor de planningslogica.

## Doel

Geautomatiseerde veldbeschikbaarheidscontrole voor oefenwedstrijden op **[Sportparklocatie]**. Wanneer iemand (via email, WhatsApp of andere kanalen) een wedstrijd wil plannen, controleert de API de beschikbaarheid en geeft aan of de gewenste datum/tijd mogelijk is — of stelt alternatieven voor.

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
| [Heren 1] | BufferVoor | 60 min | 1 uur voor de wedstrijd geen andere wedstrijden op hetzelfde veld |
| [Heren 1] | BufferNa | 30 min | 30 min na de wedstrijd geen andere wedstrijden op hetzelfde veld |

Ook ondersteund:
- `VoorkeurVeld`: team speelt bij voorkeur op een bepaald veld, optioneel met een tijd. Sinds #666 wordt
  deze regel daadwerkelijk toegepast door de auto-planner (als eerste laag in de rangorde); daarvoor werd
  hij opgeslagen en in de GUI getoond zonder enig effect op de planning.

Voorkeurstijden per team staan niet in `dbo.TeamRegels` maar in `dbo.TeamVoorkeurTijden` (per `DagVanWeek`).

### Zonsondergang-beperking (velden zonder kunstlicht)
- Wedstrijd moet eindigen **voor zonsondergang**
- Zonsondergang berekend via NOAA solar algorithm voor de clublocatie ([breedtegraad]°N, [lengtegraad]°E) — coördinaten configureerbaar in `dbo.AppSettings`
- Opgeslagen in `dbo.Zonsondergang` tabel (handmatige overrides mogelijk)
- **Geen harde buffer**, wel een waarschuwing als marge < 20 minuten

### Standaard voorkeurstijd per leeftijdscategorie (zachte regel)

Per leeftijdscategorie staat een standaard voorkeurstijd in `dbo.Speeltijden.StandaardVoorkeurTijd`,
instelbaar per club via **Beheer → Instellingen → Speeltijden** (#666). De auto-planner gebruikt die tijd
voor teams die géén eigen rij in `dbo.TeamVoorkeurTijden` hebben.

Leeg laten betekent: geen streeftijd — de planner kiest dan het eerst beschikbare slot. De waarden zijn
clubbeleid en staan bewust **niet** hardcoded in C#; vóór #666 stond er wel een vaste staffel in code,
die alleen als sorteersleutel werd gebruikt en nooit als streeftijd.

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

### Kwartbanen: hoe een veld verdeeld wordt (#666)

Een veld bestaat uit **vier kwartbanen**: `A1`, `A2`, `B1`, `B2`. Die labels zijn de `VeldSubpositie` en
komen terug in de Sportlink-veldstring ("Kunstgras 1 A2").

> **Veldstring → veldnummer: één matching, op drie plekken identiek (#707, #719).** Sportlink levert het
> veld als `<veldnaam>[ <subpositie>]`; `dbo.Velden` bevat alleen de veldnaam. Een treffer is een exact
> gelijke veldnaam, óf een veldnaam gevolgd door een spatie en de subpositie — **langste veldnaam eerst**,
> zodat `veld 1 achter B` bij "veld 1 achter" hoort en niet bij "veld 1".
>
> Dit stond ooit als `LEFT(veld, 6)` in de SQL-paden. Die afkap vereist dat élke veldnaam maximaal zes
> tekens is én in de eerste zes uniek, en dat is twee keer niet waar: `veld 10` werd `veld 1` (bezetting
> op het verkeerde veld, waarna veld 10 vrij leek → **dubbele boeking**) en `hoofdveld` matchte niets en
> viel volledig uit de bezetting. De matching zit nu in `Planner.Shared.VeldResolver.Resolve` (C#,
> tier-agnostisch — sinds #819 verplaatst uit `PlannerShared.ResolveVeld`; die methode en
> `AutoPlanService.NormaliseerVeld` zijn nu dunne delegaties, gedrag ongewijzigd),
> `VeldResolutie.SqlOuterApply` (SQL Server-specifieke SQL-generatie vanuit C#) en de view
> `planner.AlleWedstrijdenOpVeld`.
>
> De view staat op **twee** plekken — het DB-project én `Script.PostDeployment1.sql` — en CI rolt alleen
> dat laatste uit. `VeldResolutieDriftTests` faalt als ze uiteenlopen of als de zes-tekens-afkap
> terugkomt.
>
> **Postgres-tier (#819):** de Postgres-vertaling van deze view (`Database.Postgres/PostgresPlannerViewGenerator.cs`)
> bouwt de veldresolutie bewust **niet** opnieuw in SQL na — dat zou een derde, onafhankelijke kopie
> van deze matching zijn. De view levert daar de ruwe, ongeresolveerde veldstring terug;
> `PostgresPlannerAvailabilityReader` resolveert die met exact dezelfde `Planner.Shared.VeldResolver`
> die ook de SQL Server-tier gebruikt. Zie de doc-comment op `PostgresPlannerViewGenerator` voor de
> volledige motivatie en voor een empirisch gevonden hoofdlettergevoeligheidsverschil tussen SQL
> Server's collatie en Postgres' regex-operator (opgelost met `~*`; gerelateerd aan #820).

| Veldafmeting | Banen | Toegestane plekken |
|---|---|---|
| 0.25 | 1 | `A1`, `A2`, `B1` of `B2` |
| 0.50 | 2 aangrenzend | `A` (banen 1+2) of `B` (banen 3+4) — niet dwars door het midden |
| 1.00 | 4 | geen subpositie |

`FieldScheduler.PastOpVeld` houdt per kandidaattijd bij welke banen al bezet zijn door wedstrijden die op
dat moment overlappen, en kiest daaruit de eerste vrije plek (`EersteVrijeSubpositie`).

**Waarom dit zo moest:** de oude toewijzing keek alleen naar het *aantal* gelijktijdige wedstrijden — de
eerste kreeg `A1`, de tweede `A2`, de derde `B1`. Twee fouten volgden daaruit. Een halfveldwedstrijd op
`A` (banen 1+2) plus een kwartveldwedstrijd leverde `A2` op, precies bovenop de eerste. En met `A1` en `B1`
bezet en `A2` vrij koos hij alsnog `B1`. De capaciteitscheck telde uitsluitend de fracties op, dus
numeriek leek dat te passen (0,5 + 0,25 = 0,75 ≤ 1,00) terwijl de banen botsten.

Bij handmatig verslepen bepaalt de verticale droppositie in de rij welke baan het wordt, zodat een
kwartveldwedstrijd bewust op `A2` gezet kan worden in plaats van op de eerste vrije plek.

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

## Codestructuur (intern)

`PlannerService` is een dunne facade die delegeert naar use-case services. Bestaande callers (PlannerFunctions, EmailProcessorFunction) roepen `PlannerService.*Async(...)` aan — de interne indeling is transparant.

| Service | Locatie | Verantwoordelijkheid |
|---|---|---|
| `PlannerService` | `Planner/PlannerService.cs` | Facade — delegeert naar services |
| `AvailabilityService` | `Planner/Services/` | CheckAvailabilityAsync, Doordeweeks |
| `AutoPlanService` | `Planner/Services/` | AutoPlanAsync, Toepassen, Veldbezetting — de enige dagplanning-optimalisatie |
| `RescheduleService` | `Planner/Services/` | CheckRescheduleAvailabilityAsync |
| `TeamScheduleService` | `Planner/Services/` | GetTeamScheduleAsync |
| `PlannerShared` | `Planner/Services/` | CanFitMatch, FieldScheduler, constanten |
| `PlannerDataAccess` | `Planner/PlannerDataAccess.cs` | Facade → repositories in `Planner/Repositories/` |
| `ClubScope` | `Planner/ClubScope.cs` | ClubCode-resolutie + SQL-predicaten voor clubisolatie |

`FieldScheduler` is de pure scheduling engine — geen DB of API-calls, alleen slot-berekening op basis van beschikbaarheid en buffers.

---

## ClubCode-isolatie — harde eis (#573, #580)

> **Elke planner- en bezettingsquery is hard gescoped op ClubCode.** Een database bevat de
> productieclub én de ALLSTARS-demoklub; zonder filter belandt clubvreemde data in
> zoekresultaten *en* in beslislogica. Dat is zowel een planningsfout (onjuiste bezetting →
> foutieve "niet mogelijk"-antwoorden) als een data-isolatieprobleem.

Twee predicaten, omdat de tabellen verschillen:

| Tabelgroep | ClubCode | Predicaat |
|---|---|---|
| `planner.*`, `dbo.*` | NOT NULL | `[ClubCode] = @clubCode` |
| `his.*` | NULLABLE (migratie 001) | `ISNULL(x.[ClubCode], @primaireClubCode) = @clubCode` — via `ClubScope.HisFilter("x")` |

De NULL-tolerantie op `his.*` is bewust: niet-gestempelde legacy-rijen horen bij de primaire
club, precies zoals de backfill in migratie 001. Zonder die tolerantie zouden die wedstrijden
uit de bezetting vallen → **onderschatte bezetting → dubbele boekingen**.
ALLSTARS-rijen zijn altijd expliciet gestempeld en lekken dus nooit mee.

**Regels bij nieuwe of gewijzigde queries:**

1. Elke repository-methode die clubdata leest heeft een `string? clubCode = null` parameter.
2. Resolutie loopt via `ClubScope.Resolve(...)`: geen expliciete waarde → de primaire club van
   deze deployment. Nooit een lege string — dat schakelt het filter uit. Ontbreekt de instelling,
   dan volgt een `InvalidOperationException` (fail-explicit).
3. De view `planner.AlleWedstrijdenOpVeld` levert `ClubCode` en `Wedstrijdcode` per rij; consumers
   filteren zelf op club. Wijzig de view **op twee plekken tegelijk**:
   `Database/planner/Views/AlleWedstrijdenOpVeld.sql` (DB-project) én
   `Database/Script.PostDeployment1.sql` (`CREATE OR ALTER` — dit is het pad dat productie bereikt).
4. Uitzondering, bewust: `Speeltijden` en `TeamRegels` in `AutoPlanService` lezen de primaire club.
   Dat is KNVB-referentiedata en clubconfiguratie die de demomodus hergebruikt; er zijn geen
   ALLSTARS-rijen. Deze richting kan productie-antwoorden niet vervuilen.

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
  "teamNaam": "[ClubCode] JO11-1",
  "tegenstander": "[Tegenstander] JO11-2",
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
    "wedstrijd": "[ClubCode] JO11-9 - [Tegenstander] JO11-3",
    "aanvangsTijd": "11:30",
    "eindTijd": "12:45",
    "veldNaam": "veld 4"
  },
  "reden": "[ClubCode] JO11-9 heeft al een wedstrijd op 16 mei: [ClubCode] JO11-9 - [Tegenstander] JO11-3 om 11:30 (veld 4).",
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
  "teamNaam": "[ClubCode] JO11-1",
  "tegenstander": "[Tegenstander] JO11-2",
  "aangevraagdDoor": "trainer@voorbeeld.nl"
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
Velddefinities met type (vrije tekst, bijv. kunstgras/natuurgras) en verlichting. Elke vereniging
configureert dit naar eigen situatie — inclusief clubs met een ander aantal velden of uitsluitend
natuurgras. Volledig beheerbaar via Instellingen → Velden in de Admin GUI (#679); daarvoor kon een
veld alleen via een directe database-wijziging worden toegevoegd.

| Kolom | Beschrijving |
|-------|-------------|
| VeldNummer | Uniek nummer (PK — deployment-breed, niet per club, zie "Deployment-model" in CLAUDE.md) |
| VeldNaam | Weergavenaam (bijv. "veld 1") |
| VeldType | Vrije tekst (bijv. `kunstgras` of `natuurgras`) — bepaalt welke velden ontlast worden bij de grasveld-ontlasten optimalisatie. Puur beschrijvend, geen vaste enum. |
| HeeftKunstlicht | Verlichting beschikbaar — bepaalt zonsondergang-beperking |
| Actief | Of het veld in gebruik is |

Voorbeeld seeddata: veld 1–4 kunstgras + kunstlicht, veld 5 natuurgras zonder kunstlicht, veld 6 inactief — elke club configureert dit naar eigen situatie.

### dbo.VeldBeschikbaarheid
Beschikbaarheidsvensters per dag van de week per veld. Bepaalt welke velden op welke dagen
beschikbaar zijn. Beheerbaar via Instellingen → Velden (#679).

Optioneel gekoppeld aan een `dbo.VeldPeriode` via `PeriodeId` (#581): `NULL` is het
standaardregime en geldt buiten elke actieve periode (exact het gedrag van vóór #581). Is er voor
de club een actieve periode op de gevraagde datum, dan gelden uitsluitend de rijen met dat
`PeriodeId` — nooit een samenvoeging met het standaardregime, want periodes zoals "Zomerstop" en
"Competitie" zijn expliciet tegengestelde regimes. `PlannerAvailabilityRepository.GetAvailableFieldsAsync`
bepaalt dit per aanroep opnieuw aan de hand van de opgevraagde datum.

### dbo.VeldPeriode
Herbruikbaar regime met een vaste geldigheidsrange (`DatumVan`/`DatumTot`), bijv. "Zomerstop" of
"Competitie" (#581). Er mag nooit meer dan één actieve periode van dezelfde club tegelijk lopen —
overlap wordt bij het opslaan geweigerd (`AdminVeldPeriodeRepository.OverlaptMetAndereAsync`). Een
club zonder periodes heeft hier geen rijen; dat verandert niets aan het bestaande gedrag.

### dbo.VeldTraining
Terugkerende trainingsbezetting per veld per weekdag — een tweede, club-vrij-instelbare
bezettingsbron naast wedstrijden (#679, uitwerking van #581). Elke club legt zelf vast welke velden
op welke dag door training bezet zijn, en dat mag per dag verschillen (bijv. maandag ruim,
donderdag vol). Heeft (nog) geen periode-begrip: een trainingsblok geldt het hele jaar. Wil een
club dat trainingen tijdens de zomerstop niet meetellen, dan is dat vandaag nog handwerk (het
trainingsblok tijdelijk op `Actief = 0` zetten) — periode-scoping van trainingsblokken is bewust
buiten de scope van #581 gehouden en zou een vervolgissue zijn.

| Kolom | Beschrijving |
|-------|-------------|
| VeldNummer | FK naar dbo.Velden |
| DagVanWeek | ISO-dag 1–7 |
| VanTijd / TotTijd | Tijdvenster dat door training bezet is |
| Omschrijving | Vrije tekst (bijv. "JO15-2 training"), optioneel |
| Actief | Blok tijdelijk uitschakelen zonder te verwijderen |

`PlannerAvailabilityRepository.GetFieldOccupationsAsync` voegt actieve trainingsblokken toe als
derde bezettingsbron naast `his.matches` en `planner.GeplandeWedstrijden` — zowel de planner als de
e-mailpijplijn zien hierdoor automatisch een kleiner beschikbaar venster, zonder aparte integratie.
Een club zonder rijen in deze tabel houdt exact het gedrag van vóór deze feature.

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

## Auto-planner (`POST /api/planner/auto-plan`) — de enige dagplanning-optimalisatie

De auto-planner verdeelt alle wedstrijden van een dag over de beschikbare velden. **Sinds #666 is dit
de enige optimalisatie**: het losse endpoint `POST /api/planner/optimaliseer` en de bijbehorende
`OptimizationService` zijn vervallen. Dat tweede pad negeerde voorkeurstijden en prioriteiten volledig,
waardoor de twee knoppen in de GUI verschillende — en onderling tegenstrijdige — planningen opleverden.

### Definitie van "optimaal"

> Een planning is optimaal als elk team op zijn voorkeurstijd staat, en anders zo dicht mogelijk daarbij.

Compact plannen is expliciet **geen** doel. Tot #666 was het omgekeerde geïmplementeerd: lag het vroegste
vrije slot meer dan één buffer vóór de voorkeurstijd, dan pakte de planner dát slot. Een team met
voorkeur 14:30 belandde zo op 09:00 — vijf en een half uur ernaast — terwijl de tabel "OK" meldde.

### Rangorde: regels → voorkeuren → defaults

Per wedstrijd bepaalt `AutoPlanService.BepaalPlanDoel` het planningsdoel in deze vaste rangorde:

| Laag | Bron | Wat het oplevert |
|---|---|---|
| **0 — regels** | `dbo.TeamRegels`, `RegelType = 'VoorkeurVeld'` | Voorkeursveld + optioneel een tijd op die regel |
| **1 — voorkeuren** | `dbo.TeamVoorkeurTijden` voor de betreffende `DagVanWeek` | Voorkeurstijd van het team zelf |
| **2 — defaults** | `dbo.Speeltijden.StandaardVoorkeurTijd` | Standaardtijd van de leeftijdscategorie |
| **3 — niets** | — | Geen streeftijd: eerst beschikbare slot |

`BufferVoor`/`BufferNa` uit `dbo.TeamRegels` zijn geen laag maar een **randvoorwaarde**: die gelden altijd.

**Prioriteit beslist conflicten tussen teams.** Binnen elke laag worden wedstrijden verwerkt op
`Prioriteit` oplopend — **laag getal = belangrijker**, dezelfde conventie in `TeamVoorkeurTijden` én
`TeamRegels`. Wie eerder verwerkt wordt, claimt zijn plek eerst. Vóór #666 werd `Prioriteit` alleen
gebruikt om binnen één team de primaire voorkeursrij te kiezen, en dus nooit om te bepalen welk team
voorrang krijgt: een team met prioriteit 1 kon zijn voorkeurstijd verliezen aan een team met
prioriteit 10.

### Algoritme (FieldScheduler)

1. **Sorteervolgorde** — `(laag, prioriteit, streeftijd, leeftijdscategorie, teamnaam)`.
2. **Plaatsing** — met streeftijd loopt `FindAndOccupyNearTime` de kandidaattijden van de streeftijd naar
   buiten af (voorkeur, ±5, ±10, … tot ±90 min) en pakt het eerste haalbare slot; eerder gaat vóór later
   bij gelijke afstand. Is er een voorkeursveld, dan wordt dat veld bij élke kandidaattijd als eerste
   geprobeerd — een **zachte** voorkeur, zodat een team niet onplanbaar wordt als het veld vol zit.
   Zonder streeftijd valt de planner terug op het eerst beschikbare slot (`FindAndOccupyNextSlot`),
   met 09:00 als ondergrens.
3. **Ondergrens** — met een streeftijd is de ondergrens de veldbeschikbaarheid zelf, niet de vaste
   09:00-dagstart. Een team dat 08:30 wil en een veld dat om 08:00 opengaat, krijgt 08:30.
4. **Buffers** — `PastOpVeld` is de enige plek die bepaalt of een wedstrijd op een veld past, en bewaakt
   twee dingen tegelijk:
   - **Capaciteit** — wedstrijden die elkaar in tijd overlappen delen het veld; toegestaan zolang de som
     van de veldfracties binnen 1.00 blijft. Tussen zulke gelijktijdige wedstrijden hoort géén buffer:
     die staan naast elkaar, niet achter elkaar.
   - **Buffer** — wedstrijden die elkaar niet overlappen gebruiken het veld ná elkaar; daartussen geldt
     de grootste van de standaardbuffer en de teamspecifieke `BufferNa`/`BufferVoor` uit `dbo.TeamRegels`.

   Deze controle zat vóór #666 alleen in `FindEarliestSlot`. Het pad dat op een voorkeurstijd plant keek
   uitsluitend naar capaciteit, waardoor wedstrijden rug-aan-rug werden ingepland met nul minuten ertussen
   en de 60-minutenregel van een eerste elftal simpelweg werd overgeslagen. Dat viel pas op toen dat pad
   door de precedence-wijziging de normale route werd.

### Handmatig verslepen in de tijdlijn

De berekende planning is met de muis aan te passen: een wedstrijdblok kan naar een andere tijd (stappen
van 5 minuten, zelfde afronding als de planner) of naar een ander veld gesleept worden. Dat gebeurt
volledig client-side in `Dagplanning.razor`; er is geen extra endpoint.

- Alleen de **optimale planning** is sleepbaar. De tab "Huidige situatie" is de stand uit Sportlink en
  blijft read-only.
- Na een zet worden `Status`, `VoorkeurAfwijkingMinuten`, `VoorkeurStatus`, het aantal te wijzigen
  wedstrijden en de geschatte eindtijd opnieuw bepaald met dezelfde regels als de server, zodat een
  handmatige zet net zo eerlijk beoordeeld wordt als een berekende.
- `ControleerConflicten` bewaakt dezelfde twee regels als `PastOpVeld` (capaciteit bij overlap, buffer bij
  opeenvolging) en toont per overtreding welke twee wedstrijden het betreft. Een handmatige zet kan dus
  wel een onmogelijke planning opleveren, maar niet stilzwijgend.
- Wegschrijven gaat ongewijzigd via **Toepassen in testmodus** (`/planner/auto-plan/toepassen`).

### Twee losse statussen — bewust gescheiden

| Veld | Betekenis |
|---|---|
| `Status` | Verplaatst de planner deze wedstrijd t.o.v. de huidige stand? (`ongewijzigd` / `wijziging` / `nieuw-slot` / `niet-inplanbaar` / `onbekend-team`) |
| `VoorkeurStatus` | Staat de wedstrijd op de gewenste tijd? (`op-tijd` / `kleine-afwijking` ≤15 min / `grote-afwijking` >15 min / `geen-voorkeur`) |

Deze twee werden vóór #666 door elkaar gehaald: de groene "OK"-badge kwam uit `Status == "ongewijzigd"`,
dus een wedstrijd die de planner niet verplaatste toonde "OK" ongeacht een afwijking van 60 minuten.
`VoorkeurBron` (`regel` / `team` / `leeftijd`) laat zien uit welke laag de streeftijd komt.

### Club-scoping

`TeamRegels` wordt opgehaald **voor de opgevraagde club** (`X-Club-Code`). Dat stond eerder op de
primaire club onder de aanname "er zijn geen ALLSTARS-rijen" (#573); die aanname klopt niet meer, waardoor
buffers en voorkeursveld in de testmodus stilzwijgend werden genegeerd — precies de modus waarin je het
test. `Speeltijden` gebruikt de eigen club met terugval op de primaire club, omdat zonder speeltijden
geen enkele wedstrijd een duur heeft.

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
| `BerichtAiService.cs` | OpenAI classificatie (kanaal-agnostisch) |
| `BerichtModels.cs` | DTO's en enums (InkomendBericht, BerichtClassificatie) |
| `BerichtResponseGenerator.cs` | Template-gebaseerde antwoord-opbouw (kanaal-agnostisch) |

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

1. **App Registration** `[ClubCode]-Veldplanner-EmailProcessor` (daemon, geen redirect URI)
2. **API Permissions** (Application type): `Mail.Read`, `Mail.ReadWrite`, `Mail.Send` + admin consent
3. **Client Secret** aanmaken (24 maanden geldigheid)
4. **Application Access Policy** — beperk tot coordinator-mailbox via mail-enabled security group

### Uitrolfasen

| Fase | Beschrijving | Email-bestemming |
|------|-------------|-----------------|
| Fase 1 | Review mode — controle door coördinator | Review-mailbox |
| Fase 2 | Productie — direct antwoord | Originele afzender |

### Thuis/uit-herkenning bij herplanverzoeken

Bij herplanverzoeken kan de email binnenkomen via een intern clubcontact (doorgestuurd namens de tegenstander) of rechtstreeks van de tegenstander. De communicatie-flow verschilt per situatie.

#### Stap 1 — Afzender herkennen

Het interne domein van de club is geconfigureerd in `dbo.AppSettings` als `internDomein` (bijv. `@mijnclub.nl`).

| Aanwijzing | Conclusie |
|-----------|-----------|
| Emaildomein = `internDomein` uit AppSettings | Club-intern (thuisteam-kant) |
| Ander emaildomein | Mogelijk tegenstander of ouder/coach tegenstander |

#### Stap 2 — Namens wie is het verzoek?

De AI-laag analyseert de tekst op patronen:

| Patroon in tekst | Conclusie |
|-----------------|-----------|
| "[Tegenstander] vraagt of...", "zij willen..." | Doorgestuurd door intern clubcontact, verzoek namens uitteam |
| "Wij kunnen niet om...", "Is het voor ons mogelijk..." | Afzender zelf is de vragende partij |
| "Kunnen we de wedstrijd verplaatsen" | Afzender = vragende partij |

#### Stap 3 — Thuis/uit bepalen uit wedstrijddata

Altijd betrouwbaar uit de database: `his.matches.teamnaam` = thuisteam (de eigen club). De andere partij in het `wedstrijd`-veld is het uitteam. Dit is harde data, geen interpretatie nodig.

#### Stap 4 — Communicatie-flow per scenario

| Afzender | Verzoek namens | Flow |
|----------|---------------|------|
| Club-intern | Tegenstander | Check planning → Overleg eigen team → Antwoord via intern contact terug naar tegenstander |
| Tegenstander direct | Zichzelf | Check planning → Overleg eigen team → Antwoord naar tegenstander |
| Club-intern | Eigen team | Check planning → Direct overleg met tegenstander |

#### Afzender geautomatiseerde berichten

Geautomatiseerde antwoorden worden verstuurd onder de naam zoals ingesteld in `PlannerAfzenderNaam` (bijv. `[ClubNaam] Veldplanner`). De afzendernaam staat **nooit hardcoded in de code**. De handtekening verwijst naar de verantwoordelijke contactpersoon:

```
Met vriendelijke groet,

[PlannerAfzenderNaam]
Geautomatiseerd antwoord namens [CoordinatorNaam]
[CoordinatorFunctie]
```

De afzendergegevens worden **niet hardcoded** maar opgeslagen in `dbo.AppSettings`:

| Instelling | Beschrijving | Voorbeeld |
|-----------|-------------|-----------|
| `PlannerAfzenderNaam` | Naam van het geautomatiseerde systeem | `[ClubNaam] Veldplanner` — invullen per club |
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
