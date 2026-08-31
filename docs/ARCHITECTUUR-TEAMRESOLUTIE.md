# Architectuur — teamnaam naar TeamId (teamresolutie)

> Status: volledig opgeleverd (#692, #696, #697, #698, #699, #700, #701). De oude regex-normalisatie
> en stringheuristieken zijn verwijderd: deze laag is het enige pad waarlangs een team wordt herkend.

## Het probleem

Inkomende e-mail moet aan de juiste wedstrijd worden gekoppeld. Dat vereist dat een teamaanduiding
uit vrije tekst ("JO13-2", "JO 13-2", "13-1") wordt herleid tot één specifiek team. Vóór deze
vertaallaag gebeurde dat op vier plekken onafhankelijk van elkaar:

1. een AI-classificatie die vrije tekst teruggaf zonder de teamlijst van de club te kennen;
2. regex-normalisatie in `BerichtPipeline` die losse spellingsvarianten repareerde;
3. een heuristiek die op stringvorm besloot of een team "de eigen club" was ("geen spatie" = eigen);
4. `LIKE '%teamnaam%'`-matching in SQL, die bij meerdere treffers stilzwijgend de eerste koos.

Elke stap gokte zelfstandig. Een tegenstander die zijn eigen team zonder spatie schreef, werd
daardoor als eigen team geclassificeerd — thuis en uit omgedraaid, zonder enige foutmelding.

## Waarom dit geen kwestie van "betere regex" is

Sportlink levert elk team in **twee schrijfwijzen** aan, die naar hetzelfde fysieke team verwijzen
maar géén gedeelde sleutel hebben (`teamcode` is `-1` bij lokale teams, `lokaleteamcode` is `-1` bij
bondsteams). Geverifieerd tegen live `stg.teams`-data:

| `teamsoort` | Notatie | Voorbeelden |
|---|---|---|
| `lokaal` | clubeigen, mét `J`, streepje bij G-teams | `JO10-1`, `MO13-1`, `G-1`, `1` |
| `bond` | KNVB, mét clubprefix, **zonder** `J` | `[club] O10-1`, `[club] MO13-1`, `[club] G1`, `[club] 1` |

Dit verklaart de al bestaande `O13 → JO13`-regel: die was nooit een e-mailtypfout-correctie, maar
het verschil tussen bonds- en lokale notatie. Voor één club leverden 255 ruwe `his.teams`-namen
**116** werkelijke teams op.

Verder komen in echte data voor: veteranen (`[club] 35+1`), vrouwen (`[club] VR1`,
`[club] VR30+1`), gemengde teams met een `JM`-suffix (`[club] O14-1JM`), G-teams, en teams met een
eigen naam zonder patroon. Een normalisatie die dat niet aankan, mangelt bestaande teams.

Het scheidingsteken tussen leeftijd en teamnummer varieert óók: streepje (`MO13-1`), schuine streep
(`jo13/2`), punt of komma, én een **kale spatie** (`MO13 1`). Die laatste vorm ontbrak aanvankelijk in
de regels, waardoor `MO13 1` de sleutel `MO131` kreeg en `MO13-1` de sleutel `MO13-1` — twee sleutels
voor hetzelfde team. Gevolg: geen alias- en geen exacte match, én `Parse` gaf `null` zodat
`LeeftijdNummer`/`TeamNummer` in `dbo.Teams` leeg bleven en ook het kandidatenpad (stap 3 hieronder)
stilviel. Voor de democlub AllStars FC, waarvan élk team deze notatie gebruikt, was daarmee geen enkel
team meer herkenbaar (#766).

## De oplossing: één vertaalpunt, deterministisch

```
vrije tekst uit e-mail
        │
        ▼
  AI-classificatie ......... levert alleen RUWE signalen (teamtekst, datum, intentie)
        │
        ▼
  TeamNaamNormalisatie ..... deterministisch: clubprefix strippen, O→JO, scheidingstekens,
        │                    hoofdletters. GEEN AI. Enige plek met deze regels.
        ▼
  TeamResolver ............. 1. gevalideerde alias   → TeamId  (confidence 1.0)
        │                    2. exacte canonieke match → TeamId  (confidence 1.0)
        │                    3. kandidaten op leeftijd+teamnummer
        │                       └─ precies 1 → TeamId (confidence 0.9)
        │                       └─ meerdere → disambiguatie of onbeslist
        ▼
  TeamId (of expliciet onbeslist — nooit een gok)
```

**Kernprincipe:** AI doet taalinterpretatie, code en database doen identiteit. Bij ambiguïteit
wordt niet gegokt.

### Ambiguïteit is echt, niet theoretisch

Bij één club bestaan tien paren met dezelfde leeftijd én hetzelfde teamnummer, alleen verschillend
in jongens/meisjes — bijvoorbeeld `JO13-1` en `MO13-1`. Een e-mail die alleen "13-1" noemt is dus
aantoonbaar dubbelzinnig. Daarvoor is er één plek die mag kiezen:

`TeamDisambiguationAiService` krijgt een genummerde kandidatenlijst en mag **alleen een index**
teruggeven (forced choice). De keuze wordt daarna in C# gevalideerd tegen die lijst, dus een
gehallucineerd nummer kan nooit tot een verkeerd `TeamId` leiden. Boven acht kandidaten wordt niet
gedisambigueerd: dan is de tekst te vaag en is terugvragen aan de afzender correcter.

## Datamodel

| Tabel | Rol |
|---|---|
| `dbo.Teams` | Eén rij per werkelijk team, gesleuteld op `(ClubCode, TeamnaamGenormaliseerd)`. Gevuld door de nachtelijke sync. Verdwenen teams worden gedeactiveerd, niet verwijderd. |
| `dbo.TeamAliassen` | Uitsluitend schrijfwijzen die **niet** uit de normalisatie volgen: geleerd uit e-mail of handmatig toegevoegd. Status `pending`/`validated`/`rejected` — alleen `validated` wordt vertrouwd. |

De sync schrijft géén aliassen: alle Sportlink-schrijfwijzen van één team normaliseren per definitie
naar dezelfde sleutel, dus een alias-rij zou dupliceren wat `dbo.Teams` al weet.

Een alias die uit AI-disambiguatie komt, krijgt status `pending` en wordt dus **niet** vertrouwd
totdat een coördinator hem goedkeurt (Beheer → Teamaliassen). Zo kan een foutieve keuze zich niet
zelfversterken.

## Uitrol — geen schakelaar

Er is **geen** instelling om deze laag uit te zetten. Dat is een bewuste keuze: sinds #700 is dit het
enige pad waarlangs een team wordt herkend, en een schakelaar die dat kan uitzetten is dan geen
veiligheidsventiel maar een voetangel — hij zou de teamherkenning stil kunnen uitschakelen zonder dat
er iets kapot lijkt.

Wat er in de plaats is gekomen aan veiligheid:

| Situatie | Gedrag |
|---|---|
| Resolver niet geregistreerd in DI | E-mailverwerking stopt met een foutmelding. Verwerken zonder teamherkenning is erger dan niet verwerken. |
| `dbo.Teams` leeg (bijv. direct na een deploy) | De canonicalisatie wordt eenmalig alsnog uitgevoerd op de al aanwezige `his.teams`-data. Lukt dat niet, dan wordt er niet verwerkt en volgt een expliciete foutmelding. |
| Teamaanduiding niet herleidbaar | Geen gok: de tak handelt af als "team onbekend". |
| Meerdere kandidaten, geen betrouwbare keuze | Geen gok: de vraag wordt teruggelegd bij de afzender. |

De gereedheidscheck staat bewust in fase 2 van de verwerking, ná het laden van `dbo.AppSettings`.
In fase 1 zou hij (a) altijd falen omdat de clubCode dan nog niet geladen is, en (b) bij élke poll de
database openen — wat een Azure SQL Serverless-database 24/7 wakker houdt en het gratis vCore-budget
verbruikt.

Logregels om op te zoeken: `TEAMRESOLUTIE` (per bericht: teamId, bron, confidence) en
`TEAMS CANONICALISATIE` (per sync: aantal teams, gekoppelde schrijfwijzen, niet-herleidbare namen).

**AllStars FC-uitzondering (#756):** de democlub heeft geen eigen Sportlink-sync — zijn
`his.teams`/`his.matches`-rijen komen uit de PostDeployment-demodata-seed. `SportlinkSyncPipeline`
roept `TeamCanonicalisatieService.RefreshAsync` daarom na elke echte sync **twee keer** aan: één keer
voor de geconfigureerde `clubCode`, één keer expliciet voor `"ALLSTARS"`. Zonder die tweede aanroep
blijft `dbo.Teams` voor de democlub permanent leeg en toont elke UI die daaruit leest (bijv. de
teamdropdown bij Voorkeurstijden) nul teams voor AllStars FC.

## De genormaliseerde sleutel is opgeslagen data — wijzigen vraagt een migratie (#766)

`dbo.Teams.TeamnaamGenormaliseerd` en `dbo.TeamAliassen.RuweTekstGenormaliseerd` zijn **persistente**
kolommen met een waarde die door C#-code berekend is. Elke wijziging in `TeamNaamNormalisatie` is
daarmee ook een datamigratie.

Zonder migratiestap gaat het als volgt mis: de MERGE in `UpsertTeamAsync` matcht op
`(ClubCode, TeamnaamGenormaliseerd)`. Met een nieuwe sleutel vindt hij de bestaande rij niet meer, valt
in de INSERT-tak en botst op `UQ_Teams_Club_Teamnaam` — de teamnaam bestaat immers al. Die fout wordt
per team gevangen en gelogd, terwijl `DeactiveerOntbrekendeTeamsAsync` de oude rij op `IsActief = 0`
zet. Netto: **de teams verdwijnen uit `dbo.Teams` en komen ook bij volgende syncs nooit terug**, want de
unique constraint blijft falen.

`TeamCanonicalisatieService.MigreerSleuteldriftAsync` lost dit generiek op, vóór de upserts:

1. Herbereken per bestaande rij de sleutel uit de al opgeslagen `Teamnaam` — in C#, dus zonder de
   normalisatieregels in T-SQL na te bouwen.
2. Wijkt die af, dan worden sleutel **én** `LeeftijdNummer`/`TeamNummer` in-place bijgewerkt. Die twee
   komen uit dezelfde ontleding: een sleutel zonder streepje leverde geen componenten op, en dan geeft
   `FindKandidatenAsync` nul kandidaten. Alleen de sleutel repareren herstelt de exacte match maar laat
   de ambiguïteitsafhandeling ("13-1") stilliggen tot de volgende volledige canonicalisatie.
3. Vallen twee bestaande rijen op dezelfde nieuwe sleutel, dan waren het twee schrijfwijzen van
   hetzelfde fysieke team. De rij die de sleutel al had (of anders de oudste) blijft; de aliassen van de
   ander — inclusief geleerde en handmatig toegevoegde — worden omgehangen en de dubbele rij wordt
   verwijderd. Verwijderen mag hier omdat de verwijzingen net omgehangen zijn; laten staan zou juist
   schadelijk zijn, want de rij houdt de teamnaam bezet en blokkeert daarmee de upsert van de winnaar.
4. Aliassleutels worden eveneens herberekend. Voor `Bron = 'Sync'` doet de canonicalisatie dat verderop
   toch al; geleerde en handmatig toegevoegde aliassen zouden anders alleen nog op de exacte ruwe tekst
   vindbaar zijn.

De stap is idempotent: zonder drift kost hij twee SELECTs en verandert niets. Hij loopt mee in elke
sync (voor de eigen club én voor AllStars FC), en daarnaast in `TeamlijstGereedheid` — dus ook vóór
e-mailverwerking en vóór een dry-run in de e-mailtester. Daardoor herstelt een productiedatabase zich
ook wanneer de nachtelijke sync uit staat (`syncEnabled = 0`) of nog niet gelopen heeft sinds de deploy,
zonder handmatig migratiescript.

Logregels om op te zoeken: `sleutel gemigreerd voor` en `dubbele schrijfwijze ... samengevoegd met`.

## Waarom exacte matching in plaats van LIKE

De schrijfwijze verschilt per bron: `his.matches` gebruikt "[club] JO10-1" (mét J), de bondsrijen in
`his.teams` "[club] O10-1" (zonder), en de e-mailclassificatie levert weer een derde vorm. Die
normalisatie leeft in C#, niet in T-SQL.

Daarom registreert de canonicalisatie élke schrijfwijze die in de brondata voorkomt als gevalideerde
alias bij het bijbehorende team. Het zoeken van een wedstrijd wordt daarmee een **exacte** vergelijking
op de ruwe naam. Dat is niet alleen sneller en indexeerbaar, het sluit ook de klasse fouten uit waarbij
"JO13-1" ook "JO13-10" raakt.

Schrijfwijzen die niet herleidbaar zijn, zijn in de praktijk geen clubteams — losse
toernooi-inschrijvingen en tegenstanders in oefenwedstrijden. Die krijgen bewust geen alias en worden
alleen geteld in de logregel, zodat een onverwachte stijging opvalt zonder de review-lijst te vervuilen.

## Bestanden

| Bestand | Verantwoordelijkheid |
|---|---|
| `Planner.Shared/TeamNaamNormalisatie.cs` | Enige plek met normalisatieregels. Puur, geen DB, geen AI. **Tier-onafhankelijk** — beide databasebomen gebruiken exact deze klasse (verhuisd hierheen bij #889; stond tot dan in `FunctionApp/TeamResolution/`). |
| `FunctionApp/TeamResolution/TeamResolver.cs` | Resolutievolgorde; kiest nooit zelf bij ambiguïteit. |
| `FunctionApp/TeamResolution/TeamCandidateRepository.cs` | Lookups tegen `dbo.Teams`/`dbo.TeamAliassen`, altijd op ClubCode. |
| `FunctionApp/TeamResolution/TeamDisambiguationAiService.cs` | Forced-choice keuze uit een korte kandidatenlijst. |
| `FunctionApp/TeamResolution/TeamAliasLearningService.cs` | Legt nieuwe schrijfwijzen vast als `pending`. |
| `FunctionApp/TeamResolution/TeamCanonicalisatieService.cs` | Vult `dbo.Teams` na de sync; ontdubbelt de twee notaties; migreert opgeslagen sleutels na een normalisatiewijziging. |
| `FunctionApp/TeamResolution/TeamlijstGereedheid.cs` | Vult de teamlijst alsnog als die leeg is en migreert sleuteldrift als die wél gevuld is; faalt hard en zichtbaar als dat niet lukt. |

**Postgres-tier (epic #815).** De datatoegangslaag bestaat als parallelle boom onder
`FunctionApp.Postgres/TeamResolution/` — `TeamCandidateRepository`, `TeamAliasLearningService`
(#889, deel 1) en `TeamCanonicalisatieService` (#889, deel 2), tegen `public.teams`/
`public.teamaliassen`. De normalisatielogica zelf is **niet** gedupliceerd: beide bomen gebruiken
`Planner.Shared.TeamNaamNormalisatie`. `TeamResolver`, `TeamDisambiguationAiService` en
`TeamlijstGereedheid` zijn daar (nog) niet vertaald — zie
`docs/ARCHITECTUUR-DATABASE-TIERS.md` §28.

## Regels bij wijzigingen

1. **Normalisatieregels horen uitsluitend in `TeamNaamNormalisatie`.** Een nieuwe regex elders in de
   codebase is een architectuurschending — dat is precies het probleem dat deze laag oplost.
2. **Voeg nooit een regel toe die een ontbrekend geslacht-prefix raadt.** "13-1" is dubbelzinnig; dat
   hoort in de kandidaten-/disambiguatiestap, niet in een string-functie.
3. **De disambiguator mag alleen kiezen uit aangeboden kandidaten**, en de keuze wordt altijd in C#
   gevalideerd. Nooit vrije generatie van een teamnaam.
4. **Nieuwe naamvormen eerst tegen echte data verifiëren** (`stg.teams` / `his.teams`) vóór je de
   normalisatie aanpast. De vormen in dit document zijn zo gevonden, niet bedacht.
5. **Test met de clubprefix als parameter**, nooit hardcoded: de prefix komt uit `dbo.AppSettings`.

## Postgres-collatie-kanttekening (#820)

`Database/SportlinkSqlDb.sqlproj` zet het volledige SQL Server-schema op de case-insensitive
default-collatie (`ModelCollation = 1033, CI`). `TeamCandidateRepository.cs` leunde daar
stilzwijgend op: een kale `[TeamnaamGenormaliseerd] = @sleutel`-vergelijking "werkte" alleen omdat
de kolom-collatie hoofdlettergevoeligheid al wegfiltert. Postgres' default-collatie is
case-sensitief — diezelfde vergelijking matcht daar stilzwijgend nul rijen zodra de opgeslagen
casing afwijkt van de vers berekende sleutel (geen foutmelding, gewoon "team niet gevonden").

Alle drie de lookups in `TeamCandidateRepository.cs` (`TeamnaamGenormaliseerd`, `RuweTekst`,
`RuweTekstGenormaliseerd`) vergelijken daarom expliciet via `UPPER(...)` op beide kanten — portable
naar elke tier, geen afhankelijkheid van een onzichtbare schema-eigenschap.
`TeamCandidateRepositoryCollationTests` bewaakt dit tekstueel, net als `VeldResolutieDriftTests`
voor de veldresolutie.

**`RuweTekst` is bewust ook ge-upper't.** De intentie van die tak is "exacte bronschrijfwijze",
maar onder de huidige CI-collatie is die vergelijking vandaag al feitelijk hoofdletterongevoelig.
`UPPER()` behoudt het waargenomen gedrag; een bewust hoofdlettergevoelige variant zou een
gedragswijziging zijn (mogelijk minder validated-alias-treffers) en is niet gekozen.

**Bijgewerkt (#820, vervolgronde):** de Postgres-tier heeft inmiddels wél een teamherkenning-
datalaag (`FunctionApp.Postgres/TeamResolution/TeamCandidateRepository.cs`/
`TeamAliasLearningService.cs`, #889) tegen `public.teams`/`public.teamaliassen`. Daar was dit
risico geen theoretisch vervolgpunt meer maar een levende bug: Postgres' default-collatie is
case-sensitief, dus zonder de `UPPER()`-wrap gaf `FindExactTeamAsync`/`FindValidatedAliasAsync`
stilzwijgend nul resultaten bij afwijkende opgeslagen casing — en de kale
`UNIQUE(clubcode, teamnaam)`/`UNIQUE(clubcode, ruwetekst)`-constraints uit de eerste Postgres-
migratie (#887) lieten een casing-only-duplicaat toe die SQL Server vandaag al zou weigeren.

`Database.Postgres/migrations/007_teams_collation_fix.sql` vervangt die kale `UNIQUE`-constraints
door expression-based unique indexes op `upper(...)` (`ux_teams_club_teamnaam_upper`,
`ux_teams_club_teamnaamgenormaliseerd_upper`, `ux_teamaliassen_club_ruwetekst_upper`). De Postgres-
tier's `TeamCandidateRepository`/`TeamAliasLearningService` wrappen dezelfde drie vergelijkingen nu
ook expliciet in `UPPER(...)` — inclusief `TeamAliasLearningService`'s `ON CONFLICT (clubcode,
upper(ruwetekst))`, dat moest meeveranderen omdat de conflict-doeltabel exact moet matchen met de
onderliggende (nu expression-based) unique index. Empirisch geverifieerd tegen een wegwerp-
Postgres-16-container (2026-08-31): een casing-only-duplicaat wordt geweigerd, `FindExactTeamAsync`/
`FindValidatedAliasAsync` vinden het team ondanks afwijkende opgeslagen casing, en een herhaalde
`LegVastAsync`-aanroep met andere casing verhoogt de teller op de bestaande rij in plaats van een
duplicaat aan te maken.

**Nog niet gedaan — vereist expliciete eigenaargoedkeuring, niet autonoom uit te voeren:** de
audit/replay tegen échte, historische productiedata (`his.teams`/`stg.teams`/`dbo.Teams`) om te
bepalen of er vandaag al casing-drift verborgen zit achter SQL Server's CI-collatie. Dat vereist
toegang tot echte clubdata — zie issue #820 voor de openstaande status.
