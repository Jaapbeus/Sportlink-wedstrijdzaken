# Codekwaliteit — strikte regels met een exit-code

> Vastgelegd naar aanleiding van **#1248** (thema-logica woordelijk gedupliceerd over twee
> database-tiers) en **#1252** (de platformafhankelijke bug die daardoor maandenlang onzichtbaar
> bleef). Dit document is de bron voor alle codekwaliteitsregels in dit project. CLAUDE.md vat ze
> samen en verwijst hierheen; AGENTS.md wordt uit CLAUDE.md afgeleid.

---

## 1. Wat er misging

`FunctionApp/Admin/AdminThemeFunction.cs` en `FunctionApp.Postgres/Admin/AdminThemeFunction.cs`
bevatten dezelfde regex-set voor kleur-, favicon- en logo-extractie, dezelfde hexvalidatie, dezelfde
SSRF-allowlist-orkestratie en dezelfde standaardkleuren. Het enige echte verschil was de
databaseclient en de kolomnaam-casing.

De duplicatie ontstond op 2026-08-30 in de Postgres-poort (#887, PR #897), drie maanden nadat de
oorspronkelijke thema-functie was gebouwd (#325). In het nieuwe bestand stond vanaf de eerste
commit letterlijk:

> *"De HTML-scraping/SSRF-allowlist-logica in `Extract` is ongewijzigd gekopieerd — die is
> databasetier-onafhankelijk."*

**De kopie was dus bewust, gedocumenteerd en zichtbaar voor iedere reviewer.** Ze werd niet
tegengehouden. Dat is de eigenlijke vraag die dit document beantwoordt: niet "hoe kon iemand dit
missen", maar "waarom was er geen grond om het af te wijzen".

### Wat het kostte

De prijs kwam bij #1252 binnen. `ResolveUrl` gebruikte `Uri.TryCreate(url, UriKind.Absolute, …)`
als test voor "is dit een absolute URL". Op Unix parseert `"/favicon.ico"` daarmee **succesvol**,
als `file:`-URI; op Windows niet. De relatieve tak was daardoor onbereikbaar, en élke
root-relatieve verwijzing gaf `null`. De Function App draait op een Linux Consumption Plan en de
ontwikkelmachine is macOS — beide Unix. Favicon- en logo-extractie heeft dus **nooit gewerkt**,
vanaf #325 in mei. Zonder foutmelding: de beheerder zag "geen logo gevonden", niet te
onderscheiden van een site zonder logo.

De bug zat in beide kopieën, en in geen van beide een test.

---

## 2. Root cause — vijf lagen

**1. De tier-regel werd breder gelezen dan hij is.** CLAUDE.md zegt: *"Eén tier per
club-deployment, nooit een gedeelde C#-providerabstractie. […] nooit een runtime-switch in gedeelde
code."* Dat verbiedt één interface met `SqlConnection`/`NpgsqlConnection` achter een schakelaar. Het
zegt niets over pure, tier-onafhankelijke logica — `ARCHITECTUUR-DATABASE-TIERS.md` §2 staat het
delen daarvan expliciet toe. In de praktijk werd de regel toegepast op het *hele bestand*.

**2. De opdracht liet de vraag niet toe.** De scope van #887 was "vertaal de zestien
admin-endpointparen 1-op-1 naar de Postgres-tier". Binnen die formulering is "welk deel hiervan
hoort eigenlijk in `Planner.Shared`?" geen deelvraag maar scope-uitbreiding. Een agent die zijn
opdracht netjes uitvoert, dupliceert.

**3. Niets mat het.** Er bestond geen enkele controle op duplicatie, bestandsgrootte,
methodelengte, complexiteit of tier-drift. De CI bewaakte de databasekant wél
(`check-postgres-table-coverage.sh` en twee zusterscripts), de C#-kant niet.

**4. Geen test, dus geen signaal.** De thema-logica had nul tests tot #1248 er zelf tests bij
schreef. Een fout die stilzwijgend `null` teruggeeft, meldt zich niet.

**5. En daaronder, de eigenlijke oorzaak: proza is geen controle.** Dit patroon is in dit project
al vastgelegd voor security-regressies. Elke regel die hier standhoudt, heeft een exit-code. Elke
regel die alleen in CLAUDE.md staat, wordt gevolgd zolang het uitkomt — en dat is bij een
1-op-1-poort precies niet.

### Dit was de vierde keer

| Issue | Wat er gedupliceerd was | Hoe het aan het licht kwam |
|---|---|---|
| #692 → #889 | Teamnaam-normalisatie | Bij de Postgres-poort; daarna naar `Planner.Shared` |
| #1130 | `SsrfProtection`, `ReplyPolicy`, feedbackkern | Externe reviewronde (#1107), weken later |
| #1122 | Zes kopieën van de Sportlink-toggle-check, drie van de statusvertaling | Reviewronde na epic #986 |
| #1248 | Volledige thema-logica | Toevallig, tijdens onderzoek voor epic #1249 |

Vier keer hetzelfde mechanisme: **kopiëren bij de poort, centraliseren bij een latere review.**
Dat is geen reeks incidenten meer, dat is het normale gedrag van het systeem.

### Waarom dit bij AI-ondersteund ontwikkelen harder groeit

Kopiëren is voor een agent goedkoper dan hergebruiken: het vraagt geen begrip van de bestaande
abstractie en het risico op regressie in de bestaande tier is nul. GitClear mat over 211 miljoen
gewijzigde regels dat blokken met vijf of meer gedupliceerde regels in 2024 met een factor acht
toenamen, dat het aandeel gekloonde regels steeg van 8,3% (2021) naar 12,3% (2024), en dat het
aandeel verplaatste/geherstructureerde regels in dezelfde periode daalde van 25% naar onder de 10%
([bron](https://www.gitclear.com/ai_assistant_code_quality_2025_research)). De richting van dit
project is dus de richting van het gemiddelde — tenzij er iets tegenin duwt.

---

## 3. Nulmeting (develop `03865a9`, 2026-09-19 — na de merges van #1248 en #1254)

| Metriek | Waarde |
|---|---|
| Woordelijk identieke betekenisvolle regels tussen de twee tierbomen (77 paren) | **4.641** |
| jscpd-duplicatie over alle C#-broncode | **15,4%** (376 clones) |
| Regels logica in `@code`-blokken van Blazor-pagina's | **1.715** over 14 pagina's |
| Blazor-pagina's mét code-behind | 4 van 18 |
| `.cs`-bestanden boven 500 regels | 26 (waarvan 4 boven 800) |
| `.editorconfig` / `Directory.Build.props` / analyzers | afwezig — alleen `<Nullable>enable</Nullable>` |
| Architectuurtestproject | afwezig |
| Harde regels in CLAUDE.md zonder enige geautomatiseerde controle | **21** |
| Verschil CLAUDE.md ↔ AGENTS.md (moesten tweelingen zijn) | **280 regels, 9 ontbrekende secties** |

Die laatste regel verdient aparte vermelding. AGENTS.md — het regelboek dat de tweede reviewer van
dit project leest — miste onder meer *"Multi-tier databasestrategie"*, *"Teamnaam → TeamId: één
vertaalpunt"* en *"Uitgaande integraties — altijd via EgressGuard"*. De twee regels die duplicatie
moeten tegenhouden en één beveiligingsregel. De reviewer die #1248 had kunnen tegenhouden, kende
de regel niet.

---

## 4. De regels

Elke regel hieronder heeft een guard, of staat expliciet als niet-afdwingbaar gemarkeerd. Er is
geen derde categorie: een regel zonder controle en zonder die markering hoort hier niet.

### Regel 1 — Tier-onafhankelijke logica staat in `Planner.Shared`

Een bestand in `FunctionApp/` of `FunctionApp.Postgres/` bevat uitsluitend: query's,
parameterbinding, en de vertaling van een kernstatus naar een HTTP-respons. Al het andere —
validatie, regex, formattering, businessregels, orkestratie van een externe aanroep — hoort in
`Planner.Shared`.

Precedenten: `ThemeCore` (#1248), `FeedbackCore` + `SsrfProtection` (#1130),
`TeamNaamNormalisatie` (#889), `SportlinkEndpointSupport` (#1122).

De vraag bij een tier-poort is nooit "vertaal ik dit bestand?" maar **"welk deel hiervan gaat over
de database, en welk deel niet?"** Alleen het eerste deel wordt vertaald.

*Guard: `scripts/ci/check-tier-duplicatie.sh` — ratchet op het totaal aantal woordelijk identieke
betekenisvolle regels.*

### Regel 2 — Duplicatie mag nooit stijgen

Het gemeten duplicatiegetal is een plafond, geen doel. Het staat in
`scripts/ci/codekwaliteit-plafonds.txt` en mag alleen omlaag. Verhogen kan, maar dan in een PR die
uitlegt waarom — de afweging wordt een diff die iemand goedkeurt.

Bestaande duplicatie wordt niet in één ronde opgeruimd: dat zou riskanter zijn dan het probleem.
De ratchet zorgt dat ze alleen nog kleiner wordt.

*Guard: idem regel 1.*

### Regel 3 — Geen logica in Blazor-pagina's

Elke `.razor` onder `BlazorAdmin/Pages/` met C#-logica heeft een code-behind: `<Pagina>.razor.cs`,
`public partial class`, `[Inject]` in plaats van `@inject`. Een pagina met een code-behind mag
daarnaast géén `@code`-blok hebben.

Reden is testbaarheid: `BlazorAdmin.Tests` kan een partial class instantiëren, een `@code`-blok
niet. Dat 1.715 regels logica in pagina's staan, verklaart waarom dat testproject met 14 tests het
kleinste van de vijf is.

Dit is een eigen architectuurkeuze, geen Microsoft-voorschrift: Microsoft beschrijft beide vormen
als ondersteund en noemt geen grens
([bron](https://learn.microsoft.com/aspnet/core/blazor/components/#partial-class-support)).
Precedent in dit project: de vier Sportlink-extensiepagina's (#1122).

*Guard: `scripts/ci/check-blazor-codebehind.sh` — hard op dubbele logica, ratchet op het totaal.*

### Regel 4 — Platformafhankelijke valkuilen zijn verboden, tenzij gemotiveerd

Vier patronen die in dit project aantoonbaar stille fouten hebben opgeleverd:

| Patroon | Waarom | Incident |
|---|---|---|
| `UriKind.Absolute` als "is dit een URL"-test | Op Unix parseert `"/pad"` succesvol als `file:`-URI | #1252 |
| `DateTime.Now` waar `UtcNow` hoort | Lokale tijd opgeslagen, als UTC gemarkeerd, nog eens omgerekend | #246 |
| `GETDATE()` waar `GETUTCDATE()` hoort | Dezelfde fout, aan de databasekant | #246 |
| `<input type="time">` in plaats van `<TimeInput>` | "830" en "8:30" worden niet genormaliseerd | — |

Een uitzondering staat in `scripts/ci/codekwaliteit-valkuilen-allowlist.txt`, per pad **en met
reden**. Een regel zonder reden laat de guard falen: een uitzondering die niemand kan beoordelen,
groeit vanzelf uit tot gewoonte.

*Guard: `scripts/ci/check-codekwaliteit-valkuilen.sh`.*

### Regel 5 — Eén regelboek, afgeleid in plaats van gekopieerd

CLAUDE.md is de bron. AGENTS.md wordt eruit gegenereerd met
`python3 scripts/ci/genereer-agents-md.py --schrijf` en wordt nooit met de hand bewerkt — zelfde
patroon als `openapi.json` uit `openapi.yaml`.

Dit is regel 1, toegepast op de documentatie zelf. Twee documenten die hetzelfde moeten zeggen en
met de hand worden bijgehouden, lopen uiteen; dat is hier ook gebeurd, met negen ontbrekende regels
als gevolg.

*Guard: `scripts/ci/genereer-agents-md.py` (zonder `--schrijf`).*

### Regel 6 — Een nieuwe regel krijgt een guard, of wordt als onbewaakt gemarkeerd

Dit is de regel die de andere zeven overeind houdt, en de directe les van dit onderzoek. Wie een
harde regel toevoegt aan CLAUDE.md of aan dit document, doet één van twee dingen:

1. schrijft er een guard bij en zet die in het register hieronder; of
2. zet hem in het register met `handmatig` en één zin over waarom een controle niet kan.

Wat niet mag, is een regel zonder allebei. Dat is hoe er 21 onbewaakte regels ontstonden.

*Guard: `scripts/ci/check-regelregister.sh` — controleert dat elk genoemd script bestaat,
uitvoerbaar is en daadwerkelijk in een workflow wordt aangeroepen, en dat er geen guard bestaat die
niet in het register staat.*

### Regel 7 — Een productiebestand blijft onder de 500 regels

### Regel 8 — Een methode blijft onder de 80 regels

Beide zijn ratchets op een **aantal**, niet op een grens per bestand: geteld wordt hoeveel
bestanden en methodes er boven zitten, en dat aantal mag niet stijgen. Bestaande code mag dus
blijven; nieuwe code blijft eronder, of ruimt iets anders op.

Dat is een bewuste keuze, omdat er geen gezaghebbende drempel bestaat om naar te wijzen. Google's
reviewrichtlijnen noemen expliciet géén bestandsgrens en stellen dat "smallness" geen simpele
functie van regelaantal is
([bron](https://github.com/google/eng-practices/blob/master/review/developer/small-cls.md)).
SonarSource hanteert cognitieve complexiteit 15 per functie; Microsofts CA1502 staat op
cyclomatische complexiteit 25
([bron](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1502)). Drie
serieuze bronnen, drie andere antwoorden. Een zelfgekozen harde grens zou zevenentwintig bestaande
bestanden in één klap illegaal maken, en zo'n guard wordt uitgezet.

500 en 80 markeren niet "goed", maar "dit wordt moeilijk te lezen en te testen".

**Testbestanden tellen niet mee.** Een testbestand groeit door losse gevallen naast elkaar te
zetten; dat is geen verstrengeling en leest ook bij tweeduizend regels van boven naar beneden. Een
guard die het toevoegen van tests bestraft, werkt averechts.

Opvallend bij de nulmeting: van de acht grootste bestanden zijn er zes de twee helften van drie
tier-paren, en van de zes langste methodes zijn het er ook zes. Regel 7 en 8 wijzen dus naar
dezelfde schuld als regel 1, vanuit een andere hoek.

*Guard: `scripts/ci/check-bestandsgrootte.sh`.*

---

## 5. Register

<!-- REGELREGISTER-BEGIN -->

| Regel | Afgedwongen door | Draait in |
|---|---|---|
| 1, 2 — tier-duplicatie stijgt niet | `scripts/ci/check-tier-duplicatie.sh` | `build.yml` |
| 3 — geen logica in Blazor-pagina's | `scripts/ci/check-blazor-codebehind.sh` | `build.yml` |
| 4 — platformafhankelijke valkuilen | `scripts/ci/check-codekwaliteit-valkuilen.sh` | `build.yml` |
| 5 — AGENTS.md afgeleid uit CLAUDE.md | `scripts/ci/genereer-agents-md.py` | `build.yml` |
| 6 — elke regel heeft een guard | `scripts/ci/check-regelregister.sh` | `build.yml` |
| 7, 8 — bestandsgrootte en methodelengte stijgen niet | `scripts/ci/check-bestandsgrootte.sh` | `build.yml` |
| Alle acht — de guards worden zelf getest | `scripts/ci/check-codekwaliteit.test.sh` | `build.yml` |

De guards die al bestonden staan hier ook in. Het register is daarmee de volledige lijst: een
guard die er niet in staat, laat `check-regelregister.sh` falen — zodat een controle niet stilletjes
uit een workflow kan verdwijnen zonder dat iemand het merkt.

| Regel | Afgedwongen door | Draait in |
|---|---|---|
| Padverwijzingen exact in casing (#825) | `scripts/ci/check-path-casing.sh` | `build.yml` |
| Postgres-identifiers lowercase snake_case | `scripts/ci/check-postgres-identifier-casing.sh` | `build.yml` |
| Tabellen gedekt in beide tierbomen | `scripts/ci/check-postgres-table-coverage.sh` | `build.yml` |
| Kolommen gedekt in beide tierbomen | `scripts/ci/check-postgres-column-coverage.sh` | `build.yml` |
| Procedures/views gedekt in beide tierbomen | `scripts/ci/check-postgres-procedure-view-coverage.sh` | `build.yml` |
| RLS aan op elke tabel (#1198, #1220) | `scripts/ci/check-rls-enabled.sh` | `build.yml` |
| Supabase-lints (#1220) | `scripts/ci/check-splinter-lints.sh` | `build.yml` |
| Thema-CSS-variabelen consistent (#1255) | `scripts/ci/check-theme-variables.sh` | `build.yml` |

<!-- REGELREGISTER-EINDE -->

---

## 6. Wat bewust (nog) niet wordt afgedwongen

Eerlijk vermeld, zodat niemand denkt dat het gedekt is.

| Onderwerp | Waarom niet | Vervolg |
|---|---|---|
| Roslyn-maintainability-analyzers (CA1502/1505/1506) | Staan niet standaard aan, ook niet bij `AnalysisMode=All` ([bron](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview)). Ze aanzetten vóór het aantal waarschuwingen bekend is, maakt de eerstvolgende PR rood. | Issue #1263 |
| jscpd in CI | Zou duplicatie binnen één tier ook vangen (de tier-guard doet dat niet). Vraagt een npm-afhankelijkheid in CI; eerst het drempelgedrag vaststellen. | Issue #1263 |
| `GETDATE()` in de bestaande SQL Server-bomen | 34 treffers in `Database/`, `FunctionApp/setup/` en `scripts/migrations/`; een migratie wijzig je nooit achteraf. Staan op de allowlist. | Issue #1263 |
| Testdekking per productiemap | `Database.Postgres.Cli/`, `MigrationTools/` en `Tools/` hebben geen testproject. | Issue #1263 |

---

## 7. Hoe je hiermee werkt

```bash
# Alle codekwaliteitsguards lokaal, zelfde volgorde als CI:
bash scripts/ci/check-tier-duplicatie.sh
bash scripts/ci/check-blazor-codebehind.sh
bash scripts/ci/check-codekwaliteit-valkuilen.sh
bash scripts/ci/check-bestandsgrootte.sh
bash scripts/ci/check-regelregister.sh
python3 scripts/ci/genereer-agents-md.py

# CLAUDE.md gewijzigd? Regenereer AGENTS.md:
python3 scripts/ci/genereer-agents-md.py --schrijf
```

Een guard die faalt omdat je iets hebt verbeterd, zegt welk getal in
`scripts/ci/codekwaliteit-plafonds.txt` moet. Neem dat over in dezelfde PR — winst die niet wordt
vastgezet, lekt binnen een paar PR's weg.

**Over de nultolerantie naar boven.** Bij een getal van vier cijfers verschuift de tier-meting soms
een of twee regels door toeval: twee bestanden krijgen onafhankelijk van elkaar een identieke
regel. Dat is tijdens het invoeren zelf gebeurd — de merge van #1254 haalde 73 gedupliceerde regels
uit het thema-paar en bracht er elders netto 2 terug. Het antwoord daarop is het plafond opnieuw
vastleggen, met de reden erbij, en **niet** een marge naar boven inbouwen. Zo'n marge is precies de
ruimte waarin echte groei ongemerkt past: vijf PR's van elk twee regels zijn samen een nieuw
gekopieerd blok, en geen van vijf zou zijn opgevallen.
