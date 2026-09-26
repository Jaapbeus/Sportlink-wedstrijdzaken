# Versioning & Changelog — Definities en Beslisregels

Dit document is de **gezaghebbende bron** voor alle vragen over wat een bug is,
wat een feature is, wat in het changelog hoort en hoe versienummers worden bepaald.
Bij twijfel: raadpleeg dit document. Bij aanpassing: commit de wijziging hier ook.

---

## 0. CISO-verantwoordelijkheid — AVG en PII in documentatie

> **Dit is de meest kritieke sectie.** Documentatie is publiek zodra de repo publiek wordt
> of een PR wordt gemerged. Een emailadres in CHANGELOG.md is een datalek.

### Wat NOOIT in CHANGELOG, docs/ of commit-messages mag

| Type gegeven | Voorbeeld (NOOIT zo opschrijven) | Wat te gebruiken |
|---|---|---|
| Emailadres (persoonlijk of zakelijk) | adres eindigend op @vv-club.nl of @gmail.com | `[emailadres]` of `naam@example.nl` |
| Naam van een persoon in bug-context | "bug gemeld door Jan Jansen" | "bug gemeld door een beheerder" |
| Telefoonnummer | een NL mobiel nummer (06 + 8 cijfers) | nooit nodig in changelog |
| Sportlink-ledencode | ledencode in formaat BCZ-... | `[ledencode]` |
| IP-adres of server-hostname | `192.168.1.1`, `srv01.vv-club.nl` | nooit nodig in changelog |
| Inhoud van een testaanvraag of e-mail | "e-mail over wedstrijd op 14 mei" | omschrijving zonder persoonsgegevens |

### Hoe beschrijf je een bug ZONDER PII?

Fout: `Fixed: e-mailverwerking faalde voor een specifiek gmail-adres met punt vóór @`
Goed: `E-mailadressen met een punt vóór het @-teken werden incorrect geparsed`

Fout: `Fixed: classificatie van e-mail van een externe club werkte niet`
Goed: `AI-classificatie herkende externe club-emailadressen niet als 'tegenstander'`

Fout: `Fixed: herplanverzoek van Jan Jansen voor wedstrijd JO13-2 op 14 mei`
Goed: `Herplanverzoeken voor jeugdteams werden verkeerd geclassificeerd`

### Blokkeringsprotocol

Als Claude (als CISO) twijfelt of een changelog-entry PII bevat:

1. **STOP** — commit niet
2. Herschrijf de entry zodat het gedrag beschreven wordt, niet de persoon of het adres
3. Controleer: is de herschreven versie begrijpelijk zonder de PII? Zo nee → beschrijving klopt niet
4. Pas daarna committen

Bij ontdekking van PII in een bestaande commit:
1. **Meld direct aan de gebruiker** — ook als de repo privé is
2. Verwijder via `git filter-repo` of `git filter-branch` (destructief — overlegen eerst)
3. Roteer eventuele API-sleutels of wachtwoorden als die ook exposed waren
4. Documenteer in SECURITY.md

### Automatische bewaking

Drie lagen blokkeren PII automatisch:
- **Pre-commit hook** (lokaal) — blokkeert commit als CHANGELOG.md of docs/ een emailadres bevat
- **Security Scan (GitHub Actions)** — `pii-docs` job blokkeert de merge
- **Security Gate** — geen merge naar main mogelijk zolang pii-docs rood is

---

## 1. Wat hoort WEL in het CHANGELOG?

Het changelog is geschreven **voor de beheerder en gebruiker van de applicatie**,
niet voor de developer. De kernvraag is:

> _"Merkt iemand die de applicatie gebruikt of beheert dit verschil?"_

| Wijziging | In CHANGELOG? | Reden |
|---|---|---|
| Nieuw scherm in Admin GUI | ✅ Ja | Beheerder merkt het direct |
| Nieuw API-endpoint | ✅ Ja | Integrerende partijen worden geraakt |
| Bug die 500-errors veroorzaakte | ✅ Ja | Beheerder had last van de fout |
| Security-patch (ook intern) | ✅ Ja | Altijd transparant — vertrouwenseis |
| Nieuwe instelling in AppSettings | ✅ Ja | Beheerder moet weten dat het configureerbaar is |
| Breaking change in API of schema | ✅ Ja | Altijd — ook als de wijziging klein is |
| Verwijdering van functionaliteit | ✅ Ja | Beheerder moet zich kunnen voorbereiden |

---

## 2. Wat hoort NIET in het CHANGELOG?

| Wijziging | In CHANGELOG? | Reden |
|---|---|---|
| Refactoring zonder gedragswijziging | ❌ Nee | Geen merkbaar effect voor gebruiker |
| Hernoemen van interne klassen | ❌ Nee | Puur intern (bijv. EmailAiService → BerichtAiService) |
| Test-script verbeteringen (Test-App.ps1) | ❌ Nee | Ontwikkeltool, niet applicatiefunctionaliteit |
| CLAUDE.md / documentatie bijwerken | ❌ Nee | Intern ontwikkeldocument |
| Build-configuratie (csproj, niet-deploy workflows) | ❌ Nee | Intern |
| Typo's in code-comments | ❌ Nee | Intern |
| Aanpassing in een test die fout positief gaf | ❌ Nee | De applicatie veranderde niet, de test was fout |
| Dependency-update zonder gedragswijziging | ❌ Nee | Tenzij het een security-CVE was |
| Versiebeheer-setup zelf (CHANGELOG aanmaken) | ❌ Nee | Meta, niet applicatie-inhoud |

### Grensgevallen

**Deploy-workflow fix** — alleen in changelog als de fout een deployment blokkeerde
die gebruikers troffen. Een correctie van de SDK-versie in `deploy.yml` die geen gedrag
verandert: ❌ niet in changelog (interne infrastructuur-correctie).

> Verwar de **SDK**-versie in `deploy.yml` (`DOTNET_VERSION: '10.0.x'`) niet met het
> **doelframework**. Dat van de FunctionApp is en blijft `net9.0` — op beide tiers — tot epic #1063
> de cutover naar Flex Consumption doet; een `net10.0`-build geeft op het Linux Consumption Plan
> een 503 "Function host is not running". Alleen `BlazorAdmin` staat op `net10.0`. Bump dus nooit
> het `<TargetFramework>` van een FunctionApp-csproj op grond van dit voorbeeld.

**Performance-verbetering** — alleen in changelog als de verbetering merkbaar is
voor de gebruiker (bijv. "laadtijd overview 60% sneller"). Micro-optimalisaties: ❌.

**Hernoemen van routes** — `admin→beheer` is een Breaking Change voor integrerende
partijen, dus ✅ in changelog onder `Changed`.

**Databasemigratie** — `deploy.yml` past migraties zélf toe, vóór de code live gaat: job
`db-migrate` bij `DatabaseTier=SqlServer`, `db-migrate-postgres` bij `DatabaseTier=Postgres`, en
`deploy` wacht op de migratiejob van de actieve tier. Er is dus geen handmatige migratieronde meer
na een release.

- Een migratie die alleen schema *toevoegt*: ❌ niet in changelog — de beheerder merkt er niets van.
- Een migratie die de **vorige** code breekt (kolom weg, type gewijzigd, constraint aangescherpt):
  ✅ onder `Changed`, met een expliciete waarschuwing. En: die mag **niet in dezelfde release** als
  de code die hem nodig heeft — de migratie draait immers vóór de nieuwe code live is. Zie
  `docs/ARCHITECTUUR-DATABASE-TIERS.md` §57.
- De smoke test in `deploy.yml` faalt op een niet-lege `pendingMigrations` in `GET /api/health`.

---

## 3. Wat is een Bug?

> **Bug** = De code doet iets anders dan gespecificeerd of redelijkerwijs verwacht,
> met aantoonbaar onjuist gedrag als gevolg.

Drie vereisten, alle drie vereist:

1. **Er was een specificatie of duidelijke verwachting** — expliciet (issue, design) of impliciet (de functie heet `LoadSettings` dus settings moeten geladen zijn)
2. **De code wijkt daarvan af** — niet door bewust design, maar door een fout
3. **Dit heeft meetbaar effect op gedrag** — een gebruiker of beheerder ondervindt het

### Voorbeelden: WEL een Bug

| Situatie | Waarom Bug |
|---|---|
| `WaitForDatabaseAsync` laadde geen settings → alle admin-endpoints gaven 500 | Verwachting: settings beschikbaar na DB-verbinding. Effect: alle endpoints faalden. |
| `LoadSettingsAsync` miste 7 van 18 kolommen (`ClubCode` etc.) | Verwachting: alle instellingen geladen. Effect: ClubCode leeg → 500. |
| `his.teams` had geen ClubCode → teams-endpoint 500 | Tabel was aangemaakt vóór multi-club migratie zonder migratiescript. Effect: endpoint onbruikbaar. |
| E-mail tester gebruikte hardcoded antwoord i.p.v. echte pipeline | Verwachting: dry-run ≡ live. Effect: tester gaf verkeerde output. |
| `teamnaam LIKE '[ClubCode]%'` filter in teams-query | Hardcoded clubnaam; voor andere clubs werkt het niet. Effect: architectuurschending + functionele fout. |

### Voorbeelden: GEEN Bug (maar toch een fix)

| Situatie | Wat het is | Waarom geen Bug |
|---|---|---|
| `blazor-error-ui` altijd in statische HTML → Test-App.ps1 false positive | Testscript-fix | De applicatie werkte correct; de test was fout |
| Beheerder meldt dat iets "raar werkt" → bleek verkeerd geconfigureerd | Configuratieprobleem | Code was correct; geen code-wijziging nodig |
| AI classificeert een e-mail verkeerd | Modelgedrag / promptverbetering | Geen specificatie geschonden; eventueel `Fixed` als prompt duidelijk fout was |
| Route-prefix `admin→beheer` — "dat werkte toch?" | Breaking Change / Behavior Change | Het was een bewuste ontwerpkeuze, geen fout |

---

## 4. Bug vs. Issue vs. Fix — de driehoek

### GitHub Issue
Een GitHub Issue is een **gemelde afwijking of verzoek**. De oorzaak is nog onbekend.
Een Issue kan uitmonden in:

```
GitHub Issue
    ├── Bug Fix        → code was fout, gecorrigeerd
    ├── Feature        → terechte wens, nieuw gebouwd
    ├── Enhancement    → terechte wens, bestaande feature uitgebreid
    ├── Configuration  → code was correct, configuratie aangepast
    ├── Documentation  → onduidelijkheid opgehelderd, geen code-wijziging
    └── By Design      → gedrag was correct, verwachting bijgesteld (won't fix)
```

Niet elke Issue is een Bug. Niet elke Issue leidt tot een CHANGELOG-entry.

### Fix
Een **Fix** is de oplossing van een Bug of een Issue. De Fix bepaalt wat er in het
changelog komt:

| Fix-type | CHANGELOG-entry |
|---|---|
| Code was fout, gecorrigeerd | `### Fixed` |
| Test/monitoring was fout, geen code-wijziging | ❌ Niet in changelog |
| Configuratie was fout, applicatie correct | `### Fixed` (beheerder had last) |
| Spec was onduidelijk, gedrag bijgesteld | `### Changed` (bewuste aanpassing) |
| Security-schending gedicht | `### Security` |

### Een onterechte melding is een issue, geen changelog-entry

Een onterechte melding of een verkeerde terugkoppeling is een **issue** die een fix oplevert, maar
valt in de categorie `By Design` of `Documentation`: de applicatie veranderde niet, dus in het
changelog verschijnt niets.

- In de commit-message: `fix(test): Test-App.ps1 detecteerde blazor-error-ui false positive`
- In CHANGELOG: niets — de applicatie was correct.

---

## 5. Wat is een Feature?

> **Bumpregels staan in [§6](#6-versie-bump-beslisboom), niet hier.** Deze sectie gaat over de
> *classificatie* (feature, enhancement, behavior change). Tijdens development krijgt élke `feat:`
> een **PATCH**-bump; MINOR komt pas bij de release. De versienummers in de kopjes hieronder zijn
> dus release-nummers, niet wat je per commit zet.

### Nieuwe Feature (development: PATCH · bij de release: MINOR)

> Iets wat de applicatie eerder **niet kon**, nu **wel kan**.
> Een geheel nieuwe capability voor de beheerder of gebruiker.

Kenmerken:
- Nieuw scherm, nieuw endpoint, nieuwe workflow, nieuw kanaal
- Vereist typisch nieuwe DB-tabel(len) of significante nieuwe code-paden
- Iemand die de applicatie al kent zegt: "Oh, dit is nieuw"

Voorbeelden in dit project:
- Admin GUI (Blazor) — v2.0.0: bestond niet, nu wel
- E-mailverwerkingspipeline — compleet nieuwe flow
- E-mail tester (dry-run) — nieuwe capability
- InternDomein-filter — nieuwe classificatielogica
- TeamRegels CRUD — teamspecifieke regels bestonden niet als concept

### Feature-uitbreiding / Enhancement (development: PATCH of REVISION)

> Een bestaande feature krijgt **extra opties, velden of gedrag**.
> De basiswerking bestond al; er wordt iets aan toegevoegd.

Kenmerken:
- Bestaand formulier/endpoint krijgt extra veld(en)
- Bestaande berekening wordt uitgebreid met een nieuw scenario
- Iemand die de applicatie al kent zegt: "Oh, dit kon ik nog niet maar het past erbij"

Versie-impact (zie §6 voor de volledige beslisboom):
- Tijdens development: elke uitbreiding die de gebruiker iets nieuws laat doen → **PATCH**
  (`2.15.0.0 → 2.15.1.0`), ongeacht of je het een feature of een enhancement noemt
- Een kleine uitbreiding die onderdeel is van een bugfix → **REVISION** (`2.15.1.0 → 2.15.1.1`)
- Bij de release wordt dit samen één **MINOR**-bump (`2.15.x.x → 2.16.0.0`)
- Breaking change → **MAJOR**, in beide fasen

Voorbeelden in dit project:
- EmailVoetnoot — e-mail bestond, voetnoot-editor is nieuw veld → **Enhancement**
- Extra kolommen in AppSettings — settings bestonden, nieuwe kolommen komen erbij → **Enhancement**
- Veldbeschikbaarheid per dag — velden bestonden, beschikbaarheidslogica is nieuw → grensgeval: **Nieuwe feature** (nieuwe DB-tabel, nieuwe workflow)

### Behavior Change / Refactor (geen versie-bump)

> Bestaand gedrag wordt **bewust anders**, zonder dat het technisch een bug was.
> Gedrag was correct per spec, maar de spec is bijgesteld.

In CHANGELOG als `### Changed`. Geen versie-bump tenzij breaking.

Voorbeelden:
- Route-prefix `admin→beheer` — was correct, is anders geworden
- UTC-tijdstempels in GUI in plaats van raw UTC — weergave-keuze
- `?? "[ClubCode]"` → `?? throw InvalidOperationException` — architectuurkeuze aangescherpt

---

## 6. Versie-bump beslisboom

Het versienummer heeft **vier** cijfers: `MAJOR.MINOR.PATCH.REVISION` — met twee duidelijk gescheiden fasen.

### Fase 1 — development (commit-voor-commit op feature/* branch)

| Getal | Wanneer omhoog | Reset bij |
|---|---|---|
| **MAJOR** | Breaking change voor de gebruiker | — |
| **PATCH** | Nieuwe feature (`feat:`) | MINOR-bump → 0 |
| **REVISION** | Bugfix, security-patch, kleine fix, CSS/UX met zichtbaar effect | PATCH-bump → 0 |

> **Waarom MINOR niet tijdens development?** Productie-MINOR-nummers moeten overeen komen met
> echte releases. Als elke `feat:`-commit de MINOR ophoogt, raakt productie (v2.5) ver achter op
> development (v2.15) zonder dat er ook maar één release is geweest.

```
Development — wat is er gewijzigd?
│
├── Verwijdert of breekt bestaande functionaliteit voor een gebruiker?
│   └── JA → MAJOR (x.0.0.0)
│
├── Voegt nieuwe gebruikersfunctionaliteit toe? (feat:)
│   └── JA → PATCH (2.15.x.0)
│
├── Repareert iets wat verkeerd werkte? (fix: / security:)
│   └── JA → REVISION (2.15.1.x)
│
├── Kleine fix, CSS, UX-verbetering of chore met zichtbaar effect?
│   └── JA → REVISION (2.15.1.x)
│
└── Alleen intern (refactor zonder effect, docs, tooling, CLAUDE.md)?
    └── Geen versie-bump
```

### Fase 2 — release (develop → main, één keer per release)

Kijk naar de inhoud van `[Unreleased]` in CHANGELOG.md en bepaal dan pas de MINOR/PATCH:

| Inhoud van `[Unreleased]` | Versie-actie | Voorbeeld |
|---|---|---|
| Bevat minimaal één `feat:` | **MINOR bump**, PATCH + REVISION → 0 | `2.15.2.3 → 2.16.0.0` |
| Alleen `fix:`/`security:`, geen `feat:` | PATCH bump, REVISION → 0 | `2.15.2.3 → 2.15.3.0` |
| BREAKING CHANGE aanwezig | MAJOR bump | `2.15.x.x → 3.0.0.0` |

> **Resultaat:** productie gaat netjes `2.15 → 2.16 → 2.17`. Development heeft tussentijds
> volledige granulariteit (`2.15.1.0`, `2.15.2.3`) zonder de productie-teller op te blazen.

### In de csproj — er zijn er **drie**, niet twee

Zet alle drie de velden synchroon op het volledige 4-cijferige nummer in **alle drie** de csproj's:

| Bestand | Tier / component |
|---|---|
| `FunctionApp.Postgres/FunctionApp.Postgres.csproj` | Postgres-tier — **draait in productie** |
| `FunctionApp/fa-dev-sportlink-01.csproj` | SQL Server-tier |
| `BlazorAdmin/BlazorAdmin.csproj` | Admin GUI |

```xml
<Version>2.15.1.0</Version>
<AssemblyVersion>2.15.1.0</AssemblyVersion>
<FileVersion>2.15.1.0</FileVersion>
```

> **De Postgres-csproj wordt structureel vergeten.** Een wijziging die alleen die tier raakt, raakt
> geen van de andere twee bestanden — en niets waarschuwt ervoor. Dat ging mis bij #859, #952 en
> #939 (alle drie gecorrigeerd). Controleer bij twijfel het veld `version` in de respons van
> `GET /api/health`: dat komt van de tier die daadwerkelijk draait.

Verifieer in één regel dat alle drie gelijk staan:

```bash
grep -rn "<Version>" --include='*.csproj' .
```

Dit getal wordt via `Assembly.GetExecutingAssembly().GetName().Version?.ToString(4)` getoond in de header.

### Twijfelgevallen

**"Is dit een bug of een feature?"**
→ Was het gedrag ooit zo bedoeld? JA = bug (REVISION). NEEN (het werkte nooit anders) = feature (PATCH).

**"Is dit PATCH of REVISION tijdens development?"**
→ Voegt het iets toe aan wat de gebruiker kan? JA = PATCH. Alleen repareren/verfijnen = REVISION.

**"Is dit MAJOR?"**
→ Alleen als een bestaande gebruiker iets moet aanpassen (API, config, workflow) om te kunnen blijven werken na de update.

---

## 6b. Releaseprocedure — van `[Unreleased]` naar een getagde release

De stappen hieronder in deze volgorde. Stap 2 is de voorwaarde waaronder `release.yml` überhaupt
release-notes vindt.

1. **Bepaal het nieuwe nummer** volgens Fase 2 hierboven, op basis van de inhoud van
   `## [Unreleased]`.
2. **Verplaats alles** van `## [Unreleased]` naar een nieuwe kop `## [x.y.z.r] — YYYY-MM-DD` en zet
   een lege `## [Unreleased]` terug bovenaan.
   > `release.yml` haalt de release-notes letterlijk uit die sectiekop
   > (`awk "/^## \[${VERSION}\]/…"`), waarbij `VERSION` de tag zonder `v` is. Wijkt de kop af van
   > de tag, dan komt er een lege release uit met de melding "Geen entry voor versie … gevonden".
3. **Bump de drie csproj's** (zie [In de csproj](#in-de-csproj--er-zijn-er-drie-niet-twee)).
4. **PR `develop` → `main`.** Daarop draaien `pre-release-check.yml` en
   `pre-release-db-check.yml`; beide moeten groen zijn.
5. **Na de merge: tag aanmaken op `main`.**
   ```powershell
   git checkout main && git pull
   git tag vX.Y.Z.R -m "Release vX.Y.Z.R"
   git push origin vX.Y.Z.R
   ```
   Die tag triggert twee workflows tegelijk:
   - `release.yml` — maakt de GitHub Release aan met de notes uit de CHANGELOG-sectie.
   - `close-released-issues.yml` — sluit de issues uit die sectie (en uit de commit-subjects sinds
     de vorige tag) en verwijdert hun label `status: awaiting-release`.
6. **Controleer de deploy per job** en doe de live browser-rendercheck op de Admin GUI — zie de
   veiligheidsregels in `CLAUDE.md`. Een groene workflow bewijst niet dat de GUI rendert.

Alternatief voor stap 5: `release.yml` handmatig starten via *Actions → Release aanmaken → Run
workflow* met het versienummer als input.

---

## 7. Changelog-stijlgids

### Schrijf voor de beheerder, niet de developer

| ❌ Niet | ✅ Wel |
|---|---|
| `EmailProcessorFunction: NormaliseerTeamNaam gebruikt nu clubCode` | `Teamnamen worden nu correct genormaliseerd op basis van de clubCode uit AppSettings` |
| `Refactor: kanaal-agnostische BerichtPipeline` | (niet in changelog — intern) |
| `fix: WaitForDatabaseAsync roept LoadSettingsAsync aan` | `Admin-endpoints gaven 500 bij opstart omdat instellingen niet geladen waren; opgelost` |
| `feat: EmailVoetnoot NVARCHAR(MAX) in AppSettings` | `Beheerders kunnen nu een gedeelde voettekst instellen die automatisch onder alle uitgaande e-mails wordt geplaatst` |

### Format per entry

```markdown
- **Korte omschrijving** — optionele toelichting voor wie niet meteen snapt waarom.
  Nooit een technische "hoe", wel de "wat" en "waarom" voor de lezer.
```

### Issue-nummers in het changelog — verplicht, in één vaste notatie

> **Let op: dit is precies omgekeerd aan wat hier tot #1269 stond.** De release-automatisering
> *leest* het changelog; issue-nummers weglaten laat issues permanent openstaan.

`.github/workflows/close-released-issues.yml` sluit bij een release-tag de issues die in de
CHANGELOG-sectie van díe versie staan. Het pakt daarvoor elke haakjesgroep die **uitsluitend**
issuenummers bevat — `(#574)` of `(#599, #595)`. Inline code-spans worden eerst weggestreept
(#1179), zodat een entry die de conventie zélf uitlegt geen issues sluit uit zijn eigen
voorbeeldtekst.

| Bedoeling | Notatie | Effect bij de release |
|---|---|---|
| **Attributie van opgeleverd werk** | `(#574)` of `(#599, #595)`, achter de omschrijving | Issue wordt gesloten, label `status: awaiting-release` verwijderd |
| **Kruisverwijzing naar vervolgwerk** | in proza: `zie issue #739` | Geen effect — het issue blijft open |
| Nummer in de titel zelf | ❌ `Fixed #116 SWA route mismatch` | Onleesbaar voor de beheerder; niet doen |

De tekst blijft dus mensvriendelijk; het nummer staat **erachter**, niet ervoor:

- ❌ `Fixed #116 SWA route mismatch`
- ✅ `Toegangsbeheer via SWA-routing werkt nu correct voor beveiligde schermen (#116)`

> **Gebruik `(#N)` alleen voor werk dat écht in díe versie zit.** Bij v2.18.0.1 stonden drie
> vervolgissues die juist bij die release waren *aangemaakt* (#734, #739, #740) tussen haakjes; de
> release sloot ze en ze moesten met de hand worden heropend. Een vervolgpunt noem je in proza.

---

_Dit document wordt beheerd door de architect/developer (Claude Code).
Vragen of aanpassingen: maak een GitHub Issue of bespreek in de PR._
