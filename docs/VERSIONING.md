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
| Breaking change in API of schema | ✅ Altijd — ook als klein |
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
die gebruikers troffen. Een fix van `.NET 9.0.x → 10.0.x` in deploy.yml terwijl de
productie al op .NET 10 staat: ❌ niet in changelog (interne infrastructuur-correctie).

**Performance-verbetering** — alleen in changelog als de verbetering merkbaar is
voor de gebruiker (bijv. "laadtijd overview 60% sneller"). Micro-optimalisaties: ❌.

**Hernoemen van routes** — `admin→beheer` is een Breaking Change voor integrerende
partijen, dus ✅ in changelog onder `Changed`.

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
| `teamnaam LIKE 'VRC%'` filter in teams-query | Hardcoded clubnaam; voor andere clubs werkt het niet. Effect: architectuurschending + functionele fout. |

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

### De "issue"-terminologie van de developer

De gebruiker noemde het zelf al: *"Een onterechte melding of verkeerde terugkoppeling
is een issue die een fix opleverde"*. Precies — dat is de `By Design` of `Documentation`
categorie. In het changelog verschijnt dit **niet**, want de applicatie veranderde niet.

In de commit-message: `fix(test): Test-App.ps1 detecteerde blazor-error-ui false positive`
In CHANGELOG: niets — de applicatie was correct.

---

## 5. Wat is een Feature?

### Nieuwe Feature (MINOR bump: 2.0.0 → 2.1.0)

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

### Feature-uitbreiding / Enhancement (MINOR of PATCH)

> Een bestaande feature krijgt **extra opties, velden of gedrag**.
> De basiswerking bestond al; er wordt iets aan toegevoegd.

Kenmerken:
- Bestaand formulier/endpoint krijgt extra veld(en)
- Bestaande berekening wordt uitgebreid met een nieuw scenario
- Iemand die de applicatie al kent zegt: "Oh, dit kon ik nog niet maar het past erbij"

Versie-impact:
- Uitbreiding zonder breaking change → MINOR (2.0.0 → 2.1.0)
- Uitbreiding die bestaande gedrag vervangt → MINOR
- Kleine uitbreiding die onderdeel is van een bugfix → PATCH

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
- `?? "VRC"` → `?? throw InvalidOperationException` — architectuurkeuze aangescherpt

---

## 6. Versie-bump beslisboom

Het versienummer heeft **vier** cijfers: `MAJOR.MINOR.PATCH.REVISION`

| Getal | Wanneer omhoog | Reset bij |
|---|---|---|
| **MAJOR** | Breaking change voor de gebruiker | — |
| **MINOR** | Nieuwe feature | MAJOR-bump → 0 |
| **PATCH** | Bugfix of security-patch | MINOR-bump → 0 |
| **REVISION** | **Elke commit die gebruikerszichtbare bestanden raakt** (.razor, .cs, .css, .sql, .json in wwwroot) | PATCH-bump → 0 |

> **Waarom Revision?** De beheerder ziet het versienummer in de header (bijv. `v2.5.0.1`).
> Na een deployment kan de beheerder bevestigen dat de juiste versie actief is door het getal te checken.
> Zonder Revision ziet elke kleine fix er hetzelfde uit — geen bevestiging mogelijk.

```
Wat is er gewijzigd?
│
├── Verwijdert of breekt bestaande functionaliteit voor een gebruiker?
│   └── JA → MAJOR (x.0.0.0)
│
├── Voegt nieuwe gebruikersfunctionaliteit toe?
│   └── JA → MINOR (2.x.0.0)
│
├── Repareert iets wat verkeerd werkte voor een gebruiker?
│   └── JA → PATCH (2.0.x.0)
│
├── Beveiligingspatch?
│   └── JA → PATCH (2.0.x.0) — tenzij breaking → MAJOR
│
├── Kleine fix, CSS, UX-verbetering of chore met zichtbaar effect?
│   └── JA → REVISION (2.0.0.x)
│
└── Alleen intern (refactor zonder effect, docs, tooling, CLAUDE.md)?
    └── Geen versie-bump
```

### In de csproj

Zet alle drie velden synchroon op het volledige 4-cijferige nummer:
```xml
<Version>2.5.0.1</Version>
<AssemblyVersion>2.5.0.1</AssemblyVersion>
<FileVersion>2.5.0.1</FileVersion>
```

Dit getal wordt via `Assembly.GetExecutingAssembly().GetName().Version?.ToString(4)` getoond in de header.

### Twijfelgevallen

**"Is dit een bug of een feature?"**
→ Was het gedrag ooit zo bedoeld? JA = bug. NEEN (het werkte nooit anders) = feature.

**"Is dit MINOR of PATCH?"**
→ Voegt het iets toe aan wat de gebruiker kan? JA = MINOR. Alleen repareren = PATCH.

**"Is dit MAJOR?"**
→ Alleen als een bestaande gebruiker iets moet aanpassen (API, config, workflow) om te kunnen blijven werken na de update.

---

## 7. Changelog-stijlgids

### Schrijf voor de beheerder, niet de developer

| ❌ Niet | ✅ Wel |
|---|---|
| `EmailProcessorFunction: NormaliseerTeamNaam gebruikt nu clubCode` | `Teamnamen worden nu correct genormaliseerd voor clubs met een andere code dan VRC` |
| `Refactor: kanaal-agnostische BerichtPipeline` | (niet in changelog — intern) |
| `fix: WaitForDatabaseAsync roept LoadSettingsAsync aan` | `Admin-endpoints gaven 500 bij opstart omdat instellingen niet geladen waren; opgelost` |
| `feat: EmailVoetnoot NVARCHAR(MAX) in AppSettings` | `Beheerders kunnen nu een gedeelde voettekst instellen die automatisch onder alle uitgaande e-mails wordt geplaatst` |

### Format per entry

```markdown
- **Korte omschrijving** — optionele toelichting voor wie niet meteen snapt waarom.
  Nooit een technische "hoe", wel de "wat" en "waarom" voor de lezer.
```

### Geen GitHub issue-nummers in changelog

Issue-nummers horen in de commit-body, niet in het changelog. Het changelog is
een mensvriendelijk document, geen ticket-tracker.

- ❌ `Fixed #116 SWA route mismatch`
- ✅ `Toegangsbeheer via SWA-routing werkt nu correct voor beveiligde schermen`

---

_Dit document wordt beheerd door de architect/developer (Claude Code).
Vragen of aanpassingen: maak een GitHub Issue of bespreek in de PR._
