# Sportlink-dataservice (lezen) — endpoints, synchronisatiestrategie en veldreferentie

Dit document beschrijft de **leesrichting**: de publieke club-dataservice van Sportlink waaruit de
nachtelijke synchronisatie de teams, wedstrijden en wedstrijddetails ophaalt. De schrijfrichting
(webapp → Sportlink Club) staat in [SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md), de
automatische login in [SPORTLINK-AUTOLOGIN.md](SPORTLINK-AUTOLOGIN.md).

> Bij #1580 uit `FunctionApp/AGENTS.md` en het rootbestand `AGENTS.md` hierheen verplaatst, zodat die
> instructiebestanden binnen het budget van de agents passen. De harde regels (nooit een request-URL
> loggen, `/uitslagen` voegt geen toekomstige wedstrijden toe, sync-bereik) staan daar nog compact; dit
> document is de uitwerking. Voorbeeldwaarden zijn bewust placeholders: echte teamnamen, plaatsnamen en
> namen van personen horen niet in de repository (AVG, zie `SECURITY.md`).

## Basis

- Basis-URL: `https://data.sportlink.com`. Authenticatie met de queryparameter `clientId=<waarde>`;
  de waarde staat in `dbo.AppSettings.SportlinkClientId` (SQL Server-tier) of
  `public.appsettings` (Postgres-tier), nooit in code of configuratiebestanden.
- **Log of commit nooit een Sportlink-request-URL.** De `clientId` is geclassificeerd als publieke identifier
  (restrisico, geen secret; besluit eigenaar 2026-10-05, zie `SECURITY.md`, "Classificatie: Sportlink-clientId"),
  maar die classificatie is geen vrijstelling: de waarde komt niet in git, logs of telemetrie. De URL bevat
  de `clientId`; log daarom het endpoint plus de `wedstrijdcode` (of een andere niet-identificerende
  aanduiding). CI blokkeert een logtemplate met een URL-placeholder (#1200).
- Documentatie van Sportlink:
  - alle endpoints: <https://sportlinkservices.freshdesk.com/nl/support/solutions/articles/9000062942-lijst-met-artikelen-van-club-dataservice>
  - online testtool: <https://sportlinkservices.github.io/navajofeeds-json-parser/article/?programma>
  - JSON-parserdocumentatie: <https://sportlinkservices.github.io/navajofeeds-json-parser/article/>

## Endpoints in gebruik

| Endpoint | URL | Betekenis |
|---|---|---|
| teams | `/teams?clientId=` | Alle teams van de club |
| programma | `/programma?clientId=&weekoffset=` | **Primaire bron** voor alle wedstrijden (competitie, beker, oefenwedstrijden). Bevat scheidsrechter, veld, kleedkamers, logo's, vertrek- en verzameltijd |
| uitslagen | `/uitslagen?clientId=&weekoffset=` | **Alleen scoreverrijking** voor verleden wedstrijden: voegt uitslag, uitslag-regulier enzovoort toe aan bestaande programma-rijen. Voegt **geen** nieuwe toekomstige wedstrijden toe |
| wedstrijd-informatie | `/wedstrijd-informatie?clientId=&wedstrijdcode=` | Volledig detail per wedstrijd |

Beschikbaar maar niet in gebruik: `/standen?clientId=` (competitiestanden) en `/spelers?clientId=`
(spelerslijst).

## Synchronisatiestrategie: programma versus uitslagen

`/programma` is de **single source of truth** voor toekomstige wedstrijden. `/uitslagen` mag:

- bestaande rijen verrijken met scorevelden (alleen de score-kolommen bijwerken);
- nieuwe rijen toevoegen voor verleden wedstrijden die niet via `/programma` binnenkwamen.

`/uitslagen` mag **niet**:

- gedeelde velden overschrijven (wedstrijd, thuisteam, uitteam, veld, aanvangstijd, enzovoort);
- nieuwe rijen toevoegen voor toekomstige wedstrijden.

De timer (`FETCH_SCHEDULE`, standaard `0 0 4 * * *`) en de handmatige sync halen standaard vorige week
tot en met het einde van het seizoen op (uit `dbo.Season`); `?reset=true&season=YYYY` haalt een heel
seizoen opnieuw op.

## Programma-endpoint — alle beschikbare velden

Live opgehaald uit `/programma`. De voorbeeldwaarden zijn placeholders.

| Veld | Type | Betekenis | Voorbeeld |
|---|---|---|---|
| `wedstrijddatum` | datetime | Wedstrijdmoment, ISO 8601 | `2026-03-30T20:15:00+0200` |
| `wedstrijdcode` | int | Unieke wedstrijd-ID (8 cijfers) | `19816434` |
| `wedstrijdnummer` | int | Wedstrijdnummer | `19780` |
| `teamnaam` | string | Eigen teamnaam | `[ClubCode] 8` |
| `thuisteamclubrelatiecode` | string | Clubcode thuisteam | `BBBZXXXX` |
| `uitteamclubrelatiecode` | string | Clubcode uitteam | `BBBZYYYY` |
| `thuisteamid` | int | Thuisteam-ID | `99007` |
| `thuisteam` | string | Naam thuisteam | `[ClubCode] 8` |
| `thuisteamlogo` | string | Logo-URL thuisteam | `https://binaries.sportlink.com/...` |
| `uitteamid` | int | Uitteam-ID | `222309` |
| `uitteam` | string | Naam uitteam | `[Tegenstander] 8` |
| `uitteamlogo` | string/null | Logo-URL uitteam | `https://binaries.sportlink.com/...` |
| `teamvolgorde` | int | Volgorde van het team | `8` |
| `competitiesoort` | string | Soort competitie | `regulier`, `Oefenwedstrijd` |
| `competitie` | string | Naam van de competitie | `0214 Mannen Zaterdag reserve` |
| `klasse` | string | Klasse | `7e klasse` |
| `poule` | string | Poule | `07 (M)` |
| `klassepoule` | string | Klasse en poule | `7e klasse 07 (M)` |
| `kaledatum` | datetime | Kalenderdatum (SQL-formaat) | `2026-03-30 00:00:00.00` |
| `datum` | string | Leesbare datum | `30 mrt.` |
| `vertrektijd` | string | Vertrektijd | `08:35` |
| `verzameltijd` | string | Verzameltijd | `19:30` |
| `aanvangstijd` | string | Aanvangstijd | `20:15` |
| `wedstrijd` | string | Omschrijving | `[ClubCode] 8 - [Tegenstander] 8` |
| `status` | string | Status | `Te spelen` |
| `scheidsrechters` | string | Volledige omschrijving scheidsrechter | `[Naam scheidsrechter] (Scheidsrechter)` |
| `scheidsrechter` | string | Naam scheidsrechter | `[Naam scheidsrechter]` |
| `accommodatie` | string | Naam van de accommodatie | `[Sportparklocatie]` |
| `veld` | string | Veldaanduiding | `veld 3` |
| `locatie` | string | Soort locatie | `Veld`, `Outdoor` |
| `plaats` | string | Plaats | `[PLAATS]` |
| `rijders` | string/null | Rijders | `null` |
| `kleedkamerthuisteam` | string | Kleedkamer thuisteam | `1` |
| `kleedkameruitteam` | string | Kleedkamer uitteam | `8` |
| `kleedkamerscheidsrechter` | string | Kleedkamer scheidsrechter | (leeg) |
| `meer` | string | Link naar het wedstrijddetail | `wedstrijd-informatie?wedstrijdcode=19816434` |

`/programma` is dus de primaire bron voor alle wedstrijden (competitie, beker én oefenwedstrijden);
`/uitslagen` verrijkt alleen bestaande rijen met scores en voegt historische rijen toe voor verleden
wedstrijden.

## Een nieuwe databron toevoegen (SQL Server-tier, `FunctionApp/`)

1. Maak een entiteitsklasse in `Enitities.cs` (de typefout in de bestandsnaam is legacy).
2. Voeg een mappingrij toe in de tabel `mta.source_target_mapping`.
3. Voeg de fetch-logica toe in `Function1.cs`.
4. Voeg een mergeprocedure toe aan het databaseproject.
5. Werk `Utilities.cs` bij als er nieuwe instellingen nodig zijn.
6. **Doe hetzelfde op de Postgres-tier** (`FunctionApp.Postgres/Sync/` en `Database.Postgres/`): een feature
   bestaat op alle gebouwde tiers of op geen (zie `docs/ARCHITECTUUR.md` §8.4).

## Databaseproblemen onderzoeken (alleen lokaal)

```sql
-- Laatste rijen in de historietabel bekijken
SELECT TOP 10 * FROM [his].[teams] ORDER BY mta_inserted DESC;
EXEC sp_MergeStgToHis @sourceTable='teams', @targetTable='teams';
```

> **Destructief — alleen op de lokale ontwikkeldatabase, nooit in productie:** de historietabellen
> verwijderen laat ze bij de volgende run opnieuw aanmaken en wist alle historie.
>
> ```sql
> DROP TABLE IF EXISTS [his].[teams];
> DROP TABLE IF EXISTS [his].[matches];
> DROP TABLE IF EXISTS [his].[matchdetails];
> ```

Een geplande uitvoering controleer je in de logs van de Function App (Azure Portal) of lokaal in de
console. Handmatig starten: `GET http://localhost:7094/api/sync-matches` (SQL Server-tier; vereist een
Entra ID-Bearer-token met de rol `admin`, sinds #1350 werkt de Azure master key niet meer).
