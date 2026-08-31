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
  daadwerkelijk zou aanroepen.
- **`TeamResolver`/`TeamDisambiguationAiService`/`TeamlijstGereedheid`** — zelfde reden, zie §28.

## Gerelateerd

Onderdeel van epic [#815](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/815).
