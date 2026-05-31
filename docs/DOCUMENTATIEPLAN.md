# Documentatieplan — Sportlink Wedstrijdzaken

Dit bestand definieert hoe documentatie wordt ingedeeld, bijgehouden en geverifieerd.
Het is de enige bron van waarheid voor de documentatiestructuur.

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
| [ARCHITECTURE.md](ARCHITECTURE.md) | Architectuurprincipes: multi-club, ClubCode, kanaalstrategie |
| [ARCHITECTURE-PLANNER.md](ARCHITECTURE-PLANNER.md) | Planner API: algoritme, velddefinities, API-contract |
| [API.md](API.md) | Alle HTTP-endpoints: routes, parameters, response-formaten |
| [api-standaarden/openapi.yaml](api-standaarden/openapi.yaml) | Machine-readable OpenAPI 3.0 spec — bewaakt op actualiteit via CLAUDE.md |
| [api-standaarden/openapi.json](api-standaarden/openapi.json) | Zelfde spec in JSON-formaat |
| [api-standaarden/openspec/](api-standaarden/openspec/config.yaml) | Structured requirements specs per domein |
| [EMAIL-VERWERKING.md](EMAIL-VERWERKING.md) | E-mailpipeline, AI-classificatie, templates, kanaalstrategie |
| [VERSIONING.md](VERSIONING.md) | Semver-regels, conventional commits, release-workflow, CHANGELOG-richtlijnen |
| [VERIFICATIE-SCRIPTS.md](VERIFICATIE-SCRIPTS.md) | Test-App.ps1: schema-controle, endpoint-verificatie, Blazor-pagina's |
| [LOKAAL-DEBUGGEN.md](LOKAAL-DEBUGGEN.md) | Services starten, poorten, Azurite, func start, hot-reload |
| [SPORTLINK-CLUB-SCHERMEN-ANALYSE.md](SPORTLINK-CLUB-SCHERMEN-ANALYSE.md) | Analyse van Sportlink Club-schermen en beschikbare datavelden |

### Setup

| Bestand | Onderwerp |
|---------|-----------|
| [DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) | Developer lokale setup: .NET, SQL, Azurite, GitHub Actions (⚠️ herschrijving nodig voor v2.7) |
| [SETUP-CHECKLIST.md](SETUP-CHECKLIST.md) | Snelle checklist voor eerste opzet |
| [ENTRA-AUTH-BEHEER.md](ENTRA-AUTH-BEHEER.md) | Entra App Registration, Easy Auth, rollen — configure via scripts |

### Ongecategoriseerd (root — ter review)

Bestanden die hieronder staan zijn nog niet ingedeeld. Review ze en verplaats naar
een categorie of verwijder ze als ze verouderd zijn.

| Bestand | Status |
|---------|--------|
| [INDEX.md](INDEX.md) | Inhoudsopgave — blijft in root als navigatiepunt |
| `DOCUMENTATIEPLAN.md` | Dit bestand — blijft in root |

---

## Versie-verificatieconventie

Elk documentatiebestand krijgt onderaan een versie-footer:

```markdown
---
*Laatste verificatie: vX.Y.Z — YYYY-MM-DD*
```

**Wanneer bijwerken:** als er in een release wijzigingen zijn in het domein dat dit
bestand beschrijft. Niet bij elke commit — alleen bij inhoudelijke wijzigingen.

**Hoe controleren of een doc verouderd is:**
```powershell
# Grep op "Laatste verificatie" → vergelijk met huidige versie
Select-String -Path "docs\*.md" -Pattern "Laatste verificatie"
```

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
5. Voeg `<!-- Laatste verificatie: vX.Y.Z — datum -->` toe onderaan

---

*Laatste verificatie: v2.7.0.1 — 2026-05-31*
