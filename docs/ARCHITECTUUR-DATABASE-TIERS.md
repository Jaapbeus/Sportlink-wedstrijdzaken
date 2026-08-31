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

**Tier-keuze is bewust onveranderlijk na de eerste deploy — afdwinging nog niet gebouwd.** #816
legt vast dat de repository-variabele `DatabaseTier` de keuze bepaalt (hard-fail bij een
ontbrekende/onbekende waarde), maar een *wijziging* van een reeds actieve, geldige waarde naar
een andere tier zou vandaag stilzwijgend bij de eerstvolgende reguliere deploy worden toegepast —
dat mag niet zonder expliciete migratiebevestiging. Zolang er maar één tier (`SqlServer`)
daadwerkelijk bestaat, is een echte switch fysiek niet mogelijk (de resolver weigert
`Postgres`/`Sqlite` al hard omdat die bomen nog niet bestaan) — het handhavingsmechanisme zelf
(bijv. een vergelijking met de vorige gedeployde tier + een handmatige approval-gate) is daarom
een open ontwerppunt, te bouwen zodra een tweede tier daadwerkelijk gebouwd wordt en een switch
voor het eerst fysiek mogelijk is.

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
| [#820](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/820) — Collation/case-sensitivity-fix | Correctheid van teamnaam-matching onder Postgres |
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
| [#853](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/853) — Business-key-kolom vs. demodata-seed | Besluit over `GENERATED ALWAYS`; zonder dit faalt de seed volledig |
| [#854](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/854) — UTC in audit-tijdstempels | `NOW()` schrijft lokale tijd; empirisch 2 uur afwijking in de zomer |
| [#855](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/855) — Identifier-casing | De boom volgt sectie 3 van dit document niet consequent |

### 6b. Blokkades in de bestaande tier (niet Postgres-specifiek)

| Sub-issue | Levert op |
|---|---|
| [#856](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/856) — Seed slaat zichzelf over op een verse database ✅ opgelost (Optie B, zie §13) | Zonder dit zijn er op elke nieuwe installatie nul demoteams en -wedstrijden |
| [#857](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/857) — Synchronisatie-rem is dode code | Een lokale run praat nu met de externe bron en kan issues aanmaken |
| [#858](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/858) — AVG-maskering hangt aan kolomcasing | Onder lowercase-identifiers lekken volledige e-mailadressen naar de browser |
| [#859](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/859) — Stille faalpaden rond databaseconfiguratie | Gezondheidscheck geeft 200 zonder database; wachtlus duurt vijf minuten |

### 6c. Ontbrekende scope

| Sub-issue | Levert op |
|---|---|
| [#860](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/860) — Applicatie-datalaag en projectopzet (kapstok) | **Het grootste gat**: 40 bestanden, ~212 SQL-statements. Uitgewerkt naar vijf sub-issues: [#891](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/891) (projectopzet, blokkerend, gemerged), [#887](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/887) (beheer), [#888](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/888) (planner), [#889](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/889) (e-mail/teamresolutie), [#890](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/890) (synchronisatie) |
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
isolated-worker-project (net9.0 — zelfde harde beperking als de bestaande `FunctionApp`, zie
sectie ".NET versie" in CLAUDE.md), met een `ProjectReference` naar `Database.Postgres` (#818) en
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
buiten #889's eigen scope-omschrijving).

## Gerelateerd

Onderdeel van epic [#815](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/815).
