# Changelog

Alle noemenswaardige wijzigingen in dit project worden bijgehouden in dit bestand.

De indeling volgt [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versienummering volgt het 4-cijferig schema `MAJOR.MINOR.PATCH.REVISION` — zie [docs/VERSIONING.md](docs/VERSIONING.md).

> **Definities en beslisregels** -- wat is een bug, wat is een feature, wat hoort hier wel/niet in:  
> zie [docs/VERSIONING.md](docs/VERSIONING.md).

> **Conventie voor versies:**
> - `MAJOR` (x.0.0.0) -- grote nieuwe laag of breaking change (bijv. Admin GUI, nieuwe auth-laag)
> - `MINOR` (2.x.0.0) -- nieuwe feature, backwards compatible (nieuw endpoint, nieuw scherm, nieuwe instelling)
> - `PATCH` (2.0.x.0) -- bugfix of beveiligingspatch
> - `REVISION` (2.0.0.x) -- kleine fix, CSS/UX-verbetering of aanpassing met zichtbaar effect; elke commit die de beheerder merkt

---

## [Unreleased]

### Fixed
- Blazor WASM crashte bij nl-NL browser-locale ("An unhandled error has occurred") door ontbrekende globalization-data in invariant-mode (#359). Alle gebruikers met een Nederlandse browser konden de app niet openen.
- Email-handtekening gebruikte hardcoded fallback "Coördinator thuiswedstrijden" als `coordinatorFunctie` niet geconfigureerd was (#356). Veld is nu optioneel — ontbrekende instelling geeft geen tekst, geen fout.

### Security
- Setup-scripts `fix-merge-procedure.sql` en `complete-database-setup.sql` gebruikten `GETDATE()` (lokale servertijd) in gegenereerde `sp_MergeStgToHis` voor `mta_modified`/`mta_inserted`. Vervangen door `GETUTCDATE()` (#355). Productie CI-pad was niet aangetast — alleen lokale developer-setup.

## [2.5.0.1] — 2026-05-26

### Fixed
- Multi-club data-isolatie volledig gerepareerd (#352): alle admin-schermen (Instellingen, Sync-status, Templates, Speeltijden, Voorkeurstijden, Veldenbeschikbaarheid, Email-log, Leermomenten, Uitgesloten-emails, Teambegeleiding) tonen nu uitsluitend data van de actief geselecteerde club. Voordien negeerden alle API-endpoints de `X-Club-Code`-header van de Blazor-frontend en laadden altijd de primaire/eerste club uit de startup-cache.
- Club-switch in de topbalk staat nu gecentreerd in de navigatiebalk in plaats van rechtsuitgelijnd.
- Instellingen-submenu in de navigatiebalk is nu zichtbaar (knop had Bootstrap `.btn-link`-kleur die onzichtbaar was op de donkere achtergrond).
- Import-script teambegeleiding: `TRUNCATE TABLE` vervangen door `DELETE WHERE ClubCode = @cc` zodat data van andere clubs niet wordt gewist bij een import.
- Navigatiemenu-iconen staan nu gelijk uitgelijnd met de menutekst (was licht omhoog verschoven door een verouderde CSS-regel voor het oude SVG-icon-systeem).
- Dagplanning-pagina wordt nu automatisch leeggemaakt bij het wisselen van club in de topbalk, zodat er geen verwarring ontstaat met data van de vorige club.

---

## [2.5.0] — 2026-05-26

### Added
- Navigatiemenu toont nu iconen (Bootstrap Icons) bij elk menu-item.
- Navigatiemenu is geherstructureerd: Dagplanning, Leermomenten, Email-tester en Teambegeleiding staan bovenaan; Instellingen, Speeltijden, Voorkeurstijden, E-mailtemplates en Thema zijn samengebracht onder een inklapbaar 'Instellingen'-submenu dat automatisch openklapt zodra de beheerder een van die pagina's bezoekt.
- Instellingen-pagina heeft nu een 'Opslaan'-knop rechtsboven naast de paginatitel, zodat opslaan toegankelijk is zonder naar het einde van het formulier te scrollen.
- Thema-pagina heeft nu een 'Opslaan'-knop rechtsboven naast de paginatitel.
- Themabeheer uitgebreid: beheerders kunnen nu een favicon en club-logo opslaan via de Thema-pagina. De knop 'Ophalen' haalt kleuren, favicon én OG-afbeelding tegelijk op uit de club-website. Het favicon wordt direct in het browsertabblad toegepast; het logo verschijnt linksboven in de navigatiebalk naast de clubnaam. Nieuwe DB-velden `FaviconUrl` en `LogoUrl` op `dbo.AppSettings`.
- Dashboard herontworpen: toont nu vier grote klikbare kaarten (Dagplanning, Leermomenten, Email-tester, Teambegeleiding) als snelkoppelingen, met optioneel club-logo bovenaan.
- Synchronisatiestatus en emailverwerkingslog zijn verplaatst van het Dashboard naar de Instellingen-pagina (bovenaan, boven de configuratievelden).

---

## [2.4.1] — 2026-05-26

### Fixed
- Migratie 002 (AllStars FC seed) werkt nu correct: kolomnamen in `avg.Teambegeleiding` gecorrigeerd en VeldNummer-reeks aangepast naar 101-103 om PK-conflict met de primaire club te vermijden.
- Club-selector in de topbalk selecteert nu automatisch de juiste primaire club (op basis van `SyncEnabled=1`) in plaats van altijd de eerste club in de lijst. Brede selector, het "Club:"-voorvoegsel is verwijderd.
- Wisselen van club in de topbalk werkt nu correct: alle pagina's (Dashboard, Instellingen, Thema, Voorkeurstijden, Speeltijden, E-mailtemplates, Teambegeleiding, Leermomenten) laden hun data automatisch opnieuw zodra de beheerder van club wisselt.
- Thema-pagina UX (#338): 'Kleuren ophalen' staat nu naast het URL-veld als input-groep; 'Opslaan' is duidelijk los van de URL-sectie geplaatst met een scheidingslijn; secundaire kleur-preview werkt nu correct real-time; HEX-waarde zonder `#`-prefix (bijv. `1b6ec2`) wordt automatisch aangevuld.

---

## [2.4.0] — 2026-05-26

### Added

- **AllStars FC demo-club en multi-club GUI switch (#324):** Synthetische demo-club AllStars FC toegevoegd voor testen buiten het KNVB-seizoen. De Admin GUI toont een club-selector dropdown in de topbalk zodat beheerders naadloos kunnen schakelen tussen de primaire club en AllStars FC. Alle API-calls sturen automatisch de `X-Club-Code` header mee via een nieuwe `ClubCodeHeaderHandler`. Database-laag: `ClubCode`-kolom toegevoegd aan `his.teams`, `his.matches` en `his.matchdetails`; `SyncEnabled`-vlag in `dbo.AppSettings` (0 = geen Sportlink API-sync voor deze club). Nieuw endpoint `GET /api/beheer/clubs` voor de lijst van beschikbare clubs. Idempotente migratiescripts in `scripts/migrations/`.
- **Club-thema aanpasbaar (#325):** Beheerders kunnen via de nieuwe pagina **Thema** in de Admin GUI de kleurstelling van de interface aanpassen op de huisstijl van de club (primaire kleur, secondaire kleur, accentkleur, tekstkleur op primaire achtergrond). Kleuren worden als CSS-variabelen live toegepast zonder pagina-herladen. Optioneel: automatisch kleuraccenten extraheren uit de club-website. De themakleuren worden bij elke login opgehaald en toegepast. Technisch: vijf nieuwe kolommen op `dbo.AppSettings`, drie nieuwe admin-endpoints (`GET/PUT /api/beheer/theme`, `POST /api/beheer/theme/extract` met SSRF-bescherming) en een Blazor-beheerpagina.
- **Email feedback loop — zelflerend classificatiesysteem (#323):** Wanneer een afzender reageert op een AI-antwoord en de planner heeft het oorspronkelijke verzoek verkeerd geclassificeerd, detecteert het systeem nu automatisch die correctie en slaat deze op als "leermoment". Beheerders kunnen leermomenten valideren of afwijzen via de nieuwe pagina **Leermomenten** in de Admin GUI. Gevalideerde leermomenten worden bij de volgende e-mail als few-shot voorbeeld meegegeven aan de AI, waardoor dezelfde classificatiefout niet herhaald wordt. Technisch: twee nieuwe kolommen op `planner.EmailVerwerking` (`IsReplyOpOnsAntwoord`, `ReplyOpVerwerkingId`), nieuwe tabel `planner.ClassificatieCorrectie`, drie nieuwe admin-endpoints en een Blazor-beheerpagina.
- **Real-time Sportlink API voor plannerbeschikbaarheid (#24):** De planner raadpleegt nu standaard de live Sportlink `/programma`-API bij het berekenen van veldbeschikbaarheid, in plaats van uitsluitend de lokale database. Voordeel: altijd actuele veldocupatie, ook als de nachtelijke sync nog niet is gelopen. Bij een API-fout (time-out, netwerkprobleem) valt de planner automatisch terug op de database. De nieuwe instelling "Real-time Sportlink API raadplegen" op de Instellingen-pagina schakelt dit gedrag per club aan of uit.
- **E-mailtemplates koppeling aan pipeline (#287):** `BouwTemplateAntwoord` raadpleegt nu de database voor elke hardcoded fallback via `EmailTemplateService.GetTemplateAsync`. Als een beheerder een template heeft aangemaakt voor `beschikbaarheid_check`, `herplan_verzoek`, `bevestiging`, `team_contact_opvragen` of `buiten_scope`, wordt die DB-versie gebruikt in plaats van de hardcoded standaard. Dropdown in de Admin GUI uitgebreid met alle actieve template-keys, ingedeeld per categorie.

### Changed

- **README: Sportlink Club Dataservice als expliciete vereiste vermeld:** De README maakt nu duidelijk dat een actief Club Dataservice-abonnement bij Sportlink verplicht is. Inclusief prijsindicatie en link naar de productpagina, zodat clubs weten wat ze nodig hebben voordat ze beginnen.

### Fixed

- **Start-Debug.ps1 parse-fout door em-dash encodingprobleem (#326):** Regel 109 bevatte een em-dash (U+2014) die PowerShell als Windows-1252 decodeerde. Byte 0x94 werd daardoor als aanhalingsteken gelezen, waardoor de string vroegtijdig sloot en het script niet kon starten. Vervangen door ASCII-koppelteken. Gevolg van de bug: alle lokale services (Azurite, FunctionApp, BlazorAdmin) startten niet via `Start-Debug.ps1`.
- **Clean-GitHistory.ps1 bugfixes (#321):** Twee fouten gerepareerd: (1) `OrderedDictionary` heeft geen `ContainsKey()`-methode — alle 11 aanroepen vervangen door `Contains()`; (2) git weigert te fetchen naar een checked-out branch — `git checkout --detach HEAD` toegevoegd voor de fetch-stap, daarna terugkeer naar de originele branch. Script is nu volledig uitvoerbaar.
- **DB connection retry window verdubbeld (#306):** `WaitForDatabaseAsync` verhoogd van 10 naar 20 retries (15s per poging = 5 min totaal). Voorkomt dat de dagelijkse timer-sync mislukt als Azure SQL Free Tier langer dan 2,5 min nodig heeft om te hervatten na auto-pause.
- **Kalender weekstart op maandag (#300):** `<html lang="en">` was de oorzaak van zondag-als-eerste-dag in alle datumkiezers en kalenders. Gewijzigd naar `lang="nl"` + `CultureInfo("nl-NL")` in `Program.cs`.
- **Planner: no-op suggesties onderdrukt (#301):** De planner toonde verplaatsingen die de eindtijd niet verbeterden. Na het genereren van suggesties wordt nu de gesimuleerde nieuwe eindtijd vergeleken met de huidige; zijn er geen verbeterd werd de huidige planning is al optimaal teruggegeven.
- **Teambegeleiding: e-mail en telefoon zichtbaar op kaartje (#299):** Beheerders zien nu het e-mailadres en telefoonnummer van elke begeleider op de `/teambegeleiding`-pagina. Klikbare `mailto:` en `tel:`-links. Onder de kaartjes staat een kopieerknop voor de Outlook-ontvangersregel.
- **Voorkeurstijden: team-dropdown (#289):** De vrije tekstinvoer voor het teamveld is vervangen door een dropdown gevuld vanuit `GET /api/beheer/teams`. Voorkomt typfouten en inconsistenties.
- **Voorkeurstijden: inactieve teamregels verborgen (#288):** Inactieve teamregels worden niet meer getoond in de lijst.
- **Instellingen: SQL-instructies verwijderd (#285):** De rode "Wijzigen via SQL: UPDATE ..." helpteksten zijn verwijderd en vervangen door "Contacteer de systeembeheerder om deze waarde te wijzigen."

### Security

- **Preventie-infrastructuur club-specifieke data (#321):** Drie onafhankelijke lagen voorkomen nu dat Azure-resourcenamen of infrastructuuridentifiers naar GitHub gaan: (1) 7 nieuwe gitleaks-regels op structurele naampatronen, (2) nieuw CI-job `infra-patterns` in security-scan.yml met ondersteuning voor `CLUB_EXTRA_PATTERNS` GitHub Secret, (3) pre-push hook bugfix voor nieuwe branches. Script `scripts/security/Clean-GitHistory.ps1` voor historische git-history cleanup toegevoegd.
- **Absoluut verbod club-specifieke data in GitHub issues en PR's:** SECURITY.md uitgebreid met volledig hoofdstuk — vervangingstabel, rapportagepatroon voor bevindingen, en controleplicht checklist. Aanleiding: 29 issues/comments bevatten echte Azure resource namen, tenant/client IDs en club-domein; allemaal geredacteerd. (#PR320)
- **Teambegeleiding PII alleen voor admin-rol (#310):** Alle drie endpoints in `AdminTeambegeleidingFunction` worden nu beschermd met `RequireAdmin` i.p.v. `RequireAuthenticated`. Persoonsgegevens (naam, e-mail, telefoonnummer) zijn niet meer toegankelijk voor de `user`-rol.
- **Documentatiefout appsettings.Production.json gecorrigeerd (#311):** De handleiding instrueerde foutief om `appsettings.Production.json` te committen. Vervangen door correcte instructie: dit bestand wordt door CI aangemaakt en mag nooit in git.
- **Hardcoded VRC resourcenamen verwijderd uit infrastructuurbestanden (#312):** `infrastructure/main.bicep`, `main.parameters.json`, `docs/openapi.yaml`, `docs/openapi.json` en `scripts/azure/Configure-EntraApp.ps1` gebruiken nu `<clubcode>`-placeholders i.p.v. hardcoded VRC-waarden.

---
## [2.3.0] — 2026-05-24

### Changed

- **Versie 2.2.1** — PATCH-bump voor feedback widget bugfix (#284).
- **Start-Debug.ps1: BlazorAdmin met hot reload** (#285): `dotnet watch run` vervangt `dotnet run`. Wijzigingen in `.razor`, `.cs` en `.css` worden automatisch herladen zonder herstart van de service. Nieuw `-NoWatch` vlag voor omgevingen zonder hot reload. Duidelijke melding dat FunctionApp géén hot reload ondersteunt (Azure Functions isolated worker limitatie) en herstart vereist na C#-wijzigingen.

### Fixed

- **Dagplanning: onnodige optimalisatiesuggesties onderdrukt** (#291/#290): planner genereert geen suggesties meer als er geen zinvol doel is. `veld5-ontlasten` is nooit zinvol op doordeweekse dagen (veld 1-4 niet beschikbaar). `strakker-plannen` is alleen zinvol als een gewenste eindtijd is opgegeven of het doel expliciet gekozen. Zonder doel én zonder gewenste eindtijd geeft de planner direct "Geen optimalisatie nodig" terug.
- **Dagplanning HTTP 500 bij dagen zonder wedstrijden** (#291): `PlannerHtmlGenerator.GenereerHtml` riep `.Max()` aan op een lege lijst wanneer er geen wedstrijden gepland waren (bijv. zondag). Geeft nu een informatieve melding terug in plaats van een 500-fout.
- **Dry-run email: teamconflict heeft nu eigen antwoordtekst** (#291): teamconflict verscheen voorheen als aanhangsel aan "geen veld beschikbaar" — terwijl het veld helemaal niet gecheckt werd bij een teamconflict. Teamconflict heeft nu een eigen berichttak zonder vermelding van veldgebrek.
- **Dry-run planner response toonde geneste lege arrays** (#291): `JToken` (Newtonsoft) geserialiseerd via `System.Text.Json` gaf corrupt JSON terug. Opgelost door `JsonDocument.Parse(...).RootElement` te gebruiken in `EmailTestFunction`.
- **Feedback widget loop bij aanvulvragen** (#283): na het beantwoorden van OpenAI-aanvulvragen keerden dezelfde vragen terug bij "Opnieuw controleren". Backend accepteert nu direct zodra antwoorden zijn ingevuld (re-validatie overbodig — antwoorden vullen de gaten per definitie). Frontend bewaart bovendien bestaande antwoorden als vragen ongewijzigd terugkomen.

### Added

- **Teambegeleiding opvragen (#168):**
  - Admin GUI-pagina `/teambegeleiding`: beheerders en gebruikers (user-rol) kunnen een team kiezen en de naam + rol van de begeleiders inzien. Een inline formulier stuurt een vraag door aan de begeleiding — het e-mailadres van de coach wordt nooit getoond (AVG art. 6.1.f). Reply-To is het e-mailadres van de aanvrager; de coach antwoordt rechtstreeks.
  - E-mail auto-reply: inkomende berichten met "wie is de trainer/coach van [team]?" worden automatisch geclassificeerd als `TeamContactOpvragen`, doorgestuurd naar de coach (BCC coördinator) en beantwoord met "uw vraag is doorgestuurd, contactgegevens worden niet gedeeld".
  - Nieuwe API-endpoints: `GET /api/beheer/teambegeleiding`, `GET /api/beheer/teambegeleiding/{team}`, `POST /api/beheer/teambegeleiding/doorsturen`.
  - Nieuw Auth-niveau `RequireAuthenticated()` in EasyAuthHelper: admin- én user-rol hebben toegang tot teambegeleiding.
- **Speeltijden beheer in Admin GUI** (#291): nieuwe pagina `/instellingen/speeltijden` waarmee beheerders speeltijden per leeftijdscategorie kunnen inzien, toevoegen, bewerken en verwijderen. Het veld "Totaal (incl. rust)" is de totale veldblokkeertijd die de planner direct gebruikt — rust wordt niet apart opgeteld in code. API-endpoints: `GET/POST /api/beheer/speeltijden` en `PUT/DELETE /api/beheer/speeltijden/{leeftijd}`.
- **Speeltijden DB leidend voor veldplanning** (#291): de planner gebruikt uitsluitend `dbo.Speeltijden.WedstrijdTotaal` voor de berekening van wedstrijdduur. De Sportlink API-waarde `Duration` (die geen rust bevat) wordt niet meer gebruikt. Ontbrekende leeftijdscategorie geeft nu een duidelijke foutmelding met verwijzing naar de beheerpagina in plaats van een stille fallback naar 105 minuten.

- **Infrastructure as Code met Bicep** (#257): nieuwe `infrastructure/` map met Bicep-bestanden die alle bestaande Azure-resources declaratief beschrijven (Function App, Consumption Plan, Storage Account, Static Web App, Application Insights). `az deployment group what-if` detecteert drift zonder wijzigingen te maken. Monitoring-module is aanwezig maar standaard uitgeschakeld (`deployMonitoring=false`) om onbedoelde Log Analytics kosten te voorkomen. Nieuwe GitHub Actions workflow `infrastructure.yml` is alleen handmatig uitvoerbaar met keuze tussen `what-if` en `deploy`.
- **Dagplanning pagina in Admin GUI** (#235): nieuwe pagina `/dagplanning` in de Admin GUI waarmee beheerders de veldoptimalisatie kunnen starten zonder directe API-kennis. Invoervelden: datum (standaard eerstvolgende zaterdag), optimalisatiedoel, gewenste eindtijd, buffertijd. Na genereren toont de pagina een statistiekenbalk (huidige en geschatte eindtijd, verplaatsingen, bezettingsgraad) en de interactieve HTML-planner in een iframe. Knoppen om de e-mailversie te kopiëren naar klembord en de browser-versie te downloaden als `.html`-bestand. Het `/api/planner/optimaliseer`-endpoint gebruikt nu Easy Auth (Bearer token) in plaats van een Function Key, conform alle andere admin-endpoints.
- **OpenAPI 3.0 spec voor alle 40 API-endpoints** (#259): volledig gedocumenteerde `docs/openapi.yaml` (2765 regels) met alle endpoints gegroepeerd per tag (core, planner, beheer, feedback). Bevat request/response schemas via `$ref`, security-schemas (functionKey + easyAuth), `x-correlation-id` response header, rate limits, en nauwkeurige request-body shapes op basis van de broncode. Te gebruiken met Swagger UI, Postman of stoplight.io.
- **MONITORING.md: alerting, KQL-queries en escalatiematrix** (#260): nieuwe documentatie met Application Insights instelprocedure, gratis alert-typen (Activity Log + Resource Health), KQL-debugging queries (correlation-ID tracing, 500-fouten, trage requests, sync-monitoring), escalatiematrix en bekende beperkingen. `APPLICATIONINSIGHTS_CONNECTION_STRING` toegevoegd aan `local.settings.template.json`. `CLAUDE.md` documentatietabel verwijst nu naar `docs/MONITORING.md`.
- **Automatische database-migratie in deploy pipeline** (#256): `deploy.yml` heeft nu een `db-migrate` job die na elke deploy naar `main` het `Script.PostDeployment1.sql` uitvoert via `azure/sql-action`. De job voegt tijdelijk het runner-IP toe aan de Azure SQL-firewall en verwijdert die regel altijd (ook bij fout). De job wordt overgeslagen als `AZURE_SQL_SERVER_NAME` niet geconfigureerd is (veilig voor nieuwe omgevingen). Vereiste nieuwe GitHub secrets/vars: `AZURE_SQL_CONNECTION_STRING`, `AZURE_SQL_RESOURCE_GROUP`, `AZURE_SQL_SERVER_NAME`.
- **Correlation-ID tracing in alle admin-endpoints** (#258): alle 29 HTTP-functies in `FunctionApp/Admin/` en `FunctionApp/Feedback/` lezen nu `x-correlation-id` uit de request header (of genereren een nieuwe GUID) en sturen deze terug in elke response header. De ID is ook beschikbaar als `CorrelationId` in de Application Insights log-scope voor end-to-end tracing via KQL.
- **AVG-bewaartermijn avg.Teambegeleiding** (#238): `sp_CleanupTeambegeleiding` stored procedure verwijdert rijen ouder dan 1 jaar (vangnet als importscript langere tijd niet gedraaid heeft). Maandelijkse timer trigger `CleanupTeambegeleiding` (1e van de maand, 04:00 UTC). Bewaartermijn gedocumenteerd in `exports/README.md`.
- **Kostenbeleid als meest prominente architectuurregel in CLAUDE.md** (#255): expliciete gratis-eerst eis voor alle Azure-resources; verplichting om vóór elke feature-toevoeging én deployment actuele Microsoft-prijsdocumentatie te controleren via Microsoft Learn MCP; harde stop-deployment regel bij gedetecteerde prijswijziging; geverifieerde tabel van gratis vs. potentieel-betaalde resources; verificatiechecklist als deployment-gate.
- **Build-teller versioning voor lokale ontwikkeling**: vierde versiecomponent (`2.2.0.x`) automatisch ophoogbaar via `Bump-Build.ps1`. Zichtbaar in health-endpoint (`GET /api/health` geeft nu ook `version` terug) en in de Feedback-modal van de Admin GUI. `.\Bump-Build.ps1 -NewPatch` verhoogt de patch-versie voor nieuwe functionaliteit.
- **Documentatie-index**: alle documentatie geconsolideerd in `docs/` met een nieuwe [inhoudsopgave (docs/INDEX.md)](docs/INDEX.md) ingedeeld per doelgroep (beheerders, developers, architectuur, security). README toont de documentatietabel prominent.

### Changed

- **Email processor: AI-First flow — database slaapt bij BuitenScope-emails** (#251): de AI-classificatie vindt nu plaats vóór elke database-aanroep. Emails die als BuitenScope worden geclassificeerd krijgen alleen een Outlook-label ("Geen AI antwoord") — de database wordt niet gewekt. Pas als er minstens één email is die daadwerkelijk door de planner verwerkt moet worden, wordt de database wakker gemaakt. Spaart vCores op de gratis Azure SQL database. Bijvangst: `GETDATE()` → `GETUTCDATE()` in alle UPDATE-statements in de email processor.

### Fixed

- **Git hooks check in Test-App.ps1** (#239): `Test-App.ps1` controleert nu of `core.hooksPath` is ingesteld op `.githooks` en of `sensitive-patterns.txt` aanwezig is. Ontbrekende configuratie geeft een waarschuwing (niet een fout, want CI dekt de fallback).
- **Dagelijkse sync brak af als `accommodatie` niet ingesteld** (#254): `MarkeerVervallenGeplandeWedstrijden` gooit nu geen exception als de instelling ontbreekt — de stap wordt overgeslagen met een waarschuwing. De rest van de sync (teams, wedstrijden, uitslagen) loopt gewoon door.
- **Deploy smoke test: curl timeout brak retry-loop af** (#264): `set -e` in GitHub Actions liet curl's exit code 28 (timeout) de hele step onmiddellijk beëindigen. Alle `curl`-aanroepen in de test-stap gebruiken nu `|| true` zodat timeouts niet meer de step afbreken en de retry-loop altijd doorloopt.
- **FunctionApp target terug naar net9.0** (#264): vorige sessie had `net10.0` ingezet als target framework "voor lokale dev", maar het Azure Functions Linux Consumption Plan ondersteunt alleen `.NET 9`. Resultaat: 502 op alle endpoints na elke deploy. Gecorrigeerd naar `net9.0`; .NET 10 SDK kan `net9.0` projecten bouwen en uitvoeren.
- **FunctionApp target framework**: `net9.0` expliciet vastgelegd als vereiste voor Azure Functions op Linux Consumption Plan — upgrade naar `net10.0` veroorzaakt een 503 bij eerste deploy. Geborgd in `CLAUDE.md` en projectgeheugen zodat deze fout niet opnieuw wordt gemaakt.

### Security

- **AVG: coordinator-mailboxadres niet meer gelogd (#269)**: `EmailGraphService` logt niet langer het e-mailadres van de coordinator-mailbox in logregels bij inbox-polling (aanwezig bij "Geen emails gevonden", "X emails opgehaald" en foutmeldingen). Logs bevatten nu "coordinator-mailbox" als generieke omschrijving.
- **SQL-injectie in MergeStgToHis opgelost (#270)**: schema- en tabelnamen werden als strings in de query-string gezet via string interpolatie. Vervangen door `SqlParameter`-objecten via `command.Parameters.AddWithValue()`. De stored procedures `sp_CreateTargetTableFromSource` en `sp_MergeStgToHis` ontvangen de parameters nu netjes via de parameterlijst.
- **AVG: e-mailadres niet meer gelogd bij toevoegen uitsluitingsadres** (#248): `AdminUitgeslotenEmailFunction` logt nu alleen het ID van het nieuwe uitsluitingsadres, niet het e-mailadres zelf. Consistent met de delete-actie die ook alleen ID logt.
- **AVG: Afzender gemaskeerd in email-log API** (#241): `/api/beheer/email-log` geeft `Afzender` terug als `***@domein.nl` in plaats van het volledige e-mailadres. De domein-informatie blijft beschikbaar voor debugging; het persoonsgegeven (lokaal deel) niet.
- **Architectuurovertreding opgelost: ClubCode DEFAULT 'VRC' verwijderd** (#242): `DEFAULT 'VRC'` constraint verwijderd uit `planner.EmailVerwerking.ClubCode`. `CHECK (LEN(ClubCode) > 0)` toegevoegd. Migratie in `Script.PostDeployment1.sql` dropt de bestaande constraint in productie.
- **Security-scan scope uitgebreid naar alle docs/** (#240): `pii-docs` job scant nu alle `docs/*.md` bestanden met `git ls-files`. Gerelateerd: een voorbeeld-e-mailadres in `docs/API.md` vervangen door `trainer@voorbeeld.nl`.
- **tenantId/clientId verwijderd uit documentatie en scripts** (#237): `docs/AZURE-ENTRA-SETUP.md` gebruikt nu generic placeholders i.p.v. echte Azure IDs. Scripts ophalen IDs dynamisch via `az ad sp show`. `appsettings.Production.json` al vervangen door template (PR #244).

- **Pre-publish cleanup — PII en servernamen verwijderd uit broncode** (#135): drie categorieën anonimisatie vóór publicatie als open-source project: (1) club-specifieke e-mailadressen in docs en scripts vervangen door generieke plaatshoudernamen; (2) hardcoded Azure SQL servernaam in foutmelding vervangen door generieke tekst (`Azure SQL Server → Database`); (3) setup-scripts documenteren nu generieke defaults zodat andere clubs ze direct kunnen gebruiken.

---

## [2.1.2] — 2026-05-20

**PATCH-release: API-connectie hersteld na login — `net_http_handler_not_assigned` opgelost.**

### Fixed

- **Alle API-aanroepen faalden na inloggen** (#195): dashboard, instellingen en feedbackknop gaven `net_http_handler_not_assigned` terug na een succesvolle login. Oorzaak: `AuthorizationMessageHandler` (die MSAL Bearer tokens toevoegt) is een `DelegatingHandler` en vereist een `InnerHandler` — de transport-laag die het HTTP-verzoek naar de browser fetch API stuurt. Zonder expliciete toewijzing gooit Blazor WASM de fout bij elke API-aanroep. Fix: `handler.InnerHandler = new HttpClientHandler()` in `Program.cs`.

---

## [2.2.0] — 2026-05-20

**MINOR-release: AVG-retentiebeleid EmailVerwerking, PII uit logs, security hardening en documentatie-update.**

### Added

- **AVG-retentie EmailVerwerking** (#208): automatische cleanup via wekelijkse timer trigger (`CleanupEmailVerwerkingFunction`, zondagochtend 03:00 UTC). Emailinhoud en afzendergegevens worden na 30 dagen geanonimiseerd, na 90 dagen verwijderd. Stored procedure `planner.sp_CleanupEmailVerwerking` is idempotent en inbegrepen in de database-migratie. Het import-script voor `avg.Teambegeleiding` waarschuwt nu als de data ouder is dan 90 dagen.

### Security

- **PII verwijderd uit Azure Function logs** (#210): e-mailadressen, onderwerpregels en emailinhoud worden niet meer gelogd in Azure Function logs / Application Insights. MessageId en VerwerkingId zijn niet-herleidbaar en worden wel gelogd voor troubleshooting. SECURITY.md uitgebreid met lagen 5 (logging-AVG) en 6 (automatische retentie).
- **Gitleaks SQL-uitsluiting verwijderd** (#212): de brede `Database/*.sql` uitsluiting in `.gitleaks.toml` is verwijderd. SQL-bestanden worden nu volledig gescand op secrets en PII. Historisch commit 5311d64 is gedocumenteerd als uitzondering.
- **Stille fallbacks vervangen door InvalidOperationException** (#214): `plannerAfzenderNaam` en `clubName` in `BerichtAiService`, `BerichtResponseGenerator` en `PlannerHtmlGenerator` gooien nu een expliciete fout bij ontbrekende configuratie in `dbo.AppSettings`. Stille fallbacks maskeerden misconfiguratie.

### Changed

- **GETDATE() vervangen door GETUTCDATE()** (#215): alle `mta_inserted`, `mta_modified` en timestamp-kolommen in SQL-tabellen en stored procedures gebruiken nu GETUTCDATE() conform de architectuurregel UTC in DB / `ToLocalTime()` in Blazor.
- **DEFAULT 'VRC' gedocumenteerd als migratie-backwards-compat** (#213): `-- migratie-backwards-compat` commentaar toegevoegd aan alle SQL-tabellen en migraties met `DEFAULT 'VRC'`. C#-inserts geven ClubCode altijd expliciet mee vanuit AppSettings. DEFAULT is nodig voor ALTER TABLE op bestaande rijen.

### Documentation

- **v2-admin-handleiding bijgewerkt** (#156): volledige herschrijving van de verouderde SWA-proxy architectuur naar de actuele Easy Auth + MSAL Bearer token architectuur. Secties toegevoegd over Easy Auth configuratie op de Function App, verplichte 3-user-test, MSAL Bearer token flow, en de valkuil rondom de Blazor WASM roles JSON-array.

### Security (pre-publish)

- **Pre-publish cleanup — PII en servernamen verwijderd uit broncode** (#135): drie categorieën anonimisatie vóór publicatie als open-source project: (1) club-specifieke e-mailadressen in docs en scripts vervangen door generieke plaatshoudernamen; (2) hardcoded Azure SQL servernaam in foutmelding vervangen door generieke tekst (`Azure SQL Server → Database`); (3) setup-scripts documenteren nu generieke defaults zodat andere clubs ze direct kunnen gebruiken.

---

## [2.1.2] — 2026-05-20

**PATCH-release: API-connectie hersteld na login — `net_http_handler_not_assigned` opgelost.**

### Fixed

- **Alle API-aanroepen faalden na inloggen** (#195): dashboard, instellingen en feedbackknop gaven `net_http_handler_not_assigned` terug na een succesvolle login. Oorzaak: `AuthorizationMessageHandler` (die MSAL Bearer tokens toevoegt) is een `DelegatingHandler` en vereist een `InnerHandler` — de transport-laag die het HTTP-verzoek naar de browser fetch API stuurt. Zonder expliciete toewijzing gooit Blazor WASM de fout bij elke API-aanroep. Fix: `handler.InnerHandler = new HttpClientHandler()` in `Program.cs`.

---

## [2.1.1] — 2026-05-20

**PATCH-release: defense-in-depth auth-keten gevalideerd via 3-user-test (admin / user / geen-rol) op productie.**

Zeven samenhangende fixes en verbeteringen sinds v2.1.0 die de Entra ID auth-flow definitief waterdicht maken voor multi-user/multi-rol scenario's. Volledige defense-in-depth architectuur met vijf onafhankelijke lagen (tenant + assignment-required + app roles + frontend role-gate + backend RequireAdmin) gedocumenteerd in `CLAUDE.md` en `docs/AZURE-ENTRA-SETUP.md`. Idempotente PowerShell-scripts in `scripts/` zorgen dat de Entra-config nooit meer handmatig hoeft.

### Added
- **Post-logout redirect naar clubwebsite** (#192): na klikken op 'Uitloggen' belandde de gebruiker op `/authentication/logout-callback` met de MSAL default tekst 'Processing logout callback...' zonder feedback of exit-pad. `Authentication.razor` toont nu een groene-check + 'Je bent uitgelogd' melding, en redirect na 1,5 seconde naar de URL geconfigureerd in `PostLogoutRedirectUrl`. URL via `IConfiguration` uit `appsettings.Production.json` zodat dit per club configureerbaar is — geen hardcoded club-string in code (zie CLAUDE.md). Bij ontbrekende config: alleen de uitgelogd-melding, geen redirect.

### Fixed
- **Blazor WASM: CustomUserFactory voor Entra `roles` JSON-array** (#190): geauthenticeerde users met admin-rol kregen na login toch de NoAccess pagina te zien. Root cause: Blazor's standaard `RemoteUserAccount`-factory cast een JSON-array `"roles": ["admin"]` uit het ID-token naar één claim met de hele JSON-string als value (`'["admin"]'`). `ClaimsPrincipal.IsInRole("admin")` faalt daardoor — de claim value is een string, niet de losse rol-naam. Officieel Microsoft Learn troubleshoot artikel: https://learn.microsoft.com/troubleshoot/entra/entra-id/app-integration/troubleshoot-rabc-issues-webassembly-auth-apps. Fix: `BlazorAdmin/Services/CustomUserFactory.cs` toegevoegd dat de `roles` claim uit `account.AdditionalProperties` uitleest, de bestaande JSON-string claim verwijdert, en voor elk array-element een losse `Claim(roleClaim, value)` toevoegt. Geregistreerd in `Program.cs` via `.AddAccountClaimsPrincipalFactory<CustomUserFactory>()`. CLAUDE.md MSAL-checklist uitgebreid van 10 naar 11 verplichte items.

### Added
- **Azure Entra ID setup scripts + protocol** (#187): twee idempotente PowerShell-scripts in `scripts/` voor verify en configure van de Entra App Registration en Service Principal. `Verify-AzureAuthSetup.ps1` doet een read-only diagnose en print per defense-in-depth laag de actuele state (App Roles, optionalClaims, appRoleAssignmentRequired, admin-assignment). `Configure-EntraApp.ps1` patcht ontbrekende configuratie idempotent (App Roles, optionalClaims voor `roles` in idToken/accessToken, `appRoleAssignmentRequired = true`, admin role-assignment voor jaapadmin) en heeft `-WhatIf` support voor dry-runs. Volledige documentatie in `docs/AZURE-ENTRA-SETUP.md` incl. 3-user-test, bekende valstrikken (cached MSAL token, case-sensitive IsInRole) en snippets om nieuwe users met user/admin rol toe te wijzen. Aanleiding: in productie ontbrak `optionalClaims` voor `roles` in App Registration manifest, waardoor zelfs jaapadmin met admin-role-assignment de role-claim niet in zijn ID-token kreeg en de NoAccess pagina te zien kreeg.

### Security
- **Defense in depth: frontend role-gate toegevoegd** (#185): een Entra-tenant-user zonder admin/user app-rol kon de volledige admin UI-shell (sidebar, navigatie, FEEDBACK-knop) zien. API-calls faalden wel met 401 dankzij `EasyAuthHelper.RequireAdmin()`, maar de UI rendert vóór de eerste API-call — dus de gebruiker zag een werkende admin-app zonder data. Frontend role-check toegevoegd in `App.razor`: zonder `admin` of `user` rol verschijnt nu een `NoAccess` pagina zonder MainLayout. CLAUDE.md documenteert nu expliciet de 5-laagse defense-in-depth security architectuur (tenant + assignment + roles + frontend-gate + backend-gate) en verplicht een 3-user-test (admin / user / geen-rol) bij elke auth-wijziging. Tevens `staticwebapp.config.json` opgeschoond: `Cache-Control: no-cache` voor `index.html` voorkomt dat oude deploys vast blijven plakken in browsercache, en het overbodige `Blazor-Environment` legacy header (.NET 9) is verwijderd.

### Fixed
- **Chrome Incognito kon Blazor app niet laden — SWA serveerde Brotli wasm zonder `Content-Encoding`** (#183): Azure Static Web Apps doet content negotiation op `_framework/*.wasm` requests en serveert de pre-compressed `.wasm.br` data — maar zonder de bijbehorende `Content-Encoding: br` response header. Chrome (vooral Incognito mode zonder cache) interpreteert de Brotli-bytes dan als raw wasm, waarna de SHA-256 integrity check mislukt en de app crasht met "An unhandled error has occurred" op 92% laden. Edge werkte 'toevallig' omdat het een uncompressed versie uit cache had. Fix: `<CompressionEnabled>false</CompressionEnabled>` in `BlazorAdmin.csproj` zodat `dotnet publish` geen `.wasm.br`/`.wasm.gz` bestanden meer genereert. SWA valt dan terug op uncompressed serving of doet zijn eigen dynamische compressie mét correcte `Content-Encoding` header. Trade-off: eerste WASM-download ~3x groter; browser cache maakt latere loads instant.

### Security
- **Blazor login-redirect blokkeerde op 'Verbonden!'** (auth-redirect-loop hotfix): de health-check splash in `App.razor` (1.1s `Phase.Ready` delay + `Phase.Running` transition) voorkwam dat de MSAL auth-check tijdig werd uitgevoerd, waardoor InPrivate gebruikers de "Verbonden!"-melding zagen maar nooit doorgestuurd werden naar de Microsoft login. `App.razor` is herschreven naar een expliciete state-machine waarbij auth-evaluatie de éérste prioriteit is — geen blocking delays meer voor de auth-redirect. Daarnaast is `<script src="_content/Microsoft.Authentication.WebAssembly.Msal/AuthenticationService.js">` expliciet aan `index.html` toegevoegd (Microsoft docs schrijven dit voor) en `MsalProviderOptions.LoginMode = "redirect"` ingesteld zodat MSAL niet eerst probeert een popup te openen (geblokt in admin-context). Resultaat: ongeauthenticeerde bezoekers worden onmiddellijk doorgestuurd naar de Microsoft login zonder enige admin-UI flash.

### Fixed
- **Blazor WASM crasht op SWA — .NET 10 omgevingsnaam** (#171): in .NET 10 is de `Blazor-Environment` HTTP header vervangen door `<WasmApplicationEnvironmentName>` in het `.csproj` bestand. Zonder deze instelling laadde Blazor altijd `appsettings.json` (met localhost URL) in plaats van `appsettings.Production.json`, waardoor MSAL zonder ClientId initialiseerde en de app crashte. Fix: `<WasmApplicationEnvironmentName>Production</WasmApplicationEnvironmentName>` toegevoegd aan `BlazorAdmin.csproj` voor Release-builds. Tevens Easy Auth opnieuw ingeschakeld op de Function App.

---

## [2.1.0] — 2026-05-19

**MINOR-release: Easy Auth bearer-token, Feedback Widget, KNVB-context, geocoding en pipeline-refactor.**

### Fixed
- **TargetFramework FunctionApp terug naar net9.0** (#162): eerste deploy-poging van v2.1.0 faalde omdat `net10.0` niet ondersteund wordt op Azure Functions Linux Consumption plan. Per officiële Microsoft-docs: ".NET 10 apps cannot run on Linux Consumption — use Flex Consumption instead". FunctionApp + FunctionApp.Tests teruggebracht naar `net9.0`; BlazorAdmin blijft op `net10.0` (browser-runtime, geen Azure-restrictie). Migratie naar Flex Consumption + .NET 10 staat als aparte epic op de roadmap (deadline 10 november 2026 — EOL van .NET 9).

### Security
- **Easy Auth op Function App — Bearer token auth** (#100): Admin GUI (Blazor WASM) authenticeert nu direct via Entra ID met MSAL. Bearer tokens worden automatisch meegestuurd naar de Function App, die de tokens valideert via Azure Easy Auth. SWA-proxying van API-calls is losgelaten — SWA dient alleen statische bestanden. Alle `/api/beheer/*`, `/api/test/*` en `/api/feedback/*` endpoints controleren het `X-MS-CLIENT-PRINCIPAL` header (via `EasyAuthHelper`) en vereisen de `admin`-rol. Lokale ontwikkeling werkt zonder auth (`WEBSITE_SITE_NAME` afwezig). CORS geconfigureerd zodat alleen de SWA-origin API-calls mag doen.
- **Security Gate bewaakt nu ook v2/develop PRs** (#135): `security-scan.yml` triggert voortaan ook op pull requests richting `v2/develop`. Eerder was er een blinde vlek waarbij code zonder beveiligingscontrole `v2/develop` in kon via een PR.
- **Blazor Admin GUI: Entra ID auth in productie** (geen issue nr): `Program.cs` detecteert automatisch de omgeving — in productie (SWA-deployment) wordt `EntraAuthService` geregistreerd met MSAL, in development `LocalAuthService`. `appsettings.Production.json` bevat de AzureAd-configuratie (placeholders; in te vullen na Entra-registratie). De SWA CLI-configuratie (`swa-cli.config.json`) maakt lokaal testen van auth-flows mogelijk zonder echte Entra ID.

### Fixed
- **'Doordeweeks' geeft altijd maandag t/m donderdag terug** (#140): bij e-mails als 'kunnen wij volgende week doordeweeks spelen?' retourneerde de AI soms vrijdag of weekenddagen. AI-prompt verduidelijkt ('doordeweeks = ma-do, vrijdag is geen doordeweekse dag') én deterministische code-override in `BerichtPipeline` zorgt dat bij aanwezigheid van 'doordeweeks' altijd exact de vier weekdagen (ma/di/wo/do) van de afgeleide kalenderweek worden gebruikt.
- **TeamRegelDto hardcoded ClubCode verwijderd** (#135): standaard `ClubCode = "VRC"` in `TeamRegelDto` vervangen door `string.Empty` — voorkomt stille multi-club data-isolatie bypass bij ontbrekende ClubCode.

### Removed
- **'Intern domein' instelling verwijderd** (#148): De instelling waarmee e-mails van een heel domein automatisch werden overgeslagen is verwijderd. Beheerders gebruiken voortaan de 'Uitgesloten e-mailadressen' lijst om specifieke adressen uit te sluiten. De veldfiltering op domeinniveau was overbodig geworden nu uitsluitingen per adres worden beheerd.

### Added
- **Automatische GPS-coördinaten via Nominatim** (#139): Beheerders kunnen in de Instellingen-pagina een vrije-tekstveld 'Accommodatieplaats' invullen en op 'Zoek coördinaten' klikken. Het systeem zoekt de coördinaten automatisch op via Nominatim (OpenStreetMap) — handmatig decimale coördinaten invullen is niet meer nodig. Latitude en longitude worden als leestekst getoond en pas opgeslagen wanneer de beheerder op Opslaan klikt.
- **API-ready loading screen** (#137): De Admin GUI toont nu een laadscherm met draaiende spinner totdat de FunctionApp bereikbaar is. Zodra de verbinding is gemaakt verschijnt een groene vinkje-animatie, waarna de app zich opent. Voorkomt "Fetch error" op alle pagina's bij trage FunctionApp-opstart in lokale ontwikkeling.
- **Intelligente Feedback Widget** (#129): Beheerders kunnen rechtsboven in de Admin GUI op **FEEDBACK** klikken om een fout of wens te melden. Na invullen valideert GPT-4o-mini automatisch of de beschrijving volledig genoeg is — als dat zo is gaat de melding direct door, anders verschijnen gericht aanvulvragen (max 3). De uiteindelijke beschrijving wordt door de AI omgezet naar een gestructureerd GitHub issue met acceptatiecriteria, dat Claude Code autonoom kan oppakken.

### Fixed
- **Email-log isolatie** (#119): `planner.EmailVerwerking` had als enige v2-tabel geen `ClubCode` kolom. Beheerders van club A konden daardoor de email-log van club B zien. Kolom toegevoegd; bestaande rijen krijgen standaardwaarde via migratie.

### Added
- **KNVB-verplaatsingsregels in AI-context** (#73): de AI-classificatie controleert automatisch of een herplanverzoek mogelijk een KNVB-regel overtreedt. De regels voor seizoen 2025/'26 (Categorie A, Categorie B, snipperdagen, bekerdeadlines) zijn als context meegegeven aan GPT-4o-mini. Bij een mogelijke overtreding vult de AI het veld `KnvbNotitie` in; dit wordt als let-op-melding in het antwoordbericht opgenomen inclusief link naar de KNVB-website.
- **Teamleider-notificatie bij herplanverzoeken** (#66): wanneer een tegenstander een herplanverzoek instuurt, stuurt het systeem automatisch een korte interne notificatie naar de teamleider/trainer van het betrokken VRC-team. De contactgegevens worden opgezocht in `avg.Teambegeleiding` (geïmporteerd via het bestaande CSV-script). In review mode gaat de notificatie naar de reviewer in plaats van de echte teamleider. Als geen teamleider is gevonden, wordt de notificatie stil overgeslagen. AVG: de e-mail van de teamleider wordt alleen intern gebruikt en nooit naar externe partijen verstuurd.
- **Automatische herstart bij schema-wijziging** (#27): wanneer een beheerder het ophaalschema wijzigt via de Instellingen-pagina, werkt de applicatie de Azure App Setting automatisch bij via de Azure Management API — de Function App herstart zichzelf zonder handmatige actie. CRON-expressies worden gevalideerd vóór opslaan. De Instellingen-pagina toont een leesbare omschrijving van het schema én de eerstvolgende drie uitvoertijden.
- **Automatisch GitHub Issues bij exceptions** (#105, #106): onverwachte exceptions in de timer- en HTTP-triggers worden automatisch gerapporteerd als GitHub Issues. Deduplicatie op fingerprint: bestaand open issue krijgt een comment, nieuw issue wordt aangemaakt met labels `bug` en `type: bug`. Rate-limiting voorkomt dat hetzelfde issue meer dan één keer per 24 uur gerapporteerd wordt. Vereist configuratie van `GitHubPat`, `GitHubOwner` en `GitHubRepo` — als `GitHubPat` niet is ingesteld, wordt alles stil overgeslagen.

### Added
- **Team-schedule endpoint** (#70): `GET /api/planner/team-schedule?team=VRC+JO11-1` geeft een overzicht van alle nog te spelen wedstrijden per team tot seizoenseinde, inclusief een gesorteerde zaterdag-lijst met status `vrij`/`oefenwedstrijd`/`bezet`. Ondersteunt `?format=html` voor een leesbaar HTML-rapport. Onbekend team → 404; ontbrekende parameter → 400.
- **Error fingerprinting** (#104): `SystemUtilities.ComputeFingerprint(Exception)` berekent een deterministische 12-karakter hex fingerprint per exception (type + genormaliseerd bericht + callsite in SportlinkFunction-namespace). Basis voor deduplicatie van GitHub Issues in v2.1.
- **Unit tests voor BerichtPipeline** (#51): xUnit test project `FunctionApp.Tests` toegevoegd aan de solution. 13 tests voor `BerichtPipeline.ValideerDagDatum` — datums in onderwerp, body, dag-naam correctie, prioriteitsregels en randgevallen.

### Changed
- **Kanaal-agnostische BerichtPipeline** (#120): `ValideerDagDatum`, `VerwerkMetPlannerAsync` en `BouwTemplateAntwoord` zijn verplaatst van `EmailProcessorFunction` naar een nieuwe `BerichtPipeline`-klasse in `FunctionApp/Processing/`. De email dry-run tester en de live email-verwerker gebruiken nu dezelfde pipeline-code zonder koppeling via `EmailProcessorFunction`.

---

## [2.0.0] — 2026-05-17

**Grote versie: Blazor WebAssembly Admin GUI + Email verwerkingspipeline**

Volledige nieuwe beheerslaag bovenop de bestaande ETL-pipeline.
Beheerders kunnen nu via een browser de applicatie volledig configureren,
e-mailtemplates beheren en inkomende e-mails laten verwerken door AI.

### Added

#### Admin GUI (Blazor WebAssembly)
- **Dashboard** — synchronisatiestatus, laatste sync-tijdstip en activiteitsoverzicht
- **Instellingen** (`/instellingen`) — alle AppSettings live aanpasbaar zonder deployment; inclusief auditlog van wijzigingen
- **E-mailtemplates** (`/email-templates`) — AI-antwoordtemplates per berichttype beheren; gedeelde voetnoot voor alle uitgaande e-mails
- **Voorkeurstijden** (`/voorkeurstijden`) — per team gewenste speeltijden en dagvoorkeuren instellen (CRUD)
- **Veldbeschikbaarheid** (`/veldbeschikbaarheid`) — per veld beschikbare tijdvensters en zonsondergangslogica configureren
- **Uitgesloten e-mails** — expliciete uitsluitingslijst voor adressen en domeinen
- **E-mail tester** (`/email-tester`) — AI-classificatie dry-run testen zonder e-mail te versturen of op te slaan

#### Admin REST API (`/api/beheer/`)
- `GET / PUT /api/beheer/settings` — instellingen lezen en opslaan (met auditlog)
- `GET /api/beheer/sync/status` — synchronisatiestatus opvragen
- `POST /api/beheer/sync/trigger` — synchronisatie handmatig starten
- `GET / PUT / POST / DELETE /api/beheer/templates` — e-mailtemplates beheren
- `GET / POST / PUT / DELETE /api/beheer/voorkeurstijden` — teamvoorkeurstijden beheren
- `GET / POST / PUT / DELETE /api/beheer/teamregels` — teamspecifieke regels (buffers, veldvoorkeur)
- `GET / POST / PUT / DELETE /api/beheer/uitgesloten-emails` — uitsluitingslijst beheren
- `GET / POST / PUT / DELETE /api/beheer/velden` — veldinformatie beheren
- `GET / POST / PUT / DELETE /api/beheer/veldbeschikbaarheid` — veldbeschikbaarheid beheren
- `GET /api/beheer/email-log` — verwerkte e-mails inzien (AVG-compliant: geen volledige bodies)
- `GET /api/beheer/teams` — teamoverzicht voor dropdowns in GUI

#### E-mailverwerkingspipeline
- **AI-classificatie** via GPT-4o-mini: categoriseert inkomende e-mails als `beschikbaarheid_check`, `herplan_verzoek`, `bevestiging` of `buiten_scope`
- **AI-gegenereerde antwoorden** op basis van configureerbare e-mailtemplates in de database
- **Kanaal-agnostische architectuur** — `BerichtPipeline` ondersteunt e-mail, dry-run en toekomstige kanalen (WhatsApp, etc.)
- **E-mail voetnoot** — beheerbare gedeelde voettekst die automatisch onder alle uitgaande e-mails wordt geplaatst
- **Intern domeinfilter** — e-mails van het eigen clubdomein worden automatisch overgeslagen

#### Nieuwe AppSettings-kolommen
- `Accommodatie` — naam van de sportaccommodatie
- `InternDomein` — e-mails van dit domein worden genegeerd (bijv. `vv-club.nl`)
- `HerplanDeadlineDagen` — minimum aantal dagen vóór wedstrijddatum dat een herplanverzoek nog mag binnenkomen
- `BufferMinuten` — buffer tussen wedstrijden op hetzelfde veld
- `AccommodatieLatitude` / `AccommodatieLongitude` — GPS-coördinaten voor zonsondergangberekening
- `EmailVoetnoot` — gedeelde voettekst voor alle uitgaande e-mails (NVARCHAR MAX)

#### Database
- `dbo.AppSettingsAudit` — append-only auditlog van alle instellingswijzigingen (CISO-eis)
- `dbo.TeamVoorkeurTijden` — teamspecifieke dag- en tijdvoorkeuren
- `dbo.EmailTemplateInstellingen` — beheerbare AI-antwoordtemplates per berichttype
- `dbo.UitgeslotenEmailAdressen` — expliciete uitsluitingslijst voor e-mailadressen/domeinen
- `dbo.TeamRegels` — teamspecifieke regels voor buffers en veldvoorkeur
- `dbo.Velden` — veldinformatie (naam, type, kunstlicht, actief)
- `dbo.VeldBeschikbaarheid` — tijdvensters per veld per dag

#### Ontwikkeltools
- `Start-Debug.ps1` — één commando start Azurite + FunctionApp + BlazorAdmin in aparte vensters
- `Test-App.ps1` — zelfherstellend verificatiescript: schema-validatie, build, API smoke tests en Blazor paginachecks; `-Fix` herstelt schema-drift automatisch

### Changed

- **Multi-club ondersteuning** — alle hardcoded clubnaam-strings vervangen door dynamische `AppSettings.ClubCode`; geen `?? "VRC"` fallback meer; falende instelling gooit `InvalidOperationException`
- **Routeprefix** — van `/api/admin/` naar `/api/beheer/` (consistente Nederlandse naamgeving)
- **BerichtPipeline** — email-specifieke klassen hernoemd naar kanaal-agnostische `Bericht*`-namen
- **.NET 10.0** als target framework (was .NET 9)
- **Microsoft.Graph** bijgewerkt naar 6.0.3
- **Application Insights** volledig verwijderd — niet nodig in huidige architectuur, niet gratis
- **CORS** dead code verwijderd — SWA-proxying maakt aparte CORS-configuratie overbodig

### Fixed

- **AppSettings laden** — `WaitForDatabaseAsync` laadde instellingen niet; alle admin-endpoints hadden een lege settings-cache, waardoor `ClubCode`-opzoeken faalde (alle admin-endpoints gaven 500)
- **`LoadSettingsAsync` incompleet** — query laadde slechts 11 van 18 kolommen; `ClubCode`, `InternDomein`, `HerplanDeadlineDagen`, `BufferMinuten` en `EmailVoetnoot` ontbraken
- **Teams-endpoint (500)** — `his.teams` had geen `ClubCode`-kolom (dynamisch aangemaakt vóór multi-club migratie); kolom toegevoegd en 388 bestaande rijen gevuld vanuit AppSettings
- **E-mail tester dry-run** — gebruikte hardcoded antwoordgenerator in plaats van de echte pipeline; resultaten nu identiek aan live e-mailverwerking
- **Afzendernaam in dry-run** — `AfzenderNaam` was verplicht; nu optioneel zodat de aanhef identiek is aan live
- **UI verbeteringen** — meerdere kleine correcties na eerste live browser-test
- **Blazor detectie Test-App.ps1** — `blazor-error-ui` div staat altijd in de statische index.html (hidden); false positive verwijderd uit schema-check

### Security

- **SWA route mismatch** (#116) — beveiligde routes correct geconfigureerd in `staticwebapp.config.json`
- **ClubCode-isolatie** (#117) — data-isolatie per club gegarandeerd in alle admin-endpoints
- **Foutberichten** (#118) — `ex.Message` niet meer doorgestuurd naar API-responses (potentieel informatie-lek)
- **AppSettings auditlog** — alle instellingswijzigingen gelogd met tijdstip, veld, oude en nieuwe waarde

---

## [1.x] — zie git log vóór 2026-05-16

Versie 1 bestond uit de Sportlink ETL-pipeline (API-sync → SQL), e-mailverwerking (basis) en multi-club fundament.
Zie `git log main` voor de volledige v1-geschiedenis.

---

_Dit changelog wordt bijgehouden door de architect/developer (Claude Code).  
Bij vragen over een specifieke wijziging: zie het bijbehorende GitHub issue of de commit-body._
