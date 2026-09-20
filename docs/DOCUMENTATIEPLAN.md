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
| [ARCHITECTUUR-EMAIL-MODULE.md](ARCHITECTUUR-EMAIL-MODULE.md) | Doelarchitectuur e-mailverzendlaag — ontwerp, migratie nog niet gestart |
| [SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md) | Schrijfrichting webapp → Sportlink Club: protocol, endpoints, agent-tokengrens |
| [ARCHITECTUUR-PLANNER.md](ARCHITECTUUR-PLANNER.md) | Planner API: algoritme, velddefinities, API-contract |
| [API.md](API.md) | Alle HTTP-endpoints: routes, parameters, response-formaten |
| [api-standaarden/openapi.yaml](api-standaarden/openapi.yaml) | Machine-readable OpenAPI 3.0 spec — bewaakt op actualiteit via CLAUDE.md |
| [api-standaarden/openapi.json](api-standaarden/openapi.json) | Zelfde spec in JSON-formaat |
| [EMAIL-VERWERKING.md](EMAIL-VERWERKING.md) | E-mailpipeline, AI-classificatie, templates, kanaalstrategie |
| [VERSIONING.md](VERSIONING.md) | Semver-regels, conventional commits, release-workflow, CHANGELOG-richtlijnen |
| [VERIFICATIE-SCRIPTS.md](VERIFICATIE-SCRIPTS.md) | Test-App.ps1: schema-controle, endpoint-verificatie, Blazor-pagina's |
| [LOKAAL-DEBUGGEN.md](LOKAAL-DEBUGGEN.md) | Services starten, poorten, Azurite, func start, hot-reload |
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

### Navigatie en archief

| Bestand | Status |
|---------|--------|
| [INDEX.md](INDEX.md) | Inhoudsopgave — blijft in de `docs/`-root als navigatiepunt |
| `DOCUMENTATIEPLAN.md` | Dit bestand — blijft in de `docs/`-root |
| [ARCHITECTURE-V2.md](ARCHITECTURE-V2.md) | Gearchiveerd: de single-tier architectuur van 2026-05-17 tot de Postgres-cutover (#976). Niet meer bijwerken — ook de bestandsnaam niet, want dit is een bevroren historisch verslag, geen geldende regel |

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
> Of de conventie wordt afgedwongen via de CLAUDE.md Stap 2b-checklist, óf ze wordt geschrapt —
> dat is een openstaand besluit voor de eigenaar.

---

## Updateregels per categorie

| Categorie | Bijwerken bij |
|-----------|--------------|
| **Gebruikers** | Schermwijziging, nieuwe knop, gewijzigde workflow in Admin GUI |
| **Administrator** | Nieuw export-formaat, gewijzigde alert-configuratie, nieuw beheerproces |
| **Developers** | Nieuw endpoint, gewijzigde architectuur, nieuw algoritme, gewijzigde buildstap |
| **Setup** | Nieuwe prerequisite, gewijzigde GitHub secret/variable, configuratiestap gewijzigd |

Deze regels zijn **aanvullend op** CLAUDE.md Stap 2b (de volledige documentatiechecklist
per bestand). Dit plan beschrijft de structuur; Stap 2b beschrijft welk bestand bij
welke wijziging.

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
