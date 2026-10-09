# Documentatie — Sportlink Wedstrijdzaken

Nieuw hier? Begin bij de [README](../README.md) voor het idee achter de app. Daarna kun je deze
korte route volgen:

1. **Rondkijken:** [eerste rondje door de app](BEHEERDER-HANDLEIDING.md#eerste-rondje-door-de-app).
2. **Proberen:** [fictieve AllStars-wedstrijden](TESTMODUS-ALLSTARS.md#een-eerste-proefrondje), op een bestaande of lokale installatie.
3. **Zelf draaien:** [lokaal opzetten](DEVELOPER-SETUP.md) of [een eigen clubinstallatie](../SETUP-NIEUWE-CLUB.md).

Hieronder staat de volledige leeslijst, van dagelijks gebruik tot technische verdieping.
De regels voor het bijhouden van documentatie staan in [DOCUMENTATIEPLAN.md](DOCUMENTATIEPLAN.md).

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
| [PDF-export](ARCHITECTUUR-PDF-EXPORT.md) | QuestPDF-generator in `Planner.Shared/Deel/`: licentievoorwaarde per club, native assets, pakketgrootte, platformbewijs |
| [E-mailverwerking](EMAIL-VERWERKING.md) | Pipeline, AI-classificatie, templates, kanaalstrategie |
| [E-mailmodule (doelarchitectuur)](ARCHITECTUUR-EMAIL-MODULE.md) | Verzendlaag, afzenderstrategie, e-maillogging — ontwerp, migratie nog niet gestart |
| [Sportlink-dataservice (lezen)](SPORTLINK-DATASERVICE.md) | Leesrichting: endpoints, sync-strategie programma/uitslagen, volledige veldreferentie van `/programma`, nieuwe databron toevoegen |
| [Sportlink Web Extension](SPORTLINK-WEB-EXTENSION.md) | Schrijfrichting webapp → Sportlink Club: protocol, endpoints, agent-tokengrens |
| [Feedback](FEEDBACK.md) | Feedbackwidget voor alle gebruikers, beheeroverzicht, technische context en redactie, bewaartermijnen, inzagelog, verwerkingsregister (#764) |
| [Automatische Sportlink-login](SPORTLINK-AUTOLOGIN.md) | Automatic login met TOTP (#1411): implementatie, productie-setup, beveiliging |

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
| [Dossier agentinstructies binnen het budget](DOSSIER-AGENTINSTRUCTIES-BUDGET.md) | Gedateerd werkdossier van #1580: meting van het Codex-budget, beslisregels, regel→bestemming-matrix per sectie van `AGENTS.md`, verificatie |
| [Dossier Speeltijden inline formulier](DOSSIER-SPEELTIJDEN-INLINE-FORMULIER.md) | Gedateerd werkjournaal van #1552/#1553/#1554: besluiten (opslag per bewerksessie, kaartweergave, labelkoppeling), verificatiebewijs, reviewafhandeling, restbeperkingen |

**Onderzoek en ontwerp — nog geen gebouwde code**

| Document | Inhoud |
|----------|--------|
| [SQLite-tier](ARCHITECTUUR-SQLITE-TIER.md) | Tier 3 — voorbereidend ontwerp, nog niet gebouwd |
| [Cosmos DB e-maillog](ARCHITECTUUR-COSMOSDB-EMAILLOG.md) | Tier 4, alleen het e-mailverwerkingslog — ontwerp + kostenverificatie, nog niet gebouwd |
| [Sportlink schermen-analyse](SPORTLINK-CLUB-SCHERMEN-ANALYSE.md) | Beschikbare datavelden in de Sportlink Club-interface — brondata voor SPORTLINK-WEB-EXTENSION.md |
| [Sportlink Club schrijfacties — onderzoek](ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md) | Bronrapport met netwerktraces en gevonden endpoint-contracten — brondata voor SPORTLINK-WEB-EXTENSION.md |
| [Serena vs Graft benchmark](SERENA-VS-GRAFT-BENCHMARK.md) | Context-ophaal workflows: waarom dit project Serena gebruikt, waar Graft sterker is, praktische richtlijnen |

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
| [Runbook Flex-migratie](RUNBOOK-FLEX-MIGRATIE.md) | Historisch verslag, migratie voltooid op 2026-10-03/04: Linux Consumption → Flex Consumption (epic #1063) — volgorde FLEX-05 t/m 09, kostencontroles, cutover en rollback |

---

## Projectdocumentatie (repo-root)

| Document | Inhoud |
|----------|--------|
| [README](../README.md) | Projectoverzicht, quick start, architectuurdiagram |
| [CHANGELOG](../CHANGELOG.md) | Versiehistorie — alle features en fixes per release |
| [SECURITY](../SECURITY.md) | Security-beleid, AVG-regels, secrets-protocol |
| [AGENTS.md](../AGENTS.md) | De enige bron van de agentinstructies (Codex én Claude Code) — architectuurregels, buildproces; `CLAUDE.md` is een stub die ernaar verwijst |

---

*Structuur gedefinieerd in [DOCUMENTATIEPLAN.md](DOCUMENTATIEPLAN.md). Een document dat in `docs/`
wordt toegevoegd, hernoemd of verwijderd, wordt in dezelfde PR in deze index én in
DOCUMENTATIEPLAN.md bijgewerkt.*
