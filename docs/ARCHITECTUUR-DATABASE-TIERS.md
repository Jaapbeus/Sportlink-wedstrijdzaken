# Architectuur — Database-tiers

> **Dit document is een index + vastgelegd besluit, geen implementatiehandleiding.** Voor de
> daadwerkelijke bouw van een tier: ga naar het bijbehorende sub-issue in sectie 5. Dit document
> beschrijft *waarom* de volgorde en de scheiding tussen tiers vaststaan — niet *hoe* een specifieke
> tier wordt gebouwd.

## 1. Bouwvolgorde (vier tiers, vaste volgorde)

1. **SQL Server / Azure SQL** — bestaand, ongewijzigd. De huidige productie-tier.
2. **Postgres** (lokaal via Docker + Supabase in de cloud) — **eerste prioriteit**.
3. **SQLite** — na Postgres.
4. **Cosmos DB** — uitsluitend voor het e-mailverwerkingslog (`planner.EmailVerwerking`), niet voor
   de hoofddatabase. Laatste in de volgorde.

**Aanleiding:** de bestaande Azure SQL Free-tier (serverless) heeft een hard maandelijks
vCore-second-budget. Bij uitputting pauzeert Azure de database geforceerd tot de volgende
kalendermaand, zonder mogelijkheid om daaromheen te werken — dit gebeurde ~10 dagen in augustus
2026. Een tweede tier-optie met een ander faalmodel is de structurele oplossing, geen workaround.

**Waarom deze volgorde vaststaat:**
- Postgres eerst — de meest volwassen relationele optie, met een gratis cloud-variant (Supabase)
  die qua faalmodel (7-dagen-pauzebeleid bij inactiviteit) fundamenteel anders is dan Azure SQL
  serverless' vCore-uitputting.
- SQLite daarna — een lichter, file-based alternatief; relevant zodra de opslagvraag op Azure
  Functions Linux Consumption is opgelost (zie #826).
- Cosmos DB als laatste — raakt uitsluitend één geïsoleerd onderdeel (het e-maillog), niet de
  hoofddatabase, en heeft dus de laagste prioriteit.

## 2. "Eén tier per club, nooit gelijktijdig, geen gedeelde abstractie" — besluit en rationale

Een gedeelde C#-providerabstractie (`DbProviderFactory`, een generieke `DbConnection`) over SQL
Server's dynamische DDL en `MERGE`-syntax zou een reëel lek-risico zijn: abstracties die
complexiteit uit één engine proberen te verbergen terwijl een andere engine die complexiteit niet
op dezelfde manier heeft, lekken typisch door op precies de plekken waar het pijn doet —
dynamische schema-generatie en upsert-semantiek.

Omdat er nooit meer dan één tier tegelijk actief is binnen één deployment (precies één echte club +
AllStars FC als demo-club per fork, zie "Deployment-model" in CLAUDE.md), is er ook geen
functioneel voordeel dat het risico zou rechtvaardigen.

**Consequentie:** volledig gescheiden, parallelle implementatiebomen per tier
(`Database.Postgres/`, `Database.Sqlite/`), gekozen via het tier-keuzemechanisme (#816) op
build/deploytijd — niet via een runtime-switch in gedeelde code.

**Uitzondering, expliciet afgebakend: pure, provider-agnostische business-logica mag wél gedeeld
worden.** Deze regel gaat over een DB-*provider*-abstractie (dynamische DDL, upsert-/MERGE-
semantiek) — niet over elke regel C# die toevallig door meer dan één tier gebruikt wordt. #819
extraheerde de Sportlink-veldstring-matching (voorheen `PlannerShared.ResolveVeld` +
`AutoPlanService.NormaliseerVeld`, uitsluitend tekstbewerking zonder SQL/ADO.NET-afhankelijkheid)
naar het tier-agnostische `Planner.Shared/`, gebruikt door zowel `FunctionApp` (SQL Server-tier)
als `Database.Postgres`. Zonder die extractie zou de Postgres-view de matching opnieuw in SQL
moeten herbouwen — een derde, onafhankelijke kopie naast de bestaande T-SQL-versie en de
C#-versie, precies het onderhoudsrisico dat #719 al blootlegde voor die twee. Vuistregel voor een
volgende tier: bevat de te herbouwen logica geen SQL en geen providerspecifieke aanroep, verhuis
haar naar een gedeeld project in plaats van haar te dupliceren.

**Tier-keuze is bewust onveranderlijk na de eerste deploy — afdwinging gebouwd bij #976.** #816
legt vast dat de repository-variabele `DatabaseTier` de keuze bepaalt (hard-fail bij een
ontbrekende/onbekende waarde). Met de Postgres-tier inmiddels functioneel gereed werd een switch
voor het eerst fysiek mogelijk (zie sectie 49 voor de eenmalige productiecutover die dat moment
concreet maakte) — een *wijziging* van een reeds actieve, geldige waarde naar een andere tier zou
zonder extra maatregel stilzwijgend bij de eerstvolgende reguliere deploy worden toegepast, en dat
mag niet zonder expliciete bevestiging.

`resolve-database-tier.sh` (#976) eist daarom een tweede, los te bewerken repository-variabele
`DatabaseTierSwitchConfirmation` die exact moet overeenkomen met `DatabaseTier`. Bij de stabiele
situatie (geen wissel) staan beide al gelijk en blokkeert dit niets — bij een enkele, per ongeluk
gewijzigde `DatabaseTier` faalt de build hard (exitcode 3) met een duidelijke foutmelding, in
plaats van production stilzwijgend naar een andere database te laten omschakelen. Een bewuste
wissel vereist het expliciet bijwerken van *beide* variabelen in dezelfde actie. Getoetst door
`scripts/ci/Test-TierSwitchConfirmation.ps1`, los van de tier-naamresolutie zelf
(`Test-TierMappingConsistency.ps1`, #865).

## 3. Identifier-casing-conventie

*(Woordelijk overgenomen uit #814 §6 — afgeronde beslissing, geen onderwerp van herontwerp.)*

> Elke engine-specifieke boom gebruikt zijn eigen idiomatische identifier-conventie (SQL Server:
> PascalCase; Postgres/SQLite: lowercase snake_case). Een gedeelde C#-schemadefinitie houdt
> kolomnamen logisch/neutraal vast en past casing pas toe bij het genereren van
> backend-specifieke SQL. Nieuwe SQL-mapstructuren gebruiken consequent lowercase mapnamen; nieuwe
> PowerShell-scripts volgen de bestaande Verb-Noun-PascalCase-conventie. Verwijs altijd naar
> bestandspaden met exact dezelfde hoofdlettering waarmee ze zijn aangemaakt — nooit vertrouwen op
> een case-insensitief bestandssysteem, want de daadwerkelijke hostingomgeving en de meeste
> CI-runners zijn case-sensitief.

**Postgres-specifieke valkuil die deze conventie rechtvaardigt:** Postgres vouwt ongequote
identifiers automatisch naar lowercase (`CREATE TABLE Teams` wordt intern `teams`); een latere,
gequote referentie (`"Teams"`) matcht daar niet meer mee en faalt. Vandaar de regel "altijd
lowercase, nooit quoten" voor de nieuwe bomen.

**Empirisch bevestigd (Docker, Postgres 16, 2026-08-30):** een ongequote `CREATE TABLE Teams`
resulteert in een tabel die intern `teams` heet; een daaropvolgende query tegen `"Teams"`
(gequote) faalt met `undefined_table`.

SQL Server's eigen schemaconventie is `dbo`; Postgres gebruikt idiomatisch `public` (of een
projectgekozen naam) — nooit `dbo` letterlijk overnemen in de Postgres-boom.

**Geautomatiseerde bewaking (#864):** `scripts/ci/check-postgres-identifier-casing.sh` scant elke
`Database.Postgres/migrations/*.sql` op tabel- en kolomnamen met een hoofdletter, of die tussen
dubbele aanhalingstekens staan — beide zijn een schending van deze conventie. Draait als CI-stap,
zonder database en zonder secrets.

## 4. Bestandssysteem-casing / Linux-CI-risico

Git's `core.ignorecase=true` (gangbare default op Windows/macOS) merkt een casing-mismatch lokaal
niet op, terwijl Linux CI-runners (`core.ignorecase=false`) daar hard op falen — een reëel
"werkt-bij-mij-niet-in-CI"-risico specifiek voor de nieuwe tier-bomen, waar consequent lowercase
mapnamen de norm zijn en een enkele PascalCase-tikfout dus niet lokaal wordt gesignaleerd.

Geautomatiseerde bewaking hiervan is de scope van **#825** (CI-guard voor
bestandssysteem-casing) — dit document beschrijft het risico en wijst ernaar door, het lost het
zelf niet op.

## 5. Cross-referentietabel — index van alle sub-issues onder epic #815

Dit is de **enige plek** waar een developer die met deze epic begint, hoort te starten — dit
document functioneert als index, niet als volledige inhoud (die staat per definitie in de
individuele sub-issues en, later, in de code zelf).

| Sub-issue | Levert op |
|---|---|
| [#816](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/816) — Tier-keuze-mechanisme bij fork-opzet | Het build/deploy-tijd-mechanisme waarmee een fork precies één tier kiest |
| [#817](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/817) — Nieuw architectuurdocument docs/ARCHITECTUUR-DATABASE-TIERS.md | Dit document zelf |
| [#818](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/818) — ETL-engine herontwerp (C#-schemadefinitie) | Postgres-vertaling van de staging→history-mergelaag |
| [#819](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/819) — Kernview-vertaling `planner.AlleWedstrijdenOpVeld` → Postgres | Postgres-vertaling van de planner-kernview |
| [#820](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/820) — Collation/case-sensitivity-fix ✅ opgelost | Correctheid van teamnaam-matching onder Postgres |
| [#821](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/821) — Nieuwe migratie-aanpak `Database.Postgres/` | Schema-als-code voor de Postgres-tier (geen SSDT-equivalent) |
| [#822](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/822) — docker-compose Postgres-service | Lokale Postgres-ontwikkelomgeving naast SQL Server |
| [#823](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/823) — CI Postgres fresh-db-equivalent | CI-verificatie van een vers Postgres-schema |
| [#824](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/824) — Teambegeleiding-CSV-import naar Postgres | Postgres-vertaling van de CSV-importpijplijn |
| [#825](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/825) — CI-guard voor bestandssysteem-casing | Geautomatiseerde bewaking van het risico in sectie 4 |
| [#826](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/826) — SQLite-tier (fase 3), zie [ARCHITECTUUR-SQLITE-TIER.md](ARCHITECTUUR-SQLITE-TIER.md) | Tier 3 — voorbereidend ontwerp, nog niet blokkerend |
| [#827](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/827) — Repository-boundary-refactor e-maillog | Prerequisite-opschoning vóór #828 (**gemerged**) |
| [#828](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/828) — Cosmos DB-ontwerp e-maillog, zie [ARCHITECTUUR-COSMOSDB-EMAILLOG.md](ARCHITECTUUR-COSMOSDB-EMAILLOG.md) | Tier 4 — uitsluitend het e-maillog, niet de hoofddatabase |

## 6. Tweede ronde sub-issues — gevonden bij het ontwerp van de zelftest (#851)

> **Lees dit vóór je #818–#825 als "de hele Postgres-tier" beschouwt.** Bij het uitwerken van een
> end-to-end zelftest is de tier-scope tegen de werkelijke broncode gelegd. Daaruit bleek dat
> #818–#825 samen géén draaiende applicatie opleveren, en dat er een aantal blokkades in de weg
> staan die geen van de bestaande sub-issues dekt. Die zijn belegd in onderstaande issues.

**Exitcriterium van fase 1 is niet "#825 gemerged", maar "[#851](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/851)
groen".** De zelftest draait de tier lokaal end-to-end en is daarmee het enige afrekenbare bewijs
dat de omzetting werkt. Zolang die rood staat, is de tier niet af — hoeveel sub-issues er ook
gesloten zijn.

### 6a. Blokkades in de Postgres-boom

| Sub-issue | Levert op |
|---|---|
| [#853](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/853) — Business-key-kolom vs. demodata-seed ✅ opgelost (Optie A, zie §7) | Besluit over `GENERATED ALWAYS`; zonder dit faalt de seed volledig |
| [#854](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/854) — UTC in audit-tijdstempels | `NOW()` schrijft lokale tijd; empirisch 2 uur afwijking in de zomer |
| [#855](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/855) — Identifier-casing | De boom volgt sectie 3 van dit document niet consequent |

### 6b. Blokkades in de bestaande tier (niet Postgres-specifiek)

| Sub-issue | Levert op |
|---|---|
| [#856](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/856) — Seed slaat zichzelf over op een verse database ✅ opgelost (Optie B, zie §13) | Zonder dit zijn er op elke nieuwe installatie nul demoteams en -wedstrijden |
| [#857](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/857) — Synchronisatie-rem is dode code | Een lokale run praat nu met de externe bron en kan issues aanmaken |
| [#858](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/858) — AVG-maskering hangt aan kolomcasing | Onder lowercase-identifiers lekken volledige e-mailadressen naar de browser |
| [#859](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/859) — Stille faalpaden rond databaseconfiguratie ✅ opgelost | `/api/health` geeft 503 bij "unconfigured" i.p.v. 200; een mislukte instellingenlaadt is zichtbaar via `settingsLoaded`; de wachtlus is kort buiten productie en overschrijfbaar via `DbWaitMaxRetries`/`DbWaitDelayMs`, op beide tiers |

### 6c. Ontbrekende scope

| Sub-issue | Levert op |
|---|---|
| [#860](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/860) — Applicatie-datalaag en projectopzet (kapstok) ✅ alle vijf sub-issues gemerged | **Het grootste gat**: 40 bestanden, ~212 SQL-statements. Uitgewerkt naar vijf sub-issues, alle gemerged: [#891](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/891) (projectopzet), [#887](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/887) (beheer), [#888](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/888) (planner — elf endpoints, nul 501-stubs), [#889](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/889) (e-mail/teamresolutie), [#890](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/890) (synchronisatie) |
| [#861](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/861) — Resterende procedures en views | #818 dekt 2 van 8 procedures, #819 dekt 1 van 4 views |
| [#862](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/862) — Demodata-seed + dekking | Postgres-variant van de seed, plus de circa 17 tabellen zonder demodata |

### 6d. Bewaking en testinfrastructuur

| Sub-issue | Levert op |
|---|---|
| [#863](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/863) — Tier-provenance in de gezondheidscheck | Het bewijsmiddel dat de applicatie écht op de bedoelde engine draait |
| [#864](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/864) — Drift-guards naar de tweede boom | Schema- en logicadrift tussen de twee bomen wordt nu door niets bewaakt |
| [#865](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/865) — Tier-mapping als gedeelde data | Houdt de belofte van #816 overeind dat er één vertaalpunt is |
| [#866](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/866) — Integratietests env-gestuurd | De tests van #818 staan nu onvoorwaardelijk uit |
| [#867](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/867) — Fixtureserver + egress-blokkade | Maakt het synchronisatiepad testbaar zonder externe dienst |
| [#851](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/851) — **End-to-end zelftest** | Het exitcriterium van fase 1 |

### 6e. Aanbevolen volgorde

De afhankelijkheden lopen niet gelijk aan de nummering:

1. **Deblokkeren**: #853, #854, #855 — dit zijn correcties op werk dat al gedaan is, en hoe later
   ze landen hoe meer erop gebouwd is. ✅ Alle drie gemerged.
2. **Testbaar maken**: #867 (egress-blokkade — nodig vóórdat er geautomatiseerd gedraaid wordt),
   #866, #863. ✅ #866/#863 volledig gemerged; #867 gedeeltelijk (fixtureserver + SQL-Server-tier-
   test gemerged, Postgres-tier-variant wacht op #890, CI-wiring op #866-patroon nu beschikbaar).
3. **Bouwen**: #860 (het grootste stuk — uitgewerkt naar #891/#887/#888/#889/#890, zie §6c),
   daarna #861 en #862. #891 (projectopzet) en #887 (beheer, alle 16 endpointparen) gemerged;
   #888/#889/#890 nog open — #887's `AdminSyncFunction.Trigger`/`AdminTeambegeleidingFunction.
   Doorsturen` zijn bewuste 501-stubs die op #890 resp. #889 wachten.
4. **Bewaken**: #864, #865.
5. **Afrekenen**: #851 groen krijgen.

#856, #857, #858 en #859 raken de bestaande tier en kunnen parallel, los van de tier-migratie.
#857 en #867 horen samen te landen: de eerste beschrijft waarom een lokale run nu naar buiten
praat, de tweede levert de schakelaar die dat blokkeert.

## 7. Business-key-bronkolommen altijd vullen, ook in demodata (#853)

**Besluit: Optie A** — de demodata-seed vult de daadwerkelijke business-key-bronkolommen
(`teamcode`, `lokaleteamcode`, `poulecode` voor `his.teams`), in plaats van de afgeleide
sleutelkolom een uitzonderingspositie te geven.

**Aanleiding:** `Database.Postgres/PostgresSchemaGenerator.cs` (#818) maakt de synthetische
`bk_`-kolom een `GENERATED ALWAYS AS (...) STORED`-kolom, afgeleid uit de business-key-kolommen die
`KnownEntities.cs` voor die entiteit aanwijst. De bestaande AllStars-demodataseed in
`Script.PostDeployment1.sql` vulde die drie bronkolommen nooit — alleen de (op SQL Server een
gewone kolom zijnde) `bk_teams` rechtstreeks. Op Postgres faalt dat: een gegenereerde kolom
accepteert geen directe waarde, en zonder de kolom leeg laten geeft elk team dezelfde afgeleide
sleutel (drie keer `NULL` → `COALESCE` naar dezelfde lege string), waarna de unieke index alle op
één na weigert.

**Afweging tegen optie B** (de gegenereerde kolom terugzetten naar een gewone, door de ETL gevulde
kolom, zoals de bestaande SQL Server-boom): dat zou de garantie opgeven die de `GENERATED
ALWAYS`-kolom juist biedt — dat de sleutel per definitie overeenkomt met de brondata, nooit los kan
raken door een vergeten bijwerking elders. Optie A kost alleen een kleine, additieve wijziging aan
één seed-blok; optie B zou al het empirisch geverifieerde werk uit #818/#819/#821/#824 rond de
gegenereerde kolom ongedaan maken. **Optie A gekozen.**

**Consequentie voor toekomstige demodata en voor #862** (de nog te bouwen Postgres-variant van deze
seed): vul altijd de echte business-key-bronkolommen van een entiteit, met unieke, herkenbaar-fictieve
waarden — nooit alleen de afgeleide/opgeslagen sleutelweergave. Dit geldt voor elke toekomstige
entiteit die op dezelfde manier gemodelleerd wordt.

## 8. Audit-tijdstempels: TIMESTAMPTZ, niet naïeve TIMESTAMP + timezone-wrap (#854)

**Besluit:** de audit-kolommen (`mta_inserted`/`mta_modified`/`mta_deleted`) van elke his-tabel zijn
`TIMESTAMPTZ`, niet een naïeve `TIMESTAMP` met een expliciete `timezone('utc', ...)`-wrap per
schrijfactie.

**Aanleiding:** `NOW()` in een naïeve `TIMESTAMP`-kolom gebruikt de sessietijdzone bij de impliciete
cast — draait de databaseserver niet op UTC, dan staat er lokale tijd in een kolom die de rest van
de applicatie als UTC behandelt. Exact de regressie die PR #246 al oploste voor SQL Server
(`GETDATE()` → `GETUTCDATE()`), nu empirisch bevestigd voor Postgres (wegwerpcontainer op
`Europe/Amsterdam`-sessietijdzone: een naïeve kolom + `NOW()` weekt 2 uur af van de werkelijke UTC-tijd).

**Waarom `TIMESTAMPTZ` boven de `timezone('utc', ...)`-wrap:** Postgres normaliseert een
`TIMESTAMPTZ`-waarde intern altijd naar UTC, ongeacht de sessietijdzone waarin hij geschreven is —
`NOW()` hoeft dus niet aangepast te worden. Npgsql leest de kolom terug als `DateTime` met
`Kind=Utc`. Er bestaat vandaag geen C#-consument van deze specifieke Postgres-kolommen die al een
eigen `SpecifyKind`-aanroep doet (in tegenstelling tot de SQL Server-tier, waar dat wel nodig is) —
dus geen dubbele-conversie-risico om na te lopen.

Empirisch bevestigd met een integratietest die de databasesessie expliciet op `Europe/Amsterdam`
zet (`Options=-c timezone=...` in de connectiestring, zodat ook de intern door de orchestrator
geopende connectie de tijdzone erft) en aantoont dat de geschreven waarde binnen een seconde van de
werkelijke UTC-tijd ligt, met `Kind=Utc`.

## 9. KnownEntities-kolomcasing consistent met §3 (#855)

**Bevinding:** `Database.Postgres/KnownEntities.cs` week op twee plekken af van de in §3 vastgelegde
lowercase-snake_case-conventie: de `ClubCode`-kolom (alle drie entiteiten) en vrijwel de volledige
`matchdetails`-entiteit (~60 kolommen) waren PascalCase, letterlijk overgenomen uit de SQL
Server-brontabellen. Omdat `PostgresIdentifier.Quote` onvoorwaardelijk quote't, landde die casing
letterlijk in de database — elke latere, ongequote verwijzing (`WHERE clubcode = @club`) zou daarop
stukgelopen zijn.

**Fix:** alle kolommen en de business-key-lijst in `KnownEntities.cs` zijn lowercase gemaakt.

**Twee guards toegevoegd tegen regressie:**
1. `EntityDefinition.Create` valideert nu dat de entiteitsnaam, elke kolomnaam en elke
   business-key-verwijzing exact gelijk is aan zijn eigen lowercase-vorm — faalt hard bij
   constructie, dus al bij het schrijven van een nieuwe entiteit, niet pas bij het genereren van
   DDL of het draaien tegen een live database.
2. `KnownEntitiesTests.GenerateHisTable_VoorElkeBekendeEntiteit_GeenEnkeleKolomnaamWijktAfVanLowercase`
   genereert de his-DDL voor alle drie de daadwerkelijke `KnownEntities`-entiteiten en controleert
   dat geen enkele gequote kolomidentifier afwijkt van zijn lowercase-vorm — dekt zo ook wat
   `EntityDefinition.Create` niet ziet (de tabel- en indexnamen die de generator zelf toevoegt).
   Bewust beperkt tot kolomnamen, niet de `UQ_`/`IX_`-indexnaamprefixes — die dragen de bestaande,
   SQL-Server-gespiegelde prefix-conventie (zie het issue zelf, dat `UQ_teams_bk` ongewijzigd als
   voorbeeld citeert) en worden nergens via een handgeschreven, ongequote query aangesproken.

De reeds bestaande `fresh-db-postgres`-CI-job bewaakt dezelfde conventie al voor de
migratie-gebaseerde configuratietabellen (`appsettings`, `velden`, `speeltijden`,
`geplandewedstrijden`) via een live `information_schema.columns`-query — die twee guards dekken nu
samen zowel de migratiegebaseerde als de entiteitsgebaseerde (ETL-)boom.

## 10. FunctionApp.Postgres — projectopzet (#891)

**Besluit:** `FunctionApp.Postgres/` is een minimaal, zelfstandig Azure Functions
isolated-worker-project (net9.0 — zelfde beperking als de bestaande `FunctionApp`, zie sectie
".NET versie" in CLAUDE.md; beide gaan naar net10.0 via epic #1063), met een `ProjectReference` naar `Database.Postgres` (#818) en
verder bewust **geen** kopie van `FunctionApp/Program.cs`'s Graph-/AI-/e-mail-/monitoring-DI. Die
registraties horen bij de functionaliteit die #887 (beheer), #888 (planner), #889 (e-mail/
teamresolutie) en #890 (synchronisatie) vertalen — niet bij de projectopzet zelf.

**Configuratielaag:** `PostgresDatabaseConfig` leest `POSTGRES_CONNECTION_STRING` — dezelfde naam
die `Database.Postgres.Cli` (#821) al gebruikt voor het migratiepad, bewust geen tweede
naamschema. Zet `ApplicationName=SportlinkFunctionAppPostgres` op de connectiestring (#863-precedent
toegepast op dag één, niet als latere toevoeging).

**Bewust géén `Pooling=false`:** de SQL Server-tier zet dat specifiek om Azure SQL's serverless
auto-pause niet te blokkeren (#808). Dat is een eigenschap van díe hostingkeuze, niet een
algemene regel — zonder bevestiging dat de Postgres-tier op een vergelijkbare auto-pausende laag
draait, is Npgsql's standaard pooling (efficiënter hergebruik van verbindingen) de juiste default.
Herzie dit zodra de daadwerkelijke Postgres-hosting vaststaat.

**`/api/health` heeft geen `"paused"`-status:** de SQL Server-tier herkent Azure SQL's auto-pause
aan foutnummer 40613 — Azure-SQL-specifiek. Zonder een bevestigd, vergelijkbaar auto-pause-concept
voor de gekozen Postgres-hosting zou `"paused"` hier verzonnen zijn; een onbereikbare database is
hier altijd `"unavailable"` of `"timeout"`. `tier`/`provider`/`serverVersion` volgen verder exact
het #863-patroon (build-time metadata resp. `SHOW server_version`).

**Empirisch bevestigd:** `func start` tegen een wegwerp-Postgres-16-container levert een werkende
`/api/health` op — `status="ok"`, `tier="Postgres"`, `provider="Npgsql"`,
`serverVersion="16.15 (Debian 16.15-1.pgdg13+2)"` (echte, live opgehaalde serverversie).

**Tier-resolver:** `scripts/ci/database-tiers.json`'s Postgres-rij staat nu op `"built": true` —
dit is de PR die de implementatieboom toevoegt, conform de eigen regel van dat bestand
("Zet 'built' op true in dezelfde PR die de implementatieboom toevoegt, nooit eerder"). Dit
betekent een buildbaar, deploybaar project bestaat — niet dat #887-890's functionaliteit al
compleet is, exact zoals de SQL Server-tier ook incrementeel is opgebouwd met `"built": true`
vanaf het begin.

## 11. FunctionApp.Postgres/Admin — eerste vertaalde beheer-endpoint (#887)

**Besluit:** `FunctionApp/Admin/EasyAuthHelper.cs` en `AdminEndpoint.cs` zijn woordelijk gekopieerd
naar `FunctionApp.Postgres/Admin/` (geen gedeelde abstractie, §2) — beide waren al vrijwel volledig
provider-agnostisch (pure claims-/header-parsing); de enige aanpassing is de doorverwijzing naar
`PostgresAppSettings`/`PostgresSystemUtilities.WaitForDatabaseAsync` in plaats van hun SQL
Server-tegenhangers.

**`PostgresAppSettings` is bewust onvolledig ten opzichte van `SystemUtilities.AppSettings`:** de
SQL Server-tier laadt ~18 kolommen uit `dbo.AppSettings`; `public.appsettings` heeft er vandaag
drie (`clubcode`, `accommodatie`, `syncenabled`, zie `Database.Postgres/migrations/001_baseline.sql`).
`PostgresAppSettings.LoadSettingsAsync` laadt uitsluitend wat daadwerkelijk bestaat — een
fantoom-fallback voor niet-bestaande kolommen zou misconfiguratie maskeren. Sub-issues die nieuwe
functionaliteit vertalen breiden dit uit zodra de bijbehorende migratie de kolom toevoegt.

**Eerste vertaalde endpoint: `AdminClubsFunction`/`AdminClubsRepository`.** Empirisch geverifieerd
(wegwerp-Postgres-16-container, twee geseede rijen): `GET /api/beheer/clubs` retourneert beide
clubs, gesorteerd op `syncenabled DESC, clubcode` — functioneel gelijk aan de SQL Server-tier.
**Bewust gedocumenteerd gat:** `public.appsettings` heeft geen `clubname`-kolom; deze vertaling
gebruikt `clubcode` ook als weergavenaam totdat een toekomstige migratie dat verschil dicht.

**Tijdens deze vertaling ontdekt: `public.speeltijden` mist drie kolommen** (`WedstrijdHelft`,
`WedstrijdRust`, `StandaardVoorkeurTijd`) ten opzichte van `dbo.Speeltijden` — zie #893 (opgelost,
zie §12). Blokkeerde de CRUD-vertaling van `AdminSpeeltijdenFunction`/`Repository`, die daarom nog
niet in deze ronde is meegenomen.

**#887 is inmiddels volledig afgerond: alle 16 admin-endpointbestanden hebben een
Postgres-tegenhanger.** De resterende vijftien volgden dezelfde, nu gevestigde structuur: repository
in `FunctionApp.Postgres/Admin/Repositories/`, endpoint in `FunctionApp.Postgres/Admin/`, zelfde
route als de SQL Server-tier. Nieuwe migraties per tabelgroep: `003_admin_tables.sql` (Teams,
TeamAliassen, TeamRegels, TeamVoorkeurTijden, UitgeslotenEmailAdressen, VeldPeriode,
VeldBeschikbaarheid, VeldTraining, EmailTemplateInstellingen, `planner.EmailVerwerking`,
`planner.ClassificatieCorrectie`, plus de ontbrekende kolommen op `appsettings`/`velden`/
`speeltijden`), `004_appsettingsaudit.sql` (`AppSettingsAudit`), `005_appsettings_theme_assets.sql`
(`faviconurl`/`logourl`).

**Twee genuine Postgres-vs-SQL-Server-verschillen empirisch aangetroffen tijdens deze vertaling**
(niet aangenomen, gevonden via een echte runtime-fout tegen een wegwerp-container):
- **Impliciete tekst→numeriek-conversie bestaat niet in Postgres.** `AdminSettingsFunction`'s
  dynamische `UPDATE … SET [veld] = @waarde` bindt elke gewijzigde waarde als string (de
  JSON-request is `Dictionary<string,string?>`). SQL Server accepteert dat via impliciete conversie
  in een `INT`/`BIT`/`FLOAT`-kolom; Postgres geeft `42804 column "bufferminuten" is of type integer
  but expression is of type text`. Fix: een `FieldCasts`-tabel geeft de vier niet-tekstvelden een
  expliciete `::type`-cast in de UPDATE-SQL. Zonder deze fix zou elke PUT op een numeriek/boolean
  AppSettings-veld hard falen — `dotnet build` ziet dit niet, alleen een echte runtime-aanroep.
- **Npgsql weigert een `DateTime` met `Kind=Unspecified` voor een `TIMESTAMPTZ`-parameter.**
  `AdminEmailLogFunction`'s `vanaf`/`tot`-filters komen uit `DateTime.TryParse(...).Date` (Kind
  onveranderd = Unspecified) — dat werkte op `DATETIME2` (kent geen Kind), niet op Npgsql's
  `TIMESTAMPTZ`-binding. Fix: `DateTime.SpecifyKind(…, Utc)` vóór parameterbinding.

**Drie sub-endpoints zijn bewuste 501-stubs, geen gemiste scope:** `AdminSyncFunction.Trigger` en
`AdminTeambegeleidingFunction.Doorsturen` hangen af van respectievelijk de volledige
Sportlink-ETL-pipeline (#890) en de e-mailverzend-/teamresolutielaag
(`GraphServiceClient`/`EmailGraphService`/`IEmailPersistenceRepository`/`OntvangerParser`, #889) —
geen van beide bestaat nog op de Postgres-tier. Elke stub retourneert een expliciete 501 met
verwijzing naar het blokkerende issue in plaats van een no-op te faken die stil niets doet.
`AdminSyncFunction.Status` en alle vier `AdminTeambegeleidingFunction`-endpoints op
`avg.teambegeleiding`/`avg.importlog` (GetTeams, GetBegeleiders, Import) zijn wél volledig vertaald
en werkend — alleen het Graph-afhankelijke pad is geblokkeerd.

## 12. public.speeltijden — drie ontbrekende kolommen bijgewerkt (#893)

**Bevinding:** `Database.Postgres/migrations/001_baseline.sql`'s `public.speeltijden` dekte alleen
`leeftijd`, `veldafmeting`, `wedstrijdtotaal`, `clubcode` — `dbo.Speeltijden`
(`Database/dbo/Tables/Speeltijden.sql`) heeft daarnaast `WedstrijdHelft`/`WedstrijdRust` (beide
`INT NOT NULL`) en `StandaardVoorkeurTijd` (`TIME NULL`, #666 — standaard voorkeurstijd per
leeftijdscategorie).

**Fix:** `003_speeltijden_kolommen.sql` voegt de drie kolommen toe via `ALTER TABLE ... ADD COLUMN
IF NOT EXISTS`. `wedstrijdhelft`/`wedstrijdrust` krijgen `DEFAULT 0` — uitsluitend om `ADD COLUMN
... NOT NULL` toe te staan op een tabel met eventueel al bestaande rijen, geen bewuste
business-default; elke rij die de applicatie zelf schrijft vult beide altijd expliciet.

**Bewaakt in CI:** de `fresh-db-postgres`-job controleert nu ook dat deze drie kolommen bestaan,
naast de bestaande kernobjecten- en identifier-casing-controles.

**Empirisch geverifieerd:** migratiepad tweemaal toegepast tegen een wegwerp-Postgres-16-container
— idempotent (`schema_migrations` blijft op 3 rijen), eindschema komt exact overeen met
`dbo.Speeltijden` (op naamconventie/lowercase na, §3).

## 13. Demodata-seed verhuisd naar een expliciete post-sync stap (#856, architectuurbesluit "Optie B")

**Probleem:** `Database/Script.PostDeployment1.sql` zaaide de AllStars-teams/-wedstrijden direct na
het velden-/speeltijden-blok, maar `his.teams`/`his.matches` bestaan op een verse database nog niet
— die worden pas dynamisch aangemaakt door de ETL bij de eerste Sportlink-sync
(`FunctionApp/CreateTable.cs` + `sp_CreateTargetTableFromSource`). De oude code ving dit op met een
stille `PRINT` + `RETURN`: geen foutmelding, geen afwijkende exitcode, HTTP 200 op elke route terwijl
de dagplanning, teambegeleidingspagina en testdatapagina leeg bleven.

**Besluit (eigenaar, 2026-08-30): Optie B.** De democonfiguratie (velden, veldbeschikbaarheid,
speeltijden) blijft in `Script.PostDeployment1.sql` — die tabellen bestaan altijd al. De
team-/teambegeleiding-/wedstrijddemo (die wél van `his.teams`/`his.matches` afhangt) verhuist naar
een los, expliciet aan te roepen script: `scripts/migrations/003-seed-allstars-demo-matches.sql`,
uit te voeren ná de eerste sync. `Script.PostDeployment1.sql` meldt voortaan met een `RAISERROR`
(zichtbaar in elke sqlcmd-uitvoer) of `his.teams`/`his.matches` al bestaan, in plaats van de vorige
stille `PRINT`.

**Bewust géén severity ≥ 11 op die RAISERROR:** dat zou de bestaande `-V 11`-vlag in zowel de
`fresh-db`-CI-job als de productie-deploy laten falen — en "de eerste sync is nog niet gelopen" is
op een gloednieuwe installatie een normale, verwachte toestand, geen fout. Severity 10 blijft
zichtbaar (de boodschap verschijnt altijd in de log, met een uniek `(#856)`-voorvoegsel) zonder de
deploy zelf te breken.

**Empirisch bevestigd** (wegwerp-SQL-Server-2022-container, `his.teams`/`his.matches` met de hand
aangemaakt om de na-de-eerste-sync-situatie na te bootsen): het nieuwe script zaait exact 28 teams,
28 begeleiders en 224 wedstrijden — precies de aantallen die #856's eigen acceptatiecriterium
noemt — en is idempotent (een tweede run voegt niets toe). De `fresh-db`-CI-job bootst dit scenario
nu ook zelf na en bewaakt deze drie aantallen.

**Update (#862, zie §14 hieronder):** de Postgres-tier heeft nu dezelfde tweedeling — #856's
"identiek gedrag in beide tiers"-acceptatiecriterium is voor deel 1 van #862 (het rijcontract)
bevestigd.

## 14. Postgres-tier demodata-seed, deel 1: het rijcontract (#862)

**Zelfde tweedeling als #856, nu voor de Postgres-tier.** `public.velden`/`veldbeschikbaarheid`/
`speeltijden`/`teamregels` bestaan altijd (aangemaakt door eerdere migraties), dus die demodata staat
in een gewone, automatisch toegepaste migratie: `Database.Postgres/migrations/006_allstars_demodata.sql`.
`his.teams`/`his.matches`/`avg.teambegeleiding` voor de democlub-teams/-wedstrijden hangen af van de
eerste Postgres-Sportlink-sync (dezelfde #856-les geldt hier evengoed) en staan daarom in een los,
expliciet aan te roepen script:
`scripts/migrations/003-seed-allstars-demo-matches-postgres.sql`.

**Vertaalconstructies zonder directe Postgres-tegenhanger, zoals het issue voorspelde:**
- `CHECKSUM()` → `hashtext()` (niet-cryptografische hash, zelfde soort determinisme).
- `CROSS APPLY … OFFSET … FETCH NEXT 1 ROWS ONLY` → `CROSS JOIN LATERAL (… OFFSET … LIMIT 1)`.
  `WITH ORDINALITY` op een kale `VALUES`-lijst bleek in Postgres een syntaxfout op te leveren —
  weggelaten; een letterlijke `VALUES`-lijst behoudt in de praktijk zijn schrijfvolgorde zonder
  `ORDER BY`, exact dezelfde (impliciete) aanname als de SQL Server-versie met
  `ORDER BY (SELECT NULL)`.
- De object-bestaanscontrole `OBJECT_ID(...) IS NULL` → `to_regclass(...) IS NULL`.
- `RAISERROR(…, 16, 1)` → `RAISE EXCEPTION` binnen één groot `DO $$ … $$`-blok — met opzet alles in
  één blok: `RAISE EXCEPTION` breekt het hele blok af vóórdat Postgres de latere INSERT-statements
  (die anders op een niet-bestaande tabel zouden knallen) ooit probeert te plannen. Dat is
  robuuster dan SQL Server's multi-batch-aanpak, waar een `RETURN` alleen de huidige batch afbreekt
  en een volgende `GO`-batch alsnog op de ontbrekende tabel had kunnen struikelen.
- `DATEDIFF(DAY, '19000101', @Vandaag) % 7` (DATEFIRST-onafhankelijke zaterdagberekening) →
  `(vandaag - DATE '1900-01-01') % 7` (Postgres' date-min-date levert direct een geheel aantal
  dagen op, geen `DATEDIFF`-aanroep nodig).
- `bk_teams`/`bk_matches` zijn in Postgres `GENERATED ALWAYS`-kolommen (#818) — expliciet weggelaten
  uit de INSERT-kolomlijst (in tegenstelling tot SQL Server, waar `bk_teams` een gewone, wél
  in te vullen kolom is); `teamcode`/`lokaleteamcode`/`poulecode` blijven gevuld zodat de kolom zich
  correct (en uniek per team) aflaadt — dezelfde #853-les.

**TeamRegels (#862's contract noemt 1 rij) bestond nog niet voor de democlub, op geen van beide
tiers** — toegevoegd aan zowel `Database/Script.PostDeployment1.sql` (SQL Server) als
`006_allstars_demodata.sql` (Postgres), zelfde vorm (`BufferVoor`, 60 minuten, gekoppeld aan
"AllStars Heren 1").

**Empirisch bevestigd** (wegwerp-Postgres-16-container, `his.teams`/`his.matches` met de hand
aangemaakt via de letterlijke `PostgresSchemaGenerator.GenerateHisTable`-output om de
na-de-eerste-sync-situatie na te bootsen — geen aanname, opgevraagd bij de generator zelf): velden=3,
veldbeschikbaarheid=21, speeltijden=1 (gekopieerd van een test-primaire-club), teamregels=1,
teams=28, teambegeleiding=28, wedstrijden=224 — exact het contract uit #862. Beide scripts zijn
idempotent bevestigd (tweede run voegt niets toe) en beide faalpaden (democlub ontbreekt;
his.teams/his.matches bestaan nog niet) geven de verwachte, duidelijke foutmelding. De
`PostDeployment op verse Postgres-database`-CI-job bootst dit scenario nu ook zelf na en bewaakt
dezelfde zeven aantallen.

**Nog niet gedekt (deel 2 van #862, bewust niet in deze ronde meegenomen):** de circa elf tabellen
die vandaag op GEEN van beide tiers demodata hebben (Teams-beheertabel, TeamAliassen,
TeamVoorkeurTijden, EmailTemplateInstellingen, UitgeslotenEmailAdressen, EmailVerwerking,
GeplandeWedstrijden, ClassificatieCorrectie, Zonsondergang, ImportLog, VeldTraining) — en de
dekkingscontrole die per GUI-route moet bewijzen dat er een demorij bestaat. Dat blijft open scope
op #862.

## 15. Resterende stored procedures en views — de vier AVG-opschoonprocedures (#861)

**Vier van de zes resterende procedures vertaald: de AVG-opschoonprocedures.** Zelfde
architectuurbeslissing als #818's `PostgresMergeOrchestrator`: de procedurele logica leeft in C#
(`Database.Postgres/PostgresCleanupProcedures.cs`), niet in een Postgres-functie/-procedure. Elke
methode berekent zijn tijdgrenzen éénmalig in C# (`DateTime.UtcNow`, al `Kind=Utc`) en geeft ze als
parameter mee aan zowel de UPDATE als de DELETE, zelfde reden als het origineel: een rij mag niet
tussen de twee statements door van venster wisselen.

Twee nieuwe timer-triggered functies in `FunctionApp.Postgres/Email/` (`CleanupEmailVerwerkingFunction`,
`CleanupTeambegeleidingFunction`) roepen deze methoden aan, met exact dezelfde CRON-schema's als de
SQL Server-tier (wekelijks zondag 03:00 UTC resp. maandelijks de 1e om 04:00 UTC) en dezelfde
FK-opruimvolgorde (#424: ClassificatieCorrectie vóór EmailVerwerking).

**Empirisch bevestigd** (wegwerp-Postgres-16-container, vijf voorbereide `EmailVerwerking`-rijen op
5/45/100/120/10 dagen oud plus een correctierij die een jonge rij aan een oude rij koppelt): de
correctierij werd verwijderd ondanks zijn eigen leeftijd van 10 dagen, omdat één van zijn twee
FK's naar een 100 dagen oude ouderrij wijst — precies het scenario dat het SQL Server-commentaar
beschrijft als reden voor de opruimvolgorde. Verder: 45-dagenrij geanonimiseerd maar niet
verwijderd, 100- en 120-dagenrij's verwijderd, 5- en 10-dagenrij's ongewijzigd.
`avg.teambegeleiding`/`avg.importlog` (3 resp. 1-jaars-/90-dagengrenzen) identiek bevestigd.

**Bewust niet in deze ronde:**
- `sp_CreateDateTable`/`sp_UpdateSeasonTable` — `dbo.Season`/`dbo.DateTable` hebben nog geen
  Postgres-migratie, en de primaire consument (`SeasonHelper.GetSeasonEndWeekOffsetAsync`, het
  weekbereik voor de synchronisatie) is #890's territory. Vertalen zou een nieuwe migratie plus een
  eigen tabelontwerp vergen zonder een consument die het op de Postgres-tier al aanroept.
- De drie `pub.*`-rapportageviews (`pub.Matches`, `pub.Teams`, `pub.DateTable`) — **expliciet en
  gemotiveerd laten vervallen**, conform de optie die #861 zelf aanbiedt. Een zoekactie over de
  volledige broncode levert nul consumenten op; ze bestaan uitsluitend voor externe rapportage
  buiten de applicatie. Een toekomstige externe-rapportagebehoefte kan deze alsnog toevoegen als een
  aparte, bewuste beslissing — geen omissie.

**Losstaande bevinding tijdens dit werk, niet gefixt (buiten #861's scope):**
`Database.Postgres/PostgresPlannerViewGenerator.CreateView` (#819) wordt vandaag **uitsluitend**
door `Database.Postgres.Tests` uitgevoerd — geen migratie, geen applicatiecode roept het aan. Een
verse Postgres-installatie mist de view `planner.alle_wedstrijden_op_veld_ruw` dus volledig, en
`PostgresPlannerAvailabilityReader` (die er `SELECT`-vanuit doet) zou falen met "relation does not
exist" zodra iets die klasse aanroept. Vandaag heeft niets in `FunctionApp.Postgres` die klasse als
consument (#888, de planner, is nog niet gestart), dus dit heeft nu geen runtime-impact — maar
#888 loopt hier tegenaan zodra de planner wordt aangesloten. De view kan niet vooraf via een gewone
migratie aangemaakt worden (`CREATE VIEW` vereist dat `his.matches`/`his.teams` al bestaan, en die
tabellen ontstaan pas bij de eerste sync — dezelfde #856-klasse beperking als bij demodata). Juiste
fix vermoedelijk: `CreateView` idempotent (`CREATE OR REPLACE`) uitvoeren vanuit
`PostgresPlannerAvailabilityReader.GetFieldOccupationsAsync` zelf, vlak vóór de `SELECT`. Gemeld
hier zodat #888 dit niet opnieuw hoeft te ontdekken.

> **Opgelost in §44.** De bevinding klopte en is empirisch bevestigd; de daar voorgestelde plaats
> (in de reader) bleek bij nader inzien niet de juiste — zie §44 voor de gekozen plek en waarom.

## 16. Planner-logica — eerste vertaalde endpoint: Veldbezetting (#888)

**`GET /api/planner/veldbezetting` volledig vertaald**, inclusief bewijs voor de twee valkuilen die
#888 zelf noemt:

- **`OUTER APPLY` → `LATERAL JOIN`.** `AllstarsTestDataRepository.GetAllMatchesForDatumAsync`'s
  niet-ALLSTARS-tak zocht via `OUTER APPLY (SELECT TOP 1 …) t` het team op bij een wedstrijd.
  Postgres-vertaling: `LEFT JOIN LATERAL (SELECT … LIMIT 1) t ON TRUE` — hetzelfde precedent als
  `PostgresPlannerViewGenerator` (#819), nu voor een tweede plek.
- **`LeeftijdNormalisatie.SqlExpr`** (de leeftijdscategorie-normalisatie, bijv. "Onder 13" → "JO13")
  is vertaald naar `PostgresLeeftijdNormalisatie.SqlExpr` — `+` → `||`, `LTRIM(RTRIM(…))` → `TRIM(…)`,
  en `LIKE '%Meiden'` → `ILIKE '%Meiden'` (SQL Server's default collatie maakt `LIKE` daar al
  hoofdletterongevoelig; Postgres' niet — zelfde soort lokale fix als #819's `~` → `~*`, de
  systemische collatiekwestie blijft #820's scope). **Alleen de SQL-generatie is verhuisd** — de
  pure C#-methode `Normaliseer` (geen SQL-afhankelijkheid) is bewust **niet** naar
  `Planner.Shared` verplaatst in deze PR: die verhuizing raakt ook de SQL Server-tier (twee
  bestaande call sites + een testbestand) en is een aparte, gemotiveerde refactor-beslissing,
  geen onderdeel van een Postgres-vertaling. Tot die verhuizing gebeurt bestaat de pure
  normalisatielogica dus kortstondig in twee vormen (FunctionApp en, waar nodig, opnieuw
  geïmplementeerd in de Postgres-tier) — bekende, hier vastgelegde schuld.

**Empirisch geverifieerd** tegen een wegwerp-Postgres-container: zowel het ALLSTARS-democlubpad
(`ExtractLeeftijdFromTeamNaam`-fallback, geen `his.teams`-koppeling nodig) als het pad van een
"echte" primaire club (`his.teams`-`LATERAL JOIN` + `LeeftijdNormalisatie`-vertaling) leverden de
verwachte leeftijdscategorie en duur op — voor de primaire club: teamnaam "VRC JO13-1" met
`leeftijdscategorie = 'Onder 13'` in `his.teams` leverde via de `LATERAL JOIN` +
`PostgresLeeftijdNormalisatie.SqlExpr` correct `"leeftijdsCategorie":"JO13"` en de bijbehorende
`duurMinuten`/`veldafmeting` uit `public.speeltijden` op.

**Bewust niet in deze ronde (aanzienlijk grotere, apart te verifiëren stap):** de overige elf
planner-endpoints (`CheckAvailability`, `DoordeweeksBeschikbaar`, `BevestigWedstrijd`,
`AutoPlan`/`AutoPlanToepassen`, `HerplanCheck`/`HerplanBevestig`, `ZoekWedstrijd`,
`GetTeamSchedule`) hangen af van `AvailabilityService`, `AutoPlanService`'s FieldScheduler-engine
(de eigenlijke dagplanning-optimalisatie, regels→voorkeuren→defaults-rangorde uit #666),
`RescheduleService` en `TeamScheduleService` — samen ruim 1600 regels bedrijfslogica, exclusief de
vijf repositories die ze aanroepen. `GetTeamSchedule` hangt bovendien af van `dbo.Season`, dat nog
geen Postgres-migratie heeft (zelfde gat als #861's `sp_UpdateSeasonTable`-uitstel).

## 17. E-mailpersistentie en teamresolutie — data-accesslagen vertaald (#889)

**Volledig vertaald:** `SqlEmailPersistenceRepository` (audit-trail/dedup tegen
`planner.emailverwerking`, 15 methoden), `LearningMomentRepository`
(`planner.classificatiecorrectie`-leermomenten) en de teamresolutie-repositories
`TeamCandidateRepository`/`TeamAliasLearningService` (tegen `public.teams`/
`public.teamaliassen`, #887).

**Vertaalconstructies:**
- `SCOPE_IDENTITY()` → `RETURNING id`.
- `SqlException.Number == 2601/2627` (unique violation) →
  `PostgresException.SqlState == PostgresErrorCodes.UniqueViolation`.
- De alias-upsert (`IF NOT EXISTS … INSERT ELSE UPDATE`) → `INSERT … ON CONFLICT (clubcode,
  ruwetekst) DO UPDATE SET` — `public.teamaliassen` heeft (#887) al een unique constraint op dat
  paar. `LearningMomentRepository`'s guard (`planner.classificatiecorrectie` heeft géén unique
  constraint op het paar, exact zoals de SQL Server-tier) blijft daarentegen een
  `INSERT … WHERE NOT EXISTS (…)`, dezelfde vorm als het origineel.

**Architectuurbeslissing — `TeamNaamNormalisatie` verhuisd naar `Planner.Shared`, in
tegenstelling tot #888's `LeeftijdNormalisatie`-precedent.** CLAUDE.md legt hard vast:
"Normalisatieregels horen uitsluitend in `FunctionApp/TeamResolution/TeamNaamNormalisatie.cs` —
een nieuwe teamnaam-regex elders is een architectuurschending." Een tweede, onafhankelijke kopie
bouwen (zoals bij `LeeftijdNormalisatie` bewust wél gedaan, gedocumenteerd als tijdelijke schuld)
zou die regel letterlijk overtreden. Daarom is deze keer de refactor wél uitgevoerd: `TeamNaamNormalisatie.cs`
(en de bijbehorende `TeamNaamComponenten`-record) zijn verhuisd naar `Planner.Shared/`, zelfde
precedent als `VeldResolver`/`VeldNormalisatie` (#819). Negen bestanden in de SQL Server-tier
kregen een `using Planner.Shared;` (vijf productiebestanden, twee testbestanden, plus één
volledig-gekwalificeerde verwijzing in `PlannerMatchRepository.cs` die simpelweg
`TeamNaamNormalisatie` werd). **Geverifieerd zonder regressie:** de volledige
`FunctionApp.Tests`-suite (431 geslaagd, 5 environment-gated geskipt) en de verhuisde
`TeamNaamNormalisatieTests` (nu in `Planner.Shared.Tests`, 59 tests) slagen ongewijzigd.

**Empirisch geverifieerd** tegen een wegwerp-Postgres-container (rechtstreekse aanroep van de
repository-methoden, geen HTTP-laag nodig): insert + dedup-exceptie op een dubbele MessageId,
status-/pogingen-tracking, `IsBeantwoord` losstaand van het te anonimiseren `VerstuurdNaar`-veld,
reply-detectie via `ConversationId` met JSON-veldextractie, teambegeleiding-doorstuur-audit,
classificatiecorrectie-insert + alleen-gevalideerde-voorbeelden-query, en — het expliciete
acceptatiecriterium van dit issue — een geleerde teamalias die `FindValidatedAliasAsync` pas
oplevert **na** handmatige validatie (`status = 'pending'` → `null`, na `UPDATE … SET status =
'validated'` → gevonden). Een herhaalde `LegVastAsync`-aanroep verhoogde `aantalkeergebruikt` naar
2 in plaats van een duplicaat aan te maken.

**Bewust niet in deze ronde:** `TeamCanonicalisatieService` (506 regels, orkestreert AI-
disambiguatie + de bovenstaande repositories — een aanzienlijk grotere stap) en de volledige
e-mail-AI-pijplijn (`BerichtAiService`, `BerichtResponseGenerator`, `EmailProcessorFunction`,
`EmailGraphService` — samen >2700 regels, bevatten geen directe SQL-toegang en vallen dus al
buiten #889's eigen scope-omschrijving). **Bijgewerkt:** `EmailProcessorFunction`/`EmailGraphService`
zijn sinds §52 (#972) alsnog vertaald — de mailbox stond zonder die functie sinds de §49-cutover
volledig stil.

**Nagekomen fix (#820):** deze paragraaf se `TeamCandidateRepository`/`TeamAliasLearningService`
kregen een correctness-fix ná deze ronde — Postgres' case-sensitieve default-collatie liet
`FindExactTeamAsync`/`FindValidatedAliasAsync` stilzwijgend falen bij afwijkende opgeslagen casing,
en de kale `UNIQUE`-constraints op `public.teams`/`public.teamaliassen` lieten een casing-only-
duplicaat toe. Volledige analyse en verificatie: docs/ARCHITECTUUR-TEAMRESOLUTIE.md, sectie
"Postgres-collatie-kanttekening (#820)".

## 18. Synchronisatie- en stagingpad vertaald (#890)

**Volledig vertaald:** de kernorkestratie `PostgresSyncPipeline.RunSyncAsync` — API ophalen →
`stg.teams`/`stg.matches`/`stg.matchdetails` → `his.*` — plus de bijbehorende staging-laag
`PostgresStagingRepository` en de gedupliceerde JSON-modellen (`Team`/`Match`/`MatchDetails` + 7
geneste typen) in `FunctionApp.Postgres/Sync/SportlinkModels.cs`. Twee buitenste triggers erbovenop:
`SyncFunction` (timer + `GET /api/postgres/sync-matches`) en `AdminSyncFunction.Trigger`
(fire-and-forget, zelfde vorm als de SQL Server-tier).

**Vertaalconstructies:**
- `CreateStagingTable.ExecuteAsync`/drie losse `MergeStgToHis(...).ExecuteAsync()`-aanroepen →
  `PostgresMergeOrchestrator.RecreateStgTableAsync`/`EnsureHisTableAsync`/`MergeStgToHisAsync`
  (#818) — geen nieuwe schema-/mergelaag nodig, alleen aanroepen wat er al stond.
- De drie SQL-Server-specifieke staging-guards zijn *niet* 1-op-1 vertaalbaar naar Postgres-syntax
  (`IF EXISTS ... ELSE IF ...` bestaat daar niet als top-level statement) en zijn daarom herschreven
  als expliciete opeenvolgende C#-stappen: programma's dedup-guard (`SELECT`-existence-check vóór
  `INSERT`) en uitslagen se "update-als-bestaat-anders-alleen-invoegen-als-niet-toekomstig"-guard
  (`UPDATE` eerst; bij 0 geraakte rijen alleen `INSERT` als de wedstrijddatum niet in de toekomst
  ligt). Die laatste datumvergelijking gebeurt als een ordinale C#-stringvergelijking tegen een
  vooraf berekende UTC-ISO8601-tijdstip-string, functioneel identiek aan het origineel se
  `CONVERT(NVARCHAR(50), GETUTCDATE(), 127)` maar zonder een Postgres-date-formatfunctie nodig te
  hebben.
- De hyphenated kolommen `uitslag-regulier`/`uitslag-nv`/`uitslag-s` (afkomstig uit de JSON-
  velden, zie #855's kolomcasing-precedent) moeten in elke raw-SQL-referentie expliciet gequote
  worden (`"uitslag-regulier"`) — `PostgresIdentifier.Quote` deed dat al bij het aanmaken van de
  stg-tabel.

**Empirisch geverifieerd** tegen een wegwerp-Postgres-container, met de bestaande, tier-
onafhankelijke `SportlinkFixtureServer`/`SportlinkFixtures` (#867, beide `public`, rechtstreeks
herbruikbaar zonder aanpassing): een volledige sync-run tegen de fixture levert het team, de
wedstrijd (inclusief de door /uitslagen bijgewerkte score en status) en de matchdetails correct in
`his.*` op, en `lastsynctimestamp` wordt bijgewerkt. Een tweede, identieke run bewijst idempotentie
— geen dubbele rijen in `his.matches`/`his.matchdetails`.

**Bewust niet in deze ronde — drie gedocumenteerde, tijdelijke gaten, geen equivalent gedrag:**
- **Seizoensgrenzen (`dbo.Season`)** zijn niet naar Postgres gemigreerd — er bestaat geen
  migratiebestand voor een seizoenstabel. De SQL Server-tier se eigen `SeasonHelper` valt bij elke
  fout al terug op een hardcoded `30` (weken vooruit); `SyncFunction` gebruikt diezelfde
  gedocumenteerde constante rechtstreeks voor de standaardsync. De reset-modus
  (`?reset=true&season=`), die de seizoensstart nodig heeft, geeft een expliciete 501 in plaats van
  een geraden startweek.
- **Teamcanonicalisatie** (`TeamCanonicalisatieService.RefreshAsync`, twee best-effort-aanroepen in
  het origineel) is overgeslagen — bestaat nog niet op deze tier. `his.teams`/`his.matches` worden
  wel gevuld; alleen de afgeleide, ontdubbelde canonicalisatie ontbreekt.
- **`MarkeerVervallenGeplandeWedstrijdenAsync`** is in het origineel juist ONGUARD (geen try/catch —
  een fout daar hoort de hele sync te laten falen). Op de Postgres-tier ontbreekt deze logica nog
  volledig; dit is dus een echt gat, geen best-effort-omissie zoals de teamcanonicalisatie hierboven.

## 19. Schema-drift-guard en veldresolutie-drifttest uitgebreid naar de tweede boom (#864, deel 1)

**Gedaan:**
- **Veldresolutie-drifttest**: `VeldResolutieDriftTests.GeenAfkapOpZesTekensMeer` bewaakt nu ook
  `Database.Postgres/PostgresPlannerViewGenerator.cs` (vierde plek, zie sectie 16 en de
  klasse-doc-comment van `FunctionApp/Planner/VeldResolutie.cs`). Niet omdat daar vandaag een kopie
  van de zes-tekens-truncatie staat — #819's architectuurbesluit hield veldresolutie bewust
  volledig C#-side via het tier-agnostische `Planner.Shared.VeldResolver` — maar als tripwire mocht
  die resolutie ooit alsnog SQL-side terugkomen. De regex is verbreed om zowel SQL Servers
  `m.[veld]` als Postgres' ongequote `m.veld` te herkennen.
- **Identifier-casing-guard** (zie sectie 3 hierboven):
  `scripts/ci/check-postgres-identifier-casing.sh`, nieuwe CI-stap.
- **Niet-demoklub-assertie voor Postgres**: de `fresh-db-postgres`-job insertte al een
  niet-democlub-speeltijdenrij als bronrij voor de AllStars-kopieerstap (#862), maar asserteerde
  nooit expliciet dat die rij blijft bestaan — de Postgres-tegenhanger van de SQL Server-assertie
  "Speeltijden moeten voor de primaire club bestaan, niet alleen voor de democlub" (#740) ontbrak
  dus. Toegevoegd.

**Bewust niet in deze ronde, met reden — geen gat maar een architecturale constatering:**
- **De SQL-Server-specifieke schema-drift-guard** (`Database`-DB-project vs.
  `Script.PostDeployment1.sql`) is NIET letterlijk uitgebreid naar Postgres, omdat de twee bomen
  structureel verschillen: SQL Server heeft een apart ontwerptijd-schema (het DB-project) dat kan
  uiteenlopen van wat er daadwerkelijk wordt uitgerold (`PostDeployment1.sql`) — precies het risico
  dat die guard afdekt. Postgres heeft die splitsing niet: `Database.Postgres/migrations/*.sql`
  ZIJN de uitrol, er is geen aparte kopie die kan driften. De bestaande
  `fresh-db-postgres`-CI-job dekt het analoge risico al (migraties tweemaal uitvoeren,
  `schema_migrations`-rijaantal vergelijken met het aantal `.sql`-bestanden) — dat is dus geen gat,
  maar een architecturaal andere invulling van hetzelfde doel.
- **De onderlinge boomvergelijking** ("welke tabellen/kolommen/procedures/views bestaan in de ene
  boom en niet in de andere, met een expliciete uitzonderingenlijst") is de grootste en risicovolste
  deelopgave van #864 en is nog niet gebouwd — vereist het robuust matchen van PascalCase
  SQL-Server-identifiers tegen hun lowercase Postgres-tegenhangers over twee volledig verschillende
  bestandsindelingen (los DB-projectbestand per tabel vs. cumulatieve migratiebestanden). Blijft
  open scope op #864.

## 20. Zelftest-poorten G2-G4 zijn nu echte metingen (#860-acceptatiecriterium, vervolg op #851)

**#860's kapstok-acceptatiecriterium "de zelftest (#851) haalt fase 4 tot en met 8" is deels
voldaan.** `scripts/dev/Test-PostgresTier.ps1`'s G2 (schema, eerste run), G3 (idempotentie, tweede
run) en G4 (demodata en rijtellingen) stonden allemaal op `blocked` in afwachting van de
applicatie-datalaag — die datalaag bestaat inmiddels (deels, via #887-#890), dus zijn dit nu echte
metingen in plaats van stubs.

**G2/G3** herhalen lokaal precies wat de CI-job `fresh-db-postgres` al deed: `Database.Postgres.Cli`
tweemaal draaien tegen de wegwerpcontainer, kernobjecten controleren, `public.schema_migrations`-
telling vergelijken met het aantal `.sql`-bestanden.

**G4** seedt de AllStars-demodata in dezelfde volgorde als die CI-job en toetst de rijtellingen
**altijd tegen het contract in `selftest-expectations.psd1`**, nooit tegen een `-BaselinePath`-
meting van de levende SQL Server-ontwikkeldatabase. Dat is een bewuste, empirisch onderbouwde
keuze: een baseline-vergelijking gaf tijdens het bouwen valse mismatches op `speeltijden`
(baseline 33, verse Postgres-seed 1) en `teamregels` (baseline 3, verse seed 1) — de
ontwikkeldatabase had die rijen simpelweg opgehoopt door jarenlang handmatig testen, exact de reden
waarom het contract die twee velden al als `Min` in plaats van `Exact` classificeert. Baseline-
metingen (SQL Server) worden daarom altijd als geslaagd vastgelegd — deze poort meet en legt vast,
oordeelt niet (zie het script se eigen `.PARAMETER Mode`-documentatie) — met een informatieve notitie
als de levende data van het contract afwijkt.

**Bijkomende bugfix, gevonden tijdens het empirisch testen van deze poorten:** `Wait-ForPostgres`
(#901's `-d`-fix loste al één race conditie op) kon nog steeds "gereed" melden vlak vóórdat de
server daadwerkelijk queries accepteerde — `pg_isready` slaagde, maar de eerstvolgende échte query
gaf `FATAL: the database system is starting up`. Opgelost door ná een geslaagde `pg_isready` ook een
`SELECT 1` te proberen als de `postgres`-OS-gebruiker (peer-auth via het Unix-socket in de
container, geen wachtwoord nodig) en pas "gereed" te melden zodra die ook slaagt.

**Bewust niet in deze ronde — een aparte, grotere opgave (issue #909):** G5 ("Applicatie praat
aantoonbaar met de juiste engine") en G6 ("API met inhoudsasserties") vereisen een daadwerkelijk
draaiende Azure Functions-host (`func start`) tegen `FunctionApp.Postgres` — inclusief een
Azurite-afhankelijkheid, het ontbreken van een gecommit `local.settings.json`, een koude-
startwachttijd (~20s, #175) en een eigen teardown-verantwoordelijkheid voor het functiehost-proces.
Dat is een wezenlijk ander soort risico dan G2-G4 (die alleen tegen de database praten) — een
halfbakken versie zou precies de "nep-groen"-fout opleveren die dit script elders bewust vermijdt.

## 21. Seizoensgrenzen vertaald + `MarkeerVervallenGeplandeWedstrijdenAsync` gedicht (#890, vervolg)

**`public.season` (migratie 008)** is de Postgres-tegenhanger van `dbo.Season`, gebruikt door het
nieuwe `PostgresSeasonHelper` (`FunctionApp.Postgres/Sync/PostgresSeasonHelper.cs`) —
`GetSeasonEndWeekOffsetAsync`/`GetSeasonStartWeekOffsetAsync`, functioneel gelijk aan de SQL
Server-tier se `SystemUtilities.SeasonHelper`, met dezelfde fallbackwaarden (30 resp. -40 weken) bij
een fout of lege tabel. `SyncFunction`'s standaardsync gebruikt nu het echte seizoenseinde in
plaats van een vaste `30`; de reset-modus (`?reset=true&season=`), die voorheen een expliciete 501
gaf, werkt nu volledig.

**Eenmalige seed, geen doorlopende aanvulling.** De migratie zaait bij toepassing dezelfde twee/drie
seizoenen die `dbo.sp_UpdateSeasonTable` op een verse SQL Server-installatie zou zaaien (berekend
tegen `CURRENT_DATE` op het moment van migreren, uit `public.appsettings.seasonstartmonth` met
fallback `7`). **Structureel verschil met de SQL Server-tier, bewust gedocumenteerd, geen gat dat
deze ronde oplost:** `Script.PostDeployment1.sql` roept `sp_UpdateSeasonTable` bij ELKE productie-
deploy opnieuw aan en rolt het seizoen zo automatisch door zodra de kalender twee maanden voor de
volgende start zit. Een Postgres-migratie draait precies één keer, ooit — er bestaat op deze tier
nog geen mechanisme dat vanzelf een nieuw seizoen toevoegt naarmate de tijd verstrijkt. Een
toekomstige installatie die lang genoeg meedraait zonder handmatige aanvulling van `public.season`
loopt op een gegeven moment uit de seizoenen; dat is een reëel, apart op te pakken vervolgpunt.

**Bewust niet meegenomen: `dbo.DateTable`/`sp_CreateDateTable`.** Een repo-brede zoekactie toont
precies één consument binnen de applicatie: de view `pub.DateTable` — en die drie `pub.*`-
rapportageviews zijn al expliciet en gemotiveerd laten vervallen voor de Postgres-tier (§15,
issue #861: nul consumenten binnen de applicatie). Een Postgres-tegenhanger van `dbo.DateTable` zou
dus uitsluitend een tabel zijn die nergens gelezen wordt.

**`MarkeerVervallenGeplandeWedstrijdenAsync` vertaald** naar
`FunctionApp.Postgres/Planner/Repositories/PlannerMatchRepository.cs` — bewust **uitsluitend** deze
ene methode, niet de rest van die klasse (die blijft #888's grotere, nog niet gestarte scope, zie
§16). Dit was het derde, expliciet als "echt gat" gedocumenteerde punt uit §18 (in tegenstelling tot
de teamcanonicalisatie, die in het origineel al best-effort is): `PostgresSyncPipeline.RunSyncAsync`
riep hem nog helemaal niet aan. Nu wél, en — net als het SQL Server-origineel — ONGEGUARD: een fout
hier hoort de hele sync te laten falen.

Zelfde teamalias-gebaseerde matching als het origineel (#700: de teamnaam in
`planner.geplandewedstrijden` en de teamnaam in `his.matches` gebruiken verschillende
schrijfwijzen, dus beide kanten worden via gevalideerde aliassen naar hetzelfde team herleid), met
`UPPER(...)`-vergelijkingen op de alias-tekst — zelfde precedent als #820 (Postgres' default-
collatie is case-sensitive). Een nieuwe, minimale `PostgresClubScope`
(`FunctionApp.Postgres/Planner/PostgresClubScope.cs`) levert alleen wat deze ene methode nodig
heeft (`Resolve`/`Primary`/`AddHisParams`/`HisFilter`/`RequireAccommodatieAsync`) — niet een
volledige 1-op-1-vertaling van het SQL Server-origineel se `ClubScope` (die ook `LegacyFilter` heeft
voor `avg.Teambegeleiding` en breed hergebruikt wordt door de nog niet vertaalde
planner-repositories); die uitbreiding hoort bij #888 zodra er een echte tweede consument is.

**`planner.geplandewedstrijden` mist nog steeds vier kolommen** t.o.v. `planner.GeplandeWedstrijden`
(`wedstrijdduurminuten`, `aangevraagddoor`, `opmerking`, `mta_inserted`) — alleen `mta_modified`
(migratie 009, nodig om deze ene methode te laten werken) is toegevoegd. De overige vier horen bij
functionaliteit die nog niet bestaat op deze tier (`BevestigWedstrijd`, `SaveHerplanVerzoekAsync`,
...) — toevoegen zodra die daadwerkelijk vertaald wordt, niet vooruitlopend hierop.

**Empirisch geverifieerd** tegen een wegwerp-Postgres-16-container: het migratiepad tweemaal
toegepast (idempotent — `public.season` blijft op 3 rijen, geen dubbele seed); `PostgresSeasonHelper`
geeft de echte week-offsets terug (niet de fallbackwaarden) tegen de geseede seizoenen, én valt
terug op de gedocumenteerde fallback voor een niet-bestaand seizoensjaar;
`MarkeerVervallenGeplandeWedstrijdenAsync` markeert — via `his.matches` echt aangemaakt met de
productie-schemagenerator, niet aangenomen — precies de rij die via de teamalias en de datum matcht
(inclusief een andere-hoofdlettergebruik-teamnaam om de `UPPER(...)`-vergelijking daadwerkelijk te
toetsen), laat een niet-matchende controlerij (andere datum) ongemoeid, en logt een waarschuwing
zonder te crashen wanneer de accommodatie-instelling ontbreekt — net als het origineel.

## 22. Zelftest-poorten G5/G6 draaien tegen een echte functiehost (#909)

De opgave die §20 bewust vooruitschoof is uitgevoerd voor de Postgres-tier.
`scripts/dev/Test-PostgresTier.ps1` start `FunctionApp.Postgres` nu zelf op, bewijst dat die host
met de bedoelde databaseserver praat (G5) en toetst daarna dertien API-endpoints op **inhoud**
(G6). Voor het eerst wordt in deze zelftest de applicatiecode zelf gemeten, niet alleen het schema
eronder.

### Vier ontwerpkeuzes, elk uit een empirische bevinding

**1. Een eigen poort (7098), geen overname van 7094.** De documentatie bij `Get-SelftestPorts`
legde vast dat de zelftest poort 7094 overneemt omdat `BlazorAdmin/wwwroot/appsettings.json` die URL hardcodeert. Die
reden geldt alleen voor de browsersweep (G7/G8), die via BlazorAdmin loopt. G5/G6 roepen de host
rechtstreeks aan en zijn dus aan geen enkele vastgelegde URL gebonden. Gevolg: een draaiende
ontwikkelsessie hoeft niet gestopt te worden — en kan dus ook niet vergeten worden terug te zetten.
De teardown-verantwoordelijkheid die het issue noemde vervalt daarmee, in plaats van dat er een
mechanisme voor gebouwd moest worden.

**2. Configuratie volledig via omgevingsvariabelen — empirisch bevestigd.** De open vraag uit #909
was of Azure Functions Core Tools zonder `local.settings.json` kan starten. Dat kan: alle waarden
uit het `Values`-blok worden ook uit de procesomgeving gelezen, en `Start-Process` geeft de omgeving
van de aanroepende sessie door aan het kindproces. Er komt dus niets nieuws op schijf en het bestand
van de ontwikkelaar wordt niet aangeraakt. Bevestigd met een run waarin `local.settings.json`
aantoonbaar afwezig was en de host desondanks volledig opkwam.

**3. `func` is op Windows geen executable.** `Start-Process -FilePath 'func'` faalt met
*"%1 is not a valid Win32 application"*: npm installeert `func.ps1`/`func.cmd`-shims. De start loopt
daarom via de shell, exact zoals `Start-Debug.ps1` het al deed. De procesboom is daardoor vier lagen
diep (shell → npm-shim → `func` → dotnet-worker); alleen het wrapper-PID stoppen is niet genoeg,
vandaar `Stop-FunctionHost`, die `Get-ProcessTree` gebruikt en daarna wacht tot de poort echt vrij
is. Gemeten koude start: 18 seconden, in lijn met de ~20s uit #175.

**4. Azurite is niet weg te configureren.** `FunctionApp.Postgres` heeft drie timer-triggers, en de
host weigert te starten zonder bruikbare `AzureWebJobsStorage` zodra er één niet-HTTP-trigger
geïndexeerd wordt. `Start-SelftestAzurite` hergebruikt een al draaiende Azurite en zet er anders een
wegwerpcontainer neer die de teardown weer opruimt. Bewust `UseDevelopmentStorage=true` en dus de
vaste poorten 10000-10002: de alternatieve route (een volledige connectiereeks op een eigen poort)
vereist een accountsleutel in de aanroep, en die hoort niet in een script in git.

### Wat G5 bewijst — drie bewijzen die los van elkaar staan

| Assertie | Waarom die op zichzelf niet genoeg is |
|---|---|
| `health.tier` / `health.provider` | Komt uit build-time assembly-metadata (#863). Bewijst welke bóom draait, niet met welke database die praat. |
| `health.serverversie` | De applicatie meldt een serverversie; die wordt vergeleken met wat de container zélf op `SHOW server_version` antwoordt. Sluit een andere Postgres uit, maar komt nog steeds uit de applicatie. |
| `engine.onafhankelijk-bevestigd` | Het enige bewijs dat **niet** van de applicatie komt: `pg_stat_activity` in de wegwerpcontainer toont een verbinding met `application_name = 'SportlinkFunctionAppPostgres'`. Samen met G1's negatieve controle (de SQL Server-container is aantoonbaar gestopt) sluit dit een stille terugval uit. |

Daarnaast controleert G5 dat **geen enkele functie in foutstatus staat**. Dat is geen formaliteit:
een indexeringsfout maakt de host niet onbereikbaar — de HTTP-endpoints blijven gewoon 200 geven
terwijl een andere functie stil onbruikbaar is. Precies dat werd hier gevonden (zie hieronder).

### Gevonden defect: de synchronisatietimer startte nooit bij wie het sjabloon volgde

`FunctionApp.Postgres/local.settings.template.json` miste `FETCH_SCHEDULE`. De host kwam op,
`/api/health` gaf 200, alle beheer-endpoints werkten — en `PostgresFetchAndStoreApiData` stond
permanent in foutstatus met *"'%FETCH_SCHEDULE%' does not resolve to a value"*. Alleen zichtbaar in
het opstartlog. Sjabloon aangevuld; de zelftest zet de waarde zelf ook als hij ontbreekt.

### Negatieve controle — de poort kan aantoonbaar rood worden

Een groene poort die nooit rood kán worden bewijst niets. Met `FETCH_SCHEDULE='dit-is-geen-cron'`
werd G5 rood op `geen-indexeringsfout`, weigerde G6 überhaupt te meten (een inhoudsassertie bewijst
niets zolang niet vaststaat dát deze host met de juiste engine praat), en gaf het script exitcode 1
— met een volledig geslaagde opruiming. Zonder die manipulatie: 44 geslaagd, 0 gefaald,
3 geblokkeerd, exitcode 0.

### Drie geblokkeerde asserties, elk met een echt nummer

| Endpoint | Blokkade |
|---|---|
| `api/beheer/email-log` | #858 — AVG-maskering van afzenderadressen, nog open. Bovendien staan er nul rijen in een verse database, dus er valt niets te maskeren; beide redenen wijzen naar hetzelfde issue. |
| `api/beheer/templates` | #911 (nieuw) — **geen van beide** bomen seedt e-mailsjablonen, en geen van beide endpoints voegt standaardteksten uit code toe. Op een verse database is het antwoord dus leeg, symmetrisch over de tiers. Geen Postgres-regressie. |
| `api/beheer/teams` | #890 — de verwachting haalde twee tabellen door elkaar. Dit endpoint leest `public.teams` (de canonicalisatietabel), niet `his.teams` (de ETL-historie die G4 telt en waar de 28 demoteams wél in staan). `public.teams` wordt gevuld door de teamcanonicalisatie tijdens een synchronisatie, en die is op de Postgres-tier nog niet vertaald — gedocumenteerd gat 2 van §18. |

De formulering in `selftest-expectations.psd1` is voor die laatste gecorrigeerd: "28 teams in
demomodus" suggereerde dat G4 en G6 hetzelfde meten, wat niet zo is.

### Bewust niet in deze ronde

- **G5/G6 voor de basismeting (SQL Server).** Die zou een volledige functiehost tegen de **levende**
  ontwikkeldatabase starten. Achtergrondtaken lopen bij het opstarten alsnog als hun geplande moment
  al verstreken is, dus die host kan die database wijzigen — terwijl G4 de basismeting juist bewust
  alleen-lezen houdt. Op de Postgres-tier speelt dat niet: daar is de database een wegwerpcontainer.
  Veilig maken vergt een wegwerp-SQL-Server-database of het gericht uitschakelen van de timers, en
  dat is een eigen opgave. Beide poorten melden dit in Baseline-modus als `blocked` met deze reden,
  niet als geslaagd.
- **G7/G8 (browsersweep en schrijfpaden).** Ongewijzigd bij de skill; een client-side gerenderde
  pagina is niet met een HTTP-aanroep te beoordelen. Die fase start nog steeds een eigen
  dev-omgeving op poort 7094.
- **Een permanente CI-variant van G5/G6.** De poort draait lokaal en gebruikt Docker, Azurite en
  Core Tools. Of dat op een CI-runner betaalbaar is, is niet onderzocht.

## 23. Waarschuwing: twee onafhankelijk gebouwde eindpunten kunnen alsnog dezelfde databaselaag dupliceren (#913)

**Bevinding, geen nieuwe regel — een concreet, empirisch voorbeeld van een risico dat sectie 2
al benoemt.** `AdminTeambegeleidingFunction.Import` (#887, vertaalde het beheer-endpoint) en
`Database.Postgres/TeambegeleidingImporter` (#824, vertaalde specifiek de CSV-importpijplijn) zijn
**onafhankelijk van elkaar** gebouwd — beide leveren dezelfde AVG-gevoelige databasebewerking
(delete + bulklaad + auditlog-insert voor `avg.teambegeleiding`/`avg.importlog`). Toen #887 aan de
beurt was, herbouwde het de databaselaag zelf in plaats van de al bestaande, door #824's eigen
review-fact-check-addendum geharde `TeambegeleidingImporter.ImportAsync` aan te roepen — met als
gevolg dat de atomiciteitsgarantie die #824 specifiek toevoegde (delete + bulklaad + auditlog-insert
in één transactie) in de daadwerkelijk aangeroepen productiecode ontbrak: een fout tussen de delete
en de insert-lus liet de club zonder teambegeleidingsdata achter.

**Fix:** `AdminTeambegeleidingFunction.Import` behoudt zijn eigen CSV-parselogica (kolomherkenning,
aliassen — hoort daar, is een presentatie-/inputlaag-concern), maar delegeert de databaselaag nu
naar `TeambegeleidingImporter.ImportAsync` in plaats van hem te herbouwen.

**Empirisch bevestigd** (wegwerp-Postgres-16-container): een tweede import die halverwege faalt (een
teamnaam die de `VARCHAR(100)`-kolomlengte overschrijdt, tijdens de binaire COPY) laat de data van
een voorgaande, geslaagde import nu volledig intact — vóór de fix zou de delete al zijn doorgevoerd
zonder dat de nieuwe data volledig werd weggeschreven.

**Waarom hier vermeld, niet alleen in de PR:** dit is het eerste concrete, empirisch aangetoonde
geval van de duplicatie die sectie 2 in algemene termen waarschuwt te vermijden — nuttig als
precedent voor toekomstige sub-issues die een endpoint vertalen dat al een eigen, specifiek
gebouwde datalaag elders in de Postgres-boom heeft. Controleer bij het vertalen van een nieuw
beheer-endpoint altijd eerst of er al een specifiekere, geharde implementatie bestaat vóór je de
databasebewerking zelf herbouwt.

## 24. Cross-tree tabeldekking-guard — #864 deel 2, de grootste deelopgave uit sectie 19

**#908 (deel 1, sectie 19) liet de grootste deelopgave van #864 expliciet open: "een controle die
de bomen onderling vergelijkt: welke tabellen ... bestaan in de ene en niet in de andere."** Deze
ronde levert het TABEL-niveau van die controle (kolommen, procedures en views blijven bewust
buiten deze ronde, zie hieronder).

**Nieuw script:** `scripts/ci/check-postgres-table-coverage.sh`, gewired als nieuwe stap in de
bestaande `build`-job van `.github/workflows/build.yml`, direct na de identifier-casing-guard.

**Vertaalregel bleek geen "robuuste fuzzy-matching" nodig te hebben, in tegenstelling tot wat #864
zelf als de moeilijkheid noemde.** Elke migratie die tot nu toe geschreven is vertaalt een SQL
Server-tabelnaam op precies één manier: schema `dbo` → `public` (elk ander schema ongewijzigd),
tabelnaam PascalCase → lowercase, verder letterlijk gelijk (`TeamAliassen` → `teamaliassen`,
`GeplandeWedstrijden` → `geplandewedstrijden`, ...). Een directe, deterministische naamvertaling
volstaat dus — geen Levenshtein/fuzzy-matching, geen handmatige mapping-tabel.

**Twee categorieën tabellen die bewust geen (nog geen) Postgres-tegenhanger hebben, beide
hardcoded in het script met een reden erbij:**
1. **`DYNAMISCH_AANGEMAAKT`** — de zes ETL-tabellen (`his.teams`/`matches`/`matchdetails`,
   `stg.teams`/`matches`/`matchdetails`) die `PostgresMergeOrchestrator` dynamisch aanmaakt op
   basis van `KnownEntities.cs` (#818) — geen migratie, dus geen `CREATE TABLE`-regel om te
   vinden, maar wel degelijk een echte tabel. Zelfde soort allowlist-item als de SQL
   Server-tier se eigen schema-drift-guard al had voor `stg.*`/`his.*`.
2. **`EXCEPTIONS`** — vijf tabellen, elk met een concrete, geverifieerde reden: `dbo.DateTable`
   (nul consumenten, zie sectie 21), `dbo.KnvbKalenderDag` (e-mail-AI-pijplijn, #889's
   scope-afbakening), `dbo.Zonsondergang` en `planner.HerplanVerzoeken` (allebei #888's
   FieldScheduler-/Herplan-resterende scope), en `mta.source_target_mapping` (architecturaal
   vervangen door `KnownEntities.cs`, #818 — geen Postgres-stuurtabel nodig).

**Bevinding tijdens het bouwen: geen van de drie "onverwachte" ontbrekende tabellen was
daadwerkelijk onverwacht.** Voordat de EXCEPTIONS-lijst er stond, gaf het script drie treffers
(`KnvbKalenderDag`, `Zonsondergang`, `HerplanVerzoeken`) naast de al bekende `DateTable`. Een
consumenten-check (grep over `FunctionApp/**/*.cs`) bevestigde voor alle drie dat ze uitsluitend
gebruikt worden door functionaliteit die #888/#889 zelf al als hun eigen, nog niet gestarte
resterende scope documenteren — geen nieuwe gaten, alleen een automatische bevestiging van wat al
bekend was. Dat is precies waarom dit script waarde toevoegt: de volgende keer dat zo'n tabel
onopgemerkt ontbreekt, is het geen toeval meer dat iemand het ontdekt.

**Empirisch geverifieerd** (geen database nodig, pure bestandsvergelijking):
- Schone staat: script slaagt (alle 28 SQL Server-tabellen gedekt via migratie, dynamische
  ETL-tegenhanger, of expliciete uitzondering).
- Negatieve controle 1: een uitzonderingsregel (`dbo.DateTable`) tijdelijk verwijderd uit een
  kopie van het script → faalt zichtbaar op precies die tabel.
- Negatieve controle 2: een nieuwe, fictieve SQL Server-tabel (`dbo.NieuweTestTabel`) tijdelijk
  toegevoegd, geen Postgres-tegenhanger en geen uitzondering → faalt zichtbaar, daarna
  opgeruimd (nooit gecommit).

**Bewust niet in deze ronde:**
- **Kolomniveau-vergelijking** — de tabel-check hierboven bewijst alleen dat de tabel bestaat,
  niet dat elke kolom aanwezig is. Precies het patroon dat al twee keer een echt gat opleverde
  binnen deze epic (#893: `public.speeltijden` miste drie kolommen; sectie 21: `planner.
  geplandewedstrijden` miste `mta_modified`) — beide pas ontdekt tijdens het daadwerkelijk
  vertalen van functionaliteit die de kolom nodig had, niet door een geautomatiseerde controle.
  Een aanzienlijk grotere stap dan tabelnamen: SQL Server-kolomtypen/-nullability vergelijken met
  Postgres-equivalenten heeft geen even simpele 1-op-1-vertaalregel als tabelnamen bleken te
  hebben.
- **Stored procedures en views** — de Postgres-tier heeft geen procedure-/view-bestanden op
  dezelfde manier als de SQL Server-tier (#818/#861: procedurele logica leeft in C#-klassen,
  zoals `PostgresMergeOrchestrator`/`PostgresCleanupProcedures`), dus een bestandsgebaseerde
  1-op-1-vergelijking zoals dit script voor tabellen doet, heeft daar een ander karakter en past
  niet in dit tabellen-script.
- **De omgekeerde richting** (een Postgres-tabel zonder SQL Server-tegenhanger) — geen bekend
  scenario waarin dat een reëel risico is, aangezien de Postgres-boom uitsluitend een vertaling
  ván de SQL Server-boom is, nooit andersom.

## 25. Planner-endpoint 2 van 12: het teamrooster (#888, vervolg)

Na `GET /api/planner/veldbezetting` (§16) is `GET /api/planner/team-schedule` vertaald: per zaterdag
tot het seizoenseinde of het team vrij is, plus de wedstrijdenlijst, en met `?format=html` dezelfde
leesbare pagina als op de SQL Server-tier.

Dit endpoint was tot nu toe geblokkeerd op iets wat §21 heeft opgelost: het leest het seizoenseinde,
en `public.season` bestond niet vóór migratie 008. Dat maakte het de goedkoopste volgende stap.

### Drie engineverschillen die stuk voor stuk een team stil uit het rooster laten vallen

De vertaling van `GetFutureMatchesForTeamAsync`/`TeamExistsAsync` raakt precies de plek waar SQL
Server impliciet vriendelijk is en Postgres letterlijk. Alle drie zijn ze empirisch aangetoond op
een wegwerpcontainer met een naïeve en een vertaalde variant naast elkaar:

| Verschil | Naïeve vertaling | Gevolg in productie |
|---|---|---|
| **Collatie** (#820) — SQL Server's `Latin1_General_CI_AS` vergelijkt hoofdletterongevoelig, Postgres' default niet | `teamnaam = ANY(...)` | Een wedstrijdrij met afwijkende kast (`ALLSTARS JO10 1`) verdwijnt stil uit het teamrooster |
| **Padding** — SQL Server negeert bij `=`/`IN` op `varchar` de spaties aan het eind, Postgres niet | idem | Een rij met een afsluitende spatie in `teamnaam` verdwijnt stil, terwijl dezelfde rij op de andere tier meetelt |
| **Statusvergelijking** | `m.status <> 'Afgelast'` | Een afgelaste wedstrijd die de bron als `afgelast` levert, blijft staan — de zaterdag toont dan "bezet" terwijl het team vrij is |

Vandaar `UPPER(TRIM(m.teamnaam)) = ANY(@sleutels)` en `UPPER(m.status) <> 'AFGELAST'`. De meting die
dat onderbouwt, op vier bewust lastige rijen: de naïeve variant vond `900003,900004`, de vertaalde
`900001,900002,900003,900004` — en de naïeve statusvergelijking liet de afgelaste `900003` staan waar
de vertaalde hem uitsluit.

**Let op de asymmetrie in dezelfde methode:** `planner.geplandewedstrijden.status` wordt door de
applicatie zelf gezet (kolomdefault `'Te bevestigen'`), dus daar staat bewust een kale vergelijking.
`his.matches.status` en `his.matches.teamnaam` komen uit de externe bron en staan daarom wél in
`UPPER(...)`. "Overal maar `UPPER()` zetten" zou dat onderscheid wegpoetsen.

### Twee vertaalpunten in de teamresolutie

`TeamSchrijfwijzenAsync` (#700) is in het origineel een T-SQL-batch met `DECLARE @teamId` en een
vroege `RETURN`; buiten een functie of DO-blok bestaat dat in Postgres niet. Het is nu één query met
een CTE die hetzelfde `COALESCE` van twee scalaire subquery's doet — vindt die niets, dan levert de
CTE `NULL` en matcht geen enkele rij, wat exact het gedrag van de `RETURN` is. Verder gaan de
schrijfwijzen als één array-parameter mee (`= ANY(...)`) in plaats van als een dynamisch opgebouwde
`IN`-lijst met genummerde parameters: dezelfde semantiek, maar de querytekst hangt niet meer af van
het aantal aliassen.

### Empirische verificatie

Tegen een wegwerp-Postgres-16-container met de volledige demodata-seed, via de **echte
HTTP-endpoints** op een draaiende functiehost — niet via een testharnas dat de repository
rechtstreeks aanroept: **36 asserties, 0 gefaald.** Onder meer:

- de drie engineverschillen hierboven, elk met een rij die alleen door de vertaling wordt gevonden
  respectievelijk uitgesloten;
- teamresolutie via de canonieke naam én via een gevalideerde alias, waarbij de aliastekst bewust
  *niet* naar dezelfde genormaliseerde sleutel herleidt — anders zou het aliaspad niet los van het
  normalisatiepad getoetst zijn;
- de negatieve controles: onbekend team → 404, lege parameter → 400, alias met status `pending` →
  404 (en de bijbehorende wedstrijdrij valt dan ook uit het rooster), inactief team → 404, en de
  primaire club ziet het team van de democlub niet;
- de zaterdagenlijst: elke datum is werkelijk een zaterdag, de reeks loopt tot het seizoenseinde uit
  `public.season`, en `bezet`/`oefenwedstrijd`/`vrij` klopt per dag — inclusief de zaterdag met
  uitsluitend een afgelaste wedstrijd, die `vrij` hoort te zijn;
- de zelf ingeplande oefenwedstrijd uit `planner.geplandewedstrijden`, met veldnaam via de join en
  zonder wedstrijdcode;
- `?format=html`: statuscode, content-type en de aanwezigheid van kalenderstrook en wedstrijdtabel.

### Bewust niet in deze ronde

- **De tien resterende planner-endpoints:** `CheckAvailability`, `DoordeweeksBeschikbaar`,
  `BevestigWedstrijd`, `AutoPlan`/`AutoPlanToepassen` (de FieldScheduler-dagplanning-optimalisatie,
  #666), `HerplanCheck`/`HerplanBevestig`, `ZoekWedstrijd` en `PopulateSunset`. Die hangen samen aan
  `AvailabilityService`, `PlannerShared`'s FieldScheduler-engine en `RescheduleService` — ruim 1600
  regels bedrijfslogica met schrijfpaden, en dus een wezenlijk ander verificatierisico dan de twee
  lezende endpoints die er nu staan.
- **`TeamScheduleHtmlRenderer` verhuizen naar `Planner.Shared/`.** Het is pure presentatie zonder
  databaseafhankelijkheid en zou daar passen, maar een verhuizing sleept `TeamScheduleResponse` en
  zijn twee onderliggende typen mee en raakt dus de SQL Server-boom. Zelfde afweging en hetzelfde
  antwoord als bij `LeeftijdNormalisatie.Normaliseer` in §16: een aparte refactor-beslissing, hier
  opnieuw vastgelegd als bekende schuld. `TeamNaamNormalisatie` valt hier nadrukkelijk **niet** onder
  — daarvoor geldt de "precies één plek"-regel uit CLAUDE.md, en die wordt hier gewoon uit
  `Planner.Shared` gebruikt (transitief via `Database.Postgres`).

## 26. Kleinere zusterbevinding van sectie 23: onvolledig audit-spoor op beide tiers (#916)

**Klein, bewust laag geprioriteerd, en dit keer op BEIDE tiers tegelijk** — in tegenstelling tot
sectie 23 (#913, uitsluitend Postgres) is dit geen porteringsfout maar een vooraf bestaand gebrek
dat 1-op-1 is overgenomen bij het porten (#887): `AdminTemplatesFunction.Put` deed de template-
upsert en de auditlog-insert als twee losse, niet-getransactioneerde statements, op zowel de SQL
Server- als de Postgres-tier. Gevonden door dezelfde audit-agent-aanpak die sectie 23 opleverde,
toegepast op de overige Admin-endpoints.

**Fix:** beide tiers wrappen dit nu in één transactie — hetzelfde patroon dat
`AdminSettingsFunction.Put` (beide tiers) al correct toepaste, dus geen nieuw ontwerp nodig.

**Empirisch geverifieerd op beide tiers** (wegwerp-`postgres:16`-container met alle migraties
toegepast, en een wegwerpdatabase op de lokale SQL-Server-2022-container die na afloop is gedropt —
de ontwikkeldatabase is niet aangeraakt): een `NULL` in de `NOT NULL`-kolom
`gewijzigddoor`/`GewijzigdDoor` forceert een echte constraintfout ná de geslaagde upsert. Bewust
een databasefout en géén kunstmatige C#-exception, zodat de meting het daadwerkelijke
transactiegedrag van de engine aantoont en niet alleen de C#-controlestroom.

Per tier zijn drie scenario's gemeten, zodat de fix niet alleen "groen" is maar de bug ook
aantoonbaar reproduceerbaar was:

| Scenario | Verwacht | Postgres 16 | SQL Server 2022 |
|---|---|---|---|
| A — zónder transactie (het gedrag van vóór deze fix) | template blijft gewijzigd, géén auditrij | bevestigd | bevestigd |
| B — mét transactie (de fix) | template teruggedraaid, 0 auditrijen | bevestigd | bevestigd |
| C — happy path | template + precies 1 auditrij | bevestigd | bevestigd |

Scenario A is essentieel: zonder die meting bewijst B niets: dan is niet vast te stellen of de
rollback het gedrag daadwerkelijk verandert of dat de upsert sowieso al niet bleef staan.

Verificatie liep, net als sectie 23, via een losse harness die de exacte transactielogica uit de
fix reproduceert (geen mock van `HttpRequest`/`FunctionContext` buiten een draaiende host —
zelfde beperking als daar).

**Bewust niet meegenomen:** een bredere audit van elke overige Admin-endpoint op beide tiers voor
hetzelfde patroon — de gerichte audit die dit opleverde dekte alleen de Postgres-tier-bestanden;
een systematische sweep van de SQL Server-tier op hetzelfde gebrek is geen onderdeel van epic #815
en dus niet in deze ronde meegenomen.

## 27. Cross-tree kolomdekking — #864 deel 3, het niveau waarop de epic al twee keer een gat had

Sectie 24 (#917) leverde de tabelvergelijking en noemde daarbij expliciet het volgende, nog
ontbrekende niveau: **"de tabel-check bewijst alleen dat de tabel bestáát, niet dat elke kolom
aanwezig is."** Dat is niet theoretisch — het is binnen deze epic al twee keer een echt gat
geweest, beide keren pas gevonden toen iemand toevallig functionaliteit vertaalde die de kolom
nodig had:

- **#893** — `public.speeltijden` miste `WedstrijdHelft`/`WedstrijdRust`/`StandaardVoorkeurTijd`.
- **Sectie 21** — `planner.geplandewedstrijden` miste `mta_modified`.

Deze ronde dicht dat niveau, met **twee mechanismen in plaats van één** — niet uit voorkeur voor
symmetrie, maar omdat de twee groepen tabellen structureel anders bestaan.

### Waarom twee mechanismen

| Groep | Waar de Postgres-kolommen vandaan komen | Bewaakt door |
|---|---|---|
| 19 tabellen | `Database.Postgres/migrations/*.sql` — statische DDL | `scripts/ci/check-postgres-column-coverage.sh` |
| 6 ETL-tabellen (`his.*`/`stg.*`) | `PostgresSchemaGenerator` op sync-tijd, uit `KnownEntities.cs` (#818) | `Database.Postgres.Tests/EtlKolomdekkingTests.cs` |

Voor de tweede groep bestaat geen `CREATE TABLE`-regel om te vinden. Een shellscript zou daarvoor
de C#-lijst opnieuw moeten parseren — een tweede, eigen interpretatie van dezelfde waarheid, en
precies het soort duplicatie waar sectie 23 voor waarschuwt. De test roept in plaats daarvan de
**echte generator** aan en leest de kolommen uit de DDL die in productie ook daadwerkelijk wordt
uitgevoerd. Dat is een sterker bewijs, niet alleen een goedkoper.

### De vertaalregel bleek opnieuw deterministisch

Net als bij tabelnamen (sectie 24) volstaat een directe naamvertaling: elke migratie tot nu toe
schrijft de SQL Server-kolomnaam letterlijk in lowercase over (`ClubCode` → `clubcode`,
`StandaardVoorkeurTijd` → `standaardvoorkeurtijd`). Geen fuzzy matching, geen handmatige
mapping-tabel.

**Kolom-typen en nullability blijven bewust buiten beide controles.** Dáár bestaat wél geen
1-op-1-regel (`NVARCHAR` → `VARCHAR`/`TEXT`, `BIT` → `BOOLEAN`, `DATETIME2` → `TIMESTAMPTZ`, en per
kolom een bewuste afweging — zie #854 voor een geval waarin dat een echte beslissing was). Een
naamvergelijking dekt de twee historische gaten hierboven volledig af; een typevergelijking vergt
een eigen vertaaltabel en is een aparte opgave.

### Eerste bevinding: een echte, nog niet vastgelegde naamdivergentie

De SQL Server-tier is bij de synthetische business-key-kolom **zelf inconsistent**:
`his.Teams` en `his.Matches` gebruiken `bk_<entiteit>` (`bk_teams`, `bk_matches`), maar
`his.MatchDetails` gebruikt de naam van de business-key-*kolom*: `bk_WedstrijdCode`.
`PostgresSchemaGenerator.BusinessKeyColumnName` hanteert consequent `bk_<entiteit>` voor alle drie,
dus daar heet hij `bk_matchdetails`.

Een repo-brede zoekactie bevestigt dat niets buiten de SQL Server-boom naar `bk_WedstrijdCode`
verwijst — alleen `mta.source_target_mapping` en `Script.PostDeployment1.sql`, en de Postgres-tier
heeft die stuurtabel architecturaal niet (#818). De inconsistentie spiegelen zou de Postgres-boom
dus onnodig onregelmatig maken zonder iets op te lossen. Vastgelegd als bewuste afwijking in
`EtlKolomdekkingTests.BewusteAfwijkingen`, mét die redenering.

### Vier bewust gedocumenteerde kolomuitzonderingen

`planner.GeplandeWedstrijden` mist op de Postgres-tier nog `WedstrijdDuurMinuten`,
`AangevraagdDoor`, `Opmerking` en `mta_inserted` — exact de vier kolommen die sectie 21 al als
bekend en beredeneerd uitstel benoemde (ze horen bij `BevestigWedstrijd`/`SaveHerplanVerzoekAsync`,
#888's nog niet gestarte scope). Ze staan nu met issuenummer in `KOLOM_UITZONDERINGEN`. Dat maakt
het verschil tussen "vergeten" en "uitgesteld" voor het eerst machineleesbaar in plaats van
alleen in proza.

### Empirische verificatie — vier negatieve controles, want een groene poort die niet rood kan worden bewijst niets

Schone staat: **19 tabellen en 191 kolommen** vergeleken door het script, **6 tests** groen voor de
ETL-tabellen. Dat aantal wordt door het script zelf uitgeprint, zodat een stilzwijgend
teruggevallen teller zichtbaar is.

| # | Manipulatie | Verwacht | Gemeten |
|---|---|---|---|
| 1 | Uitzondering `planner.GeplandeWedstrijden.Opmerking` verwijderd | script faalt op precies die kolom | bevestigd, exitcode 1 |
| 2 | Fictieve kolom `[NieuweTestKolom]` aan `dbo.Velden` toegevoegd | script faalt op die kolom | bevestigd, exitcode 1; daarna teruggedraaid (nooit gecommit) |
| 3 | Migratiemap teruggebracht tot één lege migratie | de "nul Postgres-kolommen geparseerd"-guard slaat aan i.p.v. alles te laten slagen | bevestigd, exitcode 1 |
| 4 | Tabelbestand zonder parseerbare kolommen toegevoegd | de "nul kolommen"-guard slaat aan | bevestigd, exitcode 1 |
| 5 | `bk_wedstrijdcode`-afwijking uitgeschakeld in de test | 1 van 6 tests faalt | bevestigd |
| 6 | Kolom `speeldagteam` uit `KnownEntities.cs` verwijderd | zowel de `his`- als de `stg`-test faalt, met de kolomnaam in de melding | bevestigd; daarna teruggedraaid |

Controle 3 is er specifiek omdat een lege Postgres-verzameling élke vergelijking triviaal zou laten
slagen — dat is precies de "nul asserties = groen"-val. Controle 4 dekt dezelfde val aan de andere
kant, en die is niet hypothetisch: een eerste versie van de parser vereiste blokhaken rond de
kolomnaam, en leverde daarom stilzwijgend **nul** kolommen op voor de twee bestanden die hun
kolommen ongequote declareren (`dbo.DateTable`, `stg.MatchDetails`). Beide vallen in de definitieve
opzet weliswaar binnen de overgeslagen tabellen, maar de faalwijze — een bestand dat geruisloos
niets bijdraagt in plaats van een fout te geven — was echt. De parser accepteert nu beide
schrijfwijzen, én een tabel die nul kolommen oplevert is expliciet een fout.

Controle 4 leverde en passant nog een bewijs op dat niet gepland was: in die uitgeklede opstelling
(alleen `001_baseline.sql`) meldde het script `dbo.Velden.VeldType` en `HeeftKunstlicht` als
ontbrekend — die twee komen in de echte boom pas via een `ALTER TABLE ... ADD COLUMN` in
`003_admin_tables.sql`. Dat toont aan dat het cumulatief samenvoegen van `ALTER TABLE`-blokken over
meerdere migraties daadwerkelijk meeweegt in de groene meting, en geen dode code is.

### Bewust niet in deze ronde

- **Kolom-typen en nullability** — zie de motivering hierboven; een eigen opgave met een eigen
  vertaaltabel.
- **Stored procedures en views** — ongewijzigd ten opzichte van sectie 24: de Postgres-tier heeft
  die niet als bestanden (#818/#861: procedurele logica leeft in C#-klassen), dus een
  bestandsvergelijking heeft daar een wezenlijk ander karakter. Dit blijft het laatste open punt
  van #864.
- **De omgekeerde richting** (een Postgres-kolom zonder SQL Server-tegenhanger) — bewust geen fout,
  en er is nu een concreet voorbeeld waarom: `stg.*` krijgt op de Postgres-tier een
  `clubcode`-kolom die de SQL Server-tegenhanger niet heeft (daar komt de ClubCode pas bij de merge
  naar `his.*`). Dat is een bewust verschil, geen drift.

## 28. `TeamCanonicalisatieService` vertaald — en daarmee is §18's tweede gedocumenteerde gat gedicht

Sectie 17 (#889, deel 1) leverde de teamresolutie-repositories en schoof `TeamCanonicalisatieService`
(506 regels) expliciet vooruit. Sectie 18 noemde datzelfde uitstel als **gedocumenteerd gat 2** van
de sync-pijplijn: `his.teams`/`his.matches` werden wel gevuld, maar de afgeleide, ontdubbelde
canonicalisatie ontbrak. Sectie 22 vond er vervolgens een derde spoor van — `api/beheer/teams` stond
in de zelftest op `blocked` omdat `public.teams` (de canonicalisatietabel) op deze tier per definitie
leeg bleef. Eén vertaling, drie eerder los vastgelegde gaten.

De service is nu vertaald naar `FunctionApp.Postgres/TeamResolution/TeamCanonicalisatieService.cs`
en aangeroepen vanuit `PostgresSyncPipeline` — tweemaal, primaire club én democlub, allebei
best-effort (try/catch), exact zoals het origineel. Dat guard-onderscheid is opzettelijk en staat
nu naast elkaar in dezelfde methode: de canonicalisatie is afgeleid werk dat de al geslaagde
ETL-run niet mag laten falen, terwijl `MarkeerVervallenGeplandeWedstrijdenAsync` er direct onder
juist ONgeguard blijft (§21).

### Vier vertaalconstructies, elk met een concrete valkuil

| Constructie | Vertaling | Wat er misgaat bij de naïeve variant |
|---|---|---|
| `MERGE ... ON (ClubCode, TeamnaamGenormaliseerd)` | `INSERT ... ON CONFLICT (clubcode, upper(teamnaamgenormaliseerd)) DO UPDATE` | Zie hieronder — de kale kolomvariant werkt niet eens |
| `WHEN MATCHED AND target.[Bron] = 'Sync'` | `WHERE teamaliassen.bron = 'Sync'` op `DO UPDATE` | Een geleerde alias met status `pending` wordt door de sync op `validated` gezet — een directe schending van CLAUDE.md's regel "een geleerde alias is pas waarheid na goedkeuring" |
| `DECLARE @teamId ... IF NULL ... RETURN` | CTE die nul rijen levert | Bestaat buiten een functie/DO-blok niet in Postgres; zelfde precedent als `TeamSchrijfwijzenAsync` (§25) |
| `GETUTCDATE()`, `LTRIM(RTRIM(...))` | `NOW()` (kolommen zijn `TIMESTAMPTZ`, #854), `TRIM(...)` | — |

**De `ON CONFLICT`-doelen moesten de expression-based indexes zijn, niet de kale kolomparen.**
Migratie `007_teams_collation_fix.sql` (#820) heeft de kale `UNIQUE`-constraints juist vervángen
door unique indexes op `(clubcode, upper(...))`. Een naïeve vertaling `ON CONFLICT (clubcode,
teamnaamgenormaliseerd)` is daardoor geen subtiel afwijkend gedrag maar een harde fout — zie de
negatieve controle hieronder.

**Eén constructie zonder 1-op-1-tegenhanger: de teruggavewaarde van de aliasupsert.** Het origineel
geeft na de MERGE onvoorwaardelijk `1` terug zodra er een team gevonden is. `RETURNING` vuurt
daarentegen alléén bij een daadwerkelijk uitgevoerde INSERT of DO UPDATE — en de `WHERE` op
`DO UPDATE` onderdrukt die update juist voor handmatige/geleerde aliassen. Zonder correctie zou zo'n
alias als "niet herleidbaar" geteld worden, precies het getal dat volgens de klasse-documentatie
bestaat zodat *"een onverwachte stijging opvalt"*. Vandaar een tweede `SELECT`-tak
(`bestaand`-CTE) die dat geval opvangt.

### Bijkomende refactor: `LeeftijdNormalisatie.Normaliseer` naar `Planner.Shared`

§16 hield de pure C#-methode bewust in de SQL Server-tier en legde vast dat ze *"waar nodig,
opnieuw geïmplementeerd in de Postgres-tier"* zou bestaan — bekende schuld. §25 herhaalde die
afweging voor `TeamScheduleHtmlRenderer`. Deze ronde is het moment waarop die schuld daadwerkelijk
zou moeten worden aangegaan: `TeamCanonicalisatieService` is de eerste Postgres-consument die niet
de SQL-generatie maar de *pure* logica nodig heeft. Een tweede, onafhankelijke kopie van deze regels
is exact de drift die `VeldResolutieDriftTests` voor de veldresolutie bewaakt, dus is de verhuizing
alsnog uitgevoerd in plaats van de schuld op te bouwen.

De splitsing is bewust langs de tier-grens gelegd, niet langs de klassegrens:

| Onderdeel | Waar | Waarom |
|---|---|---|
| `Normaliseer` (pure C#) | `Planner.Shared/LeeftijdNormalisatie.cs` | Geen database-afhankelijkheid; beide tiers gebruiken exact deze code |
| `SqlExpr` (SQL Server) | `FunctionApp/Planner/LeeftijdNormalisatieSql.cs` (hernoemd) | `+`, `LTRIM(RTRIM(...))`, `LIKE` |
| `SqlExpr` (Postgres) | `Database.Postgres/PostgresLeeftijdNormalisatie.cs` (ongewijzigd) | `\|\|`, `TRIM(...)`, `ILIKE` (#888) |

De hernoeming naar `LeeftijdNormalisatieSql` voorkomt twee gelijknamige klassen in dezelfde scope —
dat zou elke `LeeftijdNormalisatie.Normaliseer`-aanroep in de SQL Server-boom stilzwijgend naar de
verkeerde klasse laten resolven op basis van naamruimte-nabijheid. Zes call sites bijgewerkt, de
drie `Normaliseer`-tests mee verhuisd naar `Planner.Shared.Tests`. **Geen regressie:**
`FunctionApp.Tests` 429 geslaagd / 5 environment-gated geskipt, `Planner.Shared.Tests` 83 geslaagd.

Het onderscheid met §17's `TeamNaamNormalisatie`-verhuizing blijft betekenisvol: daar dwong
CLAUDE.md's harde "precies één plek"-regel de verhuizing af, hier is het een eigen afweging die de
epic zelf al twee keer had opgeschreven als openstaand.

### Empirische verificatie

Tegen een wegwerp-`postgres:16`-container, met het volledige migratiepad toegepast via
`Database.Postgres.Cli` (dus dezelfde weg als productie) en `his.teams`/`his.matches` aangemaakt
door de échte `PostgresMergeOrchestrator`/`PostgresSchemaGenerator` — geen handgeschreven DDL.
De service is rechtstreeks aangeroepen vanuit een wegwerp-consoleproject met een tijdelijke
`InternalsVisibleTo`, die na afloop is verwijderd en met een schone rebuild is bevestigd.
**21 asserties, 0 gefaald**, in negen scenario's:

| Scenario | Wat het aantoont |
|---|---|
| A — ontdubbeling | Vier `his.teams`-rijen (twee schrijfwijzen × meerdere poules) → precies twee canonieke teams; bondsnotatie gekozen als weergavenaam; beide schrijfwijzen als `Sync`/`validated`-alias; een tegenstandersnaam uit `his.matches` krijgt bewust géén alias |
| B — idempotentie | Tweede identieke run: geen extra team- of aliasrijen |
| C — goedkeuringsregel | Een alias op `bron='Leren'`/`status='pending'` blijft ongemoeid én blijft aan het team gekoppeld |
| D — #820-casing | Een opgeslagen sleutel handmatig naar lowercase gezet: de upsert matcht nog steeds, geen duplicaat, team blijft actief |
| E — sleuteldrift (#766) | Verouderde sleutel + `NULL` leeftijd/teamnummer worden hersteld, rij wordt niet gedeactiveerd |
| F — samenvoegen | Twee rijen die op dezelfde sleutel vallen: verliezer verwijderd, handmatige alias omgehangen naar de winnaar |
| G — deactivering | Team dat uit `his.teams` verdwijnt gaat op `isactief=false`, wordt niet verwijderd |
| H — clubisolatie | Een `his.teams`-rij van een andere club levert geen rij op |
| I — lege bron | Club zonder `his.teams`-rijen: waarschuwing, geen crash, geen schrijfactie |

**Drie negatieve controles** — elk een naïeve vertaling die er plausibel uitziet:

| # | Naïeve variant | Gemeten gevolg |
|---|---|---|
| 1 | `ON CONFLICT (clubcode, teamnaamgenormaliseerd)` (kale kolommen) | `42P10: there is no unique or exclusion constraint matching the ON CONFLICT specification`; élk team belandt in de per-team-catch en `public.teams` blijft leeg — A1 t/m A5 rood |
| 2 | `WHERE teamaliassen.bron = 'Sync'` weggelaten | C1 rood: de geleerde alias springt van `pending` naar `validated` — de goedkeuringsregel uit CLAUDE.md sneuvelt stil |
| 3 | `bestaand`-CTE weggelaten | Logregel gaat van `3 bronschrijfwijzen gekoppeld, 1 niet herleidbaar` naar `2 gekoppeld, 2 niet herleidbaar`: een correct gekoppelde alias wordt als onherleidbaar geteld |

Controle 1 is de belangrijkste les van deze ronde: op de Postgres-tier is de collatie-keuze uit #820
niet alleen een vergelijkingskwestie in `WHERE`-clausules, maar bepaalt hij ook welke
`ON CONFLICT`-doelen überhaupt bestaan. Elke toekomstige upsert tegen `public.teams` of
`public.teamaliassen` moet daarom op `upper(...)` infereren.

### Bewust niet in deze ronde

- **`EmailTemplateService`** (116 regels, `dbo.EmailTemplateInstellingen` + een statische cache) is
  het laatste bestand uit #889's eigen scope-omschrijving met directe databasetoegang dat nog geen
  Postgres-tegenhanger heeft. `AdminTemplatesFunction` op de Postgres-tier verwijst er al naar in
  zijn documentatie. #889 blijft daarvoor open.
- **`TeamResolver`, `TeamDisambiguationAiService`, `TeamlijstGereedheid`.** De resolutievolgorde en
  de AI-disambiguatie bevatten geen directe SQL-toegang en vallen daarmee buiten #889's eigen
  scope-omschrijving; `TeamlijstGereedheid` is de enige consument van de losse publieke
  `MigreerSleuteldriftAsync(clubCode, log)`-ingang, die daarom op deze tier bewust niet is
  meevertaald — dode code toevoegen zou hier niets bewijzen.
- **Een gecommitteerde integratietest.** De verificatie liep via een wegwerpharnas, net als bij
  §23/§26 — `Database.Postgres.Tests` referenceert `Database.Postgres`, niet `FunctionApp.Postgres`,
  dus een blijvende test vergt een nieuw testproject (`FunctionApp.Postgres.Tests`) plus CI-bedrading.
  Dat is een eigen opgave; #889's derde acceptatiecriterium ("met een test vastgelegd") is daarmee
  nog niet voldaan.
- **`api/beheer/teams` in de zelftest van `blocked` naar een echte assertie halen** (§22). Dat kan nu
  in principe, maar vereist dat de zelftest een synchronisatie draait of `public.teams` anderszins
  vult; dat hoort bij #909's vervolg, niet hier.

## 29. `FunctionApp.Postgres.Tests` — het einde van de wegwerpharnas-verificatie (#890 afgerond)

Elke ronde in deze epic tot nu toe eindigde met dezelfde zin: *"empirisch geverifieerd tegen een
wegwerp-Postgres-container"* — met een consoleproject dat na afloop werd weggegooid, plus een
tijdelijke `InternalsVisibleTo` die weer werd verwijderd (§18, §21, §23, §26, §28). Dat bewees
telkens dat het op dát moment werkte. Het bewaakte daarna niets.

Deze ronde levert het testproject dat daar een eind aan maakt, en dicht daarmee het laatste
openstaande acceptatiecriterium van #890.

### Waarom dit er niet al was, en waarom het niet triviaal was

`Database.Postgres.Tests` bestond al — maar dat project referenceert `Database.Postgres`, niet
`FunctionApp.Postgres`. Alles wat §17 t/m §28 heeft opgeleverd (de repositories, de sync-pijplijn,
de canonicalisatie) leeft in dat tweede project en is bewust `internal`: het is een Functions-host,
geen bibliotheek. Vandaar `InternalsVisibleTo("FunctionApp.Postgres.Tests")` — hetzelfde patroon
als de SQL Server-tier al had (#476), nu permanent in plaats van per meting tijdelijk.

**De fixtures zijn gedeeld via `<Compile Link>`, niet gekopieerd en niet via een
`ProjectReference`.** Drie bestanden komen uit andere projecten:

| Bestand | Herkomst | Waarom niet dupliceren |
|---|---|---|
| `SportlinkFixtureServer.cs` | `FunctionApp.Tests/Sync/` (#867) | Een tweede kopie van opgenomen API-antwoorden zou tussen de tiers uiteen gaan lopen — precies waar deze epic voor waakt |
| `SportlinkFixtures.cs` | idem | idem |
| `PostgresIntegrationTestAttributes.cs` | `Database.Postgres.Tests/` (#866) | Eén CI-variabele hoort beide suites aan te zetten, met dezelfde skip-reden |

Een `ProjectReference` naar `FunctionApp.Tests` was géén optie: dat sleept transitief `FunctionApp`
mee — de **SQL Server-tier** — en dat is exact de cross-tree-koppeling die §2 verbiedt. Bovendien
zouden de twee testassemblies dan elkaars tests ontdekken. Link-compileren geeft één bronbestand met
twee compilaties: wijzigt de fixture, dan wijzigt hij voor beide tiers tegelijk.

### Wat de suite meet — acht tests, drie klassen

| Klasse | Dekt | Criterium |
|---|---|---|
| `PostgresSyncFixtureIntegrationTests` | volledige sync tegen `SportlinkFixtureServer`: welke endpoints geraakt zijn, rijen in `his.*`, en — het kernpunt — een tweede run met identieke brondata die géén duplicaten en géén `mta_modified`-update oplevert | #890, criterium 1 |
| `PostgresEmailPersistenceIntegrationTests` | insert + dedup + status/pogingen + `isbeantwoord` los van het te anonimiseren `verstuurdnaar` | #889, criterium 3 |
| `TeamCanonicalisatieIntegrationTests` | ontdubbeling van de twee schrijfwijzen, de goedkeuringsregel voor geleerde aliassen, en #820's casing-scenario | §28, blijvend gemaakt |

De tweede sync-test verdient een aparte vermelding: hij asserteert dat na een sync ook
`public.teams`/`public.teamaliassen` gevuld zijn. Die stap staat in een `try/catch` (best-effort,
§28) — zonder deze assertie zou een volledig gebroken canonicalisatie **stil** zijn. Een guard die
fouten opslikt heeft een test nodig die controleert dat er ook echt iets gebeurd is.

### Empirische verificatie — inclusief het bewijs dat de suite zichzelf niet voor de gek houdt

Tegen een wegwerp-`postgres:16` met het volledige migratiepad via `Database.Postgres.Cli`:

- **Zonder** `POSTGRES_TEST_CONNECTION_STRING`: `Skipped: 8` — zichtbaar overgeslagen, met reden.
  Geen stilzwijgend groen.
- **Met** de variabele: **8 geslaagd, 0 gefaald**. Daarna in de database gecontroleerd dat er
  daadwerkelijk rijen stonden (`his.teams`/`matches`/`matchdetails` voor de sync-testclub, plus
  `public.teams`/`teamaliassen` uit de canonicalisatiestap) — een groene testrun die niets
  wegschrijft zou er hetzelfde uitzien.

**Vier negatieve controles**, elk gericht op één eigenschap die de suite claimt te bewaken:

| # | Manipulatie in productiecode | Verwacht | Gemeten |
|---|---|---|---|
| 1 | Canonicalisatie-aanroep uit `PostgresSyncPipeline` verwijderd | de best-effort-stap wordt zichtbaar gemist | 1 van 8 rood |
| 2 | Dedup-exceptievertaling (`SqlState`-herkenning) uitgeschakeld | een dubbele `MessageId` lekt als rauwe `PostgresException` | 1 van 8 rood |
| 3 | `WHERE bron = 'Sync'` weggelaten uit de aliasupsert | geleerde alias springt naar `validated` | 1 van 8 rood |
| 4 | Changedetectie (`WHERE ... IS DISTINCT FROM ...`) in `PostgresUpsertGenerator` uitgeschakeld | `mta_modified` wordt bij een herhaalde run alsnog bijgewerkt | 1 van 8 rood, met beide tijdstempels in de foutmelding |

Controle 4 is de belangrijkste: dat is letterlijk het acceptatiecriterium van #890 (*"geen dubbele
`mta_modified`-updates bij een herhaalde run"*). Zonder die meting zou onbekend blijven of de
assertie het verschil kán zien.

### CI-bedrading — twee stappen, met opzet verschillend

- In de bestaande `build`-job draait de suite **zonder** verbindingsvariabele: dat bewijst alleen
  dat ze compileert en start (en meldt `Skipped`), zonder dat die job een database nodig heeft.
- In `fresh-db-postgres` draait ze **mét** de variabele, tegen de instantie die die job al opzet —
  ná de AllStars-demodata-assertie. De tests schrijven onder eigen `testclub-*`-clubcodes en ruimen
  die zelf op, maar de volgorde maakt onafhankelijk van die belofte zichtbaar dat ze de
  demodatatelling niet kunnen beïnvloeden.

### Bevinding: de bestaande `Database.Postgres.Tests` laat de gedeelde database gesloopt achter

**De eerste CI-run van deze PR viel om**, met `42703: column "clubcode" does not exist`. Oorzaak:
`Database.Postgres.Tests` draaide ervóór en **dropt met opzet** een reeks tabellen in zijn setup —
`public.appsettings`/`speeltijden`/`velden`, `planner.geplandewedstrijden`,
`avg.teambegeleiding`/`importlog`, `his.teams`/`matches`/`matchdetails` en
`public.schema_migrations` — en bouwt daar minimale, synthetische versies van terug. `TestEntities`
gebruikt daarbij **dezelfde entiteitsnamen als productie** (`teams`, `matches`) met een veel kleinere
kolomverzameling, en twee van de vier varianten zonder `clubcode`.

Dat is op zichzelf legitiem: die suite test de schemagenerator, niet het schema. Het probleem is dat
de gedeelde database daarna **niet meer de vorm heeft die de jobnaam suggereert**, en dat viel tot nu
toe niemand op omdat het de laatste stap was. Gemeten in één doorloop:

| Moment | `public.appsettings` | `public.schema_migrations` |
|---|---|---|
| na `Database.Postgres.Cli` (alle migraties) | 30 kolommen | 10 rijen |
| na `FunctionApp.Postgres.Tests` | 30 kolommen | 10 rijen |
| na `Database.Postgres.Tests` | **3 kolommen** | **0 rijen** |

Twee maatregelen, met verschillende reikwijdte:

1. **Volgorde in de CI-job omgedraaid** — `FunctionApp.Postgres.Tests` draait nu vóór
   `Database.Postgres.Tests`. Deze suite heeft het echte gemigreerde schema nodig; die andere maakt
   het juist kapot. Dit lost het concrete probleem op.
2. **`HisTabelVorm` als vangnet voor `his.*`** — vóór elke test wordt gecontroleerd of
   `his.teams`/`matches`/`matchdetails` alle kolommen uit `KnownEntities` (#818) hebben; zo niet, dan
   wordt de tabel gedropt en door de productiegenerator herbouwd. Nodig omdat
   `EnsureHisTableAsync` een `CREATE TABLE IF NOT EXISTS` is en een afwijkende vorm dus niet uit
   zichzelf herstelt. Dit maakt de suite volgorde-onafhankelijk, wat lokaal net zo goed telt als in
   CI. Empirisch bevestigd: tegen een door `TestEntities` vervormde `his.*` herstelt deze stap de
   productievorm (51/22/65 kolommen).

Maatregel 1 is een pleister op de volgorde, geen structurele oplossing: een derde suite die later
wordt toegevoegd loopt tegen hetzelfde aan, en de vorm van de database hangt nu af van de
stapvolgorde in een YAML-bestand. De structurele oplossing — elke suite een eigen database, of de
sloopwerkzaamheden in een eigen schema — is vastgelegd als issue #925 en valt buiten deze ronde.

### Bewust niet in deze ronde

- **De SQL Server-suite omzetten naar hetzelfde env-gestuurde mechanisme.**
  `SportlinkFixtureSyncIntegrationTests` en `PartialFailureIntegrationTests` staan nog op
  `[Fact(Skip = "...")]` en draaien dus nergens automatisch. #866 loste dit alleen voor de
  Postgres-tier op; docs/DEVELOPER-SETUP.md §7.1 benoemt dat al als openstaand. Deze ronde raakt de
  SQL Server-boom bewust niet.
- **De overige scenario's uit §28** (sleuteldriftmigratie, samenvoegen van dubbele schrijfwijzen,
  deactivering, clubisolatie, lege bron). Van de negen daar gemeten scenario's zijn de drie
  overgenomen waarvoor §28 ook een negatieve controle heeft vastgelegd; de overige zes blijven
  gedocumenteerde eenmalige metingen. Ze toevoegen kan later goedkoop — de infrastructuur staat nu.

## 30. `EmailTemplateService` — #889 afgerond, plus een latente flake uit §29 opgelost

### Het laatste bestand uit #889's scope

`FunctionApp/Email/EmailTemplateService.cs` was het laatste bestand uit de scope-omschrijving van
#889 met directe databasetoegang zonder Postgres-tegenhanger. De vertaling zelf is klein
(`SELECT TOP 1 ... WHERE [Actief] = 1` → `... WHERE actief = TRUE LIMIT 1`,
`SystemUtilities.AppSettings.RequireClubCode` → `PostgresClubScope.Resolve`), maar twee punten
verdienen een aantekening.

**Geen `UPPER(...)`-wrap, anders dan bij de teamresolutie.** #820's collatie-fix geldt voor waarden
die uit een externe bron komen. `templatekey` en `clubcode` worden door de applicatie zelf gezet —
de Beheer-GUI en de vaste sleutels in `BerichtResponseGenerator` — dus hier staat bewust een kale
vergelijking. Zelfde onderscheid als §25 maakt tussen `planner.geplandewedstrijden.status` (kaal) en
`his.matches.status` (ge-upper't). "Overal maar `UPPER()` zetten" zou dat onderscheid wegpoetsen.

**De cachesleutel is `(clubcode, key)`, niet `key`.** Dat is geen optimalisatiedetail maar een
correctness-eis uit #706: een deployment bevat naast de productieclub ook de democlub, dus met alleen
de sleutel krijgt de tweede club het sjabloon van de eerste die het ophaalde — gegevens van een
andere club in haar eigen antwoord. Vastgelegd met een test waarin beide clubs bewust hetzelfde
sleutelwoord gebruiken.

**Een echt gat gedicht, geen kosmetiek.** `AdminTemplatesFunction` op deze tier riep
`EmailTemplateService.InvalidateCache()` niet aan — bewust, want de service bestond niet (zo stond
het ook in zijn doc-comment). Nu wel, in zowel `Put` als `Reset`. Zonder die aanroep zou een
beheerder die een tekst aanpast tot vijf minuten moeten wachten voordat de wijziging effect heeft.

**Bewust vastgelegd: `GetTemplateAsync` heeft op deze tier nog geen productieconsument.** De
e-mailverwerkingspijplijn (`BerichtResponseGenerator`) die hem op de SQL Server-tier aanroept, valt
buiten #889's scope-omschrijving en is niet vertaald. De methode is toch meegenomen omdat de klasse
anders half zou bestaan — een cache invalideren die niets vult is zinlozer dan een lezer die nog geen
aanroeper heeft — en omdat het gedrag ervan nu al met tests is vastgelegd.

### De flake die §29 achterliet, en waarom hij pas nu zichtbaar werd

Het toevoegen van een vijfde testklasse veranderde de volgorde waarin xUnit de klassen draait, en
daarmee viel `TeamCanonicalisatieIntegrationTests` (uit §29) om met
`42P01: relation "his.matches" does not exist`.

**Oorzaak:** `TeamCanonicalisatieService.RegistreerBronSchrijfwijzenAsync` leest de bronschrijfwijzen
uit een `UNION` van `his.teams` **én** `his.matches` (#700). De opzet van die testklasse zorgde
alleen voor `his.teams`. De tests slaagden in §29 uitsluitend omdat
`PostgresSyncFixtureIntegrationTests` toevallig eerder draaide en `his.matches` al had aangemaakt —
en xUnit legt die volgorde niet vast.

**Dit was dus een latente fout in de ronde van §29 zelf, niet in deze.** Hij is daar niet opgevallen
omdat vier metingen achter elkaar toevallig dezelfde volgorde kregen. Gemeten na de ontdekking, elk
tegen een eigen verse database en met identieke code: **twee runs groen, twee runs rood.** Een test
die van toevallige volgorde afhangt bewaakt niets — hij meldt alleen ruis.

**Fix:** de opzet zorgt nu voor beide tabellen via `HisTabelVorm`. **Vijf achtereenvolgende runs
tegen vijf verse databases zijn groen** (14 tests elk).

**De les, breder dan dit geval:** een integratietest moet zelf zorgen voor élke tabel die de
geteste code aanraakt — niet alleen voor de tabel die hij zelf vult. Voor `RefreshAsync` is
`his.matches` net zo goed invoer als `his.teams`, ook al schrijft de test daar niets in.

### Bewust niet in deze ronde

- **De e-mail-AI-pijplijn** (`BerichtAiService`, `BerichtResponseGenerator`, `EmailProcessorFunction`,
  `EmailGraphService` — samen >2700 regels). Die bevat geen directe SQL-toegang en valt daarmee al
  buiten #889's eigen scope-omschrijving; het is de eerstvolgende consument die `GetTemplateAsync`
  daadwerkelijk zou aanroepen. **Bijgewerkt:** `EmailProcessorFunction`/`EmailGraphService` zijn
  sinds §52 (#972) alsnog vertaald en roepen `GetTemplateAsync` inmiddels ook echt aan (via
  `BerichtPipeline.BouwTemplateAntwoord`).
- **`TeamResolver`/`TeamDisambiguationAiService`/`TeamlijstGereedheid`** — zelfde reden, zie §28.

## 31. Demodata-sjablonen op beide tiers (#911) — en waarom dit géén aanvulling op migratie 006 werd

### De blinde vlek

`GET /api/beheer/templates` leest uitsluitend `dbo.EmailTemplateInstellingen` /
`public.emailtemplateinstellingen` en voegt geen standaardteksten uit code toe. Geen van beide bomen
seedde die tabel, dus op een verse database gaf het endpoint `[]` — **symmetrisch, dus geen
Postgres-regressie.** Maar het maakte de zelftest (#909, poort G6) blind: twee heel verschillende
situaties zien er allebei uit als een lege lijst.

1. de seed levert (terecht) geen sjablonen, of
2. de sjabloonquery valt stil door een kolom-/casingfout — precies het defect dat #855 elders wél
   opleverde.

De assertie stond daarom op `blocked`. Nu seeden beide bomen dezelfde twee sjablonen voor de
democlub, en meet de poort weer iets.

### Het belangrijkste besluit: een nieuw migratiebestand, geen aanvulling op 006

De voor de hand liggende plek voor deze rijen was `006_allstars_demodata.sql` — daar staat de rest
van de AllStars-demodata al. **Dat zou elke bestaande installatie hebben laten omvallen.**

`MigrationRunner` legt van elk toegepast bestand de SHA-256 vast en faalt hard zodra een reeds
toegepast bestand later een andere checksum heeft (*"Migratie '…' is al toegepast met een andere
checksum"*, #821 — met een eigen test in `MigrationRunnerIntegrationTests`). Een migratie is
onveranderlijk zodra hij ergens is toegepast; nieuwe rijen horen in een nieuw bestand. Vandaar
`010_allstars_emailtemplates.sql`.

**Dit is een structureel verschil met de SQL Server-tier dat het onthouden waard is:**
`Script.PostDeployment1.sql` is één bestand dat bij élke deploy opnieuw draait en dus idempotent
móet zijn — daar is uitbreiden ter plekke juist het correcte patroon. Op de Postgres-tier is het
tegenovergestelde waar. Dezelfde functionele wijziging vraagt dus om een tegengestelde
werkwijze per boom.

### Één regeleinde-verschil dat de symmetrie zou hebben gebroken

De eerste versie van het SQL Server-blok gebruikte `CHAR(13) + CHAR(10)` (CRLF), de Postgres-migratie
`E'\n'` (LF). Beide tiers gaven `LEN`/`length` = dezelfde waarde niet — 157/158 tegen 150/156 — en
dat viel alleen op omdat de tellingen naast elkaar zijn gelegd. Omdat de hardcoded standaarden in
`BlazorAdmin/Pages/EmailTemplates.razor` `\n` gebruiken, is de SQL Server-kant naar `CHAR(10)`
gebracht. Zonder die correctie zou #911 een asymmetrie hebben opgelost door er een nieuwe,
subtielere voor terug te geven.

### Empirische verificatie — beide tiers, echte containers

| Meting | SQL Server 2022 (wegwerpcontainer) | Postgres 16 (wegwerpcontainer) |
|---|---|---|
| Seed toegepast | `Script.PostDeployment1.sql` tweemaal, exitcode 0 | `Database.Postgres.Cli` tweemaal, 11 migraties |
| Severity-16-fouten in het log | 0 (expliciet gescand op `Msg <n>, Level 1[6-9]` — een "success"-melding alleen is niet genoeg, zie de les uit v2.18.0.0) | n.v.t. |
| Rijen voor ALLSTARS | 2 | 2 |
| Idempotent | ja, tweede run voegt niets toe | ja, checksum ongewijzigd |

**Woord-voor-woord gelijk op beide tiers:** de sleutels, onderwerpen en bodyteksten zijn uit beide
databases gedumpt met `chr(10)` → `<LF>` genormaliseerd; de twee dumps hebben dezelfde MD5. Een
directe `HASHBYTES`/`sha256`-vergelijking zou hier misleiden — SQL Server hasht `NVARCHAR` als
UTF-16 en Postgres `text` als UTF-8, dus die geven per definitie verschillende waarden voor
identieke tekst (de em-dash in het onderwerp maakt dat zichtbaar). De tekstvergelijking is het
bewijs, de hashvergelijking was het niet.

**Drie negatieve controles op de nieuwe CI-asserties:**

| # | Manipulatie | Gemeten |
|---|---|---|
| 1 | `Actief = 0` op één sjabloon (SQL Server) | assertie faalt, sqlcmd-exitcode 16 |
| 2 | Onderwerp op `'   '` gezet (SQL Server) | assertie faalt, exitcode 16 — de `LTRIM(RTRIM(...)) > 0`-eis is dus load-bearing |
| 3 | Rijen verwijderd (Postgres) | telling 0, assertie zou falen |

Controles 1 en 2 tonen dat het niet volstaat dat de *rij* bestaat: de assertie eist een **actief**
sjabloon met een **niet-leeg** onderwerp, precies wat het endpoint teruggeeft.

### Bewust niet in deze ronde

- **Meer dan twee sjablonen.** De vijf bekende sleutels (`beschikbaarheid_check`, `herplan_verzoek`,
  `bevestiging`, `team_contact_opvragen`, `buiten_scope`) staan alle vijf als standaardtekst in de
  GUI. Twee is genoeg om de assertie betekenis te geven; alle vijf seeden zou de demodata laten
  suggereren dat een club ze allemaal heeft aangepast, terwijl een leeg veld juist betekent "gebruik
  de standaard".
- **De primaire club seeden.** Een lege sjablonenlijst is daar het correcte gedrag op een verse
  installatie: de club vult ze zelf, en tot die tijd gelden de hardcoded standaarden. De
  zelftest-verwachting voor `/email-templates` is daarom naar de democlub-context verplaatst in
  plaats van de eis te laten staan voor een context waar hij niet hoort.

## 32. Seizoensdoorrol — het gat uit §21 gedicht (#861)

§21 legde vast dat migratie `008_season.sql` `public.season` **één keer** zaait, en dat de
SQL Server-tier meer doet: `Script.PostDeployment1.sql` roept `sp_UpdateSeasonTable` bij élke deploy
opnieuw aan en rolt het seizoen zo vanzelf door zodra de kalender twee maanden vóór de volgende start
zit. Dat verschil is daar benoemd als *"een reëel, apart op te pakken vervolgpunt"*. Dit is dat punt.

### De schade, gemeten in plaats van beschreven

`PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync` rekent `ceil((MAX(dateuntil) − vandaag) / 7)`.
Zonder doorrol wijst `MAX(dateuntil)` op enig moment naar het verleden, en dan is die offset
**negatief** — een synchronisatievenster dat in het verleden eindigt, dus een sync die niets meer
ophaalt. Met de stand die een in 2025 opgezette installatie na migratie 008 heeft (seizoenen
2024-2025 en 2025-2026) en 31 augustus 2026 als datum: offset negatief vóór, positief na de doorrol.

Zonder die "vóór"-meting zou de "na"-meting alleen aantonen dát de offset positief is, niet dat de
doorrol daar iets aan verandert.

### C#, niet PL/pgSQL — en waarom migratie 008 daarmee bevroren historie wordt

`Database.Postgres/PostgresSeasonProcedures.cs` volgt het patroon van
`PostgresCleanupProcedures` (#861) en `PostgresMergeOrchestrator` (#818): procedurele logica leeft op
deze tier in C#. Dat betekent dat dezelfde berekening nu op twee plekken staat — hier én in het
DO-blok van migratie 008.

**Dat is geen drift-risico, en het is het uitleggen waard waarom niet.** Migratie 008 is per
definitie bevroren: `MigrationRunner` verifieert de SHA-256 van elk toegepast bestand en faalt hard
op een wijziging (#821, en §31 laat zien wat er gebeurt als je dat vergeet). Dat bestand is dus
historie — de vastgelegde staat van één moment — en geen regel die nog onderhouden wordt. De levende
regel staat vanaf nu uitsluitend in `PostgresSeasonProcedures`, en de klasse-documentatie zegt dat
expliciet.

De C#-implementatie dekt bovendien **beide** takken van het origineel (zaai-als-leeg én doorrol), zodat
een database waarvan `public.season` om welke reden dan ook leeg is, zichzelf herstelt in plaats van
de sync te laten terugvallen op de fallbackwaarde van 30 weken.

### Drie afwijkingen van het origineel, alle drie bewust

| Origineel | Hier | Reden |
|---|---|---|
| `IF NOT EXISTS (...) INSERT` | `INSERT ... ON CONFLICT (name) DO NOTHING` | Het origineel is een controleer-dan-schrijf met een venster ertussen. #631 laat zien wat een niet-sluitende guard oplevert: drie identieke rijen voor hetzelfde seizoen in productie. De unique index `ux_season_name` (migratie 008) maakt herhalen per definitie een no-op. |
| `DATEFROMPARTS(jaar, @SeasonStartMonth-2, 1)` | intervalrekenkunde vanaf 1 januari | Bij startmaand januari/februari levert `startMaand − 2` een ongeldige maand op. Migratie 008 loste dat al zo op; deze implementatie houdt die correctie aan. Voor de gangbare startmaanden (juli/augustus) is het gedrag identiek. |
| `EXEC dbo.sp_CreateDateTable` als slotstap | weggelaten | `dbo.DateTable` heeft binnen de applicatie precies één consument — de view `pub.DateTable` — en die drie `pub.*`-rapportageviews zijn voor deze tier al gemotiveerd laten vervallen (#861, nul consumenten). Ongewijzigd t.o.v. §21. |

### Aanroeppunt: de sync, niet een timer of een migratie

`EnsureSeasonsAsync` wordt aangeroepen op de drie plekken waar het seizoensvenster gelezen wordt —
`SyncFunction`'s timer- en HTTP-pad en `AdminSyncFunction.Trigger` — direct ná
`WaitForDatabaseAsync` en vóór `GetSeasonEndWeekOffsetAsync`. Een aparte timer-trigger zou een
venster laten waarin een handmatig getriggerde sync nog met een verouderde tabel werkt; de sync is
de enige consument, dus daar hoort de aanvulling.

De aanroep is **best-effort** (try/catch): beide leesmethoden hebben een gedocumenteerde fallback,
dus een niet-doorgerolde tabel geeft een suboptimaal maar werkend venster. Een harde stop zou de
schade juist vergroten. Zelfde afweging als bij de teamcanonicalisatie (§28), en bewust anders dan
bij `MarkeerVervallenGeplandeWedstrijdenAsync` (§21), dat ONgeguard blijft.

### Empirische verificatie

Zeven tests in `Database.Postgres.Tests/PostgresSeasonProceduresIntegrationTests.cs`, tegen een
wegwerp-`postgres:16` met het volledige migratiepad: lege tabel → twee afgeronde seizoenen; één dag
vóór de drempel → niets, op de drempel → precies het nieuwe seizoen met de juiste grenzen; viermaal
herhalen → nul toevoegingen; een toekomstig seizoen in de tabel verstoort de doorrol niet (exact de
#631-valkuil); startmaand januari/februari rekent zonder ongeldige maand; en de vóór/na-meting van
het synchronisatievenster hierboven.

**De datum is een parameter van de methode, geen `CURRENT_DATE`.** Zonder dat zou de doorrol-tak elf
maanden per jaar onbewijsbaar zijn en één maand per jaar iets anders meten — dat is de enige reden
dat die parameter bestaat.

**Negatieve controle:** met de doorrol-tak uitgeschakeld (letterlijk de situatie van vóór dit issue)
vallen 5 van de 7 tests om. De twee die groen blijven zijn die welke alleen de zaai-tak raken — ook
dat is informatie: het laat zien welke asserties het nieuwe gedrag daadwerkelijk meten.

**Bijvangst — dezelfde bevinding als #925:** deze testklasse moet `public.appsettings` eerst in de
productievorm terugbrengen (`ADD COLUMN IF NOT EXISTS`, letterlijk de regels uit migratie 003),
omdat twee andere klassen in hetzelfde project die tabel droppen en met drie kolommen terugbouwen.
Zonder die stap slaagt deze klasse alleen wanneer ze toevallig als eerste draait — hetzelfde
volgordeprobleem als §30 beschrijft, nu binnen één testproject in plaats van tussen twee.

### Bewust niet in deze ronde

- **De CI-objectvergelijking tussen beide tiers** (het derde acceptatiecriterium van #861). Voor
  tabellen en kolommen bestaat die inmiddels (§24, §27); voor procedures en views is het een
  wezenlijk andere vergelijking, omdat de Postgres-tier ze niet als bestanden heeft. Dat blijft
  open op #861 én op #864, die er hetzelfde punt over openhoudt.
- **`dbo.DateTable`/`sp_CreateDateTable`** — ongewijzigd gemotiveerd weggelaten, zie de tabel
  hierboven en §21.

## 33. Eén database per testsuite (#925) — de oorzaak weg in plaats van het symptoom

§29 legde de bevinding vast: `Database.Postgres.Tests` **dropt met opzet** een reeks tabellen
(`public.appsettings`/`speeltijden`/`velden`, `planner.geplandewedstrijden`, `avg.*`, `his.*`,
`public.schema_migrations`) en bouwt daar minimale, synthetische versies van terug. Legitiem — die
suite test de generator en de runner, niet het schema — maar het laat de gedeelde CI-database in een
niet-productievorm achter.

**Binnen twee rondes heeft dat twee keer toegeslagen**, allebei op een manier die niets met de
eigenlijke wijziging te maken had:

| Ronde | Symptoom |
|---|---|
| §29 (#924) | `42703: column "clubcode" does not exist` — `his.matches` had de `TestEntities`-vorm |
| §32 (#928) | `42703: column "clubname" does not exist` — `public.appsettings` was teruggebracht tot drie kolommen |

De maatregelen daar waren een stapvolgorde en vangnetten in de testcode: symptoombestrijding. De
vorm van de database hing af van de volgorde in een YAML-bestand, en een derde suite zou tegen
hetzelfde aanlopen.

### De maatregel

`Database.Postgres.Tests` draait in CI nu tegen een **eigen database**, `sportlink_ci_dbtests`, die
in dezelfde containerservice wordt aangemaakt en apart wordt gemigreerd.
`FunctionApp.Postgres.Tests` houdt `sportlink_ci` — de database die de job zelf al migreert en
seedt. Geen codewijziging, geen extra container: één `CREATE DATABASE` en één extra migratieronde.

### Waarom dit onzichtbaar bleef, en wat de nieuwe controle daaraan doet

**De slopende suite is altijd groen.** Hij bouwt precies terug wat hij zelf nodig heeft; hij merkt
niets van de schade die hij aanricht. Alleen wat erná draait, breekt. Zolang die suite de laatste
stap was, viel er dus per definitie niets op — en toen er een suite achter kwam, leek het alsof
díe suite stuk was.

Vandaar een expliciete controlestap ná beide suites: `sportlink_ci` moet nog de volledige
gemigreerde vorm hebben (`public.schema_migrations` gelijk aan het aantal migratiebestanden, en
`public.appsettings` met meer dan twintig kolommen).

**Empirisch, één container met twee databases:**

| | `sportlink_ci` | `sportlink_ci_dbtests` |
|---|---|---|
| Na migreren | 30 kolommen, 11 migratierijen | 30 kolommen, 11 migratierijen |
| Na beide suites | **30 kolommen, 11 migratierijen** | 5 kolommen, 0 migratierijen |

De rechterkolom hoort zo: die database is er om gesloopt te worden. Beide suites groen
(14/14 respectievelijk 82/82).

**Negatieve controle:** `Database.Postgres.Tests` bewust tegen `sportlink_ci` gedraaid — de oude,
niet-geïsoleerde situatie. De suite zelf blijft **groen** (82/82, precies het punt hierboven), maar
de nieuwe controlestap slaat aan op beide condities: 5 kolommen in plaats van 30, en 0 in plaats van
11 migratierijen. Zonder die controle zou een toekomstige terugval naar één database opnieuw
onzichtbaar zijn tot de volgende suite erover struikelt.

### De vangnetten blijven staan — met opzet

`HisTabelVorm` (§29) en de `ADD COLUMN IF NOT EXISTS`-herstelstap in
`PostgresSeasonProceduresIntegrationTests` (§32) worden niet verwijderd. In CI is hun oorzaak weg,
maar **lokaal** draaien beide suites tegen dezelfde wegwerpcontainer — precies zoals
`docs/DEVELOPER-SETUP.md` §7.2 het beschrijft. Zonder die vangnetten zou een lokale run in de
verkeerde volgorde alsnog omvallen, en dat is de run waarin een ontwikkelaar zijn wijziging voor het
eerst ziet.

Voor de seizoenstests geldt bovendien dat isolatie per *project* hun geval niet oplost: de slopende
klassen zitten in hetzelfde project en dus achter dezelfde connectiestring.

### Bewust niet in deze ronde

- **`TestEntities` hernoemen.** Die gebruikt bewust de productienamen `teams`/`matches`. Andere
  namen zouden de botsing op `his.*` wegnemen, maar de suite test juist dat de generator werkt voor
  namen zoals die in productie voorkomen. Met een eigen database is de botsing bovendien geen
  probleem meer.
- **Isolatie per testklasse** (eigen schema of database per klasse). Dat zou ook het
  binnen-project-geval oplossen dat de seizoenstests raakt, maar vereist dat
  `PostgresMergeOrchestrator` een schemanaam accepteert — een productiecode-wijziging omwille van
  een test, en dus een eigen afweging.
- **De SQL Server-tier.** Die kent hetzelfde patroon niet: `PartialFailureIntegrationTests` en
  `SportlinkFixtureSyncIntegrationTests` draaien nergens automatisch (§29's laatste punt).

## 34. De laatste opschoonprocedure: `sp_CleanupAppSettingsAudit` (#781/#861)

**Een AVG-gat, geen ontbrekend gemak.** `public.appsettingsaudit` bestond al sinds migratie 004 en
groeide bij elke instellingswijziging. De tabel bevat op meerdere plekken persoonsgegevens:
`gewijzigddoor` is een Entra-gebruikersnaam/UPN, en `oudewaarde`/`nieuwewaarde` kunnen
e-mailadressen bevatten (bijvoorbeeld bij een wijziging van `GraphMailbox` of
`EmailReviewRecipient`). De bewaartermijn-instelling `appsettingsauditbewaardagen` bestond eveneens
al — maar op deze tier handelde niets er ooit naar. Rijen bleven dus onbeperkt staan, in strijd met
AVG art. 5 lid 1 sub e (opslagbeperking).

Dit was een **bekend, gedocumenteerd** gat en geen verrassing: migratie 004 benoemt het zelf
expliciet als "een van de resterende procedures uit #861". Sectie 15 leverde vier van de vijf
opschoonprocedures; dit is de vijfde en laatste.

**Vertaling.** `PostgresCleanupProcedures.CleanupAppSettingsAuditAsync` + een maandelijkse
timerfunctie (`0 30 4 1 * *`, een half uur ná de bestaande teambegeleiding-opschoning op 04:00,
zodat de twee taken niet op dezelfde verbinding overlappen). De drietraps-terugval van het
origineel is letterlijk overgenomen en in één query samengevat met `COALESCE` over twee geordende
subselects:

1. De primaire club (niet `ALLSTARS`) is leidend voor deployment-brede instellingen — zelfde
   patroon als #598/#740.
2. Vangnet als alleen de democlub bestaat (verse fork vóór de eerste echte configuratie).
3. `NULL` of `<= 0` valt terug op de gedocumenteerde default van 730 dagen. Bewust een default en
   géén "dan maar niets opruimen": dat laatste zou een configuratiefout stilzwijgend in een
   AVG-overtreding laten ontaarden. `NULLIF(GREATEST(kolom, 0), 0)` implementeert stap 3 zonder een
   extra round-trip.

De tijdgrens wordt éénmalig in C# berekend en als parameter meegegeven — zelfde keuze als de vier
procedures uit sectie 15. `tijdstip` is `TIMESTAMPTZ` en de grens een UTC-`DateTime`, dus de
vergelijking is absoluut: een databaseserver in een andere tijdzone (de zelftest draait bewust op
Europe/Amsterdam, #854) verschuift het venster niet.

**Zesde procedure, zelfde mal (#1114).** `public.sportlinkmutationaudit` (migratie 013, epic #986)
had hetzelfde gat: een `triggerddoor`-kolom met de UPN van de beheerder en geen enkele opschoning.
`CleanupSportlinkMutationAuditAsync` is een letterlijke afgeleide van de vijfde — dezelfde
drietraps-terugval, dezelfde enkele DELETE, eigen kolom `sportlinkmutationauditbewaardagen`
(migratie 017, default 365 dagen: één seizoen plus marge, korter dan de 730 van appsettingsaudit
omdat dit log per mutatie groeit en Sportlink de mutatie zelf ook logt). Timer `0 45 4 1 * *`, een
kwartier na deze. Op de SQL Server-tier is `dbo.sp_CleanupSportlinkMutationAudit` de tegenhanger,
en `check-postgres-procedure-view-coverage.sh` kent de mapping. Zes integratietests naar het model
hieronder, plus één die bewijst dat een verse `Pending`-rij blijft staan.

**Empirisch geverifieerd, met aantoonbaar onderscheidend vermogen.** Zes blijvende integratietests
in `FunctionApp.Postgres.Tests` (niet langer een wegwerpharnas, zie sectie 30) dekken alle drie de
terugvaltrappen, de lege-`appsettings`-rand, en idempotentie. Elke test controleert niet alleen wát
verdwijnt maar ook wát blijft staan — een test die enkel "er is iets verwijderd" aantoont, zou een
procedure die *alles* verwijdert groen laten.

Dat die tests werkelijk iets bewaken is apart gemeten in plaats van aangenomen: met de
`<= 0`-terugvalbescherming tijdelijk uit de code gesloopt worden er twee rood (de 400-dagenrij
verdwijnt dan ten onrechte). Een bewaking die niet rood kán worden, bewijst niets.

**Bewust niet in deze ronde:** de drie `pub.*`-views (`pub.Matches`, `pub.Teams`, `pub.DateTable`)
hebben geen Postgres-tegenhanger. Dat is een beredeneerde uitzondering, geen gat: geen enkele
regel applicatiecode consumeert ze (geverifieerd met een repo-brede grep over `FunctionApp/`,
`FunctionApp.Postgres/` en `BlazorAdmin/`) — het zijn consumentgerichte views op de SQL
Server-ETL-boom. `sp_CreateDateTable` deelt diezelfde status (zie sectie 32). Daarmee resteert voor
de cross-tree-vergelijking van procedures en views (#864) alleen nog het *geautomatiseerd* bewaken
van deze mapping; de inhoudelijke inventarisatie is met deze sectie compleet.

## 35. Planner-regressienet vóór verdere portering (#888, slice 1)

**Uitgangspunt: er stond 873 regels plannercode op de Postgres-tier met nul blijvende testdekking.**
Een verkenning met zeven parallelle lezers over de plannerlaag leverde één conclusie die de volgorde
van al het resterende #888-werk bepaalt: elke volgende port zou zichzelf moeten bewijzen tegen een
laag die zelf nergens tegen afgerekend werd. Vandaar deze slice vóór verdere vertaling, niet erna.

**Drie dingen geleverd.**

1. **Zelftestpoort G6 stond daadwerkelijk rood.** `api/beheer/templates` was bij #911/#927 aan
   `selftest-expectations.psd1` toegevoegd zonder bijbehorende assertie-tak in
   `Test-PostgresTier.ps1`. De `default`-tak markeert dat expliciet als fout — *"een endpoint
   toevoegen aan de verwachtingenlijst zonder assertie hier is een fout, geen overslag"* — en dat
   werkte precies zoals bedoeld: het ontwerp ving een echte omissie. Nu voorzien van een
   inhoudscontrole (niet-leeg onderwerp én de twee verwachte democlubsleutels).
2. **De twee bestaande planner-endpoints stonden in het geheel niet in de verwachtingenlijst.** Ze
   waren dus door niets gedekt — terwijl juist de plannerlaag stil fout kan gaan: een lege
   veldbezetting leest als een rustige dag, niet als een defect. Toegevoegd, met een
   `{EERSTVOLGENDE_ZATERDAG}`-token dat het script vervangt door de dag waarop de demoseed zijn
   eerste speelronde zet. Bewust een token en geen vaste datum: die zou binnen een week verlopen en
   de meting stilzwijgend op een lege dag laten uitkomen.
3. **Het enige geporte schrijfpad is vastgelegd**: zes tests op
   `MarkeerVervallenGeplandeWedstrijdenAsync`, inclusief de #692-regel dat alleen een
   `validated`-alias mag koppelen, de #820-hoofdletterongevoeligheid, de accommodatiefilter, en de
   overslaan-zonder-fout-route als de accommodatie-instelling ontbreekt.

**Twee assertiefouten die alleen door daadwerkelijk uitvoeren aan het licht kwamen.** Beide waren
plausibel en zouden als groen zijn gepasseerd zonder de run:

- De eerste versie eiste dat elke veldbezettingsrij een veld noemt. De demoseed plant niets op een
  veld in, dus `veld` is daar altijd `NULL` — de assertie was aantoonbaar fout, niet de code. Nu
  wordt op teamnaam + aanvangstijd getoetst: de inhoud die uit `his.matches` moet komen en die bij
  een porteerfout wegvalt.
- De teamrooster-assertie kreeg een 404. Oorzaak: `TeamExistsAsync` resolvet via
  `public.teams`/`public.teamaliassen` — de *canonieke* lijst — en die vult de demoseed niet
  (geverifieerd: nul `INSERT`s in beide seedbestanden). De 404 was dus correct gedrag van het
  endpoint. Dat legt een bredere leemte bloot: in de zelftest is **alles wat op teamresolutie leunt
  structureel onbereikbaar**, omdat er geen synchronisatie draait en canonicalisatie dus nooit
  plaatsvindt. Vastgelegd als issue #931; het endpoint staat nu met een `Blocked`-markering in de
  lijst in plaats van weggelaten, zodat zichtbaar blijft dat hier dekking hoort te komen.

**Onderscheidend vermogen apart gemeten.** Met de `validated`-eis tijdelijk uit de query gesloopt
(`status = 'validated'` → `status IS NOT NULL`) wordt precies één test rood: de test die die regel
bewaakt. Daarna hersteld en opnieuw groen geverifieerd. Volledige suite 26/26; de zelftest ging van
G6-rood naar 46 geslaagd, 0 gefaald, 3 geblokkeerd.

**Bewust niet in deze ronde.** De verkenning leverde een plan van negentien slices op, waarvan dit
de eerste is. De grootste bevinding daaruit hoort hier genoteerd: #888 is géén "negen endpoints
porten". Onder die negen wrappers (~340 regels) ligt ~1570 regels service, ~1360 regels repository
en een planningsmotor, en van de ~34 repositorymethoden op de SQL Server-tier staan er vandaag zes
op de Postgres-tier — twee volledige repositories hebben er nog geen bestand. Twee
architectuurbesluiten (waar de pure planningsmotor woont; waar de planner-DTO's wonen) horen
volgens het precedent van §25 buiten een implementatie-PR te worden genomen, en twee harde
schemablokkades vragen nieuwe migratiebestanden omdat `MigrationRunner` hard faalt op elke
checksumwijziging.

## 36. Procedures/views-dekkingsguard — de derde en laatste bomen-vergelijking (#864, deel 4)

**Sluit de bomen-vergelijking af.** Deel 2 (#917) bewaakt tabellen, deel 3 (#922) kolommen; dit is
het derde en laatste stuk: stored procedures en views. Sectie 34's inventarisatie was compleet maar
handmatig — een nieuwe SQL Server-procedure zonder Postgres-tegenhanger zou onopgemerkt blijven tot
iemand de Postgres-tier daadwerkelijk gebruikt, exact het patroon waarom #893 en de
`mta_modified`-kolom uit sectie 21 allebei pas gevonden werden toen iemand toevallig
functionaliteit vertaalde die de ontbrekende kolom nodig had.

**Waarom dit geen bestandsvergelijking kan zijn, in tegenstelling tot de andere twee.** Een
tabelnaam vertaalt mechanisch (`dbo` → `public`, PascalCase → lowercase) — daar volstaat een
regel. Een procedurenaam wordt een willekeurige C#-methodenaam
(`sp_CleanupAppSettingsAudit` → `CleanupAppSettingsAuditAsync`, `sp_MergeStgToHis` →
`MergeStgToHisAsync` op een generieke orchestratorklasse). `scripts/ci/check-postgres-procedure-view-coverage.sh`
vergelijkt daarom tegen een expliciete `MAPPING`-array (object → bestand + regex op het C#-symbool),
met dezelfde `EXCEPTIONS`-discipline als de andere twee scripts voor de vier objecten zonder
tegenhanger (`dbo.sp_CreateDateTable`, `pub.DateTable`, `pub.Matches`, `pub.Teams` — zie sectie 34).

**Een echte bug gevonden tijdens het bouwen, niet alleen tijdens het gebruiken.** De eerste versie
matchte het C#-symbool met een ongeankerde `grep -qE`. Een opzettelijke test — `EnsureSeasonsAsync`
hernoemen naar `EnsureSeasonsAsyncV2` — hoorde de mapping als verweesd te melden, maar de oude naam
bleef als *voorvoegsel* van de nieuwe staan en de ongeankerde regex matchte hem gewoon door. Pas na
`\b...\b`-woordgrenzen toe te voegen faalde die test zoals bedoeld. Zonder die correctie zou het
script bij precies het scenario dat het moet vangen — een hernoemd of verwijderd symbool — stil
blijven doorgaan. Alle drie faalscenario's zijn nu apart bewezen: een nieuwe procedure zonder
mapping/exceptie, een verweesde mapping (het symbool bestaat niet meer), en een mapping die naar
een niet-bestaand bestand wijst.

## 37. Negen planner-endpoints: van stille 404 naar eerlijke 501 (#888)

**Geen porteringsslice, maar een eerlijkheidsslice.** Vóór deze sectie riep een aanroep naar
bijvoorbeeld `POST /api/planner/auto-plan` op de Postgres-tier een generieke 404 op — de route
bestond simpelweg niet. Dat leest als "deze functionaliteit bestaat niet" in plaats van "dit is een
bewust nog niet vertaald stuk, en hier is precies waarom niet". Zelfde discipline als
`AdminTeambegeleidingFunction.Doorsturen` en het vroege `AdminSyncFunction.Trigger` (vóór #890):
een expliciete 501 met de daadwerkelijke reden, nooit een neppe 200 en nooit een stille 404.

**Negen endpoints, drie soorten gaten** — elk geverifieerd tegen de daadwerkelijke broncode, niet
aangenomen:

| Gat | Endpoints | Wat precies ontbreekt |
|---|---|---|
| Twee ontbrekende repositories | `CheckAvailability`, `DoordeweeksBeschikbaar`, `HerplanCheck` | `PlannerAvailabilityRepository` en `TeamRulesRepository` hebben nog geen bestand op deze tier (bevestigd door `AvailabilityService`/`RescheduleService`'s eigen aanroepen na te lopen) |
| Twee ontbrekende schematabellen | `PopulateSunset`, `HerplanBevestig` | `dbo.Zonsondergang` en `planner.HerplanVerzoeken` — dezelfde twee die al als gemotiveerde uitzondering in `scripts/ci/check-postgres-table-coverage.sh` staan, hier dus geen nieuwe aanname |
| Ontbrekende `PlannerMatchRepository`-methoden | `BevestigWedstrijd`, `ZoekWedstrijd`, `HerplanBevestig` | `SavePlannedMatchAsync`/`FindMatchAsync`/`FindMatchByCodeAsync` — het kleinste gat van de drie: `planner.geplandewedstrijden` en `his.matches` bestaan al |
| De FieldScheduler-engine | `AutoPlan`, `AutoPlanToepassen` | `PlannerShared.cs` (538 regels) — de architectuurbeslissing waar die woont staat nog open (zie §35's gedeelde randvoorwaarden) |

**Empirisch, permanent en zonder database.** Elke stub roept alleen `EasyAuthHelper.RequireAdmin`
aan (pure headerinspectie — geen `FunctionContext`-gebruik) en retourneert onvoorwaardelijk 501.
Twaalf tests in `FunctionApp.Postgres.Tests/PlannerFunctionStubTests.cs` roepen de statische
methoden daarom rechtstreeks aan met een `DefaultHttpContext`, zonder functiehost of container —
en draaien dus in élke `dotnet test`-uitvoering, niet alleen de containergebonden CI-job. Beide
negatieve controles apart bewezen: de statuscode naar 200 wijzigen laat precies de negen
statuscode-asserties rood gaan; de tekst van één 501-reden inkorten laat precies de bijbehorende
inhoudsassertie rood gaan.

**Route-pariteit statisch bevestigd, niet aangenomen.** Een script vergelijkt naam, HTTP-werkwoord
en route van elk `[Function(...)]`-attribuut tussen de twee `PlannerFunction.cs`-bestanden: alle elf
planner-specifieke endpoints komen exact overeen. `Health` is het enige verschil — bewust, want die
route loopt op de Postgres-tier via een apart bestand (`HealthFunction.cs`, #863).

## 38. De twee openstaande architectuurbeslissingen uit §35/§37 zijn genomen (#888)

**Waar woont de FieldScheduler-planningsmotor?** Naar `Planner.Shared` — verhuisd, niet
gedupliceerd. De motor was al aantoonbaar SQL-vrij (zie §37) en delegeerde de veldnaam-matching al
aan `Planner.Shared.VeldResolver` sinds #819; alleen de motor zelf en de constanten/helpers eromheen
stonden nog op de SQL Server-tier. Zelfde precedent als `TeamNaamNormalisatie` (#889): logica zonder
tier-afhankelijkheid hoort op precies één plek te staan, niet op twee plekken met een driftguard
ertussen.

**Waar wonen de planner-DTO's?** Gesplitst, niet alles naar één kant. De motor construeert
`SlotToewijzing` rechtstreeks (via `ToSlotToewijzing`) en werkt op `VeldInfo`,
`VeldBeschikbaarheidInfo`, `BestaandeWedstrijd`, `TeamRegel`, `TeamVoorkeurVeld` en `Speeltijd` —
die zes modellen plus `VeldSoort`/`VeldTypeClassificatie` (dezelfde "één plek"-regel als de motor
zelf, #705/#707) staan nu in `Planner.Shared/PlannerDomeinModellen.cs`. De HTTP-wire-contracten
(`CheckAvailabilityRequest`, `AutoPlanResponse`, `HerplanCheckRequest`, ...) blijven bewust per tier
eigen bestanden — die zijn JSON-vormgeving van één specifiek endpoint, geen gedeelde rekenlogica.
Zelfde onderscheid als `TeamScheduleModels.cs` (gedupliceerd, §16) tegenover `TeamNaamNormalisatie`
(verhuisd, §17).

**Waarom de klasse "PlannerShared" heet terwijl ze al in de namespace `Planner.Shared` zit** — een
bewuste, risicoarme keuze: alle circa zestig bestaande aanroepen in `AvailabilityService`,
`RescheduleService`, `AutoPlanService`, `SportlinkApiClient` en vier testbestanden bleven zo
ongewijzigd werken met alleen een `using Planner.Shared;`-toevoeging. Geen enkele aanroepnaam
hoefde te veranderen — alleen twee call sites die de inmiddels-verwijderde,
`CheckAvailabilityResponse`-getypeerde overload van `AddWeekdayWarning` gebruikten, moesten naar de
al bestaande `List<string>`-overload (die overload was zelf al tier-neutraal).

**`FunctionApp.Postgres` kreeg een directe `ProjectReference` naar `Planner.Shared`** — voorheen
kwam die er alleen transitief binnen via `Database.Postgres` (voor `VeldResolver`/`VeldNormalisatie`/
`TeamNaamNormalisatie`). Een impliciete afhankelijkheid zou de bedoelde gelaagdheid omkeren zodra
planner-code op deze tier de motor daadwerkelijk aanroept.

**Geverifieerd, niet aangenomen.** De volledige bestaande `FunctionApp.Tests`-suite draaide vóór en
ná de verhuizing in twee losse git-worktrees (één op `origin/develop`, één op deze branch) om een
exacte, onafhankelijke vergelijking te forceren: **429 geslaagd, 5 geskipt, 0 gefaald — in beide
worktrees identiek.** `Planner.Shared.Tests` (83 tests, incl. `TeamNaamNormalisatieTests`) en
`FunctionApp.Postgres.Tests` blijven eveneens groen; de volledige oplossing bouwt schoon.

**Bewust niet in deze ronde:** de bestaande testbestanden die de motor rechtstreeks testen
(`PlannerSharedTests.cs`, `VeldBezettingHerplanTests.cs`, `FieldOccupationFilterTests.cs` in
`FunctionApp.Tests`) zijn niet meeverhuisd naar `Planner.Shared.Tests`. Ze testen nu weliswaar
tier-agnostische code, maar staan nog op de oude plek — een cosmetische opruiming die de risicoarme
scope van deze refactor niet hoefde te vergroten. Blijft open scope voor een latere, kleine slice.

## 39. Twee repositories erbij: beschikbaarheid en teamregels (#888)

**De laatste twee ontbrekende repositories uit §35's inventarisatie.** `PlannerAvailabilityRepository`
en `TeamRulesRepository` bestonden nog helemaal niet op deze tier — met deze sectie staat de volledige
repositorylaag onder de scheduling-engine (§38) op zijn plek, op de nog niet vertaalde
`PlannerMatchRepository`-methoden (`FindMatchAsync`, `FindMatchByCodeAsync`,
`SavePlannedMatchAsync`, `SaveHerplanVerzoekAsync`) na.

**`PostgresClubScope` kreeg `AddClubParam`.** De eerdere, minimale versie had alleen
`HisFilter`/`AddHisParams` (voor `his.*`-tabellen) — deze repositories raken tabellen met
`clubcode NOT NULL` (`public.veldperiode`, `public.veldbeschikbaarheid`, `public.veldtraining`, en
de `clubcode`-uitvoerkolom van de view) en hebben het strikte predicaat nodig.

**De risicovolste stap: veldresolutie in C#, niet in SQL.** `planner.alle_wedstrijden_op_veld_ruw`
levert voor "Competitie"-rijen bewust de ruwe Sportlink-veldstring terug in plaats van een
veldnummer (#819's architectuurbeslissing — zie `PostgresPlannerViewGenerator`'s eigen
doc-comment). `PlannerAvailabilityRepository.GetFieldOccupationsAsync` resolveert die zelf met
`Planner.Shared.PlannerShared.VindVeldNummer` — dezelfde matching die de SQL Server-tier gebruikt,
sinds §38 letterlijk dezelfde implementatie. Een rij die niet resolveert (veldnummer blijft `0`)
valt stil weg, exact het SQL Server-origineel se `WHERE v.VeldNummer IS NOT NULL`-filter. De
SQL-Server-dedup (per veldnummer+aanvangstijd+wedstrijd de eerste rij, gesorteerd op bron) kan hier
pas ná de C#-resolutie plaatsvinden — vóór resolutie is het veldnummer voor Competitie-rijen nog
leeg, dus zou de dedup-sleutel voor die rijen altijd `NULL` zijn.

**Bijvangst: een duplicaat opgeruimd, niet ernaast gelegd.** Deze tier had al een eigen
`internal sealed record Speeltijd` in `PlannerSettingsRepository.cs`, met exact dezelfde vier velden
als de nu gedeelde `Planner.Shared.Speeltijd` (§38). Twee modellen met identieke vorm is precies de
duplicatie die de architectuurregels willen voorkomen — verwijderd, de twee aanroepers
(`AutoPlanService`, `PlannerSettingsRepository` zelf) gebruiken nu de gedeelde klasse.

**Empirisch geverifieerd**, inclusief het scenario dat er het meest toe doet: een gesynchroniseerde
wedstrijd met een veldstring die tegen geen enkel veld matcht valt stil uit de bezetting — geen
crash, geen "veld 0", gewoon afwezig, zoals het origineel. Zes permanente integratietests dekken
periode-aware beschikbaarheid, de drie bezettingsbronnen (Competitie/Planner/Training) samen,
exacte wedstrijdcode-uitsluiting, en de teamregel-aggregaties (buffers, voorkeursveld, meerdere
teams in één query). Onderscheidend vermogen apart bevestigd: met het resolutiefilter tijdelijk
gesloopt (`.Where(w => w.VeldNummer != 0)` → altijd waar) wordt precies de test rood die dat
scenario dekt; hersteld weer groen.

## 40. Drie planner-endpoints echt gewireerd: schema-inhaalslag plus de laatste `PlannerMatchRepository`-methoden (#888)

**Sluit §39's eigen "op ... na"-lijst.** `FindMatchAsync`, `FindMatchByCodeAsync`,
`SavePlannedMatchAsync` en `SaveHerplanVerzoekAsync` zijn nu vertaald — genoeg om
`BevestigWedstrijd`, `ZoekWedstrijd` en `HerplanBevestig` van een 501-stub naar een echte
implementatie te zetten. Van de zes resterende stubs (§37) blijven er nu nog drie over:
`CheckAvailability`/`DoordeweeksBeschikbaar`/`HerplanCheck` (wachten op `AvailabilityService`/
`RescheduleService`, niet op ontbrekende repositories — die bestaan sinds §39) en `AutoPlan`/
`AutoPlanToepassen` (wachten op `AutoPlanService`/`PlannerHtmlGenerator`, niet op de
FieldScheduler-engine zelf — die verhuisde al in §38).

**Een schemagat dat migratie 009 al had aangekondigd, nu gedicht.** `planner.geplandewedstrijden`
miste vier kolommen t.o.v. de SQL Server-tier: `wedstrijdduurminuten`, `aangevraagddoor`,
`opmerking`, `mta_inserted` — 009's eigen doc-comment noemde dit al expliciet als bewust
uitgesteld ("toevoegen zodra die functionaliteit daadwerkelijk vertaald wordt"). Erbij: de
UNIQUE-slotconstraint (`clubcode, datum, aanvangstijd, veldnummer, velddeelgebruik`) en de FK naar
`velden` — beide een echte dataintegriteitsgarantie, niet alleen SQL Server-parochialisme. Twee
nieuwe tabellen: `public.zonsondergang` en `planner.herplanverzoeken`, beide tot nu toe een
gemotiveerde uitzondering in de tabeldekking-guard (`scripts/ci/check-postgres-table-coverage.sh`).
Die uitzondering is voor `herplanverzoeken` nu vervallen; `zonsondergang` blijft erin staan tot
`PopulateSunset` ook vertaald is — de tabel bestaat, maar niets vult hem nog.

**Postgres-valkuil: `ADD CONSTRAINT` kent geen `IF NOT EXISTS`.** Kolommen en indexen ondersteunen
dat wel (`ADD COLUMN IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`), een FK-constraint niet — een
kale `ALTER TABLE ... ADD CONSTRAINT` faalde bij de tweede toepassing (die CI van elk
migratiebestand eist) op `42710: constraint already exists`. Opgelost met een
`DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = '...') THEN ... END IF; END $$;`
-guard. Empirisch bevestigd: de volledige migratieset tegen een verse container, daarna migratie
011 alleen nog een keer — exit 0, idempotent.

**`FindMatchAsync` doet bewust GEEN veldresolutie — in tegenstelling tot §39.** Het antwoord van dit
endpoint toont de rúwe Sportlink-veldstring (`m.veld`) als `VeldNaam`, precies zoals het SQL
Server-origineel: dit pad is een zoekresultaat voor een beheerder, geen numeriek veldnummer voor de
FieldScheduler-engine. Een lezer die na §39 verwacht dat élke veldstring-uitlezing via
`PlannerShared.VindVeldNummer` moet — dat hoeft hier niet, en had hier ook geen zin gehad: er is
geen kandidatenlijst om tegen te resolveren, alleen een tekst om te tonen.

**Bijvangst: een echte productiebug op de SQL Server-tier, gevonden tijdens het vertalen.**
`SaveHerplanVerzoekAsync` miste `ClubCode` volledig in de INSERT, terwijl
`[planner].[HerplanVerzoeken].[ClubCode]` `NOT NULL` is zonder `DEFAULT` — elke aanroep van
`HerplanBevestig` op de bestaande, live tier gooide een SQL-fout. Apart gefixt in dezelfde commit
(zie CHANGELOG "Fixed"); de Postgres-versie is vanaf het begin met `ClubCode` geschreven.

**Twee bestaande CI-guards bijgewerkt, niet omzeild.** `check-postgres-table-coverage.sh` verloor
zijn `herplanverzoeken`-uitzondering; `check-postgres-column-coverage.sh`'s
`KOLOM_UITZONDERINGEN`-lijst werd leeg (was vier regels voor exact de vier kolommen hierboven).
Beide opnieuw groen na de wijziging — geen enkele guard is uitgeschakeld om dit te laten slagen.

**Bijvangst 2: een CI-only regressie, niet in de applicatie zelf.** De eerste `gh pr checks`-run op
deze PR faalde deterministisch op "PostDeployment op verse Postgres-database" — drie tests in
`PlannerAvailabilityRepositoryIntegrationTests` (niet door deze PR aangeraakt) kregen een lege
collectie terug voor iets dat wél in `his.matches` stond. Reproductie lokaal (verse container +
exact dezelfde migratie-/seedstappen als de workflow) bevestigde: `planner.alle_wedstrijden_op_veld_ruw`
kiest de "primaire club" via `CROSS JOIN LATERAL ... WHERE syncenabled = true ORDER BY clubcode
LIMIT 1` — identiek aan het SQL Server-origineel (`Database/planner/Views/AlleWedstrijdenOpVeld.sql`),
correct zolang er precies één `syncenabled`-club is (§"Deployment-model" in CLAUDE.md). De
CI-workflow zette echter een tweede, synthetische club (`CIPRIMARY`, alleen bedoeld als
kopieerbron voor #862's speeltijden-seed) óók op `syncenabled = true`, zonder accommodatie —
sorteert vóór elke `testclub-*`, dus werd DIE rij de "gekozen" primaire club, en filterde elke
wedstrijd van élke andere club op een lege accommodatie. Gefixt in `.github/workflows/build.yml`
(`syncenabled = false` voor `CIPRIMARY` — de kopieerquery in 006 filtert daar toch niet op), niet
in de view: de view se aanname is een bewuste, correcte architectuurkeuze voor productie, alleen de
testomgeving overtrad hem per ongeluk.

**Empirisch geverifieerd.** Zeven permanente integratietests tegen een echte Postgres-container:
teamresolutie via alias (niet de canonieke naam zelf), een onbekend team levert `null` op (geen
LIKE-terugval), een ontbrekende speeltijd gooit dezelfde foutmelding als het origineel,
`FindMatchByCodeAsync` vindt een wedstrijd ook met status 'Afgelast' (bewust géén statusfilter,
in tegenstelling tot `FindMatchAsync`), en beide schrijfmethoden slaan alle kolommen correct op.
Onderscheidend vermogen bevestigd op de belangrijkste regressie: met `ClubCode` tijdelijk uit de
`SaveHerplanVerzoekAsync`-INSERT gehaald faalt precies díe test — met een NOT NULL-violatie, niet
met een stille lege waarde.

## 41. Beschikbaarheid, herplannen en zonsondergang: van elf endpoints zijn er negen vertaald (#888)

**Wat erbij kwam.** `AvailabilityService` (CheckAvailability + DoordeweeksBeschikbaar),
`RescheduleService` (HerplanCheck), `PostgresSunsetCalculator`, plus de laatste ontbrekende
repositorystukjes: `PlannerSettingsRepository.GetSpeeltijdAsync`/`GetSunsetAsync`/
`PopulateSunsetTableAsync` en `PlannerMatchRepository.GetTeamMatchesOnDateAsync`. Met
`PopulateSunset` erbij gewireerd blijven alleen `AutoPlan` en `AutoPlanToepassen` als 501-stub over
— die wachten op een `AutoPlanService`/`PlannerHtmlGenerator`-poort (576 regels HTML-generator),
niet meer op de planningsmotor: die woont sinds §38 in `Planner.Shared` en wordt hier voor het eerst
op deze tier daadwerkelijk aangeroepen (`TryExactTime`, `FindAllSlots`, `CanFitMatch`,
`ToSlotToewijzing`, `AddWeekdayWarning`) — het bewijs dat die verhuizing zijn doel haalde.

**De ene bewuste beperking: geen real-time Sportlink-API-pad.** Het SQL Server-origineel haalt de
bezetting op via `SportlinkApiClient.GetFieldOccupationsWithApiAsync` — een live API-aanroep met een
DB-terugval zodra `useRealtimeApi = "0"`. De Postgres-tier gebruikt hier uitsluitend die DB-terugval
(`PlannerAvailabilityRepository.GetFieldOccupationsAsync`, §39). Dat is géén nieuw of afwijkend
gedrag: het is exact het bestaande, ondersteunde configuratiepad. De live-integratie zelf is een
aparte eenheid werk — HTTP-client, `EgressGuard`-gate (de harde regel uit #857), parsing van de
`/programma`-respons en een fixture-server om tegen te testen zoals `SportlinkFixtureServer` (#867)
dat voor de sync doet. Expliciet benoemd in de klasse-doc-comments van beide services, zodat een
lezer dit niet als een vergeten detail leest.

**`SunsetCalculator` is bewust gedupliceerd, niet verhuisd.** Zelfde afweging als
`PostgresLeeftijdNormalisatie` (§16): de NOAA-berekening zelf is puur, maar hij leest de
clubcoördinaten uit een tier-eigen statische instellingencache (`PostgresAppSettings` hier,
`SystemUtilities.AppSettings` daar). Die twee caches samenvoegen tot één gedeelde plek is een
grotere refactor dan deze slice rechtvaardigt — de duplicatie is 90 regels pure wiskunde zonder
tier-afhankelijkheid, en staat hier gedocumenteerd in plaats van stilzwijgend te ontstaan.
`PostgresAppSettings` kreeg er wel twee kolommen bij (`accommodatielatitude`/`-longitude`, al
aanwezig sinds migratie 003) — precies volgens de uitbreidingsfilosofie in zijn eigen doc-comment.

**`FindLatestFitPerField` is óók gedupliceerd** (30 regels, private helper van `RescheduleService`):
een lokaal detail van één use-case, geen gedeelde-locatie-eis. De scheduling-engine waarop hij leunt
(`PlannerShared.CanFitMatch`) is wél gedeeld — dat is de grens.

**Empirisch geverifieerd.** Zeven permanente integratietests tegen een echte Postgres-container:
slot op voorkeurstijd, team-conflictdetectie, onbekende leeftijdscategorie, vensterberekening zonder
voorkeurstijd, zonsondergang-roundtrip (berekenen → opslaan → uitlezen), herplanalternatieven
exclusief de eigen wedstrijd, en een onbekende wedstrijdcode. Onderscheidend vermogen bevestigd: met
de team-conflictcontrole tijdelijk uitgeschakeld wordt precies die ene test rood, de andere 48
blijven groen.

**Twee opstellingsvalkuilen die deze tests blootlegden** (en die elke volgende testklasse hier zal
tegenkomen): (1) `TeamSchrijfwijzenAsync` resolveert via `public.teams`, niet `his.teams` — zonder
een canonieke teamrij vindt de conflictcontrole stilzwijgend niets; (2) de plannerview
(`planner.alle_wedstrijden_op_veld_ruw`) moet expliciet aangemaakt worden in de testopstelling, hij
komt niet uit een migratie.

## 42. AutoPlan: de laatste twee planner-endpoints, en twee verhuizingen in plaats van 676 regels duplicatie (#888)

**Alle elf planner-endpoints zijn nu vertaald.** `AutoPlan` en `AutoPlanToepassen` waren de laatste
501-stubs van de plannerlaag. Nieuw op deze tier: `AutoPlanService.AutoPlanAsync`/
`AutoPlanToepassenAsync`, de AutoPlan-wire-DTO's, en drie repository-methoden
(`GetVoorkeurTijdenLookupAsync`, `GetAllstarsVeldenAsync`, `UpdateAllstarsMatchAsync`).

**Twee verhuizingen naar `Planner.Shared` in plaats van duplicatie — 676 regels.**

| Verhuisd | Regels | Waarom niet dupliceren |
|---|---|---|
| `PlannerHtmlGenerator` (+ `OptimalisatieSuggestie`) | 576 | Enige tier-koppeling waren vier instellingen uit een statische cache. Die gaan nu als `HtmlInstellingen`-record naar binnen; de rest is pure stringopbouw. |
| `AutoPlanRegels` (`PlanDoel`, `BepaalPlanDoel`, sorteersleutels, `BepaalVoorkeurStatus`) | ~100 | Rekenlogica over primitieven en gedeelde domeinmodellen. Geen SQL, geen cache, geen tier-type. |

Dat is dezelfde afweging als §38 (`FieldScheduler`) en #889 (`TeamNaamNormalisatie`), en het
tegenovergestelde van `PostgresSunsetCalculator`/`PostgresLeeftijdNormalisatie` (§41/§16) — daar
woog 90 regels pure wiskunde niet op tegen een refactor van twee instellingencaches. **De grens die
uit deze reeks volgt:** koppeling aan een tier-eigen cache is op zichzelf geen reden tot
duplicatie zodra het om honderden regels gaat; injecteer de paar waarden en deel de rest.

**`BepaalPlanDoel` neemt primitieven, geen wedstrijdmodel.** De twee tiers hebben elk hun eigen
`WedstrijdRaw` (een class met setters op de SQL Server-tier, een positional record hier). Die vorm
delen zou een DTO-verhuizing afdwingen die niets oplost; twee strings binnengeven koppelt niets.

**Nul call-site-wijzigingen op de SQL Server-tier.** `AutoPlanService` houdt dunne, gelijknamige
delegaties (zelfde patroon als `NormaliseerVeld` bij #819), dus `AutoPlanHelperTests` en alle
aanroepers bleven ongewijzigd. Geverifieerd: 429 geslaagd / 5 geskipt, exact de baseline.

**Een porteervalkuil die een subagent-analyse ving vóór hij een bug werd.** Het origineel bouwt
`alleWedstrijden.ToDictionary(w => w, …)` — veilig daar, want `WedstrijdRaw` is een class
(referentie-gelijkheid). Hier is het een `record` met waarde-gelijkheid; twee inhoudelijk
identieke rijen zouden dezelfde sleutel zijn en een `ArgumentException` geven. Deze tier sorteert
daarom een lijst van paren. *Eerlijk over de reikwijdte:* die botsing is vandaag niet bereikbaar
(`wedstrijdcode` zit in de SELECT en heeft een unieke business key), dus dit is een defensieve
keuze, geen bugfix — maar wel één die niet afhangt van een invariant drie lagen verderop.

**Bijvangst: een echte beperking in de gedeelde planningsmotor (issue #939).** `FieldScheduler`
gebruikt de teamnaam alleen voor buffers en stempelt er het slot mee; hij houdt niet bij dat een
team al elders speelt. Twee wedstrijden van hetzelfde team kunnen daardoor op dezelfde tijd op twee
velden landen. Dat geldt voor **beide** tiers — de motor is sinds §38 gedeeld — dus het is bestaand
gedrag dat bij het porten zichtbaar werd, geen porteerfout. Vastgelegd in een test die bewust faalt
zodra het gedrag verandert, en apart gemeld in plaats van stilzwijgend meegefixt in een port-PR.

**Nog open op deze tier** (de enige resterende 501): `AdminTeambegeleidingDoorsturen`. Die hangt op
de uitgaande e-mailverzendlaag — `GraphServiceClient`/`EmailGraphService` zijn hier nergens
geregistreerd (`FunctionApp.Postgres/Program.cs` heeft bewust nog geen DI-registraties) en
`OntvangerParser` is niet vertaald. Net als het real-time Sportlink-API-pad (§41) is dat een
uitgaande integratie met een `EgressGuard`-gate (#857), en dus een eigen eenheid werk.

## 43. Het laatste 501-endpoint: uitgaande e-mail op de Postgres-tier (#888)

**`AdminTeambegeleidingDoorsturen` is vertaald — de Postgres-tier heeft nul 501-stubs.** Daarmee is
elk endpoint van deze tier een echte implementatie.

**Wat erbij kwam:** `FunctionApp.Postgres/Email/EmailGraphService.cs` (interface + Graph-implementatie),
de Graph-DI-registratie in `Program.cs`, en de handler zelf. `Microsoft.Graph` 6.5.0 is als
package toegevoegd — dezelfde versie die de SQL Server-tier al gebruikt, dus geen nieuw
afhankelijkheidsrisico.

**Bewust een smaller contract dan het origineel — bijgewerkt in §52.** `IEmailGraphService` had hier
oorspronkelijk één methode (`StuurTeamContactDoorAsync`) van de zes op de SQL Server-tier; de andere
vijf (`GetUnreadEmailsAsync`, `SetCategoriesAsync`, `EnsureMasterCategoryAsync`, `MarkAsReadAsync`,
`SendReplyAsync`) hoorden bij de inkomende e-mailverwerkingspijplijn, die toen op deze tier niet
bestond. Meeporten zou destijds onverifieerbare dode code hebben opgeleverd — dezelfde afweging als
§41/§42 bij ongebruikte repositorymethoden. Sinds #972 (§52) is die pijplijn (`EmailProcessorFunction`)
wél vertaald en heeft dit contract volledige pariteit met het SQL Server-origineel.

**`EgressGuard` is de enige poort, ook hier.** `IEmailGraphService` wordt alleen geregistreerd als
de Graph-secrets geconfigureerd zijn **én** `EgressGuard.ExternalIntegrationsAllowed()` true is
(#857). Niet geregistreerd → het endpoint geeft een eerlijke 503. Geen tweede, impliciete
is-dit-geconfigureerd-check in de handler; dat is precies wat #857 heeft opgeruimd.

**Twee pure utilities verhuisd naar `Planner.Shared` in plaats van gekopieerd:**

| Verhuisd | Regels | Waarom |
|---|---|---|
| `EmailSanitizer` | 144 | Veiligheidsrelevant. De doc-comment van `EmailGraphService` schreef al voor dat beide richtingen "één implementatie en één testsuite" delen — twee kopieën van sanitisatielogica die uit elkaar lopen is een beveiligingsbug in wording. |
| `OntvangerParser` | 80 | Puur, geen enkele afhankelijkheid. |

Beide zijn daarbij `public` geworden (aparte assembly). De bestaande testsuites in
`FunctionApp.Tests` bleven staan en ongewijzigd draaien — 429 geslaagd / 5 geskipt, exact de
baseline.

> **Naamgeving.** `Planner.Shared` is inmiddels de facto de tier-agnostische gedeelde laag, niet
> alleen planner-code — build.yml beschrijft hem ook zo, en hij bevat al `TeamNaamNormalisatie`,
> `LeeftijdNormalisatie` en `PlannerHtmlGenerator`. Met een e-mailsanitizer erbij wringt de naam.
> Een hernoeming is bewust **niet** in deze PR gedaan: dat raakt tien projectverwijzingen, de
> `.slnf`, de CI-workflow en elke `using` — een eigen wijziging, geen bijvangst van een endpointport.

**Bijvangst: een stille sorteerfout in het al vertaalde `GetBegeleiders` (#887).** Die query
gebruikte nog `LIKE '%Trainer%'` — hoofdletterongevoelig op SQL Server via de
`Latin1_General_CI_AS`-collatie, maar **niet** op Postgres. De teamrol komt uit een handmatig
aangeleverde CSV, dus een rol in kleine letters komt voor; die viel stilzwijgend in de ELSE-tak,
waardoor niet de trainer maar een willekeurige andere begeleider bovenaan kwam — en dus de vraag
van een ouder kreeg. Nu `ILIKE`, met een test die faalt zodra de vergelijking weer gevoelig wordt.
Zelfde klasse fout als #820; het patroon "LIKE op door mensen ingevoerde tekst" verdient op deze
tier standaard argwaan.

## 44. De plannerview bestond op een verse installatie helemaal niet (#861)

De losstaande bevinding uit §21 is nagemeten en klopte — met een grotere impact dan daar
ingeschat, want §41 en §42 hebben sindsdien de rest van de planner aangesloten.

**De meting.** Verse container, alle migraties toegepast via `Database.Postgres.Cli`, daarna:

```
SELECT to_regclass('planner.alle_wedstrijden_op_veld_ruw')  →  NULL
```

De view werd door **niets** aangemaakt behalve de testsuites zelf, die hem in hun eigen
opstelling neerzetten (§40 documenteert dat expliciet als valkuil). Precies daardoor bleef het
onzichtbaar: elke testrun was groen, en toch was er geen enkel pad waarlangs een echte installatie
aan die view kwam. Vijf endpoints leunen erop — `veldbezetting`, `check-availability`,
`doordeweeks-beschikbaar`, `herplan-check` en `auto-plan` — en die zouden op een nieuwe installatie
alle vijf `42P01: relation does not exist` hebben gegeven.

**Waarom geen migratie.** Ook nagemeten, niet aangenomen:

```
CREATE OR REPLACE VIEW planner.test_dep AS SELECT 1 FROM his.matches LIMIT 1;
→ ERROR: relation "his.matches" does not exist
```

`his.matches`/`his.teams` worden niet door een migratie aangemaakt maar dynamisch door
`PostgresMergeOrchestrator` bij de eerste sync (#818). Een migratie die de view aanmaakt zou dus op
een verse database hard falen — dezelfde #856-klasse beperking als bij de demodata.

**Waarom in de sync en niet in de reader.** §21 stelde voor om `CreateView` uit te voeren vanuit
`PostgresPlannerAvailabilityReader.GetFieldOccupationsAsync`, vlak vóór de `SELECT`. Dat was toen
een redelijke gok — er was één consument. Nu zijn het er vijf, verdeeld over drie klassen, en dan
valt die optie af:

| | Reader | Sync (gekozen) |
|---|---|---|
| Aantal plekken | 3 klassen, elk met eigen `CREATE OR REPLACE` vóór elke `SELECT` | één |
| Nieuwe consument | moet eraan denken; vergeet hij het, dan is de bug terug | krijgt het gratis |
| Kosten | een DDL-statement per leesverzoek | eens per sync |
| Volgordegarantie | geen — de reader weet niet of `his.*` al bestaat | direct ná `EnsureHisTableAsync`, dus gegarandeerd |

De aanroep staat daarom in `PostgresSyncPipeline.RunSyncAsync`, direct na de drie
`EnsureHisTableAsync`/`MergeStgToHisAsync`-paren en vóór `CanonicaliseerBestEffortAsync`. Dat is
het vroegste punt waarop de onderliggende tabellen gegarandeerd bestaan.

**Bewust niet best-effort.** `CanonicaliseerBestEffortAsync` ernaast staat wél in een `try/catch`,
en het verschil is principieel: een mislukte canonicalisatie laat de al geslaagde ETL intact, maar
een ontbrekende view maakt de halve plannerlaag stuk. Die stil doorlaten zou de sync groen laten
melden terwijl de applicatie erna kapot is — precies het faalpatroon uit
[[feedback_db_migrate_meldt_success_bij_fouten]].

**Eén bron van waarheid.** De DDL komt uit `PostgresPlannerViewGenerator.CreateView`, niet uit een
tweede kopie in een SQL-bestand. `VeldResolutieDriftTests` bewaakt die generator al; een
migratie-kopie ernaast zou stilzwijgend uit de pas kunnen lopen — en juist de veldresolutie in die
view is de risicovolste vertaalstap van de hele tier (§38).

**De test die dit vasthoudt.** `RunSyncAsync_MaaktDePlannerviewAan` **dropt de view eerst
expliciet** en asserteert dát hij weg is, vóórdat de sync draait. Zonder die stap zou de test ook
slagen op een view die een andere testklasse toevallig had achtergelaten — dan bewijst hij niets
over de pipeline. Negatieve controle uitgevoerd: met de aanroep uitgecommentarieerd faalt exact
deze ene test (`Expected ... to be True ... but found False`) en blijven de twee andere tests in
dezelfde klasse groen.

## 45. Een gedeeld defect gevonden door naar de tiers náást elkaar te kijken (#945)

Het vertaalwerk van deze epic levert af en toe iets op wat geen van beide tiers los zou hebben
opgeleverd: een fout die er altijd al zat, maar pas opvalt als je dezelfde code twee keer naast
elkaar leest.

`GetTeamMatchesOnDateAsync` gaf op **beide** tiers een lege `List<BestaandeWedstrijd>` terug zodra
de teamnaam niet naar een team in de canonieke lijst te herleiden was — bit voor bit hetzelfde als
"dit team heeft die dag geen wedstrijd". `AvailabilityService` las dat als "geen conflict" en
antwoordde `beschikbaar`, zonder fout, waarschuwing of logregel, terwijl het team op dat moment al
ingepland stond.

Het commentaar boven de SQL Server-methode benoemde het risico zelfs al — *"een gemiste vergelijking
hier zou stilzwijgend een dubbele boeking van hetzelfde team toelaten"* — maar dekte alleen de
vergelijking zelf, niet het geval waarin er niets te vergelijken viel.

**Waar de fix landt, en waarom dat de gedeelde laag is.** Het onderscheid "niet gecontroleerd" versus
"gecontroleerd, geen conflict" is een eigenschap van het *domein*, niet van een database. Het nieuwe
type `TeamWedstrijdenOpDatum` staat daarom in `Planner.Shared` naast `BestaandeWedstrijd`, precies
volgens de grens uit §42: pure logica zonder tierafhankelijkheid krijgt één plek. Twee kopieën zouden
hier bovendien uit de pas kunnen lopen op exact het punt waar de fout zat.

De repositories zelf blijven volledig gescheiden — die kennen `NpgsqlCommand` respectievelijk
`SqlCommand`. Ze geven alleen hetzelfde domeintype terug.

**Testdekking, en waar die ophoudt.** De Postgres-tier heeft een integratietest die het volledige
pad meet: een team dat níet in `public.teams` staat maar wél een wedstrijd heeft in `his.matches`,
met daarnaast de bestaande conflicttest als tegenhanger — samen scheiden ze "niet gecontroleerd" van
"gecontroleerd, geen conflict". Negatieve controle uitgevoerd: met de nieuwe tak uitgeschakeld faalt
exact die ene test.

Voor de SQL Server-tier bestaat zo'n harnas niet — `FunctionApp.Tests` heeft geen enkele test tegen
een levende SQL Server. Daar grendelt `Planner.Shared.Tests` alleen de semantiek van het type af.
Dat is eerlijk gezegd de zwakkere kant van deze fix, en het is het vermelden waard: de tier die het
langst in productie draait, is de tier met de minste geautomatiseerde dekking op dit pad.

**Wat bewust NIET in deze fix zit.** De canonieke lijst automatisch alsnog vullen vanuit een
leesverzoek. Dat maakt een `GET` schrijvend op een database met automatische pauzering en een
verbruiksbudget dat medio augustus 2026 al een keer uitgeput raakte — herstel hoort achter een
expliciete handeling (de sync, een timer, of een bewuste beheerhandeling), niet achter een
leesverzoek. Zie issue #946.

## 46. De laatste twee geblokkeerde zelftestpoorten: een herstelpad in plaats van een fixture (#931/#946)

Na §45 stonden er nog twee poorten op `blocked`: `api/beheer/teams` en `api/planner/team-schedule`.
Beide leunen op de canonieke teamlijst (`public.teams`/`public.teamaliassen`), en die is een
**afgeleide** tabel: de demoseed vult `his.teams`/`his.matches`, maar de afleiding gebeurt pas aan
het eind van een synchronisatie. De zelftest draait geen synchronisatie, dus de lijst bleef leeg en
gaven beide endpoints een correct maar onmeetbaar antwoord.

### Waarom niet de voor de hand liggende oplossingen

Drie richtingen zijn tegen elkaar afgewogen; de twee afgevallen opties zijn het vermelden waard,
want ze klinken allebei redelijk.

**`public.teams` in SQL seeden — afgewezen.** Dat zou `teamnaamgenormaliseerd` een tweede keer
berekenen buiten `Planner.Shared/TeamNaamNormalisatie.cs`. Precies de architectuurschending die
#766 al een keer heeft aangetoond: de sleutel is opgeslagen data, en een tweede berekening loopt
stilzwijgend uit de pas bij de eerstvolgende regelwijziging. Bovendien zou de poort daarna zijn
eigen seed meten in plaats van de applicatie.

**De canonicalisatie automatisch draaien vanuit de leesende endpoints — afgewezen.** Dit was de
aantrekkelijkste optie: één gereedheidspoort op de leesgrens, en alles heelt zichzelf. Maar het
maakt `GET`-paden schrijvend op een database met automatische pauzering en een verbruiksbudget dat
medio augustus 2026 al een keer uitgeput raakte, op een platform dat kan uitschalen naar meerdere
instanties. Herstel hoort achter een handeling van een mens.

**Gekozen: een expliciet herstelpad (#946).** `POST /api/beheer/teams/herstel` bouwt de canonieke
lijst opnieuw op, op beide tiers, met een knop op de pagina Teamaliassen. Poort G5b roept dat pad
aan vóórdat G6 meet. De lijst die G6 daarna telt is dus door **productiecode** opgebouwd, langs het
pad dat een beheerder ook zou nemen — geen fixture die levert wat productie zou moeten leveren.

Het endpoint heeft ook los van de zelftest bestaansrecht: de democlub werd tot nu toe alleen als
bijproduct van de synchronisatie van de primaire club gecanonicaliseerd, en er was geen enkel
herstelpad na een normalisatiewijziging behalve een handmatig script.

### De negatieve controle vond een fout in de fix zelf

Dit is het deel dat het opschrijven waard is.

De eerste versie van het endpoint riep twee dingen aan: `RefreshAsync` én daarna expliciet
`MigreerSleuteldriftAsync`. De negatieve controle op G5b — de tweede aanroep uitschakelen en
verwachten dat `G5b.sleuteldrift.hersteld` rood wordt — leverde **groen** op.

Dat was geen tekortkoming van de poort maar een fout in de productiecode: `RefreshAsync` draait de
sleutelmigratie al als zijn eigen eerste stap. De tweede aanroep rapporteerde daardoor altijd `0/0`
en kostte een extra ronde over de database. Zonder die controle was dit als "werkend" opgeleverd,
met een teller die de beheerder structureel `0 herstelde teams` had getoond — óók wanneer er wél
iets hersteld was.

De fix: één aanroep, en `RefreshAsync` geeft de tellingen terug die het toch al berekende in plaats
van ze alleen te loggen. De losse, publieke `MigreerSleuteldriftAsync`-ingang die hier eerst voor
was toegevoegd, is weer verwijderd — op deze tier heeft die geen consument, en dat maakt hem dode
code. (Op de SQL Server-tier blijft hij, daar roept `TeamlijstGereedheid` hem aan vanuit het
e-mailpad.)

De juiste negatieve controle breekt de migratie **binnen** `RefreshAsync`. Uitkomst: twee poorten
rood — `G5b.sleuteldrift.hersteld` én `G6.api-beheer-teams` (27 in plaats van 28 teams, want het
team met de kapotte sleutel wordt gedeactiveerd). Precies de cascade die #766 beschrijft, nu
zichtbaar gemaakt door een test.

### De te zwakke assertie die hierbij aan het licht kwam

`api/planner/team-schedule` eiste alleen dat de zaterdaglijst niet leeg was. Die lijst wordt
opgebouwd van vandaag tot het seizoenseinde in een lus die de wedstrijden niet eens raakt — hij is
dus per definitie gevuld zodra het team herkend wordt. Een volledig kapotte aliaskoppeling zou een
volle agenda zonder één bezette dag opleveren, en dat leest als een rustig seizoen in plaats van
als een defect. De assertie eist nu minstens één zaterdag met status `bezet`.

De poort mocht niet voor het eerst écht draaien met de zwakste assertie die hij ooit had.

### Stand van de zelftest

`Test-PostgresTier.ps1 -Tier Postgres -Mode Verify`: **54 geslaagd, 0 gefaald, 0 geblokkeerd.**
Geen enkele API-poort staat nog geblokkeerd. Wat nog openstaat zijn G7/G8 (browsersweep en
schrijfpaden, uitgevoerd door de skill) en de tijdertaak `FetchAndStoreApiData`, die op #867 wacht.

## 47. Wat de browsersweep vond dat de API-poorten niet konden vinden (#949)

Na §46 stond de zelftest op 54 geslaagd, 0 gefaald, 0 geblokkeerd — alle API-poorten groen. De
browsersweep (G7), die tot dan toe nooit tegen deze tier was uitgevoerd, vond daarna alsnog een
kapotte pagina.

### De vondst

`/testdata/wedstrijden` rendert, geeft HTTP 200, en toont bovenaan `Teams ophalen mislukt: HTTP 404`
met daaronder `0 van 0 wedstrijden`. In het netwerktabblad twee 404's:
`beheer/testdata/teams` en `beheer/testdata/wedstrijden`. `AdminTestDataFunction` — zes routes — is
nooit vertaald.

**Drie controles keken hier alle drie langs:**

| Controle | Waarom het niet aansloeg |
|---|---|
| "geen 501-stubs meer" | Deze endpoints bestaan niet; ze geven 404, geen 501. Ze tellen dus in geen enkele stub-telling mee. |
| G6 (API-asserties) | Loopt een vaste lijst af; deze zes staan er niet in. |
| G7 (browsersweep) | De route stond geblokkeerd op een bug van de *andere* tier en werd nooit uitgevoerd. |

Dit is precies waar de browsersweep voor bedoeld is, en meteen ook het bewijs dat "nul 501-stubs"
geen bruikbare definitie van compleet is: het meet de stubs die iemand heeft achtergelaten, niet de
endpoints die niemand heeft aangemaakt.

### Waarom de vertaling niet mechanisch is

De bedrijfssleutel verschilt structureel tussen de tiers:

| | Vorm |
|---|---|
| SQL Server (`Database/his/Tables/Matches.sql:3`) | `[bk_matches] NVARCHAR(100) NOT NULL` — gewone, beschrijfbare kolom |
| Postgres (`PostgresSchemaGenerator.cs:79`) | `GENERATED ALWAYS AS (...) STORED`, afgeleid van de numerieke wedstrijdcode |

De testdatapagina genereert zelf een sleutel in de vorm `ALLSTARS-<guid>`
(`BlazorAdmin/Pages/TestData/Wedstrijden.razor:345`). Die is op deze tier niet schrijfbaar én kan
nooit de vorm aannemen die de gegenereerde kolom oplevert.

De uitweg vraagt een keuze over gegevens die op de bestaande tier al in productie staan — de
sleutelvorm van bestaande demorijen. Dat is een bewuste beslissing, geen bijvangst van een
vertaalslag, en staat daarom als open punt op #949 in plaats van hier doorgevoerd te zijn.

### Drie verwachtingen die niet klopten

Dezelfde sweep legde drie foute verwachtingen bloot, die wél zonder verdere keuze te corrigeren
waren:

1. **`/dagplanning` en `/teambegeleiding` stonden geblokkeerd op #856** — een bug van de bestaande
   tier, waar het deployscript de bronnentabellen niet aanmaakt. Op deze tier maakt de zelftest die
   tabellen zelf aan vóór de seed; G4 telt 28 teams en 224 wedstrijden, en de sweep zag beide
   pagina's gewoon gevuld. Een blokkade die alleen elders klopt, laat hier bestaande dekking
   wegvallen.
2. **`/teambegeleiding` eiste "minstens één adres op het gereserveerde testdomein"** — onhaalbaar:
   het endpoint geeft **bewust nooit** e-mailadressen terug. Dat is een AVG-ontwerpkeuze. De
   assertie meet nu wat de pagina wél bewijst: dat de teamkeuzelijst alle 28 demoteams bevat, en
   dus dat de canonieke teamlijst end-to-end werkt.
3. **`/dagplanning` eiste een veldnaam uit de demovelden** — de demoseed vult bewust geen veld in.
   Hetzelfde verwachtingenbestand documenteerde die nuance elders al wél, bij `veldbezetting`.

Een verwachting die per definitie niet kan slagen is even schadelijk als een ontbrekende: hij maakt
de poort onbruikbaar in plaats van bewakend.

### Wat de sweep verder bevestigde

Twaalf van de dertien routes renderen correct tegen deze tier, zonder foutbanner en zonder
consolefouten. De negatieve controle (een niet-bestaande route) leverde een duidelijk afwijkende
niet-gevonden-pagina op, dus de methode meet werkelijk iets. De herstelknop uit §46 werkt
end-to-end door de GUI: `Teamlijst bijgewerkt: 28 team(s) actief (was 28), 28 goedgekeurde
schrijfwijze(n)`.

**Voetnoot over de opzet.** De browsersweep vereist dat de Blazor-GUI met de functiehost van *deze*
tier praat, en die verbinding loopt via een vastgelegde URL in een getrackt configuratiebestand. Er
is nog geen gedocumenteerde schakelaar die de ontwikkelomgeving op deze tier start; dat is nu
handmatig gedaan. Wie dat herhaalt: de functiehost moet `local.settings.json` hebben (niet alleen
omgevingsvariabelen), anders ontbreekt het CORS-blok en blokkeert de browser élke API-aanroep.

## 48. Testdata-endpoints op Postgres — Optie B gekozen (#952)

Vervolg op §47. De keuze die daar nog openstond ("de sleutelvorm van bestaande demorijen") is nu
gemaakt: **Optie B** — het endpoint vertaalt de sleutel, de Blazor-pagina blijft ongewijzigd.

### Waarom Optie B en niet Optie A

Optie A (de pagina genereert zelf een numerieke sleutel) is inhoudelijk aantrekkelijker — één
sleutelvorm, geen vertaling nodig — maar vraagt een wijziging aan `BlazorAdmin/Pages/TestData/
Wedstrijden.razor`, een bestand dat **beide** tiers delen. Zo'n wijziging raakt daarmee ook het
bestaande, live SQL Server-contract puur om de nieuwe tier te bedienen — exact de regressie die
§2 van dit document en de zelftest-skill (`Wat je nooit zelf aanpast: de bestaande, draaiende
tier`) uitsluiten. Optie B houdt de wijziging volledig binnen de nieuwe Postgres-boom.

### De vertaling

`FunctionApp.Postgres/Admin/AdminTestDataFunction.cs`'s `DeriveWedstrijdcode`:

- Is de aangeboden sleutel al numeriek (het geval ná een paginaherlaad — de pagina heeft dan de
  door de database afgeleide `bk_matches`-waarde teruggekregen), gebruik die rechtstreeks.
- Anders: een deterministische SHA-256-hash van de tekstsleutel, gemapt naar het bereik
  `900.000.000+`. **Bewust niet `string.GetHashCode()`** — die is sinds .NET Core per proces
  gerandomiseerd (beveiligingsmaatregel); dezelfde tekstsleutel zou na een herstart een andere
  wedstrijdcode opleveren, waardoor een upsert de bestaande rij niet meer terugvindt en een
  duplicaat aanmaakt in plaats van bij te werken.
- Bereik `900.000.000+` ligt ruim buiten zowel echte Sportlink-wedstrijdcodes (8 cijfers, zie
  `FunctionApp/CLAUDE.md`) als het gezaaide demobereik `9.000.001-9.000.224`
  (`scripts/migrations/003-seed-allstars-demo-matches-postgres.sql`).

De upsert gebruikt `ON CONFLICT (bk_matches) DO UPDATE` — hetzelfde patroon als
`PostgresUpsertGenerator.cs` al gebruikt voor de reguliere ETL-upserts tegen een gegenereerde
business-key-kolom.

### Wat dit niet oplost

De browsersweep uit §47 (G7/G8, schrijfpaden via de GUI) is met deze wijziging nog niet opnieuw
uitgevoerd — dat is de volgende stap vóór dit issue als volledig bewezen geldt.

## 49. Eenmalige productiecutover SQL Server → Supabase Postgres (#976)

Het moment dat §2 al aankondigde ("een switch voor het eerst fysiek mogelijk") is aangebroken: de
bestaande, live productie-installatie (niet een nieuwe fork) voert een eenmalige, expliciet
goedgekeurde cutover uit naar Supabase Postgres (EU-regio, DPA aanwezig). Aanleiding: het in §1
beschreven budgetuitputtings-faalmodel van Azure SQL's gratis serverless-tier.

**Dit is een uitzondering specifiek voor déze ene, reeds-lopende installatie** — de algemene regel
voor nieuwe forks/deployments (§2: één tier gekozen bij opzet, nooit runtime-switch, geen gedeelde
providerabstractie) blijft ongewijzigd. Zie de bevestiging op issue
[#814](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/814#issuecomment-5539376256).

### Wat NIET gemigreerd hoeft te worden

De ETL-pipeline (teams/programma/uitslagen/matchdetails → `his.*`) hoeft niet te worden overgezet:
`FunctionApp.Postgres/Sync/PostgresSyncPipeline.cs` roept `PostgresMergeOrchestrator` al dynamisch
aan bij elke sync-run (`RecreateStgTableAsync`/`EnsureHisTableAsync`, §14/#818) — exact zoals §2
voorschrijft. Na cutover kan de sync gewoon opnieuw starten vanaf de Sportlink API (desnoods met
`?reset=true&season=YYYY` per seizoen voor volledige historie).

### Wat wél gemigreerd moet worden

Puur lokaal ingevoerde configuratie en geleerde status, nergens anders vandaan te halen dan de
huidige SQL Server-productiedatabase: `AppSettings`, `AppSettingsAudit`, `Velden`,
`VeldBeschikbaarheid`, `VeldPeriode`, `VeldTraining`, `Speeltijden`, `TeamVoorkeurTijden`,
`TeamRegels`, `UitgeslotenEmailAdressen`, `EmailTemplateInstellingen`, `Season`, `TeamAliassen`,
`Teams` (canoniek, niet `his.teams`), `avg.Teambegeleiding`/`avg.ImportLog` (persoonsgegevens),
`planner.EmailVerwerking`/`ClassificatieCorrectie`/`GeplandeWedstrijden`/`HerplanVerzoeken`.

`MigrationTools/SqlServerToPostgresCopy` (nieuw, losstaand van beide tier-bomen — zie de
projectcommentaar in het .csproj voor waarom dit geen schending is van de "geen gedeelde
providerabstractie"-regel) is de generieke, kolomonafhankelijke rijkopieerder hiervoor: leest de
doelkolommen uit Postgres' eigen `information_schema`, scoped delete+copy op `ClubCode` (voorkomt
dat AllStars FC-democlubrijen worden geraakt), en verifieert na afloop de rijtelling bron-vs-doel.
`--dry-run` telt zonder te schrijven.

**Identity-kolommen worden nooit letterlijk gekopieerd — end-to-end lokaal getest en daardoor
ontdekt, niet vooraf bedacht.** Een eerste versie probeerde elke Postgres-`GENERATED ALWAYS AS
IDENTITY`-kolom (16 van de 19 doeltabellen hebben er een) met zijn originele SQL-Server-waarde te
vullen. Tegen een lokale Docker-opzet (SQL Server + Postgres, zie `docs/DEVELOPER-SETUP.md` §4)
met een democlub CLUB naast de al aanwezige AllStars-democlubseed botste dat meteen:
`veldbeschikbaarheid` begint in Postgres al bij `id=1` vanuit de AllStars-seed, en een
onafhankelijke club heeft in SQL Server zijn eigen IDENTITY-reeks die net zo goed bij 1 begint.
Surrogaatsleutels hebben geen betekenis buiten de database (in tegenstelling tot een natuurlijke
sleutel als `veldnummer`, dat AllStars bewust in een gereserveerd bereik 101+ houdt) — de tool laat
Postgres daarom altijd zelf een nieuwe waarde genereren, en vertaalt voor de drie
foreign-key-ketens tussen doeltabellen (`veldperiode.id -> veldbeschikbaarheid.periodeid`,
`teams.teamid -> teamaliassen.teamid`, `emailverwerking.id -> classificatiecorrectie.*verwerkingid`)
de oude naar de nieuwe waarde via een gedeelde `IdMapRegistry`. Volgorde in `Program.cs`s
tabellenlijst doet er daardoor toe: elke tabel met `IdentityMapKey` moet vóór zijn afhankelijke
tabel staan.

### Tier-switch-veiligheidsmechanisme

Zie de bijgewerkte §2: `DatabaseTierSwitchConfirmation` moet expliciet matchen met `DatabaseTier`
voordat de deploy-pipeline een tier-wijziging daadwerkelijk toepast.

### Cutover-runbook (nog niet uitgevoerd — vereist telkens expliciete eindbevestiging)

Rijtelling-/checksumvalidatie is in de kopieertool zelf ingebouwd. De resterende stappen, in
volgorde:

1. **Dry-run tegen productie** — telt rijen aan beide kanten, schrijft niets. Credentials lopen
   bewust NIET via een geautomatiseerde sessie: production SQL-wachtwoord en Supabase-wachtwoord
   zijn secrets, en de opdrachtgever voert dit zelf lokaal uit via
   `scripts/dev/Invoke-ProductionCutoverKopie.ps1` — vraagt de twee connectiestrings op via
   `Read-Host -AsSecureString` (niets op het scherm, niets in de PowerShell-commandogeschiedenis)
   in plaats van een `$env:X = "..."`-toewijzing die het wachtwoord permanent in die geschiedenis
   zou achterlaten.
2. ✅ **Lokale end-to-end-test met synthetische testdata** — uitgevoerd tegen een verse lokale
   Docker-opzet (SQL Server + Postgres, zelfde `docker-compose.yml` als elke ontwikkelaar
   gebruikt) met de al aanwezige placeholder-club `CLUB` naast AllStars FC. Bewees zowel het
   kopieerpad zelf als de ClubCode-scoping (AllStars-rijen ongemoeid) en legde de
   identity-kolom-botsing bloot die hierboven beschreven staat — dat was de eigenlijke waarde van
   deze stap, niet alleen "het draait zonder foutmelding".
3. **Echte kopie tegen productie** (zelfde tool zonder `--dry-run`) — pas na 1, en pas na
   expliciete eindbevestiging van de opdrachtgever.
4. **`POSTGRES_CONNECTION_STRING` als Azure Function App-instelling zetten** — dit is GEEN
   wijziging aan `deploy.yml` (dat bevat nooit connectiestrings): net als `SqlConnectionString`
   vandaag is dit een eenmalige `az functionapp config appsettings set`/Portal-actie rechtstreeks
   op de Function App, buiten de CI-pipeline om.
   **Sinds #1096, in deze volgorde — nooit omgekeerd:**
   1. Zorg dat een release met `FunctionApp.Postgres/prod-ca-2021.crt` (gedownload uit het
      Supabase-dashboard van déze deployment: Database → Settings → SSL Configuration — geen
      publieke, statische URL, per project verschillend) al live staat, zodat het certificaat
      op `/home/site/wwwroot/prod-ca-2021.crt` in het pakket zit.
   2. Pas dáárna de instelling uitbreiden met `?sslmode=verify-full&sslrootcert=/home/site/wwwroot/prod-ca-2021.crt`.
      Vóór stap 1 al `verify-full` zetten geeft een certificaatketen-fout (het #1095-incident,
      tweede keer).
   3. Verifiëren via `curl https://<function-app>.azurewebsites.net/api/health` —
      `tlsMode: "VerifyFull"` en `tlsWarning: null`.
5. **`DatabaseTier` én `DatabaseTierSwitchConfirmation`** in GitHub Settings → Actions → Variables
   allebei op `Postgres` zetten (zie het tier-switch-veiligheidsmechanisme hierboven) — in
   dezelfde actie, anders faalt de eerstvolgende deploy met exitcode 3.
6. **Deploy + volledige verificatielus uit CLAUDE.md**, inclusief de verplichte browser-
   rendercheck op de LIVE Admin GUI (§2a) — groene CI-jobs en HTTP 200 bewijzen niets over de GUI.
7. **Rollbackpad**: `DatabaseTier`/`DatabaseTierSwitchConfirmation` terug naar `SqlServer` +
   opnieuw deployen — de SQL Server-database zelf wordt door deze cutover niet aangeraakt of
   verwijderd, dus een terugval blijft mogelijk zolang die database blijft bestaan.

## 50. Certificaatvalidatie in `PostgresConnectionStringNormalizer` verplicht gemaakt (#1004)

**Kwetsbaarheid (HIGH/P1):** `PostgresConnectionStringNormalizer.Normalize` (Database.Postgres)
negeerde elke `sslmode`-optie uit de URI-query van een `postgres://`/`postgresql://`-connectiestring
en zette altijd `SslMode.Require`. Sinds Npgsql 8 valideert `Require` geen certificaatketen of
hostnaam meer (zie [release notes](https://www.npgsql.org/doc/release-notes/8.0.html)) — een
aanvaller die het netwerkpad of DNS naar de database kan beïnvloeden, kon zich zo als het
database-endpoint voordoen (MITM), ook als de connectiestring zelf expliciet
`?sslmode=verify-full&sslrootcert=...` opgaf. De keyword/value-vorm werd helemaal niet
gevalideerd.

**Fix:** `Normalize` parseert nu `sslmode` en `sslrootcert` uit de URI-query en vertaalt ze naar
`NpgsqlConnectionStringBuilder.SslMode`/`RootCertificate`. Daarna geldt voor **beide** vormen
(URI én keyword/value) hetzelfde beleid:

- **Host is een lokale-ontwikkelhost** (`localhost`, `127.0.0.1`, `::1` — exact de hosts uit
  `docker-compose.yml`'s `postgres`-service en `docs/DEVELOPER-SETUP.md` §7.2/de CI-job
  `fresh-db-postgres`): geen TLS-eis. De officiële `postgres:16`-image draait zonder TLS-configuratie;
  Npgsql's eigen default (`SslMode.Prefer`) valt terug op onversleuteld, precies zoals de
  gedocumenteerde lokale workflow vandaag al werkt — er is dus geen aparte env-var of opt-in nodig
  om lokaal te blijven werken.
- **Elke andere host** (per definitie productie/staging): norm is `SslMode.VerifyFull`. Een
  `RootCertificate` zonder `VerifyCA`/`VerifyFull` wordt geweigerd (Npgsql zou het anders
  stilzwijgend negeren, wat een beheerder ten onrechte kan doen geloven dat validatie actief is).
  **Gecorrigeerd in #1095 (zie hieronder):** een ontbrekende of zwakkere-maar-versleutelde modus
  (`Prefer` → opgewaardeerd naar `Require`; `Require`/`VerifyCA` blijven staan) gooit níet meer,
  maar levert een `TlsWarning` op die `/api/health` en het functielog tonen. Alleen expliciet
  onversleuteld (`Disable`/`Allow`) naar een niet-lokale host blijft een `InvalidOperationException`.
  **Let op — de oorspronkelijke aanname hier was onjuist:** Supabase gebruikt géén publiek vertrouwde
  CA voor de databaseverbinding; volgens de providerdocumentatie moet voor `verify-full` het eigen
  CA-certificaat (`prod-ca-2021.crt`) worden gedownload en via `sslrootcert` worden meegegeven.
  `VerifyFull` zonder dat certificaat faalt dus op de ketenvalidatie.

Onderscheid tussen lokaal en productie gebeurt dus op basis van de **daadwerkelijk benaderde host**,
niet op basis van welk proces de verbinding opent — bewust consistent met hoe `EgressGuard`
(§0/`FunctionApp.Postgres/Infrastructure/EgressGuard.cs`) lokaal van productie onderscheidt
(env-gebaseerd), maar toegepast op de vraag die hier telt: TLS-vertrouwen hoort af te hangen van
de server aan de andere kant van de verbinding. Dit geldt daardoor identiek voor
`PostgresDatabaseConfig` (Function App), `Database.Postgres.Cli` (migratiepad) én
`MigrationTools/SqlServerToPostgresCopy` (#976-cutoverkopie) — alle drie roepen dezelfde
`Normalize`-methode aan, er is geen aparte, zwakkere check ergens anders.

**Operationele consequentie (oorspronkelijke tekst, #1004 — inmiddels achterhaald, zie #1187:
het werd `verify-ca`, want `verify-full` is op het pooler-endpoint onhaalbaar):** de Azure Function
App-instelling `POSTGRES_CONNECTION_STRING` (zie stap 4 van het cutover-runbook hierboven) moest
`?sslmode=verify-full` bevatten; anders gooide `PostgresDatabaseConfig`'s statische initializer bij
de eerstvolgende cold start een `InvalidOperationException` (gevangen door `/api/health` als
`"unconfigured"` → HTTP 503, zie §10).

### #1095 — incident bij release v3.3.0.0 en de correctie

Precies dat gebeurde. De release v3.3.0.0 (PR #1094) was de eerste deploy met #1004 aan boord; de
productie-instelling had geen `?sslmode=` (de #976-cutover had de URI-vorm gezet zoals het
Supabase-dashboard die toont, en vóór #1004 werd daar stilzwijgend `Require` van gemaakt). De
operationele stap hierboven was niet uitgevoerd, er was geen pre-deploy-check die dat afving, en
omdat `PostgresDatabaseConfig.ConnectionString` een static initializer is, faalde daarna **elke**
databasetoegang in de hele app — planner, admin-endpoints, nachtelijke synchronisatie — niet alleen
`/api/health`. Alle deploy-jobs waren groen; de smoke test (`test`) was de enige die het zag.

Twee lessen:

1. **Fail-closed in een static initializer is een productie-breker, geen beveiligingswinst.** Een
   TLS-modus die zwakker is dan de norm maar wél versleutelt, is een configuratieschuld die
   zichtbaar moet zijn — niet een reden om de applicatie te laten uitvallen. De normalizer geeft
   daarom nu `NormalizeWithDiagnostics(raw)` → `(ConnectionString, EffectiveSslMode, TlsWarning)`.
   `Prefer` (Npgsql-default = niet opgegeven) wordt voor een niet-lokale host opgewaardeerd naar
   `Require` — de productiestand van vóór v3.3.0.0, nooit zwakker dan voorheen; `Require`/`VerifyCA`
   blijven staan; in alle drie gevallen met een `TlsWarning` zonder host of credentials.
   `/api/health` toont `tlsMode` + `tlsWarning`, `PostgresSystemUtilities.WaitForDatabaseAsync` logt
   de waarschuwing éénmalig per proces, `Database.Postgres.Cli` print hem naar stderr. Alleen
   expliciet `Disable`/`Allow` naar een niet-lokale host blijft geweigerd.
2. **De aanname over de CA was onjuist.** Supabase vereist voor `verify-full` het eigen
   CA-certificaat (`sslrootcert`). Alleen `?sslmode=verify-full` aan de instelling toevoegen had
   dus een tweede storing gegeven (certificaatketen-fout). De echte `verify-full`-uitrol — CA-cert
   meeleveren in het deploy-pakket, `sslrootcert` ernaar laten wijzen, en een pre-deploy-check die
   de effectieve TLS-modus toetst vóór de code live gaat — is een apart vervolgissue; tot die tijd
   is `tlsWarning` in `/api/health` het signaal dat die schuld nog openstaat.

**Tests:** `Database.Postgres.Tests/PostgresConnectionStringNormalizerTests.cs` — dekt beide vormen,
beide omgevingen, de contradictiecheck, en de bestaande parsingtests (percent-encoded loginvelden,
standaardpoort, lege pad → database `postgres`) blijven daarin behouden.

### #1096 — CA-certificaat gebundeld, smoke test bewaakt `tlsWarning` (bouwstenen klaar, cutover nog handmatig)

Vervolg op de twee lessen hierboven. Geen wijziging aan `Normalize`/`ApplyTlsPolicy` zelf — de
`sslmode`/`sslrootcert`-parsing en de fail-open-met-waarschuwing-policy uit #1095 ondersteunden een
CA-certificaat al. Wat ontbrak was het certificaat zelf en bewaking dat de norm ook echt gehaald
wordt:

1. **`FunctionApp.Postgres/FunctionApp.Postgres.csproj`** kopieert `prod-ca-2021.crt` naar de
   output- én publish-directory, conditioneel op `Exists(...)` — zolang het bestand ontbreekt is
   dit een no-op, geen build- of publish-fout. Het certificaat zelf staat inmiddels in de repo
   (gedownload uit het Supabase-dashboard van déze deployment: Database → Settings → SSL
   Configuration; subject/issuer "Supabase Root 2021 CA", geldig 2021-04-28 t/m 2031-04-26). Het is
   publiek (Supabase's eigen root-CA, gelijk voor het project van deze deployment) en hoort dus in
   git, niet in `.gitignore` — anders dan `local.settings.json`.
2. **De smoke test in `deploy.yml`** leest voortaan ook `tlsWarning` uit `/api/health` en meldt die
   als `::warning::`, exact hetzelfde patroon als `pendingMigrations`/`schemaWarning` (§55): nooit
   een deploy-blokkade, want de verbinding blijft functioneren (fail-open sinds #1095). Dit is de
   "pre-deploy-check op de effectieve TLS-modus" uit het vervolgissue — als CI-zichtbaarheid na de
   deploy, niet als harde gate, omdat de pipeline zelf `POSTGRES_CONNECTION_STRING` niet zet (§49
   stap 4) en dus vóór de deploy niets over de productie-instelling kan weten.

**Uitgevoerd bij release v3.4.1.0 — maar met `verify-ca`, niet `verify-full` (#1187).**
`POSTGRES_CONNECTION_STRING` is uitgebreid met
`?sslmode=verify-ca&sslrootcert=/home/site/wwwroot/prod-ca-2021.crt`.

> **Volg hier nooit het oorspronkelijke advies `verify-full` op.** Dat stond hier eerder en is voor
> deze deployment onhaalbaar. Gemeten tegen de productiedatabase, met Npgsql 10.0.3 via deze
> normalizer:
>
> | Modus | Uitkomst |
> |---|---|
> | `Require` (stand tot v3.4.1.0) | verbinding OK |
> | `verify-ca` + provider-CA | **verbinding OK** |
> | `verify-full` + provider-CA | **afgewezen** |
>
> Het pooler-endpoint levert een certificaat met alleen een CN (`*.pooler.…`) en **geen
> SubjectAltName**. .NET valideert hostnamen uitsluitend tegen de SAN — CN-matching is sinds
> RFC 6125 afgeschaft — dus `verify-full` faalt daar per definitie met
> `RemoteCertificateNameMismatch`. Omdat `PostgresDatabaseConfig.ConnectionString` een static
> initializer is, zou het opvolgen van dat advies de hele app platleggen: precies het
> #1095-incident opnieuw.

Wat `verify-ca` wél oplevert: de certificaatketen wordt gevalideerd, dus een aanvaller heeft een
certificaat nodig dat door de root-CA van de provider is ondertekend — een willekeurig
zelfondertekend certificaat volstaat niet meer. De hostnaam blijft ongevalideerd; dat is het
restrisico, en daarom blijft `tlsWarning` in `/api/health` staan. Dat is vanaf nu het juiste,
verwachte signaal en geen openstaande actie meer.

**Bewust niet meegewijzigd: het GitHub-secret `POSTGRES_CONNECTION_STRING`.** `db-migrate-postgres`
draait op een GitHub-runner, waar het pad `/home/site/wwwroot/...` niet bestaat; dezelfde waarde
daarheen kopiëren breekt de volgende deploy. Dat vraagt een eigen oplossing (het certificaat staat
ook in de checkout) en is losgekoppeld in #1187.

**Vervolg voor later:** het **directe** database-endpoint zou `verify-full` mogelijk wél halen,
maar dat raakt het verbindingslimiet-gedrag van de pooler en is dus een aparte afweging.

**Bewust nog niet gedaan:** het beleid weer aanscherpen (`Require` zonder `verify-full` opnieuw
weigeren). Dat is pas verantwoord zodra bovenstaande twee operationele stappen aantoonbaar zijn
uitgevoerd en herhaalbaar zijn vastgelegd — met een pre-deploy-check die dat afdwingt, niet met een
static initializer die de app platlegt (exact de fout uit #1004).

## 51. De lokale ontwikkelomgeving volgt de gedeployde tier (#1060)

Na de cutover van §49 draaide productie op Postgres, maar wees de dagelijkse ontwikkeltooling nog
onverkort naar de SQL Server-tier: `Start-Debug.ps1` startte `FunctionApp/` hard-gecodeerd,
`Test-App.ps1` las `SqlConnectionString` en sprak `sqlcmd`, en `docker compose up -d` startte alleen
de SQL Server-container — de Postgres-service zat achter een profile.

**Waarom dat meer is dan een ongemak.** §52 is er het bewijs van: `EmailProcessorFunction` ontbrak
volledig op de Postgres-tier, de mailbox werd sinds 2026-09-04 niet meer gepolld, en geen enkele
lokale verificatie sloeg aan — die mat een applicatie die niet gedeployd werd. Een verificatielus
die de verkeerde tier meet, is geen halve verificatie maar een misleidende.

### Wat er is omgedraaid

| Plek | Voor | Na |
|---|---|---|
| `docker compose up -d` | alleen `sqlserver` | alleen `postgres`; `sqlserver` achter `--profile sqlserver` |
| `Start-Debug.ps1` | `Set-Location FunctionApp` | `-Tier` (default `Postgres`), projectpad via `Get-DatabaseTierProject` |
| `Test-App.ps1` | `SqlConnectionString` + `sqlcmd` | `-Tier` (default `Postgres`); per tier een eigen verbindings- en schemacontrole |
| `postgres`-image | `postgres:16` | `postgres:17` — gelijk aan de gehoste hoofdversie, ook in de zelftest en de CI-job `fresh-db-postgres` |

De tier-naam wordt nergens opnieuw vertaald: beide scripts lezen `scripts/ci/database-tiers.json`
via `Get-DatabaseTierProject`, dezelfde tabel als `resolve-database-tier.sh` (#816/#865). De lokale
tier en de gedeployde tier blijven volledig losgekoppeld — `-Tier` raakt `DatabaseTier` niet aan.

### Wat de eerste echte uitvoering aan het licht bracht

Dit deel is de reden om het op te schrijven: geen van de onderstaande punten was vooraf bedacht.

**1. Een verse database levert een applicatie op waarin geen enkel beheerscherm werkt.** Na
`docker compose up -d` + alle migraties bevat de database precies één club: AllStars FC, met
`syncenabled = FALSE`. `PostgresAppSettings.LoadSettingsAsync` selecteert bewust alleen clubs met
`syncenabled = true` (een democlub mag nooit stilzwijgend de primaire club worden), dus de
instellingencache blijft leeg en `EasyAuthHelper.GetClubCodeFromRequest` gooit. Uitkomst: dertien
van de dertien `/api/beheer/*`-endpoints antwoorden 500.

Opgelost met `scripts/migrations/004-seed-lokale-placeholderclub-postgres.sql` — een club-neutrale
placeholder (`clubcode = 'CLUB'`, `syncenabled = TRUE`), bewust **buiten**
`Database.Postgres/migrations/`: alles in die map wordt op elke database toegepast, productie
inbegrepen.

**2. `/api/health` wist het al, maar niemand keek.** De endpoint gaf keurig
`status: degraded, settingsLoaded: false` (#859) — en `Start-Debug.ps1` meldde er "FunctionApp OK"
overheen, omdat het alleen op een HTTP-antwoord controleerde. Dat is dezelfde klasse fout als §2a
van CLAUDE.md beschrijft voor de live GUI: een 200 is geen bewijs. Het script leest die twee velden
nu, en wijst bij `degraded` rechtstreeks naar het seed-script hierboven.

**3. Tier-provenance als startvoorwaarde.** `/api/health` meldt zijn eigen tier uit de
assembly-metadata (#863). `Start-Debug.ps1` vergelijkt dat nu met `-Tier`: een achtergebleven
functiehost van de andere tier op poort 7094 zou anders als een geslaagde start doorgaan.

**4. De demoteams bleken langs een derde, ongedocumenteerde weg te lopen.** De migraties zaaien
voor AllStars alleen de AppSettings-rij, velden, veldbeschikbaarheid en speeltijden. De 28 teams en
224 wedstrijden staan in `scripts/migrations/003-seed-allstars-demo-matches-postgres.sql`, dat
`his.teams`/`his.matches` nodig heeft — tabellen die geen enkele migratie aanmaakt, omdat
`PostgresSchemaGenerator` ze dynamisch maakt bij de eerste sync. Lokaal draait die sync niet
(EgressGuard, en terecht), dus bleef de teamlijst leeg.

De zelftest loste dat voor zichzelf op met de his-DDL als letterlijke heredoc, overgenomen uit de
CI-job — twee met de hand bijgehouden kopieën van een schema dat de generator al produceert. Een
derde kopie in een ontwikkelscript zou bij de eerstvolgende kolomwijziging stilzwijgend uit de pas
gaan lopen. `Database.Postgres.Cli --ensure-his-tables` roept daarom
`PostgresMergeOrchestrator.EnsureHisTableAsync` aan over `KnownEntities.All` — dezelfde weg als de
ETL. `scripts/dev/Seed-AllStarsDemodata.ps1` ketent dat aan de seed en aan
`POST /api/beheer/teams/herstel`, want `public.teams` is een afgeleide tabel: zonder die laatste
stap blijft de GUI leeg terwijl `his.teams` vol staat.

Eén detail dat pas bij uitvoeren bleek: het herstel-endpoint valt zonder `X-Club-Code` terug op de
*primaire* club, en die heeft geen gesynchroniseerde teams — een 409 dus, een correct antwoord op
de verkeerde vraag. Het script stuurt de clubcode nu expliciet mee.

**5. De macOS-verificatie die tot nu toe openstond, is gedaan.** §4.3 van DEVELOPER-SETUP.md meldde
"macOS-uitvoeringsverificatie niet mogelijk gebleken (geen Apple Silicon-hardware)". Die staat er
niet meer: de volledige keten — container, migraties, seed, functiehost, `Test-App.ps1` — is
uitgevoerd op Apple Silicon tegen `postgres:17.11`, native, zonder Rosetta. Uitkomst: 36 geslaagde
controles, één openstaand punt (`GitHubPat`/`GitHubOwner` niet ingevuld), dat op beide tiers
bestaat en losstaat van deze wijziging.

### Wat bewust niet gebeurt

De SQL Server-tier wordt niet verwijderd of gedeprecieerd. Hij blijft `built: true` in
`database-tiers.json`, houdt zijn eigen compose-service, template en schemacontrole, en is het
rollbackpad van §49 stap 7. Alleen de standaardkeuze is verschoven naar de tier die daadwerkelijk
draait.
## 52. `EmailProcessorFunction` alsnog vertaald — de mailbox werd sinds §49 nooit gepolld (#972, hotfix)

**Het gat dat §49's "wat NIET gemigreerd hoeft te worden" niet zag aankomen.** Na de productiecutover
naar Postgres draaide de mailbox-getriggerde e-mailverwerking helemaal niet meer: §29/§43 hadden
`EmailProcessorFunction`/`EmailGraphService`'s vijf inkomende-mail-methoden bewust buiten scope
gehouden (geen directe SQL-toegang, dus buiten #889's scope-omschrijving) — maar zodra Postgres de
enige tier is die daadwerkelijk deployt, is "bewust nog niet vertaald" hetzelfde als "helemaal niet
actief". Geen classificatie, geen auto-reply, sinds 2026-09-04. Aanleiding: een hotfix rechtstreeks
naar `main` (goedgekeurd door de eigenaar, buiten de gewone `develop`-flow om), issue #972.

**Wat is toegevoegd:** `EmailProcessorFunction.cs` (de timer-triggered `[Function("ProcessIncomingEmails")]`
zelf, tweefasen-structuur licht/zwaar, idempotentie via `EmailIdempotentie`/`VerwerkingsBesluit`, de
uitsluitingslijst-TTL-cache), `EmailReplyPolicyService`, `EmailBatchFilterService`,
`EmailClassificationService`, `ReplyPolicy`, `EmailCategorieLabels`, `EmailBijlage`, en
`Monitoring/INoodmailThrottleStore`/`TableStorageNoodmailThrottleStore` (Azure Table Storage is
DB-tier-agnostisch, dus woordelijk over te nemen). `IEmailGraphService` groeide van het ene
smalle contract uit §43 naar volledige pariteit: de vijf destijds weggelaten methoden
(`GetUnreadEmailsAsync`, `SetCategoriesAsync`, `EnsureMasterCategoryAsync`, `MarkAsReadAsync`,
`SendReplyAsync`) zijn er nu bij.

**Structurele afwijking t.o.v. de SQL Server-tier: geen `IEmailPersistenceService`.** Die tier heeft
een interface-laag bovenop de e-mailtabel; hier roepen `EmailReplyPolicyService` en
`EmailProcessorFunction` de bestaande statische `SqlEmailPersistenceRepository`-methoden rechtstreeks
aan met de connectiestring — zelfde stijl als `BerichtPipeline` al gebruikte. Een nieuwe
interface/DI-abstractie alleen om de SQL Server-tier 1-op-1 te spiegelen is bewust niet toegevoegd
voor een hotfix.

**Drie al bestaande, gedocumenteerde afwijkingen op `BerichtPipeline`-niveau blijven ongewijzigd**
(opponent-lookup, `TeamContactOpvragen` se `coachGevonden`, KNVB-PDF-bijlage/"verzet zonder datum") —
dit issue port een aanroeper van die pijplijn, niet de pijplijn zelf. *(Bijgewerkt: opponent-lookup
is sinds #1139 vertaald — zie §58 — `TeamContactOpvragen`/`coachGevonden` sinds #1140 — zie §61 —
en de KNVB-PDF-bijlage/"verzet zonder datum"-flow sinds #1141 — zie §62. Alle drie zijn nu vertaald.)*

**Eén nieuw ontdekte, hier voor het eerst gedocumenteerde afwijking:**
- De teamleider-/teamcontact-vervolgnotificaties (#66/#168) gebruikten
  `AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync` (bestaande, geteste query tegen
  `avg.teambegeleiding`) in plaats van `PlannerDataAccess.GetTeamleiderContactAsync` (bestond hier
  niet) — dat levert alleen een e-mailadres, geen naam, dus de notificatietekst gebruikte een
  generieke aanhef. *(Bijgewerkt: sinds #1140 gebruiken beide notificaties
  `AllstarsTestDataRepository.GetTeamleiderContactAsync` — zie §59 — inclusief de naam van de
  begeleider in de aanhef.)*
- De onafhankelijke, ARM-gebaseerde database-uitvalmonitor (`DatabaseUitvalMonitorFunction`/
  `IDatabaseStatusReader`, #831 op de SQL Server-tier) is niet vertaald — die controleert
  specifiek Azure SQL-status, wat hier niet van toepassing is. De noodmail-throttle zelf
  (`INoodmailThrottleStore`) is wél vertaald, dus de e-mail-pipeline-afhankelijke noodmail werkt.

**`BerichtAiService.DetecteerCorrectieAsync` alsnog toegevoegd** (#323-functionaliteit) — deze
methode bestond nog niet op de Postgres-tier, terwijl `LearningMomentRepository` (het andere deel
van dezelfde correctie-leerlus) al wél werkte sinds een eerdere sessie zonder dat iets hem aanriep.
De klassekop van `BerichtAiService` beweerde tot deze fix nog dat de few-shot-`voorbeelden`-parameter
"op deze tier altijd null" zou zijn — achterhaald, hier gecorrigeerd.

**Testaanpak: integratietests in plaats van fakes voor de persistentielaag.** Zonder
`IEmailPersistenceService` is er geen interface om te faken voor `EmailReplyPolicyService`/
`BepaalVerwerkingIdAsync`/`RegistreerClassificatieFoutAsync`/`HandelBuitenScopeAsync` — die staan
daarom als `[PostgresFact]`-integratietests tegen een echte database
(`EmailProcessorFunctionIntegrationTests`, `EmailReplyPolicyServiceIntegrationTests`), zelfde patroon
als `PostgresEmailPersistenceIntegrationTests`. Pure logica (`ReplyPolicy`, `EmailBatchFilterService`,
`EmailClassificationService`, `UitsluitingslijstCache`, de noodmail-throttle-tests) blijft
fake-gebaseerd en woordelijk gelijk aan de SQL Server-tier se testsuite.

**Vereist handmatige verificatie na deploy:** `EMAIL_POLL_SCHEDULE` en `EmailProcessorEnabled` als
Function App-instelling op de productie-resource — die stonden er vóór de tier-cutover al voor de
SQL Server-tier, maar zijn niet geverifieerd voor deze deploy (agents mogen App Settings niet zelf
lezen/zetten, zie CLAUDE.md's kostenbeleid-sectie).

## 53. Een gewijzigd migratiebestand faalt nu in CI in plaats van pas in productie (#1062)

`MigrationRunner` legt per migratiebestand een SHA-256 vast en weigert een bestand dat al is
toegepast maar sindsdien gewijzigd. Die bewaking werkt — maar ze sloeg pas aan op het moment dat
iemand migraties uitvoerde, en niet in de CI.

**Waarom de CI dit per definitie niet kon zien.** `fresh-db-postgres` begint altijd op een lege
database. Daar bestaat geen eerdere checksum, dus een achteraf bewerkte migratie wordt gewoon
toegepast en de job wordt groen. De enige situatie die faalt — een database die de oude versie al
had — kwam in CI nergens voor. Bij #1058 leverde dat een groene PR op die elke bestaande database,
productie inbegrepen, blokkeerde op de eerstvolgende migratieronde.

**De guard.** Bij een pull request past de job eerst het migratiepad van de *basisbranch* toe, en
daarna dat van de PR. Wie een bestaand migratiebestand wijzigt, laat die tweede stap hard falen met
exact de checksumfout die anders pas in productie zou opduiken. Bij een push naar `main`/`develop`
is er geen basis om tegen af te zetten en slaat de stap over.

**Volgordeafhankelijkheid, expliciet.** Deze guard kon niet in dezelfde PR als de herstelcommit van
#1062 zitten: die zet 012 terug naar de oorspronkelijke inhoud en is daarmee zelf "een gewijzigd
migratiebestand" ten opzichte van de toenmalige `develop`. De guard zou zijn eigen reparatie hebben
tegengehouden. Herstel eerst, guard daarna — in die volgorde gemerged.

**Wat de guard niet afvangt.** Een migratie die op een lege database slaagt maar op een gevulde
database stukloopt (bijvoorbeeld een `NOT NULL`-kolom zonder default op een tabel met rijen). De
basisbranch-database bevat in CI alleen wat de migraties zelf aanmaken, geen productie-achtige
data. Dat blijft een apart risico.

## 54. Een vertaalfout die acht dagen stil bleef, met een foutmelding die de verkeerde kant op wees (#1077)

De derde vertaalomissie in deze epic, na §45 en §52. Deze is het opschrijven waard om twee redenen
die losstaan van de fout zelf: de foutmelding loog, en niets meldde de storing.

### De fout

`PostgresStagingRepository.MergeUitslagenAsync` noemde `@clubcode` in zijn INSERT maar bond die
parameter nooit. De meegegeven `clubCode` werd nergens gebruikt. De drie andere merges in hetzelfde
bestand binden hem wél, en het SQL Server-origineel ook — alleen het uitslagenpad is bij de
vertaling overgeslagen.

### Waarom de melding naar het schema wees en niet naar de code

```
Npgsql.PostgresException 42703: column "clubcode" does not exist
```

`stg.matches` heeft die kolom gewoon. Npgsql laat een placeholder waarvoor geen parameter bestaat
letterlijk in de SQL staan, en in PostgreSQL is `@` een **geldige prefix-operator** (absolute
waarde). De server leest `@clubcode` dus als "operator `@` toegepast op kolom `clubcode`" en
rapporteert die kolom als ontbrekend.

**Dit is een Postgres-specifieke valstrik zonder tegenhanger op SQL Server**, waar dezelfde fout een
ondubbelzinnige *"must declare the scalar variable"* oplevert. Elke `42703` op een kolom waarvan je
zeker weet dat hij bestaat, is daarom eerst een aanwijzing voor een ongebonden parameter — niet voor
schemadrift. In deze epic waren de eerdere `42703`-gevallen (§29, §32) juist wél schemadrift; die
gelijkenis maakte het zoeken langer dan nodig.

### Waarom hij alleen op twee van de drie weekoffsets sloeg

De INSERT draait uitsluitend wanneer de voorafgaande UPDATE nul rijen raakte. Voor de huidige week
staan de wedstrijden al in staging uit de programma-fetch, dus daar slaagt de UPDATE en wordt het
kapotte pad nooit bereikt. Voor de twee voorgaande weken valt hij door naar de INSERT. Vandaar
precies `weekOffset=-2` en `-1`, acht nachten achter elkaar identiek.

### Waarom de bestaande dekking het niet zag

`PostgresSyncFixtureIntegrationTests` draait het volledige synchronisatiepad, maar zijn fixture
levert uitslagen voor wedstrijden die de programma-fetch al had ingevoegd. De UPDATE raakt dan een
rij, de methode doet `continue`, en het INSERT-pad wordt nooit uitgevoerd. In productie is dat pad
juist de regel. Een test die het *pad* niet raakt, dekt de code niet af hoeveel regels hij ook
aanroept.

`UitslagenMergeIntegrationTests` dwingt dat pad nu af, met een tegenhanger op de UPDATE-tak zodat
"welk pad liep hier eigenlijk" meetbaar blijft.

### De duurdere les: acht dagen stilte

De fout zelf was één ontbrekende regel. Dat hij acht dagen bleef liggen, kwam door drie dingen die
niets met deze vertaling te maken hebben — de timer slokte zijn uitzondering op en rapporteerde
`Success`, één mislukte deelstap onderdrukte het bijwerken van `lastsynctimestamp` zonder dat
zichtbaar te maken, en niets bewaakte de leeftijd van die tijdstempel. Alle drie zijn gedicht in
#1081; zie `docs/MONITORING.md`.

Voor volgende tiervertalingen is dat het bruikbare deel: een vertaalfout is onvermijdelijk, maar de
tijd tussen ontstaan en ontdekken is een ontwerpkeuze.

## 55. Code die vooruitloopt op het schema is nu zichtbaar in `/api/health` (#1098, hotfix)

Het tweede incident van release v3.3.0.0, direct na de hotfix voor #1095 (§50): `/api/health`
gaf weer 200 met `database: online`, maar elk beheerscherm bleef ±15 seconden op "laden..." staan
en eindigde in `500 {"error":"Ophalen mislukt"}`.

**Wat er gebeurde.** v3.3.0.0 bevatte vier nieuwe migraties (012 t/m 015, epic #986).
`PostgresAppSettings.LoadSettingsAsync` selecteerde sinds die release `sportlinkextensionenabled`
uit `public.appsettings` (migratie 012). Op de Postgres-tier past niets de migraties automatisch
toe op productie: `db-migrate` in `deploy.yml` draait uitsluitend het SQL Server-PostDeployment-
script, en er staat bewust geen Supabase-connectiestring in CI (§49, stap 1). De migraties waren
bij deze release niet handmatig toegepast, dus de kolom bestond niet:

1. Postgres antwoordde `42703 undefined_column`;
2. `PostgresSystemUtilities.WaitForDatabaseAsync` vangt élke exceptie uit de laadstap als
   "database onbereikbaar" en herhaalt vijf keer met drie seconden wachttijd — de "laden..."-fase;
3. daarna gooit hij door, en elke `Admin*Function` vertaalt dat in de generieke 500.

Lokaal exact gereproduceerd door de kolom en de ledger-rij van 012 te verwijderen: 500 na 12,0 s,
`42703` in het functielog, `settingsLoaded: false` in health. Dezelfde keten als §51 punt 1
beschreef voor een verse database — alleen was de oorzaak nu geen ontbrekende club maar een
ontbrekende kolom, en de documentatie boven `AdminSettingsFunction` beweerde nog dat "de kolom
bestaat misschien nog niet" op deze tier geen realistische toestand was. Dat is gecorrigeerd.

**Waarom health en de smoke test dit niet zagen.** Health deed alleen `SHOW server_version`, en
`settingsLoaded` beschreef de *laatste* laadpoging — direct na een herstart was er nog geen
poging geweest, dus meldde een verse host `true`. De smoke test in `deploy.yml` accepteerde een
200 zonder naar de inhoud te kijken. Groen, terwijl dertien van de dertien beheerendpoints 500
gaven.

**De fix, drie lagen:**

1. **`LoadSettingsAsync` overleeft precies deze ene ontbrekende kolom.** Bij `42703` op
   `sportlinkextensionenabled` valt de lader terug op de kolomset van v3.2 en geldt
   `sportlinkExtensionEnabled = "0"` — exact de `DEFAULT false` die migratie 012 zelf zou zetten.
   Dit is bewust géén algemene fantoom-fallback (de regel uit de klassedocumentatie blijft staan):
   elke andere ontbrekende kolom is een harde fout, want daar is geen migratie-default voor. En
   het is niet stil: `schemaWarning` in health, één `LogWarning` per proces. Dezelfde "melden,
   niet weigeren"-lijn als §50/#1095.
2. **Health meldt `pendingMigrations`.** `Database.Postgres` sluit de bestandsnamen uit
   `migrations/` in als `EmbeddedResource`; `MigrationRunner.GetPendingMigrationsAsync` vergelijkt
   die met de ledger `schema_migrations`. De map blijft de enige bron van de migraties zelf
   (`RunAsync` leest van schijf); een unit-test bewaakt dat de ingesloten lijst gelijk is aan de
   mapinhoud. Niet-leeg zet `status` op `degraded`. Health doet nu ook zelf één laadpoging van de
   instellingen, zodat `settingsLoaded` een feit is en geen aanname.
3. **De smoke test faalt op `settingsLoaded=false`** en geeft een `::warning::` bij openstaande
   migraties. Dat laatste is bewust geen fout: de pipeline kán ze niet toepassen, en met laag 1
   werkt de applicatie wel. *Stand bij de hotfix:* deze laag staat klaar op de lokale branch
   `ci/#1098-smoke-test-settingsloaded`, maar kon niet mee in de hotfix-PR — GitHub eist de
   `workflow`-scope voor elke wijziging onder `.github/workflows/`, en zowel het lokale
   git-credential als de GitHub-connector van de sessie misten die. Afronden: `gh auth refresh -h
   github.com -s workflow`, daarna die branch pushen en als PR naar `main` mergen.

**Wat bewust níet is gedaan — in de hotfix.** Migraties automatisch toepassen bij het opstarten
van de Function App, of een productie-connectiestring als GitHub-secret voor een
`db-migrate-postgres`-job. Het eerste maakt van elke cold start een schemawijziging met de rechten
van de applicatie; het tweede draaide de keuze uit §49 terug. Beide waren een aparte
architectuurbeslissing, geen hotfix. *Het tweede is inmiddels genomen* — door de eigenaar, in issue
#1093, en uitgewerkt in §57. Het eerste blijft afgewezen.

**Handeling voor de eigenaar na deze hotfix:** de openstaande migraties toepassen met
`Database.Postgres.Cli` (`POSTGRES_CONNECTION_STRING` als omgevingsvariabele, nooit als argument)
en daarna controleren dat `/api/health` `"pendingMigrations": []` en `"schemaWarning": null` toont.
Tot die tijd geldt de Sportlink Web Extension als uitgeschakeld; alle overige beheerschermen werken.

**Wat deze laag niet afvangt.** Een release waarvan de code op een *andere* nieuwe kolom of tabel
leunt dan `public.appsettings.sportlinkextensionenabled`. Die meldt zich wél via
`pendingMigrations`, maar het betreffende endpoint faalt nog steeds. De structurele borging is
§57: de pipeline past de migraties zelf toe, vóór de code.

## 56. De migratie-checksum was platformafhankelijk — en blokkeerde daarmee de hele keten (#1112)

`MigrationRunner` hashte een migratiebestand over de rauwe bytes. Dat is precies genoeg om "is dit
bestand achteraf gewijzigd" te bewaken (§53), en precies te veel zodra hetzelfde bestand op twee
platforms verschillende bytes heeft.

**Hoe dat gebeurt zonder dat iemand iets wijzigt.** `.gitattributes` zet `* text=auto`. Op een
Windows-machine met `core.autocrlf=true` — de Windows-default — checkt git elk tekstbestand met
CRLF uit, ook `Database.Postgres/migrations/*.sql`; op macOS, Linux en de CI-runner staat er LF. De
blob in git is in beide gevallen identiek. Wie vanaf Windows migreert legt dus een CRLF-checksum in
`schema_migrations` vast; de eerstvolgende run vanaf LF ziet "andere checksum" en gooit de fout die
voor een *gewijzigd* bestand bedoeld is. En omdat de runner elk bestand op volgorde afhandelt en bij
de eerste fout stopt, blokkeert één zo'n rij niet alleen dat bestand maar elke migratie erna.

**Waar het gevonden is.** Niet op een verse ontwikkeldatabase, maar op de lokale container met de
herstelde productiedump (#1103/#1108): `001_baseline.sql` had daar een checksum die niet met het
bestand in `main` overeenkwam. Een eerdere sessie liet die mismatch bewust staan met de open vraag
of *productie zelf* dezelfde afwijking heeft. Een latere sessie, onwetend van die vraag, herschreef
alle elf ledger-rijen 001–011 lokaal naar de LF-waarde (per bestand geverifieerd dat de CRLF-variant
exact de oude waarde gaf — puur een regeleinde-artefact, geen inhoud) en wiste daarmee het lokale
bewijs. De vraag over productie bleef daardoor open.

**De fix, in de runner zelf.**

1. `ComputeChecksum` normaliseert `\r\n` → `\n` vóór het hashen. Een puur-LF-bestand — zoals git
   het bewaart en zoals elke LF-checkout het leest — hasht exact zoals vóór #1112, dus een ledger die
   vanaf LF is gevuld blijft zonder enige reparatie kloppen.
2. Klopt de ledger-waarde niet, dan toetst `IsLineEndingVariant` of de oude waarde de rauwe checksum
   is van *ditzelfde bestand* met andere regeleindes (de CRLF-variant van de genormaliseerde inhoud,
   óf de inhoud zoals hij nu op schijf staat). Zo ja: bewezen geen inhoudelijke wijziging, de rij
   wordt naar de genormaliseerde waarde omgeschreven en de run meldt dat als
   `ChecksumNormalized`. Zo nee: dezelfde harde fout als altijd.
3. `RunAsync` geeft nu een `MigrationRunResult` terug (nieuw toegepast / al toegepast /
   genormaliseerd). De CLI drukt dat af en zet elke normalisatie als waarschuwing op stderr, zodat
   een reparatie in een deploy-log of handmatige ronde opvalt en niet stil gebeurt.

**Wat dit voor de open productievraag betekent.** Die hoeft niet meer met de hand beantwoord te
worden. Draagt productie een CRLF-rij, dan normaliseert de eerstvolgende run (§57, of een handmatige
ronde) hem éénmalig en logt dat. Draagt productie een échte afwijking — wat niets in de historie
doet vermoeden — dan faalt die run precies zoals bedoeld, met de bestandsnaam erbij. In beide
gevallen is het antwoord zichtbaar in het log van de eerste run.

**Wat bewust níet is gedaan.** Een BOM-, whitespace- of encoding-normalisatie erbij. Alleen het
regeleinde heeft een aantoonbare, platformgebonden oorzaak (`text=auto` + `autocrlf`); elke verdere
"tolerantie" verzwakt de bewaking van §53 zonder een gevonden probleem op te lossen.

## 57. Postgres-migraties draaien nu in `deploy.yml`, vóór de code (#1093)

§55 beschreef het gat en liet de beslissing bewust open. De eigenaar heeft hem genomen: **ja, een
eigen migratiestap; en vóór het publiceren van de code.** Dit is een omkering van één onderdeel van
§49 (geen productie-connectiestring in CI) — bewust, en om deze reden: zonder een moment waarop een
migratie *automatisch geprobeerd* wordt, is er ook geen moment waarop hij *zichtbaar faalt*. Zo bleef
#1062 acht dagen liggen en gaf v3.3.0.0 twee incidenten die allebei neerkwamen op "de migraties
zijn niet gedraaid".

**De job.** `db-migrate-postgres` in `deploy.yml`, gegate op `vars.DatabaseTier == 'Postgres'`,
draait `Database.Postgres.Cli` tegen het nieuwe GitHub-secret `POSTGRES_CONNECTION_STRING` (via de
omgevingsvariabele, nooit als argument — dezelfde regel als overal in dit project). `deploy` wacht
erop (`needs` + result-check), zodat de code pas live gaat als het schema er is. De job hangt aan
`build`, niet omgekeerd: een build die niet compileert mag geen schema wijzigen voor code die nooit
gedeployed wordt. `MigrationRunner` is idempotent en neemt een advisory lock, dus een herstart van
de run is veilig.

**Waarom vóór de code, en wat dat vraagt.** Alle migraties in `Database.Postgres/migrations/` zijn
additief (`ADD COLUMN IF NOT EXISTS`, `CREATE TABLE IF NOT EXISTS`, seeds); de vorige versie van de
code draait daar ongestoord op door, en de nieuwe vindt bij haar eerste request het schema al — geen
`42703`-venster zoals in §55. Dit is vanaf nu een **ontwerpregel**: een migratie die de oude code
breekt (kolom verwijderen, type wijzigen, constraint aanscherpen) mag niet in één release met de
code die hem nodig heeft. Eerst een release die de code loskoppelt, dan een release met de
destructieve migratie.

**Ontbrekend secret is een harde fout, geen skip.** Anders dan `db-migrate` (SQL Server), die stil
overslaat als de SQL-vars ontbreken, faalt deze job met een `::error::` als het secret leeg is
terwijl de tier Postgres is. Stil overslaan is exact het gedrag dat dit issue opheft. Gevolg voor
deze installatie: de eerste deploy na deze wijziging faalt totdat de eigenaar het secret heeft
gezet — dat is de bedoeling.

**Tier-gating van de SQL Server-jobs, als bijvangst.** `db-check` en `db-migrate` waren gegate op
de aanwezigheid van de SQL-vars, niet op de tier. Die vars staan er nog, dus bij elke release sinds
de cutover draaiden beide jobs nog netjes — tegen de oude Azure SQL-database, terwijl de database
die er wél toe deed nooit gemigreerd werd. Beide hangen nu aan `vars.DatabaseTier == 'SqlServer'`.
Precies één van de twee migratiejobs draait per deploy; de andere is `skipped`.

**De smoke test is aangescherpt.** Een niet-lege `pendingMigrations` na een groene
`db-migrate-postgres` is geen waarschuwing meer maar een mislukte deploy: de job zegt dat alles is
toegepast en health zegt dat er iets mist. Dat kan maar twee dingen betekenen — het secret en de
Function App-instelling wijzen naar verschillende databases, of een bestand is wél ingesloten maar
niet toegepast — en beide verdienen een rode job.

**Afhankelijkheid van §56.** Zonder de regeleinde-normalisatie had deze job op een database met
een CRLF-ledger bij de allereerste run vastgelopen, midden in een release. De twee wijzigingen
zitten daarom in dezelfde PR, in deze volgorde.

**Voor de eigenaar, eenmalig:** secret `POSTGRES_CONNECTION_STRING` aanmaken in GitHub → Settings →
Secrets and variables → Actions, met dezelfde connectiestring als de Function App-instelling
(norm sinds #1096: `sslmode=verify-full` mét `sslrootcert`, zie §50). Supabase accepteert
verbindingen van elk IP tenzij netwerkrestricties zijn ingesteld — in dat geval de GitHub
Actions-runner-ranges toestaan of de restrictie heroverwegen.

## 58. Review epic #986 — wat de database-kant opleverde (#1122)

Een review vanuit vier rollen (architect, developer, CISO, DPO) van de Sportlink Web Extension,
feitelijk getoetst tegen een verse Postgres 17-wegwerpinstantie met alle migraties en de
AllStars-demodata. De code-bevindingen staan in `docs/SPORTLINK-WEB-EXTENSION.md` §4.2 en
CLAUDE.md; hier alleen wat de database raakte.

**Geen schema-afwijking.** Elke SQL-string in de extensie is vergeleken met `\d` van de vijf
extensietabellen plus `appsettings`/`teams`/`teamaliassen`/`velden`: kolomnamen, casing, types en
NOT NULL-vulling kloppen; elke `@parameter` is gebonden (de §54-valkuil deed zich niet voor); elke
tabel heeft een `clubcode` en elke query filtert erop; alle tijdkolommen zijn `TIMESTAMPTZ` met
`now()`/`UtcNow`.

**Migratie 018 — indexen op `sportlinkpublicmatchidcache`.** De tabel had alleen de primaire
sleutel `(wedstrijdcode, clubcode)`; twee queries filteren op `clubcode` zonder `wedstrijdcode`
(laatst-opgehaalde rij voor de contract-check en de health; `publicmatchid = ANY(@ids)` voor
#1111). Additief, `IF NOT EXISTS`.

**Bewust niet gedaan.** Een bewaartermijn op `sportlinkextensierollen` (actuele-toestand-record,
zie SECURITY.md) en op `sportlinkpublicmatchidcache`/`sportlinkcontractcheck` (geen
persoonsgegevens; groei in de orde van honderden rijen per seizoen). `his.matches.kaledatum` blijft
`varchar(50)` met een `::date`-cast — die kolom hoort bij de generieke schemagenerator (#818), niet
bij deze epic; de cast is op de huidige data bewezen veilig.

## 59. Opponent-lookup vertaald — eerste van #972's vier resterende deelstukken (#1139)

§52 documenteerde drie resterende afwijkingen tussen `BerichtPipeline`'s Postgres- en SQL
Server-tier; dit issue (#1139, deelstuk 1 van #972) heft de eerste op: `PlannerMatchRepository`
(Postgres) heeft nu een `FindMatchByOpponentAsync`, en `BerichtPipeline` roept hem aan op exact
dezelfde plek en in dezelfde tweestaps-volgorde (eerst met datum, dan zonder) als het SQL
Server-origineel.

**Geen normalisatie van de tegenstandernaam nodig.** Het origineel doet hier geen
`TeamNaamNormalisatie`-opzoeking — het is een vrije-tekst `LIKE '%...%'`-zoekopdracht op
`m.wedstrijd`/`gw.Tegenstander`, geen teamresolutie. De Postgres-vertaling volgt exact hetzelfde
patroon (`ILIKE`), dus er is geen nieuwe regex of vertaalpunt bijgekomen — de bestaande regel dat
teamnaam-normalisatie uitsluitend in `Planner.Shared/TeamNaamNormalisatie.cs` hoort, blijft
onaangeroerd.

**Eén bewuste afwijking in de oefenwedstrijd-fallback.** Het SQL Server-origineel geeft in het
`planner.GeplandeWedstrijden`-fallbackpad `AanvangsTijd` terug als `"HH:mm:ss"` (`CONVERT(...,
108)`), terwijl het `his.matches`-pad daar `"HH:mm"` teruggeeft — een asymmetrie in het origineel
zelf. De Postgres-vertaling formatteert in beide paden consistent `"HH:mm"`, zoals de rest van
`PlannerMatchRepository` (Postgres) dat al deed: `planner.geplandewedstrijden.aanvangstijd` is hier
een `TIME`-kolom, geen brontekst om 1-op-1 door te geven, dus consistentie binnen de klasse weegt
zwaarder dan het letterlijk overnemen van een SQL Server-eigenaardigheid.

De overige twee deelstukken van #972 (teamcontact-opvragen, "verzet zonder datum") en het vierde
(`EmailProcessorFunction`'s resterende gaten) blijven open — zie #972 voor de volledige scope.

## 60. Drie stukken provider-onafhankelijke logica gedeeld (SsrfProtection, feedbackkern) — ReplyPolicy bewust niet (#1130)

Een Codex-review (#1107, bevinding 5) trof `Infrastructure/SsrfProtection.cs` op beide tiers
byte-voor-byte identiek aan (na normalisatie van namespace/commentaar), `Email/ReplyPolicy.cs`
functioneel identiek, en `Feedback/FeedbackFunction.cs` ~99% gelijk. §2 hierboven staat het delen
van precies zulke pure, provider-agnostische logica al toe — dit issue voerde het door voor de
drie concrete gevallen.

**Gedeeld, zoals §2 bedoelt:**
- `SsrfProtection`/`SsrfBlockedException` → `Planner.Shared/Infrastructure/SsrfProtection.cs`. Geen
  DB/providerafhankelijkheid; beide tiers hadden alleen een andere namespace. Tests verhuisd (niet
  gedupliceerd) naar `Planner.Shared.Tests/Infrastructure/SsrfProtectionTests.cs`.
- De feedbackwidget-kern (Type-allowlist #1127, de twee PII-gates #1006, AI-promptopbouw,
  GitHub-issue-payload/-aanroep, rate limiter) → `Planner.Shared/Feedback/FeedbackCore.cs` +
  `FeedbackRateLimiter`. Elke tier houdt alleen een dunne `FeedbackFunction.cs` over: de
  `[Function(...)]`-HTTP-trigger, `EasyAuthHelper.RequireAdmin`, env-var-configuratie (GitHub PAT/
  owner/repo) en de vertaling van het resultaat naar `IActionResult`. Bewust **geen**
  `IActionResult`/ASP.NET Core-afhankelijkheid in `FeedbackCore` — dat zou dit project net als
  §2's vuistregel wil vermijden aan een HTTP-framework binden dat niet elke consument van
  `Planner.Shared` nodig heeft; `FeedbackValidatieResultaat`/`FeedbackSubmitResultaat` zijn platte
  records met een status-enum, en elke tier-`FeedbackFunction` vertaalt die zelf naar de eigen
  HTTP-respons. Nieuwe tests in `Planner.Shared.Tests/Feedback/FeedbackCoreTests.cs`; de bestaande
  `FeedbackFunctionPiiGateTests.cs` op beide tiers blijven ongewijzigd van gedrag (ze testen nu de
  dunne wrapper, die intern naar `FeedbackCore` delegeert) en dus groen zonder aanpassing van de
  assertions.

**Bewust niet gedeeld: `ReplyPolicy`.** De klasse zelf is functioneel identiek, maar de types die ze
aanneemt (`BerichtClassificatie`, `VerzoekType`) zijn dat niet: ze staan in elke tier se eigen
`Email/BerichtModels.cs`, dat op zijn beurt ándere tier-specifieke types bundelt
(`InkomendBericht`, `EmailStatus`, `ClassificatieCorrectieVoorbeeld` — zie ook §52's opmerking dat
de Postgres-tier die laatste drie al vóór de e-mailportering elders had staan). `VerzoekType`/
`BerichtClassificatie` zelf verhuizen zou geen probleem zijn omdat ze identiek zijn, maar wordt in
13 bestanden per tier (`BerichtPipeline`, `EmailClassificationService`, `BerichtAiService`,
`EmailProcessorFunction`, twee Admin-repositories, ...) gebruikt — een refactor van die omvang
valt buiten de scope van dit issue en loopt vooruit op de al geplande, bredere e-mailmodule-migratie
in `docs/ARCHITECTUUR-EMAIL-MODULE.md` (epic #777, nog niet gestart). `ReplyPolicy.cs` blijft dus
op beide tiers staan zoals het was, inclusief de eigen tests
(`FunctionApp.Tests/Email/ReplyPolicyTests.cs` en de Postgres-tegenhanger) — geen gedragswijziging.

## 61. Teamcontact opvragen vertaald — tweede van #972's vier resterende deelstukken (#1140)

§58 hief de eerste van drie in §52 gedocumenteerde `BerichtPipeline`-afwijkingen op; dit issue
(#1140, deelstuk 2 van #972) heft de tweede op: `AllstarsTestDataRepository.GetTeamleiderContactAsync`
is vertaald naar de Postgres-tier, en `BerichtPipeline`'s `TeamContactOpvragen`-tak geeft nu een echte
`coachGevonden` terug in plaats van altijd `false`.

**Matching-sleutel bewust anders dan het SQL Server-origineel.** Het origineel vergelijkt met een
eigen `REPLACE(...,' ','')REPLACE(...,'-','')`-sleutel rechtstreeks in T-SQL. De Postgres-vertaling
gebruikt in plaats daarvan `TeamNaamNormalisatie.NormaliseerVoorVergelijking` — de enige toegestane
teamnaam-normalisatielaag (zie `docs/ARCHITECTUUR-TEAMRESOLUTIE.md`) — toegepast in C# op elke
kandidaatrij uit `avg.teambegeleiding`, in plaats van een tweede ad-hoc regex/REPLACE-implementatie
in SQL te bouwen. Zelfde precedent als `PlannerMatchRepository.TeamSchrijfwijzenAsync`/
`FindMatchByOpponentAsync` (§58). Functioneel gelijk gedrag: zowel de lokale notatie ("JO13-1") als
de KNVB-notatie ("O13-1") normaliseren naar dezelfde sleutel, dus is er geen aparte
"knvbSleutel"-tweede parameter nodig zoals op de SQL Server-tier.

**`PostgresClubScope.LegacyFilter` toegevoegd.** `avg.teambegeleiding.clubcode` is `NOT NULL DEFAULT
''` (migratie 002) — dezelfde "kan leeg zijn, hoort dan bij de primaire club"-situatie als
`avg.Teambegeleiding` op de SQL Server-tier. `PostgresClubScope` had tot dit issue alleen `HisFilter` (voor
`his.*`) en `AddClubParam` (voor tabellen met een strikt `NOT NULL`-ClubCode); `LegacyFilter` is de
Postgres-tegenhanger van `ClubScope.LegacyFilter` op de SQL Server-tier.

**`EmailProcessorFunction`'s teamleider-/teamcontact-vervolgnotificaties (#66/#168) hersteld naar
volledige pariteit** — zie §52's "Eén nieuw ontdekte afwijking": beide gebruikten
`AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync` (alleen een e-mailadres, geen naam, dus een
generieke aanhef). Nu gebruiken ze `AllstarsTestDataRepository.GetTeamleiderContactAsync`, woordelijk
gelijk aan het SQL Server-origineel — inclusief "Hoi {naam}," in de herplanverzoek-notificatie.
`EmailReplyPolicyService` blijft ongewijzigd: die roept `GetTeamleiderContactAsync` op de SQL
Server-tier uitsluitend aan binnen de KNVB-PDF-bijlage/"verzet zonder datum"-tak (§52's derde
afwijking, nog open) — geen consument op de Postgres-tier vandaag, dus geen wijziging nodig.

De overige twee deelstukken van #972 ("verzet zonder datum", `EmailProcessorFunction`'s resterende
gaten — dat laatste al opgelost via de #972-hotfix, zie §52) blijven open voor het deel dat nog
niet is vertaald — zie #972 voor de volledige scope.

## 62. "Verzet zonder datum" vertaald — derde en laatste van #972's vier resterende deelstukken (#1141/#561)

§58 en §61 hieven de eerste twee van de drie in §52 gedocumenteerde `BerichtPipeline`-afwijkingen
op; dit issue (#1141, deelstuk 3 van #972) heft de derde en laatste op: de KNVB-PDF-bijlage +
vrije-zaterdagen-voorzet voor een herplanverzoek van de tegenstander zonder concrete nieuwe datum
(#561) werkt nu ook op de Postgres-tier, in plaats van altijd terug te vallen op het standaard
herplanpad.

**Nieuwe tabel `public.knvbkalenderdag` (migratie 019).** Postgres-tegenhanger van
`dbo.KnvbKalenderDag` — landelijke KNVB-speeldagenkalender per regio/seizoen, geen ClubCode-kolom
(zelfde reden als het SQL Server-origineel). De seed is mechanisch overgenomen uit
`Database/Script.PostDeployment1.sql` (seizoenen 2025/2026 en 2026/2027, alle 8 seizoen/regio-
blokken, 423 rijen) — `BIT` 1/0 → `BOOLEAN`, `N'...'` → `'...'`, `[Kolom]` → kolom, en de
`IF NOT EXISTS`-per-blok-guard van het origineel vervangen door één `INSERT … ON CONFLICT
(seizoen, regio, datum) DO NOTHING` op de primaire sleutel — functioneel gelijkwaardig, want de
PK-kolommen zijn identiek aan de guard-kolommen. Geregistreerd in de CI-dekkingsscripts
(`check-postgres-table-coverage.sh`/`check-postgres-column-coverage.sh`); de eerdere
`dbo.KnvbKalenderDag`-uitzonderingsregels in beide scripts zijn verwijderd.

**`PostgresAppSettings` laadt nu `knvbpdfbijlageingeschakeld`/`knvbstandaardregio`.** Beide kolommen
bestaan onvoorwaardelijk sinds migratie 003 — geen optionele-kolom-dans zoals bij
`sportlinkextensionenabled`/`sportlinkdryrun` (zie de klassekop van `PostgresAppSettings.cs`) nodig.
`AdminSettingsFunction` (Postgres) had deze twee instellingen al in de GET/PUT-whitelist en
-validatie staan (uit een eerdere sessie) — alleen de procesbrede cache miste ze nog.

**Drie nieuwe klassen, telkens een tegenhanger van het SQL Server-origineel:**
- `FunctionApp.Postgres/Planner/Repositories/KnvbKalenderRepository.cs` —
  `GetVrijeZaterdagenAsync`, leest `public.knvbkalenderdag` met dezelfde filters (dagtype
  Competitie/Beker/Inhaal, alleen zaterdagen, `maxAantal`, uitsluiting van al bezette data).
- `FunctionApp.Postgres/Sync/PostgresSeasonHelper.GetCurrentKnvbSeizoenAsync` — leest
  `public.season`, zelfde "eerste seizoen met `dateuntil >= vandaag`"-semantiek als het
  SQL Server-origineel.
- `FunctionApp.Postgres/Email/KnvbPdfService.cs` — leest de KNVB-kalender-PDF's als Content-bestand.
  **Geen gedeelde `Planner.Shared`-service**: hoewel de leeslogica zelf provider-onafhankelijk is
  (alleen `EmailBijlage` + `ILogger` nodig), heeft elke tier al zijn eigen `EmailBijlage`-record in
  een eigen namespace (`SportlinkFunction.Email`/`FunctionApp.Postgres.Email`) — een gedeelde
  `KnvbPdfService` zou een gedeeld `EmailBijlage`-type vereisen, en dat is een grotere refactor dan
  dit issue rechtvaardigt. Een twin, net als `EmailBijlage` zelf, is hier het consistente patroon.

**PDF-bestanden: één bron, twee build-outputs.** `FunctionApp.Postgres.csproj` verwijst met een
MSBuild `Content Include`+`Link` naar dezelfde bronbestanden als
`FunctionApp/fa-dev-sportlink-01.csproj` (`../FunctionApp/Content/KnvbKalenders/2026-2027/*.pdf`)
in plaats van een tweede 1,3 MB-kopie in git te zetten. Beide tiers krijgen zo bij het builden hun
eigen kopie in de output-directory (nodig voor twee losse deployments), zonder dat het bronbestand
twee keer in de repository staat.

**`BerichtPipeline` (Postgres): `BouwVerzetZonderDatumResponseAsync` toegevoegd**, woordelijk gelijk
aan het SQL Server-origineel — regio/bijlage-instelling uit `ClubAppSettingsSnapshot` (dry-run pad)
of `PostgresAppSettings` (echte mailbox-verwerking), seizoen via `PostgresSeasonHelper`, vrije
zaterdagen via `KnvbKalenderRepository`, reeds bezette data via
`PlannerMatchRepository.GetFutureMatchesForTeamAsync` (bestond al). Ontbreekt de regio of staat de
bijlage-instelling uit, dan blijft het bestaande fallbackgedrag gelden — expliciet gelogd
("VERZET-ZONDER-DATUM - geen knvbStandaardRegio ingesteld; val terug op het standaard herplan-pad"),
niet stilzwijgend. `BerichtResponseGenerator.BouwVerzetZonderDatumAntwoord` (het antwoord-sjabloon)
en `EmailReplyPolicyService`'s BCC/bijlage-tak (via
`AllstarsTestDataRepository.GetTeamleiderContactAsync`, §61, en `KnvbPdfService`) zijn eveneens
vertaald — beide fail-safe: een mislukte contact- of PDF-lookup verstuurt de mail gewoon zonder BCC
of bijlage, nooit een crash.

Met dit issue is #972 volledig afgerond: alle vier de resterende deelstukken (opponent-lookup §58,
teamcontact §61, verzet-zonder-datum hier, `EmailProcessorFunction` al via de #972-hotfix, zie §52)
zijn nu vertaald.

## 63. Npgsql 10: `DATE` en `TIME` komen als `DateOnly`/`TimeOnly` uit de niet-generieke leespaden (#1170)

Npgsql 10.0 wijzigde de standaard-CLR-mapping van twee kolomsoorten:

| PostgreSQL-type | Npgsql 9 gaf | Npgsql 10 geeft |
|---|---|---|
| `date` | `DateTime` | **`DateOnly`** |
| `time without time zone` | `TimeSpan` | **`TimeOnly`** |
| `timestamp` / `timestamptz` | `DateTime` | `DateTime` (ongewijzigd) |
| `interval` | `TimeSpan` | `TimeSpan` (ongewijzigd) |

Dit geldt uitsluitend voor de **niet-generieke** leespaden: `ExecuteScalar()` en
`NpgsqlDataReader.GetValue(i)`. De sterk getypeerde lezers (`reader.GetDateTime(i)`,
`reader.GetTimeSpan(i)`, `GetFieldValue<T>()`) blijven gewoon converteren en zijn niet geraakt. Het
schrijfpad is evenmin geraakt: een `DateTime`-parameter naar een `date`-kolom en een
`TimeSpan`-parameter naar een `time`-kolom werken onveranderd.

**Waarom dit gevaarlijker is dan het lijkt.** De gangbare vorm in deze codebase was

```csharp
var result = await cmd.ExecuteScalarAsync();
return result is DateTime einde ? DateOnly.FromDateTime(einde) : null;
```

Een niet-passend patroon geeft hier **geen fout**. De `is`-test faalt gewoon en de methode neemt
haar "niets gevonden"-tak. Bij de upgrade bestonden vier zulke plekken; precies één had
testdekking en viel meteen om, de andere drie waren volledig stil:

| Plek | Kolom | Stil gevolg |
|---|---|---|
| `PlannerSettingsRepository.GetSunsetAsync` | `zonsondergang` (TIME) | planner gebruikt de berekende i.p.v. de opgeslagen zonsondergang |
| `PlannerSettingsRepository.GetSeasonEndDateAsync` | `season.dateuntil` (DATE) | "geen seizoen bekend" terwijl de rij bestaat |
| `PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync` | `season.dateuntil` (DATE) | synchronisatievenster valt terug op de vaste 30 weken |
| `PostgresSeasonHelper.GetSeasonStartWeekOffsetAsync` | `season.datefrom` (DATE) | idem, −40 weken |

**Regel.** Tast een `ExecuteScalar`-resultaat van een `date`- of `time`-kolom af op `DateOnly`
respectievelijk `TimeOnly` — nooit op `DateTime`/`TimeSpan`. Staat er een `is`-patroon op een
scalar zonder dat er een test op zit, dan is dat per definitie een stille-faalkandidaat: de test
hoort erbij. `FunctionApp.Postgres.Tests/PostgresTemporeleScalarMappingIntegrationTests.cs` dekt
de drie die dekking misten, zodat de volgende Npgsql-major hier luidruchtig faalt.

**Wat hier níet door geraakt werd, en waarom.** De `LeesRij`-mapper
(`Admin/Repositories/RepositoryRijMapper.cs`) gebruikt wél `GetValue` en voedt daarmee de JSON van
de beheer-endpoints. Elke betrokken query selecteert date- en time-kolommen echter al via
`to_char(...)`, dus die komen als tekst terug en het API-contract verandert niet. Zou een nieuwe
query een `date`-kolom rechtstreeks selecteren, dan wijzigt de JSON van `"2026-09-15T00:00:00"`
naar `"2026-09-15"` — een contractwijziging die de Admin GUI raakt. Houd `to_char` aan.

**TLS.** Npgsql 10 valideert servercertificaten alleen nog tegen root-CA's (gelijk aan libpq). Dat
raakt uitsluitend `VerifyCA`/`VerifyFull`.

> **Bijgewerkt bij #1236.** Deze alinea stelde dat productie op `Require` draait en daarom
> ongewijzigd is, met `verify-full` als "nog openstaande stap". Allebei achterhaald: sinds
> v3.4.1.0 draait productie op **`verify-ca`** mét de meegeleverde CA (#1187, zie §50), en
> `verify-full` is op het pooler-endpoint **onhaalbaar** — dat certificaat heeft alleen een CN
> (`*.pooler.…`) en geen SubjectAltName, waar `verify-full` juist tegen valideert. Dit is dus geen
> openstaande stap maar een gesloten afweging.
>
> Gevolg voor Npgsql 10: omdat productie op `VerifyCA` staat, is de root-CA-eis **wél** van
> toepassing. Het bestand waar `sslrootcert` naar wijst moet de **root**-CA bevatten, niet alleen
> een tussenliggend certificaat. Het meegeleverde `FunctionApp.Postgres/prod-ca-2021.crt` voldoet
> daaraan; controleer dit opnieuw zodra de provider zijn CA vernieuwt (geldig tot 2031-04-26).

## 64. `his.matches`/`his.teams` reconciliëren nu na elke sync — Sportlink is de waarheid (#1193)

**Probleem.** De `stg → his`-upsert (`PostgresUpsertGenerator`) voegde rijen toe en werkte ze bij,
maar verwijderde nooit iets. Een wedstrijd die de club in Sportlink annuleerde of verwijderde bleef
daardoor voor altijd in `his.matches` staan — met alle gevolgen van dien voor elke lezende query
(Dagplanning, auto-plan, veldbezetting). `mta_deleted` bestond al in het schema (nullable
`TIMESTAMPTZ` op alle drie de entiteiten, zie §8) maar werd nergens gezet.

**Besluit — soft-delete, geen hard delete.** Na de `stg → his`-merge markeert een nieuwe
reconciliatiestap elke `his`-rij die niet meer in de zojuist geladen `stg`-snapshot voorkomt als
verwijderd (`mta_deleted = NOW()`). De rij blijft bestaan — `his` is een audit-trail — maar elke
lezende query sluit hem voortaan uit. Zelfde principe als `TeamCanonicalisatieService.DeactiveerOntbrekendeTeamsAsync`
voor `public.teams` (§ elders in dit document): "verdwenen bij de bron" wordt een status, geen
verwijdering.

**Scope, de kern van de ontwerpvraag.** Een sync-run bevraagt nooit de hele kalender — `/programma`
bevraagt een weekoffset-venster, `/uitslagen` altijd minstens de laatste twee weken. Zonder venster
zou reconciliatie legitieme wedstrijden buiten het bevraagde bereik ten onrechte als verdwenen
markeren. Twee entiteiten, twee scopes:

- **`teams`** (`PostgresMergeOrchestrator.ReconcileFullScopeAsync`) — `/teams` kent geen
  weekoffset-parameter, dus `stg.teams` is bij elke run een complete club-snapshot. Reconciliatie
  hoeft dan alleen op `clubcode` te scopen.
- **`matches`** (`ReconcileWindowedAsync`) — het venster wordt **niet** uit de weekoffset-parameters
  teruggerekend naar kalenderdata: hoe Sportlink een weekoffset precies naar een kalenderweek
  vertaalt (welke dag een "week" begint) staat nergens gedocumenteerd, dus is dat niet hard na te
  bouwen aan de app-kant — een wal-klok-gebaseerde gok zou het risico lopen legitieme data buiten
  het echte venster te raken. In plaats daarvan wordt het venster afgeleid uit de MIN/MAX van
  `kaledatum` die déze sync-run daadwerkelijk in `stg.matches` laadde voor de club: gegarandeerd
  nooit breder dan wat er echt bevraagd is. Levert `stg` voor de club geen enkele rij op (mislukte
  fetch, of een sync-venster dat toevallig nul wedstrijden opleverde), dan gebeurt er niets — dat is
  zelfhelend zodra een latere sync wél minstens één wedstrijd in een venster teruggeeft dat de
  betrokken datum omvat.
- **`matchdetails` reconcilieert bewust NIET mee.** `/wedstrijd-informatie` wordt per wedstrijdcode
  los opgehaald; een individuele fetch-fout voor één wedstrijd zou anders een detailrij van een wél
  nog bestaande wedstrijd onterecht als verdwenen kunnen markeren.

**Best-effort, net als teamcanonicalisatie.** De reconciliatiestap draait ná een succesvolle merge
en is per entiteit in een eigen try/catch — een fout hierin mag een al geslaagde ETL-run niet alsnog
laten falen, en alleen wanneer de bijbehorende fetch-fase zonder fouten verliep (anders zou een
onvolledige `stg`-snapshot legitieme rijen als verdwenen kunnen aanmerken).

**Alle lezers aangepast.** `PostgresClubScope.HisFilter` — het predicaat achter vrijwel elke
`PlannerMatchRepository`-query — sluit nu ook `mta_deleted IS NULL` in, en
`planner.alle_wedstrijden_op_veld_ruw` (`PostgresPlannerViewGenerator`) filtert `m.mta_deleted`/
`t.mta_deleted` in respectievelijk de `WHERE`- en de team-`JOIN`. Een nieuwe lezer van `his.matches`
of `his.teams` filtert dit voortaan zelf mee — het is geen automatisme via een view of trigger.

**Nooit een tweede reconciliatie-implementatie.** `Database.Postgres/PostgresReconciliationGenerator.cs`
is de ene plek die het `UPDATE ... SET mta_deleted = NOW() WHERE NOT EXISTS (...)`-statement bouwt,
gedreven door dezelfde `EntityDefinition`/business-key-expressie als de upsert. Een nieuwe
`his.*`-entiteit die ooit hetzelfde soort reconciliatie nodig heeft roept
`ReconcileFullScopeAsync`/`ReconcileWindowedAsync` aan — geen eigen `NOT EXISTS`-query ernaast.

## 65. Row-Level Security alsnog ingeschakeld — #985 had een onvolledig dreigingsmodel (#1198)

**Supabase's eigen Security Advisor meldde op 13 september 2026 een CRITICAL-bevinding**
(`rls_disabled_in_public`) op de productietier: elke tabel in `public` was extern
leesbaar/schrijfbaar/verwijderbaar. Dit issue reconstrueert waarom, en waarom geen enkele
codereview — Claude Code, Codex, of een mens — dit ooit gemeld heeft.

### Wat #985 goed deed, en wat het miste

§985 (2026-09-04) beoordeelde RLS vanuit drie rollen (CISO, DPO, Architect) tegen de architectuur
van dit project en kwam tot een bewust, gedocumenteerd besluit om RLS niet te implementeren. De
redenering zelf klopte, voor de vraag die ze stelde:

- Er is precies één vertrouwde databaseclient (de FunctionApp, via één
  `POSTGRES_CONNECTION_STRING`-rol) — geen per-eindgebruiker databaserollen, geen client-side
  gebruik van Supabase's PostgREST/JWT-auth-flow.
- Autorisatie zit volledig in de applicatielaag (Entra ID/Easy Auth, vijf lagen defense-in-depth).
- `ClubCode` wordt gefilterd in C#, niet via databasepolicies — en dat is prima, want er is
  precies één (productie)club per deployment (zie "Deployment-model" in CLAUDE.md).

**De vraag die #985 niet stelde:** wat stelt Supabase als *platform* zelf standaard open, los van
of onze applicatie dat gebruikt? Supabase genereert voor élke tabel in het `public`-schema
automatisch een PostgREST-REST-endpoint. Dat endpoint is bereikbaar met de anon-key — een sleutel
die *bewust* niet geheim is (Supabase's eigen documentatie noemt hem veilig om in een browser te
plaatsen), **omdat RLS wordt verondersteld die toegang te begrenzen.** Zonder RLS is die
veronderstelling vals: de anon-key wordt dan een universele sleutel tot elke `public`-tabel, voor
wie dan ook met de project-URL — ongeacht of déze applicatie ooit een regel PostgREST-code
uitvoert. #985's conclusie "er is maar één vertrouwde client" ging over clients die *wij* bouwen;
Supabase's eigen infrastructuur is zelf ook een client, en die stond na het besluit van #985
onbedoeld wagenwijd open.

### Waarom geen review dit ooit meldde

Dit is geen codefout die aan een reviewer voorbij is gegaan — het is een *afwezigheid* die nooit
in de vorm van code heeft bestaan. Geen enkele van de 20 migraties tot #1198 bevatte ooit een
regel over RLS, in beide richtingen: geen `ENABLE ROW LEVEL SECURITY`, maar ook geen `DISABLE`. Er
was dus geen diff, geen bestand, geen PR-regel om op te reageren. Een codereview — of die nu door
Claude Code, Codex, of een collega wordt gedaan — beoordeelt wat er verandert in de repository.
Databaseplatform-configuratie die uitsluitend via een externe dashboard-toggle bestaat (of, zoals
hier, door een bewust besluit om iets *niet* te doen) laat structureel geen spoor na dat
git-gebaseerde review kan zien. Supabase's eigen Security Advisor draait tegen de levende
database, niet tegen de repository — dat is precies waarom hij dit wél zag en wij niet.

### De oplossing

`Database.Postgres/migrations/021_enable_row_level_security.sql` zet RLS aan op alle 29
toepassingstabellen (`public`, `avg`, `planner`) — inclusief de migratie-ledger `public.schema_migrations`
zelf, want Supabase's advisor onderscheidt niet naar gevoeligheid. **Zonder policies, bewust:** de
`POSTGRES_CONNECTION_STRING`-rol is eigenaar van elke tabel (zij heeft ze aangemaakt) of is de
Supabase-superuser via de pooler — Postgres omzeilt RLS onvoorwaardelijk voor zo'n rol, met of
zonder policies. Lokaal empirisch bevestigd tijdens #1198: na het inschakelen van RLS op alle
tabellen bleef `SELECT`/`INSERT` via de gewone ontwikkelrol (ook lokaal een superuser-equivalent)
ongewijzigd werken; `pg_roles.rolbypassrls` bevestigt dit. Dit sluit dus uitsluitend Supabase's
eigen `anon`/`authenticated`-PostgREST-toegang — precies het gat dat de advisor meldde — zonder de
rest van #985's conclusie (geen per-rij-autorisatielogica nodig) te herzien.

### Wanneer dit ánders zou moeten

Dezelfde twee triggers als in #985 al genoemd, plus een derde:

1. Het deployment-model verandert naar *shared multi-tenant hosting* (meerdere echte clubs met
   eigen admins in één database) → dan zijn per-tabel policies wél nodig, niet alleen "RLS aan".
2. Supabase's eigen PostgREST/JWT-auth wordt ooit rechtstreeks gebruikt in plaats van de
   FunctionApp als enige gatekeeper → dan moeten er policies komen die op `auth.uid()` filteren.
3. **Nieuw, uit dit issue:** bij elke toekomstige architectuurbeoordeling van een hostingplatform
   (Supabase of anders) hoort expliciet de vraag "wat ontsluit dit platform zelf standaard, los
   van onze eigen code?" — zie de nieuwe harde regel in CLAUDE.md/AGENTS.md onder "Supabase
   Postgres — Row-Level Security verplicht op elke tabel".

## 66. `rls_auto_enable()` — een vangnet blijkt geen overbodig artefact (vervolg op #1198)

Ná migratie 021 meldde Supabase's Security Advisor twee nieuwe WARN-bevindingen op
`public.rls_auto_enable()`: een `SECURITY DEFINER`-functie, aanroepbaar door `anon` en
`authenticated` via `/rest/v1/rpc/rls_auto_enable`.

**Eerste, verkeerde hypothese:** de functie staat in geen enkele migratie of C#-bestand in dit
repository — een volledige grep bevestigde dat. De aanname was dus dat ze een overbodig bijproduct
was van een eenmalige "fix"-actie in het Supabase-dashboard, en de eerste versie van migratie 022
liet haar dan ook droppen.

**Waarom dat mis was, en hoe dat aan het licht kwam vóórdat het schade kon doen:** de `DROP
FUNCTION` faalde direct met `2BP01: cannot drop function ... because other objects depend on it`.
`pg_get_functiondef` en `pg_event_trigger` lieten zien waarom: de functie retourneert
`event_trigger` en is gekoppeld aan het event-trigger `ensure_rls` (`ddl_command_end`) —
geregistreerd naast onmiskenbaar Supabase-eigen platform-triggers (`pgrst_ddl_watch`,
`issue_pg_cron_access`, `issue_pg_graphql_access`, `issue_pg_net_access`). Dit is Supabase's eigen
aanbevolen mechanisme om RLS automatisch aan te zetten op elke **nieuwe** `public`-tabel — precies
het vangnet dat het probleem van migratie 021 voor toekomstige tabellen voorkomt. Hem droppen had
dus een nieuwe, blijvende regressie geïntroduceerd in ruil voor het "oplossen" van een bevinding
die niets met de functie zelf te maken had.

**De werkelijke oorzaak van de WARN, en de juiste fix:** Postgres kent `EXECUTE` op een nieuwe
functie standaard toe aan de rol `PUBLIC`, en Supabase's PostgREST-laag ontsluit elke
`public`-functie met `EXECUTE` voor `PUBLIC`/`anon`/`authenticated` automatisch als RPC-endpoint —
ongeacht of de functie voor extern gebruik bedoeld is. Een event-trigger-functie is dat per
definitie niet (Postgres roept hem uitsluitend intern aan via de event-trigger-machinerie).
Migratie `022_revoke_public_execute_rls_auto_enable.sql` haalt daarom alleen `EXECUTE ... FROM
PUBLIC` weg; functie en event trigger blijven volledig intact. Lokaal geverifieerd tijdens dit
vervolgonderzoek: ná de revoke bleef `ensure_rls` enabled, en het aanmaken van een verse testtabel
resulteerde nog steeds direct in `relrowsecurity = true` — het vangnet werkt, alleen de onbedoelde
publieke aanroepbaarheid is weg.

**Les voor toekomstige Supabase-bevindingen:** een object dat niet in de eigen migraties/code
voorkomt is niet automatisch overbodig — het kan een deel van Supabase's eigen
platforminfrastructuur zijn. Controleer altijd `pg_get_functiondef`/`pg_event_trigger` (of het
Supabase-equivalent) vóór een `DROP`, en laat een `DROP` die faalt op een dependency-fout eerst de
vraag "waarom bestaat dit object" beantwoorden in plaats van de fout te omzeilen met `CASCADE`.

## 67. `rls_auto_enable()`, tweede poging — de PUBLIC-grant was niet de enige (vervolg op #1198/§66)

Ná deploy van migratie 022 bleef Supabase's Security Advisor dezelfde twee WARN-bevindingen tonen
op `public.rls_auto_enable()` — de fix uit §66 loste het probleem dus niet volledig op.

**Oorzaak:** Supabase kent bij projectaanmaak standaard `EXECUTE` op elke `public`-functie toe aan
de rollen `anon` en `authenticated` — een grant die **los staat** van de `PUBLIC`-grant die
Postgres zelf standaard zet. Migratie 022 trok alleen de `PUBLIC`-grant in; de twee
Supabase-eigen, rechtstreekse grants aan `anon`/`authenticated` bleven onaangeroerd.

**Waarom dit niet eerder opviel bij de lokale verificatie:** de rollen `anon` en `authenticated`
bestaan helemaal niet in de lokale/CI-Postgres. Het zijn cluster-brede rollen die Supabase's
controlplane bij projectaanmaak aanmaakt — geen onderdeel van een gewone database-restore, want
rollen zijn cluster-objecten, geen database-objecten. Migratie 022 kon dus lokaal probleemloos
slagen zonder het echte productiegat te dekken; de lokale verificatie bewees alleen dat de
`PUBLIC`-revoke geen fout gaf, niet dat het probleem opgelost was.

**De fix, en hoe hij dit keer wél lokaal aantoonbaar geverifieerd is:** migratie
`023_revoke_anon_authenticated_rls_auto_enable.sql` trekt `EXECUTE` in bij `anon` en
`authenticated`, met een existence-check per rol zodat de migratie op elke omgeving foutloos
draait. Omdat deze rollen lokaal ontbreken, is het *ontbrekende-rol-pad* alleen een "geen fout"
bewijs — onvoldoende. Daarom is de daadwerkelijke revoke-logica apart bewezen: `anon` en
`authenticated` tijdelijk lokaal aangemaakt, `EXECUTE` erop gezet (de productiesituatie
nagebootst), exact de DO-block-inhoud van de migratie handmatig uitgevoerd, bevestigd dat beide
grants verdwenen waren, en de testrollen daarna weer opgeruimd.

**Les, aanvullend op §66:** "geen fout bij het toepassen" bewijst niet "het probleem is opgelost"
wanneer de omgeving waartegen je test de rollen die het probleem veroorzaken niet eens kent. Bij
een Supabase-specifieke rol/grant-fix: simuleer de rol lokaal (`CREATE ROLE ... NOLOGIN`) en
herhaal het exacte scenario, in plaats van te vertrouwen op "de migratie gaf geen foutmelding".

## 68. De RLS-regel afgedwongen in plaats van opgeschreven — en waarom een lokale test hem niet kan bewijzen (#1220)

§65 tot §67 losten het RLS-gat op en legden de regel vast: elke nieuwe tabel krijgt in dezelfde
migratie een `ENABLE ROW LEVEL SECURITY`. Die regel stond daarna correct in `CLAUDE.md` en
`AGENTS.md` — en werd door niets gecontroleerd.

Dat is dezelfde vorm als het oorspronkelijke probleem. §65 verwoordde het al:

> Dit is geen codefout die aan een reviewer voorbij is gegaan — het is een *afwezigheid* die nooit
> in de vorm van code heeft bestaan.

Een migratie 025 die de `ALTER TABLE` vergeet komt door élke bestaande controle heen: er ontbreekt
geen bestand, er mist geen kolom, er is geen diff om op te reageren. Het zou opnieuw pas blijken
wanneer Supabase's Security Advisor het in productie meldt. Bij #1198 duurde dat twaalf dagen.

### Wat er is toegevoegd

Twee guards in de job `fresh-db-postgres` van `build.yml`, beide tegen de **levende** database ná
het toepassen van de migraties — niet tegen bestanden, want een tabel kan in migratie 012 zijn
aangemaakt en pas in 021 RLS hebben gekregen:

| Script | Vraag die het beantwoordt |
|---|---|
| `scripts/ci/check-rls-enabled.sh` | Heeft elke tabel in `public`/`avg`/`planner` `relrowsecurity = true`? |
| `scripts/ci/check-splinter-lints.sh` | Meldt Supabase's eigen linter iets op de punten die vóór livegang te beoordelen zijn? |

Splinter (`supabase/splinter`) is de motor onder de Security- en Performance Advisor in het
dashboard. Het wordt **niet** in dit repo opgenomen: upstream levert geen LICENSE-bestand (GitHub
rapporteert de licentie als `null`), en code zonder expliciete licentie herdistribueren in een
publieke repository is een onnodig risico. In plaats daarvan wordt het tijdens de CI-run opgehaald,
vastgepind op een commit-SHA én geverifieerd op SHA-256 — reproduceerbaar en manipulatiebestendig,
zonder herdistributie.

### Wat bewust níet faalt, en waarom dat geen compromis is

Van de 29 splinter-lints laten er vijf de build falen. De rest wordt geteld en getoond, maar
blokkeert niets. Twee van die keuzes zijn principieel:

- **`rls_enabled_no_policy` gaat op alle 29 tabellen af — dat ís de architectuur.** §65 koos bewust
  voor RLS zonder policies. Dit lint gaten zou betekenen dat #985/#1198 wordt teruggedraaid.
- **`unindexed_foreign_keys` is schema-statisch, maar de vraag is dat niet.** Of een foreign key een
  index nodig heeft, hangt af van hoe er gequeryd wordt, niet van hoe het schema eruitziet. #1211
  liet dat precies zien: 22 Performance Advisor-bevindingen, getoetst tegen productie, waarvan er
  drie een index kregen (migratie 024) en de rest bewust bleef staan. Een gate op een verse
  CI-database zou die afweging afdwingen zónder de gegevens die ervoor nodig zijn — en zou vandaag
  meteen op zes bestaande, al beoordeelde bevindingen falen. Dit hoort bij de dagelijkse run tegen
  productie (#1221), die wél statistieken heeft.

### De blinde vlek van §67, nu omgekeerd

§67 legde vast dat de rollen `anon` en `authenticated` lokaal en in CI niet bestaan, waardoor
grant-gebaseerde controles er stille no-ops zijn. `check-splinter-lints.sh` maakt die rollen nu aan
voordat het draait — splinter weigert zonder hen zelfs te starten (`role "anon" does not exist`).
Daarmee is de les van §67 van een instructie in een architectuurdocument een uitgevoerde stap in de
pipeline geworden.

Tijdens het bouwen kwam de spiegelbeeldige variant aan het licht, en die is minstens zo belangrijk:

> **Een lokale database die uit een productiedump is hersteld, kan deze guard niet laten falen.**

De negatieve test — een tabel zonder RLS aanmaken en controleren dat de guard rood wordt — slaagde
niet lokaal. De guard bleef groen en telde de nieuwe tabel zelfs mee als *wél* beveiligd. Oorzaak:
een herstelde productiedump bevat Supabase's event-trigger `ensure_rls` (→ `public.rls_auto_enable()`,
de functie uit §66), die op élke `CREATE TABLE` automatisch RLS aanzet. De tabel kreeg RLS voordat
de guard ernaar keek.

```
ensure_rls -> rls_auto_enable (ddl_command_end)
pgrst_ddl_watch -> extensions.pgrst_ddl_watch (ddl_command_end)
issue_pg_cron_access, issue_pg_graphql_access, issue_pg_net_access, ...
```

Het bewijs is daarom geleverd op een **verse** `postgres:17`-container met alleen de migraties
erop — exact de CI-situatie, en de enige omgeving waar de negatieve test iets betekent. Daar
gedroegen beide guards zich zoals bedoeld: exit 1 met een bruikbare foutmelding op een overtreding,
exit 0 zodra die verholpen is.

**Les, aanvullend op §67:** §67 waarschuwde dat lokaal iets kan *ontbreken* dat productie wél heeft.
Het omgekeerde geldt net zo goed: lokaal kan iets *aanwezig* zijn dat CI en een verse installatie
niet hebben, en dat een test stilzwijgend laat slagen. Bij elke guard die een databasetoestand
bewaakt: stel vast in welke omgeving de negatieve test betekenis heeft, en draai hem dáár. Een guard
die nooit heeft gefaald, is geen geverifieerde guard.

### Bijvangst

Zowel `CHANGELOG.md` als §65 spraken van "alle 27 toepassingstabellen". Migratie 021 bevat 29
`ALTER TABLE`-regels, en zowel een verse CI-database als de lokale productiekopie tellen er 29.
Beide plekken zijn gecorrigeerd naar 29 — het is precies het getal dat de nieuwe guard rapporteert,
dus een afwijking hier zou toekomstige sessies op het verkeerde been zetten.

## 69. Supabase Performance Advisor getoetst — welke meldingen een defect zijn en welke bewust blijven (#1211)

> **Verhouding tot §68 (#1220).** Die paragraaf zet dezelfde linter (splinter) als CI-gate in,
> maar **sluit precies de drie lintfamilies uit die hieronder behandeld worden** —
> `unindexed_foreign_keys`, `unused_index` en `no_primary_key` — omdat ze contextafhankelijk zijn
> en een verse CI-database geen gebruiksstatistiek heeft. Die uitsluiting en de analyse hieronder
> zijn twee kanten van dezelfde conclusie: deze drie lints vragen een menselijk oordeel per geval,
> niet een harde gate. Wat hieronder staat is dat oordeel, voor de run van 16 september 2026.

De Supabase Performance Advisor meldde 22 bevindingen (alle INFO). Alle 22 zijn empirisch getoetst
tegen het schema, de queries in `FunctionApp.Postgres/` en een lokale Postgres 17. **Twee waren een
echt defect, één is een integriteitsprobleem dat de advisor als performanceprobleem labelt, en
negentien zijn correct waargenomen maar vragen bewust géén actie.** Deze paragraaf legt vooral dat
laatste vast: zonder die onderbouwing wordt bij elke volgende advisor-run dezelfde analyse opnieuw
gedaan, of erger, wordt een index gedropt die juist nodig is.

### De kernles: "unused index" is een waarneming, geen diagnose

`ix_teamaliassen_club_genormaliseerd` werd gemeld als *unused index — candidate for removal*. De
index was inderdaad nooit gebruikt, maar de voorgestelde remedie was precies verkeerd: hij was niet
ongebruikt omdat de app stil is, maar omdat **geen enkele query hem kón gebruiken.**

Migratie 003 legde hem aan op de kale kolom `(clubcode, ruwetekstgenormaliseerd)`. Migratie 007
(#820, collatie-fix) zette daarna alle vergelijkingen op die kolom om naar `UPPER(...)` en zette
`public.teams` en `teamaliassen.ruwetekst` wél om naar expressie-indexen — maar
`ruwetekstgenormaliseerd` niet. Een gewone b-tree kan een predicaat `UPPER(kolom) = ...` niet
bedienen, dus de index stond er sindsdien als pure schrijflast.

Gemeten op 60.000 rijen, met de échte queryvorm uit `TeamCandidateRepository.cs:53` en
`PlannerMatchRepository.cs:66` (een `OR` over `ruwetekst` en `ruwetekstgenormaliseerd`):

| Geval | Vóór 024 | Na 024 |
|---|---|---|
| Geen treffer (meest voorkomende pad) | `Seq Scan`, 14,50 ms, 609 buffers, 60.000 rijen gefilterd | `BitmapOr` over beide expressie-indexen, 0,046 ms, 6 buffers |
| Treffer achteraan de tabel | `Seq Scan`, 14,53 ms, 609 buffers | `BitmapOr`, 0,04 ms, 7 buffers |

De `OR` is hier het interessante deel: Postgres kan hem alleen efficiënt afhandelen als **beide**
takken een bruikbare index hebben. Zolang één tak onbruikbaar was, viel het hele predicaat terug op
een volledige scan — de expressie-index op `ruwetekst` uit 007 leverde in deze query dus niets op.
Dat verklaart waarom dit zo lang onzichtbaar bleef: de index uit 007 leek de zaak gedekt te hebben.

Eén meetartefact, om verkeerde conclusies te voorkomen: bij een treffer vóór in de heap is de
`Seq Scan` mét `LIMIT 1` juist sneller (0,015 ms), omdat hij direct kan stoppen. Dat is de gunstige
uitzondering, niet de norm — bij teamherkenning is "geen gevalideerde alias gevonden" het normale
pad, en dat is exact het geval dat de volledige scan afdwingt.

**Regel hieruit:** een `UPPER()`/`LOWER()`-vergelijking in een query vereist een expressie-index op
diezelfde uitdrukking. Wijzig je een vergelijking naar `UPPER(...)`, dan is het bijwerken van de
bijbehorende index onderdeel van diezelfde wijziging — niet iets voor later.

### Wat bewust niet gewijzigd is

**Zeven "unused index"-meldingen: de feature draait nog niet.** `ix_sportlinkpublicmatchidcache_*`,
`ix_sportlinkmutationaudit_*`, `ix_sportlinkcontractcheck_*` en `ix_knvbkalenderdag_datum` komen uit
migraties 013–019 en horen bij de Sportlink Web Extension (epic #986), die nog niet in productie
draait. Per index is gecontroleerd dat de query wél bij de indexvorm past — bijvoorbeeld
`WHERE clubcode = @clubcode ORDER BY opgehaaldop DESC LIMIT 1` tegenover `(clubcode, opgehaaldop DESC)`.
Twee ervan zijn bij #1122 juist toegevoegd om een groeiende volledige scan te voorkomen; droppen zou
die fix terugdraaien. **Een advisor die "nooit gebruikt" meldt, kan niet zien dat een feature nog
niet live is** — dat onderscheid moet altijd handmatig gemaakt worden.

**`IX_matchdetails_clubcode`: nutteloos, maar droppen levert niets op.** In het vastgelegde
deploymentmodel (§"Deployment-model" in `CLAUDE.md`) draait één primaire club per deployment, dus
`clubcode` heeft in de praktijk één distinct waarde — lokaal geverifieerd: alle rijen in
`his.matches` dezelfde waarde. Een index met die selectiviteit wordt nooit gekozen. Dat geldt even
goed voor `IX_matches_clubcode` en `IX_teams_clubcode`, die de advisor níet noemde — reden te meer
om hier niet selectief te gaan droppen. De winst is verwaarloosbaar, de kosten zijn schema-churn en
een afwijking tussen de tiers.

**Vier van de zes onindexeerde FK's: begrensde tabellen.** `veldbeschikbaarheid` (30 rijen) en
`veldtraining` (0) zijn structureel begrensd door velden × dagen van de week (±63 rijen); een
FK-index daarop kan per definitie nooit iets opleveren. De afweging bij een onindexeerde FK gaat
**niet** over de omvang van de ouder maar over die van het kind: bij een `DELETE` op de ouder scant
Postgres het kind per verwijderde rij. Daarom is `planner.geplandewedstrijden` (groeit per seizoen)
wél meegenomen en zijn de twee begrensde configuratietabellen dat niet.

**`no_primary_key` op `his.*` en `stg.*`: ontwerpkeuze.** De drie `his`-tabellen hebben elk al een
unique index op hun business key (`UQ_matches_bk`, `UQ_teams_bk`, `UQ_matchdetails_bk`), dus de
efficiëntiezorg is al ondervangen; een gedeclareerde PK zou cosmetisch zijn. De `stg`-tabellen
worden elke run getruncate en bulk-geladen — een PK voegt daar alleen ingest-kosten toe.
`public.season` heeft `ux_season_name`. Geen actie.

### `public.appsettings` heeft géén enkele constraint — apart op te pakken

De advisor meldt dit onder *no primary key / performance*. Met twee rijen is dat de verkeerde bril:
het echte punt is dat `pg_constraint` voor deze tabel **nul rijen** teruggeeft — geen PK én geen
unique op `clubcode`. Niets verhindert twee rijen met dezelfde `clubcode`, terwijl de code
instellingen leest met `SELECT ... LIMIT 1`. Een dubbele rij geeft dan geen fout maar stilzwijgend
de verkeerde configuratie — precies wat de regel "geen stille fallback" in `CLAUDE.md` wil
voorkomen.

Bewust niet in migratie 024 opgelost: een `CREATE UNIQUE INDEX` in een migratie die automatisch bij
deploy draait (#1093) **faalt hard als productie al dubbele rijen heeft, en neemt dan de deploy
mee** (§57). Dit vereist eerst een controle op de productiedatabase en is daarom een aparte,
door de eigenaar bevestigde stap.

### Over het query performance log

Het log van dezelfde run bevatte geen aanknopingspunt voor tuning: de zwaarste queries zijn
Supabase's eigen platformverkeer, niet de applicatie. `SELECT name FROM pg_timezone_names` (rol
`authenticator`) is alleen al 41,9% van de totale databasetijd, de extensie-inventarisatie 17,6% en
`pgbouncer.get_auth` 5,0%; samen met de catalogusqueries van PostgREST en het dashboard is dat het
overgrote deel. Dat is dashboard- en connectiepooler-overhead. **Bij het lezen van een Supabase
query-performance-log is de eerste vraag dus welke `rolname` een query uitvoert** — `authenticator`,
`postgres`, `pgbouncer` en `supabase_admin` zijn platform, niet applicatie.

## 70. De migratie-CLI meldt het exceptietype, niet de foutmelding — CI-uitvoer is publiek (#1225)

`Database.Postgres.Cli` schreef bij een mislukte migratie `ex.Message` naar stderr. Sinds §57 draait
die CLI in de job `db-migrate-postgres` van `deploy.yml`, en **de Actions-logs van deze repository
zijn publiek**. GitHub maskeert uitsluitend de exacte, volledige waarde van een secret — niet een
deelstring ervan die toevallig in een foutmelding staat. Een Npgsql-verbindingsfout luidt
`Failed to connect to <host>:<poort>`; bij een authenticatiefout kan Npgsql ook de gebruikersnaam
noemen, die bij de gehoste provider de projectidentificatie draagt. Alle drie zijn onderdelen van
`POSTGRES_CONNECTION_STRING`.

Gereproduceerd met een synthetische connectiestring. In de laatste 40 deploy-runs is de job nooit
gefaald, dus dit was een openstaand lekpad, geen vastgesteld lek — maar precies dezelfde foutklasse
als #1200, waar de Sportlink-clientId via een log-URL lekte.

**Wat er nu gebeurt.** `Database.Postgres/MigratieFoutRapportage.cs` is de ene plek die een mislukte
databasehandeling naar een consoleregel vertaalt: aanhef + stap + exceptietype, plus de SQLSTATE-code
bij een `PostgresException`. `MigrationRunner.RunAsync` heeft daarvoor een optionele
`onMigratieStart`-callback gekregen — de aanroeper weet zo wélk migratiebestand faalde zonder de
exception te hoeven lezen of in te pakken (en zonder dat de bestaande exception-typen wijzigen,
waar de integratietests op toetsen). `MigrationTools/SqlServerToPostgresCopy` gebruikt dezelfde
helper; ook dat hulpmiddel opent verbindingen met host en wachtwoord erin, en zijn uitvoer belandt
in de praktijk in een issue.

**Waarom dit bruikbaar blijft om fouten mee te zoeken.** Bestandsnaam plus SQLSTATE wijzen de
oorzaak aan: het bestand de SQL, de SQLSTATE de foutklasse (`42601` syntax, `42703` onbekende kolom,
`23505` unique violation). De volledige melding staat in de databaselogs van de provider en is
lokaal reproduceerbaar tegen een wegwerpcontainer.

**Bewust niet gedaan: een conditionele variant** die de volledige melding wél schrijft zodra de
omgeving aantoonbaar niet-publiek is. Een schakelaar die bepaalt of een secret in een log belandt,
is één configuratiefout verwijderd van een lek; de altijd-veilige vorm kost hier niets dat niet
elders terug te vinden is.

**Geborgd in CI.** De job `pii-patterns` in `security-scan.yml` bevatte al de #1200-guard op
`ILogger`-templates met een URL-placeholder. Die keek alleen naar `ILogger`, niet naar
`Console.Error`/`Console.Out` — de reden dat dit pad langs de vorige controle kwam. De guard faalt
nu ook op een `Console.Error.WriteLine`/`Console.WriteLine` met een geïnterpoleerde exception in
productie-C# (testprojecten uitgezonderd, die noemen zo'n vorm juist letterlijk).


## 71. Eerste geauthenticeerde MCP-run — nulmeting, en de RLS-vraag die hij deels beantwoordt (#1234)

Op **2026-09-17**, kort na release v3.5.0.0, is `/supabase-check` voor het eerst met een echt token
tegen productie gedraaid (#1222 leverde de configuratie; #1234 de run). Vijftien MCP-aanroepen,
read-only en project-scoped. Uitkomst: **geen bevindingen**.

Dit is de nulmeting waartegen elke volgende run zich laat afzetten. Zonder vastgelegde nulmeting is
"het aantal is gestegen" over een paar weken niet vast te stellen.

### Nulmeting 2026-09-17

| Signaal | Waarde |
|---|---|
| Advisors | Uitsluitend INFO. Geen ERROR, geen WARN — dus ook niets voor de baseline |
| `rls_enabled_no_policy` | 29 tabellen — dat *is* de architectuur van #985/#1198, geen defect |
| `unindexed_foreign_keys` | 4 — #1211 gaf er drie een index in migratie 024; de rest bleef bewust staan |
| `no_primary_key` | 8 — staat open als #1235 |
| Postgres-logs, 24 uur | 63 regels, alle `LOG/00000` (checkpoints); één losse `08006` connection-close, ver onder de piekdrempel |
| Edge-logs, 24 uur | 22 requests, alle 200, uitsluitend Supabase's eigen health-endpoints |
| Pgbouncer | Alleen `server idle timeout (age=600s)` — normaal poolgedrag |
| Auth-/autorisatiefouten | Geen |
| 5xx | Geen |
| Databaseomvang | **17 MB = 3,4 %** van de 500 MB Free-grens |
| Grootste tabel | `his.matches` — 1,4 MB, 850 rijen |
| Verbindingen | Alleen platformrollen plus de read-only MCP-sessie; geen groei |

De agent maakte geen issue aan, want er viel niets te melden. Dat is het bedoelde gedrag: stilte is
de uitkomst bij een schone database, niet een teken dat de controle niet heeft gedraaid.

### De `execute_sql`-vraag: deels beantwoord, en waarom "deels" hier belangrijk is

#1222 liet één vraag open: geeft `execute_sql` in read-only mode rijen terug op een
applicatietabel, of nul? Read-only mode verbindt als een **niet-eigenaar**, en sinds #1198 heeft
elke applicatietabel RLS aan zonder policies — zonder `BYPASSRLS` levert `SELECT` dan nul rijen op
**zonder foutmelding**.

**Wat deze run bewijst:** catalogusquery's werken volledig. Databaseomvang (`pg_database_size`),
tabelgroottes en rijschattingen (`pg_class`/`pg_stat_user_tables`), en actieve verbindingen
(`pg_stat_activity`) kwamen allemaal terug met echte waarden. De capaciteitscontrole van de
monitorprompt functioneert dus.

**Wat deze run níet bewijst:** of een gewone `SELECT` op bijvoorbeeld `public.appsettings` rijen
oplevert. De "850 rijen" hierboven is een **catalogusschatting**, geen `SELECT COUNT(*)` op de
tabel zelf. De twee paden lopen langs verschillende rechten: catalogusweergaven zijn niet
RLS-beschermd, applicatietabellen wel.

Dat onderscheid is precies het soort verschil dat §66 en §67 duur hebben geleerd — "het gaf geen
fout" en "het gaf het juiste antwoord" zijn niet hetzelfde, en een RLS-gat komt hier stil naar
boven als een lege resultaatset in plaats van als een foutmelding.

**Om het af te maken**, in een sessie met de MCP geladen:

```
Vraag via de Supabase MCP, read-only:
  SELECT COUNT(*) FROM public.appsettings;
  SELECT COUNT(*) FROM public.velden;
```

- **Komt er een getal > 0 terug** → de read-only rol heeft `BYPASSRLS` (of is tabeleigenaar), en
  datavragen via MCP werken gewoon. Noteer dat hier.
- **Komt er 0 terug terwijl de tabel aantoonbaar rijen heeft** → dat is **correct gedrag**: RLS
  zonder policies sluit de niet-eigenaar buiten. **Niet repareren met policies** — dat heropent
  #985/#1198 en zet het gat weer open dat Supabase's advisor destijds als CRITICAL meldde. De
  monitorprompt moet dan voor datavragen terugvallen op de catalogus of op
  `Database.Postgres.Cli`-toegang vanuit CI.

Zolang dit niet is vastgesteld: vertrouw voor de monitor uitsluitend op catalogusquery's — die zijn
aantoonbaar betrouwbaar — en behandel een lege resultaatset uit een applicatietabel als
"onbeantwoorde vraag", nooit als "de tabel is leeg".

### Terzijde: de sessie zelf is zichtbaar in de meting

De MCP-sessie verscheen in `pg_stat_activity` als extra verbinding. Dat is geen ruis maar een
bruikbaar detail: het bevestigt dat de read-only verbinding daadwerkelijk tot stand kwam, en het
verklaart waarom het aantal verbindingen tijdens een controle één hoger ligt dan erbuiten.

## 72. Demodata van de democlub: waarom een eenmalige migratie het verkeerde gereedschap was (#1246)

De democlub AllStars FC was zowel lokaal als in productie een halve club: wél een instellingenrij,
velden, veldbeschikbaarheid en een teamregel uit migratie 006 — maar **0 teams, 0 wedstrijden,
0 teambegeleiding en 0 speeltijden**. De teamregel die 006 aanmaakt verwijst naar `AllStars Heren 1`,
een team dat nergens bestond. Dat wijkt af van het deploymentmodel in `CLAUDE.md` ("precies één
echte club + AllStars FC als demo/testdata — in dezelfde database").

### Twee onafhankelijke oorzaken

**(a) De team-/wedstrijdseed draaide nergens automatisch.**
`scripts/migrations/003-seed-allstars-demo-matches-postgres.sql` (28 teams, 224 wedstrijden,
28 teambegeleiders) kán geen migratie zijn: het vult `his.teams`/`his.matches`, en die tabellen
maakt geen enkel migratiebestand aan — `PostgresSchemaGenerator` doet dat dynamisch bij de eerste
ETL-sync (§-les van #856). Die analyse was correct en goed opgeschreven. Wat ontbrak was de
vervolgstap: er kwam nooit een plek die het script daarna alsnog uitvoerde.
`scripts/dev/Seed-AllStarsDemodata.ps1` overbrugde het voor een ontwikkelmachine; `deploy.yml` had
geen equivalent. **Een correcte analyse met een ontbrekende uitvoerstap is functioneel gelijk aan
geen analyse.**

**(b) De speeltijden-copy in migratie 006 kón per definitie niet slagen.**
Regel 54-60 van 006 kopieert speeltijden van de primaire club naar de democlub. Dat is de *enige*
`INSERT INTO public.speeltijden` in alle migraties: de primaire club vult zijn speeltijden via de
Admin GUI, dus op migratiemoment valt er niets te kopiëren. De copy leverde 0 rijen op, en omdat
een migratie eenmalig is en `IF NOT EXISTS`-gated, werd het nooit opnieuw geprobeerd.

> **De generieke les:** een eenmalige migratie mag niet afhangen van data die pas later door een
> gebruiker wordt ingevoerd. Zulke afhankelijkheden horen in een **herhaalbare, idempotente
> seedstap**, niet in de ledger.

### Waarom CI dit niet ving

`fresh-db-postgres` verifieerde de copy-logica van 006 wél — maar bouwde daarvoor eerst met de hand
de gunstige volgorde: een `CIPRIMARY`-club plus een speeltijdenrij neerzetten, en 006 daarna nóg een
keer als los bestand draaien. Die constructie bewees dat de *query* klopt, nooit dat de *volgorde*
klopt. In werkelijkheid draait 006 één keer, via de ledger, vóór er één speeltijd bestaat.

Dat is dezelfde klasse blinde vlek als §67: de test stelde een gunstiger wereld op dan de
werkelijkheid, en bewees daardoor iets anders dan hij leek te bewijzen.

### Wat er nu staat

| Waar | Wat |
|---|---|
| `scripts/migrations/003-...-postgres.sql` | Speeltijden-copy erbij (inclusief `standaardvoorkeurtijd`, die 006 niet meenam). Idempotent; `RAISE NOTICE` als de primaire club nog niets heeft |
| `Database.Postgres/DemodataSeeder.cs` | Draait het seedscript en telt daarna wat de democlub werkelijk heeft. Slaat over (geen fout) als de democlub niet in `appsettings` staat — een fork mag hem weghalen |
| `Database.Postgres.Cli --seed-demodata <pad>` | Derde CLI-modus, zodat `deploy.yml` het script kan draaien zonder de connectiestring door een `psql`-argument te halen (#1225-regel) |
| `deploy.yml`, job `db-migrate-postgres` | Na de migraties: `--ensure-his-tables`, dan `--seed-demodata`. Beide idempotent, bij elke release |
| `build.yml`, job `fresh-db-postgres` | Nieuwe stap op een eigen database die de **echte** installatievolgorde nabootst, met een before-assertie (0 speeltijden na alleen de migraties) als rode test |

**Migratie 006 is bewust ongewijzigd gebleven** — hij is toegepast en checksum-bewaakt; hem
repareren zou elke bestaande database blokkeren. De copy staat nu op een plek die wél opnieuw mag
draaien, en de oude copy in 006 blijft een no-op.

### Wat de pipeline níet kan, en waarom dat een signaal is

`public.teams` is een **afgeleide** tabel en wordt uitsluitend opgebouwd door
`POST /api/beheer/teams/herstel` (#946). Dat endpoint is `RequireAdmin` en vereist een Entra-token,
dat een deploypipeline niet heeft en ook niet hoort te hebben. Automatiseren zou betekenen dat de
canonicalisatielogica een tweede keer in SQL wordt nagebouwd — precies de fixture die #946 en
`Seed-AllStarsDemodata.ps1` bewust vermeden.

Daarom meldt de seedstap dit in plaats van het op te lossen: bij `his.teams > 0 AND public.teams = 0`
schrijft de CLI de marker `DEMOCLUB_CANONIEKE_LIJST_ONTBREEKT`, en `deploy.yml` maakt daar een
`::warning::` van met de verwijzing naar de knop op de pagina Teamaliassen. Bewust **geen**
build-breker: de rest van de demodata staat er wel, en een deploy laten falen op een demoklus is
niet in verhouding.

---

## 73. Thema-logica gedeeld — en de platformafhankelijke bug die de duplicatie verborgen hield (#1248, #1252)

Vierde stuk provider-onafhankelijke logica dat naar `Planner.Shared` verhuist, na de drie van §60.
`FunctionApp/Admin/AdminThemeFunction.cs` en `FunctionApp.Postgres/Admin/AdminThemeFunction.cs`
bevatten dezelfde zeven regexen, dezelfde `_skipColors`-lijst, dezelfde hexvalidatie, dezelfde
SSRF-allowlist-flow en dezelfde standaardkleuren als magic strings; het enige echte verschil was de
databaseclient en de kolomnaam-casing. De Postgres-tier was hier een generieke 1-op-1 poort (#887),
geen bewuste keuze voor thema-logica — maar het gevolg was wel dat elke wijziging aan het
kleurmodel twee keer met de hand moest, in twee bestanden die niets van elkaar weten.

**Gedeeld:** `Planner.Shared/Theming/ThemeCore.cs` — kleur-/favicon-/logo-extractie, hexvalidatie,
de allowlist-vergelijking, `ThemeUpdateRequest`, de standaardkleuren en het GET-responscontract.
Zelfde vorm als `FeedbackCore` (§60): een pure klasse zonder ASP.NET Core-afhankelijkheid, met
status-enums en resultaatrecords. Elke tier houdt alleen de eigen databasetoegang over en vertaalt
een status naar `IActionResult`. Beide bestanden zijn daarmee van ~310 naar 178 regels gegaan en
verschillen nog uitsluitend in `SqlConnection` vs. `NpgsqlConnection`, de query-tekst en de
klasse-documentatie.

Eén detail dat bij het delen bewaard moest blijven: de allowlist-host komt als **lui**
`Func<Task<string?>>` binnen, niet als kant-en-klare waarde. Anders zou een onbruikbare URL ineens
eerst een `WaitForDatabaseAsync` + query kosten, terwijl beide tiers de vorm van de URL daarvóór al
afwezen. Een ontdubbeling die stilletjes de volgorde verandert is geen ontdubbeling meer.

### De bug die pas zichtbaar werd toen er voor het eerst een test op stond

Er bestond geen enkele test op deze logica — precies het risico dat #1248 beschrijft. De tests die
bij deze consolidatie zijn toegevoegd vielen meteen om op zes gevallen, en dat bleek geen
testfout maar **#1252**:

```csharp
if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
    return abs.Scheme == "http" || abs.Scheme == "https" ? abs.ToString() : null;
if (Uri.TryCreate(baseUri, url, out var rel))      // ← onbereikbaar voor "/pad"
```

Op Unix parseert `Uri.TryCreate("/favicon.ico", UriKind.Absolute, out _)` **succesvol**, als
`file:`-URI. De eerste tak wordt dus genomen, het schema is `file`, en de methode geeft `null`
terug; de relatieve tak is voor root-relatieve paden onbereikbaar. Gevolg: favicon- en
logo-extractie leverden in productie **nooit** iets op — ook de ingebouwde terugval `/favicon.ico`
niet — zonder foutmelding, want `null` is een geldige waarde in een geslaagd antwoord.

Op Windows geeft dezelfde aanroep `false` en werkt de code wél zoals bedoeld. Dat is de reden dat
dit jaren onopgemerkt bleef: de fout bestaat alleen op het platform waar de code draait (Linux
Consumption) en niet op het platform waar een ontwikkelaar hem het snelst zou zien.

**De les, breder dan thema:** `Uri.TryCreate(..., UriKind.Absolute, ...)` is geen betrouwbare test
voor "is dit een absolute URL" wanneer de invoer ook een pad kan zijn. Gebruik
`UriKind.RelativeOrAbsolute` en beslis daarna op `IsAbsoluteUri`. Dezelfde valkuil zat in
`HostUitWebsiteUrl`, waar een opgeslagen waarde als `/pad` een lege host opleverde in plaats van
`null`; die controleert nu expliciet op schema én niet-lege host. Beide zijn fail-closed, dus er
was geen security-gat — maar wel een stille onjuistheid.

## 74. Kleurenpalet per modus als JSON — en waar een SQL Server-schemawijziging écht hoort (#1254)

Epic #1249 heeft per modus (licht/donker) een volledige kleurenset nodig, niet vier platte kolommen.
Twee ontwerpkeuzes, en één correctie op een aanname die in de uitvoeringsinstructie stond.

**Eén JSON-document per modus, geen kolom per kleur.** `themecolorslightjson` en
`themecolorsdarkjson` (`TEXT`/`NVARCHAR(MAX)`) in plaats van een kolom per kleur. Het aantal kleuren
groeit binnen dit epic nog — een kolom per kleur betekent bij elke uitbreiding een nieuwe migratie
in twee tiers, plus een nieuw veld in twee DTO's en een nieuwe validatieregel. Additief bovenop de
bestaande vier platte `themecolor*`-kolommen, die de terugval blijven voor clubs zonder
licht/donker-set; een bestaande installatie merkt van deze migratie dus niets.

**De prijs van een vrij sleutelveld is dat de vorm van sleutel én waarde vastgelegd moet worden.**
De waarde belandt in de browser in een CSS custom property (`--theme-<sleutel>-light`), samengesteld
uit door een admin ingevoerde tekst. `ThemeCore` legt daarom vast: een sleutel matcht
`^[a-z][a-zA-Z0-9-]{0,39}$`, een waarde `^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$`, maximaal 40 sleutels
per palet. Een admin is binnen het deploymentmodel van §393 vertrouwd, dus dit is geen
autorisatiegrens — maar een waarde die ongefilterd een stylesheet-property vult hoort een vaste vorm
te hebben, en een vrije `rgba(...)`-string zou dat niet zijn. De acht-cijferige hexvariant bestaat
precies omdat de hover-schaduw alpha nodig heeft; dat is de reden om `#rrggbbaa` toe te staan en
`rgba()` niet.

Bij het teruglezen geldt het omgekeerde: `PaletUitJson` geeft `null` bij onleesbare of ongeldige
inhoud in plaats van een uitzondering. Een kapot palet in één kolom mag nooit het hele
thema-endpoint laten vallen — de club valt dan terug op de platte kleuren.

### Een SQL Server-schemawijziging hoort in `Script.PostDeployment1.sql`, niet in `scripts/migrations/`

De uitvoeringsinstructie van #1250 vroeg om een nieuw bestand
`scripts/migrations/005-add-theme-colors-json-to-appsettings.sql`, naar het patroon van `003`. Dat
patroon bestaat, maar **die map wordt door niets automatisch uitgevoerd**: `scripts/migrations/`
bevat seed- en eenmalige hulpscripts die met de hand of via `DemodataSeeder` draaien (zie §72 en
`docs/DEVELOPER-SETUP.md`). De SQL Server-schemawijziging die de deploy daadwerkelijk toepast staat
in `Database/Script.PostDeployment1.sql` — dat is wat de `db-migrate`-job uitvoert en wat de
CI-job "PostDeployment op verse database" test. `003-add-favicon-logo-to-appsettings.sql` heeft
daarom een tegenhanger op regel 362 van dat bestand; het losse bestand in `scripts/migrations/`
was het duplicaat, niet het mechanisme.

De nieuwe kolommen staan dus op drie plekken die elk hun eigen rol hebben:

| Plek | Rol |
|---|---|
| `Database/Script.PostDeployment1.sql` | Wat de deploy en CI toepassen op een bestaande én verse SQL Server-database |
| `Database/dbo/Tables/AppSettings.sql` | De SSDT-tabeldefinitie — beschrijft hoe de tabel eruit hoort te zien |
| `Database.Postgres/migrations/026_appsettings_theme_modes.sql` | De Postgres-tier, via `MigrationRunner` |

Bij die gelegenheid is ook de drift gedicht die #1250 correct opmerkte: `[FaviconUrl]` en
`[LogoUrl]` stonden sinds #339 wél in `Script.PostDeployment1.sql` en dus live in productie, maar
waren nooit aan de SSDT-tabeldefinitie toegevoegd. De definitie beschreef de echte database dus al
niet meer. Ze staan er nu in.

**Let op bij het nummeren:** #1250 noemde `025` voor de Postgres-migratie, maar dat nummer was
inmiddels bezet door `025_appsettings_primaire_sleutel.sql` (#1218). Een migratiebestand wordt nooit
achteraf gewijzigd (§53), dus een dubbel nummer is niet terug te draaien — controleer de map altijd
op het moment van schrijven, niet het nummer uit een issue dat eerder is opgesteld.

## §-verwijzingen in migratiekoppen — vertaaltabel (#1236)

> **Migratiebestanden worden nooit achteraf gewijzigd.** `MigrationRunner` legt per bestand een
> SHA-256 vast en weigert een bestand dat al is toegepast maar sindsdien is gewijzigd; dat zou elke
> volgende migratie op bestaande databases blokkeren (zie §53 en de checksum-guard in `build.yml`).
> De §-nummers in hun koppen verwijzen daarom naar de nummering zoals die gold op het moment van
> schrijven. Dit document is sindsdien hernummerd. Gebruik deze tabel:

| Migratie | Kop verwijst naar | Bedoelde sectie nu |
|---|---|---|
| `021_enable_row_level_security.sql` | §64 | **§65** — RLS alsnog ingeschakeld, #985 had een onvolledig dreigingsmodel |
| `022_revoke_public_execute_rls_auto_enable.sql` | §65 | **§66** — `rls_auto_enable()`, een vangnet blijkt geen overbodig artefact |
| `023_revoke_anon_authenticated_rls_auto_enable.sql` | §66/§67 | **§66 en §67** — klopt nog; §67 is de tweede poging (de PUBLIC-grant was niet de enige) |
| `024_index_tuning_performance_advisor.sql` | §69, §57 | **§69** klopt (Performance Advisor getoetst); **§57** klopt (migratie die de vorige code breekt) |

**Voorkom dat dit opnieuw ontstaat:** verwijs in nieuwe migratiekoppen niet naar een §-nummer maar
naar de **titel** van de sectie, eventueel met het issuenummer erbij. Een titel overleeft een
hernummering, een nummer niet. Dus `zie "Row-Level Security alsnog ingeschakeld" (#1198)` in plaats
van `zie §65`.

## Gerelateerd

Onderdeel van epic [#815](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/815).
