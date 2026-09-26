# Sportlink API Documentatie

**Basis-URL (lokaal):** `http://localhost:7094/api`
**Basis-URL (productie):** `https://<function-app-hostnaam>/api` — de hostnaam van de Function App
van jouw deployment; zie `servers` in `docs/api-standaarden/openapi.yaml`.

> **Tier-opmerking:** sinds #1266 bestaan **alle** endpoints op beide tiers (`FunctionApp` = SQL
> Server, `FunctionApp.Postgres` = Postgres, productie sinds #976) — ook de `/sportlink/*`- en
> `/beheer/sportlink-extensie/*`-endpoints van de Sportlink Web Extension (epic #986). De tiers zijn
> gelijkwaardig; `scripts/ci/check-tier-pariteit.sh` bewaakt dat een endpoint niet op één tier kan
> blijven bestaan. Zie `docs/ARCHITECTUUR-DATABASE-TIERS.md` voor de tier-strategie.
>
> **De enige route die per tier verschilt is de handmatige synchronisatie:**
> `GET /api/postgres/sync-matches` op de Postgres-tier, `GET /api/sync-matches` op de SQL
> Server-tier. Op de andere tier geeft die route `404`.
>
> Eén autorisatieverschil tussen de tiers, bewust: de `/sportlink/*`-endpoints vereisen op de
> Postgres-tier alleen de rol `Wedstrijdzaken` (die vervangt daar de admin-check), en op de SQL
> Server-tier `Wedstrijdzaken` **bovenop** `admin`
> (`FunctionApp/Sportlink/SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync`). De aanbevolen
> roltoewijzing `["admin","Wedstrijdzaken"]` voldoet aan beide.

## Beveiliging

Vier beveiligingsniveaus:

| Niveau | Sleutel | Wie | Endpoints |
|--------|---------|-----|-----------|
| **Anoniem** | geen | iedereen | `GET /api/health` |
| **Master key** | `?code=` queryparameter met de Azure **Master key** (`AuthorizationLevel.Admin`) | Timer/operator, integraties | Uitsluitend `GET /api/postgres/sync-matches` (Postgres-tier) en `GET /api/sync-matches` (SQL Server-tier) |
| **Admin** | Easy Auth Bearer + `admin`-rol (`EasyAuthHelper.RequireAdmin`) | Alleen coördinator | Alle overige endpoints: `/api/beheer/*`, `/api/planner/*`, `/api/feedback/*`, `/api/test/*` |
| **Wedstrijdzaken** | Easy Auth Bearer + `Wedstrijdzaken`-rol (`EasyAuthHelper.RequireWedstrijdzaken`) | Wedstrijdsecretariaat | Alle `/api/sportlink/*` (op de SQL Server-tier bovendien óók de `admin`-rol) |

> **`AuthorizationLevel` in de trigger zegt niets over de echte poort.** Op één na staat elk
> endpoint op `AuthorizationLevel.Anonymous` — dat betekent alleen "geen Function key". De
> daadwerkelijke rolcontrole gebeurt in de functie zelf, via `EasyAuthHelper.RequireAdmin` /
> `RequireWedstrijdzaken`, direct of via de wrappers `AdminEndpoint.ExecuteAsync` (default
> `RequireAdmin`) en — op de SQL Server-tier — `PlannerFunction.HandleAsync` (`RequireAdmin`).
> **Er is geen endpoint dat met alleen een Function key te benaderen is**, behalve de twee
> sync-routes hierboven.
>
> `EasyAuthHelper.RequireAuthenticated` (`admin` óf `user`) bestaat wel in de code, maar wordt
> **nergens aangeroepen**. Er is dus geen endpoint waar de `user`-rol toegang geeft; een gebruiker
> met alleen `user` krijgt overal `403`.

Zonder token → `401 Unauthorized`. Mét geldig token maar zonder de vereiste rol → `403 Forbidden`
met body `{ "error": "Forbidden: vereiste rol ontbreekt" }`. In beide gevallen vindt er geen
verwerking plaats.

> **Lokaal:** ontbreekt de omgevingsvariabele `WEBSITE_SITE_NAME` (dus buiten Azure), dan slaat
> `RequireRole` de controle over en is elk endpoint zonder token bereikbaar. Dat is een
> bewuste dev-bypass en geldt nooit in productie.

---

## Overzicht endpoints

| Methode | Endpoint | Niveau | Beschrijving |
|---------|----------|--------|-------------|
| `GET` | `/health` | Anoniem | Status, versie, tier-herkomst (#863) — zie hieronder |
| `GET` | `/postgres/sync-matches` | **Master key** (`?code=`) | Handmatige Sportlink-synchronisatie — **Postgres-tier (productie)**. Antwoordt `200`, `207` (deelstappen mislukt, `lastsynctimestamp` niet bijgewerkt) of `500`. Bestaat niet op de SQL Server-tier |
| `GET` | `/sync-matches` | **Master key** (`?code=`) | Handmatige Sportlink-synchronisatie — **SQL Server-tier**, zelfde parameters (`reset`, `season`). Antwoordt `200` of `500`. Bestaat niet op de Postgres-tier |
| `GET/PUT` | `/beheer/settings` | **Admin** | Club-instellingen ophalen/opslaan (incl. Sportlink Web Extension-schakelaar) |
| `GET` | `/beheer/geocode` | **Admin** | Adres → GPS-coördinaten opzoeken voor de accommodatie-instelling |
| `GET` | `/beheer/sync/status` | **Admin** | Status van de laatste Sportlink-synchronisatie, plus optioneel `?jobId=` voor een specifieke sync-job (#1138) |
| `POST` | `/beheer/sync/trigger` | **Admin** | Synchronisatie starten via een Storage Queue-job (#1138) — geeft direct een `jobId` terug, geen fire-and-forget meer |
| `GET` | `/beheer/teams` | **Admin** | Teamlijst ophalen |
| `GET` | `/beheer/templates` | **Admin** | Alle e-mailtemplates per berichttype ophalen |
| `PUT` | `/beheer/templates/{key}` | **Admin** | Eén e-mailtemplate opslaan |
| `POST` | `/beheer/templates/{key}/reset` | **Admin** | Eén e-mailtemplate terugzetten naar standaard |
| `GET/POST` | `/beheer/uitgesloten-emails` | **Admin** | E-mailadressen uitsluiten van automatische antwoorden: lijst ophalen / toevoegen |
| `DELETE` | `/beheer/uitgesloten-emails/{id}` | **Admin** | Uitsluiting verwijderen |
| `GET/POST` | `/beheer/voorkeurstijden` | **Admin** | Gewenste speeltijden per team: lijst ophalen / toevoegen |
| `PUT/DELETE` | `/beheer/voorkeurstijden/{id}` | **Admin** | Gewenste speeltijd wijzigen / verwijderen |
| `GET/POST` | `/beheer/teamregels` | **Admin** | Planningsregels per team (bijv. buffertijd): lijst ophalen / toevoegen |
| `PUT/DELETE` | `/beheer/teamregels/{id}` | **Admin** | Planningsregel wijzigen / verwijderen |
| `GET` | `/beheer/email-log` | **Admin** | Verwerkte e-mails inzien (AVG-conform: geen berichtteksten) |
| `POST` | `/test/email` | **Admin** | AI-classificatie dry-run zonder e-mail te versturen (Email-tester-pagina) |
| `POST` | `/feedback/validate` | **Admin** | Feedback-widget: voorvalidatie op volledigheid |
| `POST` | `/feedback/preview` | **Admin** | Feedback-widget: exacte titel + body van het te publiceren issue opvragen, zónder iets aan te maken (#1205) |
| `POST` | `/feedback/submit` | **Admin** | Feedback-widget: publiceren als **openbaar** GitHub-issue; met `bevestiging` wordt exact de in het voorbeeld getoonde tekst gepubliceerd |
| `POST` | `/planner/check-availability` | **Admin** | Veldbeschikbaarheid controleren — gescoped op `X-Club-Code` header |
| `POST` | `/planner/doordeweeks-beschikbaar` | **Admin** | Doordeweekse beschikbaarheid door het seizoen heen — gescoped op `X-Club-Code` header |
| `POST` | `/planner/bevestig` | **Admin** | Wedstrijdslot boeken |
| `POST` | `/planner/populate-sunset` | **Admin** | Zonsondergangtabel vullen |
| `POST` | `/planner/zoek-wedstrijd` | **Admin** | Bestaande wedstrijd zoeken — gescoped op `X-Club-Code` header |
| `POST` | `/planner/herplan-check` | **Admin** | Herplan-alternatieven simuleren — gescoped op `X-Club-Code` header |
| `POST` | `/planner/herplan-bevestig` | **Admin** | Herplanverzoek registreren |
| `POST` | `/planner/auto-plan` | **Admin** | **Dagplanning optimaliseren** — regels → voorkeurstijden → leeftijdsdefaults |
| `POST` | `/planner/auto-plan/toepassen` | **Admin** | Berekende planning wegschrijven (alleen testmodus ALLSTARS) |
| `GET` | `/planner/veldbezetting?datum=` | **Admin** | Wedstrijden op een datum, zonder optimalisatie-berekening |
| `GET` | `/planner/team-schedule` | **Admin** | Wedstrijdschema per team — gescoped op `X-Club-Code` header |
| `GET` | `/beheer/teambegeleiding` | **Admin** | Alle teams met begeleiding in database |
| `GET` | `/beheer/teambegeleiding/{team}` | **Admin** | Begeleiders van team (naam + rol, nooit e-mail) |
| `POST` | `/beheer/teambegeleiding/doorsturen` | **Admin** | Vraag doorsturen (BCC coördinator). `ontvangers` bepaalt de ontvangers (max 15, gevalideerd, uitsluitingslijst gecontroleerd); leeg → server-side coach-lookup (#765) |
| `POST` | `/beheer/teambegeleiding/import` | **Admin** | CSV-import van begeleiders — vervangt de rijen van de club atomisch (DELETE + inserts + audit-rij in één transactie, rollback bij elke fout; #1131/#1132). Kolomlengtes worden vóór elke destructieve stap gevalideerd; een te lange waarde geeft `400` met `{ error, fouten: [...] }` (rij/kolom-omschrijving per overtreding) en laat de vorige import ongemoeid. Postgres-tier serialiseert vervangingen per club (`pg_advisory_xact_lock`) zodat twee gelijktijdige imports elkaar nooit tot een vereniging van beide batches kunnen combineren. CSV wordt in-memory verwerkt en nooit opgeslagen; `avg.ImportLog` bevat alleen metadata — geen PII |
| `GET/POST` | `/beheer/speeltijden` | **Admin** | Speeltijden per leeftijdscategorie: lijst ophalen / toevoegen |
| `PUT/DELETE` | `/beheer/speeltijden/{leeftijd}` | **Admin** | Speeltijd van één leeftijdscategorie wijzigen / verwijderen |
| `GET` | `/beheer/leermomenten` | **Admin** | Classificatie-leermomenten ophalen (`?status=pending\|validated\|rejected`) |
| `GET` | `/beheer/leermomenten/stats` | **Admin** | Aantallen leermomenten per status |
| `PUT` | `/beheer/leermomenten/{id}/valideer` | **Admin** | Leermoment valideren of afwijzen (`{ "actie": "valideer"\|"afwijzen" }`) |
| `GET` | `/beheer/teamaliassen` | **Admin** | Teamnaam-aliassen ophalen (`?status=pending\|validated\|rejected&limit=100`) — inclusief canonieke teamnaam |
| `POST` | `/beheer/teams/herstel` | **Admin** | Canonieke teamlijst opnieuw opbouwen uit `his.teams` (Postgres-tier; `his.Teams` op de SQL Server-tier): volledige canonicalisatie + sleutelmigratie (#766). Idempotent. `409` als er nog niets gesynchroniseerd is — "niets te doen" is bewust geen `200` (#946) |
| `PUT` | `/beheer/teamaliassen/{id}/valideer` | **Admin** | Alias goedkeuren of afwijzen (`{ "status": "validated"\|"rejected" }`) |
| `DELETE` | `/beheer/teamaliassen/{id}` | **Admin** | Alias definitief verwijderen |
| `GET` | `/beheer/theme` | **Admin** | Club-thema ophalen (kleuren + website-URL + `lightColors`/`darkColors`) — gefilterd op `X-Club-Code` header. De paletten zijn `null` zolang er geen licht/donker-set is ingesteld; de client valt dan terug op de vier platte kleuren |
| `PUT` | `/beheer/theme` | **Admin** | Club-thema opslaan (`{ primary, secondary, accent, textOnPrimary, clubWebsiteUrl, faviconUrl, logoUrl, lightColors, darkColors }`) — gefilterd op `X-Club-Code` header. `lightColors`/`darkColors` zijn sleutel→hex-objecten voor het volledige palet per modus (#1254); elke sleutel moet `^[a-z][a-zA-Z0-9-]{0,39}$` zijn en elke waarde `#rrggbb` of `#rrggbbaa`, maximaal 40 per palet |
| `POST` | `/beheer/theme/extract?url=` | **Admin** | Kleuren extraheren uit club-website (SSRF-beschermd) |
| `GET` | `/beheer/clubs` | **Admin** | Lijst van beschikbare clubs (`[{ clubCode, clubName }]`) voor de GUI-selector |
| `GET` | `/beheer/sportlink-extensie/rollen` | **Admin** | Sportlink Web Extension (#986/#988): per functionele rol tonen of een eigen Sportlink-serviceaccount gekoppeld is, door wie en wanneer |
| `PUT` | `/beheer/sportlink-extensie/rollen/{rolNaam}` | **Admin** | Koppeling registreren/overschrijven voor een rol (`{ SportlinkAccountNaam }`) — `LaatstGekoppeldDoor` altijd server-bepaald |
| `PUT` | `/beheer/sportlink-extensie/rollen/{rolNaam}/token` | **Admin** | Refresh-token productie-persistent registreren (`{ RefreshToken }`) — write-only, geen GET-tegenhanger, valideert vóór opslag (#990/#991) |
| `GET` | `/beheer/sportlink-extensie/health?live=false` | **Admin** | Statussectie: extension/dry-run-instelling, koppeling + laatste tokenverversing per rol, laatste mutatiefout, laatste contract-check. Zonder `live=true` geen Sportlink-aanroep; `live=true` doet één tokenverversing + één leesaanroep (#998) |
| `GET` | `/beheer/rolfeatureinstellingen` | **Admin** | Per-club, per-rol feature-instellingen voor de Wedstrijdzaken-rol ophalen (kleedkamers/scheidsrechter/veld) — fail-closed (#1341, epic #1338) |
| `PUT` | `/beheer/rolfeatureinstellingen` | **Admin** | Eén FeatureKey aan/uit zetten (`{ FeatureKey, Enabled }`) — `admin` is hier nooit instelbaar, die rol heeft altijd alles aan (#1341) |
| `GET` | `/sportlink/match/{wedstrijdcode}` | **Wedstrijdzaken** | Read-only wedstrijdgegevens uit Sportlink Club: PublicMatchId-cache/reverse-lookup + permissievlaggen (#987/#991); sinds #1339 ook het huidige veld (`fieldId`/`fieldSize`, prefill) en server-berekende `veldOpties`/`subpositieOpties` (voorstellen op onze eigen veldnaam, géén Sportlink-gegeven); sinds #1341 ook de per-rol feature-toestemmingen (`KleedkamersFeatureToegestaan`/`ScheidsrechterFeatureToegestaan`/`VeldFeatureToegestaan`); sinds #1340 (VOORSTEL, DPO-vraag nog niet bevestigd — zie `docs/SPORTLINK-WEB-EXTENSION.md` §8) ook de relatiecode van de huidige scheidsrechter/AR1/AR2 (`ScheidsrechterRelatieCode`/`Ar1RelatieCode`/`Ar2RelatieCode`, uitsluitend het relatiecode-veld, nooit een naam) — genuld als `ScheidsrechterFeatureToegestaan` false is; het exacte Sportlink-JSON-veldnaam voor de relatiecode is NOOIT live geverifieerd |
| `GET` | `/sportlink/match/{wedstrijdcode}/public-match-id` | **Wedstrijdzaken** | Lichtgewicht variant — alleen `PublicMatchId` (cache/reverse-lookup, geen volledige Match-aanroep), voor de deep-link-knop in Dagplanning (#989) |
| `PUT` | `/sportlink/match/{wedstrijdcode}/dressingrooms` | **Wedstrijdzaken** | Kleedkamers toewijzen — eerste echte Sportlink-mutatie, guardrail + audit-log (#992); sinds #1341 ook geweigerd (409) als de per-rol feature-instelling uitstaat |
| `PUT` | `/sportlink/match/{wedstrijdcode}/field` | **Wedstrijdzaken** | Veld(deel) wijzigen — `IsForceUpdate` server-side altijd `false` (semantiek onbevestigd, #993); sinds #1341 ook geweigerd (409) als de per-rol feature-instelling uitstaat |
| `PUT` | `/sportlink/match/{wedstrijdcode}/officials` | **Wedstrijdzaken** | Officials (scheidsrechter/AR1/AR2) toewijzen — scaffolding, endpoint/body ONBEVESTIGD en altijd code-gelockt (`forceDryRun`, onafhankelijk van `sportlinkDryRun`); alleen relatiecode/persoons-ID, geen namen (AVG, #994); sinds #1341 ook geweigerd (409) als de per-rol feature-instelling uitstaat |
| `PUT` | `/sportlink/match/{wedstrijdcode}/change-request` | **Wedstrijdzaken** | Wijzigingsverzoek datum/tijd/accommodatie — **ALLEEN stap 1 (valideren)** van Sportlinks tweestaps flow, `Toelichting` verplicht; ONBEVESTIGD en altijd code-gelockt (`forceDryRun`, onafhankelijk van `sportlinkDryRun`); enige mutatie die een echte tegenstander raakt — stap 2 (bevestigen) is bewust niet gebouwd (#995) |
| `GET` | `/sportlink/change-requests` | **Wedstrijdzaken** | Inkomende wijzigingsverzoeken van tegenstanders ophalen (#996), sinds #1111 verrijkt met eigen wedstrijdcontext (`Wedstrijd`: nummer, teams, datum, tijd, accommodatie uit `his.matches` via de PublicMatchId-cache; `null` als niet gecachet) en met openstaande (`CONFIRM`) verzoeken vooraan |
| `PUT` | `/sportlink/change-requests/{publicRequestId}/action` | **Wedstrijdzaken** | Wijzigingsverzoek goedkeuren (`Actie=APPROVE`) of afwijzen (`Actie=DENY`, `Remarks` verplicht) (#996) |
| `POST` | `/sportlink/club-match` | **Wedstrijdzaken** | Oefenwedstrijd ("clubwedstrijd") aanmaken — scaffolding, endpoint/body ONBEVESTIGD en altijd code-gelockt (`forceDryRun`, onafhankelijk van `sportlinkDryRun`); geen `SportlinkMutationGuard` (er is vooraf geen bestaande wedstrijd), alleen eigen toggle/EgressGuard-check. Body sinds #1116: `MatchDateTime`, `Duration`, `TeamNaam` (actief clubteam uit eigen database), `Tegenstander` (vrije tekst), `VeldNummer`, `Description` — de server leidt `PublicHomeTeamId` (`his.teams.teamcode` via de gevalideerde teamaliassen), `AgeClassCode` (leeftijdscategorie van het team) en `FacilityId` (club-instelling `accommodatie`, opgezocht in de Sportlink-locatielijst) zelf af en meldt wat niet lukte als `Waarschuwingen`. Verwijderen/uitslag bewust niet gebouwd (#997) |
| `GET` | `/sportlink/club-match/picklists` | **Wedstrijdzaken** | De twee Sportlink-picklists (Teams + Location) — read-only, persoonsgegevensvrij. Sinds #1116 niet meer door het formulier gebruikt; diagnostisch endpoint voor de mens die de ClubMatch-body live bevestigt (welke ID-vorm hanteert Sportlink Club?) (#997) |
| `GET/POST` | `/beheer/velden` | **Admin** | Velden beheren: naam, type (vrije tekst), kunstlicht, actief — per club vrij instelbaar (#679). Lijst ophalen / toevoegen |
| `PUT` | `/beheer/velden/{veldNummer}` | **Admin** | Veld wijzigen. **Er is geen DELETE** — een veld wordt op inactief gezet in plaats van verwijderd |
| `GET/POST` | `/beheer/veldbeschikbaarheid` | **Admin** | Openingsvenster per veld per weekdag, optioneel gekoppeld aan een periode (`PeriodeId`, #581). Lijst ophalen / toevoegen |
| `PUT/DELETE` | `/beheer/veldbeschikbaarheid/{id}` | **Admin** | Openingsvenster wijzigen / verwijderen |
| `GET/POST` | `/beheer/veldtraining` | **Admin** | Terugkerende trainingsbezetting per veld per weekdag — telt mee als bezetting in planner en e-mailreacties (#679). Lijst ophalen / toevoegen |
| `PUT/DELETE` | `/beheer/veldtraining/{id}` | **Admin** | Trainingsbezetting wijzigen / verwijderen |
| `GET/POST` | `/beheer/veldperiodes` | **Admin** | Herbruikbare regimes (bijv. "Zomerstop", "Competitie") met een geldigheidsrange; koppel een veldbeschikbaarheid-venster eraan om het alleen tijdens die periode te laten gelden (#581). Lijst ophalen / toevoegen |
| `PUT/DELETE` | `/beheer/veldperiodes/{id}` | **Admin** | Periode wijzigen / verwijderen |
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

**HTTP-statuscode (#859):** `database: "unconfigured"` (geen bruikbare connectiereeks — env var
ontbreekt of is van de verkeerde engine) geeft **503**, niet 200. Elke andere `database`-waarde
(ook `paused`/`timeout`/`unavailable`, tijdelijke toestanden) blijft 200 met `status: "degraded"`.

### Antwoord

Voorbeeld van de **Postgres-tier** (de tier die in productie draait):

```json
{
  "status": "ok",
  "version": "3.5.3.1",
  "timestamp": "2026-09-19T15:00:00Z",
  "database": "online",
  "settingsLoaded": true,
  "lastSync": "2026-09-19T04:00:12Z",
  "syncStale": false,
  "tier": "Postgres",
  "provider": "Npgsql",
  "serverVersion": "17.4",
  "tlsMode": "VerifyFull",
  "tlsWarning": null,
  "schemaWarning": null,
  "pendingMigrations": []
}
```

Op de **SQL Server-tier** ontbreken `lastSync`, `syncStale`, `tlsMode`, `tlsWarning`,
`schemaWarning` en `pendingMigrations`; `tier` is dan `"SqlServer"` en `provider`
`"Microsoft.Data.SqlClient"`.

| Veld | Type | Beschrijving |
|---|---|---|
| `status` | `string` | `"ok"` als `database` `"online"` is, `settingsLoaded` `true` is en — alleen op de Postgres-tier — `pendingMigrations` leeg is **én** `syncStale` `false` is op een omgeving waar een synchronisatie hoort te draaien (`EgressGuard` laat uitgaand verkeer toe). Anders `"degraded"`. Op de SQL Server-tier tellen alleen `database` en `settingsLoaded` mee |
| `version` | `string` | Vierdelig assembly-versienummer, bijv. `"3.5.3.1"` |
| `database` | `string` | `online`, `paused`, `timeout`, `unavailable` of `unconfigured` — `unconfigured` geeft HTTP 503 |
| `settingsLoaded` | `boolean` | `false` als de laatste poging om de clubinstellingen te laden mislukte (#859) — geen foutdetails hier, die staan in het functielog |
| `tier` | `string` | De databasetier waarmee dit artefact gebouwd is — zie `scripts/ci/database-tiers.json` |
| `provider` | `string` | De gebruikte databasedriver |
| `serverVersion` | `string \| null` | Versienummer van de databaseserver zelf; `null` als niet bereikbaar |
| `lastSync` | `string \| null` | Postgres-tier (#1081): laatste synchronisatietijd (UTC) van de primaire club; `null` als nooit gesynchroniseerd |
| `syncStale` | `boolean` | Postgres-tier (#1081): `true` als `lastSync` ouder is dan `SyncMaxAgeHours` (standaard 36) of ontbreekt; telt alleen mee in `status` waar een synchronisatie hoort te draaien |
| `tlsMode` | `string \| null` | Postgres-tier (#1095): de TLS-modus die daadwerkelijk geldt op de databaseverbinding (bijv. `VerifyFull`, `Require`); `null` bij `unconfigured` |
| `tlsWarning` | `string \| null` | Postgres-tier (#1095): `null` als het TLS-beleid (`VerifyFull`, #1004) gehaald is; anders een waarschuwing — zonder host of credentials — dat de verbinding wél versleuteld is maar certificaat/hostnaam niet volledig gevalideerd worden |
| `schemaWarning` | `string \| null` | Postgres-tier (#1098): `null` als `public.appsettings` alle kolommen heeft die deze versie verwacht; anders een waarschuwing dat de code vooruitloopt op het schema en welke migratie ontbreekt. De applicatie draait dan door op de migratie-default |
| `pendingMigrations` | `string[] \| null` | Postgres-tier (#1098): bestandsnamen uit `Database.Postgres/migrations/` die nog niet in de ledger `schema_migrations` staan. Leeg = code en schema lopen gelijk; `null` als de database niet bereikbaar is. Alleen lezen — toepassen blijft een handeling van de beheerder (`Database.Postgres.Cli`) |

**Vanaf 3.3.0.2 doet de Postgres-tier bij elke aanroep zelf één poging de clubinstellingen te laden**
zodra de database bereikbaar is, zodat `settingsLoaded` een feit is en geen aanname over een eerdere
poging. Direct na een herstart meldde het veld anders `true` terwijl het eerste beheerscherm daarna
op 500 zou lopen (#1098).

---

## GET /api/postgres/sync-matches (Postgres-tier) — GET /api/sync-matches (SQL Server-tier)

Handmatig een Sportlink API synchronisatie starten (teams, wedstrijden, wedstrijddetails).

**De route verschilt per tier.** Dit is het enige endpoint waarvoor dat geldt:

| Tier | Route | Implementatie |
|---|---|---|
| Postgres (productie) | `GET /api/postgres/sync-matches` | `FunctionApp.Postgres/Sync/SyncFunction.cs` |
| SQL Server | `GET /api/sync-matches` | `FunctionApp/Function1.cs` |

**Authenticatie:** `AuthorizationLevel.Admin` — de Azure **Master key** via `?code=`. Dit is het
enige endpoint dat níet via Easy Auth + rolcheck loopt.

### Queryparameters

| Parameter | Type | Verplicht | Beschrijving |
|-----------|------|-----------|-------------|
| `reset` | `boolean` | Nee | `true` = volledige seizoensynchronisatie in plaats van incrementeel |
| `season` | `integer` | Nee | Startjaar seizoen (bijv. `2024`). Gebruikt met `reset=true`. Ontbreekt of onparseerbaar → stille terugval op de standaardmodus (vorige week t/m einde seizoen) |

### Voorbeeld

```
GET /api/postgres/sync-matches?code=<master-key>
GET /api/postgres/sync-matches?reset=true&season=2025&code=<master-key>
```

### Antwoord

| Status | Tier | Body |
|---|---|---|
| `200 OK` | beide | `"Sync voltooid. WeekOffset-bereik: {from} tot {to}."` |
| `207 Multi-Status` | **alleen Postgres** | `{ "status": "gedeeltelijk mislukt", "weekOffsetFrom": -1, "weekOffsetTo": 12, "melding": "..." }` — één of meer deelstappen zijn mislukt en `lastsynctimestamp` is bewust **niet** bijgewerkt (#1081). De geslaagde deelstappen blijven staan; het functielog noemt de betrokken fase(s) |
| `500` | beide | Onverwachte fout; zie het functielog |

De SQL Server-tier kent de `207` niet en antwoordt alleen `200` of `500`.

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
| `heelVeld` | `boolean` | Nee | Dwing een heel veld af, ook als de leeftijdscategorie normaal op een deelveld speelt. Levert een waarschuwing op, geen fout |

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

Bevestig en boek een wedstrijdslot. Schrijft naar `planner.geplandewedstrijden` (Postgres-tier; `planner.GeplandeWedstrijden` op de SQL Server-tier).

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
| `heelVeld` | `boolean` | Nee | Dwing een heel veld af, ook als de leeftijdscategorie normaal op een deelveld speelt |

### Response (200)

```json
{
  "id": 1,
  "datum": "2026-04-25",
  "aanvangsTijd": "12:00",
  "eindTijd": "13:15",
  "veldNummer": 3,
  "status": "Te bevestigen"
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
| `status` | `string` | Altijd `"Te bevestigen"` bij aanmaak |

### Foutantwoord (400)

```json
{
  "error": "Request body met 'datum', 'aanvangsTijd' en 'veldNummer' is verplicht."
}
```

Ook 400 bij een ongeldige duur (#1134): `"Wedstrijdduur moet groter zijn dan 0 minuten."`,
`"Wedstrijdduur van ... minuten is onwaarschijnlijk groot (max 480 minuten)."` of
`"Aanvangstijd plus wedstrijdduur overschrijdt het einde van de dag."`.

### Foutantwoord (409) — bezettingsconflict

Server-side controleert de aanvraag atomair tegen de bestaande bezetting (dezelfde notie van
conflict als `POST /api/planner/check-availability`): volledige-veldreserveringen die elkaar
overlappen, of gedeelde-veldreserveringen waarvan de veldfracties samen boven 1.00 uitkomen,
geven 409 in plaats van een stille dubbele boeking. Twee reserveringen die elkaar precies
aanraken (bijv. 10:00–11:00 gevolgd door 11:00–12:00) zijn GEEN conflict.

```json
{
  "error": "Veld 3 is op 2026-04-25 tussen 12:00 en 13:15 al bezet.",
  "conflicterendeWedstrijd": {
    "wedstrijd": "[ClubCode] JO13-1 - Tegenstander",
    "aanvangsTijd": "12:30",
    "eindTijd": "13:45",
    "veldNummer": 3,
    "veldDeelGebruik": 1.00,
    "bron": "Planner"
  }
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
| `bufferMinuten` | `integer` | Nee | Buffer tussen wedstrijden. Standaard 15. Teamspecifieke buffers uit `public.teamregels` gaan vóór als die groter zijn |

### Rangorde van het planningsdoel

Per wedstrijd wordt de streeftijd bepaald in deze vaste volgorde:

| Laag | Bron | `voorkeurBron` |
|---|---|---|
| 0 | `public.teamregels`, `regeltype = 'VoorkeurVeld'` (veld + optioneel tijd) | `regel` |
| 1 | `public.teamvoorkeurtijden` voor de betreffende dag van de week | `team` |
| 2 | `public.speeltijden.standaardvoorkeurtijd` van de leeftijdscategorie | `leeftijd` |
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
      "competitiesoort": "Competitie",
      "duurMinuten": 85,
      "veldafmeting": 1.00,
      "huidigeVeld": "veld 2",
      "huidigeTijd": "11:00",
      "heeftVeld": true,
      "heeftTijd": true,
      "optimaalVeldNummer": 3,
      "optimaalVeldNaam": "veld 3",
      "optimaalVeld": "veld 3",
      "optimaalTijd": "11:15",
      "status": "wijziging",
      "nietInplanbaaarReden": null,
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

> **`nietInplanbaaarReden` — let op de dubbele `a`.** De veldnaam bevat een typefout die in het
> wire-contract zit (`AutoPlanWedstrijdItem` op beide tiers). Hij is gevuld bij
> `status = "niet-inplanbaar"` en anders `null`. Hernoemen is een breaking change voor elke
> consument, dus de naam blijft zoals hij is.

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
`JO13-1`). Ze worden vastgelegd in `public.teamaliassen` met status `pending`. Alleen een alias met
status `validated` mag bij teamnaam-resolutie als vertrouwde exacte match gelden — zo kan een
onjuiste gok zich niet zelfversterken. Alles is gescoped op de club uit de `X-Club-Code` header.

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
Bestaat de tabel nog niet (op de Postgres-tier: migratie `003_admin_tables.sql` nog niet
toegepast; op de SQL Server-tier: post-deployment script niet uitgevoerd), dan volgt een lege
lijst met nullen in plaats van een fout.

> Het voorbeeld hierboven toont `"bron": "AiDisambiguatie"` — de waarde die de nu verwijderde
> forced-choice teamdisambiguatie (#697) bij een geleerde alias schreef. Sinds #1268 bestaat die
> functionaliteit op geen van beide tiers meer, dus dit is de weergave van een rij uit vóór die
> wijziging; nieuwe aliassen ontstaan niet meer langs deze weg. Zie
> `docs/ARCHITECTUUR-TEAMRESOLUTIE.md` voor de volledige achtergrond.

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

### Teamspecifieke regels (`public.teamregels`; `dbo.TeamRegels` op de SQL Server-tier)

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
# Postgres-tier — standaard lokaal (Start-Debug.ps1) én in productie
curl http://localhost:7094/api/postgres/sync-matches
curl "http://localhost:7094/api/postgres/sync-matches?reset=true&season=2025"

# SQL Server-tier — Start-Debug.ps1 -Tier SqlServer
curl http://localhost:7094/api/sync-matches
```

### Sync-job starten en pollen (Admin GUI, #1138)

`POST /beheer/sync/trigger` zet niet meer fire-and-forget een `Task.Run` op, maar schrijft een
job-rij en een bericht op de bestaande `AzureWebJobsStorage`-queue `sync-jobs`. Een aparte
QueueTrigger-functie (`SyncJobProcessor`) verwerkt het bericht en werkt de status bij.

```bash
curl -X POST http://localhost:7094/api/beheer/sync/trigger
# {"status":"gestart","jobId":"…","weekOffsetFrom":-1,"weekOffsetTo":15,"tijdstip":"…"}

curl "http://localhost:7094/api/beheer/sync/status?jobId=<jobId>"
# {"lastSyncTimestamp":"…","fetchSchedule":"…","status":"ok",
#  "job":{"id":"…","status":"running|succeeded|failed","weekOffsetFrom":-1,"weekOffsetTo":15,
#         "createdAt":"…","startedAt":"…","completedAt":null,"errorMessage":null}}
```

Zonder `?jobId=` geeft `/beheer/sync/status` de meest recente job terug. `job` is `null` zolang er
nog nooit een sync is gestart. Mogelijke `job.status`-waarden: `pending`, `running`, `succeeded`,
`failed` — bij `failed` bevat `errorMessage` de reden.
