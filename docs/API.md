# Sportlink API Documentatie

> **De specificatie bijhouden (verplicht bij elk nieuw, gewijzigd of verwijderd endpoint).** Nooit een
> endpoint-wijziging committen zonder de spec bij te werken: ze is het contract voor andere systemen,
> consumers en toekomstige agentsessies, en een verouderde spec misleidt (erger dan geen spec).
>
> ```
> □ docs/api-standaarden/openapi.yaml bijgewerkt (nieuwe route, gewijzigde parameters, nieuwe response)?
> □ docs/api-standaarden/openapi.json gesynchroniseerd — altijd uit de YAML gegenereerd, nooit met de hand?
> □ info.version in openapi.yaml bijgewerkt naar de huidige appversie?
> □ dit document (endpointtabel + voorbeelden) bijgewerkt?
> ```
>
> `openapi.json` regenereer je uit de YAML (vereist Python met PyYAML, `pip install pyyaml`):
>
> ```bash
> python3 -c "import yaml,json,io; s=yaml.safe_load(io.open('docs/api-standaarden/openapi.yaml',encoding='utf-8')); json.dump(s, io.open('docs/api-standaarden/openapi.json','w',encoding='utf-8'), indent=2, ensure_ascii=False)"
> ```
> (Op Windows kan `python` in plaats van `python3` heten.)

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
> **Geen autorisatieverschil tussen de tiers** — beide gebruiken dezelfde gedeelde orkestratie
> (`Planner.Endpoints/Sportlink/SportlinkEndpointSupportCore.ExecuteWedstrijdzakenAsync`, #1271).
> Sinds #1400 zijn de mutatie-endpoints (`PUT`/`POST` onder `/sportlink/*`) en de twee
> viewing-endpoints (`GET /sportlink/match/{wedstrijdcode}` en `.../public-match-id`) daadwerkelijk
> verschillend beveiligd:
> - **Mutaties:** beide poorten na elkaar controleren nu `Wedstrijdzaken` óf `admin`
>   (`EasyAuthHelper.RequireWedstrijdzaken`, eerste poort sinds #1376; tweede poort
>   `AdminEndpoint.ExecuteWedstrijdzakenOfAdminAsync`, sinds #1400 — vóór #1400 eiste die tweede
>   poort altijd `admin`). Netto-effect: `Wedstrijdzaken` alléén (zonder `admin`) triggert nu
>   daadwerkelijk een Sportlink-mutatie, bewust getest op beide tiers
>   (`Sportlink_AlleenWedstrijdzaken_PasseertDeAdminPoort`). Een gewone `user` zonder `Wedstrijdzaken`
>   krijgt hier nog steeds `403` (`Sportlink_AlleenUser_WordtGeweigerdOpDeWedstrijdzakenPoort`).
> - **Viewing:** loopt sinds #1400 via `AdminEndpoint.ExecuteAuthenticatedAsync` (elke ingelogde rol,
>   `admin` + `user`) — Planning en het Sportlink-paneel zijn generiek zichtbaar. Welke velden een
>   niet-Wedstrijdzaken-viewer daadwerkelijk terugkrijgt blijft server-side geredigeerd (zie de
>   endpointtabel hieronder en `docs/SPORTLINK-WEB-EXTENSION.md` §3.4).
>
> De aanbevolen roltoewijzing voor het wedstrijdsecretariaat blijft `["user","Wedstrijdzaken"]`
> (of `["admin"]`, dat impliceert alles).

## Beveiliging

Drie beveiligingsniveaus:

| Niveau | Sleutel | Wie | Endpoints |
|--------|---------|-----|-----------|
| **Anoniem** | geen | iedereen | `GET /api/health` — geaggregeerde status zonder diagnostics |
| **Admin** | Easy Auth Bearer + `admin`-rol (`EasyAuthHelper.RequireAdmin`) | Alleen coördinator | Alle overige `/api/beheer/*`- (inclusief het feedbackoverzicht `/api/beheer/feedback/*`), `/api/planner/*`- (behalve `veldbezetting`), `/api/test/*`-endpoints, én `GET /api/postgres/sync-matches` / `GET /api/sync-matches` (sinds #1350) |
| **Admin/User** | Easy Auth Bearer + `admin` óf `user` (`AdminEndpoint.ExecuteAuthenticatedAsync`, #1330) | Elke ingelogde gebruiker | `GET /api/planner/veldbezetting` (Planning-pagina, #1400), de drie `/api/feedback/*`-endpoints (#764: iedereen die is ingelogd mag feedback geven), `GET /api/sportlink/match/{wedstrijdcode}` en `.../public-match-id` (Sportlink-paneel viewen, #1400), plus de drie Teambegeleiding-lookup/doorstuur-endpoints (#1330) |
| **Wedstrijdzaken** | Easy Auth Bearer + `Wedstrijdzaken`-rol **óf** de `admin`-rol (`EasyAuthHelper.RequireWedstrijdzaken`, beide poorten sinds #1400 — vóór #1400 eiste de tweede poort altijd `admin`) | Wedstrijdsecretariaat | De Sportlink-mutatie-endpoints (`PUT`/`POST` onder `/api/sportlink/*`) |

> **`AuthorizationLevel` in de trigger zegt niets over de echte poort.** Élk endpoint staat op
> `AuthorizationLevel.Anonymous` — dat betekent alleen "geen Function key". De daadwerkelijke
> rolcontrole gebeurt via de centrale wrappers `AdminEndpoint.ExecuteAsync` (uitsluitend admin, met
> database-toegang), `AdminEndpoint.ExecuteZonderDatabaseAsync` (uitsluitend admin, zonder database —
> sinds #764 door geen enkel feedback-endpoint meer gebruikt), `AdminEndpoint.ExecuteAuthenticatedAsync` (elke ingelogde
> rol — admin + user, #1330/#1400, sinds #764 ook de drie `/api/feedback/*`-endpoints) en, voor de Sportlink-mutatie-endpoints, via
> `SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync` (Wedstrijdzaken óf admin, beide poorten sinds
> #1400). Twee bewuste uitzonderingen roepen `EasyAuthHelper.RequireAdmin` nog direct aan, vóór een
> eventuele databaseaanroep: `POST /api/beheer/theme/extract` (een luie URL-check vóór de
> databaseaanroep) en `GET /api/beheer/geocode` (geen database nodig) — zelfde poort, alleen zonder
> de databasewacht van de wrapper. **Sinds #1350 accepteert geen enkel endpoint nog een Function key
> of Master key** — ook de twee sync-routes niet meer (zie hieronder).
>
> **Uitgaande integraties (#857, #1676):** een endpoint dat een externe dienst aanroept (de twee sync-routes,
> `POST /api/beheer/sync/trigger`, `GET /api/beheer/geocode`, `POST /api/beheer/theme/extract`) antwoordt
> `503` met `{ "error": ... }` zolang `EgressGuard.ExternalIntegrationsAllowed()` onwaar is (buiten Azure-hosting
> zonder expliciete opt-in).
>
> `EasyAuthHelper.RequireAuthenticated` (`admin` óf `user`) is bij #1350 verwijderd omdat hij toen
> nergens werd aangeroepen. Sinds #1330 bestaat die combinatie weer, nu als de expliciet genoemde
> poort `AdminEndpoint.ExecuteAuthenticatedAsync` — zie de rij **Admin/User** hierboven voor welke
> endpoints dat zijn. Op elk ander endpoint krijgt een gebruiker met alleen `user` nog steeds `403`.

Zonder token → `401 Unauthorized`. Mét geldig token maar zonder de vereiste rol → `403 Forbidden`
met body `{ "error": "Forbidden: vereiste rol ontbreekt" }`. In beide gevallen vindt er geen
verwerking plaats.

> **Lokaal:** alleen met de expliciete schakelaar `LOCAL_AUTH_BYPASS=true` (gezet door
> `Start-Debug.ps1`) slaat `RequireRole` de controle over en is elk endpoint zonder token bereikbaar.
> Zonder schakelaar geldt de controle ook lokaal, en zodra de app in Azure draait
> (`WEBSITE_SITE_NAME` gezet) wordt de schakelaar geweigerd (#1700).

---

## Overzicht endpoints

| Methode | Endpoint | Niveau | Beschrijving |
|---------|----------|--------|-------------|
| `GET` | `/health` | Anoniem | Geaggregeerde readiness, versie en statusvlaggen — zie hieronder |
| `GET` | `/beheer/health` | **Admin** | Gedetailleerde healthinformatie voor diagnose; via `AdminEndpoint.ExecuteZonderDatabaseAsync` |
| `GET` | `/postgres/sync-matches` | **Admin** | Handmatige Sportlink-synchronisatie — **Postgres-tier (productie)**. Alleen de vastgelegde Sportlink-dataservice mag als bron zijn ingesteld; een andere basis-URL stopt de sync vóór uitgaand verkeer (`207`, fase `configuratie`). Antwoordt `200`, `207` (deelstappen mislukt, `lastsynctimestamp` niet bijgewerkt), `409` (andere sync actief) of `500`. Bestaat niet op de SQL Server-tier |
| `GET` | `/sync-matches` | **Admin** | Handmatige Sportlink-synchronisatie — **SQL Server-tier**, dezelfde basis-URL-regel en parameters (`reset`, `season`). Antwoordt `200`, `207` (deelstappen mislukt, `LastSyncTimestamp` niet bijgewerkt, #1779), `409` (andere sync actief) of `500`. Bestaat niet op de Postgres-tier |
| `GET/PUT` | `/beheer/settings` | **Admin** | Club-instellingen ophalen/opslaan (incl. Sportlink Web Extension-schakelaar en de seizoensstartmaand `SeasonStartMonth` 1–12 per club; wijzigen bouwt de seizoenen van die club opnieuw op). `FetchSchedule` in een PUT moet een niet-lege, uitvoerbare zesvelden-CRON van maximaal 50 tekens zijn; anders 400 zonder de invoer in de foutmelding. Een weggelaten veld blijft ongewijzigd |
| `GET` | `/beheer/geocode` | **Admin** | Adres → GPS-coördinaten opzoeken voor de accommodatie-instelling |
| `GET` | `/beheer/sync/status` | **Admin** | Status van de laatste Sportlink-synchronisatie, plus optioneel `?jobId=` voor een specifieke sync-job (#1138) |
| `POST` | `/beheer/sync/trigger` | **Admin** | Synchronisatie starten via een Storage Queue-job (#1138) — geeft direct een `jobId` terug, geen fire-and-forget meer. Optionele body `{"reset":true,"season":2025}` haalt het hele seizoen opnieuw op vanaf de seizoensstart (#1352); ongeldig seizoen of een seizoen zonder seizoensrij → 400 (#1461) |
| `GET` | `/beheer/teams` | **Admin** | Teamlijst ophalen |
| `GET` | `/beheer/teams/keuzelijst` | **Admin** | Actieve teams met `teamId` + canonieke naam, voor het koppelen van een teamtekst aan een team (#1568) |
| `GET` | `/beheer/templates` | **Admin** | Alle e-mailtemplates per berichttype ophalen |
| `PUT` | `/beheer/templates/{key}` | **Admin** | Eén e-mailtemplate opslaan |
| `POST` | `/beheer/templates/{key}/reset` | **Admin** | Eén e-mailtemplate terugzetten naar standaard |
| `GET/POST` | `/beheer/uitgesloten-emails` | **Admin** | E-mailadressen uitsluiten van automatische antwoorden: lijst ophalen / toevoegen. POST JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `DELETE` | `/beheer/uitgesloten-emails/{id}` | **Admin** | Uitsluiting verwijderen |
| `GET/POST` | `/beheer/voorkeurstijden` | **Admin** | Gewenste speeltijden per team: lijst ophalen / toevoegen. POST JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `PUT/DELETE` | `/beheer/voorkeurstijden/{id}` | **Admin** | Gewenste speeltijd wijzigen / verwijderen. PUT JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `GET/POST` | `/beheer/teamregels` | **Admin** | Planningsregels per team (bijv. buffertijd): lijst ophalen / toevoegen. POST JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `PUT/DELETE` | `/beheer/teamregels/{id}` | **Admin** | Planningsregel wijzigen / verwijderen. PUT JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `GET/POST` | `/beheer/veldtraining` | **Admin** | Terugkerende trainingsblokken per veld ophalen / toevoegen. POST JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `PUT/DELETE` | `/beheer/veldtraining/{id}` | **Admin** | Trainingsblok wijzigen / verwijderen. PUT JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `GET/POST` | `/beheer/veldperiodes` | **Admin** | Veldperiodes ophalen / toevoegen. POST JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `PUT/DELETE` | `/beheer/veldperiodes/{id}` | **Admin** | Veldperiode wijzigen / verwijderen. PUT JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `GET` | `/beheer/email-log` | **Admin** | Verwerkte e-mails inzien (AVG-conform: geen berichtteksten of onderwerp; `Afzender` en `VerstuurdNaar` gemaskeerd tot het domein); per regel `HeeftTrace` (#1568), plus `statusCounts` voor alle overeenkomende regels buiten de begrenzing van `items`; date-only filters behouden UTC-kalenderdagsemantiek (ook `tot` op middernacht), timestamps zijn exacte UTC-grenzen; de variant `yyyy-MM-dd HH:mm` wordt als UTC gelezen (#1819) |
| `GET` | `/beheer/email-log/{id}/trace` | **Admin** | Permanente, PII-arme beslissingstrace van één verwerking (`id` = `Id` uit het e-maillog): `verwerkingId`, `verzoekType`, `status` (`null` als de verwerking is opgeruimd), `ontvangstDatum`, `aangemaakt`, `zekerheid`, `sjabloonSleutel`, `appVersie`, `trace` (`stappen`, `oordeel`). Geen body, afzender, onderwerp of ruwe teamtekst uit de mail (alleen allowlist-velden, `BeslissingsTrace.VoorOpslag()`). **404** als er geen trace is (#1568) |
| `POST` | `/test/email` | **Admin** | AI-classificatie dry-run zonder e-mail te versturen (Email-tester-pagina); de respons bevat sinds #1568 ook `trace` (beslissingstrace) en `leersuggestie`, en sinds #1583 een definitief `eindoordeel` (automatisch verstuurd / review / geen antwoord, met de actuele zekerheidspoort-instelling meegewogen); de tester geeft de gevalideerde leermomenten mee aan de classificatie (zoals productie) en schrijft niets; JSON-body maximaal 1 MiB UTF-8 (**413**), ongeldige UTF-8/JSON **400**; **503** `{ error, aiBeschikbaar: false }` als AI-classificatie niet is geconfigureerd of toegestaan |
| `POST` | `/feedback/validate` | **Admin/User** | Feedback-widget: voorvalidatie op volledigheid. Per gebruiker begrensd (30 AI-aanroepen per 10 min, samen met `preview`). **503** `{ error, aiBeschikbaar: false }` als de AI-dienst niet geregistreerd is (lokaal door de EgressGuard); geldt ook voor `preview` en `submit`, beide tiers gelijk (#1487). Invoerlimieten voor `validate`, `preview` en `submit` (#1677): body ≤ 65536 UTF-16-tekens en 262144 UTF-8-bytes (anders **413** `{ error }`); ongeldige UTF-8/JSON **400**, max 5 `vragenAntwoorden` (anders **400**), vraag ≤ 500 en antwoord ≤ 2000 tekens (afgekapt) |
| `POST` | `/feedback/preview` | **Admin/User** | Feedback-widget: exacte titel + body van het te publiceren issue opvragen, zónder iets aan te maken (#1205) |
| `POST` | `/feedback/submit` | **Admin/User** | Feedback-widget: melding bewaren in `avg.Feedback` (melder = Entra object-ID + naam-momentopname, nooit in het publieke issue). **Admin**: direct als openbaar GitHub-issue; met `bevestiging` wordt exact de in het voorbeeld getoonde tekst gepubliceerd. Bij een time-out, verbroken verbinding of HTTP 408/5xx tijdens publiceren is de uitkomst onbekend (`502`); de melding blijft op `publiceren` staan en is te herstellen met `POST /beheer/feedback/{id}/publiceer` (automatisch herstel, #1803). **User**: wacht op publicatie door een beheerder, de response bevat geen issueverwijzing. Optioneel `telemetrie` (technische context, geredigeerd, na 90 dagen gewist). Limiet: 3 per 10 min per gebruiker, 30 per uur per club → `429` |
| `GET` | `/beheer/feedback` | **Admin** | Feedbackoverzicht (filters `type`, `status`, `vanaf`, `tot`, `q`, `limit`, `offset`); elke aanroep komt in het inzagelog (#764) |
| `GET` | `/beheer/feedback/{id}` | **Admin** | Eén melding met technische context; komt in het inzagelog |
| `POST` | `/beheer/feedback/{id}/publiceer` | **Admin** | Een wachtende melding publiceren als GitHub-issue (exact de bewaarde tekst, PII-gate opnieuw); `409` bij een tweede klik of zolang een eerdere poging nog kan lopen (claim jonger dan 4 minuten, langer dan één volledige herstelpoging). Staat de melding op `publiceren` (onbekende uitkomst of mislukte receipt-opslag), dan zoekt de server eerst via de GitHub-issuelijst (niet de search-API) naar een issue met de publicatiereferentie van de melding en koppelt dat; alleen als het ontbreekt wordt opnieuw geplaatst. Is GitHub dan onbereikbaar: `502` en de melding blijft herstelbaar. Wijst GitHub de aanmaak expliciet af (4xx behalve 408): `502` en status `github-mislukt`, opnieuw te publiceren. Een oudere melding zonder referentie krijgt er vóór de POST een; lukt dat bewaren niet, dan `502` zonder POST en is ze opnieuw te publiceren (#1803) |
| `GET` | `/beheer/feedback/inzagelog` | **Admin** | Wie bekeek welke melding (nooit de inhoud; 24 maanden bewaard) |
| `POST` | `/planner/check-availability` | **Admin** | Veldbeschikbaarheid controleren — gescoped op `X-Club-Code` header |
| `POST` | `/planner/doordeweeks-beschikbaar` | **Admin** | Doordeweekse beschikbaarheid door het seizoen heen — gescoped op `X-Club-Code` header |
| `POST` | `/planner/bevestig` | **Admin** | Wedstrijdslot boeken |
| `POST` | `/planner/populate-sunset` | **Admin** | Zonsondergangtabel vullen |
| `POST` | `/planner/zoek-wedstrijd` | **Admin** | Bestaande wedstrijd zoeken — gescoped op `X-Club-Code` header |
| `POST` | `/planner/herplan-check` | **Admin** | Herplan-alternatieven simuleren — gescoped op `X-Club-Code` header |
| `POST` | `/planner/herplan-bevestig` | **Admin** | Herplanverzoek registreren |
| `POST` | `/planner/auto-plan` | **Admin** | **Dagplanning optimaliseren** — regels → voorkeurstijden → leeftijdsdefaults |
| `POST` | `/planner/auto-plan/deel` | **Admin** | Planning delen zoals getoond op het scherm (HTML/PDF, stateless, #1460); body maximaal 524288 UTF-16-tekens en 2097152 UTF-8-bytes (**413**), ongeldige UTF-8 **400** |
| `POST` | `/planner/auto-plan/toepassen` | **Admin** | Berekende planning wegschrijven (alleen testmodus ALLSTARS) |
| `GET` | `/planner/pdf-export` | **Admin/User** | Of PDF-export voor de gekozen club aan staat: `{ "pdfExportIngeschakeld": bool }` — Planning toont de PDF-knop alleen dan, ook voor de rol `user` (#1459) |
| `GET` | `/planner/menu-toegang` | **Admin/User** | Menu-sleutels die de ingelogde gebruiker mag zien: `{ "featureKeys": ["menu.planning", …], "sportlinkExtensieActief": bool }` — `admin` krijgt alle, andere rollen de sleutels die hun rol in "Rechten per rol" aan heeft staan (zonder DEBUG); `sportlinkExtensieActief` is de niet-gevoelige menuvlag voor de Sportlink Web Extension (zodat niet-admins hem krijgen zonder `/beheer/settings`). Alleen UI-gemak voor het hoofdmenu; elk endpoint autoriseert zelf (#1647) |
| `GET` | `/planner/veldbezetting?datum=` | **Admin/User** | Wedstrijden op een datum, zonder optimalisatie-berekening — voedt de Planning-pagina, sinds #1400 generiek zichtbaar voor elke ingelogde gebruiker |
| `GET` | `/planner/team-schedule` | **Admin** | Wedstrijdschema per team — gescoped op `X-Club-Code` header |
| `GET` | `/beheer/teambegeleiding` | **Admin + user** | Alle teams met begeleiding in database (#1330: elke ingelogde rol) |
| `GET` | `/beheer/teambegeleiding/{team}` | **Admin + user** | Begeleiders van team: naam, rol, functie, e-mailadres en telefoonnummer (#1330: elke ingelogde rol). Elke opvraging komt in het inzagelog `avg.TeambegeleidingInzageLog` (wie, wanneer, welk team, aantal rijen; geen gegevens van de begeleiders); lukt het vastleggen niet, dan `500` zonder gegevens (#1701) |
| `POST` | `/beheer/teambegeleiding/doorsturen` | **Admin + user** | Vraag doorsturen (BCC coördinator). `ontvangers` bepaalt de ontvangers (max 15, gevalideerd, uitsluitingslijst gecontroleerd); voor een andere rol dan `admin` moet elk adres bij het team horen (begeleiders, coördinator of de aanvrager zelf), anders 403; leeg → server-side coach-lookup (#765) (#1330: elke ingelogde rol). Max 3 verzendingen per gebruiker per 10 minuten, anders 429; alleen een daadwerkelijke verzending telt (een mislukte verzending geeft haar eigen slot terug), `admin` valt erbuiten; de teller is in-memory en geldt per instantie (bij uitschalen of na een herstart kan het totaal hoger uitvallen; geen globale kwotering) (#1671) |
| `POST` | `/beheer/teambegeleiding/import` | **Admin** | CSV-import van begeleiders — vervangt de rijen van de club atomisch (DELETE + inserts + audit-rij in één transactie, rollback bij elke fout; #1131/#1132). Kolomlengtes worden vóór elke destructieve stap gevalideerd; een te lange waarde geeft `400` met `{ error, fouten: [...] }` (rij/kolom-omschrijving per overtreding) en laat de vorige import ongemoeid. Postgres-tier serialiseert vervangingen per club (`pg_advisory_xact_lock`) zodat twee gelijktijdige imports elkaar nooit tot een vereniging van beide batches kunnen combineren. CSV wordt in-memory verwerkt en nooit opgeslagen; `avg.ImportLog` bevat alleen metadata — geen PII |
| `GET/POST` | `/beheer/speeltijden` | **Admin** | Speeltijden per leeftijdscategorie: lijst ophalen / toevoegen. POST JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft `400`, te groot geeft `413` vóór verwerking |
| `PUT/DELETE` | `/beheer/speeltijden/{leeftijd}` | **Admin** | Speeltijd van één leeftijdscategorie wijzigen / verwijderen. PUT JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft `400`, te groot geeft `413` vóór verwerking |
| `GET` | `/beheer/leermomenten` | **Admin** | Classificatie-leermomenten ophalen (`?status=pending\|validated\|rejected`) |
| `GET` | `/beheer/leermomenten/stats` | **Admin** | Aantallen leermomenten per status |
| `POST` | `/beheer/leermomenten` | **Admin** | Leermoment toevoegen als beheerder (#1568): `{ "origineelVerzoekType"?, "juistVerzoekType", "samenvatting", "herkomstVerwerkingId"? }`. Direct gevalideerd, herkomst `Admin`, permanent. Samenvatting (max 500) wordt gesaneerd; de aanmaker komt uit het principal |
| `DELETE` | `/beheer/leermomenten/{id}` | **Admin** | Leermoment van een beheerder (herkomst `Admin`) definitief verwijderen (AVG, #1568). Alleen eigen club: andere club of onbekend → `404`; een leermoment uit een beantwoorde mail (`Reply`) → `409` |
| `PUT` | `/beheer/leermomenten/{id}/valideer` | **Admin** | Leermoment valideren of afwijzen (`{ "actie": "valideer"\|"afwijzen" }`) |
| `GET` | `/beheer/teamaliassen` | **Admin** | Teamnaam-aliassen ophalen (`?status=pending\|validated\|rejected&limit=100`) — inclusief canonieke teamnaam |
| `POST` | `/beheer/teams/herstel` | **Admin** | Canonieke teamlijst opnieuw opbouwen uit `his.teams` (Postgres-tier; `his.Teams` op de SQL Server-tier): volledige canonicalisatie + sleutelmigratie (#766). Idempotent. `409` als er nog niets gesynchroniseerd is — "niets te doen" is bewust geen `200` (#946) |
| `POST` | `/beheer/teamaliassen` | **Admin** | Alias aanmaken als beheerder (#1568): `{ "ruweTekst", "teamId", "herkoppel"?, "bevestigDubbelzinnig"?, "herkomstVerwerkingId"?, "reden"? }`. Bron `CoordinatorCorrectie`, direct `validated`; `409` bij een bestaande alias voor een ander team tenzij `herkoppel: true`, en `409` (`code: "dubbelzinnig"`) als de tekst bij meerdere teams past tenzij `bevestigDubbelzinnig: true` |
| `GET` | `/beheer/onbekende-teamteksten` | **Admin** | Wachtrij met teamteksten die de pipeline niet kon koppelen (`?status=open\|afgehandeld\|genegeerd&limit=100`) (#1568) |
| `PUT` | `/beheer/onbekende-teamteksten/{id}/status` | **Admin** | Wachtrijregel op `open`, `afgehandeld` of `genegeerd` zetten (`{ "status": ... }`) |
| `PUT` | `/beheer/teamaliassen/{id}/valideer` | **Admin** | Alias goedkeuren of afwijzen (`{ "status": "validated"\|"rejected" }`); legt wie en wanneer vast |
| `DELETE` | `/beheer/teamaliassen/{id}` | **Admin** | `Sync`-aliassen blijvend afwijzen (`rejected`); andere herkomsten fysiek verwijderen, steeds binnen de geselecteerde club |
| `GET` | `/beheer/theme` | **Admin** | Club-thema ophalen (kleuren + website-URL + `lightColors`/`darkColors`) — gefilterd op `X-Club-Code` header. De paletten zijn `null` zolang er geen licht/donker-set is ingesteld; de client valt dan terug op de vier platte kleuren |
| `PUT` | `/beheer/theme` | **Admin** | Club-thema opslaan (`{ primary, secondary, accent, textOnPrimary, clubWebsiteUrl, faviconUrl, logoUrl, lightColors, darkColors }`) — gefilterd op `X-Club-Code` header. `lightColors`/`darkColors` zijn sleutel→hex-objecten voor het volledige palet per modus (#1254); elke sleutel moet `^[a-z][a-zA-Z0-9-]{0,39}$` zijn en elke waarde `#rrggbb` of `#rrggbbaa`, maximaal 40 per palet. `faviconUrl`/`logoUrl` zijn optioneel; een ingevulde waarde wordt getrimd, `http://` wordt als `https://` opgeslagen, en daarna moet het een absolute `https://`-URL van maximaal 300 tekens zijn (anders HTTP 400) |
| `POST` | `/beheer/theme/extract?url=` | **Admin** | Kleuren extraheren uit club-website (SSRF-beschermd) |
| `GET` | `/beheer/clubs` | **Admin** | Lijst van beschikbare clubs (`[{ clubCode, clubName }]`) voor de GUI-selector |
| `GET` | `/beheer/sportlink-extensie/rollen` | **Admin** | Sportlink Web Extension (#986/#988): per functionele rol tonen of een eigen Sportlink-serviceaccount gekoppeld is, door wie en wanneer |
| `PUT` | `/beheer/sportlink-extensie/rollen/{rolNaam}` | **Admin** | Koppeling registreren/overschrijven voor een rol (`{ SportlinkAccountNaam }`) — `LaatstGekoppeldDoor` altijd server-bepaald |
| `GET/PUT/DELETE` | `/beheer/sportlink-extensie/rollen/{rolNaam}/autologin` | **Admin**, primaire club | Status zonder secrets; credentials versleuteld instellen/verwijderen. PUT doet geen login. Maximaal 8192 bytes JSON. Zie [automatische login](SPORTLINK-AUTOLOGIN.md). |
| `GET` | `/beheer/sportlink-extensie/health?live=false` | **Admin** | Statussectie: extension/dry-run-instelling, koppeling + laatste tokenverversing per rol, laatste mutatiefout, laatste contract-check. Zonder `live=true` geen Sportlink-aanroep; `live=true` doet één tokenverversing + één leesaanroep (#998) |
| `GET` | `/beheer/rolfeatureinstellingen` | **Admin** | Volledige toegangsmatrix ophalen: elke combinatie van rol × functie als `{ RolNaam, FeatureKey, Enabled }` — fail-closed (#1390, opvolger van #1341/epic #1338) |
| `PUT` | `/beheer/rolfeatureinstellingen` | **Admin** | Eén cel aan/uit zetten (`{ RolNaam, FeatureKey, Enabled }`) — `admin` is hier nooit instelbaar, die rol heeft altijd alles aan; toegestane rollen zijn `user`/`Wedstrijdzaken`/`Sectiehoofd`/`Ledenadministratie` (#1390). JSON-body maximaal 1 MiB UTF-8-bytes; ongeldige UTF-8/JSON geeft 400, te groot geeft 413 vóór verwerking |
| `GET` | `/sportlink/match/{wedstrijdcode}` | **Admin/User** (sinds #1400 — voorheen Wedstrijdzaken) | Read-only wedstrijdgegevens uit Sportlink Club: PublicMatchId-cache/reverse-lookup + permissievlaggen (#987/#991); sinds #1339 ook het huidige veld (`fieldId`/`fieldSize`, prefill) en server-berekende `veldOpties`/`subpositieOpties` (voorstellen op onze eigen veldnaam, géén Sportlink-gegeven); sinds #1341 ook de per-rol feature-toestemmingen (`KleedkamersFeatureToegestaan`/`ScheidsrechterFeatureToegestaan`/`VeldFeatureToegestaan`), sinds #1400 ook `MagWijzigen`; sinds #1340 (VOORSTEL, DPO-vraag nog niet bevestigd — zie `docs/SPORTLINK-WEB-EXTENSION.md` §8) ook de relatiecode van de huidige scheidsrechter/AR1/AR2 (`ScheidsrechterRelatieCode`/`Ar1RelatieCode`/`Ar2RelatieCode`, uitsluitend het relatiecode-veld, nooit een naam) — genuld als `ScheidsrechterFeatureToegestaan` false is; het exacte Sportlink-JSON-veldnaam voor de relatiecode is NOOIT live geverifieerd. **#1400:** viewing is generiek (admin+user); wélke permissievlaggen/relatiecodes terugkomen hangt af van de éigen rol van de aanroeper (`admin`/`Wedstrijdzaken` → echte waarden, gewone `user` → alles `false`/`null`) |
| `GET` | `/sportlink/match/{wedstrijdcode}/public-match-id` | **Admin/User** (sinds #1400 — voorheen Wedstrijdzaken) | Lichtgewicht variant — alleen `PublicMatchId` (cache/reverse-lookup, geen volledige Match-aanroep), voor de deep-link-knop op Planning en Veld optimalisatie (#989, sinds #1361 op beide pagina's) |
| `PUT` | `/sportlink/match/{wedstrijdcode}/dressingrooms` | **Wedstrijdzaken** | Kleedkamers toewijzen — eerste echte Sportlink-mutatie, guardrail + audit-log (#992); sinds #1341 ook geweigerd (409) als de per-rol feature-instelling uitstaat |
| `PUT` | `/sportlink/match/{wedstrijdcode}/field` | **Wedstrijdzaken** | Veld(deel) wijzigen — `IsForceUpdate` server-side altijd `false` (semantiek onbevestigd, #993); sinds #1341 ook geweigerd (409) als de per-rol feature-instelling uitstaat |
| `PUT` | `/sportlink/match/{wedstrijdcode}/officials` | **Wedstrijdzaken** | Toewijzingsactie sinds #1319 door eigenaar vrijgegeven; huidige route volgt `sportlinkDryRun`. Payloadvelden/positiewaarden blijven onbevestigde aannames; alleen relatiecode/persoons-ID, geen namen (AVG, #994); sinds #1341 ook geweigerd (409) als de per-rol feature-instelling uitstaat |
| `PUT` | `/sportlink/match/{wedstrijdcode}/change-request` | **Wedstrijdzaken** | Wijzigingsverzoek datum/tijd/accommodatie — **ALLEEN stap 1 (valideren)** van Sportlinks tweestaps flow, `Toelichting` verplicht; sinds #1319 live bevestigd (was code-gelockt); enige mutatie die een echte tegenstander raakt — stap 2 (bevestigen) is bewust niet gebouwd (#995). Sinds #1320 retourneert de respons ook een diagnostiektrace (HTTP-status, endpoint, audit-ID, de vier toplevel-booleans) i.p.v. kaal Mutatie+Validatie — zie `docs/SPORTLINK-WEB-EXTENSION.md` §6.4 |
| `PUT` | `/sportlink/audit/{id}/notitie` | **Wedstrijdzaken** | Testnotitie koppelen aan een bestaande Sportlink-mutatieauditpoging (#1320) — voor de eigenaar-gestuurde productieproef; scoped op `ClubCode`, 404 bij verkeerde club of onbekend ID; lege string toegestaan om te wissen (max. 1000 tekens) |
| `GET` | `/sportlink/change-requests` | **Wedstrijdzaken** | Inkomende en uitgaande wijzigingsverzoeken ophalen (#996; `RequestStatus` = Sportlinks `ChangeRequestStatus`, plus `IsIncomingRequest` en `StatusGroep` OPEN/ACCEPTED/DENIED/REVOKED/UNKNOWN, #1439), sinds #1111 verrijkt met eigen wedstrijdcontext (`Wedstrijd`: nummer, teams, datum, tijd, accommodatie uit `his.matches` via de PublicMatchId-cache; `null` als niet gecachet), sinds #1464 met Sportlinks eigen `ExternalMatchId`/`Thuisteam`/`Uitteam` als terugval, en met openstaande (`StatusGroep` OPEN, inkomend eerst) verzoeken vooraan |
| `PUT` | `/sportlink/change-requests/{publicRequestId}/action` | **Wedstrijdzaken** | Wijzigingsverzoek goedkeuren (`Actie=APPROVE`) of afwijzen (`Actie=DENY`, `Remarks` verplicht) (#996) |
| `POST` | `/sportlink/club-match` | **Wedstrijdzaken** | Oefenwedstrijd ("clubwedstrijd") aanmaken — volgt `sportlinkDryRun`; **contract sinds #1427 live bevestigd** (body gelijk aan Sportlinks eigen formulier). Body: `MatchDateTime`, `Duration`, `TeamNaam`, `VrijeTekst` (bij `true` geen 400 voor een onbekend team; Sportlinks standaardteam levert dan het team-ID), `Tegenstander`, `VeldNummer`, `Description`, sinds #1437 `Velddeel` (`1.0`/`0.5`/`0.25`/`0.125`, standaard heel veld; de notatie van half/kwart/achtste is een aanname) en `AgeClassCode` (Sportlink-`Id` uit het formulier-endpoint; wint van de categorie van het team). De server haalt `ClubMatchDefaults`, `PickListsTeams`, `PickListsLocation` en `PickListsMatchInformation` op en leidt daaruit het `T…`-team-ID, leeftijdscategorie, spelactiviteit, `SubFacilityId` af; het wedstrijdnummer komt sinds #1437 uit een eigen teller (`YYMMDD` + volgnummer per speeldag, max. 99 per dag → anders 400); de clubinstelling `SportlinkSpelactiviteit` gaat voor de spelactiviteit van het team; team/veld niet (eenduidig) bij Sportlink → 400, overige terugvallen als `Waarschuwingen`. De respons bevat sinds #1892 ook `Thuisteam`, `Uitteam` (de verstuurde namen) en `UitteamGekoppeld` (`false` = vrije tegenstander, geen teamkoppeling); bij een echte aanmaak leest de server de wedstrijd terug (één extra read-only `Match`-aanroep) en voegt een waarschuwing toe als Sportlink een ander uitteam toont dan opgegeven (best-effort). Geen `SportlinkMutationGuard` (er is vooraf geen wedstrijd). Uitslag bewust niet gebouwd (#997); verwijderen: zie de regel hieronder (#1440) |
| `DELETE` | `/sportlink/club-match/{publicMatchId}` | **Wedstrijdzaken** | Clubwedstrijd (oefenwedstrijd) verwijderen (#1440) — live sinds #1458 (volgt `sportlinkDryRun`; bij dry-run `IsDryRun: true`). Haalt eerst de wedstrijd op voor `SportlinkMutationGuard` (`Verwijderen`: alleen bij expliciet `IsKernelMatch = false` én `IsHomeMatch`, anders 409); **503 als de mutatie-auditservice ontbreekt (fail-closed, geldt voor alle Sportlink-mutaties)**; elke poging staat in de mutatie-audit met het `PublicMatchId`. Nooit automatisch herhaald. Respons `{ IsSuccess, Violations, IsDryRun, IsForcedDryRun, PublicMatchId }`; 400 bij een ongeldig `PublicMatchId`, 404 als Sportlink de wedstrijd niet kent. De GUI biedt dit alleen aan op het resultaat van "Wedstrijd aanmaken" |
| `GET` | `/sportlink/club-match/dryrun-status` | **Wedstrijdzaken** | Actuele dry-run-stand (`{ DryRun }`) voor de banner op "Wedstrijd aanmaken" — leest alleen de club-instelling `sportlinkDryRun`, geen Sportlink-aanroep (#1427) |
| `GET` | `/sportlink/koppeling-status` | **Wedstrijdzaken** | Koppelingsstatus van rol Wedstrijdzaken voor de vooraf-melding op de werkpagina's (#1773): `{ RolNaam, Niveau (Ok/Waarschuwing/Verbroken), IsVerbroken, Melding, LaatsteLoginUtc }`, afgeleid uit de opgeslagen automatische-loginstatus (credentials/refresh-token aanwezig, blokkade, retrydatum, sessieduur). Eigen database, geen Sportlink-aanroep, nooit een tokenwaarde; 503 als de beveiligde opslag ontbreekt |
| `GET` | `/sportlink/club-match/formulier` | **Wedstrijdzaken** | Voorinvulling voor "Wedstrijd aanmaken" (#1437): per actief team `{ TeamNaam, Leeftijdscategorie, AgeClassCode, Duur, Veldafmeting }` (Sportlink-categorie, wedstrijdduur en velddeel uit de speeltijden van de club) plus `AgeClasses` `[{ Id, Description }]` uit Sportlink en `SportlinkBeschikbaar`. Eigen endpoint omdat de speeltijden-API admin-only is. Is Sportlink onbereikbaar, dan komen de teamgegevens zonder lijst terug (`SportlinkBeschikbaar: false`) |
| `GET` | `/sportlink/club-match/picklists` | **Wedstrijdzaken** | De twee Sportlink-picklists (Teams + Location) — read-only, persoonsgegevensvrij. Sinds #1427 los van elkaar: een onherkenbare teamlijst blokkeert de locatielijst niet meer. Diagnostisch endpoint (id + naam per item); het aanmaakpad gebruikt sinds #1427 de volledige lijsten via de server (#997) |
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

### Request-bodylimieten

De JSON-bodies van instellingen, sync-trigger, teambegeleiding doorsturen, templates, testdata, thema, velden/veldbeschikbaarheid, Sportlink-extensierollen en de planner-endpoints hebben een limiet van 1 MiB UTF-8. Ongeldige UTF-8 of JSON geeft **400**; een body boven de limiet geeft **413** vóór de actie of databasebewerking. De CSV-import van teambegeleiding behoudt de bestaande uploadlimiet van 5 MiB UTF-8: zowel de gedecodeerde `csvContent` als de JSON-envelope worden begrensd, en overschrijding geeft **413** vóór CSV-parsing. De envelope laat zesvoudige worst-case escaping, de bestaande opslagruimte van 500 tekens voor de bestandsnaam en 64 bytes voor de DTO-wrapper toe. Feedback behoudt de bestaande limiet van 65536 UTF-16-tekens en krijgt daarnaast een byteplafond van 262144. Planning delen behoudt 524288 UTF-16-tekens en krijgt daarnaast een byteplafond van 2097152. Deze limieten gelden per endpoint.

Beide Functions-workers registreren vóór de hoststart een eindig Kestrel-**transportplafond van 220.200.960 bytes**, gelijk aan het [gedocumenteerde maximum van Azure Functions](https://learn.microsoft.com/en-us/azure/azure-functions/functions-app-settings#functions_request_body_size_limit). Dit verandert geen Azure-instelling. De afzonderlijke applicatielimiet voor de CSV-JSON-envelope blijft **31.460.344 bytes** (`6 × 5 MiB + 6 × 500 + 64`); alle kleinere routelimieten, serializers en autorisatievolgorde blijven gelijk. De reader leest voor parsing maximaal de routelimiet plus één detectiebyte; een te hoge opgegeven Content-Length wordt zonder parsing geweigerd.

Het transportplafond is bewust ruimer dan de applicatielimiet. [Kestrel ruimt na een vroeg antwoord de resterende HTTP/1-body op](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/request-draining), met een vaste timeout van vijf seconden en behoud van de eindige serverlimiet. Daarbij wordt niets naar een tijdelijk bestand geschreven of door de applicatie verwerkt. Een serverplafond exact op de routegrens kan deze opruiming afbreken terwijl de Functions-proxy nog schrijft; de proxy kan dan het gecontroleerde 413-antwoord vervangen door een transportfout. HTTP/1-chunkframing telt bovendien mee bij het Kestrel-plafond. De grotere transportmarge laat normale te grote verzoeken gecontroleerd met 413 eindigen; ze vergroot de toegestane DTO of CSV niet. Verzoeken die het transportplafond of de drain-timeout overschrijden kunnen op transportniveau worden afgebroken; een JSON-antwoord is bij zulke afgebroken verbindingen niet gegarandeerd.

De GUI gebruikt `TeamsApiClient` → `AdminApiTransport.PostAsJsonAsync` zonder request-streamingoptie. De [.NET 10 BrowserHttpHandler](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.Net.Http/src/System/Net/Http/BrowserHttpHandler/BrowserHttpHandler.cs) buffert dan de body en geeft bytes door aan Fetch; de browser kent de inhoudslengte. Dezelfde applicatiegrens geldt voor chunked requests zonder Content-Length. Lokale synthetische HTTP-tests via de echte Functions-host controleren de volledige escaped GUI-envelope, exact de applicatiegrens en overschrijdingen met en zonder Content-Length; dit is geen productie- of browser-uploadtest.

---

## Sportlink-foutantwoorden (#1815)

De bestaande Sportlink-routes geven bij een bezette lokale autologinlease (`LokaleLeaseBezet`)
**409** met `{ "error": "Een andere lokale Sportlink-verversing is bezig; er is geen Sportlink-aanroep gedaan. Probeer het opnieuw." }`.
Dit geldt voor de twee `GET /sportlink/match/{wedstrijdcode}`-varianten, `GET /sportlink/change-requests`,
`GET /sportlink/club-match/picklists` en de zeven mutatieroutes hieronder. De geblokkeerde
Sportlink-aanroep is niet uitgevoerd; een latere poging is toegestaan. Dit is een lokale blokkade,
geen upstream-fout (502). De formulier-, health- en veldbezettingroutes behouden hun bestaande
status-/terugvalantwoord en vertalen deze status niet naar HTTP 409.

Als Sportlink een **live-mutatie heeft verwerkt** maar het afronden van de vooraf geregistreerde
audit mislukt, volgt **500** met `{ "error": "Sportlink heeft de wijziging verwerkt, maar de audit kon niet worden afgerond. Probeer de actie niet opnieuw; neem contact op met de beheerder." }`.
De audit blijft open en de veldplannercache is al geleegd. De aanroeper mag deze mutatie niet
opnieuw versturen. Deze specifieke 500 geldt niet voor dry-run of een inhoudelijke afwijzing;
andere interne fouten kunnen nog steeds een algemene 500 geven.

Deze auditafronding geldt voor precies deze bestaande mutatieroutes (alle paden onder `/api`):

- `PUT /sportlink/match/{wedstrijdcode}/dressingrooms`
- `PUT /sportlink/match/{wedstrijdcode}/field`
- `PUT /sportlink/match/{wedstrijdcode}/officials`
- `PUT /sportlink/match/{wedstrijdcode}/change-request` (alleen de bestaande validatiestap)
- `PUT /sportlink/change-requests/{publicRequestId}/action`
- `POST /sportlink/club-match`
- `DELETE /sportlink/club-match/{publicMatchId}`

---

## GET /api/health

Publieke, geaggregeerde status zonder database-, schema-, migratie-, TLS- of synchronisatiedetails. De probe is per tier maximaal vijf seconden gecachet en gelijktijdige requests delen één refresh. De auth-poort van eventuele gebruikers blijft onafhankelijk van deze process-lokale cache.

**HTTP-statuscode (#859):** `database: "unconfigured"` geeft **503**. Andere database-statussen (waaronder `paused`, `timeout` en `unavailable`) blijven **200** met `status: "degraded"`.

```json
{
  "status": "ok",
  "version": "3.16.0.4",
  "database": "online",
  "settingsReady": true,
  "migrationsCurrent": true,
  "schemaWarningPresent": false,
  "tlsWarningPresent": false
}
```

| Veld | Type | Beschrijving |
|---|---|---|
| `status` | `string` | Bestaande statusregel: database online, instellingen geladen en (Postgres) migraties bekend en actueel, plus de bestaande stale-sync-status. `degraded` door alleen een stale sync blijft een waarschuwing en is geen deploy-fout. |
| `version` | `string` | Vierdelige assemblyversie. |
| `database` | `string` | `online`, `paused`, `timeout`, `unavailable` of `unconfigured`; de laatste geeft HTTP 503. |
| `settingsReady` | `boolean` | `false` als de instellingen niet geladen zijn. |
| `migrationsCurrent` | `boolean \| null` | Postgres: `true` als er geen migraties openstaan, `false` als dat wel zo is; `null` als dit niet vastgesteld kon worden. SQL Server retourneert `null` omdat deze healthprobe daar geen migratiestatus heeft. |
| `schemaWarningPresent` | `boolean` | Alleen of de Postgres-schemawaarschuwing aanwezig is. |
| `tlsWarningPresent` | `boolean` | Alleen of de Postgres-TLS-waarschuwing aanwezig is. |
| `readPathsOk` | `boolean \| null` | Beide tiers (#1911): uitkomst van de leesproef van de planner-leespaden (speeltijden-lookup met dezelfde code als de Planning). `false` zet `status` op `degraded` en laat de deploy-smoke-test falen; `null` = niet gemeten (database offline of geen speeltijden-rij). |

De publieke respons bevat geen ruwe serverversie, tier/provider, sync-tijd, TLS-modus, waarschuwingsinhoud of migratienamen. De deployment gate blijft bij dezelfde uitkomsten falen en houdt schema/TLS-waarschuwingen niet-fataal.

## GET /api/beheer/health

Admin-only detailrespons voor diagnose. De route gebruikt de bestaande `AdminEndpoint.ExecuteZonderDatabaseAsync`-rolcontrole; de public health authorization blijft anoniem en heeft geen gedeelde identity-cache.

De detailrespons behoudt de bestaande `status`, `version`, `timestamp`, `database`, `settingsLoaded`, `tier`, `provider`, `serverVersion`, `lastSync`, `syncStale`, `tlsMode`, `tlsWarning`, `schemaWarning` en `pendingMigrations`-velden. Postgres-specifieke velden ontbreken of zijn `null` op SQL Server. Waarschuwingen, serverdetails en migratienamen zijn alleen beschikbaar achter de admin-poort.


---

## GET /api/postgres/sync-matches (Postgres-tier) — GET /api/sync-matches (SQL Server-tier)

Handmatig een Sportlink API synchronisatie starten (teams, wedstrijden, wedstrijddetails).

**De route verschilt per tier.** Dit is het enige endpoint waarvoor dat geldt:

| Tier | Route | Implementatie |
|---|---|---|
| Postgres (productie) | `GET /api/postgres/sync-matches` | `FunctionApp.Postgres/Sync/SyncFunction.cs` |
| SQL Server | `GET /api/sync-matches` | `FunctionApp/Function1.cs` |

**Authenticatie:** Easy Auth Bearer + `admin`-rol, via `AdminEndpoint.ExecuteAsync` — sinds #1350
dezelfde poort als elk ander beheerendpoint. Vóór #1350 liep dit endpoint op
`AuthorizationLevel.Admin` (de Azure Master key via `?code=`); die is vervallen omdat er geen
geautomatiseerde aanroeper was die van de master key afhing — de nachtelijke sync draait als
timer-trigger in hetzelfde proces — en een master key één statisch geheim zonder identiteit of
audittrail is dat bovendien de hele Function App beheert. Een `?code=`-queryparameter wordt nu
genegeerd; alleen een geldig Entra ID Bearer-token met de `admin`-rol geeft toegang.

### Queryparameters

| Parameter | Type | Verplicht | Beschrijving |
|-----------|------|-----------|-------------|
| `reset` | `boolean` | Nee | `true` = volledige seizoensynchronisatie in plaats van incrementeel |
| `season` | `integer` | Nee | Startjaar seizoen (bijv. `2024`). Gebruikt met `reset=true`. Ontbreekt of onparseerbaar → stille terugval op de standaardmodus (vorige week t/m einde seizoen) |

### Voorbeeld

```
GET /api/postgres/sync-matches
Authorization: Bearer <entra-token>

GET /api/postgres/sync-matches?reset=true&season=2025
Authorization: Bearer <entra-token>
```

### Antwoord

| Status | Tier | Body |
|---|---|---|
| `200 OK` | beide | `"Sync voltooid. WeekOffset-bereik: {from} tot {to}."` |
| `409 Conflict` | beide | `{ "status": "already running", "message": "A synchronization is already running. Try again later." }` — another run currently owns the shared staging area; this request did no sync work |
| `207 Multi-Status` | beide (SQL Server sinds #1779) | `{ "status": "gedeeltelijk mislukt", "weekOffsetFrom": -1, "weekOffsetTo": 12, "mislukteFases": ["matchdetails"], "melding": "..." }` — één of meer deelstappen zijn mislukt en `lastsynctimestamp` is bewust **niet** bijgewerkt (#1081). De geslaagde deelstappen blijven staan; het functielog noemt de betrokken fase(s) |
| `401 Unauthorized` | beide | Geen (geldig) Bearer-token — ook een `?code=`-master key geeft dit resultaat sinds #1350 |
| `403 Forbidden` | beide | Geldig token, maar zonder de `admin`-rol |
| `500` | beide | Onverwachte fout; zie het functielog |

Beide tiers antwoorden `200`, `207`, `401`, `403`, `409` of `500`, met dezelfde `207`-body (gedeeld via `SyncMatchesAntwoordCore`). `mislukteFases` bevat vaste labels (`teams`, `programma`, `uitslagen`, `matchdetails`, `configuratie`); een ontbrekende of ongeldige `sportlinkApiUrl` (geen vastgelegde Sportlink-dataservice) geeft op beide tiers `207` met fase `configuratie`, zonder uitgaand verkeer. Tijdelijke fouten (HTTP 5xx, 429, time-out) op alle dataservice-aanroepen van de sync worden per aanroep maximaal driemaal geprobeerd, binnen een gedeeld budget van zes extra pogingen per run (een herhaalde poging hoogstens 30 s, geen herhaling meer na vijf minuten looptijd), voordat een fase als mislukt telt (#1779).

---

## Planning geblokkeerd bij onbekende bronduur (#1814)

`POST /api/planner/check-availability`, `/planner/doordeweeks-beschikbaar`,
`/planner/herplan-check` en `/planner/bevestig` geven HTTP 409 met
`{"error":"Planning geblokkeerd: wedstrijdduur onbekend voor bestaande veldbezetting."}`
als een bestaande wedstrijd wel een veld en tijd maar geen betrouwbare Sportlink- of
speeltijdenduur heeft. De aanvraag wordt dan niet als vrij behandeld en een boeking wordt
niet opgeslagen. Bij `/planner/bevestig` kan 409 ook een gewoon bezettingsconflict betekenen.

## POST /api/planner/check-availability

Controleer of een veld beschikbaar is voor een oefenwedstrijd. Geeft een specifieke slottoewijzing, beschikbare tijdvensters, of een teamconflict terug.

> **Clubscope (#573, #580):** de optionele header `X-Club-Code` bepaalt welke club wordt
> doorzocht. Zonder header valt het endpoint terug op de primaire club van deze deployment.
> Wedstrijden, bezetting, velden, speeltijden en teamregels van andere clubs (inclusief de
> `ALLSTARS`-demodata) worden nooit meegenomen. Dit geldt ook voor
> `/planner/zoek-wedstrijd`, `/planner/herplan-check`, `/planner/doordeweeks-beschikbaar`
> en `/planner/team-schedule`.
>
> **Onbekende waarde (#1677, #1689):** de headerwaarde wordt getoetst aan de clubs in de
> instellingentabel (maximaal 20 tekens `A-Z`, `a-z`, `0-9`, `_`, `-`). Een ongeldige vorm of een
> club die niet bestaat geeft geen fout maar dezelfde terugval als zonder header: de primaire club.
> Een bestaande club wordt in de opgeslagen schrijfwijze gebruikt (een exacte match gaat voor; bij
> meerdere varianten in hoofdletters altijd dezelfde). De clublijst wordt hoogstens 60 seconden
> gecachet; een onbekende waarde laat haar opnieuw laden (hoogstens eens per 10 seconden), zodat een
> pas toegevoegde club direct herkend wordt. Is de lijst nog nooit te laden geweest (database
> onbereikbaar), dan geldt een syntactisch geldige waarde zoals vóór #1677. Geldt voor alle
> endpoints die `X-Club-Code` lezen, op beide databasetiers.

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
| `dagdeel` | `string` | Nee | Dagdeelfilter: `"ochtend"` (08:30 - 12:00), `"middag"` (12:00 - 17:00) of `"avond"` (17:00 - 22:00). Vensters en alternatieven blijven binnen dat dagdeel; een onbekende waarde wordt genegeerd |
| `leeftijdsCategorie` | `string` | Nee | Leeftijdscategorie (bijv. `JO11`, `MO17`, `VR`, `1-99`). Bepaalt standaardwedstrijdduur en veldgrootte. Zonder categorie is een positieve `wedstrijdDuurMinuten` vereist |
| `teamNaam` | `string` | Nee | Teamnaam voor conflictcontrole en teamspecifieke regels |
| `tegenstander` | `string` | Nee | Tegenstander (alleen voor administratie) |
| `wedstrijdDuurMinuten` | `integer` | Nee | Positieve duur in minuten; overschrijft de standaard uit Speeltijden. Zonder categorie of positieve duur stopt de controle met een reden en zoekt geen slot of venster |
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
| `reden` | `string\|null` | Reason when unavailable. A team name that does not meet the display checks is replaced by `het opgegeven team` in the team-conflict explanation (#1809). |
| `gecontroleerdDagdeel` | `string\|null` | Het dagdeel (`ochtend`, `middag`, `avond`) waarbinnen de vensters en alternatieven zijn berekend; `null` als er geen dagdeel is toegepast of de controle niet is uitgevoerd (#1587). Het e-mailantwoord vermeldt dit dagdeel |
| `alternatieven` | `array` | Tot 3 alternatieve tijdsloten |
| `beschikbareVensters` | `array\|null` | Beschikbare vensters per veld (alleen Modus 2) |
| `waarschuwingen` | `array` | Planning warnings as normalized single-line strings; empty results are omitted. See the output rules below (#1809). |

Warning output rules (#1809, both database tiers; API version 3.16.0.2): before the response is serialized,
control characters and whitespace are normalized to spaces. The delimiters `<`, `>`, backticks,
`{`, `}`, `[` and `]` are replaced by spaces; words containing `://` or `@`, or starting with
`www` (case-insensitive), are removed. Each remaining warning is truncated after 300 characters
when necessary and followed by `…` (at most 301 characters in the output). Warnings that become
empty are omitted; their remaining order is preserved. These rules apply to `waarschuwingen`,
not to every free-text response field. In warnings about an unrecognized team, a team name that
fails the display checks is shown as `het opgegeven team`.

### Foutantwoord (400)

```json
{
  "error": "Request body met 'datum' veld is verplicht."
}
```

---

## POST /api/planner/bevestig

Bevestig en boek een wedstrijdslot. Schrijft naar `planner.geplandewedstrijden` (Postgres-tier; `planner.GeplandeWedstrijden` op de SQL Server-tier). De aanvrager wordt server-side afgeleid uit de ingelogde principal; een eventueel meegestuurd `aangevraagdDoor`-veld wordt genegeerd.

### Aanvraag

```json
{
  "datum": "2026-04-25",
  "aanvangsTijd": "12:00",
  "veldNummer": 3,
  "leeftijdsCategorie": "JO13",
  "teamNaam": "[ClubCode] JO13-1",
  "tegenstander": "[Tegenstander] JO13-2",
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
| `aangevraagdDoor` | `string` | Nee | Verouderd compatibiliteitsveld; genegeerd. De server bepaalt de aanvrager uit de ingelogde principal. |
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

Register a reschedule request. **Does NOT modify the match** — only records the request with status "Aangevraagd". The actual change in Sportlink is a manual process. The server derives the requester from the authenticated principal; any supplied `aangevraagdDoor` value is ignored.

### Request Body

```json
{
  "wedstrijdcode": 12345678,
  "gewensteAanvangsTijd": "10:00",
  "gewenstVeldNummer": 2,
  "opmerking": "08:30 is niet haalbaar"
}
```

### Request Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `wedstrijdcode` | `integer` | **Yes** | Match code |
| `gewensteAanvangsTijd` | `string` | **Yes** | Desired new time `HH:mm` |
| `gewenstVeldNummer` | `integer` | No | Desired field number |
| `aangevraagdDoor` | `string` | No | Deprecated compatibility field; ignored. The server derives the requester from the authenticated principal. |
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

### Query-parameters (#1364)

| Parameter | Type | Verplicht | Beschrijving |
|-----------|------|-----------|-------------|
| `format` | `string` | Nee | `json` (standaard), `html` of `pdf` — dezelfde berekening, maar het resultaat als gedeeld document (zelfde kolommen als de Planning-export). Bestandsnaam `huidige-planning-<datum>.pdf` / `optimale-planning-<datum>.pdf`. Andere waarde: 400 |
| `tab` | `string` | Nee | `huidig` (standaard) of `optimaal` — welke kant in het document komt. Alleen relevant bij `format=html|pdf`. Andere waarde: 400 |

`format=pdf` vereist dat de clubinstelling **PDF-export** (`PdfExportIngeschakeld`, Instellingen) aan staat; standaard staat hij uit en geeft `format=pdf` dan **`409`** met een uitleg (#1459). `json` en `html` zijn niet afhankelijk van die instelling.

Bij `format=html|pdf` moet `datum` strikt `yyyy-MM-dd` zijn (anders 400). De berekening is een pure
leesbewerking; alleen `/planner/auto-plan/toepassen` schrijft, dus een tweede aanroep voor de export heeft
geen bijwerking.

### POST /api/planner/auto-plan/toepassen — getoond plan toepassen (#1802)

Alleen voor de democlub `ALLSTARS` en een beheerder. De huidige Admin GUI stuurt **alle** rijen
van de getoonde preview mee, ook niet-ingedeelde rijen. Een handmatig versleept kwart- of halfveld
wordt met zijn exacte subpositie opgeslagen; de server kiest geen vervangend slot.

```json
{
  "datum": "2026-08-22", "bufferMinuten": 15,
  "toewijzingen": [
    { "wedstrijdCode": 1, "huidigeVeld": "veld 101 A1", "huidigeTijd": "09:00",
      "veldNummer": 101, "veldSubpositie": "A2", "tijd": "10:00" },
    { "wedstrijdCode": 2, "huidigeVeld": null, "huidigeTijd": null,
      "veldNummer": null, "veldSubpositie": null, "tijd": null }
  ]
}
```

`toewijzingen` is optioneel voor bestaande API-clients. Bij **weglaten** berekent het legacy-pad
het plan opnieuw; dat pad garandeert niet dat eerdere handmatige wijzigingen worden toegepast.
Met de lijst controleert de server de volledige wedstrijdcodeset, verwachte huidige velden,
tijden en subposities, actieve velden, bronduur, veldfracties, openingstijden en buffer-/baanconflicten
tegen actuele ALLSTARS-data. Een doel vereist zowel `veldNummer` als `tijd` (exact `HH:mm`, zonder seconden, tijdsaanduidingen of witruimte); beide `null`
laat de rij oningedeeld. Een previewrij met een onbekend team en zonder betrouwbare speelduur wordt niet meegeschreven en telt niet mee als veldbezetting bij deze controle; andere geldige toewijzingen worden wel gecontroleerd. Als de doeltijd dezelfde `HH:mm`-minuut heeft als de verwachte brontijd,
blijven de bronseconden behouden, ook als veld of subpositie verandert. Een conflict geeft HTTP `409` zonder gedeeltelijke updates en de GUI
laat de getoonde aanpassingen staan. Voor een andere club geldt `403`.

Een geslaagde toepassing antwoordt met `bijgewerkt`, `mislukt` en `fouten`; het exacte pad meldt
`mislukt: 0`. De broncontrole en alle updates vinden binnen één serialiseerbare transactie plaats.

### POST /api/planner/auto-plan/deel — planning delen zoals getoond (#1460)

Stateless: de browser stuurt de lijst zoals die op Veld optimalisatie staat (inclusief handmatig
versleepte blokken); de server valideert en rendert hetzelfde HTML/PDF-document als
`auto-plan?format=`. Geen opslag, geen herberekening. Query `format=html|pdf` (verplicht).

```json
{ "datum": "2026-10-03", "tab": "optimaal",
  "wedstrijden": [ { "teamNaam": "...", "wedstrijd": "Thuis - Uit", "competitiesoort": "...", "tijd": "09:30", "veld": "..." } ] }
```

Limieten: max 500 wedstrijden; tekstvelden max 200 tekens zonder stuurtekens; `tijd` `HH:mm`;
`datum` `yyyy-MM-dd` tussen 2020-01-01 en 2100-12-31; body max 512 KB (413). De club komt van de
aanroeper — een afwijkende optionele `clubCode` geeft 403. Overige fouten: 400.
`format=pdf` volgt dezelfde clubinstelling **PDF-export** als hierboven: uit (standaard) geeft **`409`** (#1459).

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
      "voorkeurVeldToegepast": null,
      "teamBufferVoor": null,
      "teamBufferNa": 60,
      "sportlinkLeidend": true
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

`teamBufferVoor`/`teamBufferNa` (#1430) zijn de actieve teamregels `BufferVoor`/`BufferNa` van het team
in minuten, of `null` zonder regel. Het zijn de ruwe waarden: de effectieve buffer is het maximum met de
algemene buffer, en tussen twee opeenvolgende wedstrijden telt de grootste van de `BufferNa` van de
voorganger en de `BufferVoor` van de opvolger. De Admin GUI gebruikt ze om na handmatig verslepen exact
dezelfde regel toe te passen als de planner.

`sportlinkLeidend` (#1814) is `true` wanneer de planner de Sportlink-speelduur gebruikt. Rond dat
slot geldt geen appbuffer; overlap blijft altijd een conflict. De handmatige conflictcontrole
gebruikt deze waarde ook na het verslepen.

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
"wat staat er nu al gepland"-weergave (zie de pagina Planning in de Admin GUI, sinds #1361).

**Veld, tijd en blokduur uit Sportlink (#1563):** is de Sportlink Web Extension aan en bereikbaar, dan komen `veld`,
`aanvangsTijd`, `veldafmeting` en `duurMinuten` van een wedstrijd rechtstreeks uit de Sportlink-veldplanner (blokduur =
speelduur + pauze + in-/uitloop). Wat Sportlink niet kent, of als Sportlink niet bereikbaar is, behoudt de eigen berekening.

**Sportlink-status (#1774):** Sportlink is leidend (WZ-ADR-013), dus een terugval is zichtbaar en nooit stil. Het JSON-antwoord is
een envelope `{ "sportlink": {...}, "wedstrijden": [...] }`, maar **alleen op aanvraag met `?status=true`**. Zonder die parameter blijft
het antwoord de kale array van wedstrijdregels (ongewijzigd contract, zodat een reeds geopende of oudere client blijft werken); de
Planning-pagina vraagt de envelope expliciet aan. `sportlink` heeft de velden `geraadpleegd` (bool), `reden` (vaste code), `toelichting` (leesbare tekst),
`geraadpleegdOpUtc` (UTC-tijdstip of `null` bij terugval) en `koppelingHerstelNodig` (bool: de beheerder moet de koppeling herstellen).
Bij `geraadpleegd: false` toont de Planning de databasekopie, die kan afwijken van Sportlink; het HTML- en PDF-document zet dan een
melding onder de titel.

**Sportlink is leidend (#1582):** een Sportlink-blok zonder eigen regel (bijvoorbeeld een wedstrijd van een andere club op
hetzelfde park) komt als eigen regel in het antwoord (`wedstrijdCode` is dan `null`, `bron` is `Sportlink`). Een eigen regel die
Sportlink niet kent blijft staan met `nietInSportlink: true`. `tegenstander` is de andere kant van `wedstrijd` ten opzichte van
`teamNaam`. De drie velden `tegenstander`, `nietInSportlink` en `bron` zijn nieuw; de rest van de vorm is ongewijzigd.

**Autorisatie (#1400):** `Admin/User` (`AdminEndpoint.ExecuteAuthenticatedAsync`) — dit is het enige
endpoint in dit bestand dat niet uitsluitend admin vereist, want de Planning-pagina moet voor elke
ingelogde gebruiker zichtbaar zijn.

### Query-parameters

| Parameter | Type | Verplicht | Beschrijving |
|-----------|------|-----------|-------------|
| `datum` | `string` | **Ja** | Datum in `yyyy-MM-dd` formaat |
| `format` | `string` | Nee | `json` (standaard), `html` (zelfstandige pagina, `text/html`) of `pdf` (`application/pdf`, bestandsnaam `veldbezetting-<datum>.pdf`). Andere waarde: 400 (#1364) |
| `status` | `boolean` | Nee | `true` = JSON-antwoord als envelope met de Sportlink-status (#1774); weggelaten of een andere waarde = kale array (oud contract). Geen effect op `html`/`pdf` |

### Antwoord — JSON (200)

Zonder `status=true` is het antwoord de kale array van wedstrijdregels (de objecten onder `wedstrijden` hieronder). Met `status=true`:

```json
{
  "sportlink": {
    "geraadpleegd": true,
    "reden": "Ok",
    "toelichting": "Veld, aanvangstijd en speelduur komen uit de Sportlink-veldplanner.",
    "geraadpleegdOpUtc": "2026-10-22T08:15:00Z",
    "koppelingHerstelNodig": false
  },
  "wedstrijden": [
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
}
```

`wedstrijden` is gesorteerd op `aanvangsTijd`; wedstrijden zonder aanvangstijd staan achteraan.

Reden-codes (`sportlink.reden`): `Ok` (geraadpleegd), en bij terugval `Democlub`, `GeenAccommodatie`, `ExtensieUit`,
`EgressGeblokkeerd`, `ClientNietGeconfigureerd`, `KoppelingVerlopen`, `RolNietGekoppeld`, `AccommodatieOnbekend`, `NetwerkFout`,
`SportlinkFout` en `Onverwacht`. `koppelingHerstelNodig` is `true` bij `KoppelingVerlopen` en `RolNietGekoppeld`.

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
      "mtaModified": "2026-07-27T07:03:00Z",
      "aangemaaktDoorNaam": null,
      "aangemaaktOp": null,
      "herkomstVerwerkingId": null,
      "reden": null,
      "beoordeeldDoorNaam": null,
      "beoordeeldOp": null
    }
  ]
}
```

Sinds #1568 staat bij een alias die een beheerder aanmaakte of beoordeelde wie en wanneer: de
weergavenaam (momentopname) en het tijdstip (UTC). `aangemaaktDoorNaam: null` betekent dat het systeem
(sync of AI) de alias aanmaakte. In de database staat daarnaast de Entra object-ID (pseudoniem); die
komt nooit in een response.

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

### POST /api/beheer/teamaliassen

Een beheerder koppelt een teamtekst aan een team (#1568) — zonder code of SQL. Bron
`CoordinatorCorrectie`, direct `validated`. De genormaliseerde sleutel wordt server-side uit
`TeamNaamNormalisatie` gehaald; de aanmaker (object-ID + naammomentopname, geen e-mailadres) komt uit het
Easy Auth-principal, nooit uit de body.

```bash
curl -X POST http://localhost:7094/api/beheer/teamaliassen -H "Content-Type: application/json" \
  -d '{"ruweTekst":"j10-04","teamId":7,"herkomstVerwerkingId":123,"reden":"uit trace"}'
```

```json
{ "id": 31, "status": "aangemaakt", "ruweTekst": "j10-04", "ruweTekstGenormaliseerd": "JO10-4", "teamId": 7, "teamnaam": "[ClubCode] JO10-4" }
```

| Situatie | Antwoord |
|---|---|
| Nieuwe alias | `201`, `status: "aangemaakt"` |
| Alias met dezelfde sleutel bestond al voor hetzelfde team | `200`, `status: "bestaat-al"` (een niet-gevalideerde alias wordt gevalideerd) |
| Alias bestaat voor een ander team, `herkoppel` ontbreekt of `false` | `409`, `code: "conflict"`, met `bestaandeAliasId`, `bestaandTeamId`, `bestaandTeamnaam`, `bestaandeStatus` en `aantalRijen` (hoeveel alias-rijen herkoppelen raakt) |
| Idem met `"herkoppel": true` | `200`, `status: "herkoppeld"`; raakt de bestaande rij en rijen met dezelfde sleutel die niet uit de Sportlink-synchronisatie komen (bron `Sync` blijft ongemoeid) |
| De tekst past zonder alias bij meerdere teams (bijv. `13-1` → JO13-1 én MO13-1), `bevestigDubbelzinnig` ontbreekt of `false` | `409`, `code: "dubbelzinnig"`, met `kandidaten`. Met `"bevestigDubbelzinnig": true` wordt de alias toch aangemaakt: voortaan gaan álle mails met deze schrijfwijze naar dit team |
| Een gelijktijdige aanroep maakte dezelfde alias net eerder aan | `409`, `code: "bestaat-al"` (geen `500`) |
| Lege tekst, `teamId` ontbreekt, tekst > 200 tekens, geen herkenbare teamaanduiding | `400` |
| Onbekend of inactief team (van deze club) | `404` |

Een open regel in `GET /api/beheer/onbekende-teamteksten` met dezelfde sleutel wordt automatisch
`afgehandeld`. `GET /api/beheer/teams/keuzelijst` levert de teams met `teamId` voor een keuzelijst.

### GET /api/beheer/onbekende-teamteksten

Teamteksten uit binnengekomen e-mails die de pipeline niet aan een eigen team kon koppelen (trace-stap
`team-herkenning`: `Onopgelost` of `MeerdereKandidaten`). Parameters: `status` (`open`, `afgehandeld`,
`genegeerd`; leeg = alles) en `limit` (default 100, max 500).

```json
{
  "count": 1, "limit": 100, "open": 1,
  "items": [
    { "id": 4, "voorbeeld": "j10-04", "genormaliseerd": "JO10-4", "aantal": 3,
      "eerstGezien": "2026-10-01T08:00:00Z", "laatstGezien": "2026-10-06T19:30:00Z",
      "laatsteVerwerkingId": 812, "status": "open" }
  ]
}
```

`voorbeeld` is de gesaneerde, afgekapte schrijfwijze (e-mailadressen en lange cijferreeksen gemaskeerd);
`laatsteVerwerkingId` is een aanwijzing zonder foreign key. Een regel die 90 dagen niet meer is gezien
wordt door de wekelijkse e-mail-cleanup verwijderd. `PUT /api/beheer/onbekende-teamteksten/{id}/status`
met `{"status":"genegeerd"}` (of `open`/`afgehandeld`) zet een regel om; onbekend id → `404`.

### PUT /api/beheer/teamaliassen/{id}/valideer

```bash
curl -X PUT http://localhost:7094/api/beheer/teamaliassen/12/valideer -H "Content-Type: application/json" -d '{"status":"validated"}'
```

```json
{ "id": 12, "status": "validated" }
```

Alleen `validated` of `rejected` zijn toegestaan → anders `400`. Onbekende id (of een id van een
andere club) → `404`. Wie (object-ID + naam) en wanneer (UTC) wordt vastgelegd (#1568).

### DELETE /api/beheer/teamaliassen/{id}

```bash
curl -X DELETE http://localhost:7094/api/beheer/teamaliassen/12
```

```json
{ "deleted": true, "id": 12 }
```

Sync-origin aliases retain their existing row with status `rejected`, so canonicalization cannot validate the association again. Other origins are physically deleted. The response shape remains unchanged. Rejection blocks the alias association, not an independent canonical-name match. Both actions are strictly scoped to the current club; logs contain only the alias identifier, without the administrator identity.

### DELETE /api/beheer/leermomenten/{id}

```bash
curl -X DELETE http://localhost:7094/api/beheer/leermomenten/5
```

```json
{ "deleted": true, "id": 5 }
```

Verwijdert uitsluitend een door een beheerder toegevoegd leermoment (herkomst `Admin`) van de eigen club. Een id van
een andere club of een onbestaand id → `404`; een leermoment uit een beantwoorde mail (herkomst `Reply`) → `409`.

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
curl -X POST http://localhost:7094/api/planner/bevestig -H "Content-Type: application/json" -d '{"datum":"2026-04-25","aanvangsTijd":"12:00","veldNummer":3,"leeftijdsCategorie":"JO13","teamNaam":"[ClubCode] JO13-1","tegenstander":"[Tegenstander] JO13-2"}'
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
curl -X POST http://localhost:7094/api/planner/herplan-bevestig -H "Content-Type: application/json" -d '{"wedstrijdcode":12345678,"gewensteAanvangsTijd":"10:00","gewenstVeldNummer":2,"opmerking":"Tijdstip is niet haalbaar"}'
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

**Herstel van onderbroken jobs (#1839, eigenaarsbesluit 2026-11-11 en kostenbesluit).** Blijft een job
door een gecrashte of herstarte host op `running` staan, dan herstelt het systeem hem begrensd: een
job die 30 minuten op `running` staat geldt als onderbroken en wordt hoogstens 2 keer opnieuw
aangeboden (3 pogingen in totaal); daarna staat hij op `failed` met een vaste melding. Het herstel
heeft bewust geen eigen timer (geen extra uitvoeringen of database-wake-ups): het loopt mee met
bestaande activiteit, namelijk `POST /beheer/sync/trigger`, `GET /beheer/sync/status` en de geplande
sync. Een normale fout of gedeeltelijke mislukking blijft terminaal `failed` en wordt niet automatisch
herhaald. De claim (`pending` naar `running`) telt een poging in de kolom `Attempt`/`attempt` (een
bestaande lopende job telt bij de upgrade als poging 1) en elke afronding is aan dat pogingnummer
gebonden, zodat een verouderde worker een herstarte poging niet overschrijft. De drempels staan op
één plek: `Planner.Shared/Sync/SyncJobRecoveryPolicy.cs`. De API zelf verandert niet.

```bash
curl -X POST http://localhost:7094/api/beheer/sync/trigger
# {"status":"gestart","jobId":"…","weekOffsetFrom":-1,"weekOffsetTo":15,"tijdstip":"…"}

curl "http://localhost:7094/api/beheer/sync/status?jobId=<jobId>"
# {"lastSyncTimestamp":"…","fetchSchedule":"…","status":"ok",
#  "job":{"id":"…","status":"running|succeeded|failed","weekOffsetFrom":-1,"weekOffsetTo":15,
#         "createdAt":"…","startedAt":"…","completedAt":null,"errorMessage":null}}
```

**Volledig seizoen opnieuw ophalen (#1352):** optionele JSON-body, zelfde semantiek als
`GET /sync-matches?reset=true&season=`:

```bash
curl -X POST http://localhost:7094/api/beheer/sync/trigger \
  -H "Content-Type: application/json" -d '{"reset":true,"season":2025}'
# weekOffsetFrom = weekoffset van de seizoensstart i.p.v. -1
```

`season` is het startjaar (2000 t/m huidig jaar + 1) en is verplicht bij `reset:true`; zonder
`reset` wordt `season` genegeerd en blijft het gedrag ongewijzigd. Ongeldige waarde of ongeldige
JSON → `400` met `{"error":"…"}` (NL-melding). Validatie staat tier-onafhankelijk in
`Planner.Shared/Sync/SyncTriggerCore.cs`.

Zonder `?jobId=` geeft `/beheer/sync/status` de actieve job terug, anders de laatste afgeronde job. `job` is `null` zolang er
nog nooit een sync is gestart. Mogelijke `job.status`-waarden: `pending`, `running`, `succeeded`,
`failed` — bij `failed` bevat `errorMessage` de reden.

### Sync-logboek en voortgang (#1912)

`GET /beheer/sync/status` levert aanvullend `activeJob`, `lastCompletedJob`, maximaal tien `jobs` en `logAvailable`. Elke job bevat `attempt` en `phases` voor de actuele poging. Elke fase bevat start/einde in UTC, een vaste status en reden, aantallen `fetched`, `added`, `updated`, `failed` en voortgang `progress`/`total`. Onbekende aantallen zijn `null`; de UI toont geen verzonnen nul. Historische foutteksten worden niet doorgegeven. `partial` maakt een onvolledige run zichtbaar.

`POST /beheer/sync/trigger` kiest atomair per club een actieve job of maakt er één aan. Bij een bestaande job volgt HTTP 202 met `alreadyRunning: true`, dezelfde `jobId` en eventueel `startedAt`/`phase`; alleen de maker zet een bericht op de queue. Bij enqueue-falen volgt een vaste 503-melding. Een nooit geclaimde, oudere pending-rij blijft bewaard maar blokkeert na de bestaande hersteltermijn geen nieuwe handmatige start. Dit verwijdert of herstelt de historische rij niet.

De fasegegevens blijven 30 dagen bewaard op beide tiers; opruimen gebeurt binnen bestaande activiteit, zonder nieuwe timer. Logschrijven is best-effort en gebonden aan club, job en poging. Als de faseopslag niet bereikbaar is, blijft de jobstatus beschikbaar met `logAvailable: false`.
