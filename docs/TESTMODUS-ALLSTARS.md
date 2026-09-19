# Testmodus — ALLSTARS fictieve wedstrijden

De ALLSTARS-testmodus maakt het mogelijk om de dagplanning en planner-logica te testen met volledig fictieve wedstrijden, zonder de echte Sportlink-data van de club te beïnvloeden.

> **Voor wie is dit document?** Het is bewust tweeledig. Alles tot en met *Testmodus activeren en
> verlaten* is voor de **beheerder van de vereniging**; daar is geen technische kennis voor nodig.
> Vanaf de paragraaf *X-Club-Code header* gaat het over de database en de code — dat deel is voor
> ontwikkelaars en kunt u overslaan.

---

## Wat is de ALLSTARS-testmodus?

In normale modus haalt de planner zijn data uit de Sportlink Club API (live) of de gesynchroniseerde database (`his.matches` met `ClubCode = '<jouwclub>'`). In ALLSTARS-modus wordt dezelfde plannerlogica uitgevoerd op testdata die opgeslagen staat in `his.matches WHERE ClubCode = 'ALLSTARS'`.

**Er is geen echte ALLSTARS-club.** De ClubCode `ALLSTARS` is een speciale sleutelwaarde die aangeeft dat het om fictieve testdata gaat.

> **Bijgewerkt (#1246).** Op de Postgres-tier is er wél een instellingenrij voor ALLSTARS: `Database.Postgres/migrations/006_allstars_demodata.sql` maakt hem aan, met `sportlinkclientid = 'ALLSTARS_NO_SYNC'` en `syncenabled = FALSE`. De democlub heeft dus nog steeds geen e-mail, API-verbinding of synchronisatieschema — maar de eerdere formulering ("er is geen rij") klopte niet meer met de code en is hier gecorrigeerd.

---

## Wat werkt in ALLSTARS-modus?

| Functionaliteit | Werkt | Toelichting |
|---|---|---|
| Dagplanning | ✅ | Laadt fictieve wedstrijden uit `his.matches WHERE ClubCode='ALLSTARS'` |
| Planner-optimalisatie | ✅ | Volledige grasveld-logica, teamtijden en veldconflicten |
| Testdata beheer (wedstrijden) | ✅ | Invoergrid op `/testdata/wedstrijden` |
| Velden & veldbeschikbaarheid | ✅ | Deelt `dbo.Velden` met de echte club |
| Speeltijden | ✅ | **Eigen rijen** met `ClubCode='ALLSTARS'`, gekopieerd van de primaire club — niet gedeeld (#1246). De kopie draait bij elke deploy en vult zichzelf aan zodra de primaire club speeltijden heeft |
| Leermomenten | ✅ | Deelt tabel met echte club |
| Instellingen | ❌ | Pagina toont testmodus-melding. Op de Postgres-tier bestaat er sinds #1246 wél een `AppSettings`-rij voor de democlub (met `syncenabled = FALSE`); de pagina blijft bewust afgeschermd |
| Synchronisatie | ❌ | Niet van toepassing — testdata wordt handmatig beheerd |
| E-mailverwerking | ❌ | Niet van toepassing — testdata genereert geen echte e-mails |
| E-mailtester | ✅ | Dry-run (verstuurt en bewaart niets). Respecteert sinds #677 de geselecteerde club: de teamnaam-prefix en de voorbeeld-handtekening (afzender, coördinator) komen uit de instellingen van de gekozen club, niet meer altijd uit die van de echte club |
| Teambegeleiding | ✅ | Leest `avg.Teambegeleiding WHERE ClubCode='ALLSTARS'` |

---

## Testdata beheren

### Wedstrijden invoeren

Navigeer naar **Testdata** onder het kopje TESTMODUS onderaan de zijbalk (alleen zichtbaar wanneer
AllStars FC in de club-keuzelijst geselecteerd is).

Op die pagina voer je fictieve wedstrijden in met:
- Datum
- Aanvangstijd
- Teamnaam (bijv. `JO9-1`, `JO11-3`)
- Veld (uit de bestaande `dbo.Velden`-tabel)
- Competitiesoort

De wedstrijden worden opgeslagen in `his.matches` met `ClubCode = 'ALLSTARS'`.

### Teams in ALLSTARS-modus

De planner-logica zoekt teamdata op via `avg.Teambegeleiding WHERE ClubCode = 'ALLSTARS'`. Sinds #1246
worden 28 fictieve teambegeleiders meegeseed door het demodata-seedscript, dat bij elke Postgres-deploy
draait — je hoeft ze niet meer met de hand aan te maken. Lokaal: `scripts/dev/Seed-AllStarsDemodata.ps1`.

### Speeltijden voor testdata

De democlub heeft **eigen speeltijdenrijen** met `ClubCode='ALLSTARS'` — hij deelt de tabel dus niet
met de echte club (#1246; de eerdere tekst hier beweerde dat wel). Die rijen worden gekopieerd van
de primaire club door `scripts/migrations/003-seed-allstars-demo-matches-postgres.sql`, dat bij elke
Postgres-deploy draait. Voer de speeltijden dus gewoon in via **Instellingen → Speeltijden** in
normale modus; de democlub volgt bij de eerstvolgende deploy vanzelf.

De koppeling in de planner werkt op teamnaam-prefix: `JO9-2` matcht op `JO9`.

---

## Testmodus activeren en verlaten

### Activeren

Kies **AllStars FC** in de club-keuzelijst midden in de bovenbalk. Die keuzelijst verschijnt zodra
er meer dan één club in de installatie staat; de democlub staat er standaard in. Zodra u hem kiest:

1. Verschijnt boven in de zijbalk het oranje blok **TESTMODUS — AllStars FC — geen productiedata**
2. Kleurt de club-keuzelijst in de bovenbalk oranje
3. Verschijnt onderaan de zijbalk het kopje **TESTMODUS** met daaronder het menu-item **Testdata**
4. Gebruiken alle schermen voortaan de gegevens van de democlub in plaats van die van uw club

De browser onthoudt uw keuze, ook nadat u hem afsluit.

### Verlaten

Kies in diezelfde keuzelijst uw eigen club weer. Het oranje blok en het menu-item **Testdata**
verdwijnen, en alle schermen tonen weer de echte gegevens.

### X-Club-Code header

Elke API-aanroep in ALLSTARS-modus stuurt `X-Club-Code: ALLSTARS` mee. De FunctionApp leest deze header in `EasyAuthHelper.GetClubCodeFromRequest()` en stuurt de ALLSTARS-specifieke datapath in (bijv. `GetAllstarsOccupationsAsync` in de planner).

---

## Datamodel

### `his.matches` — ALLSTARS-rijen

```sql
SELECT *
FROM [his].[matches]
WHERE [ClubCode] = 'ALLSTARS'
ORDER BY [kaledatum], [aanvangstijd];
```

Verplichte velden voor planner-werking:
| Veld | Voorbeeld | Reden |
|---|---|---|
| `ClubCode` | `ALLSTARS` | Discriminator |
| `kaledatum` | `2026-05-30` | Datum voor dagplanning |
| `aanvangstijd` | `10:00` | Startuur (null = niet meegenomen) |
| `teamnaam` | `JO9-1` | Prefix-match met Speeltijden |
| `veld` | `veld 2` | Naam-match met `dbo.Velden.VeldNaam` |
| `thuisteam` | `Team A` | Weergave in planner-HTML |
| `uitteam` | `Team B` | Weergave in planner-HTML |

### `dbo.Velden` — gedeeld met echte club

Velden zijn niet per club gesplitst — ze vertegenwoordigen de fysieke accommodatie. `VeldType = 'gras'` bepaalt of de grasveld-ontlastlogica op een veld van toepassing is.

---

## Technische werking (voor ontwikkelaars)

### Planner-routing

In `PlannerFunction.cs` wordt de `X-Club-Code` header uitgelezen:

```csharp
var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
var response = await PlannerService.OptimaliseerAsync(request, log, clubCode);
```

In `PlannerService.OptimaliseerAsync`:

```csharp
var occupations = string.Equals(clubCode, "ALLSTARS", StringComparison.OrdinalIgnoreCase)
    ? await PlannerDataAccess.GetAllstarsOccupationsAsync(date)
    : await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log);
```

### ClubSelectorService

`ClubSelectorService` slaat de geselecteerde clubcode op in `localStorage` (sleutel
`selectedClubCode`). De keuze voor de democlub overleeft een paginawissel, maar **niet** door een
uitzondering in de code: `ALLSTARS` staat gewoon als rij in `public.appsettings` en komt daardoor
mee in `GET /api/beheer/clubs` (`AdminClubsRepository.GetClubsAsync`, dat
`clubcode, clubname, syncenabled` uit die tabel leest). De opgeslagen keuze komt dus voor in de
opgehaalde lijst, en `MainLayout.LaadClubsAsync` laat hem daarom staan. Terugvallen op de primaire
club gebeurt alleen wanneer er géén keuze is opgeslagen, of wanneer de opgeslagen clubcode niet in
die lijst voorkomt:

```csharp
if (ClubSelector.SelectedClubCode == null ||
    !_clubs.Any(c => c.ClubCode == ClubSelector.SelectedClubCode))
{
    var primary = _clubs.FirstOrDefault(c => c.SyncEnabled) ?? _clubs[0];
    await ClubSelector.SelectClubAsync(primary.ClubCode, primary.ClubName);
}
```

Er is dus **geen ALLSTARS-uitzondering** in dit bestand. Dat onderscheid is niet academisch:
verdwijnt de AppSettings-rij van de democlub (aangemaakt door
`Database.Postgres/migrations/006_allstars_demodata.sql`), dan valt de selectie wél terug op de
primaire club.

**In gewone taal, voor de beheerder:** de democlub blijft in de keuzelijst staan omdat hij als
gewone club in de instellingen van de installatie is opgenomen — niet omdat er een speciale
uitzondering voor gemaakt is. Uw keuze wordt door de browser onthouden; kiest u AllStars FC, dan
blijft dat zo tot u zelf uw eigen club weer kiest.

---

## AVG en security

- De ALLSTARS-testdata bevat **geen echte persoonsgegevens** — alle namen en tijden zijn fictief.
- De namen en e-mailadressen in de demodata volgen het [John Doe-principe](../CLAUDE.md):
  voornamen zonder achternaam (`Frenkie`, `Bas`, `Guus`) op het gereserveerde domein
  `@allstars-fc.test`, dat nooit een echt e-mailadres kan zijn. Teamnamen hebben de vorm
  `AllStars JO13 1`.
- De testdata staat in dezelfde database als de echte data, maar is volledig geïsoleerd via de `ClubCode = 'ALLSTARS'` discriminator.
- Productie-API's (Sportlink, Microsoft Graph) worden in testmodus **niet** aangesproken.
