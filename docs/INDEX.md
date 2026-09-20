# Documentatie — Sportlink Wedstrijdzaken

Centrale inhoudsopgave — elk document in `docs/` staat hier. Structuur en categorieregels: zie
[DOCUMENTATIEPLAN.md](DOCUMENTATIEPLAN.md).

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

Voor bijdragers aan de codebase. **Begin hier:** [ARCHITECTUUR.md](ARCHITECTUUR.md) — daarna het
document van het onderdeel waaraan je werkt.

**Architectuur — geldende regels**

| Document | Inhoud |
|----------|--------|
| **[Architectuurbeschrijving](ARCHITECTUUR.md)** | **Het enige, leidende architectuurdocument** (sinds #1291). Kwaliteitsdoelen, belanghebbenden, bouwblokken, concrete uitwerkingen (auth-lagen, UTC, secrets, CI/CD) en het toetsregister met per regel een externe basis en een bewijsvorm (ISO 42010 / arc42) |
| [Multi-tier databasestrategie](ARCHITECTUUR-DATABASE-TIERS.md) | Tierkeuze, bouwvolgorde, casing-conventie, RLS — gezaghebbende bron voor de tier-status |
| [Codekwaliteit](ARCHITECTUUR-CODEKWALITEIT.md) | De codekwaliteitsregels met een CI-guard en een plafond per regel |
| [AI-services architectuur](ARCHITECTUUR-AI-SERVICES.md) | Provider-agnostisch ontwerp, datumregel, few-shot conventies, IChatClient |
| [Planner architectuur](ARCHITECTUUR-PLANNER.md) | Algoritme, velddefinities, API-contract veldplanner |
| [Teamresolutie](ARCHITECTUUR-TEAMRESOLUTIE.md) | Teamnaam-normalisatie, aliassen, disambiguatie — één vertaalpunt |
| [E-mailverwerking](EMAIL-VERWERKING.md) | Pipeline, AI-classificatie, templates, kanaalstrategie |
| [E-mailmodule (doelarchitectuur)](ARCHITECTUUR-EMAIL-MODULE.md) | Verzendlaag, afzenderstrategie, e-maillogging — ontwerp, migratie nog niet gestart |
| [Sportlink Web Extension](SPORTLINK-WEB-EXTENSION.md) | Schrijfrichting webapp → Sportlink Club: protocol, endpoints, agent-tokengrens |

**API-contract**

| Document | Inhoud |
|----------|--------|
| [API referentie](API.md) | Alle HTTP-endpoints: routes, parameters, response-formaten |
| [OpenAPI spec (YAML)](api-standaarden/openapi.yaml) | Machine-readable OpenAPI 3.0 spec — **altijd bijhouden bij endpoint-wijziging** |
| [OpenAPI spec (JSON)](api-standaarden/openapi.json) | Zelfde spec in JSON-formaat — sync met YAML |

**Werkwijze en gereedschap**

| Document | Inhoud |
|----------|--------|
| [Versioning & CHANGELOG](VERSIONING.md) | Semver-regels, conventional commits, release-workflow |
| [Verificatie-scripts](VERIFICATIE-SCRIPTS.md) | Test-App.ps1 + Start-Debug.ps1: schema-controle, endpoints, Blazor-pagina's |
| [Lokaal debuggen](LOKAAL-DEBUGGEN.md) | Services starten, poorten, hot-reload, func start |

**Onderzoek, ontwerp en archief — nog geen gebouwde code, of niet meer actueel**

| Document | Inhoud |
|----------|--------|
| [SQLite-tier](ARCHITECTUUR-SQLITE-TIER.md) | Tier 3 — voorbereidend ontwerp, nog niet gebouwd |
| [Cosmos DB e-maillog](ARCHITECTUUR-COSMOSDB-EMAILLOG.md) | Tier 4, alleen het e-mailverwerkingslog — ontwerp + kostenverificatie, nog niet gebouwd |
| [Sportlink schermen-analyse](SPORTLINK-CLUB-SCHERMEN-ANALYSE.md) | Beschikbare datavelden in de Sportlink Club-interface |
| [Sportlink Club schrijfacties — onderzoek](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) | Bronrapport met netwerktraces en gevonden endpoint-contracten |
| [Architectuurprincipes (V2, gearchiveerd)](ARCHITECTURE-V2.md) | Historische snapshot vóór de Postgres-cutover — niet meer bijwerken. Naam bewust ongewijzigd gelaten, zie DOCUMENTATIEPLAN.md |

---

## 4. Setup — eenmalige inrichting

Voor nieuwe clubs en developers die de app voor het eerst inrichten.

| Document | Inhoud |
|----------|--------|
| [Nieuwe club — Azure setup](../SETUP-NIEUWE-CLUB.md) | Fork, Azure aanmaken, Entra configureren, eerste deployment — voor club-beheerders |
| [Developer setup](DEVELOPER-SETUP.md) | .NET, SQL Server/Postgres, Azurite, GitHub Actions — lokale ontwikkelomgeving, beide databasetiers |
| [Setup checklist](SETUP-CHECKLIST.md) | Snelle checklist voor eerste opzet, beide databasetiers |
| [Entra auth & beheer](ENTRA-AUTH-BEHEER.md) | App Registration, Easy Auth, rollen, gebruikers toevoegen — via scripts |
| [Eigen domein](CUSTOM-DOMAIN.md) | Custom domain op de Static Web App, CORS-origins, redirect-URI's |

---

## Projectdocumentatie (repo-root)

| Document | Inhoud |
|----------|--------|
| [README](../README.md) | Projectoverzicht, quick start, architectuurdiagram |
| [CHANGELOG](../CHANGELOG.md) | Versiehistorie — alle features en fixes per release |
| [SECURITY](../SECURITY.md) | Security-beleid, AVG-regels, secrets-protocol |
| [CLAUDE.md](../CLAUDE.md) | Instructies voor Claude Code — architectuurregels, buildproces |

---

*Structuur gedefinieerd in [DOCUMENTATIEPLAN.md](DOCUMENTATIEPLAN.md). Een document dat in `docs/`
wordt toegevoegd, hernoemd of verwijderd, wordt in dezelfde PR in deze index én in
DOCUMENTATIEPLAN.md bijgewerkt.*
