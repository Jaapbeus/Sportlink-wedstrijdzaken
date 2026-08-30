# Architectuur — Cosmos DB voor het e-mailverwerkingslog (tier 4, ontwerp)

> **Dit document is ontwerp + kostenverificatie, geen implementatie.** Cosmos DB is tier 4, laatste
> in de bouwvolgorde van epic #815, en uitsluitend gescopet op het e-mailverwerkingslog
> (`planner.EmailVerwerking`) — **nooit** een vervanging van de hoofd-ETL-database. Dit document is
> voorbereidend werk, geen blokkade voor het werk aan tier 1/2 (SQL Server/Postgres) dat nu wordt
> uitgevoerd. Zie [ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md) voor de
> volledige bouwvolgorde.

## 1. Waarom alleen de e-maillog, nooit de kern-ETL

Cosmos DB is een schemaloze documentdatabase zonder joins, zonder foreign keys, zonder relationele
integriteitscontroles, met transacties strikt beperkt tot één partition-key-waarde. De kern-ETL-
pijplijn (cross-table `MERGE`-logica, een kernview met meerdere joins — zie #818/#819) is een veel
grotere paradigmasprong dan Postgres of SQLite — geen realistische kandidaat als vervanging van de
hoofddatabase.

De e-maillog is daarentegen één grotendeels zelfstandige tabel zonder cross-table-join-afhankelijkheid
vanuit het perspectief van de ETL-engine — de goedkoopste, laagste-impact plek in de hele codebase
om met een andere backing store te experimenteren.

**Aanbevolen (niet verplicht) vóór implementatie:** de repository-boundary-refactor voor het
e-maillog (#827, **gemerged**), zodat Cosmos DB als alternatieve implementatie achter één schone
repository-grens kan landen — geen harde afhankelijkheid, wel de logische volgorde.

## 2. Free-tier vereisten — harde eis (kostenbeleid)

Bevestigd in #814: Cosmos DB gratis tier = 1000 RU/s + 25 GB opslag, voor de levensduur van het
account, één gratis account per subscription.

**Aanvulling, geverifieerd via Microsoft Learn (2026-08-30) — ontbrak in #814:** de gratis tier is
**niet beschikbaar voor accounts in serverless-modus**. Ze geldt uitsluitend voor accounts met
provisioned of autoscale throughput, en moet expliciet worden geactiveerd bij het aanmaken van het
account.

> **Harde, niet-onderhandelbare eis:** elk Cosmos DB-account dat voor tier 4 wordt aangemaakt MOET
> expliciet de gratis-tier-korting geactiveerd hebben bij aanmaak, én provisioned of autoscale
> throughput gebruiken — **nooit** serverless-modus, anders is het account stilzwijgend niet
> gratis. Conform CLAUDE.md's kostenbeleid: nooit een nieuwe Azure-resource aanmaken zonder
> expliciete bevestiging van de gebruiker, en de prijspagina opnieuw verifiëren vlak vóór aanmaak
> (gratis-tier-voorwaarden kunnen zonder aankondiging wijzigen).

## 3. Documentvorm `EmailLog`-container (concept, geen definitief schema)

Grove mapping van de huidige `planner.EmailVerwerking`-tabel naar een document, in generieke
categorieën — **niet** als definitief schema te lezen: status (verwerkingsstatus),
ontvangst-timestamp, e-mailinhoud, verzendpoging-metadata. De exacte kolomlijst en typen moeten
**tijdens implementatie tegen de huidige `planner.EmailVerwerking`-tabel geïnventariseerd worden** —
zelfde discipline als #818 toepaste voor de ETL-entiteiten (nooit aannemen, altijd verifiëren tegen
het echte schema).

## 4. Partition-key: open ontwerpvraagstuk (bewust niet opgelost)

Het deployment-model "één echte club + AllStars FC als demo/testclub per fork" (zie CLAUDE.md,
"Deployment-model") betekent:

- **Optie A — `ClubCode` als partition key.** Zou in elke deployment maar ~2 distincte waarden
  hebben → een "hete", slecht verdeelde partitie.
- **Optie B — synthetische partition key** (bijv. op datum, of op het eigen record-id). Voorkomt
  het hete-partitie-probleem, maar maakt club-gescopeerde queries tot duurdere cross-partition
  queries.

Voor een laag-verkeer-applicatie met één echte club per deployment is dit vermoedelijk in de
praktijk geen probleem, maar het is een reële ontwerpafweging. **Besluit uitgesteld** tot tier 4
daadwerkelijk wordt opgepakt.

## 5. Retry/429-afhandeling versus SQL Server's serverless-faalmodus

Cosmos DB retourneert bij doorvoer-overschrijding een per-request HTTP 429, automatisch afgehandeld
door de officiële SDK via exponential-backoff retry — in schril contrast met de all-or-nothing
pauze-faalmodus van SQL Server serverless (het motiverende incident achter #799/#815: de gratis
Azure SQL-tier pauzeerde ~10 dagen in augustus 2026 na uitputting van het maandelijkse
vCore-second-budget).

**Eerlijke kanttekening (overgenomen uit #814 §7):** het verplaatsen van de e-maillog naar Cosmos DB
lost het oorspronkelijke beschikbaarheidsincident **niet** op. Het vermindert alleen connectie-/
schrijfdruk op de hoofddatabase, wat *mogelijk* de kans op herhaling verkleint — speculatief, niet
bewezen.

**Idempotente schrijfstrategie, apart van retry** (uit de review-fact-check-addendum): SDK-retry-
op-429 alleen voorkomt geen dubbele documenten bij een timeout-ná-succes-scenario (het schrijven
slaagde server-side, maar de client kreeg geen bevestiging vóór een timeout, en retryt). Een
deterministisch document-ID (afgeleid uit een natuurlijke sleutel, niet een gegenereerde GUID) plus
ETag/optimistic-concurrency-controle is nodig om dat scenario dubbel-vrij te maken — apart
ontwerppunt, niet opgelost door retry alleen.

## 6. Aandachtspunten CISO/DPO

- **AVG-classificatie ongewijzigd:** e-mailinhoud bevat persoonsgegevens; classificatie verandert
  niet door een andere backing store. Regio-/dataresidency-keuze (binnen de EU) niet geverifieerd —
  moet bij implementatie worden vastgesteld.
- **Secret-opslag:** connectiegegevens vermoedelijk via Function App-instellingen, niet Key Vault
  (Key Vault is potentieel betaald per het Kostenbeleid) — te bevestigen tijdens implementatie.
- **TTL vervangt de bestaande anonimisatiestap niet** (uit de review-fact-check-addendum): Cosmos
  TTL verwijdert alleen hele documenten; de bestaande 30-dagen-anonimisatiestap
  (`sp_CleanupEmailVerwerkingFunction`, partiële veldupdate — zie
  `Database/planner/System Stored Procedures/sp_CleanupEmailVerwerking.sql`)
  is een gedeeltelijke veldwijziging, geen documentverwijdering. Een scheduled-update-mechanisme
  blijft dus nodig naast een eventuele TTL-gebaseerde volledige-verwijdering na 90 dagen — TTL alleen
  is geen vervanging.

## 7. Expliciet uitgestelde besluiten

- [ ] Partition-key: `ClubCode`-gebaseerd vs. synthetische sleutel (sectie 4).
- [ ] Exact documentschema / kolommapping vanuit `planner.EmailVerwerking` (sectie 3).
- [ ] Idempotente schrijfstrategie: deterministisch document-ID + ETag/optimistic concurrency
      (sectie 5).
- [ ] Wanneer tier 4 daadwerkelijk wordt opgepakt — geen datum vastgelegd, laagste prioriteit van
      de vier tiers.
- [ ] Azure-regio/dataresidency voor het Cosmos DB-account (sectie 6).
- [ ] Opslagwijze van de Cosmos DB-connectiegegevens: Function App-instellingen vs. Key Vault
      (sectie 6).

## 8. Wat dit document NIET doet

- Geen Cosmos DB-account aanmaken — puur ontwerp/documentatie.
- Geen van de bovenstaande open besluiten forceren tot een keuze.
- Geen enkele afhankelijkheid creëren richting het lopende Postgres/SQL-Server-werk.

## Kostentier-check

Zie sectie 2 hierboven — dit document maakt zelf geen Azure-resource aan, dus is er geen
verificatiemoment nodig om dít document af te ronden. Bij daadwerkelijke implementatie is de
volledige CLAUDE.md-kostenchecklist (inclusief een hernieuwde MS Docs-prijsverificatie, want
gratis-tier-voorwaarden kunnen zonder aankondiging wijzigen) verplicht vóór aanmaak.

## Gerelateerd

Onderdeel van epic #815 (#828). #799 (recidiverende auto-pause/resume-problemen — het motiverende
incident, wordt door dit document niet opgelost). #827 (repository-boundary-refactor e-maillog,
gemerged) — aanbevolen vóór implementatie, geen harde blokkade.
