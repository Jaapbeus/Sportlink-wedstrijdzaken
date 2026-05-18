# Changelog

Alle noemenswaardige wijzigingen in dit project worden bijgehouden in dit bestand.

De indeling volgt [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).  
Versienummering volgt [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Definities en beslisregels** — wat is een bug, wat is een feature, wat hoort hier wel/niet in:  
> zie [docs/VERSIONING.md](docs/VERSIONING.md).

> **Conventie voor versies:**
> - `MAJOR` (x.0.0) — grote nieuwe laag of breaking change (bijv. Admin GUI, nieuwe auth-laag)
> - `MINOR` (2.x.0) — nieuwe feature, backwards compatible (nieuw endpoint, nieuw scherm, nieuwe instelling)
> - `PATCH` (2.0.x) — bugfix, beveiligingspatch, documentatie zonder gedragswijziging

---

## [Unreleased]

_Wijzigingen op `v2/develop` die nog niet zijn vrijgegeven._

### Security
- **Entra ID tenant en client geconfigureerd** (#132): `staticwebapp.config.json` en `appsettings.Production.json` ingevuld met de echte tenant- en client-ID van de VRC Azure-tenant. SWA runtime bijgewerkt van `dotnet-isolated:9.0` naar `dotnet-isolated:10.0`. Client secret wordt beheerd via Azure SWA Configuration (nooit in git).
- **Security Gate bewaakt nu ook v2/develop PRs** (#135): `security-scan.yml` triggert voortaan ook op pull requests richting `v2/develop`. Eerder was er een blinde vlek waarbij code zonder beveiligingscontrole `v2/develop` in kon via een PR.
- **Blazor Admin GUI: Entra ID auth in productie** (geen issue nr): `Program.cs` detecteert automatisch de omgeving — in productie (SWA-deployment) wordt `EntraAuthService` geregistreerd met MSAL, in development `LocalAuthService`. `appsettings.Production.json` bevat de AzureAd-configuratie (placeholders; in te vullen na Entra-registratie). De SWA CLI-configuratie (`swa-cli.config.json`) maakt lokaal testen van auth-flows mogelijk zonder echte Entra ID.

### Fixed
- **'Doordeweeks' geeft altijd maandag t/m donderdag terug** (#140): bij e-mails als 'kunnen wij volgende week doordeweeks spelen?' retourneerde de AI soms vrijdag of weekenddagen. AI-prompt verduidelijkt ('doordeweeks = ma-do, vrijdag is geen doordeweekse dag') én deterministische code-override in `BerichtPipeline` zorgt dat bij aanwezigheid van 'doordeweeks' altijd exact de vier weekdagen (ma/di/wo/do) van de afgeleide kalenderweek worden gebruikt.
- **TeamRegelDto hardcoded ClubCode verwijderd** (#135): standaard `ClubCode = "VRC"` in `TeamRegelDto` vervangen door `string.Empty` — voorkomt stille multi-club data-isolatie bypass bij ontbrekende ClubCode.

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
