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
- **Heropenen tijdens een lopende opslag van dezelfde categorie wordt geweigerd.** Het formulier
  kopieert de regelwaarden bij openen; tijdens een lopende opslag zijn dat de waarden van vóór die
  opslag, en Opslaan vanuit zo'n kopie zou het zojuist opgeslagen resultaat overschrijven (lost
  update). Daarom toont de regel "Opslaan…" en zijn **Bewerken** en **Verwijderen** van die regel
  uitgeschakeld totdat de opslag is afgerond; andere regels blijven bewerkbaar. Dit is de
  "sessie + per-regel-slot"-variant van de twee opties uit het issue; een algemene blokkade van
  wisselen tijdens opslaan is bewust niet gekozen.
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
| `BlazorAdmin/Services/SpeeltijdBewerking.cs` | **Nieuw** — `SpeeltijdBewerkSessie` + `SpeeltijdBewerking` (§2.1), zonder UI-afhankelijkheid, dus los testbaar |
| `BlazorAdmin/Pages/Speeltijden.razor(.cs)` | Pagina gebruikt `SpeeltijdBewerking`; melding boven de tabel; `Opslaan…`-status en uitgeschakelde knoppen per regel; ARIA-rollen; `data-label` per cel |
| `BlazorAdmin/Pages/Speeltijden.razor.css` | **Nieuw** — kaartweergave onder 48 rem containerbreedte |
| `BlazorAdmin/Shared/SpeeltijdFormulier.razor(.cs)` | Sessie als parameter; `for`/`id` per veld; grid-layout; `Opslaan…` op de submitknop tijdens de opslag |
| `BlazorAdmin/Shared/SpeeltijdFormulier.razor.css` | **Nieuw** — responsive grid |
| `BlazorAdmin.Tests/SpeeltijdBewerkingTests.cs` | **Nieuw** — 12 tests (§4.1) |
| `scripts/dev/browsercheck-speeltijden-formulier.mjs` | **Nieuw** — de Playwright-suite van §4.2, herhaalbaar tegen een eigen BlazorAdmin-instantie |
| `CHANGELOG.md`, `docs/BEHEERDER-HANDLEIDING.md` §11, `docs/VERIFICATIE-SCRIPTS.md`, `docs/INDEX.md`, `docs/DOCUMENTATIEPLAN.md` | Documentatie |
| Drie `.csproj` | 3.11.1.4 |

Geen API-, database- of FunctionApp-wijziging.

## 4. Verificatie (2026-10-06)

### 4.1 Unit tests — `BlazorAdmin.Tests`

`dotnet test BlazorAdmin.Tests` → **153 geslaagd, 0 mislukt** (12 nieuw in `SpeeltijdBewerkingTests`):
directe fout/succes; vertraagde fout en vertraagd succes na wisselen naar B; vertraagd resultaat na
**Nieuwe categorie**; vertraagde fout na **Annuleer** (wordt melding, sluitbaar); heropenen tijdens
lopende opslag geweigerd en daarna weer toegestaan; `IsOpslagBezig` alleen voor de regel in opslag,
ook na een mislukte opslag vrijgegeven; POST van een nieuwe categorie blokkeert geen bestaande regel;
dubbel Opslaan tijdens een lopende opslag genegeerd; bewerken werkt op een kopie; een nieuwe
categorie staat nooit onder een bestaande regel.

### 4.2 Browser — Playwright/Chromium, **193 controles geslaagd, 0 mislukt**

Opzet: eigen `dotnet run` van BlazorAdmin op een vrije poort (Development, lokale auth-bypass);
alle verkeer naar de API-poort onderschept met `page.route`, elk niet-lokaal request afgebroken.
Health-mock `{"status":"healthy","database":"online"}`; 34 synthetische categorieën, waaronder één
met een extreem lange naam. Vertraagde responsen via een gate per request.

**#1552** — zes scenario's, elk met controle dat het verkeerde formulier níet wordt geraakt:

| Scenario | Resultaat |
|---|---|
| Fout voor A komt binnen na wisselen naar B | B blijft open, geen fout onder B, melding "Opslaan van JO10 is mislukt: …" boven de tabel, A toont intussen "Opslaan…" en is uitgeschakeld, daarna weer bewerkbaar |
| Succes voor B komt binnen na wisselen naar C, waarin al getypt is | C blijft open mét de getypte waarde; regel B toont de opgeslagen waarde; geen melding |
| Fout komt binnen na **Nieuwe categorie** | nieuw formulier blijft open bovenaan, zonder fout; melding noemt de juiste categorie |
| Fout komt binnen na **Annuleer** | geen formulier heropend; melding |
| Succes komt binnen na **Annuleer** + heropenen van dezelfde categorie | heropenen is geblokkeerd zolang de opslag loopt (Bewerken én Verwijderen uitgeschakeld, andere regel niet); daarna toont het heropende formulier de opgeslagen waarde |
| Directe fout / direct succes | fout uitsluitend in het eigen formulier; succes sluit en ververst |

Alle zeven opslagen liepen via de gemockte PUT; de netwerklog bevatte precies de vier opzettelijk
mislukte responsen; geen page- of console-errors.

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

## 5. Reviewafhandeling

| Ronde | Reviewer | SHA | Uitkomst | Afhandeling |
|---|---|---|---|---|
| 1 | Codex | aangeboden head-SHA staat in de reviewaanvraag op de PR; wordt hier bij de verwerking ingevuld | *(volgt)* | *(volgt)* |

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
- Chrome logt elke 4xx/5xx-respons als console-error. De browsersuite telt die apart als
  netwerklog; wie de suite uitbreidt, moet die scheiding behouden om echte fouten niet te maskeren.

## 7. Overdracht

- Herhalen van de browsersuite: start BlazorAdmin zelf op een vrije poort
  (`ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://localhost:<poort>`
  in `BlazorAdmin/`), dan `BLAZOR_URL=http://localhost:<poort> node scripts/dev/browsercheck-speeltijden-formulier.mjs`
  vanuit een map met `playwright` geïnstalleerd (`npm install playwright`, Chromium via
  `npx playwright install chromium`). Zie `docs/VERIFICATIE-SCRIPTS.md`.
- Na de merge: worktree en branch van deze sessie opruimen; het issue blijft `awaiting-release`
  tot de volgende productierelease.

*Laatste verificatie: v3.11.1.4 — 2026-10-06*
