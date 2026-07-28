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
| `FunctionApp/TeamResolution/TeamNaamNormalisatie.cs` | Enige plek met normalisatieregels. Puur, geen DB, geen AI. |
| `FunctionApp/TeamResolution/TeamResolver.cs` | Resolutievolgorde; kiest nooit zelf bij ambiguïteit. |
| `FunctionApp/TeamResolution/TeamCandidateRepository.cs` | Lookups tegen `dbo.Teams`/`dbo.TeamAliassen`, altijd op ClubCode. |
| `FunctionApp/TeamResolution/TeamDisambiguationAiService.cs` | Forced-choice keuze uit een korte kandidatenlijst. |
| `FunctionApp/TeamResolution/TeamAliasLearningService.cs` | Legt nieuwe schrijfwijzen vast als `pending`. |
| `FunctionApp/TeamResolution/TeamCanonicalisatieService.cs` | Vult `dbo.Teams` na de sync; ontdubbelt de twee notaties. |
| `FunctionApp/TeamResolution/TeamlijstGereedheid.cs` | Vult de teamlijst alsnog als die leeg is; faalt hard en zichtbaar als dat niet lukt. |

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
