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

### Fixed

- Alle API-aanroepen vanuit de Admin GUI faalden met `net_http_handler_not_assigned` na het inloggen. Oorzaak: de `AuthorizationMessageHandler` (MSAL Bearer token) had geen transport-handler toegewezen gekregen. Fix: `InnerHandler` expliciet gezet op `HttpClientHandler`, conform Blazor WASM vereisten. Dashboard, instellingen en feedbackknop werken nu correct.

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
- **Easy Auth op Function App — Bearer token auth** (#100): Admin GUI (Blazor WASM) authenticeert nu direct via Entra ID met MSAL. Bearer tokens worden automatisch meegestuurd naar de Function App, die de tokens valideert via Azure Easy Auth. SWA-proxying van API-calls is losgelaten — SWA dient alleen statische bestanden. Alle `/api/beheer/*`, `/api/test/*` en `/api/feedback/*` endpoints controleren het `X-MS-CLIENT-PRINCIPAL` header (via `EasyAuthHelper`) en vereisen de `admin`-rol. Lokale ontwikkeling werkt zonder auth (`WEBSITE_SITE_NAME` afwezig). CORS geconfigureerd zodat alleen de SWA-origin (`[swa-unique-id].7.azurestaticapps.net`) API-calls mag doen.
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
