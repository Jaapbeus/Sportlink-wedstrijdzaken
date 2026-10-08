# Documentatieplan — Sportlink Wedstrijdzaken

Dit bestand definieert hoe documentatie wordt ingedeeld, bijgehouden en geverifieerd:
de categorieregels, de updateregels en de stappen bij een nieuw document.

> **Verhouding tot [INDEX.md](INDEX.md).** De index is de leeslijst voor wie iets zoekt; dit plan
> is de regelset voor wie iets toevoegt. Beide bevatten dezelfde bestandenlijst, en die is in de
> praktijk uit elkaar gelopen — op 2026-09-19 miste dit plan elf documenten en de index acht.
> Bij elke toevoeging, hernoeming of verwijdering in `docs/` gaan beide in dezelfde PR mee.

---

## Categorieën

### 1. Gebruikers — dagelijks gebruik
Handleidingen voor de eindgebruiker van de Wedstrijdzaken-app (beheerder van de vereniging).
Toon wat er te doen is, niet hoe het technisch werkt.

**Doelgroep:** coördinatoren, secretarissen, bestuurleden die de Admin GUI gebruiken.

### 2. Administrator — import, export en instellingen
Procesbeschrijvingen voor terugkerende beheertaken buiten de GUI om: data importeren,
exporteren, alerts instellen, testmodus gebruiken.

**Doelgroep:** de beheerder die iets inricht of exporteert, niet puur de dagelijkse gebruiker.

### 3. Developers — architectuur, debuggen, API en specs
Technische documentatie voor bijdragers aan de codebase. Alles wat je nodig hebt om
te begrijpen hoe het systeem werkt, lokaal te draaien, te testen en te releasen.

**Doelgroep:** developers (intern en contributors via fork).

### 4. Setup — eenmalige inrichting
Stap-voor-stap handleidingen voor de eerste opzet van lokale ontwikkelomgeving én
Azure-productieomgeving. Eenmalig uit te voeren, daarna niet meer dagelijks nodig.

**Doelgroep:** nieuwe developers en clubbeheerders die de app voor het eerst inrichten.

---

## Bestandsindeling — welk bestand valt waar?

### Gebruikers

| Bestand | Onderwerp |
|---------|-----------|
| [BEHEERDER-HANDLEIDING.md](BEHEERDER-HANDLEIDING.md) | Dagelijks gebruik Admin GUI: instellingen, templates, planner, e-maillog |
| [QUICK-REFERENCE.md](QUICK-REFERENCE.md) | Veelgebruikte commando's en snippets voor dagelijks beheer |

### Administrator

| Bestand | Onderwerp |
|---------|-----------|
| [ADMIN-TEAMBEGELEIDING-IMPORT.md](ADMIN-TEAMBEGELEIDING-IMPORT.md) | AVG-veilig exporteren van teambegeleidersgegevens uit Sportlink |
| [TESTMODUS-ALLSTARS.md](TESTMODUS-ALLSTARS.md) | ALLSTARS-testmodus: activeren, fictieve wedstrijden, planner testen |
| [MONITORING.md](MONITORING.md) | Resource Health Alerts, KQL-queries, escalatiematrix — beheer van alerts |
| [knvb-speeldagenkalenders/README.md](knvb-speeldagenkalenders/README.md) | Importeren van KNVB-speeldagenkalenders |

### Developers

| Bestand | Onderwerp |
|---------|-----------|
| [ARCHITECTUUR.md](ARCHITECTUUR.md) | **Het enige, leidende architectuurdocument** (sinds #1291). ISO 42010 / arc42: kwaliteitsdoelen, besluiten, concrete uitwerkingen en toetsregister met externe basis en bewijsvorm |
| [ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md) | Multi-tier strategie, bouwvolgorde, casing-conventie, RLS, sub-issue-index |
| [ARCHITECTUUR-CODEKWALITEIT.md](ARCHITECTUUR-CODEKWALITEIT.md) | Codekwaliteitsregels met CI-guard en plafond per regel |
| [ARCHITECTUUR-AI-SERVICES.md](ARCHITECTUUR-AI-SERVICES.md) | Provider-agnostisch AI-ontwerp, datumregel, few-shot conventies |
| [ARCHITECTUUR-TEAMRESOLUTIE.md](ARCHITECTUUR-TEAMRESOLUTIE.md) | Teamnaam-normalisatie, aliassen, disambiguatie |
| [ARCHITECTUUR-PDF-EXPORT.md](ARCHITECTUUR-PDF-EXPORT.md) | PDF-export met QuestPDF: licentie, native assets, pakketgrootte |
| [ARCHITECTUUR-EMAIL-MODULE.md](ARCHITECTUUR-EMAIL-MODULE.md) | Doelarchitectuur e-mailverzendlaag — ontwerp, migratie nog niet gestart |
| [SPORTLINK-DATASERVICE.md](SPORTLINK-DATASERVICE.md) | Leesrichting van de Sportlink-dataservice: endpoints, sync-strategie, veldreferentie `/programma` |
| [SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md) | Schrijfrichting webapp → Sportlink Club: protocol, endpoints, agent-tokengrens |
| [FEEDBACK.md](FEEDBACK.md) | Feedbackwidget en -overzicht: rollen, publicatiebeleid, technische context en redactie, bewaartermijnen, verwerkingsregister (#764) |
| [SPORTLINK-AUTOLOGIN.md](SPORTLINK-AUTOLOGIN.md) | Automatische Sportlink-login (#1411): implementatie, productie-setup, TOTP-verwerking |
| [ARCHITECTUUR-PLANNER.md](ARCHITECTUUR-PLANNER.md) | Planner API: algoritme, velddefinities, API-contract |
| [API.md](API.md) | Alle HTTP-endpoints: routes, parameters, response-formaten |
| [api-standaarden/openapi.yaml](api-standaarden/openapi.yaml) | Machine-readable OpenAPI 3.0 spec — bewaakt op actualiteit via AGENTS.md |
| [api-standaarden/openapi.json](api-standaarden/openapi.json) | Zelfde spec in JSON-formaat |
| [EMAIL-VERWERKING.md](EMAIL-VERWERKING.md) | E-mailpipeline, AI-classificatie, templates, kanaalstrategie |
| [VERSIONING.md](VERSIONING.md) | Semver-regels, conventional commits, release-workflow, CHANGELOG-richtlijnen |
| [VERIFICATIE-SCRIPTS.md](VERIFICATIE-SCRIPTS.md) | Test-App.ps1: schema-controle, endpoint-verificatie, Blazor-pagina's |
| [SERENA-VS-GRAFT-BENCHMARK.md](SERENA-VS-GRAFT-BENCHMARK.md) | Context-ophaal workflows: waarom dit project Serena gebruikt, waar Graft sterker is |
| [LOKAAL-DEBUGGEN.md](LOKAAL-DEBUGGEN.md) | Services starten, poorten, Azurite, func start, hot-reload |
| [DOSSIER-AGENTINSTRUCTIES-BUDGET.md](DOSSIER-AGENTINSTRUCTIES-BUDGET.md) | Gedateerd werkdossier #1580 — budgetmeting, regel→bestemming-matrix, verificatiebewijs; niet meer actief bijgewerkt |
| [DOSSIER-SPEELTIJDEN-INLINE-FORMULIER.md](DOSSIER-SPEELTIJDEN-INLINE-FORMULIER.md) | Gedateerd werkjournaal #1552/#1553/#1554 — besluiten, verificatiebewijs, reviewafhandeling; niet naar het heden bijgewerkt |
| [SPORTLINK-CLUB-SCHERMEN-ANALYSE.md](SPORTLINK-CLUB-SCHERMEN-ANALYSE.md) | Analyse van Sportlink Club-schermen en beschikbare datavelden |
| [ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) | Bronrapport netwerktraces en endpoint-contracten — onderzoek, niet meer actief bijgewerkt |
| [ARCHITECTUUR-SQLITE-TIER.md](ARCHITECTUUR-SQLITE-TIER.md) | Tier 3 — voorbereidend ontwerp, nog niet gebouwd |
| [ARCHITECTUUR-COSMOSDB-EMAILLOG.md](ARCHITECTUUR-COSMOSDB-EMAILLOG.md) | Tier 4, alleen het e-mailverwerkingslog — ontwerp + kostenverificatie, nog niet gebouwd |

### Setup

| Bestand | Onderwerp |
|---------|-----------|
| [DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) | Developer lokale setup: .NET, Docker (Postgres/SQL Server), Azurite, GitHub Actions — Windows én macOS |
| [SETUP-CHECKLIST.md](SETUP-CHECKLIST.md) | Snelle checklist voor eerste opzet, beide databasetiers |
| [ENTRA-AUTH-BEHEER.md](ENTRA-AUTH-BEHEER.md) | Entra App Registration, Easy Auth, rollen — configure via scripts |
| [CUSTOM-DOMAIN.md](CUSTOM-DOMAIN.md) | Eigen domein op de Static Web App, CORS-origins, redirect-URI's |
| [RUNBOOK-FLEX-MIGRATIE.md](RUNBOOK-FLEX-MIGRATIE.md) | Eenmalig draaiboek Flex Consumption-migratie (epic #1063); vervalt na FLEX-13 |

### Navigatie

| Bestand | Status |
|---------|--------|
| [INDEX.md](INDEX.md) | Inhoudsopgave — blijft in de `docs/`-root als navigatiepunt |
| `DOCUMENTATIEPLAN.md` | Dit bestand — blijft in de `docs/`-root |

> **Geen archiefcategorie meer (#1291-vervolg).** `ARCHITECTURE-V2.md` — de gearchiveerde,
> single-tier architectuur van vóór de Postgres-cutover (#976) — is verwijderd. De repository houdt
> alleen geldende regels vast; een bevroren historisch verslag hoort niet in de codebase maar in de
> git-geschiedenis van `docs/ARCHITECTUUR.md` zelf.

---

## Versie-verificatieconventie

Elk documentatiebestand krijgt onderaan een versie-footer:

```markdown
---
*Laatste verificatie: vX.Y.Z — YYYY-MM-DD*
```

**Wanneer bijwerken:** als er in een release wijzigingen zijn in het domein dat dit
bestand beschrijft. Niet bij elke commit — alleen bij inhoudelijke wijzigingen.

**Eén format, geen HTML-comment.** De zichtbare cursieve regel hierboven is de enige
toegestane vorm.

**Hoe controleren of een doc verouderd is:**
```powershell
# Grep op "Laatste verificatie" → vergelijk met huidige versie
Select-String -Path "docs/*.md" -Pattern "Laatste verificatie"
```
```bash
git log -1 --format=%ad -- docs/<bestand>.md   # werkt altijd, ook zonder footer
```

> **Deze conventie wordt nauwelijks gevolgd: 2 van de 29 documenten in `docs/` dragen de footer.**
> Een marker die 27 keer ontbreekt geeft geen betrouwbaar signaal over actualiteit; `git log` wel.
> Of de conventie wordt afgedwongen via de AGENTS.md Stap 2b-checklist, óf ze wordt geschrapt —
> dat is een openstaand besluit voor de eigenaar.

---

## Updateregels per categorie

| Categorie | Bijwerken bij |
|-----------|--------------|
| **Gebruikers** | Schermwijziging, nieuwe knop, gewijzigde workflow in Admin GUI |
| **Administrator** | Nieuw export-formaat, gewijzigde alert-configuratie, nieuw beheerproces |
| **Developers** | Nieuw endpoint, gewijzigde architectuur, nieuw algoritme, gewijzigde buildstap |
| **Setup** | Nieuwe prerequisite, gewijzigde GitHub secret/variable, configuratiestap gewijzigd |

### Updateregels per document (de documentatiechecklist vóór elke commit)

Na elke wijziging in architectuur, setup of GUI-functionaliteit loop je onderstaande documenten na en
werk je bij wat verouderd of onvolledig is. Niet alles hoeft altijd te wijzigen, maar elk document moet
bewust worden bekeken: lees het relevante bestand, vergelijk het met de gemaakte wijziging en update
wat niet meer klopt. Verouderde informatie is erger dan geen documentatie — ze misleidt toekomstige
sessies. Dit is de bron van de checklist; `AGENTS.md` (Stap 2b) bevat alleen de plicht en verwijst
hierheen. (Verplaatst uit `AGENTS.md`, #1580.)

| Documentatiebestand | Bijwerken bij |
|---|---|
| `AGENTS.md` | Buildproces, git-workflow, statuslabels of een agentinstructie gewijzigd (géén architectuurregel — zie §13.1 van ARCHITECTUUR.md) |
| `docs/ARCHITECTUUR.md` | Kwaliteitsdoel, randvoorwaarde, architectuurbesluit, of een systeembrede regel (auth, UTC, ClubCode, secrets, CI/CD) gewijzigd |
| `FunctionApp/AGENTS.md` | Endpoint, datamodel, API-veld of FunctionApp-configuratie gewijzigd |
| `docs/ARCHITECTUUR-PLANNER.md` | Planner-logica, pipeline of kanaalstrategie gewijzigd |
| `docs/ENTRA-AUTH-BEHEER.md` | Auth-configuratie, Easy Auth, Entra App Registration of rollen gewijzigd |
| `docs/CUSTOM-DOMAIN.md` | Eigen domein, SWA-hostnames, CORS-origins of redirect-URI's gewijzigd |
| `docs/BEHEERDER-HANDLEIDING.md` | Admin GUI: scherm, instelling, knop of workflow gewijzigd |
| `docs/VERSIONING.md` | Release-proces of semver-afspraken gewijzigd |
| `docs/API.md` | Endpoint toegevoegd, gewijzigd of verwijderd |
| `docs/api-standaarden/openapi.yaml` | Endpoint toegevoegd, gewijzigd of verwijderd (sync met API.md) |
| `docs/EMAIL-VERWERKING.md` | Email-pipeline, kanalen of AI-verwerking gewijzigd |
| `docs/ARCHITECTUUR-TEAMRESOLUTIE.md` | Teamnaam-normalisatie, `dbo.Teams`/`dbo.TeamAliassen`, disambiguatie of teamherkenning gewijzigd |
| `docs/ARCHITECTUUR-EMAIL-MODULE.md` | E-mail-verzendlaag, afzenderstrategie, ontvangerresolutie of e-mail-loggingschema gewijzigd |
| `docs/ARCHITECTUUR-DATABASE-TIERS.md` | Tier-keuze, bouwvolgorde, casing-conventie of nieuwe tier-implementatie gewijzigd |
| `docs/ARCHITECTUUR-CODEKWALITEIT.md` | Codekwaliteitsregel, guard, plafond of allowlist-uitzondering gewijzigd; nieuwe harde regel toegevoegd |
| `docs/ARCHITECTUUR-PDF-EXPORT.md` | PDF-export (QuestPDF) in `Planner.Shared/Deel/`: licentie, native assets, pakketgrootte of platformbewijs gewijzigd |
| `docs/RUNBOOK-FLEX-MIGRATIE.md` | Flex Consumption-migratie (epic #1063): volgorde, kostencontroles, cutover of rollback gewijzigd |
| `docs/SPORTLINK-DATASERVICE.md` | Endpoints, synchronisatiestrategie of veldreferentie van de Sportlink-dataservice (leesrichting) gewijzigd |
| `docs/SPORTLINK-WEB-EXTENSION.md` | Sportlink Web Extension (epic #986): rol/serviceaccount-koppeling, auth-flow of de regel dat agents dit mechanisme nooit zelf mogen uitvoeren gewijzigd |
| `docs/VERIFICATIE-SCRIPTS.md` | Testscript, schema-controle of endpoint-verificatie gewijzigd |
| `docs/MONITORING.md` | Alerting-drempelwaarden, KQL-queries of escalatiematrix gewijzigd |
| `docs/DEVELOPER-SETUP.md` | Lokale setup of configuratiestappen gewijzigd |
| `CLAUDE.md`, `FunctionApp/CLAUDE.md` | **Nooit inhoud toevoegen** — stubs met `@AGENTS.md`; `check-agent-instructies.py` bewaakt dit |
| `docs/INDEX.md` | **Altijd bij een nieuw, hernoemd of verwijderd document in `docs/`** — de index is de wegwijzer; een ontbrekend document is onvindbaar |
| `docs/DOCUMENTATIEPLAN.md` | Idem: categorie-indeling of documentatieregels gewijzigd |
| `CHANGELOG.md` | **Altijd** — elke feature of fix krijgt een entry onder `[Unreleased]` |
| `README.md` | Publieke beschrijving, architectuuroverzicht of quick-start gewijzigd |
| `SECURITY.md` | Security-beleid, AVG-regels of secrets-protocol gewijzigd |
| `.github/workflows/*.yml` | Branch-strategie, PR-doelbranches of verplichte status checks gewijzigd → controleer élke `pull_request`-/`push`-trigger (zie issue #1202) |

Naast de categorieën hierboven en de tabel: wijzig je een `.github/workflows/*.yml`, controleer dan
**élke** `pull_request`- en `push`-trigger (zie issue #1202) — een branch-strategiewijziging laat
anders een workflow stilzwijgend op de verkeerde branch draaien.

---

## Nieuwe bestanden aanmaken

1. Kies de categorie op basis van doelgroep (zie boven)
2. Gebruik kebab-case, Nederlandstalig, beschrijvend: `gebruikers-teambegeleiding.md`
3. Voeg toe aan `INDEX.md` in de juiste sectie
4. Voeg toe aan de tabel in dit bestand
5. Voeg onderaan de zichtbare regel `*Laatste verificatie: vX.Y.Z — YYYY-MM-DD*` toe (één format,
   geen HTML-comment) — zolang de conventie in stand blijft, zie de kanttekening hierboven

---

*Laatste verificatie: v3.5.3.1 — 2026-09-19 (bestandenlijst gelijkgetrokken met `docs/`;
DEVELOPER-SETUP-TODO afgevinkt)*
