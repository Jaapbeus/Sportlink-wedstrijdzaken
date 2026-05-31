# Documentatie — Sportlink Wedstrijdzaken

Centrale inhoudsopgave. Structuur en categorieregels: zie [DOCUMENTATIEPLAN.md](DOCUMENTATIEPLAN.md).

---

## 1. Gebruikers — dagelijks gebruik

Voor de beheerder die dagelijks met de Admin GUI werkt.

| Document | Inhoud |
|----------|--------|
| [Beheerder handleiding](BEHEERDER-HANDLEIDING.md) | Stap-voor-stap: instellingen, templates, veldplanner, e-maillog, teambegeleiding |
| [Quick reference](QUICK-REFERENCE.md) | Veelgebruikte commando's en snippets |

---

## 2. Administrator — import, export en instellingen

Voor de beheerder die beheertaken uitvoert buiten de dagelijkse GUI-flow.

| Document | Inhoud |
|----------|--------|
| [Teambegeleiding import](ADMIN-TEAMBEGELEIDING-IMPORT.md) | AVG-veilig exporteren uit Sportlink en importeren in SQL |
| [Testmodus — ALLSTARS](TESTMODUS-ALLSTARS.md) | Fictieve wedstrijden invoeren, planner testen zonder echte data |
| [Monitoring & alerts](MONITORING.md) | Resource Health Alerts, KQL-queries, escalatiematrix |
| [KNVB speeldagenkalenders](knvb-speeldagenkalenders/README.md) | KNVB-speeldagenkalenders importeren |

---

## 3. Developers — architectuur, debuggen, API en specs

Voor bijdragers aan de codebase.

| Document | Inhoud |
|----------|--------|
| [Architectuurprincipes](ARCHITECTURE.md) | Multi-club, ClubCode, kanaalstrategie, security-lagen |
| [AI-services architectuur](ARCHITECTUUR-AI-SERVICES.md) | Provider-agnostisch ontwerp, datumregel, few-shot conventies, IChatClient |
| [Planner architectuur](ARCHITECTURE-PLANNER.md) | Algoritme, velddefinities, API-contract veldplanner |
| [API referentie](API.md) | Alle HTTP-endpoints: routes, parameters, response-formaten |
| [OpenAPI spec (YAML)](api-standaarden/openapi.yaml) | Machine-readable OpenAPI 3.0 spec — **altijd bijhouden bij endpoint-wijziging** |
| [OpenAPI spec (JSON)](api-standaarden/openapi.json) | Zelfde spec in JSON-formaat — sync met YAML |
| [Structured specs (openspec)](api-standaarden/openspec/config.yaml) | Machine-readable requirements per domein |
| [E-mailverwerking](EMAIL-VERWERKING.md) | Pipeline, AI-classificatie, templates, kanaalstrategie |
| [Versioning & CHANGELOG](VERSIONING.md) | Semver-regels, conventional commits, release-workflow |
| [Verificatie-scripts](VERIFICATIE-SCRIPTS.md) | Test-App.ps1 + Start-Debug.ps1: schema-controle, endpoints, Blazor-pagina's — voor developers |
| [Lokaal debuggen](LOKAAL-DEBUGGEN.md) | Services starten, poorten, hot-reload, func start |
| [Sportlink schermen-analyse](SPORTLINK-CLUB-SCHERMEN-ANALYSE.md) | Beschikbare datavelden in Sportlink Club-interface |

---

## 4. Setup — eenmalige inrichting

Voor nieuwe clubs en developers die de app voor het eerst inrichten.

| Document | Inhoud |
|----------|--------|
| [Nieuwe club — Azure setup](../SETUP-NIEUWE-CLUB.md) | Fork, Azure aanmaken, Entra configureren, eerste deployment — voor club-beheerders |
| [Developer setup](DEVELOPER-SETUP.md) | .NET, SQL, Azurite, GitHub Actions — lokale ontwikkelomgeving ⚠️ herschrijving gepland |
| [Setup checklist](SETUP-CHECKLIST.md) | Snelle checklist voor eerste opzet ⚠️ herschrijving gepland |
| [Entra auth & beheer](ENTRA-AUTH-BEHEER.md) | App Registration, Easy Auth, rollen, gebruikers toevoegen — via scripts |

---

## Projectdocumentatie (repo-root)

| Document | Inhoud |
|----------|--------|
| [README](../README.md) | Projectoverzicht, quick start, architectuurdiagram |
| [CHANGELOG](../CHANGELOG.md) | Versiehistorie — alle features en fixes per release |
| [SECURITY](../SECURITY.md) | Security-beleid, AVG-regels, secrets-protocol |
| [CLAUDE.md](../CLAUDE.md) | Instructies voor Claude Code — architectuurregels, buildproces |

---

*Structuur gedefinieerd in [DOCUMENTATIEPLAN.md](DOCUMENTATIEPLAN.md) · Bijhoudconventie: zie CLAUDE.md Stap 2b*
