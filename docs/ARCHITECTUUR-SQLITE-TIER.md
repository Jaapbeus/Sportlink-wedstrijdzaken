# Architectuur — SQLite-tier (tier 3, voorbereidend ontwerp)

> **Dit document is voorbereidend werk, geen bouwhandleiding — en expliciet geen blokkade voor het
> lopende Postgres/SQL-Server-werk in epic #815.** SQLite is tier 3, gepland ná Postgres (tier 2),
> waar nu de prioriteit ligt. Dit document legt vast wat vandaag al ontwerpbaar is, onafhankelijk
> van één specifieke, nog niet te beantwoorden vraag — zie sectie 6. Zie
> [ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md) voor de volledige bouwvolgorde
> en het "geen gedeelde provider-abstractie"-besluit dat ook voor deze tier geldt.

## 1. Alle ETL/upsert-logica moet in C# leven

SQLite heeft geen server-side procedurele taal. Dat is een fundamenteel verschil met Postgres, dat
in principe PL/pgSQL zou kunnen hosten (al is die route voor de Postgres-tier ook niet gekozen —
zie #818). Voor een SQLite-tier is er geen alternatief: **alle** ETL/upsert-logica (staging → history,
change-detection, business-key-afleiding) moet in C# geïmplementeerd worden, analoog aan hoe
`Database.Postgres/PostgresSchemaGenerator.cs`/`PostgresUpsertGenerator.cs` dat vandaag al doen
voor de Postgres-tier.

## 2. Kernview/field-resolution: geen LATERAL-equivalent

SQLite kent, anders dan Postgres, geen `CROSS`/`OUTER APPLY`-equivalent (geen `LATERAL`-joins). De
huidige kernview-logica (`planner.AlleWedstrijdenOpVeld`, Postgres-vertaling in #819) moet vertaald
worden naar volledige C#-side resolution — er is geen realistisch SQL-alternatief.

Dit sluit direct aan bij de architectuurbeslissing uit #819: veldresolutie leeft al in het
tier-agnostische `Planner.Shared/VeldResolver.cs`, gebruikt door zowel `FunctionApp` (SQL Server)
als `Database.Postgres`. Een toekomstige `Database.Sqlite`-tier zou dezelfde
`Planner.Shared`-implementatie hergebruiken — geen derde kopie. Voor SQLite is dat argument nog
sterker dan voor Postgres, omdat er niet eens een SQL-alternatief bestaat om tegenop te wegen.

## 3. Testbaarheid: in-memory SQLite als kans op een netto-verbetering

`:memory:`-connectionstrings zijn een bekend, lichtgewicht .NET-testpatroon. Een SQLite-tier zou
hierdoor mogelijk **snellere en eenvoudigere** integratietests kunnen krijgen dan de huidige opzet
(wegwerp-Docker-containers voor SQL Server/Postgres) — een potentiële verbetering, onafhankelijk
van het opslagbesluit in sectie 6.

**Open verificatiepunt:** of shared-cache-mode nodig is voor multi-connection-scenario's binnen
één test.

## 4. Migraties: eigen, kleinere migration-runner nodig

Zelfde redenering als bij #821 (Postgres-migratiemechanisme): "een fresh install heeft geen
historische bagage nodig." SQLite heeft echter geen `IF`/`EXEC`-achtige procedurele mogelijkheden
binnen platte SQL zoals Postgres — een klein, C#/script-gebaseerd mechanisme is nodig, vermoedelijk
kleiner dan `Database.Postgres/MigrationRunner.cs` (geen advisory locks — SQLite heeft geen
gelijktijdige-schrijvers-model dat dat vereist; wel dezelfde checksum-verificatie-discipline).

**Open verificatiepunt:** of (gedeeltelijk) hergebruik van `MigrationRunner.cs`'s ontwerp — niet de
code zelf, want dat zou weer een gedeelde providerlaag zijn — zinvol is, of dat een volledig eigen,
kleiner mechanisme de voorkeur verdient.

## 5. Identifier-casing

Zelfde conventie als de Postgres-tier: lowercase snake_case, al vastgesteld in #814 §6 en
[ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md) §3. SQLite is in de praktijk
case-sensitief voor tabelnamen op de meeste platforms maar niet gegarandeerd op alle — de conventie
"altijd lowercase, nooit quoten" voorkomt dat dit ooit een vraag wordt.

## 6. Open architectuurbesluit: persistente opslag op Linux Consumption

Dit project is hard-pinned aan het Linux Azure Functions Consumption-plan (net9.0 isolated worker)
— zie CLAUDE.md, sectie ".NET versie". **Dit document stelt niet voor om die beperking te
heroverwegen.**

**Microsoft Learn-geverifieerd (2026-08-30):** Azure Files storage mounts worden **niet
ondersteund op het Consumption-plan** — alleen Flex Consumption, Elastic Premium en Dedicated
(App Service)-plannen. Letterlijk citaat: *"Storage mounts aren't supported on the Consumption
plan."*

Zonder mount is er geen door Microsoft gedocumenteerd mechanisme dat een gegarandeerd-persistent,
gegarandeerd-gedeeld-over-instances lokaal bestandssysteempad biedt. Consumption-plan-instances
zijn ephemeral en kunnen opschalen naar meerdere gelijktijdige instances; een SQLite-bestand op
lokale/temp-opslag van één instance is niet gegarandeerd zichtbaar voor, of veilig gelijktijdig
beschrijfbaar vanuit, een andere instance.

Dit is een **echte, onopgeloste architectuurvraag** — geen blokkade voor dit voorbereidende
document, wél een blokkade voor daadwerkelijke bouw. De project-eigenaar beslist dit pas op het
moment dat tier 3 daadwerkelijk wordt opgepakt.

### De realistische opties (gepresenteerd, niet hier beslist)

| Optie | Werking | Voordeel | Nadeel |
|---|---|---|---|
| **A — alleen voor niet-Consumption-hosting** | SQLite-tier uitsluitend voor clubs die buiten Azure Functions Consumption zelf hosten (VM, Container App, App Service Plan) | Omzeilt de beperking volledig | Niet bruikbaar op het standaard gratis-Azure-stack-hostingmodel — moet expliciet in publieke documentatie staan |
| **B — Azure Blob Storage als persistentielaag** | Lokaal SQLite als ephemere werkkopie per invocation/batch, gesynchroniseerd met Blob | Technisch mogelijk binnen Consumption | Cold-start-latency, concurrent-instance-schrijfraces tenzij geserialiseerd via een blob-lease — kan SQLite's eenvoud teniet doen |
| **C — planwijziging naar Flex Consumption** | Overstap naar een plan dat wél storage mounts ondersteunt | Native persistente opslag | Heropent het apart vastgestelde net9.0/Linux-Consumption-kostenbesluit; vereist volledige Kostenbeleid-verificatie plus expliciete goedkeuring vóór adoptie |

## 7. Randgevallen/risico's (voor de delen die wél zijn uitgewerkt)

- **C#-only ETL**: grotere kans op subtiele bugs t.o.v. de bewezen stored-procedure-logica op SQL
  Server — vraagt om extra unit-testdekking.
- **Field-resolution in C#**: gedeeld via `Planner.Shared`, dus geen apart driftrisico t.o.v. de
  Postgres-tier — wél t.o.v. de SQL Server-view, al bewaakt door `VeldResolutieDriftTests`.
- **Migration-runner als derde mechanisme**: vergroot onderhoudslast naast het PostDeployment-script
  (SQL Server) en `Database.Postgres/MigrationRunner.cs` — te verifiëren of gedeeltelijk hergebruik
  van het ontwerp (niet de code) zinvol is.
- **In-memory SQLite en multi-connection-scenario's**: shared-cache-mode-behoefte te verifiëren
  tijdens implementatie.
- **ClubCode-discriminator**: blijft van toepassing — te verifiëren hoe dit zich verhoudt tot een
  eventueel bestand-per-club-model (relevant vooral bij optie A hierboven).

## 8. Kostentier-check

Dit document creëert en wijzigt geen Azure-resources — geen prijscheck vereist. Optie B en C uit
sectie 6 hebben wél kostenimpact zodra ze daadwerkelijk gekozen worden — een hernieuwde MS
Docs-prijsverificatie plus expliciete goedkeuring is verplicht zodra tier 3 wordt opgepakt. Optie A
heeft geen kostenimpact voor de eigen gratis-tier-stack, maar verplaatst kosten naar de
zelf-hostende club — moet expliciet in publieke documentatie staan zodra gekozen.

## 9. Wat dit document NIET doet

- Geen keuze maken tussen optie A/B/C in sectie 6 — dat is aan de project-eigenaar, op het moment
  dat tier 3 daadwerkelijk wordt opgepakt.
- Geen code, geen migratiebestanden, geen `Database.Sqlite/`-project aanmaken.
- Geen enkele afhankelijkheid creëren richting het lopende Postgres/SQL-Server-werk — dit document
  kan gelezen worden zonder dat het iets aan de huidige tier-1/tier-2-voortgang verandert.

## Gerelateerd

Onderdeel van epic #815 (#826). Bouwt voort op #814 (multi-tier-onderzoek), #818 (Postgres-ETL),
#819 (Postgres-kernview, `Planner.Shared`-extractie), #821 (Postgres-migratiemechanisme).
