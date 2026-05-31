# Testmodus — ALLSTARS fictieve wedstrijden

De ALLSTARS-testmodus maakt het mogelijk om de dagplanning en planner-logica te testen met volledig fictieve wedstrijden, zonder de echte Sportlink-data van de club te beïnvloeden.

---

## Wat is de ALLSTARS-testmodus?

In normale modus haalt de planner zijn data uit de Sportlink Club API (live) of de gesynchroniseerde database (`his.matches` met `ClubCode = '<jouwclub>'`). In ALLSTARS-modus wordt dezelfde plannerlogica uitgevoerd op testdata die opgeslagen staat in `his.matches WHERE ClubCode = 'ALLSTARS'`.

**Er is geen echte ALLSTARS-club.** De ClubCode `ALLSTARS` is een speciale sleutelwaarde die aangeeft dat het om fictieve testdata gaat. Er is geen rij in `dbo.AppSettings` voor ALLSTARS — dit is bewust: de testmodus heeft geen eigen e-mail, API-verbinding of synchronisatieschema.

---

## Wat werkt in ALLSTARS-modus?

| Functionaliteit | Werkt | Toelichting |
|---|---|---|
| Dagplanning | ✅ | Laadt fictieve wedstrijden uit `his.matches WHERE ClubCode='ALLSTARS'` |
| Planner-optimalisatie | ✅ | Volledige grasveld-logica, teamtijden en veldconflicten |
| Testdata beheer (wedstrijden) | ✅ | Invoergrid op `/testdata/wedstrijden` |
| Velden & veldbeschikbaarheid | ✅ | Deelt `dbo.Velden` met de echte club |
| Speeltijden | ✅ | Deelt `dbo.Speeltijden` met de echte club |
| Leermomenten | ✅ | Deelt tabel met echte club |
| Instellingen | ❌ | Geen `AppSettings`-rij voor ALLSTARS — pagina toont testmodus-melding |
| Synchronisatie | ❌ | Niet van toepassing — testdata wordt handmatig beheerd |
| E-mailverwerking | ❌ | Niet van toepassing — testdata genereert geen echte e-mails |
| E-mailtester | ⚠️ | Werkt technisch, maar stuurt e-mails via de echte club-account |
| Teambegeleiding | ✅ | Leest `avg.Teambegeleiding WHERE ClubCode='ALLSTARS'` |

---

## Testdata beheren

### Wedstrijden invoeren

Navigeer naar **Testdata → Wedstrijden** in de zijbalk (alleen zichtbaar in ALLSTARS-modus).

Op die pagina voer je fictieve wedstrijden in met:
- Datum
- Aanvangstijd
- Teamnaam (bijv. `JO9-1`, `JO11-3`)
- Veld (uit de bestaande `dbo.Velden`-tabel)
- Competitiesoort

De wedstrijden worden opgeslagen in `his.matches` met `ClubCode = 'ALLSTARS'`.

### Teams in ALLSTARS-modus

De planner-logica zoekt teamdata op via `avg.Teambegeleiding WHERE ClubCode = 'ALLSTARS'`. Voeg fictieve teambegeleiders toe via het CSV-importscript of direct in SQL.

### Speeltijden voor testdata

De testmodus deelt de `dbo.Speeltijden`-tabel met de echte club. De koppeling in de planner werkt op teamnaam-prefix: `JO9-2` matcht op `JO9`. Voeg speeltijden toe via **Instellingen → Speeltijden** (in normale modus).

---

## Testmodus activeren en verlaten

### Activeren

Klik op de knop **Testmodus** onderaan de zijbalk (onder de gebruikersnaam). Dit:
1. Slaat `ALLSTARS` op als geselecteerde club in `localStorage`
2. Navigeert naar `/testdata/wedstrijden`
3. Toont "ALLSTARS (testmodus)" als clubnaam in de header

### Verlaten

Klik op **Testmodus — verlaten** (gele knop, verschijnt in plaats van de normale Testmodus-knop). Dit:
1. Verwijdert de ALLSTARS-selectie uit `localStorage`
2. Schakelt terug naar de primaire club (eerste club met `SyncEnabled = true` in `dbo.AppSettings`)
3. Navigeert naar het dashboard

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

## Technische werking

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

`ClubSelectorService` slaat de geselecteerde clubcode op in `localStorage`. De waarde `ALLSTARS` wordt **niet** teruggezet naar de primaire club bij paginawissel — `MainLayout.LaadClubsAsync` bevat een expliciete uitzondering hiervoor:

```csharp
if (ClubSelector.SelectedClubCode == null ||
    (!_clubs.Any(c => c.ClubCode == ClubSelector.SelectedClubCode) &&
     ClubSelector.SelectedClubCode != "ALLSTARS"))
{
    // alleen resetten als het geen ALLSTARS is
    await ClubSelector.SelectClubAsync(primary);
}
```

---

## AVG en security

- De ALLSTARS-testdata bevat **geen echte persoonsgegevens** — alle namen en tijden zijn fictief.
- De teamnamen (bijv. `Jan de Vries`, `trainer@voorbeeld.nl`) volgen het [John Doe-principe](../CLAUDE.md): bewust niet-identificeerbaar.
- De testdata staat in dezelfde database als de echte data, maar is volledig geïsoleerd via de `ClubCode = 'ALLSTARS'` discriminator.
- Productie-API's (Sportlink, Microsoft Graph) worden in testmodus **niet** aangesproken.
