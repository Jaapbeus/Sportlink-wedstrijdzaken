# Codekwaliteit — strikte regels met een exit-code

> Vastgelegd naar aanleiding van **#1248** (thema-logica woordelijk gedupliceerd over twee
> database-tiers) en **#1252** (de platformafhankelijke bug die daardoor maandenlang onzichtbaar
> bleef). Dit document is de bron voor alle codekwaliteitsregels in dit project. AGENTS.md vat ze
> samen en verwijst hierheen; AGENTS.md is de enige bron van de agentinstructies (#1579).

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
root-relatieve verwijzing gaf `null`. De Function App draaide toen op een Linux Consumption Plan (nu Linux Flex Consumption) en de
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

> **Stand sindsdien.** Deze tabel is een nulmeting op een genoemde commit en wordt niet
> bijgewerkt — dan zou hij geen nulmeting meer zijn. Wat er sindsdien is verschoven, staat hier:
>
> | Metriek | Nulmeting | Nu | Door |
> |---|---|---|---|
> | Regels logica in `@code`-blokken | 1.715 over 14 pagina's | **1.559** over 13 pagina's | #1270 (`Thema.razor`) |
> | Blazor-pagina's mét code-behind | 4 van 18 | **5 van 18** | #1270 |
>
> Het plafond in `scripts/ci/codekwaliteit-plafonds.txt` volgt de kolom "Nu"; de ratchet is de
> plek waar de actuele waarde hoort te staan, niet dit hoofdstuk.

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

**Uitzondering, met een eigen project: endpoint-orkestratie (#1271).** Niet alle tier-duplicatie is
databasetoegang. De routeparameter parsen, de client uit DI halen, de gedeelde kern aanroepen en
het resultaat naar `IActionResult` vertalen, leunt op ASP.NET Core en de Azure Functions Worker —
`Planner.Shared` blijft daarom bewust framework-vrij (zelfde grens als bij `ThemeCore`/
`FeedbackCore`). Voor precies dát soort logica bestaat sinds #1271 `Planner.Endpoints`: een tweede
gedeelde laag, met dezelfde discipline als `Planner.Shared`, maar wél met die afhankelijkheid.
Eerste precedent: `SportlinkEndpointSupportCore` (`Planner.Endpoints/Sportlink/`) — de twee
tier-`SportlinkEndpointSupport.cs`-bestanden zijn er nu een dun omhulsel om, met tier-specifieke
stukken (instellingenlezer, `EgressGuard`, auth-keten) als delegate. Een nieuwe klasse met dezelfde
soort orkestratie hoort in `Planner.Endpoints`, niet in `Planner.Shared` en niet nogmaals per tier.

*Guard: `scripts/ci/check-tier-duplicatie.sh` — ratchet op het totaal aantal woordelijk identieke
betekenisvolle regels.*

### Regel 2 — Duplicatie mag nooit stijgen

Het gemeten duplicatiegetal is een plafond, geen doel. Het staat in
`scripts/ci/codekwaliteit-plafonds.txt` en mag alleen omlaag. Verhogen kan, maar dan in een PR die
uitlegt waarom — de afweging wordt een diff die iemand goedkeurt.

Bestaande duplicatie wordt niet in één ronde opgeruimd: dat zou riskanter zijn dan het probleem.
De ratchet zorgt dat ze alleen nog kleiner wordt.

*Guard: idem regel 1, plus `scripts/ci/check-interne-duplicatie.sh` (#1263) voor duplicatie
**binnen** één boom — twee identieke methodes in hetzelfde bestand, of in twee bestanden binnen
dezelfde tier, komen niet voor in een tier-paar en dus niet in `check-tier-duplicatie.sh`. Meet
met jscpd op productiecode (geen testprojecten, zelfde reden als regel 7/8), met `FunctionApp/`
volledig uitgesloten zodat cross-tier duplicatie niet dubbel wordt geteld door twee guards
tegelijk.*

### Regel 3 — Geen logica in Blazor-pagina's

Elke `.razor` onder `BlazorAdmin/Pages/` met C#-logica heeft een code-behind: `<Pagina>.razor.cs`,
`public partial class`, `[Inject]` in plaats van `@inject`. Een pagina met een code-behind mag
daarnaast géén `@code`-blok hebben.

Reden is testbaarheid: `BlazorAdmin.Tests` kan een partial class instantiëren, een `@code`-blok
niet. Dat bij de nulmeting 1.715 regels logica in pagina's stonden, verklaart waarom dat
testproject met 14 tests het kleinste van de vijf is.

Dit is een eigen architectuurkeuze, geen Microsoft-voorschrift: Microsoft beschrijft beide vormen
als ondersteund en noemt geen grens
([bron](https://learn.microsoft.com/aspnet/core/blazor/components/#partial-class-support)).
Precedent in dit project: de vier Sportlink-extensiepagina's (#1122).

*Guard: `scripts/ci/check-blazor-codebehind.sh` — hard op dubbele logica, ratchet op het totaal.*

Alle twaalf pagina's die deze regel bij de nulmeting nog niet volgden, hebben sinds #1327 een
code-behind; het plafond staat sinds die migratie op 0. Diezelfde migratie maakte ook zichtbaar
wat #1322 al voorspelde: twaalf pagina's herhaalden woordelijk dezelfde clubwissel-lifecycle
(abonneren op `ClubSelectorService.OnChange`, `InvokeAsync`, `StateHasChanged`, afmelden bij
Dispose). Die is bij #1328 gecentraliseerd in `BlazorAdmin/Pages/ClubSelectorPageBase.cs` — een
pagina die op een clubwissel moet reageren, erft daarvan over en overschrijft alleen
`OnClubChangedAsync()`. Sinds #1578 gaat `OnChange` alleen af bij een echte wijziging van de
clubcode (naamsynchronisatie en de menuvlag Sportlink-extensie hebben eigen gebeurtenissen:
`OnClubNameChange`, `OnSportlinkExtensionChange`) en slaat de basis een herlaadronde over als de
club gelijk is aan die waarvoor de pagina is geladen (`ClubWisselTracker`).

### Regel 3b — CSS isolation, geen `<style>`-blok of statische inline style

Presentatie in een Blazor-pagina hoort in `<Pagina>.razor.css` (CSS isolation), niet in een
`<style>`-blok of een `style="..."`-attribuut in de markup. Een dynamische waarde (een berekende
positie, een gekozen kleur) mag inline blijven, maar dan uitsluitend als CSS custom property
(`style="--naam:@expressie;"`) — de daadwerkelijke CSS-eigenschap staat via `var(--naam)` in het
stylesheet.

Reden: dezelfde als regel 3, van de andere kant. Een `.razor.css`-bestand is met normale CSS-tools
te doorzoeken en te hergebruiken; 91 losse `style="..."`-attributen (waarvan sommige de hele
Gantt-tijdlijn van `Dagplanning.razor` positioneerden) zijn dat niet. Precedent:
`Dagplanning.razor.css` bestond al vóór deze regel werd afgedwongen; #1329 breidde dat patroon uit
naar alle pagina's.

Een uitzondering staat in `scripts/ci/blazor-inline-style-allowlist.txt`, per pad+regelnummer en
met reden — nooit een allowlist voor een hele pagina.

*Guard: `scripts/ci/check-blazor-inline-styles.sh` — hard, geen ratchet: een nieuwe overtreding is
altijd een fout, niet een meting die mag groeien.*

### Regel 3c — Gelinkte bronbestanden in BlazorAdmin hangen uitsluitend van de BCL af (#1461)

BlazorAdmin (WASM) refereert bewust niet aan `Planner.Shared`, maar compileert enkele bestanden
als `<Compile Include="../Planner.Shared/..." Link="..." />` (nu: `PlanningConflictRegels.cs`, #1430).
Zo'n bestand draait in de browser, dus: uitsluitend `using System*` (geen ander `Planner.Shared`-type,
geen NuGet) en nooit `RegexOptions.Compiled` (NullReferenceException tijdens renderen, geen
buildfout). Tot #1461 stond dit alleen als commentaar in het bestand zelf — een onbewaakte harde regel.

*Guard: `scripts/ci/check-gelinkte-bronbestanden.sh` — hard, geen ratchet; negatief getest in
`check-codekwaliteit.test.sh`.*

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

**Tijdinvoer gaat altijd via `<TimeInput>`.** Elk invoerveld voor een tijd in Blazor gebruikt het
component `BlazorAdmin/Shared/TimeInput.razor`. Dat roept `TimeHelper.Normalize()`
(`BlazorAdmin/Services/TimeHelper.cs`) aan en accepteert `830`, `0830` en `8:30`, allemaal omgezet naar
`HH:mm`. Een `<input type="time">` of een kale `<input @bind="...Tijd">` voor tijdinvoer is dus een
architectuurschending; een nieuw tijdveld schrijf je als `<TimeInput @bind-Value="..." />`. (Verplaatst
uit `AGENTS.md` bij #1580.)

*Guard: `scripts/ci/check-codekwaliteit-valkuilen.sh`.*


#### De `GETDATE()`-treffers zijn beoordeeld — wat overblijft is geen tijdstempel (#1301)

De drie mapbrede uitzonderingen (`Database/`, `FunctionApp/setup/`, `scripts/migrations/`) zijn
weg. Ze dekten samen honderden bestanden, dus een nieuwe tabel met `DEFAULT GETDATE()` was
stilzwijgend toegestaan — precies het tegenovergestelde van wat een allowlist hoort te doen.

Van de 34 ruwe treffers:

| Categorie | Aantal | Uitkomst |
|---|---:|---|
| Commentaar dat juist **waarschuwt** tegen `GETDATE()` | 5 | Telden al niet mee: de guard slaat `--`-regels over |
| Tijdstempelkolommen in `FunctionApp/setup/*.sql` | 8 | **Gecorrigeerd** naar `GETUTCDATE()`; allowlist-regel verwijderd |
| Seizoenskalender in `sp_UpdateSeasonTable` + zijn kopie in `Script.PostDeployment1.sql` | 20 | Blijft, met reden |
| `CAST(GETDATE() AS DATE)` in het AllStars-demoseedscript | 1 | Blijft, met reden |

**Waarom de seizoenskalender blijft.** `YEAR(GETDATE())` leidt daar af *in welk seizoen we zitten*.
Dat is een kalenderjaar, geen instant, en de afgeleide waarden zijn `DATE`-kolommen — er is niets om
naar UTC om te rekenen. Op Azure SQL is `GETDATE()` bovendien sowieso UTC; alleen een zelf gehoste
server in een andere zone wijkt af, en dan hooguit enkele uren rond een maandgrens midden in het
jaar. De procedure is idempotent en corrigeert zichzelf bij de volgende run.

De UTC-regel gaat over het **opslaan van tijdstempels**. Hem hier toepassen zou een stored procedure
op productie wijzigen voor nul effect — en dat is precies het soort wijziging dat een guard
ongeloofwaardig maakt.

**Wat de acht correcties waard waren.** De drie bestanden in `FunctionApp/setup/` bleken nergens
naar verwezen te worden en spraken bovendien de Docker-regel uit `CLAUDE.md` tegen. Dat is apart
opgepakt als issue #1309; de `GETUTCDATE()`-correctie was juist ongeacht die uitkomst.

> **Afloop (#1309).** De hele map is verwijderd — zeven bestanden, niet drie. De vier andere waren
> even ongebruikt en hoorden bij dezelfde kit: `update-appsettings.sql` documenteerde expliciet dat
> het ná `complete-database-setup.sql` draaide. Twee ervan (`fix-create-procedure.sql`,
> `fix-merge-procedure.sql`) waren losse patches op stored procedures waarvan de gezaghebbende
> definitie in het SSDT-project staat — een derde schemakopie die stil uit de pas kon lopen.

### Regel 5 — Eén regelboek: AGENTS.md is de enige bron

AGENTS.md is de enige bron van de agentinstructies, voor Codex én Claude Code. CLAUDE.md (in de
root en in elke submap met een AGENTS.md) is een stub met alleen de import `@AGENTS.md` en één
verwijzende zin. Claude Code leest een AGENTS.md niet zelf zodra er een CLAUDE.md is, ook niet in
een submap; de stub laadt de inhoud via de import (vastgesteld bij #1579). Skills volgen hetzelfde
principe: `.agents/skills/` is de bron en `.claude/skills/` een identieke kopie die
`scripts/ci/sync-skills.py --schrijf` schrijft.

Dit is regel 1, toegepast op de documentatie zelf. Twee documenten die hetzelfde moeten zeggen en
met de hand worden bijgehouden, lopen uiteen; dat is hier ook gebeurd, met negen ontbrekende regels
als gevolg. Tot #1579 werd AGENTS.md daarom uit CLAUDE.md gegenereerd; met één bron valt er niets
meer af te leiden en is de generator vervallen.

**Geen bekend tweede laadpad (#1580).** Een regel die alleen voor één agent leesbaar is, is een tweede
bron. De guard hanteert daarom een **toelatingsmatrix**, afgeleid uit de actuele documentatie van beide
clients (documentatie, geen runtimegarantie; een kanaal dat nog niet gedocumenteerd of bekend is, kan hij niet
zien). In de root en in elke submap, aanwezig of door git getrackt, weigert hij: `.claude/{rules,commands,
agents,output-styles}`, geneste skillmappen, `.codex/{skills,prompts,agents,rules,commands}` en
`.codex/hooks.json`, `AGENTS.override.md`, een instructiebestand direct onder een metadatamap (`.claude/`,
`.agents/`, `.codex/` — Claude leest `.claude/AGENTS.md`, een Codex-sessie in de root niet, lokaal vastgesteld),
en elke symlink op een instructiepad. Een getrackt `.claude/settings.json` mag alleen `$schema` en
`permissions` (daarbinnen `allow`/`deny`/`ask`) bevatten: een toelatingslijst, dus ook een nieuwe sleutel of
alias (hooks, outputstijl, agent, plugins en marketplaces, `pluginConfigs` dat bronselectie van AGENTS.md
instelt, `claudeMdExcludes`, `autoMemoryDirectory`, `env`, …) faalt. Een `.codex/config.toml` wordt als
TOML-structuur gelezen en mag UITSLUITEND `model`, `model_reasoning_effort`, `approval_policy`, `sandbox_mode`
en de tabel `sandbox_workspace_write` bevatten (ook binnen een profiel): een toelatingslijst, geen
verbodslijst. Dat is bewust ruimer dan een lijst van bekende instructiesleutels: `model_catalog_json` laadt
via een modelcatalogus extra instructievelden en bleef bij een verbodslijst ongezien (Codex-review ronde 2,
lokaal met `codex debug prompt-input` bevestigd). De lezer is een structuurlezer, geen volledige
TOML-validator: dubbele sleutels, ongeldige waarden en tabelconflicten worden niet geweigerd. Voor
`pluginConfigs` in Claude-settings geldt een conservatief projectverbod: volgens de referentie telt het sinds
Claude Code 2.1.207 alleen in user- of managed-settings, dus dit is geen bewezen actuele injectieroute. `CLAUDE.local.md`, `.claude/settings.local.json` en
`.mcp.json` mogen in geen enkele vorm in de git-index staan (gewoon bestand, symlink, index-mode 120000, ook
als verwijderd uit de werkboom). Een kapotte Git-verwijzing is een fout. De volledige lijst staat in de kop van
`check-agent-instructies.py`; dat is de enige plek, zodat ze niet uit de pas kan lopen met wat de guard
afdwingt. Een legitieme toekomstige subagent, command, hook of config vraagt een eigenaarsbesluit én een
wijziging van die guard.

*Waarom git-tracking leidend is.* De doorloop slaat `bin`, `obj`, `packages`, `.venv`, `artifacts` en
`node_modules` over. Een bestand dat git trackt wordt toch gelezen — `git add -f` omzeilt zowel
`.gitignore` als een mapnaam. Symlinks volgt de guard niet: ze breken op Windows zonder Developer
Mode en een gevolgde symlink vraagt cyclus- en padgrenzen; weigeren is eenvoudiger en sluitender.

**Omvang (#1580).** Codex leest standaard maximaal 32768 bytes (`project_doc_max_bytes`) aan
projectinstructies, over de hele keten van de root tot de werkmap samen, en kapt de rest stilzwijgend
af. `scripts/ci/agent-instructies-plafonds.txt` legt per `AGENTS.md` en per keten een ratchet-plafond
vast: groei faalt, winst van meer dan 1024 bytes moet in dezelfde PR in het plafond worden vastgezet,
een bestand zonder plafond of een plafond zonder bestand faalt, en de scheidingstekens tellen mee.
Verhogen is een diff die de eigenaar goedkeurt, geen tolerantie.

*Guards: `scripts/ci/check-agent-instructies.py` (stubs, bron, laadpaden, omvang, codeblokken),
`scripts/ci/check-agent-instructies.test.py` (fixturetests; de verwachtingen over budget, krimpmarge,
verplichte skills en verboden sleutels staan daar uitgeschreven en komen niet uit de guard) en
`scripts/ci/check-agent-instructies.mutaties.py` (geselecteerde mutaties: schakelt per genoemde guardregel
één mutant uit en eist dat de tests dan falen — dat bewijst regressiedetectie voor die mutanten, niet dat
elke regel of elke foutmelding gedekt is) en `scripts/ci/sync-skills.py` (skillkopieën).*

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

#### Aanvulling: de maintainability-analyzers meten dezelfde schuld, maar gezaghebbend (#1300)

Regel 7 en 8 tellen *regels*, omdat er voor die grens geen externe autoriteit bestaat. Voor
**complexiteit** bestaat die wel: Microsoft levert CA1502 (cyclomatische complexiteit), CA1505
(maintainability index) en CA1506 (class coupling) mee in `Microsoft.CodeAnalysis.NetAnalyzers`,
met hun eigen drempels. Sinds #1300 staan die drie aan.

Ze staan standaard uit, **ook bij `<AnalysisMode>All</AnalysisMode>`** — dat is geen vergissing van
ons maar een bewuste keuze van Microsoft, omdat de drempels projectafhankelijk zijn. Aanzetten
gebeurt per regel in `.editorconfig` in de repo-root, op `warning`.

Nulmeting (`develop` `b34e2b1`): **19 overtredingen**.

| Regel | Aantal | Waar |
|---|---:|---|
| CA1502 — cyclomatische complexiteit > 25 | 13 | zwaarste: `BindMatchDetailsParameters` (58), `BouwTemplateAntwoord` (46 / 43), `VerwerkMetPlannerAsync` (33 / 31) |
| CA1506 — class coupling | 6 | `EmailTestFunction.DryRun`, `EmailProcessorFunction.Run`, `Program.cs` — elk op beide tiers |
| CA1505 — maintainability index | 0 | — |

Twee dingen zijn hier het vermelden waard.

**CA1505 op nul betekent niet dat de regel niets doet.** De maintainability index is een
samengestelde maat die pas onder de 10 klaagt; geen enkel type zit daaronder. De regel blijft aan,
zodat de ratchet hem opvangt zodra er wél een bijkomt.

**Zes van de negentien zijn tier-paren.** Net als bij regel 7 en 8 wijst de meting naar dezelfde
schuld als regel 1: `BerichtPipeline`, `EmailTestFunction`, `EmailProcessorFunction` en `Program.cs`
staan tweemaal in de codebase, dus hun overtreding telt tweemaal. Die zes verdwijnen vanzelf zodra
de gedeelde endpoint-orkestratie (#1271) verder komt — zonder dat er één methode herschreven wordt.

**Waarom een ratchet en niet meteen `error`.** De zwaarste gevallen zitten in `BerichtPipeline`, dat
binnenkomende e-mail verwerkt. Een methode met cyclomatische complexiteit 46 daar herschrijven is
een echte refactor met productierisico, geen opruimwerk dat in een chore-PR hoort. Een gate die de
eerstvolgende PR rood maakt zonder dat iemand de overtredingen heeft gezien, wordt binnen twee PR's
weer uitgezet — en bewaakt dan niets meer.

**Testprojecten zijn uitgesloten in `.editorconfig` zelf**, niet in de guard. Zelfde redenering als
hierboven: `SportlinkClubClientTests` raakt 96 typen aan, en dat is dekking, geen verstrengeling.

*Guard: `scripts/ci/check-analyzer-complexiteit.sh` — bouwt de solution en telt. Weigert te meten
als `.editorconfig` de drie regels niet aanzet: zonder die controle zou hij stilzwijgend nul tellen
en voor altijd groen staan, precies het no-op-patroon uit §67 van
`ARCHITECTUUR-DATABASE-TIERS.md`.*

### Regel 9 — Elk HTTP-endpoint autoriseert via de wrapper, nooit via een eigen poort (#1350)

Een endpoint met de admin-rol loopt via `AdminEndpoint.ExecuteAsync` (of
`AdminEndpoint.ExecuteZonderDatabaseAsync` als het geen database nodig heeft); een
Sportlink-endpoint via `SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync`, dat daarop uitkomt.
Een losse `EasyAuthHelper.RequireAdmin(req)`-aanroep in een endpoint is een overtreding, en een
`HttpTrigger` op iets anders dan `AuthorizationLevel.Anonymous` ook: een Function- of Master key
is een tweede, identiteitsloze toegangsweg naast Easy Auth.

**Wat er misging.** Bij een inventarisatie van de 92 HTTP-endpoints op de Postgres-tier bleken 49
van de 80 admin-endpoints via de wrapper te lopen en 31 de poort zelf te bouwen — telkens dezelfde
vier regels (`ExtractOrCreateCorrelationId` → `RequireAdmin` → `BeginScope` → eigen try/catch),
soms met, soms zonder databasewacht. De SQL Server-tier had daarnaast nog een dérde patroon
(`PlannerFunction.HandleAsync`). Drie manieren om dezelfde poort te bouwen betekent drie plekken
waar de volgende wijziging er één kan vergeten — en een vergeten poort geeft niemand een
foutmelding. Bovendien stond `EasyAuthHelper.RequireAuthenticated` (rol `admin` óf `user`) al
jaren ongebruikt in de code: een poort die ruimer is dan alle gebruikte, is een uitnodiging om hem
per ongeluk te pakken. Die is verwijderd. En de twee handmatige sync-routes zaten als enige achter
een Azure master key in plaats van achter Easy Auth — zonder identiteit, zonder audittrail, met een
sleutel die de hele Function App beheert.

**Twee uitzonderingen, met reden.** `AdminThemeExtract` doet een goedkope URL-vormcontrole vóór de
databaseaanroep en `AdminGeocodeGet` raakt de database helemaal niet; de wrapper zou bij beide de
databasewacht vóór die controle zetten. Ze roepen dezelfde `RequireAdmin`-poort direct aan en staan
met die reden in `scripts/ci/endpoint-autorisatie-allowlist.txt`. `Health` is het enige anonieme
endpoint en staat daar als zodanig.

**Derde variant, sinds #1330: `AdminEndpoint.ExecuteAuthenticatedAsync`.** Zelfde poort, maar hij
accepteert elke ingelogde rol (`admin` én `user`) in plaats van uitsluitend `admin`. Uitsluitend voor
endpoints die een eigenaar expliciet heeft aangewezen als "voor alle gebruikers, niet beheerder-only":
de drie Teambegeleiding-lookup/doorstuur-endpoints (de CSV-import blijft bewust admin-only), de
Planning-/Sportlink-viewing-endpoints (#1400) en sinds #764 de drie `/api/feedback/*`-endpoints. De
nieuwe naam is bewust — geen parameter op `ExecuteAsync`, om dezelfde reden als bij de andere twee
varianten (#1272). `scripts/ci/check-endpoint-autorisatie.sh` herkent hem expliciet als wrapper, en
`EndpointAutorisatieTests.MetAlleenUserRol_Geeft403` (per tier) bewijst via de `AuthenticatedRoutes`-lijst
zowel dat de aangewezen endpoints de rol `user` doorlaten als dat elk ander endpoint hem nog steeds
weigert. Een nieuw endpoint op deze variant vraagt dus een eigenaarsbesluit én een regel in die lijst.

**De test bewijst het per endpoint, zonder database.** `EndpointAutorisatieTests` (één per tier)
vindt via reflectie élk `[Function]` met een `HttpTrigger` en roept het aan: zonder principal moet
dat `401` geven, met alleen de rol `user` `403`, en met de vereiste rol(len) moet de aanroep de
poort passeren. Dat laatste wordt bewezen met een `internal` testhaak in `AdminEndpoint`
(`PoortGepasseerdVoorTests`) die ná de rolcontrole en vóór de databasewacht een sentinel
teruggeeft — alleen de wrapper kan dat resultaat opleveren, dus een endpoint met een eigen poort
valt door de mand. De haak zit ná de poort en kan die nooit verzwakken. Een echte aanroep mét
admin was geen optie: in de CI-job met een levende database zou een DELETE- of sync-endpoint dan
écht werk doen.

*Guard: `scripts/ci/check-endpoint-autorisatie.sh` — knipt elk tierbestand per `[Function(...)]`,
houdt de blokken met een `HttpTrigger` over en eist per blok: geen directe `Require*`-aanroep
(tenzij op de allowlist), wél een van de bekende wrappers (tenzij als `anoniem` op de allowlist),
en `AuthorizationLevel.Anonymous`. Een allowlist-regel zonder reden of naar een niet-bestaand
endpoint laat hem ook falen. Dezelfde knip als de Layer-5-scan in
`scripts/azure/Verify-AzureAuthSetup.ps1`, maar in CI.*

---

## 5. Register

<!-- REGELREGISTER-BEGIN -->

| Regel | Afgedwongen door | Draait in |
|---|---|---|
| 1, 2 — tier-duplicatie stijgt niet | `scripts/ci/check-tier-duplicatie.sh` | `build.yml` |
| 1, 2 — interne duplicatie stijgt niet (#1263) | `scripts/ci/check-interne-duplicatie.sh` | `build.yml` |
| 3 — geen logica in Blazor-pagina's | `scripts/ci/check-blazor-codebehind.sh` | `build.yml` |
| 3b — geen `<style>`-blok of statische inline style in Blazor-pagina's (#1329) | `scripts/ci/check-blazor-inline-styles.sh` | `build.yml` |
| 3c — gelinkte bronbestanden in BlazorAdmin: alleen `using System*`, geen `RegexOptions.Compiled` (#1461) | `scripts/ci/check-gelinkte-bronbestanden.sh` | `build.yml` |
| 4 — platformafhankelijke valkuilen | `scripts/ci/check-codekwaliteit-valkuilen.sh` | `build.yml` |
| 5 — AGENTS.md enige bron, CLAUDE.md-stubs leeg, skills één bron, codeblokken afgesloten (#1579); geen bekend tweede laadpad (toelatingsmatrix), geen symlinks, ook niet in uitgesloten mappen; omvangplafond per bestand en per keten (#1580) | `scripts/ci/check-agent-instructies.py` | `build.yml` |
| 5 — negatieve/positieve fixturetests van de agentinstructiecontrole | `scripts/ci/check-agent-instructies.test.py` | `build.yml` |
| 5 — mutatietest: geselecteerde guardregels afzonderlijk uitschakelen maakt de tests rood (#1580) | `scripts/ci/check-agent-instructies.mutaties.py` | `build.yml` |
| 5 — skillkopieën in `.claude/skills/` identiek aan de bron in `.agents/skills/` (#1579) | `scripts/ci/sync-skills.py` | `build.yml` |
| 6 — elke regel heeft een guard | `scripts/ci/check-regelregister.sh` | `build.yml` |
| 7, 8 — bestandsgrootte en methodelengte stijgen niet | `scripts/ci/check-bestandsgrootte.sh` | `build.yml` |
| 7, 8 — maintainability-analyzers stijgen niet (#1300) | `scripts/ci/check-analyzer-complexiteit.sh` | `build.yml` |
| 9 — elk HTTP-endpoint autoriseert via de wrapper, op Anonymous (#1350) | `scripts/ci/check-endpoint-autorisatie.sh` | `build.yml` |
| Alle regels — de guards worden zelf getest | `scripts/ci/check-codekwaliteit.test.sh` | `build.yml` |

De guards die al bestonden staan hier ook in. Het register is daarmee de volledige lijst: een
guard die er niet in staat, laat `check-regelregister.sh` falen — zodat een controle niet stilletjes
uit een workflow kan verdwijnen zonder dat iemand het merkt.

| Regel | Afgedwongen door | Draait in |
|---|---|---|
| Padverwijzingen exact in casing (#825) | `scripts/ci/check-path-casing.sh` | `build.yml` |
| Postgres-identifiers lowercase snake_case | `scripts/ci/check-postgres-identifier-casing.sh` | `build.yml` |
| Migratievolgnummers uniek in Database.Postgres/migrations (#1485) | `scripts/ci/check-migratie-volgnummers.sh` | `build.yml` |
| Migratievolgnummers zelf getest (#1485) | `scripts/ci/check-migratie-volgnummers.test.sh` | `build.yml` |
| Tabellen gedekt in beide tierbomen | `scripts/ci/check-postgres-table-coverage.sh` | `build.yml` |
| Kolommen gedekt in beide tierbomen | `scripts/ci/check-postgres-column-coverage.sh` | `build.yml` |
| Procedures/views gedekt in beide tierbomen | `scripts/ci/check-postgres-procedure-view-coverage.sh` | `build.yml` |
| RLS aan op elke tabel (#1198, #1220) | `scripts/ci/check-rls-enabled.sh` | `build.yml` |
| Supabase-lints (#1220) | `scripts/ci/check-splinter-lints.sh` | `build.yml` |
| Thema-CSS-variabelen consistent (#1255) | `scripts/ci/check-theme-variables.sh` | `build.yml` |
| Beide tiers bieden dezelfde routes en timers (#1266, #1268) | `scripts/ci/check-tier-pariteit.sh` | `build.yml` |
| Verify-AzureAuthSetup.ps1 lekt geen PII bij lege parameter (#1474) | `scripts/ci/check-verify-script-guards.sh` | `build.yml` |
| Een infra-deploy mag geen bestaande app setting wissen (#1455) | `scripts/ci/check-whatif-appsettings.sh` | `infrastructure.yml` |
| De what-if-poort kan ook rood worden (#1455) | `scripts/ci/check-whatif-appsettings.test.sh` | `build.yml` |

<!-- REGELREGISTER-EINDE -->

---

## 6. Wat bewust (nog) niet wordt afgedwongen

Eerlijk vermeld, zodat niemand denkt dat het gedekt is.

| Onderwerp | Waarom niet | Vervolg |
|---|---|---|
| Testdekking per productiemap | `BlazorAdmin.Tests` heeft weinig tests tegenover bijna 7.000 regels Razor; dat groeit pas als regel 3 (code-behind) verder is doorgevoerd. De drie mappen zonder testproject zijn bij #1302 wél voorzien — zie hieronder. | Regel 3 |
| Expressie-index bij een `UPPER()`-vergelijking (#1232) — **deels bewaakt sinds #1280** | In het algemeen niet schema-statisch te bepalen zonder de queries te parsen; de splinter-gate sluit `unused_index` bewust uit (§68 van `ARCHITECTUUR-DATABASE-TIERS.md`). De regel staat in `AGENTS.md`, de meting per tier in §69 en §75 daarvan. Voor de drie sleutelkolommen van de teamresolutie is het wél afdwingbaar gebleken, omdat de vergelijkingen op één plek staan. | `FunctionApp.Tests/TeamResolution/TeamCandidateIndexSargabilityTests.cs` voor de teamresolutiekolommen; daarbuiten handmatig: `EXPLAIN (ANALYZE, BUFFERS)` resp. `SHOWPLAN_TEXT` bij zo'n wijziging |
| Services, autorisatieregels (#1272) en achtergrondlogica zonder trigger in de tier-pariteit | `check-tier-pariteit.sh` bewaakt routes en timers in beide richtingen, niet wat geregistreerd wordt of draait zonder trigger. Een ontbrekende service of timer geeft niemand een 404; de database-uitvalmonitor (#831) stond zo jarenlang alleen op de SQL Server-tier. | Bij twijfel is een handmatige vergelijking van beide `Program.cs`-bestanden de snelste toets |
| Precies één `source:`-label per issue (#1336) | Herkomst is een label en geen auteursveld: Codex en Claude Code werken via `gh issue create`/`gh api` onder credentials die niet per se een uniek account per assistent zijn, dus `issue.user.login` onderscheidt ze niet betrouwbaar. Herkomst wordt handmatig gezet door de opsteller (Codex of Claude Code) — er is geen `setIssueStatus()`-achtige helper die dit afdwingt, en geen periodieke scan die een issue zonder of met dubbel `source:`-label signaleert. | Los issue indien gewenst: een periodieke workflow (zelfde vorm als `supabase-advisors.yml`) die open issues zonder precies één `source:`-label rapporteert |
| Verweesde `status: waiting-codex` (#1336, gedeprecieerd sinds #1343) | Historische wachtstatus zonder betrouwbare afrondingstrigger; de huidige wederzijdse reviews gebruiken expliciete fase/beurt en PR-bewijs. Een issue dat op `waiting-codex` blijft staan omdat niemand terugkomt, valt niet automatisch op. Sinds #1343 is dit label gedeprecieerd (zie `AGENTS.md`); de rij blijft staan zolang het label en zijn `PROTECTED`-vermelding nog bestaan. | Los issue indien gewenst: dagelijkse/wekelijkse cron die `status: waiting-codex`-issues ouder dan N dagen signaleert, of verwijder het label + de `PROTECTED`-vermelding zodra bevestigd is dat niets er meer naar verwijst |
| Precies één `turn:`-label per issue (#1343) | Net als bij `source:` (zie rij hierboven): geen `setIssueStatus()`-achtige helper dwingt exclusiviteit af voor `turn: claude-code`/`turn: codex`/`turn: owner`, en er is geen periodieke scan die een issue zonder of met dubbel `turn:`-label signaleert. | Los issue indien gewenst: dezelfde periodieke workflow als voor `source:` uitbreiden met een `turn:`-check |
| `/security-review` vóór elke release (#1470) | Afgedwongen door de skill `/release` (stap R1), niet door CI: een review in GitHub Actions vraagt een Anthropic API-sleutel en dus API-kosten. Een release buiten `/release` om (handmatig mergen van een `develop` → `main`-PR) slaat de review over. De automatische ondergrens is de Security Gate met CodeQL, die wél verplicht is op `main`. | Geen; bewust zo gelaten. Vangrail is de verplichte Security Gate |
| Maximaal twee wederzijdse reviewrondes per PR zonder eigenaarsbesluit | De rondelimiet uit de wederzijdse reviewworkflow in `AGENTS.md` geldt voor beide agents; CI houdt nog geen teller per PR bij. | Los issue indien gewenst, pas ná de handmatige simulatie/proefautomatisering uit fase 2/3 van #1343 — te vroeg bouwen zou een teller afdwingen vóórdat bekend is hoe de Codex-app dit in de praktijk gebruikt |

### Agent-isolatie — afspraken, nog geen technische locks

Sinds de instructiewijziging van 2026-10-04 mogen Codex en Claude Code beide ontwikkelen.
`AGENTS.md` legt één implementer per taak, een eigen branch/worktree per sessie, gescheiden
scopes en wederzijdse review op een vastgelegde head-SHA vast. `source:` blijft herkomst;
implementer, reviewer en fase staan afzonderlijk bij de taak. De taak-/runtime-afspraken zijn geen technische locks. De CI-guard `check-agent-instructies.py`
bewaakt wel dat CLAUDE.md-stubs leeg blijven, skillkopieën gelijk zijn aan hun bron, codeblokken zijn afgesloten, er geen bekend tweede laadpad bestaat en de omvang binnen het plafond blijft; negatieve tests en een mutatietest bewijzen voor die gevallen dat overtredingen falen.

Een issuecomment is geen atomische taakclaim; voorlopig mogen alleen vooraf toegewezen,
gescheiden taken parallel starten. Gedeelde contracten/schema's tellen als overlap, ook zonder
Git-conflict. Een technische claimvoorziening zou afzonderlijk moeten worden gerealiseerd.

De debugscriptset is nog niet per sessie geïsoleerd: `Start-Debug.ps1` stopt bestaande services.
Daarom is één vooraf gereserveerde runtime-eigenaar vereist voor services, migraties en gedeelde
testdata. Parallelle runtimes wachten op geverifieerde isolatie van poorten, processen, PID/log/
tempbestanden, databases en externe integraties. Deze wijziging realiseert die isolatie niet.

Geplande reviewruns mogen alleen een expliciet aangevraagde review uitvoeren; label plus fase,
implementer/reviewer, gekoppelde PR en head-SHA moeten overeenkomen. Bestaande automatiseringen
moeten vóór gebruik aan dat contract worden getoetst. Betrouwbare polling, rondetelling en
exclusiviteit zijn niet door deze instructiewijziging bewezen.

### Drie mappen zonder testproject, nu met een startpunt (#1302)

`Database.Postgres.Cli/`, `MigrationTools/` en `Tools/` hadden geen enkele test. Per map is bepaald
welke logica testbaar én risicovol genoeg is; een CLI-wrapper die alleen argumenten doorgeeft is dat
niet, parsing- en vertaallogica wel.

| Project | Wat er getest wordt | Waarom juist dat |
|---|---|---|
| `Database.Postgres.Cli.Tests` (11) | `CliArgumentParser` | Bepaalt of `deploy.yml` migraties toepast, `his`-tabellen aanmaakt of demodata seedt. Een verkeerde uitkomst is een verkeerde deploy. |
| `MigrationTools.Tests` (11) | `IdMapRegistry`, `TableCopier.ResolveValue` | De enige plek in de cutover-kopie waar een fout **stil** is: geen exception, maar een rij die naar het verkeerde bovenliggende record wijst. |
| *(retired in #1411)* | Token-capturetool en tests verwijderd; zie [`docs/SPORTLINK-AUTOLOGIN.md`](SPORTLINK-AUTOLOGIN.md). | |

Drie dingen die daarvoor nodig waren, en die de moeite van het onthouden waard zijn:

**Top-level statements zijn niet testbaar.** `Database.Postgres.Cli/Program.cs` gebruikt ze, en die
compileren naar een onbereikbare `<Main>$`. De argumentafhandeling is daarom verhuisd naar
`CliArgumentParser`. Dat is geen stijlkeuze: zonder die verplaatsing valt er niets te asserten
zonder het programma daadwerkelijk te starten — dezelfde reden als regel 3 voor `@code`-blokken.

**`internal` plus `InternalsVisibleTo`, niet `public`.** `TableCopier.ResolveValue` en de twee
helpers in de inmiddels verwijderde token-capturetool waren van `private` naar `internal` gegaan. Ze hoorden niet bij het
publieke oppervlak van die programma's; ze horen alleen bevraagbaar te zijn door hun eigen tests.

**De eerste test was meteen rood, en terecht.** `WriteRefreshTokenToSettings` schreef een
`local.settings.json` zónder `Values`-object gewoon terug — zonder het token, zonder foutmelding,
met een succesmelding aan de aanroeper. Dat is nu een expliciete `InvalidOperationException`, met
een vangnet achteraf voor elk toekomstig pad waarlangs de schrijfactie wordt overgeslagen.

---

## 7. Hoe je hiermee werkt

```bash
# Alle codekwaliteitsguards lokaal, zelfde volgorde als CI:
bash scripts/ci/check-tier-duplicatie.sh
bash scripts/ci/check-interne-duplicatie.sh
bash scripts/ci/check-blazor-codebehind.sh
bash scripts/ci/check-blazor-inline-styles.sh
bash scripts/ci/check-gelinkte-bronbestanden.sh
bash scripts/ci/check-codekwaliteit-valkuilen.sh
bash scripts/ci/check-bestandsgrootte.sh
bash scripts/ci/check-regelregister.sh
python3 scripts/ci/check-agent-instructies.py
python3 scripts/ci/check-agent-instructies.test.py
python3 scripts/ci/sync-skills.py

# Alleen als je de guard zelf wijzigt (draait ~40 s): mist een test voor een regel, dan overleeft een mutant
python3 scripts/ci/check-agent-instructies.mutaties.py

# Deze ene bouwt de hele solution en duurt dus langer dan de rest bij elkaar:
bash scripts/ci/check-analyzer-complexiteit.sh

# Skill in .agents/skills/ gewijzigd? Schrijf de kopie voor Claude Code:
python3 scripts/ci/sync-skills.py --schrijf
```

Alle guards behalve de laatste lezen enkel bestanden — geen database, geen secrets, geen SDK.
`check-analyzer-complexiteit.sh` is de uitzondering: hij draait `dotnet build` op
`sportlink-wedstrijdzaken.slnf`, omdat CA1502/1505/1506 compileertijd-analyzers zijn en er geen
manier is om ze zonder compilatie te tellen. Hij weigert te meten als `.editorconfig` de drie
regels niet aanzet — anders telt hij stilzwijgend nul en staat hij voor altijd groen.

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

### Grenzen van instructiehandhaving

**Instructies versus memory.** Het expliciete eigenaarsbesluit is leidend. Een memory-notitie over
een besluit vermeldt besluitdatum, PR, geldende afspraak en uitrolstatus (draft/ongemerged, develop,
main): een nog ongemergede PR betekent dat de gedeelde branches nog de vorige tekst bevatten. Bij
sessiestart wordt die status gecontroleerd en worden oude verboden niet als actuele instructie herhaald.
Memory geeft geen extra merge- of deploybevoegdheid. (Verplaatst uit `AGENTS.md` bij #1580; de
kernregel staat daar nog.)

De skillguard vergelijkt alle bestanden van elke skill met zijn kopie; skills die uitsluitend voor
Claude Code bestaan staan op `scripts/ci/skills-alleen-claude.txt` en hebben geen kopie. De stubguard
bewijst dat een stub leeg is, niet dat Claude Code de import laadt (handmatig vastgesteld bij #1579)
en niet dat Codex het hele bestand ziet: Codex leest standaard maximaal 32 KiB aan projectinstructies
(`project_doc_max_bytes`, zie #1580). De omvangcontrole bewaakt de bytes in de repository; ze bewijst
niet wat een gebruikersinstelling op een andere machine doet, en een vertrouwd project met een
projectconfig kan het budget wél verhogen — daarom weigert de guard die sleutels in `.codex/config.toml`:
het budget moet binnen de standaard passen. **Stand van deel A:** de plafondwaarden staan nog op de meting
vóór de verkleining (124.770 en 140.947 bytes) en liggen dus boven het budget van Codex; de guard voorkomt
alleen verdere groei en geeft een waarschuwing. De absolute grens volgt bij deel C van #1580. De
toelatingsmatrix hierboven volgt de documentatie van de clientversies waartegen is getoetst (Claude Code 2.1.x,
Codex 0.158.0); native Windows, junctions en case-insensitieve bronselectie zijn niet beproefd. De
fencecheck controleert alleen top-level fences (maximaal drie spaties inspringing) in skills en
`AGENTS.md`, niet alle Markdown in docs of geneste lijst-/blockquote-fences. Het is geen volledige Markdown-parser of inhoudelijke reviewer.
De guard leest UTF-8 expliciet voor Windows/macOS; zijn tests en registervermelding draaien in CI.

De gedeelde Claude-allowlist geeft geen algemene automatische toestemming meer voor merge,
release, API-mutaties, push/tag, checkout/stash of branch-/bestandsverwijdering. Native permissies
zijn aanvullend: eigenaarsautorisatie blijft vereist. Dit is geen OS-vergrendeling, GitHub branch
protection of handhaving van de Codex-toolpermissies; persoonlijke overrides en handmatige shell-
commando's kunnen ruimer zijn. Zulke overrides heffen de gezamenlijke werkinstructies niet op.
