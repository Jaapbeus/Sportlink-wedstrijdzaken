# VRC Sportlink Wedstrijdzaken — Documentatie

## Overzicht

Dit project bevat de VRC Veldplanner API en de Sportlink data-synchronisatie. Zie de volgende bestanden voor gedetailleerde documentatie:

| Bestand | Beschrijving |
|---------|-------------|
| [API.md](API.md) | API documentatie met alle endpoints en voorbeelden |
| [SETUP.md](SETUP.md) | Installatie en configuratie handleiding |
| [SETUP-CHECKLIST.md](SETUP-CHECKLIST.md) | Checklist voor eerste keer opzetten |
| [LOCAL-DEBUG-README.md](LOCAL-DEBUG-README.md) | Lokale debug-omgeving opzetten |
| [QUICK-REFERENCE.md](QUICK-REFERENCE.md) | Snelle referentie voor veelgebruikte commando's |
| [../Planner/ARCHITECTURE.md](../Planner/ARCHITECTURE.md) | Architectuur en richtlijnen van de Veldplanner |

## Timer trigger

De `FetchAndStoreApiData` functie synchroniseert dagelijks om 04:00 data vanuit de Sportlink API. Cron-expressie: `0 0 4 * * *`.

Handmatig synchroniseren kan via: `GET /api/sync-matches`
