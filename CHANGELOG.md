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

### Added
- **`BevestigWedstrijd`, `ZoekWedstrijd` en `HerplanBevestig` werken nu echt op de Postgres-tier
  (issue 888 vervolg) — drie van de resterende zes 501-stubs zijn vervangen door de echte
  implementatie.** `planner.geplandewedstrijden` miste vier kolommen t.o.v. de SQL Server-tier
  (`wedstrijdduurminuten`, `aangevraagddoor`, `opmerking`, `mta_inserted` — al aangekondigd als
  bewust uitgesteld door een eerdere migratie), plus een UNIQUE-slotconstraint en een FK naar
  `velden`; alle drie zijn nu aanwezig. Twee nieuwe tabellen: `public.zonsondergang` en
  `planner.herplanverzoeken` (beide stonden als gemotiveerde uitzondering in de tabeldekking-guard
  — die uitzondering is nu vervallen voor `herplanverzoeken`, `zonsondergang` blijft er nog in
  staan totdat `PopulateSunset` ook is vertaald). Een kale `ADD CONSTRAINT` bleek niet idempotent
  (Postgres kent geen `IF NOT EXISTS` daarvoor, in tegenstelling tot kolommen/indexen) — opgelost
  met een `DO $$ ... $$`-guard, empirisch bevestigd door de migratie twee keer achter elkaar tegen
  een verse container te draaien. Zeven permanente integratietests tegen een echte
  Postgres-container, inclusief een opzettelijk kapotgemaakte `ClubCode`-parameter die precies één
  test rood laat gaan.
- **Veldbeschikbaarheid en teamregels vertaald naar de Postgres-tier (issue 888 vervolg).**
  `PlannerAvailabilityRepository` (beschikbaarheid periode-aware, #581; wedstrijdbezetting +
  trainingsbezetting) en `TeamRulesRepository` (buffers, voorkeursvelden) — de twee repositories
  die de vertaalde plannerlaag nog volledig miste. `PostgresClubScope` kreeg `AddClubParam` voor
  tabellen met `clubcode NOT NULL`. De risicovolste stap: `planner.alle_wedstrijden_op_veld_ruw`
  levert voor gesynchroniseerde wedstrijden bewust de rúwe Sportlink-veldstring terug (#819), dus
  deze repository resolveert die zelf naar een veldnummer — exact dezelfde matching als de SQL
  Server-tier, en met dezelfde `WHERE veldnummer IS NOT NULL`-uitsluiting voor een niet-
  resolveerbare string. Een duplicaat `Speeltijd`-type op deze tier (identieke vorm als de nu
  gedeelde `Planner.Shared.Speeltijd`) is opgeruimd in plaats van ernaast te blijven bestaan.
  Empirisch geverifieerd tegen een wegwerp-Postgres-container, inclusief een opzettelijk
  onresolveerbare veldstring (valt stil weg, geen crash en geen "veld 0"). Zes permanente
  integratietests; onderscheidend vermogen apart bevestigd — met het resolutiefilter tijdelijk
  gesloopt wordt precies de bijbehorende test rood.

### Changed
- **De planningsmotor (FieldScheduler) en de domeinmodellen waarop hij werkt zijn verhuisd naar
  `Planner.Shared` (issue 888 vervolg) — zuiver intern, geen enkel gedragsverschil.** De motor was
  al aantoonbaar SQL-vrij en delegeerde de veldnaam-matching al aan `Planner.Shared.VeldResolver`
  (#819); alleen de motor zelf stond nog op de SQL Server-tier, getypeerd tegen modellen die de
  Postgres-tier dan had moeten dupliceren. Zelfde precedent als `TeamNaamNormalisatie` (#889):
  logica zonder tier-afhankelijkheid hoort op precies één plek te staan. De HTTP-wire-contracten
  (`CheckAvailabilityRequest`, `AutoPlanResponse`, ...) blijven bewust per tier eigen bestanden.
  Zestig-plus aanroepen in `AvailabilityService`, `RescheduleService`, `AutoPlanService` en
  `SportlinkApiClient` bleven ongewijzigd werken met alleen een `using`-toevoeging. Geverifieerd
  door de volledige bestaande testsuite vóór en ná de verhuizing te draaien in twee losse
  worktrees: exact 429 geslaagd / 5 geskipt / 0 gefaald, beide keren identiek.

### Added
- **De negen niet-vertaalde planner-endpoints op de Postgres-tier geven nu een expliciete 501 in
  plaats van een stille 404 (issue 888 vervolg).** `CheckAvailability`, `DoordeweeksBeschikbaar`,
  `BevestigWedstrijd`, `ZoekWedstrijd`, `HerplanCheck`, `HerplanBevestig`, `PopulateSunset`,
  `AutoPlan` en `AutoPlanToepassen` bestonden nog niet op deze tier — een aanroep verdween
  onzichtbaar in een generieke 404 in plaats van te melden wát er precies ontbreekt. Elke melding
  noemt de daadwerkelijke, geverifieerde afhankelijkheid: twee ontbrekende repositories
  (`PlannerAvailabilityRepository`, `TeamRulesRepository`), twee ontbrekende schematabellen
  (`dbo.Zonsondergang`, `planner.HerplanVerzoeken` — al bekende uitzonderingen in de
  tabeldekking-guard), ontbrekende `PlannerMatchRepository`-methoden, of de nog niet vastgelegde
  architectuurkeuze voor de FieldScheduler-planningsmotor. Twaalf blijvende tests (geen database
  nodig) leggen zowel de statuscode als de exacte inhoud van elke melding vast; statisch
  geverifieerd dat naam, HTTP-werkwoord en route van alle elf planner-endpoints (`Health` loopt
  via een apart bestand) tussen de twee tiers exact overeenkomen.
- **De demo-club AllStars FC heeft nu twee voorbeeld-e-mailsjablonen (#911).** Op een verse
  installatie was het scherm Beheer → Berichtsjablonen leeg, op beide databasevarianten: het scherm
  toont alleen wat in de database staat, en geen van beide installatiescripts vulde die tabel. Wie
  de demo-omgeving bekeek, kon daardoor niet zien hoe een sjabloon eruitziet — en de automatische
  controle kon "er staat terecht niets" niet onderscheiden van "het ophalen van sjablonen is
  stukgelopen". Beide scripts zetten nu dezelfde twee voorbeeldteksten klaar, letterlijk gelijk aan
  wat de knop "Terugzetten naar standaard" oplevert. Woord voor woord identiek op beide varianten
  geverifieerd, en een controle in de bouwstraat bewaakt dat ze blijven staan.
- **Aangepaste e-mailteksten werken nu ook op de Postgres-tier (#889).** De sjablonenlaag was daar
  als laatste onderdeel nog niet vertaald. Een beheerder die via Beheer → Berichtsjablonen een tekst
  aanpaste, zag die wijziging op die tier tot vijf minuten later pas doorkomen; nu is ze meteen
  actief. Elke club krijgt gegarandeerd haar eigen sjabloon, ook wanneer productieclub en democlub
  hetzelfde sjabloonsleutelwoord gebruiken.
- **De Postgres-tier heeft nu een eigen, automatisch meedraaiende testsuite (#890).** Tot nu toe werd
  elke wijziging op die tier geverifieerd met een wegwerp-testopstelling die daarna werd
  weggegooid: dat bewees dat het op dát moment werkte, maar bewaakte daarna niets meer. Er is nu
  een blijvend testproject dat bij elke pull request meedraait tegen een verse database, en dat de
  volledige synchronisatie end-to-end natrekt — inclusief de belangrijkste eigenschap: een tweede
  synchronisatie met dezelfde brongegevens verandert niets en maakt geen dubbele regels aan. Ook de
  e-mailregistratie en de teamlijstopbouw worden zo bewaakt. Vier opzettelijke faalscenario's
  bevestigden dat de suite daadwerkelijk rood wordt wanneer een van die eigenschappen wegvalt.
- **De Postgres-tier bouwt na een synchronisatie zelf zijn canonieke teamlijst op (issue 889).** Tot nu
  toe vulde een sync op die tier wel de ruwe Sportlink-historie, maar bleef de ontdubbelde
  teamlijst leeg — waardoor de teamdropdown in de Admin GUI en alle teamherkenning per e-mail daar
  niets te werken hadden. Sportlink levert elk team in twee schrijfwijzen (`JO10-1` lokaal en
  `[club] O10-1` bondsnotatie); die worden nu tot één team samengevoegd, met beide schrijfwijzen
  als goedgekeurde alias. Een door een coördinator goedgekeurde of handmatig toegevoegde alias
  wordt daarbij nooit stilzwijgend door de sync overschreven. Empirisch geverifieerd tegen een
  wegwerp-Postgres-container: 21 asserties, plus drie opzettelijke faalscenario's die aantonen dat
  de vertaalkeuzes daadwerkelijk het verschil maken.
- **De cross-tree CI-guard vergelijkt nu ook stored procedures en views, het derde en laatste
  stuk van de bomen-vergelijking (issue 864, deel 4).** Kan geen bestandsvergelijking zijn zoals
  tabellen/kolommen: de Postgres-tier heeft geen procedure-/viewbestanden, die logica leeft in C#.
  `scripts/ci/check-postgres-procedure-view-coverage.sh` vergelijkt daarom tegen een expliciete
  mapping naar C#-symbolen en bewaakt dat die mapping niet verweest. Een echte bug tijdens het
  bouwen gevonden: de eerste, ongeankerde regex zag een hernoeming die de oude naam als
  voorvoegsel behield (`EnsureSeasonsAsync` → `EnsureSeasonsAsyncV2`) niet als verweesd — pas met
  woordgrenzen faalde de test zoals bedoeld. Alle drie faalscenario's apart bevestigd.
- **De cross-tree CI-guard kijkt nu ook naar kolommen, niet alleen naar tabellen (issue 864, deel
  3).** Een tabel die op de Postgres-tier wél bestaat maar kolommen mist, bleef tot nu toe
  onopgemerkt tot iemand toevallig functionaliteit vertaalde die de kolom nodig had — dat gebeurde
  binnen deze epic al twee keer. `scripts/ci/check-postgres-column-coverage.sh` vergelijkt 19
  tabellen en 191 kolommen tussen de twee bomen; de zes dynamisch aangemaakte ETL-tabellen worden
  gedekt door een nieuwe test die de échte schemagenerator-output vergelijkt met dezelfde SQL
  Server-schemabestanden. Geen database nodig, draait ook op een fork. Vier opzettelijke
  faalscenario's bevestigden dat beide controles daadwerkelijk rood kunnen worden. Eerste
  bevinding: `his.MatchDetails` heet op de SQL Server-tier `bk_WedstrijdCode` en op de
  Postgres-tier `bk_matchdetails` — een bewuste, nu vastgelegde afwijking.
- **Nieuwe CI-guard vergelijkt de SQL Server- en Postgres-databaseboom op tabelniveau (issue 864,
  deel 2 — de grootste deelopgave die deel 1 openliet).** `scripts/ci/check-postgres-table-coverage.sh`
  faalt zichtbaar zodra een tabel aan de SQL Server-boom wordt toegevoegd zonder Postgres-
  tegenhanger (via migratie, de dynamische ETL-laag, of een expliciete, beredeneerde
  uitzondering) — voorheen kon dat gat onopgemerkt blijven tot iemand de Postgres-tier
  daadwerkelijk gebruikte. Geen database nodig, draait ook op een fork. Empirisch getest met twee
  opzettelijke faalscenario's (een uitzondering verwijderd; een nieuwe, fictieve tabel
  toegevoegd) — beide gaven de verwachte foutmelding.
- **Het teamrooster werkt nu ook op de Postgres-tier** (issue 888): `GET /api/planner/team-schedule`
  toont per zaterdag tot het einde van het seizoen of een team vrij is, met de wedstrijdenlijst
  erbij, en met `?format=html` dezelfde leesbare pagina als op de bestaande tier. Bij de vertaling
  zijn drie verschillen tussen de twee databasemotoren gedicht die elk een team stilzwijgend uit het
  rooster hadden laten vallen: een wedstrijd waarvan de teamnaam met andere hoofdletters binnenkomt,
  een teamnaam met een spatie aan het eind, en een afgelaste wedstrijd die de bron in kleine letters
  aanlevert — die laatste zou een vrije zaterdag ten onrechte als bezet tonen. Alle drie empirisch
  aangetoond op een wegwerpdatabase, samen met 36 asserties tegen de echte API.
- **Zelftest-poorten G5 (juiste engine) en G6 (API-inhoudsasserties) draaien nu tegen een echte
  functiehost** in `scripts/dev/Test-PostgresTier.ps1` (issue 909). De zelftest start
  `FunctionApp.Postgres` zelf op, wacht op `/api/health`, en toetst daarna dertien
  API-endpoints op inhoud in plaats van op statuscode. G5 bevat drie bewijzen die los van
  elkaar staan: de tier-herkomst uit de applicatie, de serverversie die de applicatie meldt
  vergeleken met wat de databasecontainer zélf zegt, en — het enige bewijs dat niet van de
  applicatie komt — de verbinding die de databaseserver in `pg_stat_activity` ziet staan.
  Bovendien controleert G5 dat géén enkele functie in foutstatus staat: de HTTP-endpoints
  blijven namelijk gewoon werken terwijl een andere functie stil onbruikbaar is.
  De poort verstoort een draaiende ontwikkelsessie niet (eigen poort 7098), heeft geen
  `local.settings.json` nodig (alles via omgevingsvariabelen) en ruimt de functiehost, de
  opslagemulator en de wegwerpdatabase ook na een afgebroken run op.
- **Postgres-tier synchronisatiepad: seizoensgrenzen vertaald + een echt gat gedicht (vervolg op
  issue 890).** `public.season` (nieuwe migraties 008/009) vervangt de vaste `30`-weken-fallback:
  zowel de standaardsync als de reset-modus (`?reset=true&season=`, gaf voorheen een expliciete
  501) gebruiken nu de echte seizoenstabel via het nieuwe `PostgresSeasonHelper`.
  `MarkeerVervallenGeplandeWedstrijdenAsync` (teamalias-gebaseerde matching, #700/#820-precedent
  voor de `UPPER(...)`-vergelijkingen) is vertaald naar `FunctionApp.Postgres/Planner/Repositories/
  PlannerMatchRepository.cs` en wordt — net als het SQL Server-origineel — ongeguard aangeroepen
  ná elke sync; dit was voorheen een echt, gedocumenteerd gat (geen gelijkwaardig gedrag), in
  tegenstelling tot de teamcanonicalisatie die bewust nog ontbreekt. Beide empirisch geverifieerd
  tegen een wegwerp-Postgres-16-container, inclusief een niet-matchende controlerij die bewust
  ongemoeid moest blijven. **Bewust niet in deze ronde:** `dbo.DateTable`/`sp_CreateDateTable` (nul
  consumenten binnen de applicatie, zelfde reden als issue 861's al vervallen `pub.*`-views) en het
  jaarlijks automatisch doorrollen van het seizoen (een Postgres-migratie draait één keer, in
  tegenstelling tot de SQL Server-tier se `Script.PostDeployment1.sql` die dit bij elke deploy
  herhaalt) — beide gedocumenteerd in `docs/ARCHITECTUUR-DATABASE-TIERS.md`.
- **Zelftest-poorten G2 (schema), G3 (idempotentie) en G4 (demodata/rijtellingen) zijn nu echte
  metingen in `scripts/dev/Test-PostgresTier.ps1` (#860-acceptatiecriterium, vervolg op #851).**
  G2/G3 herhalen lokaal de CI-job `fresh-db-postgres` (migraties tweemaal draaien,
  `schema_migrations`-telling toetsen). G4 seedt de AllStars-demodata en toetst de rijtellingen
  altijd tegen het contract in `selftest-expectations.psd1` — nooit tegen een levende
  SQL-Server-basismeting, sinds die tijdens het bouwen valse mismatches gaf op rijen die het
  contract zelf al als `Min` (niet `Exact`) classificeert vanwege legitieme opeenhoping door
  handmatig testen. Baseline-runs (SQL Server) leggen nu vast in plaats van te oordelen.
  **Bijkomende bugfix:** `Wait-ForPostgres` kon "gereed" melden vlak vóórdat de server
  daadwerkelijk queries accepteerde (`pg_isready` slaagde, de eerstvolgende échte query gaf
  `FATAL: the database system is starting up`) — nu opgelost met een `SELECT 1`-naverificatie via
  peer-auth. **Bewust niet in deze ronde:** G5/G6 vereisen een daadwerkelijk draaiende
  functiehost (Azurite-afhankelijkheid, geen gecommit `local.settings.json`, eigen
  teardown-verantwoordelijkheid) — een wezenlijk grotere stap, uitgewerkt in het nieuwe issue #909.
- **Schema-drift-guard en veldresolutie-drifttest uitgebreid naar de Postgres-tier, deel 1 (issue
  864).** Drie concrete stukken geleverd: (1) `VeldResolutieDriftTests` bewaakt nu ook
  `Database.Postgres/PostgresPlannerViewGenerator.cs` als vierde plek — als tripwire, niet omdat
  daar vandaag een kopie van de zes-tekens-truncatiebug staat (#819 hield veldresolutie bewust
  C#-side); (2) nieuwe CI-stap `scripts/ci/check-postgres-identifier-casing.sh` bewaakt de
  lowercase-snake_case-conventie (docs/ARCHITECTUUR-DATABASE-TIERS.md §3) binnen de
  migratiebestanden zelf — empirisch bewezen dat hij zowel een PascalCase-kolomnaam als een
  gequote identifier detecteert; (3) de Postgres-CI-job krijgt de tegenhanger van de SQL
  Server-assertie "speeltijden moeten voor de primaire club bestaan, niet alleen voor de
  democlub" (#740), die ontbrak. **Bewust niet in deze ronde:** de SQL-Server-specifieke
  DB-project-vs-PostDeployment-guard is niet naar Postgres uitgebreid — die twee bomen zijn
  structureel verschillend (Postgres' migraties zíjn de uitrol, geen apart ontwerptijd-schema dat
  kan driften; de bestaande `fresh-db-postgres`-idempotentiecontrole dekt het analoge risico al).
  De grootste deelopgave — een controle die de twee bomen onderling vergelijkt op ontbrekende
  tabellen/kolommen — blijft open scope op issue 864.
- **CI-controle die bewijst dat de tier-tabel (`scripts/ci/database-tiers.json`) op de shell-kant
  (`resolve-database-tier.sh`) en de PowerShell-kant (`Get-DatabaseTierProject`) exact dezelfde
  uitkomst geeft (#865).** Laatste openstaande acceptatiecriterium van #816/#865: zonder deze test
  konden de twee lezers van de tabel stilzwijgend uit elkaar drijven. Nieuw script
  `scripts/ci/Test-TierMappingConsistency.ps1` toetst elke tier uit de tabel plus een bewust
  onbekende naam; draait als stap in `Build FunctionApp + BlazorAdmin` (geen database, geen
  secrets, ook bruikbaar op een fork). Detecteert en compenseert automatisch twee WSL-
  eigenaardigheden die tijdens het bouwen aan het licht kwamen (relevant voor lokale Windows-
  runs waar `bash` naar de WSL-launcher wijst i.p.v. Git Bash): WSL forwardt Windows-
  omgevingsvariabelen alleen via `WSLENV`, en vertaalt een los Windows-pad niet automatisch naar
  `/mnt/c/...`. Empirisch bewezen dat de test daadwerkelijk drift detecteert: een tijdelijk
  geïnjecteerd verschil tussen beide kanten liet de test falen; na herstel weer groen.
- **Synchronisatie- en stagingpad vertaald naar de Postgres-tier: `GET /api/postgres/sync-matches`,
  de timertrigger, en `AdminSyncFunction.Trigger` (issue 890).** `PostgresSyncPipeline` orkestreert
  fetch → `stg.*` → `his.*` met `PostgresMergeOrchestrator` (#818) voor het schema-/mergewerk; de
  drie SQL-Server-specifieke `IF EXISTS ... ELSE IF ...`-staging-guards (programma-dedup,
  uitslagen-upsert-of-alleen-invoegen-als-niet-toekomstig) zijn vertaald naar expliciete
  `SELECT`/`UPDATE`/`INSERT`-stappen in C#, met de datumvergelijking als ordinale stringvergelijking
  i.p.v. `CONVERT(..., 127)`. `Team`/`Match`/`MatchDetails`-modellen gedupliceerd in
  `FunctionApp.Postgres/Sync/SportlinkModels.cs` (aparte implementatieboom, geen referentie naar
  `FunctionApp`). Empirisch geverifieerd tegen een wegwerp-Postgres-container met de bestaande,
  tier-onafhankelijke `SportlinkFixtureServer` (#867): teams/programma/uitslagen/matchdetails komen
  correct in `his.*` terecht, de uitslagen-verrijking overschrijft de programma-rij zoals verwacht,
  en een tweede identieke run blijft idempotent (geen dubbele rijen). **Bewust niet in deze ronde,
  gedocumenteerd als tijdelijk gat:** seizoensgrenzen (`dbo.Season` is niet geport — de standaard-
  synchronisatie gebruikt dezelfde vaste fallback die de SQL Server-tier al gebruikt zodra
  `dbo.Season` onbereikbaar is; de reset-modus met een specifiek seizoen geeft een expliciete 501),
  teamcanonicalisatie (al best-effort in het origineel) en
  `MarkeerVervallenGeplandeWedstrijdenAsync` (in het origineel juist ongeguard — hier ontbreekt dus
  écht gedrag, geen equivalent).
- **E-mailpersistentie en teamresolutie vertaald naar de Postgres-tier (issue 889).**
  `SqlEmailPersistenceRepository` (audit-trail/dedup, 15 methoden), `LearningMomentRepository`
  en `TeamCandidateRepository`/`TeamAliasLearningService`. `SCOPE_IDENTITY()` → `RETURNING id`,
  unique-violation-detectie → `PostgresErrorCodes.UniqueViolation`, de alias-upsert → `INSERT …
  ON CONFLICT DO UPDATE`. **Architectuurrefactor:** `TeamNaamNormalisatie.cs` verhuisd naar
  `Planner.Shared/` (in tegenstelling tot #888's `LeeftijdNormalisatie`, die als tijdelijke schuld
  gedupliceerd bleef) — CLAUDE.md's harde regel "normalisatie hoort op precies één plek" liet geen
  tweede kopie toe. Negen SQL Server-tier-bestanden aangepast, volledige testsuite (431 tests) en de
  verhuisde normalisatietests (59 tests, nu in `Planner.Shared.Tests`) slagen ongewijzigd. Empirisch
  geverifieerd tegen een wegwerp-Postgres-container: dedup-exceptie, reply-detectie,
  classificatiecorrectie, en het expliciete acceptatiecriterium van dit issue — een geleerde
  teamalias telt pas mee ná handmatige validatie. Bewust niet in deze ronde:
  `TeamCanonicalisatieService` (AI-disambiguatie-orkestratie) en de e-mail-AI-pijplijn
  (`BerichtAiService`/`EmailProcessorFunction`/`EmailGraphService`, >2700 regels zonder directe
  SQL-toegang, al buiten dit issue's eigen scope).
- **Eerste planner-endpoint vertaald naar de Postgres-tier: `GET /api/planner/veldbezetting`
  (issue 888).** Bewijst de twee valkuilen die het issue zelf noemt: `OUTER APPLY` → 
  `LEFT JOIN LATERAL … ON TRUE` (tweede plek na #819's plannerview) en
  `LeeftijdNormalisatie.SqlExpr` → `PostgresLeeftijdNormalisatie.SqlExpr` (`+`→`||`,
  `LIKE`→`ILIKE` waar SQL Server's collatie al hoofdletterongevoelig was). Empirisch
  geverifieerd tegen een wegwerp-Postgres-container: zowel het ALLSTARS-democlubpad
  (teamnaam-extractie) als een "echte" primaire-clubpad (`his.teams`-LATERAL-JOIN +
  leeftijdsnormalisatie) leveren de verwachte leeftijdscategorie en wedstrijdduur op. De overige elf
  planner-endpoints (auto-plan/FieldScheduler-engine, herplannen, teamrooster, beschikbaarheid) zijn
  aanzienlijk groter (samen >1600 regels bedrijfslogica) en blijven open scope op issue 888.
- **Alle zestien beheer-endpointparen vertaald naar de Postgres-tier (#887), plus de gedeelde
  admin-infrastructuur die ze allemaal gebruiken.** `FunctionApp.Postgres/Admin/EasyAuthHelper.cs`
  en `AdminEndpoint.cs` (bewuste kopieën van hun SQL Server-tegenhangers — geen gedeelde
  abstractie), `PostgresAppSettings` en `PostgresSystemUtilities.WaitForDatabaseAsync`. Vertaald:
  Clubs, Speeltijden, VeldPeriode, VeldBeschikbaarheid (incl. de Velden-CRUD die daarin meelift),
  VeldTraining, VoorkeurTijden/TeamRegels, UitgeslotenEmail, TeamAliassen, Templates, Teams,
  Settings (incl. geocode), Theme, Sync (status), EmailLog, Leermomenten, Teambegeleiding
  (GetTeams/GetBegeleiders/Import) — elk empirisch geverifieerd met een volledige CRUD-cyclus tegen
  een wegwerp-Postgres-container, niet alleen `dotnet build`. Vier nieuwe migraties
  (`003_admin_tables.sql` t/m `005_appsettings_theme_assets.sql`) leveren de ontbrekende tabellen en
  kolommen (Teams, TeamAliassen, TeamRegels, TeamVoorkeurTijden, UitgeslotenEmailAdressen,
  VeldPeriode, VeldBeschikbaarheid, VeldTraining, EmailTemplateInstellingen, AppSettingsAudit,
  `planner.EmailVerwerking`, `planner.ClassificatieCorrectie`, `appsettings.faviconurl/logourl`).
  Twee genuine Postgres-vs-SQL-Server-verschillen empirisch aangetroffen en gefixt: impliciete
  tekst→numeriek-conversie bestaat niet in Postgres (dynamische AppSettings-UPDATE kreeg een
  expliciete `::type`-cast per veld), en Npgsql weigert een `DateTime` met `Kind=Unspecified` voor
  een `TIMESTAMPTZ`-parameter (EmailLog-datumfilters kregen `DateTime.SpecifyKind(…, Utc)`).
  `AdminSyncFunction.Trigger` en `AdminTeambegeleidingFunction.Doorsturen` zijn bewuste 501-stubs
  die op resp. issue 890 (ETL-pipeline) en issue 889 (e-mailverzend-/teamresolutielaag) wachten —
  geen gemiste scope, een expliciet gedocumenteerde afhankelijkheid op nog niet gestart werk.
  Tijdens de eerste vertaling ook ontdekt: `public.speeltijden` miste drie kolommen ten opzichte van
  de SQL Server-tier — apart getrackt en gefixt, zie issue 893.
- **AllStars-demodata voor de Postgres-tier, deel 1 van issue 862: hetzelfde rijcontract als de
  SQL Server-tier.** `Database.Postgres/migrations/006_allstars_demodata.sql` (velden=3,
  veldbeschikbaarheid=21, speeltijden gekopieerd van de primaire club, teamregels=1 — automatisch
  toegepast, deze tabellen bestaan altijd) en
  `scripts/migrations/003-seed-allstars-demo-matches-postgres.sql` (teams=28, teambegeleiding=28,
  wedstrijden=224 — los, expliciet aan te roepen script, want `his.teams`/`his.matches` bestaan pas
  na de eerste Postgres-Sportlink-sync, dezelfde les als issue 856). Een teamregels-demorij voor de
  democlub bestond nog niet op geen van beide tiers — toegevoegd aan beide. Empirisch geverifieerd
  tegen een wegwerp-Postgres-container (inclusief de his.teams/his.matches-simulatie via de
  letterlijke PostgresSchemaGenerator-output): exact dezelfde zeven aantallen als het SQL
  Server-contract, idempotent, en beide faalpaden (democlub ontbreekt; sync nog niet gelopen) geven
  een duidelijke foutmelding. Deel 2 (circa elf tabellen zonder demodata op beide tiers, plus een
  dekkingscontrole per GUI-route) is bewust niet in deze ronde meegenomen — blijft open op issue 862.
- **Vier AVG-opschoonprocedures vertaald naar de Postgres-tier (issue 861).**
  `Database.Postgres/PostgresCleanupProcedures.cs` (C#-methoden, zelfde architectuurkeuze als #818's
  `PostgresMergeOrchestrator`) plus twee nieuwe timer-triggered functies in
  `FunctionApp.Postgres/Email/` (`CleanupEmailVerwerkingFunction`, `CleanupTeambegeleidingFunction`)
  met dezelfde CRON-schema's als de SQL Server-tier. Dekt `planner.emailverwerking`,
  `planner.classificatiecorrectie`, `avg.teambegeleiding` en `avg.importlog` — elk met de
  twee-fase-anonimiseer-dan-verwijder-logica van het origineel, inclusief de FK-opruimvolgorde
  (#424) die een jonge correctierij verwijdert zodra hij naar een oude, te verwijderen e-mailrij
  verwijst. Empirisch geverifieerd tegen een wegwerp-Postgres-container met vijf voorbereide rijen
  op uiteenlopende leeftijden (5/45/100/120/10 dagen): exact de verwachte anonimisering/verwijdering
  per venster, inclusief het lastige FK-scenario (jonge correctierij, oude ouderrij). Bewust niet in
  deze ronde: `sp_CreateDateTable`/`sp_UpdateSeasonTable` (wachten op issue 890, geen Postgres-
  migratie voor `dbo.Season`/`dbo.DateTable` nog) en de drie `pub.*`-rapportageviews (expliciet
  laten vervallen — nul consumenten in de broncode, conform de optie die het issue zelf aanbiedt).
  Losstaande, ongefixte bevinding gemeld voor #888: `PostgresPlannerViewGenerator.CreateView` (#819)
  wordt vandaag alleen door tests uitgevoerd, niet door een migratie of applicatiecode.
- **`FunctionApp.Postgres` — de applicatie-datalaag voor de Postgres-tier bestaat nu daadwerkelijk**,
  na epic #815's grootste ontbrekende stuk (#860, uitgewerkt naar vijf sub-issues). Een minimaal,
  zelfstandig Azure Functions isolated-worker-project (net9.0) met een eigen configuratielaag
  (`PostgresDatabaseConfig`, connectiestring via `POSTGRES_CONNECTION_STRING` — dezelfde naam als
  het bestaande migratiepad) en een `/api/health` in hetzelfde format als de SQL Server-tier
  (`tier="Postgres"`, `provider="Npgsql"`, `serverVersion` via `SHOW server_version`).
  `scripts/ci/database-tiers.json`'s Postgres-rij staat nu op `"built": true` — de tier-resolver
  levert voortaan een geldig projectpad op. Empirisch bevestigd: `func start` tegen een
  wegwerp-Postgres-container levert een werkende `/api/health` op met de echte, live opgehaalde
  serverversie. De feitelijke functionaliteit (beheer-, planner-, e-mail- en synchronisatiepaden)
  volgt in vier vervolgissues (#887-#890) — dit levert uitsluitend de projectopzet zelf. (#891)
- **`/api/health` toont nu aantoonbaar met welke databasetier en -driver het draait, en het
  daadwerkelijke versienummer van de databaseserver.** Drie nieuwe, puur additieve velden: `tier`
  en `provider` komen uit build-time assembly-metadata (nooit een runtime-gok), dus ook gevuld
  wanneer de database onbereikbaar is; `serverVersion` (`SERVERPROPERTY('ProductVersion')`) komt
  aantoonbaar uit de database zelf en is alleen gevuld als die online is. Verbindingen van de
  applicatie zijn voortaan ook herkenbaar aan een `ApplicationName` op de connection string — een
  onafhankelijke bevestiging naast wat de applicatie over zichzelf zegt. Bestaand gedrag van de
  statusbewaking in de Admin GUI is ongewijzigd (leest alleen `status`/`database`, negeert onbekende
  velden). `docs/API.md` en `docs/api-standaarden/openapi.yaml`/`.json` bijgewerkt. (#863)
- **De Postgres-tier-integratietests draaien voortaan automatisch in CI in plaats van
  onvoorwaardelijk uitgeschakeld te staan.** `PostgresFactAttribute`/`PostgresTheoryAttribute`
  (`Database.Postgres.Tests/PostgresIntegrationTestAttributes.cs`) vervangen de losse
  `[Fact(Skip="...")]`/`[Theory(Skip="...")]` op alle 22 integratietests: zichtbaar overgeslagen
  (met reden in de testuitvoer) zonder `POSTGRES_TEST_CONNECTION_STRING`, onveranderd draaiend
  zodra die gezet is — zonder enige codewijziging. De CI-job "PostDeployment op verse
  Postgres-database" zet die variabele nu en draait de volledige testsuite na de migratiestap,
  tegen de instantie die de job zelf al opzet. `docs/VERIFICATIE-SCRIPTS.md` beschrijft hoe je
  dezelfde tests lokaal tegen een eigen wegwerpcontainer draait. (#866)
- **Eén centrale schakelaar (`EgressGuard`) die alle uitgaande integraties blokkeert buiten
  productie** — de Sportlink-synchronisatie (timer-trigger), GitHub-issue-rapportage, e-mail via
  Microsoft Graph en de AI-diensten. Herkent productie aan `WEBSITE_SITE_NAME` (dezelfde signaal
  die `EasyAuthHelper` al gebruikt); lokaal en in CI blijven deze integraties altijd geblokkeerd,
  ook als een secret (`OpenAiApiKey`, `GitHubPat`, Graph-credentials) toevallig wél geconfigureerd
  is. Vervangt vier losse, impliciete "is dit geconfigureerd?"-controles door één expliciete poort.
  Tegelijk gefixt: de bedoelde per-club synchronisatie-rem (`SyncEnabled`-instelling) bleek dode
  code — `syncEnabled` werd nooit in de instellingencache gezet, dus de vergelijking met `"0"` in
  `Function1.cs` kon nooit waar zijn. `docs/DEVELOPER-SETUP.md` §5.1 beschrijft hoe je lokaal werkt
  zonder externe diensten te raken, inclusief de expliciete opt-in voor een bewuste, eenmalige
  handmatige test. (#857)
- **Een lokale fixtureserver voor de Sportlink-synchronisatie, plus een end-to-end-test die het
  volledige synchronisatiepad bewijst zonder de echte Sportlink-API te raken.**
  `SportlinkFixtureServer` (`FunctionApp.Tests/Sync/`) is een minimale, wegwerpbare HTTP-server met
  opgenomen antwoorden in het echte gegevensformaat (velden en datumnotaties uit
  FunctionApp/CLAUDE.md's Sportlink-API-referentie, niet uit de demodata — die twee verschillen
  net op het punt waar database-engines ook verschillen: datuminterpretatie). De nieuwe test
  (`SportlinkFixtureSyncIntegrationTests`) draait `SportlinkSyncPipeline.RunSyncAsync` tweemaal
  tegen die fixture en bewijst zo zowel dat het volledige pad werkt als dat een tweede run met
  identieke brondata geen duplicaten oplevert en `mta_modified` niet bijwerkt op ongewijzigde rijen.
  Bijkomend gefixt tijdens het bouwen hiervan: `SystemUtilities.AppSettings`' procesbrede statische
  instellingencache had geen manier om in een test te vullen zonder een echte databaseoproep, en
  geen manier om af te sluiten — nu via `SetForTests`/`ResetForTests` (test-only, internal).
  Testparallellisme staat voortaan projectbreed uit voor `FunctionApp.Tests` (net als eerder al voor
  `Database.Postgres.Tests`), want die cache is gedeeld met andere testklassen. `docs/DEVELOPER-SETUP.md`
  beschrijft hoe je dit lokaal draait. **Bewust nog niet gedekt:** de Postgres-tier heeft nog geen
  eigen synchronisatiepad om tegen te testen (#860); wiring in CI is de scope van #866. Zie issue 867.
- **Een zelftest die bewijst dat een databasetier daadwerkelijk werkt, in plaats van dat de
  onderdelen compileren.** `scripts/dev/Test-PostgresTier.ps1` zet een wegwerpdatabase op, rolt het
  schema uit, laadt de demodata, en controleert per stap of het resultaat klopt — met exacte
  rijaantallen, niet met "er ging niets mis". De bijbehorende skill opent daarna alle
  beheerpagina's in een echte browser en controleert per pagina of de demogegevens ook echt
  zichtbaar zijn. Drie ontwerpkeuzes maken het verschil met de bestaande controles: een stap die
  niet uitgevoerd kon worden geldt als mislukt (niet als overgeslagen), een pagina zonder
  demogegevens krijgt de status "niet getest" (niet "goed"), en de lijst met te controleren
  pagina's wordt bij elke run vergeleken met de werkelijke pagina's in de broncode — zodat een
  controle op een verdwenen pagina niet jarenlang groen kan blijven staan, wat nu wél gebeurt.
  De zelftest is nog niet compleet: de stappen die een draaiende applicatie op de nieuwe tier
  vereisen staan bewust op 'geblokkeerd' tot die er is. Zie issue #851.
- `docker-compose.selftest.yml` — een aparte wegwerpdatabase voor die zelftest, met een eigen
  poort en zonder opslagvolume, zodat de ontwikkeldatabase er niet door geraakt kan worden. Hij
  draait bewust op Nederlandse tijd en niet op UTC: een tijdzonefout in de tijdstempels is op een
  UTC-server namelijk onzichtbaar. Zie issue #851.

### Fixed
- **Een herplanverzoek indienen sloeg altijd stuk op de SQL Server-tier (issue 888 vervolg,
  gevonden tijdens de Postgres-vertaling).** `PlannerMatchRepository.SaveHerplanVerzoekAsync` miste
  `ClubCode` volledig in de INSERT, terwijl `[planner].[HerplanVerzoeken].[ClubCode]` `NOT NULL` is
  zonder `DEFAULT` — elke aanroep van `POST /api/planner/herplan-bevestig` gooide een SQL-fout in
  plaats van een herplanverzoek op te slaan. Niet zichtbaar in de bestaande testsuite, die dit
  pad niet dekte. `clubCode` is nu een optionele parameter door de hele aanroepketen, met dezelfde
  `ClubScope.Resolve`-terugval naar de primaire club als de rest van deze repository.
- **De zelftest stond stil op rood en de plannerlaag was volledig ongedekt (issue 888 vervolg).**
  Drie dingen. (1) Poort G6 van de zelftest was daadwerkelijk rood: het endpoint voor
  e-mailsjablonen was aan de verwachtingenlijst toegevoegd zonder bijbehorende controle, en het
  script rekent dat — terecht — als een fout in plaats van als iets om over te slaan. Nu voorzien
  van een echte inhoudscontrole. (2) De twee plannerpagina's van de nieuwe databasetier stonden
  in het geheel niet in de verwachtingenlijst en werden dus door niets bewaakt; de veldbezetting
  wordt nu per run gecontroleerd op een dag waarop er aantoonbaar demowedstrijden staan (de datum
  wordt berekend, niet vastgelegd, anders verloopt hij binnen een week). (3) De plannercode van de
  nieuwe tier had geen enkele blijvende test; het enige schrijfpad erin is nu vastgelegd in zes
  tests, inclusief de regel dat een geleerde teamnaam pas meetelt ná goedkeuring. Dat die tests
  werkelijk iets bewaken is apart gemeten: met die regel tijdelijk uit de code gesloopt wordt
  precies de bijbehorende test rood. Tijdens dit werk bleek de teamroosterpagina in de zelftest
  principieel nog niet te controleren — vastgelegd als issue #931 en zichtbaar geblokkeerd in
  plaats van weggelaten.
- **AVG-bewaartermijn: de instellingen-audittrail werd op de Postgres-tier nooit opgeschoond
  (issue 861 vervolg).** `public.appsettingsaudit` legt bij elke instellingswijziging vast wie hem
  doorvoerde, en oude/nieuwe waarden kunnen e-mailadressen bevatten — beide persoonsgegevens. De
  tabel én de bewaartermijn-instelling bestonden al op deze tier, maar er was niets dat er ooit
  naar handelde: rijen bleven onbeperkt staan, in strijd met AVG art. 5 lid 1 sub e
  (opslagbeperking). De migratie die de tabel aanmaakte benoemde dit gat zelf al als openstaand
  werk. Nu voorzien van dezelfde maandelijkse opschoontaak als de SQL Server-tier, inclusief de
  drietraps-terugval voor de bewaartermijn (primaire club → willekeurige club → 730 dagen). Een
  onzinnige instelling (0 of negatief) valt bewust terug op de default in plaats van alles te
  verwijderen. Vastgelegd in zes blijvende integratietests; die zijn aantoonbaar
  onderscheidend — met de terugvalbescherming er tijdelijk uit gesloopt worden er twee rood.
- **De automatische controles voor de Postgres-tier deelden één database en zaten elkaar in de weg (#925).**
  Eén testverzameling breekt de database met opzet af om te controleren of ze correct opnieuw wordt
  opgebouwd; alles wat daarna draaide, kreeg een halve database te zien. Dat leverde deze week twee
  keer een foutmelding op die niets met de eigenlijke wijziging te maken had. Elke verzameling
  krijgt nu een eigen database, en een extra controle bewaakt dat de ene de andere niet meer kan
  raken. Die controle is aantoonbaar: teruggezet naar de oude opzet slaat hij aan.
- **Op de Postgres-tier zou de synchronisatie op termijn stilvallen.** Het seizoensoverzicht waaruit
  de synchronisatie afleidt hoe ver vooruit ze wedstrijden ophaalt, werd daar alleen bij de
  installatie gevuld en daarna nooit meer aangevuld — anders dan op de bestaande tier, waar dat bij
  elke uitrol opnieuw gebeurt. Een installatie die lang genoeg meedraait raakte zo door haar
  seizoenen heen, waarna het ophaalvenster in het verleden kwam te liggen en er niets meer werd
  opgehaald. De synchronisatie vult het overzicht nu zelf aan, vanaf twee maanden vóór de start van
  een nieuw seizoen — precies zoals de bestaande tier het doet. Zowel de situatie vóór als na de fix
  is gemeten, en een opzettelijk faalscenario bevestigde dat de controle het verschil ziet. Zie
  issue 861.
- **Een testcontrole voor de Postgres-tier kon willekeurig omvallen.** Drie van de tests uit de
  nieuwe Postgres-testsuite slaagden alleen als ze in een bepaalde volgorde draaiden, en die
  volgorde ligt niet vast. Gemeten over vier verse databases: twee keer groen, twee keer rood met
  exact dezelfde code. Opgelost; vijf achtereenvolgende metingen zijn nu groen. Zie issue #890.
- **E-mailtemplate opslaan kon een onvolledig audit-spoor achterlaten, op beide databasetiers
  (#916).** `AdminTemplatesFunction.Put` deed de template-upsert en de auditlog-insert als twee
  losse, niet-getransactioneerde statements — een fout tussen de twee liet de templatewijziging
  wél doorgevoerd zien terwijl er geen auditrij bijkwam. Geen dataverlies (in tegenstelling tot
  #913), wel een onvolledige audittrail voor een wijziging die wél had plaatsgevonden. Beide
  tiers wrappen dit nu in één transactie, hetzelfde patroon dat `AdminSettingsFunction.Put` al
  correct toepaste. Empirisch geverifieerd op beide tiers (wegwerp-`postgres:16`-container en een
  wegwerpdatabase op de lokale SQL-Server-2022-container): een opzettelijk geforceerde
  constraintfout in de audit-insert laat de eerder geslaagde templatewijziging nu volledig
  terugdraaien in plaats van achter te blijven — per tier bevestigd met drie scenario's (bug
  reproduceerbaar zónder transactie, teruggedraaid mét transactie, happy path voert beide rijen door).
- **De synchronisatietimer van de Postgres-tier startte nooit bij wie het configuratiesjabloon
  volgde.** `FunctionApp.Postgres/local.settings.template.json` miste `FETCH_SCHEDULE`, waardoor
  de functiehost wel opkwam maar `PostgresFetchAndStoreApiData` permanent in foutstatus stond —
  zichtbaar in het opstartlog, onzichtbaar voor wie alleen de HTTP-endpoints uitprobeerde.
  Gevonden doordat de nieuwe zelftest-poort G5 op indexeringsfouten controleert (issue 909).
- **Postgres-tier: teambegeleiding-import kon de club zonder data achterlaten bij een fout
  halverwege (#913).** `AdminTeambegeleidingFunction.Import` deed de delete, de per-rij-insert
  en de auditlog-insert als drie losse, niet-getransactioneerde stappen — een crash tussen de
  delete en de insert-lus liet de club zonder teambegeleidingsdata achter, terwijl
  `Database.Postgres/TeambegeleidingImporter` (issue 824) precies deze atomiciteit al gebouwd,
  gereviewd en getest had, maar hier nooit werd aangeroepen. Het endpoint delegeert nu naar die
  bestaande implementatie in plaats van de databaselaag opnieuw te bouwen. Empirisch geverifieerd
  tegen een wegwerp-Postgres-container: een tweede import die halverwege faalt (een te lange
  teamnaam) laat de data van de voorgaande, geslaagde import nu volledig intact — vóór deze fix zou
  de delete al zijn doorgevoerd.
- **Postgres-tier: teamherkenning kon stilzwijgend falen bij afwijkende hoofdlettergebruik, en
  stond een casing-only-duplicaatteam toe waar de SQL Server-tier dat al weigerde (issue 820,
  vervolgronde op #869).** Postgres' default-collatie is case-sensitief; de bij #889 gebouwde
  `TeamCandidateRepository`/`TeamAliasLearningService` vergeleken `teamnaamgenormaliseerd`/
  `ruwetekst`/`ruwetekstgenormaliseerd` nog kaal, en `public.teams`/`public.teamaliassen` (#887)
  hadden nog gewone `UNIQUE`-constraints. Nieuwe migratie
  `Database.Postgres/migrations/007_teams_collation_fix.sql` vervangt die door expression-based
  unique indexes op `upper(...)`; de repository-laag vergelijkt nu net als de SQL Server-tier
  (#869) expliciet via `UPPER(...)`, inclusief `ON CONFLICT (clubcode, upper(ruwetekst))`.
  Empirisch geverifieerd tegen een wegwerp-Postgres-container: een casing-only-duplicaat wordt
  geweigerd, teamlookup slaagt ondanks afwijkende opgeslagen casing, en een herhaalde alias-melding
  met andere casing verhoogt de teller in plaats van te dupliceren. **Blijft open:** de audit/replay
  tegen echte historische productiedata vereist toegang tot echte clubdata en is daarom niet
  autonoom uitgevoerd — zie issue #820.

### Changed
- De vertaling van de gekozen databasetier naar het bijbehorende project staat nu in één
  gegevensbestand (`scripts/ci/database-tiers.json`) in plaats van in het CI-script. De
  deploy-pijplijn en de lokale scripts lezen daardoor dezelfde bron — anders zou de mapping op
  twee plekken bijgehouden moeten worden, precies wat bij het opzetten van het tier-mechanisme
  voorkomen moest worden. Gedrag is ongewijzigd: een ontbrekende of onbekende waarde faalt nog
  steeds hard. Nieuw is dat een geldige tier waarvan het project nog niet bestaat een eigen
  foutcode geeft, zodat een verificatiescript dat kan onderscheiden van een echte fout. (#865)
- **Audit-log entries in `dbo.AppSettingsAudit` ouder dan de bewaartermijn worden nu automatisch
  opgeruimd (AVG-bewaartermijn).** Elke instellingenwijziging in Beheer → Instellingen werd tot nu
  toe voor onbepaalde tijd bewaard, inclusief de naam van de beheerder en eventuele
  e-mailadressen in de gewijzigde waarde. Een nieuwe maandelijkse achtergrondtaak verwijdert
  rijen ouder dan de ingestelde bewaartermijn (standaard 730 dagen / 24 maanden — instelbaar via
  `dbo.AppSettings.AppSettingsAuditBewaarDagen`, zonder dat een nieuwe versie nodig is). (#781)
- **De lokale ontwikkelomgeving draait nu ook op macOS (Apple Silicon), naast Windows.** Alle
  dev-scripts (`Start-Debug.ps1`, `Stop-Debug.ps1`, `Test-App.ps1`, `Bump-Build.ps1`,
  `smoke-test.ps1`) werken op beide platforms. Concreet: poortdetectie loopt via een
  cross-platform .NET-API in plaats van `Get-NetTCPConnection`, de procesboom-teardown gebruikt
  op macOS `ps` in plaats van WMI, de tijdelijke map wordt platform-onafhankelijk bepaald
  (`$env:TEMP` bestaat niet op macOS), en alle padverwijzingen gebruiken forward slashes — een
  backslash is op macOS namelijk een geldig teken ín een bestandsnaam en geen scheidingsteken.
  Op macOS schrijft `Start-Debug.ps1` de output van elke service naar een logbestand, omdat
  daar geen apart consolevenster geopend kan worden. Windows-gedrag is ongewijzigd. (#800)
- `sportlink-wedstrijdzaken.slnf` — een solution filter met de drie .NET-projecten. Nodig omdat
  het databaseproject een verouderd Visual Studio-formaat heeft dat buiten Windows niet te
  bouwen is; `dotnet build sportlink-wedstrijdzaken.slnf` werkt wél op beide platforms. (#800)
- `.gitattributes` — legt vast dat shell-scripts en git-hooks LF-regeleindes houden, zodat een
  commit vanaf Windows de hooks op macOS niet onbruikbaar kan maken. (#800)
- `docker-compose.yml` — de lokale ontwikkeldatabase draait nu op **beide** platforms als
  SQL Server 2022 in een container (`docker compose up -d`). Eén image, één poort, één
  verbindingsreeks, dezelfde stappen op Windows en macOS. Het SA-wachtwoord komt uit een
  lokaal `.env`-bestand en staat niet in de repository. (#800)
- **Beheerders kunnen nu een periode instellen op Instellingen → Velden, zoals "Zomerstop" of
  "Competitie", en een veldbeschikbaarheid-venster daaraan koppelen.** Een venster zonder periode
  blijft het hele jaar gelden zoals voorheen; een venster gekoppeld aan een periode geldt
  uitsluitend terwijl die periode loopt. Zo hoeft een club niet langer twee keer per jaar
  handmatig vensters toe te voegen en weer te verwijderen om de zomerstop te overbruggen — de
  velden die doordeweeks alleen tijdens de zomerstop bespeelbaar zijn, krijgen nu hun eigen
  periode-gebonden venster naast het reguliere competitieschema. Bestaande vensters blijven
  ongewijzigd werken (geen periode = standaardregime, exact zoals vóór deze wijziging). (#581)
- **`Database.Postgres/`: de eerste bouwsteen van de Postgres-tier (epic #815) — een C#-schemadefinitie
  die zowel de stg- als de his-tabel-DDL, de unieke-sleutelindex en het upsert-/change-detection-statement
  genereert voor de drie bestaande ETL-entiteiten (teams, matches, matchdetails).** Bewust géén
  gedeelde abstractie met de bestaande SQL Server-laag (die blijft functioneel ongewijzigd), en géén
  afhankelijkheid van Postgres' eigen systeemcatalogus tijdens runtime. Business-key-kolommen die
  NULL-baar zijn (zoals `poulecode`) krijgen een gegenereerde, nooit-NULL synthetische sleutelkolom
  — anders zou Postgres' `ON CONFLICT` een NULL-waarde als "nieuwe rij" behandelen in plaats van de
  bestaande bij te werken. Nog geen live databaseverbinding vanuit de FunctionApp zelf: dat is de
  scope van latere sub-issues (#821 e.v.). Empirisch geverifieerd tegen een lokale wegwerp-Postgres
  16-container, inclusief het NULL-in-compositesleutel-scenario. (#818)
- **Postgres-vertaling van de planner-kernview `planner.AlleWedstrijdenOpVeld`** (epic #815):
  `Database.Postgres/PostgresPlannerViewGenerator.cs` + `PostgresPlannerAvailabilityReader.cs`.
  De veldresolutie (Sportlink-veldstring "veld 1 A" → veldnummer + subpositie) is bewust
  **niet** opnieuw in SQL herbouwd — dat zou een derde, onafhankelijke kopie zijn naast de
  bestaande T-SQL-`OUTER APPLY` en de C#-implementatie (bewaakt door `VeldResolutieDriftTests`).
  In plaats daarvan is die matching-logica geëxtraheerd naar het nieuwe, tier-agnostische
  project `Planner.Shared/` (`VeldResolver`/`VeldNormalisatie`, pure tekstlogica zonder
  databaseafhankelijkheid) en door zowel `FunctionApp` als `Database.Postgres` hergebruikt —
  `PlannerShared.ResolveVeld`/`AutoPlanService.NormaliseerVeld` zijn nu dunne delegaties,
  gedrag ongewijzigd (115 bestaande planner-tests blijven groen). Empirisch gevonden tijdens de
  integratietests: SQL Server's G-team-detectie werkt ongemerkt door de case-insensitive
  standaardcollatie; Postgres' regex-operator `~` is dat niet en liet G-teams stil uit de
  bezetting vallen bij afwijkende hoofdlettering tussen ClubCode en teamnaam — opgelost met de
  hoofdletterongevoelige variant `~*`. Overige gelijkheidsvergelijkingen in deze view dragen
  hetzelfde class-of-bug totdat #820 een tier-brede collatiefix levert. (#819)
- **`Database.Postgres.Cli` + `MigrationRunner`: genummerde-migratiebestanden-mechanisme voor de
  Postgres-tier.** Een verse club-installatie op Postgres krijgt het huidige schema in één keer via
  genummerde `.sql`-bestanden in `Database.Postgres/migrations/`, bijgehouden in een
  `schema_migrations`-ledger-tabel — geen catalogus-probing-ceremonie zoals het SQL Server
  `PostDeployment`-script nodig heeft voor een jaren-oude, al-draaiende database. Elke toegepaste
  migratie krijgt zijn SHA-256-checksum vastgelegd; een later gewijzigd, al toegepast bestand faalt
  hard in plaats van stilzwijgend opnieuw uit te voeren. Een PostgreSQL advisory lock beschermt
  tegen twee gelijktijdige runners — empirisch gevonden tijdens het testen: de lock moet vóór de
  allereerste DDL-aanraking genomen worden, anders raceten twee runners al op het aanmaken van de
  ledger-tabel zelf. Lokaal draaien via `scripts/dev/Invoke-PostgresMigrations.ps1`.
  `001_baseline.sql` dekt vooralsnog alleen de vier configuratietabellen die de planner-kernview
  (#819) nodig heeft — zie issue #821 voor de resterende, nog niet geporte tabellen.
- **Lokale Postgres-ontwikkelcontainer naast de bestaande SQL Server-container** (#822, epic #815).
  `docker-compose.yml` krijgt een `postgres`-service (image `postgres:16`), bewust achter een
  compose-profile — een gewone `docker compose up -d` start alléén `sqlserver`, niet ongevraagd ook
  Postgres. Credentials via `POSTGRES_USER`/`POSTGRES_PASSWORD`/`POSTGRES_DB` uit hetzelfde lokale
  `.env`-patroon als het bestaande SA-wachtwoord. Nieuw `scripts/dev/Test-PostgresConnection.ps1`
  verifieert connectiviteit via `psql`, de Postgres-tegenhanger van het bestaande
  `sqlcmd`-verificatiepad. Empirisch bevestigd: `postgres:16` is een echt multi-arch image
  (linux/amd64 én linux/arm64) — in tegenstelling tot SQL Server draait dit op Apple Silicon dus
  native, zonder Rosetta-emulatie. macOS-uitvoeringsverificatie kon in deze sessie niet
  plaatsvinden (geen Apple Silicon-hardware beschikbaar) — de configuratie volgt wel consequent de
  bestaande cross-platform-regels. Zie `docs/DEVELOPER-SETUP.md` §4.3.
- **Nieuwe CI-job `PostDeployment op verse Postgres-database`** (#823, epic #815) — de
  Postgres-tegenhanger van de bestaande SQL Server-`fresh-db`-job. Past `Database.Postgres/migrations/`
  twee keer toe via `Database.Postgres.Cli` (#821) tegen een verse Postgres 16-container en bewijst
  daarmee idempotentie. Gebruikt het native GitHub Actions `services:`-blok (aanbevolen boven een
  rauwe `docker run`) met een vast, niet-geheim wegwerpwachtwoord — geen GitHub Secret, want
  `services:`-containers provisioneren vóór elke step (te vroeg voor een in-step gegenereerd
  wachtwoord) en secrets falen sowieso op fork-PR's; de container is volledig geïsoleerd en wordt
  aan het eind van de job vernietigd. Controleert kernobjecten, ClubCode-dekking, exact één
  ledger-rij na de tweede run, en lowercase-identifier-casing. Empirisch gedraaid tegen een
  wegwerpcontainer met dezelfde credentials/queries als de workflow gebruikt.
- **Postgres-equivalent van het AVG-gevoelige database-interactiedeel van de teambegeleiding-import**
  (#824, epic #815): `Database.Postgres/TeambegeleidingImporter.cs` + migratie
  `002_avg_teambegeleiding.sql` (`avg.teambegeleiding`/`avg.importlog`). Native Postgres-COPY
  (binaire import) i.p.v. `SqlBulkCopy`, delete-vóór-insert strikt op één ClubCode (nooit een
  Postgres-equivalent van `TRUNCATE`). Drie bewuste verbeteringen t.o.v. het SQL Server-origineel:
  delete+bulklaad+auditlog-insert lopen nu in één transactie (het origineel deed drie losse,
  niet-getransactioneerde aanroepen), een impliciete clubselectie valideert expliciet
  `syncenabled = true` zodat de AllStars FC-democlub nooit per ongeluk als doelclub voor échte
  persoonsgegevens wordt gekozen, en de AVG #208-staleness-check is nu ClubCode-gescoped (het
  origineel keek over alle clubs heen). De flexibele CSV-kolomherkenning van het originele
  PowerShell-script is hier bewust niet herbouwd — dit levert alleen het geteste, AVG-kritieke
  databasedeel op; zie issue #824 voor de resterende scope. Getest tegen een lokale
  Postgres-devcontainer, uitsluitend met fictieve testdata conform CLAUDE.md's goedgekeurde
  AVG-uitzonderingen.
- **Nieuwe CI-stap `scripts/ci/check-path-casing.sh`** (#825, epic #815): bewaakt dat elke
  padverwijzing in ps1/psm1/md/yml/yaml/csproj-bestanden exact overeenkomt met het daadwerkelijke,
  getrackte bestand. Git's `core.ignorecase=true` (Windows/macOS-default) merkt een
  casing-mismatch lokaal niet op; de Linux-CI-runner (`core.ignorecase=false`) faalt daar hard op
  — specifiek relevant voor de nieuwe `Database.Postgres/`-boom. Nulmeting tegen de huidige
  repo-staat (165 bestanden) is groen; empirisch geverifieerd met geïsoleerde, tijdelijke fixtures
  (niet in de echte repo-tree) dat de guard een bewuste casing-typo detecteert en genest-pad-,
  dotfile- en spatie-bevattende referenties correct negeert.

### Changed
- **De lokale database draait voortaan altijd in Docker, ook op Windows.** Werken tegen een
  rechtstreeks op Windows geïnstalleerde SQL Server-service wordt niet meer ondersteund: dat
  pad werkte alleen op Windows en dwong overal een tweede variant af in scripts én handleiding.
  Gevolg voor bestaande Windows-werkplekken: `Integrated Security=True` in
  `FunctionApp/local.settings.json` vervangen door de SQL-login uit `docs/DEVELOPER-SETUP.md`.
  `Test-App.ps1` meldt dit expliciet met de benodigde stappen als de oude instelling nog
  gebruikt wordt. (#800)

### Removed
- `FunctionApp/setup/pre-debug-check.ps1` en `FunctionApp/setup/setup-local-debug.ps1`. Beide
  gingen uit van een lokaal geïnstalleerde SQL Server op Windows met Windows-authenticatie, en
  waren daarnaast achterhaald (ze verwezen naar poort 7071 en naar F5 in Visual Studio).
  `Start-Debug.ps1` en `Test-App.ps1` dekken deze controles volledig en werken op beide
  platforms. De SQL-scripts in dezelfde map blijven bestaan — die vullen juist een verse
  database. (#800)

### Changed
- **BREAKING CHANGE: de productie-deploy vereist voortaan de repository-variabele `DatabaseTier`.** Epic #815 introduceert een multi-tier databasestrategie (SQL Server → Postgres → SQLite → Cosmos DB voor het e-maillog); dit issue legt vast hoe een fork op build/deploytijd precies één tier kiest — geen gedeelde runtime-abstractie, een aparte, volledig zelfstandige implementatieboom per tier. `deploy.yml` bouwt en publiceert nu het `.csproj` dat bij de gekozen tier hoort via een canonieke resolver (`scripts/ci/resolve-database-tier.sh`); ontbreekt de variabele of staat hij op een onbekende waarde, dan faalt de deploy-workflow hard — er is bewust geen stille terugval naar `SqlServer`. **Migratie-instructie voor bestaande forks:** zet vóór de volgende push naar `main` de variabele `DatabaseTier` op `SqlServer` (de enige vandaag geïmplementeerde waarde) via GitHub → Settings → Secrets and variables → Actions → Variables. Zonder deze stap faalt de eerstvolgende productie-deploy. (#816)

### Fixed
- **Twee race-conditions in de #851-zelftestharness (`scripts/dev/Test-PostgresTier.ps1`,
  `DevServices.psm1`), gevonden bij de eerste echte end-to-end-run van G0/G1.**
  `Wait-ForPostgres` riep `pg_isready` uitsluitend met `-U` aan, zonder `-d <database>` — de
  officiële Postgres-image draait de aanmaak van `POSTGRES_DB` via een tijdelijke, alleen-lokale
  server vóórdat de "echte" server extern gaat luisteren, en `pg_isready` zonder `-d` verbindt
  impliciet met een database die naar de OS-gebruiker is genoemd (altijd meteen aanwezig). Gevolg:
  het script meldde "gereed" terwijl de aangevraagde database `sportlink_selftest` een fractie
  later pas bestond, en de eerstvolgende query crashte met "database … does not exist". Fix:
  `-d $Database` toegevoegd, zelfde database die de aanroeper meteen daarna gebruikt. Daarnaast
  controleerde G1 (`G1.andere-engine.uit`) de SQL Server-poort met één meting direct na `docker
  stop` — op Windows/Docker Desktop kan de host-poortmapping een fractie later loslaten dan het
  containerproces zelf stopt. Fix: een korte polling-lus (max 10s) in plaats van een enkele meting.
  Beide empirisch bevestigd tegen een echte lokale run: G0 en G1 slagen nu reproduceerbaar
  (twee opeenvolgende runs, beide 100% groen op de niet-geblokkeerde poorten).
- **De AllStars-demodata sloeg zichzelf stil over op een verse database (#856).**
  `Script.PostDeployment1.sql` probeerde teams/wedstrijden te zaaien in `his.teams`/`his.matches` —
  tabellen die pas ontstaan bij de eerste Sportlink-sync en op een verse installatie dus nog niet
  bestaan. De oude code ving dat op met een stille `PRINT` + `RETURN`: geen foutmelding, geen
  afwijkende exitcode, HTTP 200 op elke route terwijl de dagplanning, teambegeleidingspagina en
  testdatapagina leeg bleven. Architectuurbesluit (eigenaar, Optie B): de team-/teambegeleiding-/
  wedstrijddemo verhuist naar een los, expliciet aan te roepen script
  (`scripts/migrations/003-seed-allstars-demo-matches.sql`, uit te voeren ná de eerste sync);
  `Script.PostDeployment1.sql` meldt voortaan met een zichtbare `RAISERROR` (i.p.v. de stille
  `PRINT`) of die tabellen al bestaan. Empirisch geverifieerd: het nieuwe script zaait exact 28
  teams, 28 begeleiders en 224 wedstrijden en is idempotent; de `fresh-db`-CI-job bootst dit
  scenario nu ook zelf na en bewaakt deze aantallen. **Nog niet gedekt:** de Postgres-tier heeft
  nog geen eigen demodata-seed (zie issue 862) om ditzelfde probleem in te hebben.
- **`public.speeltijden` (Postgres-tier) miste drie kolommen ten opzichte van `dbo.Speeltijden`**
  (`WedstrijdHelft`, `WedstrijdRust`, `StandaardVoorkeurTijd`, #666) — ontdekt tijdens #887's
  vertaling van `AdminSpeeltijdenRepository`, die alle drie gebruikt. Nieuwe migratie
  (`003_speeltijden_kolommen.sql`) voegt ze toe; de `fresh-db-postgres`-CI-job bewaakt voortaan ook
  deze kolomdekking. Empirisch geverifieerd: migratiepad tweemaal toegepast tegen een
  wegwerp-Postgres-container, idempotent, eindschema komt overeen met de SQL Server-tier. (#893)
- **De Postgres-ETL-boom (`KnownEntities.cs`) week op twee plekken af van de vastgelegde
  lowercase-snake_case-identifier-conventie** (`docs/ARCHITECTUUR-DATABASE-TIERS.md` §3): de
  `ClubCode`-kolom (alle drie entiteiten) en vrijwel de volledige `matchdetails`-entiteit
  (~60 kolommen) waren PascalCase, letterlijk overgenomen uit de SQL Server-brontabellen. Omdat
  elke identifier onvoorwaardelijk gequote wordt, landde die casing letterlijk in de database —
  elke latere, ongequote verwijzing (`WHERE clubcode = @club`) zou daarop stukgelopen zijn. Alle
  kolommen zijn nu lowercase; `EntityDefinition.Create` valideert dit voortaan af bij constructie
  en een nieuwe test bewaakt de daadwerkelijk gegenereerde DDL van alle drie entiteiten tegen
  regressie. Design-afweging vastgelegd in `docs/ARCHITECTUUR-DATABASE-TIERS.md` §9. (#855)
- **Audit-tijdstempels (`mta_inserted`/`mta_modified`/`mta_deleted`) van de Postgres-ETL-his-tabellen
  kregen lokale tijd in plaats van UTC** — `NOW()` in een naïeve `TIMESTAMP`-kolom gebruikt de
  sessietijdzone bij de impliciete cast; draaide de databaseserver niet op UTC, dan stond er lokale
  tijd in een kolom die de rest van de applicatie als UTC behandelt. Exact de regressie die PR #246
  al oploste voor SQL Server, nu voor Postgres. Kolommen zijn nu `TIMESTAMPTZ` (Postgres normaliseert
  die intern altijd naar UTC, ongeacht sessietijdzone — `NOW()` hoefde daardoor niet aangepast te
  worden). Design-afweging vastgelegd in `docs/ARCHITECTUUR-DATABASE-TIERS.md` §8. Empirisch
  bevestigd met een integratietest die de databasesessie expliciet op Europe/Amsterdam zet en
  aantoont dat de geschreven waarde binnen een seconde van de werkelijke UTC-tijd ligt.
  Bijkomend gevonden en gefixt tijdens deze verificatie: `PostgresMergeOrchestrator` maakte het
  `stg`/`his`-schema zelf nooit aan (moest tot nu toe handmatig per test gebeuren); en xUnit's
  standaard testparallellisatie liet meerdere testklassen races op elkaar lopen tegen de gedeelde
  Postgres-testinstantie — testparallellisatie nu uitgeschakeld voor `Database.Postgres.Tests`.
- **De AllStars FC-demodataseed vulde de business-key-bronkolommen (`teamcode`, `lokaleteamcode`,
  `poulecode`) van `his.teams` nooit, alleen de afgeleide sleutelweergave `bk_teams` rechtstreeks.**
  Op SQL Server bleef dat onopgemerkt (`bk_teams` is daar een gewone kolom); op de Postgres-tier
  (#818) is `bk_`  een `GENERATED ALWAYS`-kolom afgeleid uit exact die drie bronkolommen — met
  alle drie `NULL` kregen alle 28 demoteams dezelfde afgeleide sleutel en liet de unieke index er
  nog maar één over. Seed vult nu unieke, herkenbaar-fictieve waarden (`9000000+`, dezelfde
  gereserveerde demo-range als `wedstrijdcode`) in alle drie de kolommen. Design-afweging
  (waarom de bronkolommen vullen i.p.v. de gegenereerde kolom terugzetten naar een gewone kolom)
  vastgelegd in `docs/ARCHITECTUUR-DATABASE-TIERS.md` §7. Empirisch geverifieerd tegen een
  wegwerp-SQL-Server-container: exact 28 rijen, 28 unieke teamcodes, geen enkele NULL meer in de
  drie kolommen. **Geautomatiseerde CI-telling blijft nog open** — de bestaande `fresh-db`-job
  draait de seed tegen een database zonder `his.teams`/`his.matches`, waardoor het hele
  AllStars-blok zich stil overslaat (zie #856, aparte architectuurvraag die aan de eigenaar is
  voorgelegd).
- **`close-released-issues.yml` kon een issue ten onrechte sluiten bij de volgende release als een
  ander, nog niet afgerond issue-nummer toevallig in dezelfde commit-subjectregel voorkwam** (bijv.
  `fix: ... (#820) (#869)` — het PR-referentienummer ná een bewuste, losse kruisverwijzing naar een
  opzettelijk nog open issue). De oude, losse `#[0-9]+`-scan pakte élk nummer in de hele regel; de
  scan matcht nu uitsluitend de titel-conventie `type(#NNN): ...` aan het bégin van de subjectregel
  — hetzelfde onderscheid dat #838 al voor `label-awaiting-release.yml` vastlegde, nu voor de
  workflow die daadwerkelijk sluit in plaats van labelt. Empirisch geverifieerd tegen de laatste 60
  commits van deze repository: de nieuwe scan laat #820/#821 (bewust nog open) en alle
  merge-commit-ruis terecht buiten beeld, en blijft #818/#819/#822/#823/#827/#838 (echt afgerond)
  correct herkennen. (#838)
- **Teamherkenning (`TeamCandidateRepository.cs`) leunde stilzwijgend op SQL Server's case-insensitive
  default-collatie in plaats van expliciet te normaliseren.** Onder de huidige collatie "werkte" een
  kale sleutelvergelijking toevallig; een toekomstige tier met een case-sensitive default (Postgres)
  zou stilzwijgend nul rijen matchen zodra de opgeslagen casing afwijkt van de vers berekende sleutel
  — geen foutmelding, gewoon "team niet gevonden". Alle drie de lookups vergelijken nu expliciet via
  `UPPER(...)`, portable naar elke tier. Zie issue #820 voor het vervolg (Postgres-schema met
  expression-based unique indexes) — dat vereist een nog niet bestaande teamherkenning-datalaag voor
  de Postgres-tier en is bewust niet in deze wijziging meegenomen.
- **`label-awaiting-release.yml` zette 'status: awaiting-release' op issues waar nog niets aan gebouwd was**, zodra een andere PR-body ze ook maar in proza noemde (bijv. een cross-referentietabel of "zie #NNN e.v.") — de #838-fix loste dit eerder alleen voor epics op; #820/#821/#822/#823 liepen dezelfde mislabeling op via gewone sub-issues. De labelloop selecteert nu uitsluitend "sterke" referenties (PR-titel of een sluitend keyword als `Closes #NNN`), dezelfde maatstaf die #630 al voor heropenen gebruikte. (#838)
- **AllStars FC-democlub had geen veldbeschikbaarheid voor maandag t/m donderdag en vrijdag, en de zondagrij werd nooit door de planner gevonden.** De demoseed gebruikte per abuis de .NET-native dagconventie (0=zondag) in plaats van de 1=maandag/7=zondag-conventie die de rest van de applicatie hanteert, en zaaide daardoor maar 2 van de 7 dagen. De UI toonde de foutieve rij als "Dag 0". Seedscript gecorrigeerd naar alle 7 dagen met de juiste conventie; al gezaaide omgevingen herstellen zichzelf bij de volgende deploy. (#812)
- **Database-verbindingen laten de gratis vCore-secondenlimiet niet meer onnodig snel oplopen.** Alle SQL-verbindingen in de FunctionApp draaiden met standaard connection-pooling; een pooled verbinding blijft na afsluiten als actieve sessie op de server staan, wat de free-tier database verhindert automatisch te pauzeren. Pooling staat nu uit voor alle databaseverbindingen. (#808)
- **`Verify-AzureAuthSetup.ps1` rapporteerde de auth-lagen 4 en 5 altijd als FAIL**, ook als ze
  correct waren. Het script zocht `App.razor` en de admin-endpoints één directoryniveau te hoog,
  vond niets, en concludeerde daaruit dat de controles ontbraken. Dit was ook op Windows fout. (#800)
- `scripts/dev/smoke-test.ps1` bepaalde de repository-hoofdmap één niveau te hoog en verwees
  daardoor naar niet-bestaande projectpaden; het script was hierdoor onbruikbaar. (#800)
- `Test-App.ps1` accepteert nu zowel `Server=`/`Database=` als `Data Source=`/`Initial Catalog=`
  in de verbindingsreeks. De eerste schrijfwijze staat in het meegeleverde configuratiesjabloon,
  maar werd niet herkend. (#800)
- **`scripts/dev/Bump-Build.ps1` liet `<Version>` achterlopen op `<AssemblyVersion>`/`<FileVersion>`.**
  De regex voor `<Version>` verwachtte een 3-componenten-waarde, terwijl beide `.csproj`-bestanden
  daar al 4 componenten gebruikten — een gewone build-bump raakte `<Version>` daardoor helemaal
  niet, en zelfs `-NewPatch` faalde stil omdat de waarde nooit matchte. Alle drie de velden
  synchroniseren nu bij elke bump, conform de projectafspraak dat ze altijd gelijk lopen. (#806)
- **Een herhaalde storing opende steeds opnieuw een nieuw GitHub-issue in plaats van een reactie
  op het bestaande.** De zelfherstellende foutmelding controleert bij elke storing of dezelfde
  fout al eerder gemeld is, maar die controle gebruikte de GitHub Search API — die bleek
  onbetrouwbaar met het gebruikte toegangstoken en faalde stil, waarna altijd een nieuw issue
  werd aangemaakt. Twee keer leidde dit tot vijf losse issues voor exact dezelfde storing. De
  controle gebruikt nu de gewone issue-lijst van GitHub in plaats van de zoekfunctie. Een
  herhaalde storing krijgt voortaan een reactie op het bestaande issue (en heropent het als het
  ondertussen gesloten was) in plaats van een duplicaat. (#830)

- **Testmodus stuurt weer een testantwoord naar de reviewer.** In testmodus (`EmailReviewMode=true`) bouwt de AI al sinds een eerdere wijziging een voorgesteld antwoord op, maar dat werd alleen in de database bewaard — nergens te lezen zonder rechtstreekse databasetoegang. Dat testantwoord gaat nu ook naar het ingestelde reviewadres, zoals eerder ook het geval was voordat dit bewust werd uitgeschakeld. Mislukt die verzending, dan blijft het voorstel gewoon in de database staan. (#801)
- **De automatische e-mailverwerking en het handmatig doorsturen van teambegeleiding-vragen gebruiken nu overal dezelfde, centraal geregistreerde opslaglaag voor `planner.EmailVerwerking`.** Beide paden bouwden voorheen op sommige plekken hun eigen kopie van deze laag op in plaats van de gedeelde registratie te gebruiken — onzichtbaar voor de gebruiker, maar daardoor moeilijker betrouwbaar te testen en een risico dat een toekomstige wijziging per ongeluk maar één van de twee paden raakt. Gedrag is ongewijzigd; geverifieerd via de volledige verificatielus inclusief een live doorstuur-test. (#827)
- **De noodmail bij een langdurige database-uitval komt nu betrouwbaar aan, ook zonder frequente herstarts van de achtergrondtaak.** De "al verstuurd"-registratie stond in het geheugen van de lopende taak en kon daardoor verdwijnen zodra die taak opnieuw opstart — het gedrag hing dus af van iets wat niet zichtbaar of controleerbaar was. Die registratie staat nu in de bestaande opslag van de applicatie, dus hij overleeft een herstart. Daarnaast controleert een nieuwe, dagelijkse en volledig losstaande controle rechtstreeks bij Azure of de database uitzonderlijk lang gepauzeerd staat (langer dan een paar uur) — voorheen kwam die controle namelijk alleen aan bod als er toevallig een passend inkomend e-mailbericht was, waardoor er tijdens de uitval van eind augustus 2026 geen enkele melding is verstuurd. Optioneel: deze losstaande controle vereist een eenmalige extra instelling; zonder die instelling blijft alleen de bestaande melding actief. Geen nieuwe Azure-kosten. (#831)
- **Epics blijven niet meer permanent hangen op de status "wacht op release".** Elke keer dat een sub-issue-PR er in de tekst naar verwees (bijvoorbeeld "epic #815"), kreeg het epic-issue dat label opnieuw — maar omdat een epic nooit via een losse release wordt "afgesloten", werd het label daarna nooit meer automatisch verwijderd. Issues met het label `epic` worden nu overgeslagen bij het toekennen van deze status. (#838)

### Security
- **De git-hooks werden op macOS stilzwijgend overgeslagen.** Geen enkel bestand in de repository
  had de executable-vlag, en git negeert een hook zonder die vlag zonder foutmelding — de
  secrets- en AVG-scan zou op een Mac dus helemaal niet draaien terwijl alles groen oogde.
  Beide hooks staan nu als uitvoerbaar geregistreerd. (#800)
- **De AVG-controle op e-mailadressen in documentatie werkte niet op macOS.** De controle
  gebruikte `grep -P`, dat de meegeleverde grep van macOS niet kent; door de foutonderdrukking
  meldde de hook vervolgens "geen persoonsgegevens gevonden" zonder iets te hebben gecontroleerd.
  Omgezet naar een uitdrukking die op beide platforms werkt, met identiek resultaat. (#800)
- `Test-App.ps1` geeft het databasewachtwoord niet langer mee als commandoregel-argument maar via
  een omgevingsvariabele; argumenten zijn op beide platforms zichtbaar in de processenlijst. (#800)

## [2.20.0.0] — 2026-08-09

### Added
- **Teambegeleiding: het "Email Aan"-veld bepaalt nu zelf wie de doorstuur-mail ontvangt.** Voorheen was dit een read-only regeltje om naar Outlook te kopiëren, terwijl de "Vraag doorsturen"-knop altijd naar precies één, server-side bepaalde begeleider mailde — dat verschil was in het scherm niet te zien. Het veld is nu bewerkbaar (standaard gevuld met alle begeleiders van het team), een "Herstel"-knop zet het terug, en de doorstuurkaart toont exact naar wie verstuurd wordt. Verwijder wie niet mee hoeft te lezen, voeg gerust uw eigen adres toe om de flow zelf te testen — dat kon eerder niet, omdat een beheerder meestal zelf geen trainer of teamleider is. Maximaal 15 ontvangers per verzending; een ongeldig adres of een adres op de uitsluitingslijst wordt geweigerd met een duidelijke melding. Elke verzending — automatisch of handmatig opgegeven — wordt vastgelegd voor de bestaande 30-dagen-anonimisering. (#765)
- `docs/ARCHITECTUUR-EMAIL-MODULE.md`: architectuurdocument voor de e-mailfunctionaliteit —
  volledige inventarisatie van alle bestaande verzendpaden (automatische AI-reply, handmatig
  doorsturen), een doelarchitectuur met één stabiel verzend-contract, generieke ontvangerresolutie
  en logging, en een gefaseerd migratieplan inclusief de afzenderstrategie voor "verzenden als
  ingelogde gebruiker" en, als uitbreiding daarop, "verzenden namens een gedeeld postvak" (Send
  As/Send on Behalf via Exchange-mailboxrechten). Analyse en ontwerp; geen functionele wijziging
  in deze wijziging zelf. Vervolgwerk loopt via losse issues onder epic zie issue #777.
- **Bruno API-collectie voor handmatig testen van alle 72 endpoints** (`bruno/`), gegenereerd uit
  `docs/api-standaarden/openapi.yaml` en gecommit zodat hij reviewbaar blijft en in sync loopt met
  de spec. `bruno-gen.json` legt het project en de `local`-omgeving (`http://localhost:7094`) vast.
  Twee beveiligingsschema's uit de spec (functionKey, Easy Auth Bearer) worden nog niet automatisch
  per endpoint gewisseld — zie `docs/DEVELOPER-SETUP.md`, sectie "Bruno API-collectie". (#782)

### Fixed
- **De PII-scan (gitleaks + de eigen patroonscan) blokkeert niet meer op een toevallige treffer binnen een hex-hash.** Het patroon voor Nederlandse mobiele nummers had geen woordgrens, waardoor het regelmatig binnen een SHA256-hash (bijv. in een gegenereerd lockfile) matchte en de Security Gate onterecht rood liet zien. De regex is nu verankerd met `\b`, waardoor hij binnen een aaneengesloten hex-string niet meer kan matchen maar een echt telefoonnummer nog steeds herkent. Dezelfde aanscherping is ook doorgevoerd in de lokale pre-commit/pre-push hook-template, die apart van de CI-config hetzelfde patroon gebruikte. (#784)
- **Een team dat in de gegevens met een spatie geschreven staat ("MO13 1") wordt weer herkend als hetzelfde team als "MO13-1".** Een verzoek om een wedstrijd te verplaatsen kreeg het antwoord "Geen wedstrijd gevonden", terwijl die wedstrijd wél in het programma stond. Oorzaak: bij het vergelijken van teamnamen gold een schuine streep, punt of komma tussen leeftijd en teamnummer als scheidingsteken, maar een gewone spatie niet. Daardoor kreeg hetzelfde team twee verschillende interne sleutels en vond het systeem er niets bij. Dit trof **alle** teams van de democlub AllStars FC — de testset waarmee elke club de e-mailverwerking uitprobeert — en ook een e-mail van een echte club waarin het team met een spatie geschreven werd. Tegelijk werkt het herkennen van een team zonder jongens/meiden-aanduiding ("13-1") voor de democlub weer, want ook dat viel hierdoor stil. (#766)
- **Instellingen voor de KNVB-flow (standaardregio en het meesturen van de KNVB-kalender als PDF) worden weer ingelezen.** Door een fout in de databasequery mislukte het laden van de instellingen elke keer volledig; in de logs stond alleen een foutregel die geen gevolgen leek te hebben. Gevolg: bij een verzetverzoek zonder nieuwe datum viel het systeem in productie altijd terug op het oude antwoord, zonder BCC naar de eigen begeleiding, zonder PDF-bijlage en zonder voorstel met vrije zaterdagen — ook al stonden die instellingen correct ingevuld. In de e-mailtester werkte de flow wél, omdat die de instellingen langs een ander pad ophaalt; daardoor bleef dit onopgemerkt. (#767)

### Changed
- **De ontdubbelde teamlijst herstelt zichzelf als de regels voor teamnaam-vergelijking wijzigen.** De interne sleutel per team staat opgeslagen in de database maar wordt door de applicatie berekend. Wijzigt die berekening, dan sloten de opgeslagen sleutels niet meer aan en verdwenen teams uit de lijst zonder terug te komen. De synchronisatie berekent nu bij elke run de sleutels opnieuw uit de opgeslagen teamnaam en werkt ze bij; blijken twee rijen daarna hetzelfde team te zijn, dan worden ze samengevoegd met behoud van alle geleerde schrijfwijzen. Ook de e-mailverwerking en de e-mailtester controleren dit vooraf, zodat een deployment zonder nachtelijke synchronisatie niet met een onbruikbare teamlijst achterblijft. (#766)
- **De teamdropdown bij Voorkeurstijden en Teamregels toont niet meer dezelfde teams dubbel of in rauwe schrijfwijze.** De lijst kwam rechtstreeks uit de ongenormaliseerde brondata, waarin elk team meerdere keren voorkomt — per poule en in twee schrijfwijzen ("O13" naast "JO13"). De dropdown gebruikt nu de al bestaande, ontdubbelde teamlijst die na elke synchronisatie wordt bijgewerkt: één rij per fysiek team. Als onderdeel van dezelfde fix krijgt de democlub AllStars FC nu ook een gevulde, ontdubbelde teamlijst — die liep eerder nooit mee in de ontdubbelingsstap omdat die alleen voor de echte club draaide. (#756)
- **Teambegeleiding: iemand met twee functies in hetzelfde team wordt in de Outlook-kopieerregel nu maar één keer opgenomen.** De kaartenweergave toont nog steeds elke functie apart, maar de "Kopieer naar Outlook"-regel mailt niet langer dezelfde persoon twee keer. Daarnaast worden exacte duplicaat-rijen uit de Sportlink-export (dezelfde persoon met dezelfde rol twee keer) voortaan bij import overgeslagen, met een waarschuwing in het importresultaat. (#761)

## [2.19.0.0] — 2026-07-28

### Changed
- **De Dagplanning vergelijkt nu met twee tabs: Optimaal en Huidig.** Elke tab toont dezelfde opbouw — eerst de tijdlijn per veld, daaronder de wedstrijdenlijst van diezelfde stand. Omdat beide tabs op exact dezelfde hoogte beginnen, werkt wisselen als het vergelijken van twee foto's: het oog springt niet en je ziet direct wat er in de veldbezetting verandert. Eerder stonden er twee verschillende vergelijkingen door elkaar op één pagina — een tabel met "huidig" en "optimaal" in kolommen naast elkaar, én tabs die alléén de tijdlijn wisselden terwijl de tabel bleef staan. De filterknoppen gelden nu voor beide tabs, en de kolom Wijziging staat in beide: in de tab Huidig zie je daarmee welke wedstrijd gaat verschuiven, en met één klik waarheen. (#689)
- **De verdwaalde horizontale schuifbalk onder de tijdlijn is weg.** Op een breed scherm verscheen die terwijl de inhoud ruim paste. Oorzaak: het eerste en laatste tijdlabel stonden gecentreerd op hun uurlijn en vielen daardoor half buiten de tijdlijn — precies genoeg om de browser een schuifbalk te laten reserveren. Die twee labels lijnen nu links en rechts uit; geen label wordt afgekapt en op een smal scherm past de tijdlijn nu binnen het scherm in plaats van een vaste minimumbreedte te forceren. (#689)

### Added
- **Bij een vraag over meerdere datums noemt het antwoord nu de coördinator bij naam:** "Laat weten welke optie(s) de voorkeur hebben, dan gaan we samen met [naam] plannen en definitief opnemen in de planning." Staat er geen coördinatornaam in de instellingen, dan blijft de zin gewoon compleet zonder naam. De naam komt uit de instellingen van de club waarvoor het antwoord wordt opgebouwd, dus in een test met de demoklub verschijnt de demo-coördinator. (#670)

### Fixed
- **Een mislukte databasemigratie laat de uitrol nu falen in plaats van "geslaagd" te melden.** Bij het uitrollen van een nieuwe versie draait een migratiescript tegen de database. Dat script meldde succes zolang het tot het einde kwam, ook als er onderweg fouten optraden — precies wat er twee releases achter elkaar gebeurde. De uitrol stopt nu bij de eerste echte fout. Zie issue #739.
- **Een club die de software voor het eerst in gebruik neemt, krijgt nu een compleet werkende database.** Zeven tabellen — waaronder de tabel met alle instellingen — werden door het migratiescript wel gevuld en aangepast, maar nooit aangemaakt. Op de bestaande omgeving valt dat niet op, omdat die tabellen er al jaren staan; bij een nieuwe installatie ontbraken ze. Datzelfde gold voor een aantal schema's en voor vier overzichten die op de Sportlink-gegevens leunen. Het script controleert nu overal eerst of iets bestaat, en slaat wat nog niet kan netjes over tot de eerste synchronisatie is gelopen. Zie issue #734.
- **De standaard speeltijden worden nu aan de juiste club toegekend.** De vulling koos de alfabetisch eerste clubcode, en dat is bij een nieuwe installatie de democlub — waardoor de echte club zonder speeltijden achterbleef en dat bij een volgende poging niet werd hersteld. De democlub wordt nu overgeslagen en er wordt per club gekeken of er al speeltijden zijn. Zie issue #740.

### Changed (ontwikkeling en documentatie)
- **De handleidingen noemden een synchronisatie-adres dat niet bestaat.** Op vier plekken stond `/api/sync` met parameters die de code niet kent; het werkelijke adres is `/api/sync-matches` en een volledige seizoensvernieuwing gaat met `?reset=true&season=JJJJ`. Wie de handleiding volgde kreeg een foutmelding. Ook twee verouderde beweringen gecorrigeerd: de synchronisatie haalt niet "de laatste vijf weken" op maar vorige week tot en met het einde van het seizoen, en de opmerking dat er geen tests zijn klopt al lang niet meer. Raakt alleen ontwikkelaars. (#662)
- **De bouwstraat voert de databasemigratie nu bij elke wijziging uit tegen een lege database**, twee keer achter elkaar, en controleert daarna of alle 22 kerntabellen bestaan en gevuld zijn. Tot nu toe werd alleen de tekst van het script vergeleken met de schemadefinities; dat kan een ontbrekende tabel op een nieuwe installatie niet zien. Kost niets: dit draait in een wegwerpomgeving op de bouwserver, zonder Azure-resource.
- **De schemacontrole kijkt nu ook naar kolommen en eist een echte aanmaakopdracht.** Eerder gold elke vermelding van een tabelnaam als voldoende — ook een vulopdracht — en werden nieuwe kolommen helemaal niet gecontroleerd. Dat is precies hoe de twee gemiste instellingen van de vorige release door de controle glipten.

## [2.18.0.1] — 2026-07-28

### Fixed
- **Drie stille fouten in de databasemigratie naar productie zijn opgelost.** Bij het uitrollen van een nieuwe versie draait een migratiescript tegen de productiedatabase. Dat script meldde "geslaagd", terwijl er in werkelijkheid tien fouten in het uitvoerlog stonden — bij twee opeenvolgende releases, zonder dat iemand het kon zien. (#738)
  - De tabel met de KNVB-speeldagenkalender bestond helemaal niet in productie: hij stond alleen in de schemadefinitie, en die wordt bij een uitrol niet meegenomen. Alle acht vullingen van die tabel faalden dus. Daardoor kon de KNVB-kalenderbijlage bij een verzet-verzoek zonder datum in productie niet werken. De tabel wordt nu aangemaakt vóór hij gevuld wordt; het gaat om 423 speeldagen over zes regio's en twee seizoenen.
  - De aanvulling van ontbrekende leeftijdscategorieën (`JO6` en alle `MO`-categorieën) faalde omdat de clubcode niet werd meegegeven. Die categorieën ontbraken daardoor in productie, en elke opzoeking van wedstrijdduur of standaard voorkeurstijd voor zo'n categorie vond niets.
  - De primaire sleutel op de speeltijden bestond in productie nog uit één kolom in plaats van twee. Daardoor kon de democlub geen eigen speeltijden krijgen — het kopiëren botste op de gegevens van de echte club — en konden twee clubs niet dezelfde leeftijdscategorie hebben. Dat laatste is een schending van de multi-club-opzet.

  Alle drie zijn geverifieerd door de productiesituatie lokaal na te bootsen (tabel verwijderd, categorieën verwijderd, sleutel teruggezet) en het script daarna twee keer te draaien: geen enkele fout, en herhalen is veilig.

  De onderliggende oorzaak dat zulke fouten onzichtbaar bleven, is apart vastgelegd in issue #739, net als een vierde bevinding die alleen een nieuwe clubinstallatie raakt: zie issue #740.

## [2.18.0.0] — 2026-07-28

### Added
- **Als een tegenstander per e-mail vraagt om een wedstrijd te verzetten zonder een nieuwe datum te noemen, stemt het systeem geen nieuwe datum meer zelf af.** In plaats daarvan gaat het antwoord naar de tegenstander met de begeleiding van ons eigen team in BCC (zodat beide teams het onderling kunnen afstemmen), inclusief de KNVB-speeldagenkalender als bijlage en een voorzet van een paar zaterdagen waarop ons team volgens het huidige programma nog vrij is. Aangestuurd via twee nieuwe kolommen in `dbo.AppSettings` (`KnvbStandaardRegio`, `KnvbPdfBijlageIngeschakeld` — nog geen GUI-scherm, rechtstreeks in de database in te stellen) — ontbreekt de regio, dan verandert er niets aan het bestaande herplan-gedrag. (#561)
- **Teamherkenning in inkomende e-mail werkt nu vanuit één teamlijst in plaats van losse tekstregels** (#692, #696, #697, #698, #699). Teamnamen komen in allerlei schrijfwijzen binnen — "JO13-2", "JO 13-2", "jo13/2", of alleen "13-1" — en werden op meerdere plekken los van elkaar geïnterpreteerd. Daardoor kon een tegenstander per ongeluk als eigen team worden gezien, met thuis en uit omgedraaid. Er is nu één teamlijst die na elke nachtelijke synchronisatie wordt opgebouwd, en één plek die een schrijfwijze naar het juiste team herleidt.

  Dat blijkt ook nodig te zijn: Sportlink levert élk team in twee schrijfwijzen aan — de eigen clubnotatie ("JO10-1") en de KNVB-notatie met clubnaam ervoor en zonder de J ("[club] O10-1"). Voor één club leverden 255 verschillende namen zo 116 werkelijke teams op.

  Bij een aanduiding die écht dubbelzinnig is, wordt niet meer gegokt. "13-1" kan bijvoorbeeld zowel JO13-1 als MO13-1 zijn; bij één club gebeurt dat bij tien teamparen. In dat geval kiest het systeem alleen als het zeker genoeg is, en anders wordt de vraag teruggelegd in plaats van stilzwijgend het verkeerde team te pakken.

  **De e-mailverwerking verandert nog niet van gedrag:** de nieuwe herkenning staat standaard uit. Een beheerder kan hem eerst in meekijk-stand zetten (dan wordt alleen vastgelegd of de oude en nieuwe uitkomst overeenkomen) en pas daarna leidend maken. Zie [docs/ARCHITECTUUR-TEAMRESOLUTIE.md](docs/ARCHITECTUUR-TEAMRESOLUTIE.md).
- **Nieuwe pagina Beheer → Teamaliassen** (#701). Schrijfwijzen die het systeem zelf niet kan herleiden, worden vastgelegd en aan de coördinator voorgelegd. Pas na goedkeuring worden ze vertrouwd, zodat een verkeerde gok zich niet kan vastzetten.
- **Een club kan de beheeromgeving nu onder een eigen webadres bereikbaar maken**, bijvoorbeeld `wz.[clubdomein]` in plaats van het automatisch gegenereerde Azure-adres. Er is een handleiding en een script dat de drie benodigde wijzigingen in één keer doorvoert. Dat is meer werk dan alleen een DNS-record: zonder de bijbehorende instellingen laadt de omgeving wel, maar blijven alle schermen leeg of mislukt het inloggen. Het script controleert dat vooraf en laat zich veilig herhalen. Kost niets — het gratis Azure-pakket staat twee eigen webadressen toe, inclusief automatisch vernieuwende beveiligingscertificaten. (#657)
- **Per leeftijdscategorie is nu een standaard voorkeurstijd instelbaar** onder Instellingen → Speeltijden. Teams zonder eigen voorkeurstijd worden daarmee toch rond de gebruikelijke tijd ingedeeld, in plaats van simpelweg in het eerste vrije gat. De tijden zijn per club aan te passen; laat je een categorie leeg, dan kiest de planner het eerst beschikbare tijdslot. Als startwaarden staan ingevuld: JO8-JO9 en MO8-MO9 om 09:00, JO10-JO13 en MO10-MO13 om 10:00, JO14-JO15 en MO14-MO15 om 11:00, JO16-JO19 en MO16-MO19 om 12:00, G om 10:00, en vrouwen en senioren om 14:30. (#666)
- **De dagplanning laat nu per wedstrijd zien of de gewenste tijd gehaald is** — met de afwijking in minuten en waar die gewenste tijd uit komt: een teamregel, de eigen voorkeurstijd van het team, of de standaardtijd van de leeftijdscategorie. (#666)
- **Wedstrijden zijn nu met de muis te verslepen in de tijdlijn.** Naar een andere tijd — in stappen van 5 minuten — of naar een ander veld, en bij wedstrijden op een half of kwart veld ook naar een specifiek veldgedeelte: waar je verticaal in de rij neerzet bepaalt of het A1, A2, B1 of B2 wordt. De tabel, de eindtijd en het aantal te wijzigen wedstrijden lopen direct mee, en handmatig verplaatste wedstrijden krijgen een stippellijn zodat je ziet wat je zelf hebt gedaan. Ontstaat er daardoor een onmogelijke planning — twee wedstrijden die niet samen op één veld passen, of te weinig ruimte ertussen — dan verschijnt er meteen een waarschuwing met welke wedstrijden het betreft. (#666)
- **Het scherm Teambegeleiding legt nu stap voor stap uit hoe je de juiste lijst uit Sportlink haalt.** De oude toelichting was één regel en sloeg de belangrijkste keuzes over. De negen stappen staan nu op het scherm zelf, inclusief het selecteren van alle bondsteams — wordt die stap overgeslagen, dan komen ook lokale teams in de lijst terecht. (#667)
- **Er staat nu een duidelijke waarschuwing bij het importeren:** alle gegevens worden opnieuw ingelezen en vervangen de oude gegevens van de club volledig. Dat was altijd al zo, maar het scherm vertelde het niet. Een onvolledige lijst herstel je dus door simpelweg een complete lijst opnieuw te importeren. (#668)
- **Velden, veldopeningstijden en trainingsschema zijn nu volledig via een nieuw scherm (Instellingen → Velden) te beheren — vrij instelbaar per club.** Eerder kon een veld alleen via een directe database-wijziging worden toegevoegd, en bestond er al een API voor het wekelijkse openingsvenster per veld maar geen scherm om die te bewerken. Daarnaast is er nu een trainingsschema: per veld per weekdag is aan te geven wanneer het bezet is door training, en dat mag per dag verschillen — bijvoorbeeld maandag ruim beschikbaar en donderdag vol. Een trainingsblok telt automatisch mee als bezetting bij het plannen van wedstrijden én in e-mailreacties, zonder aparte koppeling. Clubs die dit niet instellen merken geen verschil in gedrag. (#679)
- **De Email-tester toont nu de ruwe e-mail (afzender, naam, onderwerp en body) als één kopieerbaar tekstblok boven de resultaten.** Handig om een geteste e-mail in één keer te bewaren of te delen bij het debuggen, zonder de velden los over te typen. (#694)

### Changed
- **De bouwstraat draait nu de tests.** Tot nu toe werden alleen de twee applicaties gebouwd en liepen de bijna vierhonderd tests nooit automatisch mee — een fout die lokaal rood was kon dus ongemerkt doorgevoerd worden.
- **Dependabot bundelt minor/patch-updates nu per ecosysteem in één PR in plaats van losse PR's per dependency.** Op één dag liepen hierdoor 163 GitHub Actions-runs en >100 e-mails op — elke losse dependency-bump kreeg een volledige CI-pass. Het ontbrekende `dependencies`-label (dat op elke Dependabot-PR een waarschuwingscomment veroorzaakte) is ook aangemaakt. Raakt alleen de repo-onderhoudslast, geen app-gedrag. (#703)
- **Het opstarten van de lokale ontwikkelomgeving wacht nu tot de onderdelen echt klaar zijn** in plaats van een vast aantal seconden te tellen. Het script meldt de werkelijke opstarttijd en het versienummer, en geeft een foutmelding als een onderdeel niet opkomt — voorheen leek een mislukte start op een geslaagde. Er is een `-Tail`-optie die alle meldingen in één venster samenvoegt, en een apart stopscript. Dit raakt alleen ontwikkelaars, niet de beheeromgeving zelf. (#684)
- **De dagplanning respecteert nu de ingestelde voorkeurstijden.** Dit was de kern van het probleem: de planner koos het vroegste vrije gat van de dag zodra dat meer dan één buffer vóór de voorkeurstijd lag. Een team met voorkeur 14:30 belandde daardoor op 09:00 — vijf en een half uur ernaast — terwijl het overzicht "OK" meldde. De planner plant nu op de gewenste tijd, en anders zo dicht mogelijk daarbij. In het testscenario van de eigenaar ging dat van 0 naar 8 van de 14 wedstrijden exact op hun voorkeurstijd; de resterende afwijkingen komen door een echt tekort aan veldruimte en worden nu ook als zodanig gemeld. Keerzijde: de dag eindigt later dan bij compact plannen, omdat wedstrijden niet meer naar voren worden getrokken. (#666)
- **Prioriteit bepaalt nu wie zijn voorkeurstijd krijgt als twee teams dezelfde plek willen.** Voorheen werd prioriteit alleen gebruikt om binnen één team de belangrijkste voorkeursregel te kiezen, en nooit om teams onderling te vergelijken: een team met prioriteit 1 kon zijn tijd verliezen aan een team met prioriteit 10. Een laag getal betekent nu overal "belangrijker" — ook bij de teamregels, waar de sortering precies omgekeerd stond. (#666)
- **Er is één knop om te optimaliseren.** De losse "Geavanceerde optimalisatie (klassiek)" is vervallen; die negeerde voorkeurstijden en prioriteiten volledig, waardoor twee knoppen met vergelijkbare uitleg verschillende planningen opleverden. Het delen van de planning als HTML — kopiëren voor e-mail of downloaden — is behouden en volgt nu de gekozen weergave. De optie om een gewenste eindtijd op te geven en resterende ruimte als extra buffer te verdelen is met dat oude pad verdwenen. (#666)
- **Teambegeleiding staat nu bovenaan** in het menu en als eerste tegel op het dashboard, direct onder Dashboard. Het is het meest gebruikte scherm: contactgegevens van begeleiders zijn hier sneller te vinden dan in Sportlink zelf. De overige schermen behouden hun onderlinge volgorde. (#669)

### Fixed
- **De twee nieuwe instellingen voor de KNVB-kalenderbijlage zouden bij het live zetten ontbreken in de database** (#561). Ze waren alleen aan de schemadefinitie toegevoegd, en die definitie wordt bij een deploy niet uitgerold — alleen het migratiescript draait. Zonder deze correctie zou de Instellingen-pagina na deze release een foutmelding geven in plaats van te laden, terwijl er lokaal niets aan de hand leek. De bestaande controle hierop kijkt alleen naar nieuwe tabellen, niet naar nieuwe kolommen; dat gat is apart vastgelegd in issue #734. Het migratiescript is tegen een database met twee clubs uitgevoerd en twee keer achter elkaar gedraaid om te bevestigen dat herhalen veilig is.
- **De teamherkenning werkt nu uitsluitend via de nieuwe teamlijst** (#700). De oude tekstregels die een teamnaam probeerden te repareren, en de aanname dat een team "van ons" is zodra er geen spatie in de naam staat, zijn verwijderd. Die aanname kon een tegenstander als eigen team aanmerken, met thuis en uit omgedraaid. Welk van de twee genoemde teams het eigen team is, wordt nu bepaald door te kijken wélke in de teamlijst staat.

  Er is ook geen schakelaar meer om de herkenning uit te zetten: als dat het enige pad is, is zo'n schakelaar geen veiligheidsventiel maar een valkuil. In plaats daarvan geldt: is de teamlijst leeg — bijvoorbeeld direct na een update, vóór de eerste nachtelijke synchronisatie — dan wordt hij eenmalig alsnog opgebouwd, en lukt dat niet, dan wordt er niet verwerkt in plaats van berichten koppelen zonder herkenning.

  Het zoeken van een wedstrijd vergelijkt nu op **exacte** namen in plaats van op "bevat". Daardoor kan "JO13-1" niet langer ook "JO13-10" raken.
- **Teamaanduidingen die het systeem zelf niet kon plaatsen werden nergens vastgelegd.** De pagina Beheer → Teamaliassen bleef daardoor altijd leeg, en voor elke terugkerende afwijkende schrijfwijze werd opnieuw een AI-keuze gemaakt. Die keuzes worden nu vastgelegd zodat u ze één keer kunt goedkeuren.
- **Een veld werd als kunstgras of gras beschouwd op basis van zijn nummer** in plaats van op het ingestelde type (#705). Bij een club met natuurgras op de lage nummers werden daardoor juist de kunstgrasvelden uit het antwoord weggelaten. Er is nu één definitie van "kunstgras" voor het hele systeem, en een veld waarvan het type niet duidelijk is wordt nooit weggelaten.
- **Bij tien of meer velden, of bij een veldnaam langer dan zes tekens, kon dezelfde tijd op hetzelfde veld twee keer worden ingepland** (#707, #719). De bezetting werd bijgehouden op een afgekorte veldnaam, waardoor "veld 10" als "veld 1" werd geregistreerd en veld 10 vrij leek. Bij het zoeken naar een nieuw tijdstip wordt de eigen wedstrijd nu uitgesloten op wedstrijdnummer, wat ongevoelig is voor de veldnaam.
- **De e-mailtemplates volgden de clubkeuze bovenin het scherm niet** (#706). Bij een test met de demoklub werden de templates van de echte club gebruikt. Bijkomend en zwaarder: de templates werden gedeeld tussen clubs in het geheugen bewaard, waardoor de ene club de templates van de andere kon krijgen.
- **Een net uitgesloten e-mailadres kon nog naar de AI-dienst worden gestuurd** (#709). De uitsluitingslijst werd alleen bij een koude start vernieuwd; nu geldt een geldigheidsduur van vijftien minuten. Een antwoord kreeg zo'n adres al niet, want die controle gebeurde altijd op een verse lijst.
- **De veldenlijst werd niet per club gefilterd** (#707), waardoor bij twee clubs met dezelfde veldnaam de bezetting naar een verkeerd veldnummer kon wijzen.
- **Een lege clubcode werd niet als ontbrekend gezien** (#707). Daardoor kon de uitsluitingslijst leeg opgeleverd worden en werden uitgesloten adressen alsnog verwerkt.
- **De documentatie van de e-mailverwerking beschreef functies die niet bestaan** (#708): een template met bijlagen en plaatshouders die er nooit waren, een voorfilter op intern domein, en een instelling voor de teamherkenning die inmiddels verwijderd is. De werkelijke lijst ondersteunde plaatshouders staat er nu in.

- **Een e-mail die niet verstuurd kon worden, kreeg daarna nooit meer antwoord** (#712). Mislukte het versturen — bijvoorbeeld door een tijdelijke storing bij de mailprovider — dan bleef het bericht ongelezen staan "voor de volgende ronde", maar bij die volgende ronde werd het juist definitief overgeslagen omdat er al een regel in het logboek stond. De afzender kreeg dus niets, en het bericht verdween uit de wachtrij. Hetzelfde gebeurde bij elke andere fout die ná dat logboekmoment optrad. Er wordt nu gekeken of de verwerking daadwerkelijk is afgerond, en zo niet wordt hij opnieuw uitgevoerd. Om te voorkomen dat één onverwerkbaar bericht eindeloos blijft terugkomen — en daarmee de wachtrij van tien berichten blokkeert — wordt het aantal pogingen bijgehouden en na een paar pogingen opgegeven, met een spoor in het logboek.
- **Een dagnaam in een citaat of ondertekening kon de wedstrijddatum verschuiven** (#692). Stond er onderaan een doorgestuurd bericht "Verzonden: dinsdag 26 mei", dan kon het systeem de gevraagde zaterdag stilzwijgend naar die dinsdag verplaatsen. Aangehaalde tekst en ondertekeningen worden nu niet meer meegelezen, en bij meerdere dagnamen in één bericht wordt de datum ongemoeid gelaten in plaats van gegokt.
- **Bij het beantwoorden van een reply won de oude datum uit de onderwerpregel.** Stond in het onderwerp "Oefenwedstrijd 30 mei" en in het bericht "30 mei kan niet, kan het 6 juni?", dan ging het antwoord opnieuw over 30 mei — elke ronde opnieuw. De datum uit de nieuwe berichttekst is nu leidend. (#692)
- **Een maand zonder jaartal kwam rond de winterstop in het verleden terecht.** "Beschikbaarheid 10 januari", verstuurd in december, werd gelezen als januari van het lopende jaar — elf maanden terug. De afzender kreeg dan automatisch te horen dat de datum in het verleden ligt. Er wordt nu het eerstvolgende voorkomen van die datum gekozen. (#692)
- **"Doordeweeks" overschreef een concreet gevraagde datum** met vier dagen, waarvan bij een bericht laat in de week een deel al verstreken was. Er wordt nu alleen naar de hele week gekeken als er géén concrete datum in het bericht staat, en verstreken dagen vallen weg. (#692)
- **Een verzoek zonder herkenbare datum leverde een antwoord met een interne foutmelding** ("Ongeldige datum: ."). Zo'n verzoek krijgt nu een net antwoord met de vraag om een concrete datum. (#692)
- **De vraag "wie is de trainer van [team]?" vond in de praktijk nooit een contactpersoon** (#710). Het zoekfilter verwachtte functietitels als "Trainer" of "Coach", terwijl de Sportlink-export stafcategorieën gebruikt ("Technische staf"). Daardoor werkte deze functie alleen op de demogegevens. Bovendien kon een gedeeltelijke naamvergelijking het bericht bij de begeleider van een ánder team afleveren ("JO13-1" matchte ook "JO13-10"), en werd niet op club gefilterd — waardoor demo- en productiegegevens door elkaar konden lopen. Alle drie zijn opgelost en tegen echte gegevens gecontroleerd.
- **De auditcopie bij het doorsturen van een contactvraag werd nooit verstuurd** (#712). De code verwees naar een instelling die niet bestaat, waardoor de meelees-copie naar de coördinator stilzwijgend wegviel bij berichten die persoonsgegevens bevatten.
- **De testmodus liet niets zien om te beoordelen** (#712). In die modus werd wél alle uitgaande post geblokkeerd, maar het voorgestelde antwoord werd niet opgebouwd en niet opgeslagen — er was dus niets om te reviewen. Het voorstel wordt nu bewaard en als zodanig gemarkeerd.
- **Het herstelscript voor de lokale database gebruikte de verkeerde tijdzone.** Bij het toevoegen van een ontbrekende kolom werd de lokale servertijd als standaardwaarde ingesteld in plaats van de wereldtijd (UTC). Dat is precies de fout die eerder tot tijdstippen in de toekomst leidde: de tijd wordt twee keer opgeteld en een sinds-melding komt dan later uit dan "nu". Alleen ontwikkelomgevingen waren geraakt, niet de productiegegevens. (#684)
- **De schemacontrole van het ontwikkelscript keek acht kolommen over.** De lijst met te controleren kolommen werd met de hand bijgehouden naast de echte schemabestanden, en liep daarop achter — onder meer de kleurinstellingen van het thema en de aan/uit-schakelaar voor de Sportlink-koppeling werden nooit gecontroleerd. Het script leest de kolommen nu uit de schemabestanden zelf en meldt het expliciet wanneer beide uit elkaar lopen. (#684)
- **Het stoppen van de lokale ontwikkelomgeving liet processen achter.** De achtergrondwaakhond die de beheeromgeving automatisch herlaadt bleef leven en startte de webserver meteen opnieuw op, waardoor een herstart kon mislukken met een onduidelijke foutmelding over een bezette poort. (#684)
- **Een ingesteld voorkeursveld had geen enkel effect op de planning.** De regel kon worden ingevoerd en werd in het overzicht getoond, maar de planner las hem nooit uit — de onderliggende query filterde het regeltype weg. Voorkeursvelden worden nu toegepast, als eerste laag vóór de voorkeurstijden. Het blijft een zachte voorkeur: zit het veld vol, dan kiest de planner een ander veld en is dat in het overzicht te zien. (#666)
- **Een groene "OK" betekende niet dat de wedstrijd op de gewenste tijd stond.** Die melding keek alleen of de planner de wedstrijd verplaatste ten opzichte van de huidige stand. Een wedstrijd die bleef staan kreeg dus "OK", ook bij een uur afwijking. Het overzicht heeft nu twee gescheiden kolommen: één voor de wijziging, één voor de voorkeurstijd. (#666)
- **In de testmodus werden teamregels stilzwijgend genegeerd.** Buffers en voorkeursvelden van de democlub werden niet gebruikt omdat de planner die regels bij de productieclub ophaalde — juist in de modus die bedoeld is om te testen. (#666)
- **Een voorkeurstijd vóór 09:00 werd altijd naar 09:00 opgeschoven,** ook als het veld al eerder open was. De vroegste starttijd volgt nu de ingestelde veldbeschikbaarheid in plaats van een vaste waarde in de code. (#666)
- **De ingestelde ruimte tussen wedstrijden werd niet aangehouden.** Wedstrijden werden rug-aan-rug op hetzelfde veld gezet met nul minuten ertussen, en ook de teamspecifieke regels — zoals een uur vrij vóór het eerste elftal — werden overgeslagen. De helft die op een voorkeurstijd plant keek alleen of het veld vrij was, niet of de buffer paste. Beide zoekpaden gebruiken nu dezelfde controle. Wedstrijden die tegelijk náást elkaar op een half of kwart veld staan houden geen buffer nodig; die worden nog steeds naast elkaar ingedeeld. (#666)
- **De indeling van een veld in helften en kwarten werd geraden in plaats van bijgehouden.** De planner kende een plek toe op basis van hóeveel wedstrijden er al gelijktijdig op het veld stonden — de eerste kreeg het eerste kwart, de tweede het tweede, en zo verder. Daardoor kon een halveveldwedstrijd op de ene helft samenvallen met een kwartveldwedstrijd op precies dat stuk, en werd een vrij kwart overgeslagen terwijl er een bezet stuk werd gekozen. Op papier paste het (de oppervlaktes telden op tot maximaal één veld), in de praktijk stonden twee teams op hetzelfde gras. Een veld wordt nu bijgehouden als vier kwartbanen, en er wordt gekeken welke daarvan op dat moment werkelijk vrij zijn. (#666)
- **Alle velden vielen samen op één rij in de tijdlijnen.** De weergave zag het veldnummer aan voor een positie-aanduiding binnen een veld, waardoor "Kunstgras 1" en "Kunstgras 2" onder dezelfde noemer belandden en alle wedstrijden over elkaar heen werden getekend. Elk veld heeft nu zijn eigen rij. (#665)
- De handleidingen beschreven een verouderd navigatiepad voor de Sportlink-export en verwezen naar een handleidingbestand dat niet meer bestaat. Ook stond er dat een import de tabel volledig leegt, terwijl uitsluitend de gegevens van de eigen club worden vervangen — bij meerdere clubs in één database was die beschrijving misleidend. (#667, #668)
- **De Email-tester negeerde de gekozen club in de schakelaar bovenin.** Stond die op de demo-club, dan werd de tekst nog altijd met de echte club-code voorvoegd — waardoor de planner nooit een testwedstrijd terugvond — en toonde het voorbeeldantwoord de echte afzendernaam en coördinator van de club in plaats van demo-gegevens. De tester haalt nu de instellingen op van de club die in de schakelaar staat, in plaats van altijd de instellingen van de echte club te gebruiken. De echte, geautomatiseerde e-mailverwerking (zonder schakelaar) is ongewijzigd. (#677)
- **Een testwedstrijd die via het scherm Testdata werd aangemaakt of gewijzigd, werd door de planner nooit teruggevonden — ook niet als team en datum kloppend leken.** Twee samenlopende oorzaken: (1) het opslaan van een testwedstrijd vulde het accommodatieveld niet, terwijl elke wedstrijd-lookup daarop filtert; en (2) die filter las de accommodatienaam altijd uit de instellingen van de echte club, nooit uit die van de demo-club — een gevolg van #677 dat toen niet volledig was doorgetrokken naar de planner-lookups zelf. Beide zijn nu gerepareerd: testwedstrijden krijgen automatisch de juiste accommodatie mee, en elke lookup gebruikt de instellingen van de club waarvoor gezocht wordt. Daarnaast herkent de planner een team nu ook als de classificatie een ander scheidingsteken gebruikt dan de brondata (bijv. "MO13-1" versus "MO13 1"). (#694)
- **Een verzoek dat het systeem niet kan classificeren, laat de wachtrij niet meer vastlopen.** Ontbrak er een veld in het antwoord van het taalmodel, dan liep de verwerking van dat bericht op een fout — en omdat de wachtrij de oudste berichten eerst pakt, konden een paar zulke berichten alle nieuwe post blokkeren terwijl er wel kosten werden gemaakt. Ontbrekende velden worden nu veilig opgevangen. (#712)
- **Bij tien of meer velden, of een veldnaam langer dan zes tekens, kon dezelfde tijd op hetzelfde veld twee keer worden ingepland** (#719, restant van #707). Het real-time pad was al gerepareerd; het pad dat de bezetting uit de database haalt niet. Daar werd de veldnaam op zes tekens afgekapt: een wedstrijd op "veld 10" werd geregistreerd als bezetting op "veld 1", waardoor veld 10 de hele dag vrij leek en er een tweede wedstrijd bij kon — terwijl veld 1 juist onterecht dicht leek te zitten. Een veldnaam als "hoofdveld" viel volledig uit de bezetting weg. Alle plekken gebruiken nu dezelfde herkenning: de exacte veldnaam, of de veldnaam gevolgd door een positie-aanduiding, waarbij de langste naam voorgaat. Er is een controle toegevoegd die faalt zodra de drie plekken uit elkaar gaan lopen — de vorige poging strandde daar juist op.
- **Een reply die later dan 30 dagen binnenkwam, werd niet meer als reply herkend — en leverde dus geen leermoment op** (#718). Het systeem gebruikte het e-mailadres van de ontvanger om te bepalen of het zelf op een bericht had geantwoord. Dat adres is een persoonsgegeven en wordt na 30 dagen automatisch gewist, waardoor die kennis verdween. Bij een verzoek dat weken vooruit ligt is een reactie na een maand volkomen normaal, dus viel het zelflerende deel van het systeem stil precies waar het het meest nodig was — zonder dat daar iets van te zien was. Het feit *dat* er geantwoord is, wordt nu apart vastgelegd en blijft bewaard; het adres zelf wordt nog steeds na 30 dagen gewist.
- **Een onderbroken verwerking kon een tweede antwoord naar dezelfde afzender sturen** (#716). Het antwoord werd verstuurd en pas daarna vastgelegd. Werd de verwerking daar precies tussenin afgebroken — een tijdslimiet, een herstart van de server — dan was de mail de deur uit terwijl er in de administratie niets van te zien was, en stuurde de volgende ronde hetzelfde antwoord nog eens. Er wordt nu vóór het versturen vastgelegd dat er verstuurd gáát worden. Blijkt daarna dat de uitkomst onbekend is, dan wordt er niet opnieuw verstuurd maar krijgt het bericht de status "ter beoordeling", zodat de coördinator zelf kan vaststellen of het antwoord is aangekomen.
- **Dezelfde correctie kon tot drie keer als leermoment in de lijst komen** (#715). Als de verwerking van een bericht opnieuw werd geprobeerd, werd ook de correctie opnieuw vastgelegd. De beheerder moest die duplicaten allemaal apart goedkeuren, en meervoudig goedgekeurd woog hetzelfde voorbeeld zwaarder mee in de AI-beoordeling dan bedoeld. Er kan nu maar één leermoment per correctie bestaan; bestaande duplicaten worden bij de update opgeruimd (de oudste blijft staan).
- **Een foutmelding kon de administratie van een geslaagd antwoord overschrijven** (#717). De foutregistratie zocht het bericht op een ander kenmerk dan alle overige bewerkingen. Liepen twee verwerkingsrondes langs elkaar, dan kon een bericht waarvan het antwoord juist wél verstuurd was in het logboek als "Fout" belanden — en dat logboek is voor de coördinator het enige spoor. De foutregistratie werkt nu op hetzelfde kenmerk als de rest en laat een bericht waarop al geantwoord is per definitie ongemoeid.
- **Een datum met een afgekorte maandnaam ("22 aug", "24 mrt.") of in slash-notatie met jaartal ("14/02/2026") werd niet herkend als expliciete datum.** Een analyse van de echte inkomende post (#722, uitsluitend geteld — geen inhoud bewaard) liet zien dat deze twee vormen net zo vaak voorkomen als de al ondersteunde volledige maandnaam en streepjesnotatie. Beide worden nu herkend, op dezelfde manier als de bestaande datumformaten: zonder jaartal geldt het eerstvolgende voorkomen. Datum-zonder-jaar in slash-notatie ("13/1") blijft bewust ongeondersteund omdat die vorm niet te onderscheiden is van een teamaanduiding. (#722)

### Security
- **Force pushes naar `main` stonden nog open voor één account — nu voor niemand meer** (#654). De productiegeschiedenis kon daarmee herschreven worden: dan klopt de koppeling tussen een release, de bijbehorende tag en wat er live staat niet meer, en is de audittrail onherstelbaar weg. De instelling stond aan in de variant "alleen deze personen mogen force pushen", en juist die variant maakte hem onzichtbaar: de ene GitHub-API meldde "aan", de andere "uit", en de lijst met wie het mocht was in geen van beide te zien zonder een derde veld op te vragen. Er is meer dan een dag gezocht naar welke API loog — geen van beide, ze beantwoorden een verschillende vraag. Het volledige verificatierecept staat nu in SECURITY.md, samen met de correctie van een passage die beweerde dat een directe push naar `main` "technisch onmogelijk" was. Dat was niet zo. Ook vastgelegd: de nood-procedure om na een datalek de geschiedenis op te schonen vereist deze instelling tijdelijk, en hoe je dat veilig doet.
- **Een externe afzender kon een klikbare link in een doorgestuurd bericht krijgen** (#692). Vragen over teambegeleiding worden vanaf de clubmailbox doorgestuurd naar de betreffende begeleider. De tekst van de afzender werd daarbij als HTML verstuurd zonder te worden ontdaan van opmaak, en de bestaande opschoning kon worden omzeild door de opmaak dubbel te versleutelen. Daarmee kon iemand een phishing-link of een onzichtbare afbeelding in een bericht plaatsen dat van de club leek te komen. De opschoning herhaalt zich nu tot de tekst schoon is, en bij het versturen is alleen een vaste lijst van opmaaktags zonder eigenschappen toegestaan — links, afbeeldingen, scripts en stijlen komen er per definitie niet door. De opmaak van de gewone automatische antwoorden is ongewijzigd.
- **De inhoud van een e-mail kon de instructies aan het taalmodel overrulen** (#692). Afzender, onderwerp en berichttekst gingen zonder scheiding de opdracht in, waardoor een tekst als "negeer voorgaande instructies" invloed kon krijgen op de classificatie — en daarmee op naar wie een bericht wordt doorgestuurd. De berichtinhoud staat nu in een apart gemarkeerd blok met een per keer wisselende, niet te raden markering, met de expliciete instructie dat alles daarbinnen gegevens zijn en nooit een opdracht.

## [2.17.2.1] — 2026-07-26

### Fixed
- **De beheeromgeving was in productie onbruikbaar: de pagina bleef op het laadscherm hangen.** De beveiligingsinstellingen van de website blokkeren scripts die direct in de pagina staan. Twee van zulke scripts waren nodig om de applicatie te starten en om kopiëren, downloaden en de FEEDBACK-knop te laten werken. Beide zijn nu naar losse bestanden verplaatst, waarmee de beveiliging onverkort blijft gelden. Lokaal was dit niet te zien omdat die beveiliging daar niet actief is. (#659)

### Changed
- De bouwstraat controleert nu bij elke wijziging of er scripts direct in de pagina staan. Dezelfde fout kan daarmee niet opnieuw ongemerkt in productie komen. (#659)

## [2.17.2.0] — 2026-07-26

### Fixed
- De melding na een release die aangeeft welke issues nog openstaan, noemde ook issues die in diezelfde release net waren afgerond. Dat kwam doordat het overzicht van GitHub even achterloopt. Er wordt nu gefilterd op wat de release zelf heeft afgehandeld, zodat de melding alleen nog echt achtergebleven issues toont. (#630)

## [2.17.1.0] — 2026-07-26

### Fixed
- **De demo-omgeving (AllStars FC) was leeg.** De democlub bestond wel, maar had geen velden, geen speeltijden, geen teams en geen wedstrijden — de testmodus in de beheeromgeving toonde daardoor een leeg scherm. De demogegevens worden nu bij elke uitrol automatisch aangemaakt: 3 velden met beschikbaarheid, 28 teams met een begeleider, en 224 wedstrijden verdeeld over de acht komende zaterdagen, ongeveer de helft thuis en de helft uit. De speeldata schuiven mee met de uitroldatum, zodat de demo niet na een paar maanden alleen nog verleden wedstrijden toont. Optimaliseren in de Dagplanning verdeelt de wedstrijden nu daadwerkelijk over de drie demovelden. (#635)
- In de testmodus werd bij een uitwedstrijd het eigen team ook als tegenstander getoond ("AllStars JO10 1 – AllStars JO10 1"). Bij een uitwedstrijd staat de tegenstander in het thuisteam-veld; daar wordt nu op gecontroleerd. (#635)
- **Het huidige seizoen werd bij elke uitrol opnieuw aan de seizoenslijst toegevoegd.** In productie stond het seizoen 2026/'27 daardoor drie keer in de lijst, en groeide dat met elke uitrol verder. De gegevenslijst die datums aan seizoenen koppelt gaf daardoor elke datum van dit seizoen drievoudig terug. De planning en de synchronisatie waren hierdoor niet geraakt — die kijken alleen naar de eerste en laatste datum. De dubbele regels zijn opgeruimd en de lijst laat een seizoen nu maar één keer toe. (#631)
- **Issues bleven na een release onterecht als 'wacht op release' openstaan, of kwamen juist weer open te staan nadat ze al waren afgerond.** Bij de vorige release gold dat voor vijf issues: hun nummer stond niet in de commit-tekst, alleen in het wijzigingsoverzicht. Daarnaast werd een al afgerond issue opnieuw geopend zodra een latere wijziging het nummer terzijde vermeldde. Beide zijn verholpen: het wijzigingsoverzicht wordt nu meegelezen bij het afsluiten, een terzijde-vermelding heropent niets meer, en na elke release wordt gemeld welke issues nog openstaan zodat er niets stil blijft hangen. (#630)

### Changed
- Onderhoudsupdates van externe softwarebibliotheken doorgevoerd (Azure Functions worker-SDK en HTTP-uitbreiding, de AI-bibliotheek, en de database-actie in de bouwstraat). Geen functionele wijzigingen; dit houdt het systeem bij op beveiligings- en foutherstel van de leveranciers. (#634)
- De controle vóór het versturen van wijzigingen is sterk versneld: van ruim negen minuten naar negen seconden bij een release. Voorheen liep die zo lang dat hij op een vastloper leek — met het risico dat iemand hem zou omzeilen. De controle is even grondig als voorheen. (#636)

### Security
- Het goedgekeurde fictieve demodomein wordt niet langer door de eigen beveiligingscontroles geblokkeerd. Het domein van de demogegevens is opgenomen in de uitzonderingslijsten, zodat documentatie over de demo-omgeving niet onterecht als privacyschending wordt gemeld. De controles zelf zijn niet verzwakt. (#649)
- De controle vóór het versturen van wijzigingen meldt nu expliciet wanneer het aanvullende scanprogramma niet op de machine staat. Voorheen werd die stap stil overgeslagen, waardoor een ontwikkelaar dacht meer bescherming te hebben dan er was. (#649)
- **Tijdelijke toegangsregels van de bouwstraat werden nooit opgeruimd.** Bij elke deploy krijgt de bouwserver kortdurend toegang tot de database; die toegang moest daarna weer worden ingetrokken. Door een fout in het opruimcommando gebeurde dat nooit, terwijl de stap wél een vinkje gaf — er stonden 15 verouderde toegangsregels open. Het commando is gecorrigeerd en fouten worden nu zichtbaar gemeld in plaats van weggeslikt. (#632)

## [2.17.0.0] — 2026-07-26

### Added
- KNVB-speeldagenkalender voor seizoen 2026/'27 is opgenomen in de database voor **alle zes districten**: West, Noord, Oost, Zuid, Landelijk en Landelijk jeugd. Per speeldatum is nu bekend of het een competitie-, beker-, inhaal-, nacompetitie- of vrije dag is, welke leeftijdscategorieën actief zijn, en welke schoolvakanties of feestdagen spelen. Clubs buiten district West kunnen de kalender daarmee ook gebruiken.
- Dagplanning toont nu direct de wedstrijden die op de gekozen datum gepland staan — zodra je een andere datum kiest, wordt dit meteen bijgewerkt zonder dat je eerst op "Optimaliseer" hoeft te klikken. (#566)
- Het AI-model is nu instelbaar via de app-instelling `AiModelName`. Een model-upgrade vereist daarmee geen nieuwe versie van de software meer. (#604)
- Elke pull request bouwt nu automatisch beide projecten en controleert of het databaseontwerp en het migratiescript nog gelijk lopen. Fouten worden zo bij het voorstellen van een wijziging gemeld in plaats van pas bij een deploy. (#599, #595)
- Verlopen KNVB-verplaatsingsregels worden nu actief gemeld in de logging, en het AI-model geeft in dat geval geen KNVB-waarschuwing meer af op basis van verouderde deadlines. Voorheen verouderden die regels stilzwijgend. (#608)
- GitHub-issues sluiten niet langer voortijdig: zodra een fix naar `develop` merget krijgt het gekoppelde issue automatisch het label `status: awaiting-release` (en wordt heropend als het al gesloten was). Pas bij de daadwerkelijke release naar productie (`main`) sluit het issue automatisch. (#615)

### Fixed
- **De FEEDBACK-knop werkte niet in de live omgeving.** Het venster liep vast met "An unhandled error has occurred" doordat de beveiligingsinstellingen van de website een techniek blokkeerden die het venster gebruikte om de paginanaam en browserversie op te halen. Lokaal was dit niet te zien omdat die beveiliging daar niet geldt. (#597)
- **Een nieuwe installatie van het systeem was onbruikbaar:** twaalf database-onderdelen — waaronder alle vier de procedures die de Sportlink-synchronisatie uitvoeren, de koppeltabel die de synchronisatie stuurt en drie tabellen die door de planner en de teambegeleiding-import worden gebruikt — werden nooit aangemaakt. Clubs die het systeem overnamen kregen daardoor foutmeldingen over ontbrekende tabellen en een synchronisatie die niets deed. Alle onderdelen worden nu bij elke deploy aangemaakt. (#595)
- **De weekendmarkering in de datumtabel stond verkeerd:** vrijdag werd als weekend gemarkeerd en zondag niet. Ook liep de dagnummering één dag voor op wat de documentatie aangaf. Beide zijn gecorrigeerd. (#610)
- De clubnaam stond nog als standaardwaarde in vijf database-onderdelen, wat de ondersteuning voor meerdere clubs doorbrak. De standaardwaarden zijn verwijderd; de club wordt nu altijd expliciet vastgelegd. Ook de initiële voorbeeldgegevens (velden, beschikbaarheid, teamregels) gebruiken nu geen clubnaam meer. (#598)
- Ontbreekt de clubinstelling, dan geeft het systeem nu een duidelijke fout in plaats van een lege clubkoppeling weg te schrijven in de planning. Zo'n lege koppeling was daarna niet meer te herstellen. (#600, #601)
- Een tijdveld negeerde opmaak-instellingen zoals de kolombreedte stilzwijgend; die worden nu wel toegepast. (#602)
- Ontbreekt de instelling voor de GitHub-repository, dan meldt het systeem dat nu als configuratiefout in plaats van een onduidelijke "niet gevonden"-melding te geven. Dit raakt clubs die het project onder een andere naam overnemen. (#607)
- Een gepauzeerde database blokkeert de bouwstap niet langer. Voorheen werden daardoor alle controles overgeslagen en konden programmeerfouten wekenlang onopgemerkt blijven. Deployen naar een gepauzeerde database blijft geblokkeerd. (#599)
- Testdata: het auto-invullen van het uitteam bij selectie van een thuisteam respecteert nu de ingestelde 'Tegenstander (nieuw)' — was voorheen hardcoded op 'FC Onbekend' ook als de gebruiker iets anders had ingevuld. (#498)
- **Een release liep vast zodra de database in slaapstand stond.** De controle die de database wakker maakt vóór een deploy legde alleen een netwerkverbinding aan; die wordt door Azure afgevangen vóórdat de database hem ziet, waardoor de database bleef slapen en de release na vijf minuten afbrak. Er wordt nu daadwerkelijk ingelogd op de database — dat is wat het wakker worden in gang zet. (#624)

### Changed
- De API-documentatie (`openapi.yaml`/`.json`) is bijgewerkt naar de huidige versie en bevat nu ook de twee endpoints voor het importeren van teambegeleiding en het verschuiven van testwedstrijden. (#605)
- De geschiedenistabellen hebben nu een index op hun sleutel en op de clubkoppeling. Dat versnelt zoekopdrachten en voorkomt dubbele rijen. Waar de Sportlink-gegevens nu al dubbelingen bevatten (zie #569) wordt de index zonder uniciteitseis aangemaakt, zodat een deploy niet vastloopt. (#606)
- Senioren-categorieen uit Sportlink worden weer correct herkend in plannerberekeningen: `Senioren` normaliseert nu naar speeltijdsleutel `1-99` en `Senioren Vrouwen` naar `VR`. Daardoor vallen seniorenwedstrijden niet meer onterecht in `onbekend-team` bij Optimaliseer/Auto-plan. (#591)
- De databasemigratie liep bij elke deploy vast zodra er meer dan één club in de instellingen stond — wat altijd het geval is doordat de AllStars FC democlub wordt aangemaakt. Gevolg: het nieuwe seizoen werd niet meer automatisch aangemaakt en een deel van de migratie werd overgeslagen. Beide zijn verholpen.
- De kolom die geplande wedstrijden aan een club koppelt werd door een fout in het migratiescript nooit aangemaakt. Daardoor ontbrak de scheiding tussen productie- en demogegevens voor geplande wedstrijden. De migratie werkt nu.
- Nieuw gesynchroniseerde teams en wedstrijden werden niet meer aan de eigen club gekoppeld, waardoor ze onterecht niet zichtbaar waren in de Dagplanner en de teamlijst — ook al stonden ze al in Sportlink gepland. Nieuwe rijen krijgen deze koppeling nu weer automatisch bij elke synchronisatie. (#567)
- Bij het zoeken naar een ander tijdstip voor een bestaande wedstrijd werd die wedstrijd op naam uit de bezetting gefilterd. Daardoor kon per ongeluk een ándere wedstrijd wegvallen (wedstrijdcode 123 komt ook voor in 3123), met verkeerde tijdsloten als advies. Er wordt nu op de exacte wedstrijdcode gefilterd. (#574)
- Beschikbaarheids- en herplanchecks haalden de teamregels per team afzonderlijk uit de database. Op een drukke zaterdag waren dat tientallen losse databasevragen. Dit gebeurt nu in één keer — sneller antwoord en minder verbruik op de gratis databaselimiet. (#575)
- De documentatie noemde .NET 10 als runtime voor de Azure Functions, terwijl die versie op het gratis hostingplan niet werkt. Bijgewerkt naar de werkelijke versie, met een expliciete waarschuwing dat upgraden een betaald plan vereist. (#579)

- `EmailProcessorFunction` is opgesplitst in testbare services (`EmailBatchFilterService`, `EmailClassificationService`, `EmailPersistenceService`, `EmailReplyPolicyService`) met nieuwe interfaces voor Graph- en persistence-koppelingen. Het gedrag blijft gelijk, maar de orkestratie is nu dunner en eenvoudiger te onderhouden en testen. (#577)
- **De coördinator krijgt geen automatische mail meer als een wedstrijd gewoon ingepland kan worden.** Een automatisch antwoord is er om een blokkade te melden: is er ruimte op de gevraagde datum, dan volgt geen mail en plant de coördinator handmatig in. Bij meerdere gevraagde datums met een gemengde uitkomst gaat er wél een antwoord uit, met per datum wat wel en niet kan. Deze berichten krijgen in Outlook het label 'Handmatige planning' en zijn terug te vinden onder Instellingen → Email verwerking. (#572)
- De waarschuwing bij doordeweekse aanvragen noemde vaste veldnummers ("alleen veld 5 beschikbaar"). Dat klopt niet voor elke club en elk seizoen. De tekst verwijst nu naar de ingestelde veldbeschikbaarheid. (#576)
- De KNVB-verplaatsingsregels waarop de AI herplanverzoeken beoordeelt zijn bijgewerkt naar seizoen 2026/'27. Verzoeken worden niet langer getoetst aan de verlopen deadlines van seizoen 2025/'26. Nieuw toegevoegd: de seizoensdata (competitiestart, winterstop, laatste speelronde, nacompetitie) en de verplaatsingsdeadlines voor de landelijke divisies.
- Review mode stuurt geen email meer terug aan de coördinator — in plaats daarvan wordt de originele email gemarkeerd met 'Geen AI antwoord' zodat de coördinator deze handmatig kan afhandelen. Interne notificaties (teamleider, team-contact) worden ook onderdrukt tijdens review mode.

### Security
- Planning- en bezettingsgegevens van andere clubs — inclusief de AllStars FC demodata — konden in zoekresultaten en in de planningsbeslissing van de eigen club terechtkomen. Alle planner- en bezettingsvragen zijn nu hard afgebakend op de eigen club: wedstrijden zoeken, teamconflicten, veldbezetting, teamrooster en het markeren van vervallen wedstrijden. (#573, #580)
- Het voorbeeldvenster van de klassieke dagplanning draait nu in een afgeschermde omgeving zonder scriptuitvoering. Extra bescherming tegen kwaadaardige inhoud in namen die uit Sportlink komen. (#603)
- `populate-sunset` endpoint gebruikt nu Easy Auth + RequireAdmin in plaats van function-key authenticatie — brengt het endpoint in lijn met alle andere admin-endpoints en voegt een Entra-audittrail toe. (#495)
- Log-aanroepen in Utilities.cs en MergeStgToHis.cs gebruiken nu structured logging (`LogError(ex, "...")`) in plaats van string-interpolatie met `ex.Message` — voorkomt dat infrastructuurdetails (servernaam, gebruikersnaam) als losse string in Application Insights terechtkomen. (#496)
- Email-log API (`GET /api/beheer/email-log`) retourneert het `FoutMelding` veld niet meer — dit veld kan voor records jonger dan 30 dagen resterend PII bevatten dat nog niet is geanonimiseerd. (#513)
- `GitHubIssueReporter` en `FeedbackFunction` geven geen stille fallback meer naar een hardcoded repo-naam als `GitHubRepo` niet geconfigureerd is — misconfiguratie wordt nu gedetecteerd en gelogd. (#532)
- `AutoPlanService` stuurt `ex.Message` niet meer door in de admin-response bij mislukte wedstrijdtoepassing — technische details worden gelogd, de client ontvangt alleen een generieke melding. (#520)
- `SportlinkSyncPipeline` logt bij JSON-deserialisatiefouten alleen het uitzonderingstype, niet `ex.Message` — voorkomt dat Sportlink-data met spelernamen/namen in logs verschijnt. (#535)
- `AdminSettingsFunction` valideert veldnamen na de whitelist ook op alfanumerieke tekens — defense-in-depth bovenop de bestaande whitelist. (#515)
- Test-afzenderadressen geharmoniseerd naar goedgekeurde AVG-placeholder `trainer@voorbeeld.nl` in EmailTestFunction, smoke-test.ps1 en documentatie. (#534, #538, #533)
- Hardcoded club-locatiepad vervangen door generieke placeholder in setup-script. (#517)
- CI deploy-workflow: database-resume gebruikt nu TCP-verbindingspoging (triggert Azure SQL Serverless auto-resume) in plaats van REST API-aanroep waarvoor onvoldoende RBAC beschikbaar was — lost structureel op dat deploys blijven hangen bij gepauzeerde database.
- `Start-Debug.ps1` toont nu een waarschuwing als `.githooks/sensitive-patterns.txt` ontbreekt op de ontwikkelmachine. (#514)
- CSP `connect-src` in `staticwebapp.config.json` gebruikt geen wildcard `*.azurewebsites.net` meer — bij deploy wordt de waarde vervangen door de specifieke URL van de eigen Function App via CI-substitutie. (#528)
- `DEFAULT 'VRC'` verwijderd uit drie tabeldefinities (`AppSettingsAudit`, `TeamVoorkeurTijden`, `UitgeslotenEmailAdressen`) — vervangen door `CHECK (LEN([ClubCode]) > 0)` conform het patroon van issue #242. Voorkomt stille datavervuiling bij multi-club deployments. (#501)
- `Test-App.ps1`: schema-repair voor ontbrekende `ClubCode` kolom gebruikt niet meer `DEFAULT 'VRC'` maar `DEFAULT ''` — zelfde principe als boven. (#501)

## [2.16.0.0] — 2026-06-01

### Security
- Beveiligingsgat in het automatisch ophalen van club-thema via externe URL gedicht — alleen URL's die overeenkomen met de geconfigureerde clubwebsite worden nog toegestaan. Elimineert risico op SSRF/DNS-rebinding volledig.
- Foutmeldingen in de email-verwerkingslog worden na 30 dagen geanonimiseerd — voorkomt dat technische foutdetails (die mogelijk persoonsgegevens kunnen bevatten) langer dan nodig bewaard blijven.
- Email-uitsluitingslijst wordt nu vóór de eerste AI-classificatie geladen (fail-closed) — als de database niet bereikbaar is bij opstarten, worden e-mails niet naar de AI gestuurd.
- Noodmeldingen bij database- of quota-fouten bevatten geen ruwe technische details meer — alleen een categorienaam.
- Classificatiecorrecties worden na 30 dagen geanonimiseerd en na 90 dagen verwijderd.

### Added
- Testdata: nieuwe knop "Verplaats datum" op de Wedstrijden-pagina — verplaatst alle ALLSTARS-testwedstrijden van een gekozen datum naar een andere datum in één klik. Handig voor het testen van de planner met realistische toekomstige wedstrijden.

### Fixed
- Planner: teams voor wie de leeftijdscategorie niet herkend wordt (bijv. toernooicommissie) worden neutraal weergegeven in de dagplanning — niet langer rood gemarkeerd als probleem.
- Planner: leeftijdscategorie 'JO15 Meiden' wordt nu correct herkend als MO15.
- Spelersfilter en teamregels filteren nu op ClubCode — ALLSTARS-testdata kon voorheen doorlekken in planner-berekeningen van de primaire club.
- Matchdetails-fouten (HTTP-fout of onleesbare data) leiden nu tot een partieel-fout-markering zodat de synchronisatietijdstempel niet wordt bijgewerkt als gegevens onvolledig zijn.

## [2.15.0.0] — 2026-06-01

### Changed
- PlannerService.cs refactored naar dunne facade (2118 → 50 regels) — alle logica verplaatst naar vijf use-case services in `FunctionApp/Planner/Services/`: `AvailabilityService`, `AutoPlanService`, `OptimizationService`, `RescheduleService`, `TeamScheduleService`. Gedeelde utilities en `FieldScheduler` in `PlannerShared`. Bestaande callers ongewijzigd. (#475)

## [2.14.1.0] — 2026-06-01

### Fixed
- Planner: leeftijdscategorie 'JO15 Meiden' wordt nu correct herkend als MO15 — alle normalisatie-queries gebruiken de nieuwe centrale `LeeftijdNormalisatie`-helper. (#486)
- Planner: teams zonder bekende leeftijdscategorie (bijv. 'Toernooi commissie') worden neutraal weergegeven in de dagplanning in plaats van rood 'Probleem'. Hun tijdslot blijft geblokkeerd voor de optimizer; herplannen en toepassen slaan ze over. (#487)

## [2.14.0.0] — 2026-06-01

### Changed
- `PlannerDataAccess.cs` refactored naar dunne facade (1039 → 100 regels) — alle SQL verplaatst naar vier repository-klassen in `FunctionApp/Planner/Repositories/`: `PlannerSettingsRepository` (10 methoden), `PlannerAvailabilityRepository` (4), `PlannerMatchRepository` (11), `TeamRulesRepository` (2), `AllstarsTestDataRepository` (4). Bestaande callers zijn ongewijzigd. (#474)

## [2.13.0.0] — 2026-06-01

### Added
- Testproject `FunctionApp.Tests` uitgebreid: 13 nieuwe tests voor P1-fixes — `EmailSanitizerTests` (e-mail masking, truncation), `MatchDetailsFetchTests` (HTTP-fout en JSON-deserialisatiefout geven false terug zodat partialFailure correct wordt gezet). Integratietests voor DB-afhankelijke scenarios toegevoegd als Skip-stubs. (#476)
- `EmailSanitizer` utility-klasse geëxtraheerd uit `EmailProcessorFunction` — e-mail masking logica is nu los testbaar.
- `FetchAndStoreMatchDetailsAsync` accepteert optionele `HttpClient`-parameter voor testinjectie.

## [2.12.0.0] — 2026-05-31

### Changed
- EmailProcessorFunction: alle SQL-operaties verplaatst naar `EmailProcessingRepository` en `LearningMomentRepository`. Function-klasse bevat geen inline SQL meer; private methoden zijn dunne delegating wrappers. Orchestrator/mailbox/notification splits volgen in vervolg-iteratie. (#465)

## [2.11.0.0] — 2026-05-31

### Changed
- Sportlink-sync refactored: `Function1.cs` is nu een dunne trigger-wrapper. Orchestratie-logica verplaatst naar `SportlinkSyncPipeline`; SQL-staging naar `SportlinkStagingRepository`. Gedeelde match-velden geëxtraheerd in helper-methode (minder duplicaat-code). (#466)

## [2.10.0.0] — 2026-05-31

### Changed
- Admin API-endpoints gebruiken nu een gedeelde `AdminEndpoint.ExecuteAsync` wrapper — auth guard, correlatie-ID en foutafhandeling zijn eenmalig gedefinieerd en kunnen niet vergeten worden bij nieuwe endpoints. (#467)
- SQL voor Speeltijden, UitgeslotenEmail, VeldBeschikbaarheid, VoorkeurTijden, TeamRegels, Leermomenten, Teams, Clubs en EmailLog verplaatst naar aparte repository-klassen in `FunctionApp/Admin/Repositories/`. (#467)

## [2.9.1.0] — 2026-05-31

### Fixed
- `LaadUitgeslotenAdressenAsync` inslikt exceptions niet langer — bij DB-fout op cold start wordt AI-verwerking nu correct uitgesteld (fail-closed garantie hersteld). (#463)
- Matchdetails-fouten (HTTP-fout of JSON-deserialisatiefout) zetten nu `partialFailure = true` — `LastSyncTimestamp` wordt niet bijgewerkt als matchdetails deels mislukken. Logs tonen succesvol/mislukt-tellingen. (#464)
- `GetSpeeltijdenLookupAsync`, `GetTeamRulesAsync`, `GetAllTeamBuffersAsync` en `GetTeamLeeftijdLookupAsync` filteren nu op `ClubCode` — ALLSTARS-testdata lekt niet meer door in planner-berekeningen. (#469)

## [2.9.0.0] — 2026-05-31

### Added
- Testdata: knop "Verplaats datum" op de Wedstrijden-pagina — verplaatst alle ALLSTARS-wedstrijden van een gekozen datum naar een nieuwe datum in één klik. (#459)

## [2.8.0.0] — 2026-05-31

### Security
- SSRF-bescherming `POST /api/beheer/theme/extract` vervangen door domein-allowlist op basis van `ThemeClubWebsiteUrl` uit AppSettings. Elimineert TOCTOU/DNS-rebinding volledig — geen DNS-lookup meer nodig. (#422, sluit ook #421)
- `sp_CleanupEmailVerwerking` fase-1 anonimisering uitgebreid met `[FoutMelding] = NULL` — voorkomt dat exception-tekst (mogelijk met PII) langer dan 30 dagen bewaard blijft. (#420)
- `UpdateFoutAsync` slaat foutmeldingen nu op via `SanitizeFoutMelding()` — verwijdert e-mailadressen en knipt af op 200 tekens vóór DB-opslag. (#420)
- Uitsluitingslijst wordt nu geladen vóór eerste AI-classificatie (fail-closed). Op cold start wordt de database gewekt en de lijst opgehaald; lukt dat niet, dan worden mails niet naar AI gestuurd. (#423)
- Noodmails (database + OpenAI quota) bevatten geen ruwe `ex.Message` meer — worden vervangen door privacy-safe foutcategorie via `CategorizeerFout()`. (#425)
- Nieuwe `sp_CleanupClassificatieCorrectie`: anonimiseert samenvattingen na 30 dagen, verwijdert na 90 dagen. Lost FK-blokkade op die de EmailVerwerking-cleanup kon laten falen. Aanroepvolgorde geborgd: correcties vóór email-verwerking. (#424)
- Nieuwe `sp_CleanupImportLog`: anonimiseert `ImporterendeDoor` + `CsvBestand` na 90 dagen, verwijdert rijen na 1 jaar. Opgenomen in de maandelijkse teambegeleiding-cleanup. (#426)
- Feedbackwidget blokkeert nu submissions met e-mailadressen of telefoonnummers (HTTP 422) vóór GitHub-publicatie. Overzichtsstap toont waarschuwing over publieke GitHub-publicatie. (#427)
- `Function1.cs` logt geen volledige Sportlink API-URLs meer — `clientId=` queryparameter verdwijnt uit Application Insights logs. (#436)
- `GitHubIssueReporter.SanitizeForPublic` redigeert nu ook URL query-parameters (`clientId`, `code`, `token`, `key`, `secret`). (#436)
- `deploy.yml`: `cat appsettings.Production.json` verwijderd — tenant/client IDs verschijnen niet meer in workflow-logs. (#437)
- 7 planner-endpoints gemigreerd van `AuthorizationLevel.Function` naar `Anonymous` + `EasyAuthHelper.RequireAdmin()`: CheckAvailability, DoordeweeksBeschikbaar, BevestigWedstrijd, ZoekWedstrijd, HerplanCheck, HerplanBevestig, GetTeamSchedule. (#433)
- Bootstrap Icons gehost vanuit `lib/bootstrap-icons/` — externe CDN (cdn.jsdelivr.net) verwijderd uit index.html. (#434)
- `staticwebapp.config.json`: Content-Security-Policy, Referrer-Policy, X-Content-Type-Options en Permissions-Policy toegevoegd als globale headers. Clickjacking geblokkeerd via `frame-ancestors 'none'`. (#434)
- `deploy.yml`: Function App deploy verplaatst naar aparte `deploy` job die `needs: [build, db-migrate]` — nieuwe code bereikt productie pas na succesvolle DB-migratie. (#430)
- `deploy.yml`: twee nieuwe smoke tests voor anonymous admin endpoint (zonder token → 401) en header-spoofing (gefakete X-MS-CLIENT-PRINCIPAL → 401). (#419)
- `.github/dependabot.yml`: Dependabot bewaakt nu ook `/BlazorAdmin` NuGet-packages en GitHub Actions. (#431)
- `.github/workflows/security-scan.yml`: Trivy blokkeert nu bij HIGH/CRITICAL findings (was: exit-code 0). Dependency-scan opgenomen in security-gate. (#431)
- `Database`: `DEFAULT 'VRC'` verwijderd uit `Speeltijden`, `VeldBeschikbaarheid`, `TeamRegels`; DROP CONSTRAINT-migraties toegevoegd voor bestaande installaties. PostDeployment scalar subquery gefixet. `PlannerAfzenderNaam` default `VRC Veldplanner` → `Veldplanner`. (#435)

### Fixed
- `EmailGraphService.SendReplyAsync` slikt verzendfouten niet meer stilzwijgend weg — exception wordt opnieuw gegooid zodat de aanroeper de status correct kan bijwerken. (#432)
- `VerwerkEmailAsync`: status `AntwoordVerstuurd` en `MarkAsRead` worden pas bijgewerkt na bevestigde Graph-send. Bij mislukking: `VerzendFout`, mail blijft ongelezen voor herverwerking. (#432)
- Sportlink-sync: deelfouten (teams, programma, uitslagen) worden expliciet bijgehouden — `LastSyncTimestamp` wordt alleen bijgewerkt als de sync volledig geslaagd is. `AdminSyncTrigger` response bevat melding over asynchrone aard. (#438)
- `BerichtAiService` en `FeedbackFunction` gebruiken nu `IChatClient` (Microsoft.Extensions.AI) i.p.v. directe `OpenAI.Chat.ChatClient`. DI-registratie in `Program.cs`. README gecorrigeerd: OpenAI direct (gpt-4o-mini), niet Azure OpenAI. (#429)
- `infrastructure/modules/function-app.bicep`: `authsettingsV2` resource toegevoegd — Easy Auth declaratief vastgelegd (AllowAnonymous + Entra ID single-tenant). Wordt overgeslagen als tenantId/clientId niet geconfigureerd zijn. (#418)
- `infrastructure/main.bicep`: `tenantId` en `clientId` parameters toegevoegd, doorgegeven aan function-app module via GitHub Variables. (#418)
- `planner.GeplandeWedstrijden`: `ClubCode NOT NULL` kolom toegevoegd aan schema + PostDeployment migratie (backfill → NOT NULL → unique constraint update). (#428)
- `PlannerDataAccess.GetSpeeltijdAsync` + `SavePlannedMatchAsync`: clubCode parameter toegevoegd (optioneel, valt terug op AppSettings). `BevestigWedstrijd` endpoint geeft clubCode door. (#428)
- `planner.AlleWedstrijdenOpVeld`: `SELECT TOP 1` subqueries vervangen door `CROSS APPLY` op AppSettings — robuuster bij meerdere AppSettings-rijen + ClubCode-filter op Speeltijden en Velden. (#428)
- `EasyAuthHelper`: `RequireAdmin` en `RequireAuthenticated` delegeren nu naar centrale `RequireRole(req, params string[] allowedRoles)` helper — elimineer duplicaatlogica. (#382)
- `EmailProcessorFunction`: statische velden `_databaseNoodmailVerstuurd` en `_uitgeslotenCacheGeladen` gemarkeerd als `volatile` voor thread-safe reads bij parallelle invocaties. (#382)

### Changed
- `docs/DEVELOPER-SETUP.md`: volledig herschreven voor v2.7 — Visual Studio/F5-workflow vervangen door `Start-Debug.ps1` + `Test-App.ps1`, BlazorAdmin-setup toegevoegd (poort 5242, `dotnet watch`), .NET 9 runtime als vereiste gedocumenteerd, fingerprint-veiligheidsregel toegevoegd, oplossing-naam gecorrigeerd naar `sportlink-wedstrijdzaken.sln`. Sluit issue #394.
- `docs/SETUP-CHECKLIST.md`: herschreven voor v2.7 — verwijzingen naar niet-bestaande scripts en Visual Studio verwijderd; `Start-Debug.ps1`, `Test-App.ps1` en .NET 9 runtime-eis toegevoegd. Sluit issue #394.
- `docs/LOKAAL-DEBUGGEN.md`: volledig herschreven voor v2.7 — v1-FunctionApp-only beschrijving vervangen door volledige v2.7-stack (BlazorAdmin :5242, FunctionApp :7094, fingerprint-regel, admin-endpoints overzicht, .NET 9 runtime, geen Azure DevOps-verwijzingen). Sluit issue #395.
- `docs/QUICK-REFERENCE.md`: herschreven voor v2.7 — Visual Studio F5 + niet-bestaande scripts vervangen door `Start-Debug.ps1`, `Test-App.ps1`, beide poorten (5242 + 7094), Azure DevOps-verwijzingen verwijderd; herkwalificeerd als Developer-document. Sluit issue #395.
- `docs/api-standaarden/openapi.yaml`: 22 ontbrekende routes toegevoegd (clubs, speeltijden, theme/extract, leermomenten/stats/valideer, teambegeleiding/doorsturen, testdata, planner/auto-plan en auto-plan/toepassen); tag `testdata` toegevoegd; bijbehorende schemas toegevoegd. `openapi.json` gesynchroniseerd. Sluit issue #396.
- `deploy.yml`: `vars.*`-referenties in `run:`-scripts vervangen door bash-omgevingsvariabelen (`$SQL_SERVER`, `$SQL_DATABASE`, `$SQL_RESOURCE_GROUP`); `AZURE_STATIC_WEB_APP_HOSTNAME` naar job-level `env:` verplaatst — elimineert VS Code GitHub Actions linter-waarschuwingen.
- `docs/SETUP.md`: nieuwe sectie 11 "GitHub Actions: Productie-deployment configureren" met overzichtstabel van alle vereiste secrets en variables, uitleg welke optioneel zijn, en verificatiestap via `gh run view`.
- **AVG/multi-club cleanup:** club-specifieke data verwijderd uit 10+ bestanden: GPS-coördinaten Veenendaal (→ geografisch centrum NL als fallback), "Sportpark Spitsbergen" (→ `[Sportparklocatie]`), clubteamnamen (→ `[ClubCode]`), Blazor PageTitles (→ "Beheer"), `PlannerAfzenderNaam`-default. `PlannerFunction.cs`: `?? "VRC"` fallback vervangen door `InvalidOperationException` — hardcoded clubnaam in productie-code.

### Fixed
- `FunctionApp/Planner/PlannerFunction.cs`: twee endpoints (AutoPlan, AutoPlanToepassen) hadden `?? "VRC"` als fallback voor ClubCode — architectuurschending en multi-club-bypass. Gooit nu `InvalidOperationException` als ClubCode niet via Easy Auth bepaald kan worden.
- `FunctionApp/Email/BerichtAiService.cs`: hardcoded jaar (2026) in twee few-shot voorbeelden in de AI system prompt vervangen. Datum staat nu dynamisch als eerste instructie in de system prompt; het `doordeweeks`-voorbeeld berekent de komende maandagdatums dynamisch vanuit `DateTime.Now`. Voorkomt dat het model verkeerde datum-context aanneemt bij datumberekening.

### Added
- `docs/ARCHITECTUUR-AI-SERVICES.md`: architectuurdocument voor alle AI-integraties — provider-agnostisch via `IChatClient`, datumregel, few-shot conventies, modelnaam uit configuratie, jaarlijkse KNVB-onderhoudsplicht.
- `docs/DOCUMENTATIEPLAN.md`: documentatieplan met categorieën (Gebruikers/Administrator/Developers/Setup) en versie-verificatieconventie.
- `docs/INDEX.md` herschreven naar nieuwe categoriestructuur met 4 doelgroepen.
- `docs/api-standaarden/`: nieuwe map voor API-standaarden (`openapi.yaml`, `openapi.json`, `openspec/`) — verplaatst uit `docs/` root; openapi.yaml versie bijgewerkt naar 2.7.0; enforcement-regels toegevoegd aan CLAUDE.md.
- CLAUDE.md: sectie "API-standaarden — altijd actueel, altijd bewaakt" toegevoegd met verplichte checklist bij elke endpoint-wijziging.

### Changed (documentatie-reorganisatie)
- 6 bestanden hernoemd voor duidelijkere naamgeving:
  - `docs/v2-admin-handleiding.md` → `docs/BEHEERDER-HANDLEIDING.md`
  - `docs/TESTING.md` → `docs/VERIFICATIE-SCRIPTS.md`
  - `docs/AZURE-ENTRA-SETUP.md` → `docs/ENTRA-AUTH-BEHEER.md`
  - `docs/HANDLEIDING-TEAMBEGELEIDING-EXPORT.md` → `docs/ADMIN-TEAMBEGELEIDING-IMPORT.md`
  - `SETUP.md` (root) → `SETUP-NIEUWE-CLUB.md` (club-installatiegids)
  - `docs/SETUP.md` → `docs/DEVELOPER-SETUP.md` (developer lokale setup)
- Alle 30+ verwijzingen naar deze bestanden bijgewerkt in CLAUDE.md, README.md, CONTRIBUTING.md, SECURITY.md, ARCHITECTURE.md, scripts, .cs-bestanden en deploy.yml.
- Kritieke inhoudsfouten gecorrigeerd: `--runtime-version 10→9` in SETUP-NIEUWE-CLUB.md, kostenclaim README.md (€0 correct), scriptpaden overal gecorrigeerd naar `scripts/dev/`, branch-strategie CONTRIBUTING.md bijgewerkt naar develop-workflow, `~420 personen` verwijderd uit ADMIN-TEAMBEGELEIDING-IMPORT.md, v2/develop-branches hersteld naar develop in SECURITY.md en BEHEERDER-HANDLEIDING.md.

## [2.7.0.1] — 2026-05-31

### Added
- Blazor toont een blokkerende overlay als de Azure SQL Free-tier database niet online is: "Database wordt opgestart..." (eerste 2 min) of "Database niet beschikbaar — maandlimiet bereikt" (daarna). Voorkomen dat beheerders eindeloos op "Laden..." kijken zonder uitleg.
- `/api/health` geeft nu ook de database-status terug (`database: "online" | "paused" | "timeout" | "unavailable"`). Status `paused` triggert automatisch de Azure SQL auto-resume.
- `db-check` job in `deploy.yml` controleert de database-status via Azure ARM API als allereerste stap — vóór build, migratie én Blazor-deploy. Hele pipeline wordt geblokkeerd als de database niet `Online` is.
- `scripts/azure/Setup-SqlAlerts.ps1`: eenmalig uit te voeren script dat een gratis Resource Health Alert aanmaakt (e-mail bij database Unavailable/Resolved).

### Fixed
- `blazor-deploy` werd eerder uitgevoerd zelfs als de database offline was (had alleen `needs: build`). Nu geblokkeerd via `db-check → build` dependency-keten.

## [2.7.0.0] — 2026-05-31

### Fixed
- GitHubIssueReporter maakte dagelijks een nieuw GitHub Issue aan voor dezelfde fout (#370): de in-memory deduplicatie werkte niet na een koude start (Azure Functions). Zoekopdracht uitgebreid naar open én gesloten issues — bij een bestaand issue (open of gesloten) wordt nu commentaar toegevoegd, en een gesloten issue wordt automatisch heropend.

### Security
- GitHubIssueReporter sanitiseert nu `ex.Message` en stacktraces vóór publicatie in GitHub Issues (#372): e-mailadressen, GUIDs, SQL-connectiestring-fragmenten en grote getallen worden vervangen door placeholders (`<email>`, `<guid>`, `<redacted>`, `<n>`). Voorheen kon PII uit exception-berichten ongesanitiseerd in publieke GitHub Issues terechtkomen.

### Fixed
- Email-planner: "heel veld" uit email wordt nu correct doorgegeven aan de beschikbaarheidscheck. Voorheen werd voor teams als JO12 altijd de speeltijd-veldafmeting (0.50) gebruikt, ook als de afzender expliciet "heel veld" vroeg. Nu overschrijft het `heelVeld`-veld (gevuld door AI en doorgegeven via BerichtPipeline → CheckAvailabilityRequest) de veldafmeting naar 1.00m, en ontvangt de coördinator een waarschuwing dat dit team normaal op een halftijdsspeelveld speelt.
- Auto-planner: voorkeurstijden zijn nu zachte richtlijnen in plaats van harde doelen. Compactheid heeft prioriteit — als de vroegst mogelijke start meer dan de teamspecifieke buffer vóór de voorkeurstijd ligt, wordt compact ingepland zodat er geen onnodig gat in het schema ontstaat. Teams met een expliciete BufferVoor-teamregel (bijv. Heren 1 met 60 min voor) behouden hun gerechtvaardigd gat.
- Auto-planner: teamspecifieke bufferregels (BufferVoor/BufferNa uit TeamRegels) worden nu toegepast tijdens het auto-plannen — eerder werden deze regels alleen gerespecteerd bij handmatig inplannen.
- Auto-planner: sorteervolgorde is gewijzigd van prioriteitsnummer naar werkelijke voorkeurstijd, zodat vroeg-spelende JO-teams vóór laat-spelende Heren-teams worden ingepland.

### Added
- Tijdinvoer-normalisering (`TimeHelper`, `TimeInput`-component): invoer als "0830" of "830" wordt automatisch gecorrigeerd naar "08:30". Eén centrale implementatie voor alle tijdinvoervelden (#365, #380).
- Globale backend-statusbanner (#365): bovenaan elk scherm verschijnt een gele banner ("Backend start op…") tijdens opstarten van de FunctionApp, en een rode banner wanneer de API onbereikbaar is — lege schermen zonder uitleg zijn niet meer mogelijk.
- Velddeel-selector in testdata invoergrid (#365): per wedstrijd kiesbaar deelveld op basis van speeltijden-tabel (JO7-JO10: A1/A2/B1/B2, JO11-JO12: A/B).
- Voorkeurstijden: veldkeuze in Teamregels toont dropdown met clubvelden (#365).
- Speeltijden per club: composite primary key `(Leeftijd, ClubCode)` — elke club configureert eigen speeltijden (#365).
- Clubnaam in sidebar altijd correct (#380): de naam boven het navigatiemenu wordt nu gevuld vanuit de clubs-lijst in MainLayout — niet meer via een aparte API-call naar de instellingen. Hierdoor toont de sidebar altijd de naam van de geselecteerde club, ook bij ALLSTARS ("AllStars FC") en bij multi-club wisseling.
- ALLSTARS testmodus-banner altijd zichtbaar in de sidebar (#380): bij selectie van AllStars FC verschijnt een gele "TESTMODUS"-banner bovenin het navigatiemenu op elke pagina. De club-selector krijgt een gele rand. De sidebar abonneert zich correct op ClubSelector.OnChange zodat de indicator instant verschijnt — ook bij het eerste laadmoment. Andere clubs zien nooit testmodus-indicatoren.
- Auto-planner dagplanning (#380): de pagina "Dagplanning" toont nu voor elke wedstrijd de huidige situatie én de optimale situatie (veld + tijd) zij aan zij. De planner pakt automatisch alle wedstrijden op de gekozen datum op — inclusief die zonder veld of tijd — en maakt een optimaal rooster op basis van leeftijdscategorie (jongste teams eerst om 09:00), deelveld-sharing (bijv. 4×JO7 tegelijk op één veld) en veldvoorkeur (kunstgras voor gras). Wedstrijdstatus: Nieuw slot / Wijziging / Ongewijzigd / Niet inplanbaar.
- Visuele planning naast per-wedstrijd tabel: twee tabbladen "Optimale planning" en "Huidige situatie" tonen de Gantt-visualisatie vóór en na optimalisatie.
- Testmodus (ALLSTARS) "Toepassen": in testmodus kunnen de optimale tijden en velden direct worden toegepast op de testdata (UPDATE his.matches WHERE ClubCode='ALLSTARS'). Productiedata blijft ongewijzigd — handmatig aanpassen in Sportlink blijft vereist.
- Filterknoppen op de wedstrijdentabel: alles / alleen wijzigingen / niet inplanbaar.
- Native Blazor Gantt-chart in Dagplanning (#380): vervangt de iframe-visualisatie door een eigen tijdlijn opgebouwd met Bootstrap + inline CSS. Elke tab (Optimale planning / Huidige situatie) toont nu uitsluitend die situatie. Veldblokken zijn gekleurd op status (groen=ongewijzigd, blauw=wijziging, amber=nieuw), tonen aanvangstijd en wedstrijdomschrijving, en ondersteunen tooltip met volledige info. Deelveld-matches (0.25/0.50) staan gestapeld in de juiste kwartrant. Tijdas loopt dynamisch van eerste tot laatste wedstrijd.
- Auto-planner houdt rekening met TeamVoorkeurTijden (#380): teams met een geconfigureerde voorkeurstijd worden ingepland op (of zo dicht mogelijk bij) die tijd. Teams met prioriteit=1 worden als eerste ingepland zodat ze hun voorkeursslot krijgen. De planner zoekt in stappen van 5 minuten naar buiten (max. 90 min tolerantie) en valt daarna terug op vroegst beschikbaar.
- Dagplanning per-wedstrijd tabel (#380): "Cat"-kolom verwijderd (categorie leesbaar uit wedstrijd-naam). Wedstrijd-kolom toont nu de volledige wedstrijd-omschrijving (thuisteam - uitteam) — bij ALLSTARS geconstrueerd uit thuisteam en uitteam waar [wedstrijd] null is.

### Fixed
- Veldplanner toonde alle 5 velden ongeacht de club (#364): de dagplanning en optimalisatie tonen nu alleen de velden die bij de actieve club horen (gefilterd op ClubCode). AllStars FC (3 velden) ziet voortaan niet meer de velden van andere clubs.
- Veldplanner-optimalisatie was afhankelijk van hardcoded "veld 5 = grasveld": alle logica is nu gebaseerd op het `VeldType`-veld in de database (`kunstgras`/`gras`). Clubs met een ander aantal velden of een andere indeling worden correct behandeld.
- ALLSTARS dagplanning toonde onzichtbare blokken voor JO-teams door slechte leeftijdscategorie-extractie — opgelost met spatie-gescheiden tokenizer (#365).
- Planner-queries joinden `dbo.Speeltijden` zonder ClubCode-filter — dubbele rijen bij multi-club data opgelost (#365).
- Wedstrijden-pagina en dagplanning toonden VRC-data in ALLSTARS-modus door race-condities bij initialisatie (#365).
- Start-Debug.ps1: runtimeconfig.json lock-fout bij dotnet watch herstart opgelost (#365).
- Dagplanning UI: "Veld 5 ontlasten" hernoemd naar "Grasveld(en) ontlasten"; statistiek "Van veld 5 verplaatst" wordt nu "Van grasveld verplaatst".

## [2.6.0.1] — 2026-05-26

### Added
- Teambegeleiding importeren via de Admin GUI (#358): beheerders kunnen nu een CSV-bestand van club.sportlink.com direct uploaden op de Teambegeleiding-pagina. De CSV wordt in de browser ingelezen (geen serveropslag — AVG), een voorbeeldtabel toont de eerste vijf rijen ter controle, en na bevestiging worden de begeleiders geïmporteerd met dezelfde kolomdetectie als het handmatige PowerShell-script. Handmatig script blijft bestaan als fallback.
- `avg.ImportLog` uitgebreid met `ClubCode`-kolom voor multi-club isolatie van importlogs.
- Koude-start indicator in de topbalk: zolang de backend nog opwarmt (koude start tot ±36 seconden) verschijnt een kleine spinner met "Backend start op…". Na succesvol laden verdwijnt de spinner en verschijnt automatisch de club-selector.

### Fixed
- CSV-import via de GUI gaf altijd "Import mislukt" door ontbrekende `ClubCode`-kolom in `avg.ImportLog`. Import werkt nu correct.

## [2.5.1.0] — 2026-05-26

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
