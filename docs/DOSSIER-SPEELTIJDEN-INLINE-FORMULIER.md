# Dossier — Speeltijden: inline bewerkformulier (#1552, #1553, #1554)

> **Werkjournaal, gedateerd.** Dit dossier legt de besluiten, het verificatiebewijs, de
> reviewafhandeling en de restbeperkingen vast van de drie vervolgissues op #1543/PR #1544. De
> geldende gedragsregels voor de pagina staan in §2; de rest beschrijft de stand op de genoemde
> datum en wordt niet naar het heden bijgewerkt (zie `DOCUMENTATIEPLAN.md`, "geen archiefcategorie").

| Veld | Waarde |
|---|---|
| Issues | #1552 (laat opslagresultaat raakt andere bewerksessie), #1553 (formulier buiten beeld op smal scherm), #1554 (labels niet aan velden gekoppeld) — alle drie `source: codex`, gevonden in de Codex-review van PR #1544 |
| Implementer / reviewer | Claude Code (sessie `claude-20261006-speeltijden-vervolg`) / Codex |
| Branch → PR | `feature/#1552-speeltijden-sessie-responsive-labels` → `develop` |
| Versie | 3.11.1.3 → 3.11.1.4 (REVISION: drie fixes, geen feature) |
| Runtime | Eigen BlazorAdmin-instantie op een vrije poort met gemockte API; de gedeelde debugomgeving (:5242/:7094, Docker, Azurite) is niet gebruikt en niet aangeraakt |

## 1. Uitgangssituatie

PR #1544 (#1543) verplaatste het bewerkformulier van onder de tabel naar een tabelrij direct onder
de bewerkte regel. De Codex-review op SHA `4c416ca6` bevestigde de hoofdflow en vond drie punten:

1. De opslagmethode hield één `_editing`/`_saveError` bij. Een laat binnenkomend PUT-resultaat
   schreef daarin, ook als de gebruiker intussen een ander formulier had geopend: de fout verscheen
   onder de verkeerde regel; een laat succes sloot het verkeerde formulier. (Race bestond al vóór
   #1543; het inline formulier maakte hem zichtbaar.)
2. Het formulier erfde de breedte van de zevenkolomstabel (±525 px) en liep op 375 px buiten beeld.
3. Geen enkel label had een `for`, geen input een `id`: `input.labels.length` was 0 voor alle zes.

## 2. Besluiten en geldende afspraken voor deze pagina

### 2.1 Opslag hoort bij een bewerksessie, niet bij een categorienaam (#1552)

- `BlazorAdmin/Services/SpeeltijdBewerking.cs` is de enige plek met bewerkstatus. Elke klik op
  **Bewerken** of **Nieuwe categorie** maakt een nieuwe `SpeeltijdBewerkSessie` (ook voor dezelfde
  categorie). `OpslaanAsync` legt de sessie vast vóór de `await` en vergelijkt daarna op
  referentie (`ReferenceEquals`), niet op `Leeftijd`.
- Een afgerond resultaat verandert uitsluitend zijn eigen sessie. Is die niet meer actief:
  - succes → het actieve formulier blijft open (met de invoer die daar al staat), de lijst wordt
    ververst zonder laadscherm;
  - fout → geen fout onder het actieve formulier, maar een sluitbare melding boven de tabel met de
    naam van de categorie ("Opslaan van JO10 is mislukt: …").
- **Heropenen tijdens een lopende opslag van dezelfde categorie wordt geweigerd — tot en met de
  verversing van de lijst erna.** Het formulier kopieert de regelwaarden bij openen; zolang de
  lijst nog de waarden van vóór de opslag bevat, zou Opslaan vanuit zo'n kopie het zojuist
  opgeslagen resultaat overschrijven (lost update). Daarom toont de regel "Opslaan…" en zijn
  **Bewerken** en **Verwijderen** van die regel uitgeschakeld totdat de verse lijst binnen is;
  andere regels blijven bewerkbaar. Dit is de "sessie + per-regel-slot"-variant van de twee opties
  uit het issue; een algemene blokkade van wisselen tijdens opslaan is bewust niet gekozen.
  *Herzien na review ronde 1 (P2):* het slot viel oorspronkelijk weg zodra de PUT klaar was, terwijl
  de GET erna nog liep — in dat venster kon de regel met oude waarden heropend worden (door Codex
  gereproduceerd). Nu geldt het slot tot de verversing is afgerond.
- **Lokale bijwerking vóór de verversing, als vangnet voor een mislukte refresh.** Direct na een
  geslaagde PUT krijgt de regel in de lijst een kopie van de opgeslagen waarden (`PasLokaalToe`) en
  signaleert de staat `Gewijzigd`, zodat de pagina rendert en de waarde meteen zichtbaar is. Mislukt
  de GET daarna, dan blijft die lokaal bijgewerkte regel staan, komt het slot vrij (anders zou de
  regel eeuwig geblokkeerd zijn) en verschijnt een sluitbare waarschuwing boven de tabel
  (`VerversFout`); een volgende geslaagde verversing ruimt die waarschuwing op. Een heropend
  formulier toont dus ook zonder verse lijst de opgeslagen waarden.
- **Lijstverversingen zijn gegenereerd: alleen het antwoord op de laatst gestelde vraag telt** (review
  ronde 2, P2). Meerdere regels mogen tegelijk worden opgeslagen en elke opslag vraagt daarna de lijst
  op; die antwoorden kunnen in omgekeerde volgorde binnenkomen. Een ouder antwoord is een oudere
  momentopname: toepassen zette een intussen opgeslagen en vrijgegeven tweede regel terug op oude
  waarden (door Codex gereproduceerd: B=91 sprong terug naar 75, en alleen Rust wijzigen stuurde totaal
  75). `VerversAsync` verhoogt daarom bij elke aanvraag `_laadGeneratie` en past een antwoord alleen toe
  als er sinds zijn aanvraag geen nieuwere is gestart. Dit geldt voor *elke* verversing — na een opslag,
  een handmatige lading, een clubwissel of een verwijdering — zodat slot, lokale bijwerking en lijst
  één samenhangend geheel blijven. Een verouderd antwoord wordt genegeerd, ook als het mislukte:
  `LaadAsync` geeft dan de huidige lijst terug als geslaagd en `VerversFout` wordt alleen door de
  nieuwste verversing gezet of gewist. Het slot van een regel komt nog steeds vrij zodra *haar eigen*
  GET terug is (toegepast of genegeerd), want de lokale bijwerking heeft haar waarden dan al veilig
  in de lijst gezet. Serialiseren van verversingen is bewust niet gekozen: een tweede opslag zou dan
  op de GET van de eerste moeten wachten, terwijl de lokale bijwerking de gebruiker al direct het
  juiste resultaat laat zien.
- De lijst (`Items`) hoort bij dezelfde staat als de bewerksessies — nodig om slot, lokale
  bijwerking, generatiecontrole en verversing zonder UI te kunnen testen.
- Het component `SpeeltijdFormulier` krijgt de sessie als parameter en staat onder `@key="<sessie>"`,
  zodat Blazor bij een lijstverversing de componentinstantie (en de getypte invoer) behoudt.

### 2.2 Smalle schermen: kaarten per regel, container query (#1553)

- `BlazorAdmin/Pages/Speeltijden.razor.css`: onder 48 rem **containerbreedte** (`.speeltijden-lijst`
  heeft `container-type: inline-size`) worden `table/tbody/tr/td` `display: block`; elke cel toont
  zijn kolomnaam via `data-label` + `::before`; de kolomkoppen zijn visueel verborgen. Een container
  query en geen media query, omdat de sidebar bepaalt hoeveel ruimte de pagina heeft.
- `BlazorAdmin/Shared/SpeeltijdFormulier.razor.css`: het formulier is een CSS-grid met
  `repeat(auto-fit, minmax(min(100%, 9rem), 1fr))` — dat volgt de breedte van de cel waarin het
  staat, in plaats van de viewport zoals Bootstrap-kolommen.
- **Expliciete ARIA-rollen** (`table`, `rowgroup`, `row`, `columnheader`, `cell`): zodra tabelcellen
  `display: block` krijgen, laat Chromium de impliciete tabelsemantiek vallen. Met de rollen blijft
  de tabel voor hulptechnologie een tabel, en de `data-label`-tekst komt in de toegankelijke naam
  van de cel terecht (gemeten: `cell "Leeftijd JO12"`).

### 2.3 Labels gekoppeld via `for`/`id`, uniek per formulierinstantie (#1554)

- Elk label heeft `for="<prefix>-<veld>"`, elke input `id="<prefix>-<veld>"`; `TimeInput` geeft het
  `id` via `CaptureUnmatchedValues` door aan zijn onderliggende input (geen wijziging aan dat
  component nodig). Het prefix is uniek per instantie (`speeltijd-<volgnummer>`), zodat twee na
  elkaar of tegelijk gerenderde formulieren nooit botsende ids hebben.

### 2.4 Kleine keuzes

- De sluitknop van de melding is een gewone `btn btn-outline-secondary` "Sluiten" en niet
  Bootstrap's `btn-close`: die laatste komt nergens in de app voor en is in het donkere thema zonder
  extra CSS onzichtbaar (zwart pictogram).
- `LaadAsync(toonLaden: false)` na een opslag: de tabel blijft staan, zodat een open formulier niet
  achter "Laden..." verdwijnt.

## 3. Gewijzigde bestanden

| Bestand | Wijziging |
|---|---|
| `BlazorAdmin/Services/SpeeltijdBewerking.cs` | **Nieuw** — `SpeeltijdBewerkSessie` + `SpeeltijdBewerking` (§2.1): sessies, regel-slot, lijst, lokale bijwerking, gegenereerde verversing; zonder UI-afhankelijkheid, dus los testbaar |
| `BlazorAdmin/Pages/Speeltijden.razor(.cs)` | Pagina gebruikt `SpeeltijdBewerking` (ook voor de lijst); melding en verversingswaarschuwing boven de tabel; `Opslaan…`-status en uitgeschakelde knoppen per regel; ARIA-rollen; `data-label` per cel |
| `BlazorAdmin/Pages/Speeltijden.razor.css` | **Nieuw** — kaartweergave onder 48 rem containerbreedte |
| `BlazorAdmin/Shared/SpeeltijdFormulier.razor(.cs)` | Sessie als parameter; `for`/`id` per veld; grid-layout; `Opslaan…` op de submitknop tijdens de opslag |
| `BlazorAdmin/Shared/SpeeltijdFormulier.razor.css` | **Nieuw** — responsive grid |
| `BlazorAdmin.Tests/SpeeltijdBewerkingTests.cs` | **Nieuw** — 22 tests (§4.1) |
| `scripts/dev/browsercheck-speeltijden-formulier.mjs` | **Nieuw** — de Playwright-suite van §4.2, herhaalbaar tegen een eigen BlazorAdmin-instantie; laadt Playwright vanaf de werkmap (P3) |
| `CHANGELOG.md`, `docs/BEHEERDER-HANDLEIDING.md` §11, `docs/VERIFICATIE-SCRIPTS.md`, `docs/INDEX.md`, `docs/DOCUMENTATIEPLAN.md` | Documentatie |
| Drie `.csproj` | 3.11.1.4 |

Geen API-, database- of FunctionApp-wijziging.

## 4. Verificatie (2026-10-06)

### 4.1 Unit tests — `BlazorAdmin.Tests`

`dotnet test BlazorAdmin.Tests` → **163 geslaagd, 0 mislukt** (22 in `SpeeltijdBewerkingTests`):
directe fout (lijst niet opnieuw opgevraagd) en direct succes; vertraagde fout en vertraagd succes na
wisselen naar B; vertraagd resultaat na **Nieuwe categorie**; vertraagde fout na **Annuleer** (wordt
melding, sluitbaar); **de race uit ronde 1** (PUT geslaagd, GET hangt, tussentijdse actie, heropenen
geweigerd, na de GET heropend met 90, tweede opslag van alleen rust stuurt totaal 90); `Gewijzigd`
gaat af met de lokaal bijgewerkte waarde vóór de GET; mislukte verversing houdt de lokaal bijgewerkte
regel, geeft de regel vrij en meldt de fout; een geslaagde verversing ruimt die melding op; nieuwe
categorie staat direct in de lijst, ook bij mislukte verversing; heropenen tijdens lopende PUT
geweigerd, andere regel niet; POST van een nieuwe categorie blokkeert geen bestaande regel; dubbel
Opslaan genegeerd; bewerken en lokaal bijwerken werken op kopieën; nieuwe categorie nooit onder een
bestaande regel; laadfout laat de lijst staan. **Nieuw voor ronde 2 (vijf tests):**

- `OverlappendeOpslagen_EenOudereLijstresponsOverschrijftGeenNieuwereWaarden` — de Codex-reproductie:
  A opslaan, GET van A vasthouden; B opslaan (91) met eigen GET; de oudere GET van A (B=75) komt
  daarna binnen → B blijft 91; heropenen en alleen Rust wijzigen stuurt totaal 91.
- `OudereLijstrespons_DieVoorDeNieuwereBinnenkomt_WordtOokGenegeerd` — omgekeerde volgorde, beide GET's
  hangen tegelijk; het oudere antwoord komt eerst.
- `MislukteNieuwsteVerversing_MetLaterBinnenkomendeOudereRespons_HoudtDeLokaleWaarden` — de nieuwste
  GET mislukt, de oudere komt daarna: lokale waarden blijven, waarschuwing blijft staan.
- `HandmatigLaden_WintVanEenNogHangendeVerversing` en
  `VerouderdeHandmatigeLading_GeeftDeHuidigeLijstTerug_ZonderFout` — de generatiecontrole geldt ook voor
  `LaadAsync` en zet de pagina niet ten onrechte in de foutstand.

### 4.2 Browser — Playwright/Chromium, **212 controles geslaagd, 0 mislukt**

Opzet: eigen `dotnet run` van BlazorAdmin op een vrije poort (Development, lokale auth-bypass);
alle verkeer naar de API-poort onderschept met `page.route`, elk niet-lokaal request afgebroken.
Health-mock `{"status":"healthy","database":"online"}`; 34 synthetische categorieën, waaronder één
met een extreem lange naam. Vertraagde responsen via een gate per request, voor de PUT en voor de GET erna afzonderlijk. De GET-mock levert de stand **op het moment van de vraag** (een momentopname, zoals een echte server), ook als het antwoord later wordt vrijgegeven — zonder dat is een oud antwoord niet te onderscheiden van een nieuw.

**#1552** — tien scenario's, elk met controle dat het verkeerde formulier níet wordt geraakt:

| Scenario | Resultaat |
|---|---|
| Fout voor A komt binnen na wisselen naar B | B blijft open, geen fout onder B, melding "Opslaan van JO10 is mislukt: …" boven de tabel, A toont intussen "Opslaan…" en is uitgeschakeld, daarna weer bewerkbaar |
| Succes voor B komt binnen na wisselen naar C, waarin al getypt is | C blijft open mét de getypte waarde; regel B toont de opgeslagen waarde; geen melding |
| Fout komt binnen na **Nieuwe categorie** | nieuw formulier blijft open bovenaan, zonder fout; melding noemt de juiste categorie |
| Fout komt binnen na **Annuleer** | geen formulier heropend; melding |
| Succes komt binnen na **Annuleer** + heropenen van dezelfde categorie | heropenen is geblokkeerd zolang de opslag loopt (Bewerken én Verwijderen uitgeschakeld, andere regel niet); daarna toont het heropende formulier de opgeslagen waarde |
| Directe fout / direct succes | fout uitsluitend in het eigen formulier; succes sluit en ververst |
| **Codex-reproductie P2:** PUT geslaagd, GET vastgehouden, tussentijdse render (Nieuwe categorie), heropenen, tweede opslag van alleen rust | regel toont 90 al vóór de GET; Bewerken/Verwijderen blijven uitgeschakeld tot de GET klaar is; daarna heropend met 90; de tweede PUT stuurt totaal 90 en rust 13 — geen lost update |
| PUT geslaagd, GET mislukt (500) | waarschuwing boven de tabel noemt de categorie; regel toont de opgeslagen waarde; regel komt vrij; heropend formulier toont de opgeslagen waarde; waarschuwing sluitbaar |
| **Codex-reproductie P2 ronde 2:** A opslaan, GET van A (A=90, B=75) vasthouden; B opslaan (91) met eigen GET; de oudere GET van A vrijgeven | B blijft 91 (niet teruggezet naar 75), A toont 90 en is vrijgegeven; heropend B toont 91; de tweede opslag van B stuurt totaal 91 |
| Idem, maar de nieuwste verversing (van B) mislukt | waarschuwing zichtbaar, B toont lokaal 93; de oudere respons van A wordt genegeerd en beide regels houden hun opgeslagen waarden; waarschuwing blijft tot Sluiten |

Alle vijftien opslagen liepen via de gemockte PUT; de netwerklog bevatte precies de zes opzettelijk
mislukte responsen (4× PUT, 2× GET); geen page- of console-errors. De suite print elke controle
direct, zodat een crash halverwege zichtbaar laat wat er al was vastgesteld.

**#1553** — 4 breedtes × 5 situaties (bewerken, bewerken met lange naam, nieuw, lange foutmelding,
lege lijst), per combinatie: alle velden/knoppen/meldingen binnen de viewport, lijst en formulier
zonder horizontale scroll, overige pagina-inhoud binnen de viewport, **Opslaan** bereikbaar met Tab.

| Viewport | Formulierbreedte | Lijst scroll/client | Documentbreedte |
|---|---|---|---|
| 320 px | 272 px | 272 / 272 | 360 px — uitsluitend door de bovenbalk van `MainLayout` (§6, #1556) |
| 375 px | 327 px | past | 375 px |
| 768 px | 462 px | past | 768 px |
| 1400 px | 1094 px | past | 1400 px |

ARIA-snapshot op 375 px (kaartweergave): `table` → `rowgroup` → `row` → `columnheader`/`cell`,
met `cell "Leeftijd JO12"` enz. — de tabelsemantiek blijft, de kolomnaam zit in de celnaam.

**#1554** — in beide modi (bewerken, nieuw): alle zes inputs hebben precies één label met
`for` = `id`; ids uniek, en opeenvolgende formulieren gebruiken verschillende id-reeksen;
toegankelijke namen `textbox "Leeftijd"`, `spinbutton "Veldafmeting"`, `spinbutton "Totaal (min)
incl. rust"`, `spinbutton "Helft (min)"`, `spinbutton "Rust (min)"`, `textbox "Standaard
voorkeurstijd"`; labelklik focust het veld (bij bestaande categorie is Leeftijd uitgeschakeld en
krijgt terecht geen focus); tabvolgorde veldafmeting → totaal → helft → rust → voorkeurstijd →
Opslaan → Annuleer (nieuw: leeftijd eerst); Enter in een veld slaat op.

### 4.3 Guards en instructiechecks (allemaal exit 0)

`check-blazor-codebehind`, `check-blazor-inline-styles`, `check-interne-duplicatie`,
`check-bestandsgrootte`, `check-codekwaliteit-valkuilen`, `check-analyzer-complexiteit`,
`check-path-casing`, `check-theme-variables`, `check-regelregister`;
`check-agent-instructies.py` en `check-agent-instructies.test.py`.
`dotnet build BlazorAdmin` groen (één bestaande CA1502-waarschuwing buiten de diff).

### 4.4 Niet lokaal uitgevoerd

- FunctionApp-builds en `Test-App.ps1`: geen FunctionApp-code gewijzigd (alleen het versieveld);
  de gedeelde debugomgeving was niet gereserveerd. CI bouwt beide tiers.
- Release-publish/CSP-controle van `index.html`: zit in de CI-job; lokaal niet herhaald.
- Echte schermlezer (VoiceOver/NVDA): alleen de accessibility tree van Chromium is gecontroleerd.
- Live API, database of productie: niets geraakt.

### 4.5 Kunnen de regressies rood worden? (negatieve controle)

Een groene regressietest bewijst niets zolang niet vaststaat dat hij ook faalt op de fout die hij
moet vangen. Daarom zijn de nieuwe tests uit ronde 2 gedraaid tegen de code van vóór de fix
(`bf8187d8`), in een tijdelijke, eigen worktree en met een eigen BlazorAdmin op een aparte poort:

- **Unit tests:** de vijf nieuwe tests uit §4.1 (163 − 158) falen alle vijf op `bf8187d8`; de overige
  158 slagen. Op de nieuwe code slagen alle 163.
- **Browsersuite:** op `bf8187d8` falen vijf controles, waaronder de Codex-reproductie zelf: *"B blijft
  91 na de oudere GET-respons van A"*, *"heropend B toont 91"* en *"tweede opslag van B stuurt totaal 91
  (body totaal=75)"* — precies het verlies dat Codex vond (de opgeslagen 91 wordt als 75 teruggeschreven).
  Op de nieuwe code: 212/212.


## 5. Reviewafhandeling

| Ronde | Reviewer | SHA | Uitkomst | Afhandeling |
|---|---|---|---|---|
| 1 | Codex | `cb09174f` | Bevindingen: P2 (regel-slot viel weg tussen geslaagde PUT en afgeronde GET → lost update gereproduceerd), P3 (gedocumenteerde scratch-mapaanroep vindt Playwright niet: ESM resolveert vanaf het scriptbestand), telling 192 ≠ 193 | **P2 opgelost:** slot tot de verversing is afgerond, lokale bijwerking vóór de GET met `Gewijzigd`-render, `VerversFout` bij mislukte refresh; regressie als unit test én als browserscenario met afzonderlijk vertraagde PUT en GET (§4). **P3 opgelost:** het script laadt Playwright vanaf de werkmap (`<werkmap>/node_modules/playwright`) met een duidelijke foutmelding als het ontbreekt; de gedocumenteerde aanroep is vanuit een scratch-map tegen het script in de repo geverifieerd — de oude versie faalde met `ERR_MODULE_NOT_FOUND`, de nieuwe draait. **Telling opgelost:** het dossier telde een inmiddels verwijderde no-op-assertie mee; nu 203, gelijk aan de suite. |
| 2 | Codex | `bf8187d8` | Bevinding: één P2 — een oudere GET van een *andere*, overlappende opslag zette een al vrijgegeven regel terug (B=91 → 75; alleen Rust wijzigen stuurde totaal 75). Ronde-1-punten bevestigd als opgelost. Dit was de laatste reviewronde (maximaal twee); geen derde review aangevraagd. | **P2 opgelost:** gegenereerde verversing (`_laadGeneratie`, §2.1) — alleen het antwoord op de laatst gestelde vraag wordt toegepast, ook voor handmatige ladingen; verouderde antwoorden worden genegeerd, ook bij een mislukte nieuwste verversing. Regressie: vijf nieuwe unit tests en twee browserscenario's (omgekeerde antwoordvolgorde, mislukte verversing), negatief bewezen op `bf8187d8` (§4.5). Verwerkt in de commit na `bf8187d8` op deze branch; de exacte SHA staat in het verwerkingscomment op PR #1557. Beurt: fase `eigenaarsbesluit`, `turn: owner`. |

## 6. Restbeperkingen en vervolg

- **Bovenbalk op 320 px (#1556).** `MainLayout`'s `.top-row` is minimaal ±360 px breed; op een
  320 px-scherm pant de hele pagina daardoor horizontaal, onafhankelijk van deze pagina (gemeten met
  gesloten formulier). Buiten de scope van #1553; apart issue, geen implementer.
- Het per-regel-slot (§2.1) blokkeert bewust alleen de regel in opslag. Een **Nieuwe categorie**
  met dezelfde naam als een regel in opslag wordt niet geblokkeerd; de server beslist dan
  (bestaat al / overschrijven). Verwijderen van een *andere* regel tijdens een lopende opslag blijft
  mogelijk.
- `display: block` op tabelcellen is een bekend patroon met bekende beperkingen; de expliciete
  rollen vangen de semantiek op, maar een echte schermlezertest staat open (§4.4).
- **Een genegeerd (ouder) lijstantwoord brengt geen wijzigingen van anderen binnen** tot de eerstvolgende
  verversing; de lijst blijft dan op de lokale bijwerkingen en het antwoord van de nieuwste vraag
  staan. Voor het eigen opslaggedrag is dat bedoeld (§2.1).
- **Gelijktijdige bewerking door een andere beheerder** is niet afgedekt (geen ETag/versieveld):
  wie een regel opent terwijl iemand anders dezelfde regel wijzigt, overschrijft die wijziging bij
  Opslaan. Het slot van §2.1 beschermt alleen tegen de eigen, nog lopende opslag.
- Chrome logt elke 4xx/5xx-respons als console-error. De browsersuite telt die apart als
  netwerklog; wie de suite uitbreidt, moet die scheiding behouden om echte fouten niet te maskeren.

## 7. Overdracht

- Herhalen van de browsersuite: start BlazorAdmin zelf op een vrije poort
  (`ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://localhost:<poort>`
  in `BlazorAdmin/`). Installeer Playwright in een scratch-map (`npm install playwright`,
  `npx playwright install chromium`) en roep van dáár uit het script in de repo aan:
  `BLAZOR_URL=http://localhost:<poort> node <repo>/scripts/dev/browsercheck-speeltijden-formulier.mjs`.
  Het script zoekt Playwright in de werkmap; kopiëren is niet nodig. Zie `docs/VERIFICATIE-SCRIPTS.md`.
- **Stand na ronde 2:** alle bevindingen van beide reviewrondes zijn verwerkt; fase `eigenaarsbesluit`, `turn: owner` op #1552/#1553/#1554. Merge naar `develop` volgt pas op een afzonderlijke opdracht van de eigenaar.
- Na de merge: worktree en branch van deze sessie opruimen; het issue blijft `awaiting-release`
  tot de volgende productierelease.

*Laatste verificatie: v3.11.1.4 — 2026-10-06 (na review ronde 2)*
