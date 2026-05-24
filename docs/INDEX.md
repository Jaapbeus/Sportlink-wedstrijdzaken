# Documentatie — Sportlink Wedstrijdzaken

Alle documentatie staat in deze map (`docs/`). Gebruik de categorieën hieronder om snel de juiste informatie te vinden.

---

## Voor beheerders (gebruikers van de Admin GUI)

| Document | Inhoud |
|---|---|
| [Admin handleiding v2](v2-admin-handleiding.md) | Stap-voor-stap handleiding voor de Admin GUI: instellingen, templates, veldplanner, e-maillog |
| [Handleiding teambegeleiding export](HANDLEIDING-TEAMBEGELEIDING-EXPORT.md) | AVG-veilig exporteren van teambegeleidersgegevens uit Sportlink naar SQL |
| [KNVB speeldagenkalenders](knvb-speeldagenkalenders/README.md) | Importeren van KNVB-speeldagenkalenders |

---

## Voor developers — opzet & lokaal draaien

| Document | Inhoud |
|---|---|
| [Setup](SETUP.md) | Volledige installatiehandleiding: .NET, Azure Functions Core Tools, Azurite, SQL Server |
| [Setup checklist](SETUP-CHECKLIST.md) | Snelle checklist voor eerste opzet van de ontwikkelomgeving |
| [Lokaal debuggen](LOKAAL-DEBUGGEN.md) | Services starten, poorten, Azurite, func start, hot-reload |
| [Quick reference](QUICK-REFERENCE.md) | Veelgebruikte commando's, SQL-snippets en configuratiewaarden |
| [Azure Entra setup](AZURE-ENTRA-SETUP.md) | Entra App Registration, Easy Auth, rollen — configuratie via scripts |

---

## Architectuur & ontwerp

| Document | Inhoud |
|---|---|
| [Planner architectuur](ARCHITECTURE-PLANNER.md) | Velddefinities, API-contract en dataflow van de veldplanner |
| [API referentie](API.md) | Alle HTTP-endpoints: routes, parameters, response-formaten, authenticatie |
| [E-mailverwerking](EMAIL-VERWERKING.md) | E-mailpipeline, kanaalstrategie, AI-verwerking, BuitenScope-logica |
| [Versiebeheer](VERSIONING.md) | Semantic versioning, conventional commits, release-workflow, CHANGELOG-richtlijnen |

---

## Testen & kwaliteit

| Document | Inhoud |
|---|---|
| [Testing](TESTING.md) | Test-App.ps1: schema-controle, endpoint-verificatie, Blazor-pagina's, -Fix modus |

---

## Security & compliance

| Document | Inhoud |
|---|---|
| [Security](../SECURITY.md) | Security-beleid, AVG-regels, secrets-protocol, responsible disclosure |
| [Azure Entra setup](AZURE-ENTRA-SETUP.md) | Auth-architectuur, defense-in-depth (5 lagen), verplichte 3-user-test |

---

## Overige projectdocumentatie (root)

| Document | Inhoud |
|---|---|
| [README](../README.md) | Projectoverzicht, quick start, architectuurdiagram |
| [CHANGELOG](../CHANGELOG.md) | Versiehistorie — alle features en fixes per release |
| [CLAUDE.md](../CLAUDE.md) | Instructies voor Claude Code — architectuurregels, buildproces, conventies |

---

*Documentatie bijhouden is verplicht bij elke wijziging — zie CLAUDE.md Stap 2b voor de volledige regel.*
