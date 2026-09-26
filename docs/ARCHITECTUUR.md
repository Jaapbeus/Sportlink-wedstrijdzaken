# Architectuurbeschrijving — Sportlink Wedstrijdzaken

> **Status:** geldend · **Laatst herzien:** 20 september 2026
> **Vorm:** architectuurbeschrijving volgens ISO/IEC/IEEE 42010:2022, gestructureerd met arc42.
>
> Dit is het **enige, leidende** architectuurdocument van dit project. Er bestaat geen apart
> uitvoeringsdocument meer — `docs/ARCHITECTURE.md` is per #1291 volledig in dit bestand
> opgenomen. Onderwerpen met genoeg eigen omvang blijven een eigen sub-document (zie de tabel in
> §13 en `docs/INDEX.md`); dit document verwijst ernaar in plaats van ze te dupliceren.
>
> **Dit document is publiek.** Het is gegrond in openbare standaarden en bevat geen waarde die deze
> installatie identificeert — zie §0.3.

---

## 0. Leeswijzer

### 0.1 Waarom deze vorm

Tot 2026-09-19 stonden de architectuurafspraken van dit project verspreid over een reeks
documenten — waaronder twee top-level architectuurdocumenten met een Engelse en een Nederlandse
naam — en als doorlopende tekst in de projectinstructies (`CLAUDE.md`). Dat werkt voor wie het
geschreven heeft en slecht voor iedereen daarna — mens of agent. Sommige regels stonden zelfs
woordelijk op drie plekken tegelijk (`CLAUDE.md`, `ARCHITECTURE.md` en een samenvatting hier), wat
bij een wijziging bijna gegarandeerd tot drift leidt. Deze beschrijving lost dat op met vier keuzes:

1. **ISO/IEC/IEEE 42010:2022** als formele basis. Die standaard scheidt de *architectuur* van de
   *beschrijving* ervan, en eist dat een beschrijving expliciet maakt: wie de belanghebbenden zijn,
   welke zorgen zij hebben, vanuit welke gezichtspunten het systeem beschreven wordt, en welke
   beslissingen daaraan ten grondslag liggen. Zij schrijft géén techniek en géén bestandsformaat voor.
2. **arc42** als praktisch sjabloon. Twaalf vaste hoofdstukken, één leesbaar document, open en gratis.
   Dat voorkomt precies de versnippering waar dit project last van had.
3. **Eén register** waarin elke regel een ID, een externe basis en een bewijsvorm heeft.
4. **Eén document, geen satelliet met dezelfde inhoud.** Op 2026-09-20 is het vroegere
   uitvoeringsdocument (`ARCHITECTURE.md`) volledig samengevoegd (#1291): de concrete checklists,
   codevoorbeelden en schemaconventies staan nu als uitwerking in het hoofdstuk waar ze inhoudelijk
   bij horen, in plaats van in een los document dat dezelfde regel nogmaals beschrijft.

### 0.2 Hoe je dit leest

| Je bent… | Begin bij |
|---|---|
| Nieuw in het project | §1 t/m §5 — doelen, beperkingen, context, bouwblokken |
| Een wijziging aan het maken | §8 (de regels, met concrete uitwerking) en §10 (het register: heeft deze regel een controle?) |
| Een agent die een wijziging toetst | §10 rechtstreeks — externe basis → lokale regel → bewijs |
| Benieuwd waarom iets zo is | §9 (besluiten) en §11 (risico's en schuld) |
| Op zoek naar een checklist of codevoorbeeld (auth, UTC, secrets, ...) | §8 — elk onderwerp heeft een concrete uitwerking direct onder de regel |

**De belangrijkste conventie van dit document:** elke regel in §10 heeft een kolom **Bewijs**. Staat
daar `ontbreekt`, dan is de regel wel geldig maar niet controleerbaar. Dat is een zichtbare,
expliciete toestand — geen omissie die je pas ontdekt als het misgaat. Die kolom is er omdat de
toetsing van september 2026 als rode draad opleverde: *controles die minder dekken dan ze
suggereren, zijn gevaarlijker dan geen controle, want ze wekken vertrouwen.*

### 0.3 Waarom dit document geen intern materiaal van derden citeert

Deze repository is publiek en bedoeld om geforkt te worden. Een architectuurbeschrijving die leunt
op de interne standaard van een organisatie zou dat materiaal naar een openbaar kanaal trekken, en
zou bovendien onbruikbaar zijn voor een fork die die standaard niet kan lezen.

Daarom is elke regel hieronder gegrond in een **openbare** bron: een ISO-standaard, het Azure
Well-Architected Framework, OWASP ASVS, een IETF RFC, de OpenAPI-specificatie of de AVG. Een fork
kan elke bron zelf nalezen zonder toegang tot iets van deze installatie.

---

## 1. Inleiding en doelen

### 1.1 Wat dit systeem doet

Sportlink Wedstrijdzaken ondersteunt de wedstrijdsecretaris van een amateurvoetbalvereniging. Het
haalt wedstrijd- en teamgegevens op bij de externe databron van de bond, plant veldgebruik, verwerkt
inkomende e-mail over wedstrijdwijzigingen, en biedt een beheerinterface waarmee één beheerder de
vereniging kan bedienen.

### 1.2 Kwaliteitsdoelen

Vijf doelen, in volgorde van gewicht. Bij een conflict wint het hogere doel, en dat conflict wordt
als besluit vastgelegd (§9).

| # | Kwaliteitsdoel | Wat het concreet betekent | WAF-pijler |
|---|---|---|---|
| 1 | **Bescherming van persoonsgegevens** | Gegevens van leden, waaronder minderjarigen, komen niet in een publiek kanaal, een log, een issue of bij een onbevoegde verwerker terecht | Security |
| 2 | **Kosten binnen het gratis plafond** | De volledige stack draait op gratis tiers; een overschrijding is een storing, geen verrassing op de rekening | Cost Optimization |
| 3 | **Betrouwbaarheid van de dagelijkse keten** | Synchronisatie, e-mailverwerking en planning doen wat ze beloven, en een storing is zichtbaar in plaats van stil | Reliability |
| 4 | **Overdraagbaarheid** | Een andere vereniging kan de repository forken en draaien zonder toegang tot iets van de oorspronkelijke installatie | Operational Excellence |
| 5 | **Onderhoudbaarheid door een klein team** | Eén beheerder plus AI-assistentie moet dit kunnen onderhouden; complexiteit die dat niet dient, gaat eruit | Operational Excellence |

Performance Efficiency is bewust het laagste doel: de belasting is enkele gebruikers en een dagelijkse
batch. Dat is een expliciete afweging, geen omissie — zie besluit **WZ-ADR-005**.

### 1.3 Belanghebbenden en hun zorgen (ISO 42010)

| Belanghebbende | Voornaamste zorg | Waar die zorg wordt geadresseerd |
|---|---|---|
| Wedstrijdsecretaris / beheerder | Werkt het, en kan ik het zelf beheren | §6 runtime-scenario's, §7 installatie |
| Clubbestuur (verwerkingsverantwoordelijke) | Zijn de persoonsgegevens rechtmatig verwerkt | §8.1, §10 concern `data` |
| Betrokkene (lid, ouder, vrijwilliger) | Waar gaan mijn gegevens heen, en hoe lang blijven ze | §8.1 bewaartermijnen, §8.5 AI |
| Ontwikkelaar / reviewer | Welke regel geldt, en hoe toets ik hem | §10 register |
| AI-agent | Welke grens mag ik niet overschrijden | §10, machinaal leesbaar |
| Forkende vereniging | Kan ik dit draaien zonder de originele installatie | §2 beperkingen, §7 deployment |

---

## 2. Randvoorwaarden

Deze drie zijn niet onderhandelbaar en bepalen vrijwel elke afweging in dit document.

| # | Randvoorwaarde | Gevolg |
|---|---|---|
| **B1** | **Publieke, forkbare repository** | Geen club-identificerende waarde in code, configuratie, issue, pull request of buildlog. Alles wat een installatie uniek maakt, is configuratie. |
| **B2** | **Gratis Azure-tiers** | Diensten met een prijskaartje — sleutelkluis, containerhosting, betaalde alarmering, meerdere omgevingen — zijn geen standaard. Een maatregel die geld kost, vraagt een expliciet besluit. |
| **B3** | **Eén vereniging per installatie** | Er is geen autorisatiegrens tussen verenigingen nodig; de clubdiscriminator is een gegevensfilter en geen beveiligingsgrens. |

**B2 verdient toelichting, want hij wordt makkelijk als een tekortkoming gelezen.** Het Azure
Well-Architected Framework kent zelf een maturity-model in vijf niveaus en beveelt aan om gefaseerd
te beginnen bij wat essentieel is, en het hoogste niveau te reserveren voor bedrijfskritische
systemen. Deze applicatie zit bewust op niveau 1 tot 2: een solide basis en eigen werkstukken, niet
een always-on bedrijfskritische inrichting. Dat is een toepassing van het kader, geen afwijking ervan.

### 2.1 B3 uitgewerkt — één fork, één primaire club (WZ-ADR-011)

**Vastgelegd na review van issue #393 (2026-05-31).** Het model: **één GitHub-fork draait exact
één productieclub, plus AllStars FC als vaste demo-/testclub, in dezelfde database, beheerd door
dezelfde admin.** Wijzig dit model niet zonder expliciete heroverweging van alle
multi-club-security-implicaties.

| Aspect | Beslissing |
|---|---|
| Clubs per deployment | Precies één echte club + AllStars FC als demo/testdata |
| `admin`-rol | Club-scoped: admin van deze installatie = admin van de ene club in deze deployment |
| `X-Club-Code`-header | UX-feature om te wisselen tussen productie- en demodata — **geen multi-user autorisatiegrens** |
| `SELECT TOP 1`/`LIMIT 1` op de settingstabel | Acceptabel: er is altijd precies één primaire club per deployment |
| Shared hosting (meerdere echte clubs, aparte admins, één deployment) | **Niet ondersteund en niet het doel** — vereist een volledige herontwerpslag van auth, data-isolatie en settings |

**Implicaties voor code:**
- Server-side validatie of een gebruiker een *specifieke* club mag beheren is **geen vereiste** in
  dit model — elke geauthenticeerde admin beheert per definitie de ene club in zijn deployment.
- Een `X-Club-Code`-waarde uit de request mag vertrouwd worden zodra hij een geldige `ClubCode` is
  in de settingstabel van déze deployment. Een controle "bestaat deze ClubCode bij ons?" is zinvol
  als hardening, maar geen beveiligingsgrens.

**AllStars FC (de vaste demo-club):**
- `ALLSTARS` is de vaste demo-`ClubCode` in broncode, seeds en testdata — hoofdletters, precies zo.
  `allstars-fc` is géén ClubCode; die schrijfwijze komt uitsluitend voor in het fictieve
  e-maildomein `@allstars-fc.test` van de seeddata (zie §8.1 voor de AVG-motivering van dat domein).
- Uitsluitend voor lokale ontwikkeling en UI-demonstraties; nooit vervangen door een echte
  club-specifieke waarde.

---

## 3. Context en systeemafbakening

### 3.1 Vakinhoudelijke context

```
  Wedstrijdsecretaris ──► Beheerinterface (browser)
                               │
  Externe databron bond ──────►│◄────── Postbus van de vereniging
  (wedstrijden, teams)         │        (inkomende wijzigingsverzoeken)
                               ▼
                         Wedstrijdzaken
                               │
                               ├──► AI-dienst (classificatie van berichten)
                               ├──► Identiteitsprovider (aanmelden en rollen)
                               └──► Uitgaande e-mail (antwoorden, meldingen)
```

### 3.2 Technische context

| Koppelvlak | Richting | Protocol | Grens |
|---|---|---|---|
| Databron van de bond | uitgaand | HTTPS, sleutel in querystring | Alleen lezen |
| Databron van de bond — schrijfrichting (Sportlink Web Extension) | uitgaand | HTTPS, servicetoken van de club | Alleen mutaties die de beheerder in de eigen GUI bevestigt; zie §5.5 |
| Postbus | in- en uitgaand | Graph-API, applicatie-identiteit | Beperkt tot één postbus |
| AI-dienst | uitgaand | HTTPS, API-sleutel | Alleen als uitgaand verkeer is toegestaan én de functie is ingeschakeld |
| Identiteitsprovider | inkomend | OIDC en JWT | Enige bron van identiteit |
| Beheerinterface → API | inkomend | HTTPS met bearer-token | De autorisatiegrens |

**Alle uitgaande koppelvlakken passeren één poort** die controleert of extern verkeer in deze
omgeving is toegestaan. Zie **WZ-INT-01**.

---

## 4. Oplossingsstrategie

| Vraagstuk | Keuze | Reden | Besluit |
|---|---|---|---|
| Waar draait het | Serverloze functies, statische webhosting, beheerde database | Past binnen het gratis plafond en vraagt geen beheer van machines | WZ-ADR-001 |
| Hoe scheiden we lagen | Eén gedeelde kern zonder in- of uitvoerafhankelijkheden; providergebonden code in de tierbomen; de webinterface praat uitsluitend over HTTP | Houdt de kern testbaar zonder database | WZ-ADR-002 |
| Meerdere databasesoorten | Elke gebouwde tier is gelijkwaardig en krijgt dezelfde functionaliteit; één tier is actief per installatie | Overdraagbaarheid: een fork kiest zelf | WZ-ADR-003 |
| Identiteit | Externe identiteitsprovider bewijst wie je bent; de applicatie beslist wat je mag | Geen eigen accountopslag, geen eigen wachtwoorden | WZ-ADR-004 |
| Prestaties | Bewust geen optimalisatiedoel | Enkele gebruikers, dagelijkse batch | WZ-ADR-005 |
| AI | Provideronafhankelijke abstractie; de keuze van dienst en model is configuratie | Een wissel is één registratie, geen verbouwing | WZ-ADR-006 |
| Deployment per club | Eén fork = één productieclub + demo-club, geen shared hosting | Geen multi-tenant-autorisatielaag nodig; simpeler en goedkoper | WZ-ADR-011 |

---

## 5. Bouwblokken

### 5.1 Niveau 1 — hoofdonderdelen

| Bouwblok | Verantwoordelijkheid | Mag níet |
|---|---|---|
| **Beheerinterface** (browsertoepassing) | Presentatie, invoer, aanroepen van de API | Persistentie, of enige databaseafhankelijkheid |
| **Gedeelde kern** | Tier- en provideronafhankelijke domeinlogica: planning, naamnormalisatie, thema, terugkoppeling, beveiligde uitgaande aanroepen | Database- of webframeworkafhankelijkheden bevatten |
| **Functie-app per tier** | HTTP-, timer- en wachtrij-ingangen, autorisatie, providergebonden gegevensverwerking | De andere tier aanroepen |
| **Databaselaag per tier** | Schema, migraties, gegevenstoegang | Logica die niets met de database te maken heeft |
| **Infrastructuur en werkstromen** | Herhaalbare uitrol, configuratie, bewaking | Club-identificerende waarden bevatten |

### 5.2 Waarom deze indeling werkt zoals ze is

De afhankelijkheden lopen één kant op en zijn cyclusvrij. De gedeelde kern bevat geen enkel
databasepakket; de beheerinterface heeft geen enkele projectverwijzing naar de achterkant. Dat is
geen voornemen maar de gemeten toestand — en precies daarom is het goedkoop om er een
architectuurtest omheen te zetten in plaats van te hopen dat het zo blijft (**WZ-ARC-01**).

**De grens die in de praktijk het vaakst verkeerd is gelegd**, is die tussen "providergebonden" en
"puur". Vier keer is bij het overzetten naar een tweede tier het *hele bestand* gekopieerd, inclusief
validatie, patronen en orkestratie die niets met de database te maken hadden. De vraag bij een
tierovergang is daarom nooit "vertaal ik dit bestand?" maar **"welk deel hiervan gaat over de
database, en welk deel niet?"** (**WZ-ARC-02**).

### 5.3 Concreet — componenten, techstack en dataflow

**Eén fork kiest exact één databasetier**, bepaald op build/deploytijd door de repository-variabele
`DatabaseTier` (`SqlServer` of `Postgres`; zie §8.4). De frontend, auth-laag en algemene
architectuur zijn voor beide tiers identiek; alleen de backend-boom en het databaseschema verschillen.

```
Browser (beheerder)
  └── Azure Static Web Apps (Free tier) — Blazor WebAssembly
        Serveert alleen statische bestanden; geen SWA-proxying naar de API
        MSAL: Bearer token wordt automatisch meegestuurd naar de Function App
        │
        │ HTTPS + Bearer token (Entra ID)
        ▼
  Azure Functions (Linux Consumption plan) — net9.0, isolated worker
        Easy Auth: valideert Bearer token, injecteert X-MS-CLIENT-PRINCIPAL
        EasyAuthHelper: checkt 'admin' rol op alle /api/beheer/*, /api/test/*, /api/feedback/*
        │
        ├── DatabaseTier=SqlServer          ├── DatabaseTier=Postgres
        │   FunctionApp/                    │   FunctionApp.Postgres/
        │   (gelijkwaardige tier)           │   (tier die in productie draait sinds #976)
        │                                   │
        ▼                                   ▼
  Azure SQL (Free tier, 32 GB)         Postgres (Docker lokaal / Supabase cloud)
    dbo.AppSettings + AppSettingsAudit   public.appsettings
    dbo.Velden, VeldBeschikbaarheid      public.velden, veldbeschikbaarheid
    planner.EmailVerwerking             planner.emailverwerking
    his.* / stg.* / pub.* (ETL)         his.* / stg.* (ETL, geen pub.*-views — zie §11)
        │                                   │
        └───────────────┬───────────────────┘
                         ▼
              Sportlink REST API (data.sportlink.com)
                alleen-lezen sync (beide tiers) +
                schrijfrichting webapp→Sportlink Club (Postgres-tier
                sinds epic #986, sinds #1266 beide gebouwde tiers, zie §5.5)
```

**Technologiestack:** `FunctionApp` `net9.0` (SQL Server-tier) · `FunctionApp.Postgres` `net9.0`
(Postgres-tier) · `BlazorAdmin` `net10.0` · `Planner.Shared` (tier-agnostische bibliotheek) ·
Azure Functions v4 · Blazor WebAssembly · Azure SQL / Postgres · Microsoft Graph API · OpenAI
(direct, model via configuratie) · Azure Static Web Apps · Entra ID (single-tenant)

**Runtimeversies zijn niet uitwisselbaar — niet upgraden zonder infrastructuurwijziging (#579).**

| Project | Target | Reden |
|---|---|---|
| `FunctionApp/fa-dev-sportlink-01.csproj` | **`net9.0`** | Linux Consumption Plan ondersteunt `net10.0` niet → 503 "Function host is not running" |
| `FunctionApp.Postgres/FunctionApp.Postgres.csproj` | **`net9.0`** | Zelfde beperking als hierboven |
| `BlazorAdmin/BlazorAdmin.csproj` | `net10.0` | Browser-runtime, geen Azure-beperking |
| Azure Portal runtime | `DOTNET-ISOLATED\|9.0` | Moet overeenkomen met csproj |

`.NET 10` voor Azure Functions vereist het **Flex Consumption Plan** — een ander plan, dus een
planwijziging vraagt altijd expliciete goedkeuring van de eigenaar (kostenbeleid, CLAUDE.md). Zie
epic #1063 voor de migratie. **Dit is een toestand met een einddatum, geen eindsituatie:** .NET 9
gaat op 10 november 2026 uit support en is de laatste .NET-versie die Linux Consumption krijgt; dat
plan zelf wordt op 30 september 2028 uitgefaseerd. In-place migratie naar Flex bestaat niet — er
moet een nieuwe Function App komen, eerst nog op `net9.0`, pas daarna de csproj's en de
stackconfiguratie naar `net10.0`. Zie ook §11 (risico's).

**ETL-data flow (identiek patroon op beide tiers):**
```
Sportlink REST API → Azure Function → stg.* (staging, per run leeggemaakt)
                                    → merge-orchestrator → his.* (persistent)
                                                          → pub.* (SQL Server: read-only views;
                                                                    Postgres: geen consumenten, §11)
```

**Auth-stroom (identiek op beide tiers, uitgewerkt in §8.2):**

| Laag | Mechanisme |
|---|---|
| Frontend | MSAL (`AddMsalAuthentication`) + `AuthorizationMessageHandler` |
| Transport | Bearer token in `Authorization`-header |
| Function App | Azure Easy Auth (AllowAnonymous mode) + `EasyAuthHelper.RequireAdmin()` |
| Lokaal (dev) | Bypass: `WEBSITE_SITE_NAME` afwezig → altijd toestaan |

### 5.4 Database — schema's en conventies per tier

> **Volledige tier-strategie, bouwvolgorde en sub-issue-index:
> [ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md).** Dit is uitsluitend het
> schema-overzicht; de strategie zelf niet hier dupliceren.

**SQL Server (`FunctionApp`):**

| Schema | Doel |
|---|---|
| `dbo` | Configuratie: `AppSettings`, `Season`, `DateTable`, `Speeltijden` |
| `stg` | Tijdelijke staging-tabellen; worden elke sync-run leeggemaakt |
| `his` | Persistente historietabellen met `mta_inserted` / `mta_modified` metadata |
| `mta` | `source_target_mapping`-tabel die dynamische DDL en MERGE-operaties aanstuurt |
| `pub` | Alleen-lezen views voor consumers |
| `planner` | E-mailverwerking en planning |
| `avg` | AVG-beschermde data (teambegeleiding); toegang beperkt |

**Postgres (`FunctionApp.Postgres`) — de tier die in productie draait sinds #976:**

| Schema | Doel |
|---|---|
| `public` | Configuratie én de meeste beheertabellen: `appsettings`, `velden`, `speeltijden`, `teams`, `teamaliassen` (dbo-equivalent) |
| `stg` | Staging, zelfde rol als SQL Server |
| `his` | Historietabellen — `bk_*`-sleutelkolommen zijn hier `GENERATED ALWAYS AS (...) STORED`, niet een gewone, ETL-gevulde kolom |
| `planner` | E-mailverwerking en classificatiecorrectie |
| `avg` | AVG-beschermde data — identieke functie als SQL Server |
| *(geen `mta`, geen `pub`)* | Merge-orchestratie leeft in C# (`PostgresMergeOrchestrator`), niet in een mappingtabel; de drie `pub.*`-rapportageviews zijn bewust niet vertaald — nul consumenten gevonden, zie §11 |

**Naamconventies:**
- Entity-properties in C# gebruiken **camelCase** overeenkomstig de Sportlink API JSON-veldnamen, op beide tiers.
- SQL Server: kolomnamen gebruiken de **exacte casing** zoals gedefinieerd in het schema (bijv. `SportlinkApiUrl`).
- Postgres: kolom- en tabelnamen zijn **altijd lowercase snake_case, nooit gequote** (bijv.
  `sportlinkapiurl`) — Postgres vouwt een ongequote identifier automatisch naar lowercase, waardoor
  een latere gequote referentie (`"ClubCode"`) niet meer matcht. `KnownEntities.cs`/
  `EntityDefinition.Create` valideert dit al bij het schrijven van een nieuwe entiteit.
- Configuratie leeft in de settingstabel van de actieve tier, niet in code of config-bestanden.

**Stored procedures / equivalenten:** SQL Server gebruikt `sp_CreateTargetTableFromSource`
(dynamische DDL) en `sp_MergeStgToHis` (UPSERT via `MERGE`); Postgres implementeert dezelfde stappen
in C# (`PostgresSchemaGenerator`, `PostgresMergeOrchestrator`) — geen Postgres-functie/-procedure,
dezelfde architectuurbeslissing als de AVG-opschoonprocedures.

**Async/await:** alle I/O is asynchroon op beide tiers. Exceptie-handling op function entry-points,
niet diep in helperfuncties.

**Database-migraties:** `deploy.yml` past migraties op beide tiers automatisch toe vóór de code live
gaat — het PostDeployment-script voor SQL Server, `Database.Postgres.Cli` (idempotent, advisory
lock, checksum-bewaakt) voor Postgres. De smoke test faalt op een niet-lege `pendingMigrations` in
`/api/health`. **Ontwerpregel:** een migratie die de *vorige* code breekt (kolom weg, type
gewijzigd, constraint aangescherpt) mag niet in dezelfde release als de code die hem nodig heeft —
zie `docs/ARCHITECTUUR-DATABASE-TIERS.md` §57.

### 5.5 Sportlink Web Extension — architectuurplaatsing

> **Volledig protocol, endpoint-contracten en de agent-tokengrens:
> [SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md).** Dit is uitsluitend de
> architectuurplaatsing — het volledige mechanisme niet hier dupliceren.

Sinds epic #986 heeft de applicatie, naast de bestaande alleen-lezen ETL-sync (Sportlink → eigen
database), ook een **schrijfrichting**: de Admin GUI kan wijzigingen (kleedkamertoewijzing, veld, en
meer) rechtstreeks terugschrijven naar Sportlink Club namens de club. De extensie is eerst alleen op
de Postgres-tier gebouwd; #1266 heeft de ontbrekende endpoints en timers alsnog op de SQL
Server-tier gezet, omdat beide gebouwde tiers gelijkwaardig zijn (WZ-ARC-03).
`scripts/ci/check-tier-pariteit.sh` bewaakt sindsdien dat een `/sportlink/*`-route niet op één tier
kan blijven bestaan.

**Kernonderdelen:**
- `Planner.Shared/Integrations/SportlinkClub/SportlinkClubClient.cs` — centrale HTTP-client voor
  alle mutaties, met een fetch-snapshot-en-echo-patroon: eerst het volledige actuele record ophalen,
  dan alleen het gewijzigde veld overschrijven en het geheel terugsturen — Sportlink accepteert geen
  partiële updates op meerdere onderzochte endpoints.
- `SportlinkMutationGuard` / `ISportlinkMutationAuditService` — elke schrijfactie wordt afgedwongen
  en gelogd vóór uitvoering.
- Rolgebaseerde serviceaccount-koppeling per club: de club kiest zelf welke Sportlink-rol de
  extensie gebruikt, geen gedeelde of hardcoded credential.

**Harde regel — de agent-tokengrens (WZ-SEC-09).** Een coding agent (Claude Code) mag **nooit** zelf
een Sportlink refresh- of accesstoken lezen, vasthouden of gebruiken om de Sportlink API aan te
roepen — dit is zowel technisch afgedwongen als beleidsmatig vastgelegd. Een token dat toch
zichtbaar wordt in een agent-sessie (ook via een paste van de gebruiker zelf) geldt als verbrand en
mag nooit worden opgeslagen, gelogd of hergebruikt — alleen de niet-geheime payload (bijv. een
JSON-body uit een netwerktrace) mag geëxtraheerd worden. Het token zelf leeft uitsluitend in de
FunctionApp.Postgres-runtime, gecaptured door een mens via de Instellingen-UI.

**"Geen aannames"-principe (WZ-INT-02).** Het schrijf-contract van een Sportlink-endpoint wordt
nooit gefabriceerd. Elke mutatie-implementatie is gegrond in een echte, door de eigenaar
aangeleverde netwerktrace van de Sportlink Club-UI zelf — nooit een geraden JSON-schema. Zie
`docs/SPORTLINK-WEB-EXTENSION.md` §4 voor de geschiedenis van gevonden en gecorrigeerde aannames.

**AVG-grens specifiek voor deze laag:** het Sportlink Match-detailendpoint bevat officials-PII
(naam, geboortedatum, foto). Diagnose van dit endpoint gebeurt uitsluitend via veld-scoped extractie
(bijv. `JsonDocument`-gebaseerde single-field lookup) — nooit een volledige response-body-log, ook
niet tijdelijk.

---

## 6. Runtime-scenario's

### 6.1 Aanmelden en autoriseren

1. De browser haalt bij de identiteitsprovider een token op.
2. Elke API-aanroep draagt dat token.
3. Het hostingplatform valideert het token en zet een gevalideerde identiteit door naar de functie.
4. De functie controleert de rol en beslist. **Dit is de autorisatiegrens.**
5. De interface verbergt wat je niet mag zien — als gebruiksgemak, nooit als beveiliging.

Volledige concrete uitwerking (vijf verdedigingslagen, 3-user-test, Blazor auth-gate,
MSAL-checklist): §8.2.

### 6.2 Een beheeractie

Rolcontrole → correlatie-identificatie vastleggen → databasebewerking → antwoord. Bij een fout: het
technische detail gaat naar het log, de aanroeper krijgt een gestandaardiseerd foutobject zonder
interne details (**WZ-API-02**).

### 6.3 Dagelijkse synchronisatie

Timer → poort voor uitgaand verkeer → externe databron ophalen → staging → samenvoegen → historie.
Er is ook een handmatige ingang; **die passeert dezelfde poort** (**WZ-INT-01**).

### 6.4 Verwerking van inkomende e-mail

Staat standaard uit. Bij inschakeling: postbus lezen → uitsluitingslijst opnieuw toepassen vlak vóór
de externe aanroep → classificeren → voorstel → menselijke bevestiging. De uitsluitingslijst wordt
tweemaal geraadpleegd, waarvan één keer met een verse lezing, zodat een net uitgesloten adres er niet
alsnog doorheen glipt. Is die lijst niet leesbaar, dan gebeurt er niets (**WZ-AI-03**).

### 6.5 Publicatie van door AI gegenereerde inhoud

De beheerder ziet **exact** de tekst die gepubliceerd wordt, en bevestigt. De bevestiging draagt díe
tekst terug in plaats van opnieuw te genereren — een tweede generatie zou andere tekst opleveren en
de voorvertoning tot een gok maken (**WZ-AI-04**).

### 6.6 Berichtverwerking — kanaal-agnostische pipeline

De verwerkingspipeline (classificeer → valideer → verwerk → bouw antwoord) is kanaal-onafhankelijk.
Welk kanaal de input levert (e-mail, dry-run, WhatsApp, Socials) maakt niet uit voor de kern van de
logica. Dit geldt op beide tiers: `BerichtPipeline`, `BerichtAiService`, `BerichtResponseGenerator`
en `EmailProcessorFunction` bestaan zowel in `FunctionApp/` als in `FunctionApp.Postgres/`. Volledige
pipeline, AI-classificatie en templates: [EMAIL-VERWERKING.md](EMAIL-VERWERKING.md).

**Elke nieuwe kanaal-koppeling:**
1. Implementeert een input-adapter: kanaalbericht → kanaal-agnostisch inputmodel (`InkomendBericht`).
2. Voert kanaalspecifieke guards uit (idempotency, domeinfilter).
3. Roept de gezamenlijke pipeline aan.
4. Verwerkt het resultaat via de kanaalspecifieke output-router.

Nooit de pipeline herhalen of pipeline-methoden direct aanroepen vanuit een nieuw kanaal.

---

## 7. Deployment en installatie

### 7.1 Doelomgeving

Eén omgeving per installatie: serverloze functie-app, statische webhosting, beheerde database,
opslagaccount, en een appregistratie bij de identiteitsprovider. Er is geen aparte test- of
acceptatieomgeving; dat volgt uit B2 en is vastgelegd als **WZ-ADR-007**.

```
[GitHub fork, club-specifieke secrets]
  │  push → deploy.yml
  ▼
Azure Functions  ← eigen Function App
Azure SQL / Postgres ← eigen database met dezelfde schema's
Azure SWA        ← eigen static web app
Entra ID         ← eigen App Registration (single-tenant)
```

Elke club heeft een volledig geïsoleerde Azure-omgeving. Er is geen shared infrastructure — zie §2.1.

### 7.2 Installatiepatroon zonder waarden in de repository

Dit is de kern van randvoorwaarde B1 en geldt voor elke fork.

1. Maak de appregistratie en de resources volgens de publieke installatiehandleiding.
2. Maak **buiten** de gekloonde repository een privéparameterbestand met uitsluitend de waarden van
   deze installatie. De handleiding toont alleen plaatshouders.
3. Meld lokaal aan bij het cloudplatform en voer de infrastructuurdefinitie eerst uit in
   *voorbeeldmodus*, daarna pas werkelijk.
4. Controleer na de uitrol de authenticatie-instellingen en het gedrag van een beheerendpoint zonder
   geldige aanmelding, vóórdat de installatie als gereed geldt.
5. Actualiseer of verwijder het privébestand bij een wijziging. Deel het nooit via de repository,
   een issue, een pull request of een buildlog.

### 7.3 Uitrol van wijzigingen

Wijziging → bouwen → tests en controles → databasemigraties → code → rookproef. Migraties gaan
vóór de code (§5.4). Een migratie die de vorige codeversie breekt, hoort niet in dezelfde uitrol als
de code die haar nodig heeft.

**De Security Gate is leidend.** Zolang de check `Security Gate — blokkeert merge bij fout` rood is,
mag er niets gemerged worden — ook niet als alle andere checks groen zijn. Geldt ongeacht welke tier
een PR raakt.

**GitHub Actions checks bij elke PR:**

| Check | Wat |
|---|---|
| Secret Detection (gitleaks) | Detecteert hardcoded secrets en tokens |
| PII File Detection | Blokkeert CSV/Excel-bestanden |
| PII Pattern Scan | Scant op AVG-gevoelige patronen (e-mails, BSN, telefoonnummers) |
| PII in Documentatie | Controleert CHANGELOG.md en docs op PII |
| Club-infrastructuur patrooncheck | Blokkeert club-identificerende resourcenamen, hostnames en GUID's in code en documentatie |
| Dependency Vulnerability Scan (Trivy) | Scant NuGet-packages op bekende CVE's |
| `fresh-db` / `fresh-db-postgres` | Verse-database-verificatie per tier: kernobjecten, identifier-casing, demodata-aantallen; `fresh-db-postgres` draait bovendien de RLS-guard en Supabase's splinter-linter tegen een levende database |
| Tier-pariteit | Vergelijkt de routes van beide tiers in beide richtingen; een verschil moet met een reden in een allowlist staan |
| Security Gate | Aggregeert alle bovenstaande checks — merge-blokkade bij fout |

Daarnaast draait een dagelijkse workflow buiten de PR-keten om die de Supabase Security- en
Performance Advisor ophaalt en nieuwe EXTERNAL-bevindingen op ERROR/WARN-niveau meldt.

**Na een PR-merge naar `main`:** elke deploy-job wordt individueel geverifieerd (niet alleen het
totale run-resultaat), en gevolgd door een browser-rendercheck op de live Admin GUI — groene CI en
HTTP 200 bewijzen niet dat de Blazor-app daadwerkelijk rendert. Zie **WZ-QUA-05** en CLAUDE.md voor
de exacte commando's.

### 7.4 Versiebeheer

Semantic Versioning met vier cijfers en twee fasen (development bumpt per commit, release bumpt één
keer bij `develop` → `main`). Volledige regels, conventional-commit-mapping en
CHANGELOG-richtlijnen: **[VERSIONING.md](VERSIONING.md)** — gezaghebbend, niet hier dupliceren.

**Eén concrete valkuil die de moeite waard is om hier te benoemen:** bij een release worden alle
drie de csproj's synchroon gebumpt — `FunctionApp/fa-dev-sportlink-01.csproj`,
`BlazorAdmin/BlazorAdmin.csproj` én `FunctionApp.Postgres/FunctionApp.Postgres.csproj`. De derde
wordt gemakkelijk gemist als een wijziging alleen Postgres-tier-bestanden raakt, want geen van de
andere twee verandert dan mee en niets waarschuwt ervoor vóór een release.

---

## 8. Overkoepelende concepten

Dit hoofdstuk beschrijft de regels in gewone taal, met direct daaronder de concrete uitwerking
(checklists, codevoorbeelden, tabellen) die nodig is om de regel toe te passen. §10 bevat dezelfde
regels als toetsbaar register met externe basis en bewijsvorm. **Bij twijfel is §10 leidend, want
daar staat hoe je het controleert.**

### 8.1 Gegevens en privacy

Clubgegevens dragen een clubdiscriminator en bewerkingen filteren daarop. Tijdstempels worden in UTC
opgeslagen en pas in de interface omgezet. Nieuwe tabellen in de Postgres-tier schakelen rij-niveau
beveiliging in binnen dezelfde migratie — niet als applicatie-autorisatie, maar om te voorkomen dat
het platform de tabel via zijn eigen automatisch gegenereerde interface openstelt.

**Elke tabel met persoonsgegevens heeft een vastgelegde bewaartermijn, een fase en een motivering** —
inclusief het geval waarin de motivering "geen termijn" luidt; die krijgt dan een herzieningstrigger.
De termijn is configuratie, zodat de verantwoordelijke hem kan vaststellen zonder nieuwe versie.

Persoonsgegevens, exports en lokale geheimen gaan nooit in de repository.

#### 8.1.1 Tijdzones — UTC in database, lokale tijd in GUI

**Alle drie lagen moeten correct zijn. Een fout in één laag stapelt offsets op.** De regel is
identiek op beide tiers; alleen het databasemechanisme verschilt.

| Laag | SQL Server | Postgres |
|---|---|---|
| **Database** | `GETUTCDATE()` — **nooit `GETDATE()`** | Audit-kolommen zijn `TIMESTAMPTZ` (niet naïef `TIMESTAMP`) — Postgres normaliseert een `TIMESTAMPTZ`-waarde intern altijd naar UTC, ongeacht de sessietijdzone |
| **FunctionApp API** | `DateTime.SpecifyKind(dt, DateTimeKind.Utc)` na elke SQL-read | Npgsql leest een `TIMESTAMPTZ`-kolom terug als `DateTime` met `Kind=Utc` — geen aparte `SpecifyKind`-aanroep nodig |
| **Blazor WASM** | `.ToLocalTime()` vóór elke `.ToString()` | Idem — ongewijzigd, tier-onafhankelijk |

```csharp
"UPDATE [dbo].[AppSettings] SET [LastSyncTimestamp] = GETUTCDATE()"

DateTime? ts = reader["LastSyncTimestamp"] != DBNull.Value
    ? DateTime.SpecifyKind(Convert.ToDateTime(reader["LastSyncTimestamp"]), DateTimeKind.Utc)
    : null;
// JSON-output: "2026-05-21T13:35:00Z"
```

**Incident (2026-05-21, SQL Server):** `GETDATE()` in `SaveLastSyncTimestampAsync` en 5 andere
C#-bestanden sloeg CEST-tijd op; de API markeerde het daarna als UTC en Blazor telde nog eens 2 uur
op. Fix in PR #246. **Zelfde klasse fout empirisch bevestigd voor Postgres:** een naïeve
`TIMESTAMP`-kolom + `NOW()` week 2 uur af van de werkelijke UTC-tijd op een
`Europe/Amsterdam`-sessietijdzone — reden voor de `TIMESTAMPTZ`-keuze hierboven.

Codereview-check: elke `INSERT`/`UPDATE` die een `DateTime`-kolom schrijft gebruikt `GETUTCDATE()`
of `DateTime.UtcNow`; elke API-response heeft een `Z`-suffix; elke Blazor-weergave heeft
`.ToLocalTime()` vóór `.ToString()`.

#### 8.1.2 Multi-club isolatie — ClubCode discriminator

Elke tabel met club-specifieke data krijgt een ClubCode-kolom (SQL Server: `ClubCode`; Postgres:
`clubcode`, casing per §5.4). Queries filteren altijd op de ClubCode uit de settingstabel van de
actieve tier.

```sql
-- SQL Server — correct
SELECT * FROM [dbo].[TeamVoorkeurTijden]
WHERE [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings])

-- Postgres — correct
SELECT * FROM public.teamvoorkeurtijden
WHERE clubcode = (SELECT clubcode FROM public.appsettings LIMIT 1)

-- Fout op beide tiers: hardcoded waarde
WHERE [ClubCode] = 'ABC'  /  WHERE clubcode = 'abc'
```

#### 8.1.3 Geen club-specifieke waarden in code

Fallback-waarden (`?? "..."`) in C# mogen **nooit** een clubnaam, domeinnaam, persoonsnaam,
plaatsnaam of adres bevatten. Ontbreekt een verplichte instelling in de settingstabel, dan gooit de
code een `InvalidOperationException` — geen stille fallback.

```csharp
// Correct — faalt snel bij ontbrekende configuratie
var clubCode = GetSetting("clubCode")
    ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt");

// Fout — maskeert misconfiguratie en breekt andere clubs
var clubCode = GetSetting("clubCode") ?? "ABC";
```

Documentatie-voorbeelden gebruiken `[ClubNaam]` als placeholder, nooit een echte waarde.

#### 8.1.4 AVG/GDPR — absolute regels

Deze regels gelden altijd, op beide tiers, ook voor geautomatiseerde processen.

- `exports/*.csv` en `exports/*.xlsx` bevatten persoonsgegevens en mogen **nooit** gecommit of
  gepusht worden. `.gitignore`, pre-commit hook, pre-push hook en de Security Gate blokkeren dit
  elk onafhankelijk.
- Alleen scripts (`.ps1`), `README.md` en handleidingen mogen in de `exports/`-map in git.
- Logging van persoonsgegevens is verboden: geen namen, geboortedatums, foto's of e-mailadressen in
  `ILogger`-output — ook niet tijdelijk tijdens diagnose van een Sportlink-endpoint (§5.5).
- E-mailadressen van leden worden uitsluitend via **BCC** gebruikt bij communicatie met derden.
- GitHub issues, PR's en commits bevatten nooit echte e-mailadressen, namen, accounts of
  club-specifieke locaties. Gebruik placeholders: `<admin-account>`, `@uwclub.nl`,
  `[CoordinatorNaam]`, `[Accommodatienaam]`.
- Retentiebeleid voor e-mailverwerking: anonimiseren na 30 dagen, verwijderen na 90 dagen.

**Git hooks activeren (verplicht bij elke nieuwe developer-machine):**
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

#### 8.1.5 AVG-veilige testdata — de uitputtende uitzonderingslijst (WZ-DAT-06)

Vaste, formeel goedgekeurde placeholders — het **John Doe-principe**: bewust niet-identificeerbaar,
niet gebonden aan een bestaand persoon of domein. Elke andere naam, elk ander e-mailadres of domein
in code geldt als potentieel persoonsgegeven.

**Voor UI-defaults van admin-only developer-testpagina's:**

| Waarde | Type |
|---|---|
| `Jan de Vries` | Fictieve naam (NL-equivalent van "John Doe") |
| `trainer@voorbeeld.nl` | Fictief e-mailadres (`.voorbeeld.nl` bestaat niet publiek; opgenomen in `.gitleaks.toml`) |

Uitsluitend als hardcoded UI-default in admin-only developer-testpagina's — nooit in
bedrijfslogica, API-fallbacks of gedeelde configuratie.

**Voor de seed-migratie van de AllStars FC-democlub** (`scripts/migrations/002-seed-allstars-fc.sql`,
`ClubCode = 'ALLSTARS'`, zie §2.1) geldt een aparte, eveneens formeel goedgekeurde uitzondering:

| Kenmerk | Waarde | Reden |
|---|---|---|
| Domein | `@allstars-fc.test` | `.test` is een gereserveerd TLD (RFC 2606) — kan nooit een echt e-mailadres worden |
| Namen | Generieke voornamen zonder achternaam (bijv. `Frenkie`, `John`) | Niet herleidbaar tot een bestaand persoon |
| Scope | Uitsluitend rijen met `ClubCode = 'ALLSTARS'` | Nooit gebruikt voor een echte club |

Nieuwe seed-rijen voor AllStars FC volgen hetzelfde patroon. Bij een breder e-mailpatroon in
`.gitleaks.toml`: controleer of `@allstars-fc\.test` een allowlist-entry nodig heeft.

### 8.2 Beveiliging en toegang

De identiteitsprovider bewijst identiteit; **de applicatie beslist over toegang**. Een zichtbare
interfacegrens is geen beveiligingsgrens. Een ontwikkelbypass is expliciet, beperkt tot de
ontwikkelomgeving en faalt dicht bij twijfel.

Een controle die beweert een autorisatiepatroon af te dwingen, moet **beide** tierbomen kennen én de
gebruikte hulpconstructies begrijpen. Een tekstuele controle die structureel vals alarm geeft, wordt
vervangen — want een controle die altijd afwijkt, leert de uitvoerder afwijkingen te negeren.

#### Rollenmatrix — de huidige werking

| Aanmelder | Beheerinterface | Beheer-API | Betekenis |
|---|---|---|---|
| Niet ingelogd | Omgeleid naar aanmelden | Geweigerd | Geen beheerder |
| Ingelogd, geen rol | Toegang geweigerd | Geweigerd | Geen beheerder |
| `user` | Interface laadt | Geweigerd op alle endpoints | **Onvolledig** — geen ondersteunde functie |
| `admin` | Volledig | Toegang | De beheerder |
| `wedstrijdzaken` | Geen toegang tot de beheerapp | Alleen de daarvoor bedoelde endpoints | Functionele rol |

De regel `user` is vandaag zonder effect: de interface laat hem binnen, de API weigert alles. Dat is
**te weinig toegang, geen lek** — maar code en documentatie beschrijven het verschillend, en dat is
een openstaand punt (**WZ-SEC-05**, §11).

#### 8.2.1 Defense in depth — vijf lagen, allemaal verplicht

`IsAuthenticated = true` is **niet** voldoende: een gebruiker in de Entra-tenant kan inloggen zonder
app-rol. Alle vijf lagen moeten onafhankelijk correct werken — een ontbrekende laag is een
security-incident.

| Laag | Wat | Waar | Status |
|---|---|---|---|
| 1 | **Tenant-restrictie** — Single tenant App Registration; externe tenants kunnen niet inloggen | Azure Portal → Entra ID → App registrations | ✓ Aanwezig |
| 2 | **Assignment required = Yes** — alleen pre-toegewezen gebruikers krijgen een token | Azure Portal → Enterprise applications → Properties | ⚠️ Per-deploy verifiëren |
| 3 | **App Roles** — `admin` en `user` gedefinieerd in App Registration manifest, `allowedMemberTypes: ["User"]` | Azure Portal → App registrations → App roles | ⚠️ Per-deploy verifiëren |
| 4 | **Frontend role-gate** — `IsInRole("admin") \|\| IsInRole("user")` BOVENOP `IsAuthenticated`; zonder rol → `NoAccess`-pagina, géén `MainLayout`. Beslissing als pure, teste functie (`AuthGate.Bepaal`), niet inline in de pagina | `BlazorAdmin/Services/AuthGate.cs` + `BlazorAdmin/App.razor` | ✓ Verplicht in code, **getest** |
| 5 | **Backend role-gate (EasyAuthHelper)** — elke admin-endpoint loopt via `AdminEndpoint.ExecuteAsync`, dat `RequireAdmin()` centraal aanroept (#1350; CI-guard `check-endpoint-autorisatie.sh`); valideert `roles`-claim in `X-MS-CLIENT-PRINCIPAL` | `FunctionApp/Admin/EasyAuthHelper.cs` (identieke kopie in `FunctionApp.Postgres/Admin/`) | ✓ Verplicht in code |

**De server is de waarheid.** Een aanvaller kan de Blazor WASM modificeren. Laag 5 is leidend voor
databeveiliging. Laag 4 is voor UX (geen app-shell voor niet-geautoriseerde gebruikers).

**Verplichte 3-user-test bij elke auth-gerelateerde wijziging:**

| Testgebruiker | Configuratie | Verwacht resultaat |
|---|---|---|
| Admin (eigen tenant) | Toegewezen met rol `admin` | Volledige UI, alle API-calls slagen |
| Gebruiker (eigen tenant) | Toegewezen met rol `user` | UI laadt, mutaties geblokkeerd |
| Geen rol (eigen tenant) | Niet toegewezen | `NoAccess`-pagina; géén sidebar, navigatie of FEEDBACK-knop |
| Externe gebruiker (andere tenant) | n.v.t. | Kan niet inloggen — Entra weigert vóór redirect |

Documenteer per release welke 3-user-tests zijn uitgevoerd. Een security-wijziging zonder deze tests
wordt niet geaccepteerd.

`CustomUserFactory` is verplicht: Blazor WASM cast een `"roles": ["admin"]` JSON-array uit het
ID-token naar één claim met de JSON-string als waarde, waardoor `IsInRole("admin")` altijd `false`
retourneert ook al staat de rol in het token. De factory pakt de array uit naar losse claims
(`.AddAccountClaimsPrincipalFactory<CustomUserFactory>()`). Daarnaast is
`options.UserOptions.RoleClaim = "roles"` verplicht: Entra schrijft app-rollen in de claim `roles`,
niet in `ClaimTypes.Role`.

#### 8.2.2 Blazor auth-gate — altijd boven de Router, nooit erin

**Kritieke regel — drie keer overtreden (PR #178, PR #179, auth-redirect-loop hotfix).** De Blazor
Admin UI mag nooit zichtbaar zijn voor niet-ingelogde gebruikers — ook niet kortstondig, ook niet de
sidebar, navigatie of FEEDBACK-knop. Een ongeauthenticeerde gebruiker moet binnen 2–3 seconden naar
de Microsoft-loginpagina worden gestuurd.

**Verboden patroon** (`AuthorizeRouteView` rendert `MainLayout` voor **alle** states, inclusief
Authorizing en NotAuthorized):
```razor
<AuthorizeRouteView DefaultLayout="@typeof(MainLayout)">
    <NotAuthorized><RedirectToLogin /></NotAuthorized>
```

**Verboden anti-patroon** — een blocking delay (bijv. een health-check) vóór de auth-check: de
auth-check start dan pas ná die vertraging, MSAL silent-SSO faalt in InPrivate-sessies, en
`NavigateToLogin` wordt te laat aangeroepen.

**Verplicht patroon:**
```razor
@if (_state == AppState.Initializing)      { @* spinner, geen layout *@ }
else if (_state == AppState.OnAuthRoute)   { <Router><RouteView /></Router> @* geen layout *@ }
else if (_state == AppState.Authenticated) { <Router><RouteView DefaultLayout="MainLayout" /></Router> }
@* RedirectingToLogin: NavigateToLogin is al aangeroepen, geen UI nodig *@
```

**Implementatieregels:** `App.razor` roept `GetAuthenticationStateAsync()` aan als **eerste** actie,
vóór elke andere check of splash. `MainLayout` rendert alleen voor de `Authenticated`-state.
`/authentication/...`-routes krijgen een aparte `Router`-branch zonder layout.
`options.ProviderOptions.LoginMode = "redirect"` — geen popup-mode (geblokt in Incognito/InPrivate).

**Verificatie bij elke Blazor auth-wijziging:** open een verse Incognito-sessie → Microsoft-login
verschijnt binnen 2–3s → vóór login géén sidebar/navigatie/FEEDBACK-knop → na login volledige UI met
werkende API-calls → F12/Network bevestigt de redirect naar `login.microsoftonline.com`.

#### 8.2.3 MSAL-configuratie checklist

Elk van deze items moet aanwezig zijn in een werkende deployment; een gemist item veroorzaakt een
vastlopende login.

| # | Item | Locatie | Reden |
|---|---|---|---|
| 1 | `<script src="_content/Microsoft.Authentication.WebAssembly.Msal/AuthenticationService.js">` vóór `blazor.webassembly.js` | `wwwroot/index.html` | MSAL JS-bridge — zonder dit script doet `RemoteAuthenticatorView` niets |
| 2 | `options.ProviderOptions.LoginMode = "redirect"` | `Program.cs` | Voorkomt popup-blocker failures in InPrivate/Incognito |
| 3 | `options.UserOptions.RoleClaim = "roles"` | `Program.cs` | Entra schrijft rollen in de claim `roles`, niet in `ClaimTypes.Role` |
| 4 | `.AddAccountClaimsPrincipalFactory<CustomUserFactory>()` | `Program.cs` | Pakt `"roles": ["admin"]` JSON-array uit naar losse claims |
| 5 | `appsettings.Production.json` met `AzureAd.Authority`/`ClientId` | `wwwroot/` | Gegenereerd door CI — zonder deze waarden crasht MSAL bij initialisatie |
| 6 | `<WasmApplicationEnvironmentName>Production</WasmApplicationEnvironmentName>` voor Release | `BlazorAdmin.csproj` | Zonder dit laadt Blazor `appsettings.json` (localhost) in productie |
| 7 | `<CompressionEnabled>false</CompressionEnabled>` | `BlazorAdmin.csproj` | Azure SWA serveert pre-compressed `.wasm.br` zonder correcte `Content-Encoding: br` → SRI-check faalt in Chrome Incognito |
| 8 | SPA redirect URI `https://<host>/authentication/login-callback` in App Registration | Azure Portal | Entra weigert de redirect als deze URI ontbreekt |
| 9 | `Authentication.razor` op `@page "/authentication/{action}"` met `<RemoteAuthenticatorView>` | `Pages/` | Verwerkt MSAL login/logout-callback |
| 10 | Easy Auth ingeschakeld + `EasyAuthHelper.RequireAdmin()` op elk admin-endpoint, via `AdminEndpoint.ExecuteAsync` (#1350) | Azure Portal + `FunctionApp*/Admin/` | Server-side validatie van Bearer token en admin-rol |
| 11 | `Cache-Control: no-cache` voor `/index.html` en `/` | `staticwebapp.config.json` | Zonder dit cachet de browser een oude `index.html` die naar assets uit een eerdere deploy verwijst → 404's en SRI-mismatches |

#### 8.2.4 Secrets en configuratie

Productie-configuratie wordt **nooit** in git opgeslagen. De CI-pipeline genereert club-specifieke
configuratie automatisch vanuit templates en GitHub Secrets/Variables.

| Bestand | In git? | Toelichting |
|---|---|---|
| `BlazorAdmin/wwwroot/appsettings.Production.template.json` | ✓ | Bevat alleen `{{PLACEHOLDER}}`-tokens |
| `BlazorAdmin/wwwroot/appsettings.Production.json` | ✗ | Gegenereerd door CI vanuit template + GitHub Variables/Secrets |
| `BlazorAdmin/wwwroot/appsettings.json` | ✓ | Localhost-config, geen secrets |
| `FunctionApp/local.settings.json` | ✗ | Bevat `SqlConnectionString` en andere secrets |
| `FunctionApp/local.settings.template.json` | ✓ | Template zonder waarden |
| `FunctionApp.Postgres/local.settings.json` | ✗ | Bevat `POSTGRES_CONNECTION_STRING` |
| `FunctionApp.Postgres/local.settings.template.json` | ✓ | Template zonder waarden |
| `exports/*.csv` / `exports/*.xlsx` | ✗ | Persoonsgegevens — zie §8.1.4 |

**Club-identificerende configuratie — als GitHub Secret** (gemaskeerd in Actions-logs, die bij een
publieke repository voor iedereen leesbaar zijn): `AZURE_FUNCTIONAPP_NAME`, `AZURE_FUNCTIONAPP_URL`,
`AZURE_STATIC_WEB_APP_HOSTNAME`, `AZURE_AD_TENANT_ID`, `AZURE_AD_CLIENT_ID`,
`POST_LOGOUT_REDIRECT_URL`. Een bestaande Variable blijft werken (`${{ secrets.NAAM || vars.NAAM }}`),
maar alleen een Secret wordt gemaskeerd.

**GitHub Variables** (gebruikt in job-`if:`, waar de `secrets`-context niet beschikbaar is):
`DatabaseTier` (`SqlServer`/`Postgres`), `DatabaseTierSwitchConfirmation` (moet exact gelijk zijn aan
`DatabaseTier`, anders faalt de deploy met exitcode 3 — zie §8.4), en bij `SqlServer`:
`AZURE_SQL_SERVER_NAME`/`AZURE_SQL_RESOURCE_GROUP`.

**GitHub Secrets:** `AZURE_CREDENTIALS`, `AZURE_FUNCTION_KEY`, `AZURE_STATIC_WEB_APPS_API_TOKEN`
(beide tiers); `SQL_CONNECTION_STRING` (alleen SqlServer); `POSTGRES_CONNECTION_STRING` (alleen
Postgres — norm `sslmode=verify-full` mét CA-certificaat; ontbreekt dat, dan meldt `/api/health` een
`tlsWarning`).

Het **Sportlink refresh-token** (epic #986) is een apart, door de club-beheerder zelf via de
Instellingen-UI gecaptured secret, niet via GitHub Secrets — zie §5.5 en `docs/SECRET-ROTATION.md`.

**Entra App Registration** mag niet via de Azure Portal handmatig worden aangepast. Gebruik altijd
de idempotente scripts (`Verify-AzureAuthSetup.ps1` → `Configure-EntraApp.ps1 -WhatIf` →
`Configure-EntraApp.ps1`, zie `docs/ENTRA-AUTH-BEHEER.md`). Na elke wijziging: sluit alle
browsertabs van de Admin GUI en open een verse Incognito-sessie — MSAL bewaart het ID-token in
`localStorage`.

### 8.3 API-contract

De machineleesbare specificatie is onderdeel van het contract, niet de documentatie erover. Zij is
leidend; de JSON-variant en de aanroepcollectie worden eruit afgeleid. Bij elke wijziging aan een
endpoint gaan specificatie, afgeleiden, documentatie en tests in dezelfde wijziging mee.

**Foutafhandeling volgt RFC 9457** (Problem Details for HTTP APIs) met de HTTP-semantiek van
RFC 9110. Elke door de applicatie geproduceerde foutresponse heeft het bijbehorende contenttype en
de velden `type`, `title` en `status`; `detail` en `instance` zijn optioneel. Uitbreidingen zijn
toegestaan mits gedocumenteerd, en bevatten nooit persoonsgegevens, geheimen, stacktraces of interne
details. Statuscodes volgen hun internationale betekenis; een `401` draagt de bijbehorende
uitdaging, een `429` draagt `Retry-After` wanneer de wachttijd bekend is.

**Paginering en idempotentie zijn geen standaardlaag.** Paginering is verplicht zodra een lijst
zonder functionele bovengrens kan groeien; een van nature begrensde keuzelijst blijft één antwoord.
Idempotentie is verplicht voor een mutatie met een externe of zichtbare bijwerking die realistisch
opnieuw verstuurd kan worden — een timeout, een wachtrijherlevering, een herhaalbare beheeractie.
Een gewone enkelvoudige mutatie zonder dat scenario krijgt geen extra sleutel.

Routes krijgen **geen** versieprefix. Er is één consument, die in dezelfde uitrol meegaat. Een
contractwijziging wordt daarom in één keer in code, client, specificatie, collectie en tests
doorgevoerd (**WZ-ADR-008**).

### 8.4 Database-tiers en pariteit

Elke gebouwde tier is gelijkwaardig. Een functie bestaat op álle gebouwde tiers of op geen. Welke
tier een installatie draait, is een uitrolkeuze en zegt niets over de status van de andere.

Providergebonden implementaties mogen verschillen; pure, betekenisgelijke logica wordt gedeeld zodra
zij geen providerkennis bevat.

Pariteit wordt geautomatiseerd bewaakt, **in beide richtingen**. De bestaande schemacontroles keken
maar één kant op; dat klopte toen de ene tier leidend was, en bij de omslag draaiden de rollen om
maar de controles niet.

**Tier-keuze is onveranderlijk na de eerste deploy.** De repository-variabele `DatabaseTier`
bepaalt welke boom gebouwd en gedeployed wordt. Een tweede variabele,
`DatabaseTierSwitchConfirmation`, moet exact gelijk zijn aan `DatabaseTier` — bij een enkele, per
ongeluk gewijzigde `DatabaseTier` faalt de build hard (exitcode 3) in plaats van production
stilzwijgend naar een andere database te laten omschakelen. Een bewuste wissel vereist het expliciet
bijwerken van *beide* variabelen in dezelfde actie.

Schema-overzicht en naamconventies per tier: §5.4. Volledige strategie:
[ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md).

### 8.5 AI en externe verwerking

Productiecode gebruikt de provideronafhankelijke abstractie; dienst en model zijn configuratie. Elke
tijdgevoelige instructie krijgt de datum dynamisch mee, en voorbeelden daarin bevatten geen vast
kalenderjaar — anders ondermijnt het voorbeeld stilletjes de dynamische datum.

**Doorgifte van persoonsgegevens aan een externe AI-verwerker wordt vastgelegd**: welke velden, op
welke grondslag, met welke bewaartermijn aan de kant van de verwerker. Een automatisch filter op
persoonsgegevens is een vangnet en **nooit** een garantie; zijn bekende blinde vlekken worden
uitputtend opgeschreven náást het filter.

### 8.6 Kosten

De stack blijft binnen gratis tiers. Een nieuwe resource of een tierwijziging wordt vooraf getoetst
tegen de actuele leveranciersdocumentatie — nooit uit geheugen, omdat een leverancier een gratis
tier zonder aankondiging kan beëindigen. Kostbare onderdelen staan in de infrastructuurdefinitie
achter een schakelaar die standaard uit staat en alleen met een expliciete keuze aan kan, zodat de
beslissing een reviewbare wijziging is. Het volledige, actiegerichte kostenprotocol (verplichte
MS-Docs-prijscheck, stopprocedure bij twijfel) staat in `CLAUDE.md` — dat is Claude's operationele
uitvoering van dit principe, niet een tweede architectuurbron.

### 8.7 Kwaliteit en bewijs

De blokkerende beveiligingspoort is leidend en wordt niet gecompenseerd door groene overige
controles (concrete CI-checklist: §7.3). Elke structurele regel krijgt waar mogelijk een automatische
controle; kan dat nog niet, dan staat de regel in §10 met bewijs `ontbreekt`.

**Elke belangrijke controle bewijst ook dat hij kán falen.** Een negatieve test hoort bij de
invoering. Een controle die stilletjes nul meldt is gevaarlijker dan geen controle.

Er geldt **geen** generieke dekkingsdrempel: dekking is een signaal bij risicovolle of gewijzigde
code, geen zelfstandig criterium om te mogen samenvoegen.

**Lagen altijd synchroon.** Database-schema, API-endpoint en Blazor GUI worden altijd in dezelfde
commit bijgewerkt, op de tier waar de wijziging landt: een nieuw databaseveld krijgt in dezelfde PR
zijn API-veld en Blazor-weergave; een nieuwe enum, template-sleutel of regeltype in code krijgt in
dezelfde commit zijn GUI-optie. Nooit een GUI die verwijst naar een API-veld dat nog niet bestaat, en
andersom. Zolang een route uitzonderlijk maar op één tier bestaat (met reden in de
tier-pariteit-allowlist, §8.4), toont de GUI dat featuregedeelte alleen als de actieve tier het
ondersteunt — nooit een knop die op de andere tier een 404 geeft.

---

## 9. Architectuurbesluiten

Elk besluit: context, keuze, gevolg. Een besluit wordt niet herschreven — een koerswijziging is een
nieuw besluit dat het oude vervangt.

| ID | Besluit | Context en gevolg |
|---|---|---|
| **WZ-ADR-001** | Serverloze hosting, geen containers | Containerhosting past niet binnen het gratis plafond. Gevolg: uitrol als pakket, en geen containerregister om te beheren. |
| **WZ-ADR-002** | Gedeelde kern zonder in- of uitvoerafhankelijkheden | Houdt domeinlogica testbaar zonder database. Gevolg: de grens "puur versus providergebonden" moet bij elke tierovergang expliciet worden getrokken. |
| **WZ-ADR-003** | Gebouwde tiers zijn gelijkwaardig | Vastgesteld 19-09-2026. De eerdere aanname dat één tier "alleen voor terugval" was, is ingetrokken: die is nooit als besluit voorgelegd. Gevolg: elke functie op alle gebouwde tiers, en pariteitsbewaking in beide richtingen. |
| **WZ-ADR-004** | Externe identiteit, lokale autorisatie | Geen eigen accountopslag. Gevolg: de applicatie blijft de autorisatiegrens, ook als het platform al valideert. |
| **WZ-ADR-005** | Prestaties zijn geen optimalisatiedoel | Enkele gebruikers, dagelijkse batch. Gevolg: geen belastingtests, geen prestatiedrempels; wél een grens op de duur van een synchronisatie. |
| **WZ-ADR-006** | Provideronafhankelijke AI-abstractie | Een wissel van AI-dienst is één registratie. Gevolg: geen providerklassen in domeincode. |
| **WZ-ADR-007** | Eén omgeving per installatie | Meerdere omgevingen kosten geld. Gevolg: geen goedkeuringsstappen tussen omgevingen; de integratiebranch neemt die rol over. |
| **WZ-ADR-008** | Geen versieprefix op routes | Eén consument, die meegaat in dezelfde uitrol. Gevolg: een contractwijziging raakt alles tegelijk. Herzien zodra er een tweede consument komt. |
| **WZ-ADR-009** | Foutmodel volgens RFC 9457 | Internationale standaard in plaats van een eigen formaat. Gevolg: één herbruikbaar schema in de specificatie; bestaande ad-hoc foutobjecten migreren. |
| **WZ-ADR-010** | Geen waarden van de installatie in de repository | Volgt uit B1. Gevolg: de eerste authenticatie-uitrol gebeurt lokaal met een privéparameterbestand; automatisering mag die waarden niet opslaan. |
| **WZ-ADR-011** | Eén fork = één productieclub + demo-club, geen shared hosting | Vastgelegd na review van #393 (2026-05-31). Gevolg: geen server-side multi-user-clubautorisatie nodig; `X-Club-Code` is UX, geen beveiligingsgrens; shared hosting vereist een volledige herontwerpslag en is expliciet niet het doel. Zie §2.1. |

### Afwijkingsregister

Een afwijking van een regel uit §10 is alleen geldig als registerregel met reden, eigenaar,
geldigheidsduur en controlebewijs. Zonder die regel geldt de norm.

| ID | Regel | Afwijking | Reden | Geldig tot | Bewijs |
|---|---|---|---|---|---|
| — | — | *(geen geregistreerde afwijkingen op dit moment)* | | | |

---

## 10. Het register — externe basis, lokale regel, bewijs

Dit is het hoofdstuk waar een agent of reviewer begint. Kolom **Bewijs** zegt hoe je controleert;
`ontbreekt` betekent dat de regel geldt maar nog niet toetsbaar is, en staat daarmee ook in §11.

### 10.1 Gegevens en privacy

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-DAT-01 | Clubscheiding | WAF Security | Clubdata draagt een discriminator; bewerkingen filteren erop; geen vaste clubwaarden in code | Reviewcontrole |
| WZ-DAT-02 | Tijd | ISO 8601 | Opslag in UTC, omzetting pas in de interface | CI-controle |
| WZ-DAT-03 | Platformblootstelling | WAF Security | Nieuwe tabellen zetten rij-niveau beveiliging aan in dezelfde migratie | CI-controle tegen een levende database |
| WZ-DAT-04 | Bewaartermijnen | AVG art. 5 lid 1 sub e | Elke tabel met persoonsgegevens heeft termijn, fase en motivering; termijn is configuratie | Documentatie + geplande opschoning |
| WZ-DAT-05 | Geen gegevens in de repository | OWASP ASVS V14 | Persoonsgegevens, exports en lokale geheimen nooit committen | Hooks + beveiligingspoort |
| WZ-DAT-06 | Testgegevens | AVG art. 5 lid 1 sub c | Alleen waarden uit een uitputtende lijst (§8.1.5), elk met de reden waarom ze niet herleidbaar zijn; de lijst is gekoppeld aan de scanners | Scanner-uitzonderingslijst |

### 10.2 Beveiliging en toegang

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-SEC-01 | Autorisatiegrens | OWASP ASVS V4 | Elke beheerroute heeft een servercontrole; de interface is nooit de grens | **ontbreekt** — zie §11 |
| WZ-SEC-02 | Ontwikkelbypass | ASVS V4 | Expliciet, beperkt tot ontwikkeling, faalt dicht bij twijfel | **ontbreekt** |
| WZ-SEC-03 | Beveiligingskoppen | ASVS V14.4 | De webinterface stuurt een restrictief inhoudsbeleid en de bijbehorende koppen | Configuratiebestand |
| WZ-SEC-04 | Geheimen | ASVS V2 / V14 | Nooit in code, sjabloon, log, issue of pull request | Hooks + beveiligingspoort |
| WZ-SEC-05 | Rollenmatrix | ASVS V4 | Code, matrix en documentatie beschrijven dezelfde werking | **ontbreekt** — lopen nu uiteen |
| WZ-SEC-06 | Injectie | ASVS V5 | Uitsluitend geparametriseerde query's | Reviewcontrole |
| WZ-SEC-07 | Uitgaande aanroep op invoer | ASVS V12 | Een door de gebruiker opgegeven adres passeert de beveiligde client met adrescontrole en begrensde doorverwijzingen | Bestaande tests |
| WZ-SEC-08 | Publicatiecontrole | — | Een tekst zonder echte waarden kan nog een vindaanwijzing zijn; bij een nog niet verholpen bevinding alleen klasse en codepad | Reviewcontrole |
| WZ-SEC-09 | Agent-tokengrens | — | Een coding agent leest, bewaart of gebruikt nooit zelf een Sportlink-token; een zichtbaar geworden token geldt als verbrand (§5.5) | Procesregel in CLAUDE.md + reviewcontrole |

### 10.3 API

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-API-01 | Contract | OpenAPI 3.0.3 | De specificatie is leidend; afgeleiden worden gegenereerd | Reviewcontrole |
| WZ-API-02 | Foutmodel | RFC 9457 / RFC 9110 | Problem Details met correct contenttype; geen interne details | **ontbreekt** — ad-hoc formaat in gebruik |
| WZ-API-03 | Statuscodes | RFC 9110 / RFC 6585 | Internationale betekenis; `401` met uitdaging, `429` met `Retry-After` | Endpointtests |
| WZ-API-04 | Routepariteit | — | Specificatie en code beschrijven dezelfde routes | **ontbreekt** — drift aangetoond |
| WZ-API-05 | Paginering | — | Verplicht bij een lijst zonder functionele bovengrens; gedocumenteerd met grens, sortering en totaal | **ontbreekt** |
| WZ-API-06 | Idempotentie | RFC 9110 §9.2.2 | Verplicht bij een mutatie met externe bijwerking die opnieuw verstuurd kan worden | **ontbreekt** |
| WZ-API-07 | Geen versieprefix | WZ-ADR-008 | Contractwijziging raakt alles in dezelfde uitrol | Reviewcontrole |

### 10.4 Architectuur en tiers

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-ARC-01 | Laaggrenzen | ISO 42010 | De gedeelde kern is vrij van database- en webafhankelijkheden; de interface heeft geen persistentie; tiers roepen elkaar niet aan | **ontbreekt** — geldt feitelijk al, is niet vastgeklikt |
| WZ-ARC-02 | Tierovergang | — | Bij een overgang wordt per deel bepaald of het providergebonden is; puur deel gaat naar de kern | Duplicatieplafond |
| WZ-ARC-03 | Tierpariteit | — | Elke functie op alle gebouwde tiers; bewaking in **beide** richtingen | **ontbreekt** voor code; aanwezig voor schema en routes |
| WZ-ARC-04 | Interfacelogica | — | Geen logica in paginabestanden; achterliggende klasse | CI-controle |
| WZ-ARC-05 | Bestands- en methodeomvang | — | Plafonds mogen niet stijgen | CI-controle |

### 10.5 Integraties en AI

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-INT-01 | Uitgaande poort | WAF Security | Elk uitgaand pad — HTTP, timer én wachtrij — passeert de poort | **ontbreekt** — één pad omzeilt hem |
| WZ-INT-02 | Geen gefabriceerde contracten | — | Een extern schrijfcontract (Sportlink Web Extension) wordt nooit gefabriceerd; elke implementatie is gegrond in een echte netwerktrace | Reviewcontrole |
| WZ-AI-01 | Providerabstractie | WZ-ADR-006 | Geen providerklassen in domeincode | Reviewcontrole |
| WZ-AI-02 | Dynamische datum | — | Tijdgevoelige instructies krijgen de datum mee; voorbeelden zonder vast jaar | Reviewcontrole |
| WZ-AI-03 | Uitsluiting vóór verzending | AVG art. 21 | Uitsluitingsregels opnieuw toepassen vlak vóór de externe aanroep; faalt dicht | Bestaande code |
| WZ-AI-04 | Publicatiegrens | — | De bevestiging draagt de getoonde tekst terug; het filter is nooit een garantie | Bestaande code |
| WZ-AI-05 | Doorgifte vastgelegd | AVG art. 30 | Velden, grondslag en bewaartermijn bij de verwerker staan beschreven | **ontbreekt** |

### 10.6 Infrastructuur, kosten en uitrol

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-COST-01 | Gratis tiers | WAF Cost Optimization | Binnen gratis tiers; wijziging vooraf toetsen tegen actuele leveranciersdocumentatie | Schakelaars in de infrastructuurdefinitie |
| WZ-COST-02 | Kostenpoort als code | WAF Cost Optimization | Kostbare onderdelen staan standaard uit, aanzetten is een expliciete keuze | Infrastructuurdefinitie |
| WZ-DEP-01 | Installatie zonder waarden | WAF Security / WZ-ADR-010 | Eerste authenticatie-uitrol lokaal met privéparameterbestand; niets in de repository | Voorbeeldmodus + controle na uitrol |
| WZ-DEP-02 | Infrastructuur is waar wat ze belooft | WAF Operational Excellence | Drukt de definitie een instelling uit, dan moet de uitrol die ook werkelijk zetten — anders eruit en de handmatige stap documenteren | **ontbreekt** — een voorwaarde wordt nooit waar |
| WZ-DEP-03 | Labels | WAF Cost Optimization | Elke resource draagt labels voor omgeving, applicatie, beheerwijze, kosten en dataclassificatie | **ontbreekt** |
| WZ-DEP-04 | Aanmelding van automatisering | WAF Security | Nieuwe of ingrijpend gewijzigde werkstromen gebruiken federatieve aanmelding zonder geheim | **ontbreekt** — bestaande werkstromen migreren |

### 10.7 Kwaliteit en bewijs

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-QUA-01 | Beveiligingspoort | WAF Security | Blokkerend; niet te compenseren | Werkstroom |
| WZ-QUA-02 | Negatieve controle | WAF Operational Excellence | Elke belangrijke controle bewijst dat hij kan falen | Per controle |
| WZ-QUA-03 | Geen dekkingsdrempel | WAF Operational Excellence | Dekking is een signaal, geen samenvoegcriterium | Bewust geen |
| WZ-QUA-04 | Meldketen | WAF Operational Excellence | Een geautomatiseerde controle draagt een manier om de melding te laten afgaan zonder echt probleem | Testingang |
| WZ-QUA-05 | Documentatie loopt mee | WAF Operational Excellence | Documentatie is bij vóór samenvoegen; verouderde informatie misleidt; groene CI + HTTP 200 bewijzen geen werkende Blazor-UI | Reviewcontrole + verplichte browser-rendercheck na elke productie-deploy |

---

## 11. Risico's en technische schuld

Alle `ontbreekt`-regels uit §10, op volgorde van effect gedeeld door kosten. Dit is tevens de
werkvoorraad.

| # | Punt | Register-ID | Waarom dit eerst | Omvang |
|---|---|---|---|---|
| 1 | Eén uitgaand pad omzeilt de poort | WZ-INT-01 | Lokaal kan een volledige ophaalronde bij de externe bron starten | Enkele regels, twee tiers |
| 2 | Geen pariteitscontrole op de codebomen | WZ-ARC-03 | Vier gedocumenteerde storingen komen hieruit voort | ± 1 dag |
| 3 | Laaggrenzen niet vastgeklikt | WZ-ARC-01 | De regels zijn vandaag al waar; borging kost geen codewijziging | ± 1 dag |
| 4 | Autorisatiedekking niet afgedwongen | WZ-SEC-01 | Negenennegentig handmatige aanroepen; niets bewaakt de honderdste | valt samen met 3 |
| 5 | Doorgifte aan AI-verwerker niet vastgelegd | WZ-AI-05 | Verantwoordingsplicht; geen code nodig | documentatie |
| 6 | Rollenmatrix loopt uiteen met de code | WZ-SEC-05 | Een verplichte test waarvan een stap niet kan slagen, leert afwijkingen negeren | ± halve dag |
| 7 | Aanmelding van automatisering zonder geheim | WZ-DEP-04 | Kost niets en de rechten staan al klaar | ± halve dag |
| 8 | Infrastructuur belooft wat ze niet uitrolt | WZ-DEP-02 | Voorbeeldmodus zou hier blijvend verschil moeten tonen | ± 2 uur |
| 9 | Labels op resources | WZ-DEP-03 | Goedkoop; nodig voor kostentoerekening | ± 2 uur |
| 10 | Documentatiedrift | WZ-QUA-05 | Meerdere documenten beschreven historisch hetzelfde mechanisme verschillend | ± halve dag |
| 11 | Foutmodel, routepariteit, paginering, idempotentie | WZ-API-02/04/05/06 | Contractkwaliteit; begin bij foutmodel en routepariteit | meerdere dagen |
| 12 | Ontwikkelbypass zonder tweede signaal | WZ-SEC-02 | Laag risico in de praktijk, maar hangt aan één omgevingsvariabele | ± 2 uur |

**Bewust niet op deze lijst**, met reden: een abstractielaag over alle gegevenstoegangsklassen
(weken werk, testbaarheid bestaat al via integratietests, geen storing eraan toe te schrijven), en
een herstructurering naar een vierlagenmodel (verdubbelt het oppervlak waarop tierpariteit bewaakt
moet worden — precies het probleem onder punt 2).

**Twee punten buiten het register, met een eigen issue in plaats van een WZ-ID:**
- **De handmatige sync-trigger heet per tier anders (#1266).** Op de SQL Server-tier is de route
  `sync-matches`, op de Postgres-tier `postgres/sync-matches`. Functioneel gelijkwaardig, maar een
  beheerder moet na een tierwissel een ander adres gebruiken. Staat als enige openstaande post in de
  tier-pariteit-allowlist; uitlijnen heeft een breaking-change-kant en is daarom een aparte wijziging.
- **Beide Function Apps staan op `net9.0` met een harde einddatum (epic #1063, zie §5.3).** .NET 9
  gaat op 10 november 2026 uit support; Linux Consumption wordt op 30 september 2028 uitgefaseerd.
  In-place migratie naar Flex Consumption bestaat niet.

---

## 12. Begrippenlijst

| Begrip | Betekenis in dit document |
|---|---|
| **Tier** | Een volledige implementatie voor één databasesoort. Meerdere tiers bestaan naast elkaar; één is actief per installatie. |
| **Gedeelde kern** | Het project met logica die van geen enkele databasesoort of webframework afhankelijk is. |
| **Pariteit** | De eigenschap dat alle gebouwde tiers dezelfde functies, tabellen en kolommen hebben. |
| **Poort voor uitgaand verkeer** | De ene plek die bepaalt of externe aanroepen in deze omgeving zijn toegestaan (`EgressGuard`). |
| **Clubdiscriminator** | De kolom die gegevens aan een vereniging koppelt. Een gegevensfilter, geen beveiligingsgrens (B3). |
| **Bewijs** | De manier waarop een regel controleerbaar is: een geautomatiseerde controle, een test, een configuratiebestand, of een expliciete reviewstap. |
| **Negatieve controle** | Een test die aantoont dat een controle daadwerkelijk rood kan worden. |
| **MSAL** | Microsoft Authentication Library — de clientbibliotheek die de Blazor-frontend gebruikt om bij Entra ID aan te melden en tokens te verkrijgen. |
| **Easy Auth** | Azure's platformvoorziening die het inkomende Bearer-token op de Function App valideert vóórdat de functiecode draait. |

---

## 13. Onderhoud van dit document

Dit document is de centrale, leidende plek voor de geldende architectuurafspraken.

Een nieuw besluit is nodig wanneer een wijziging een vastgelegde grens, een publiek contract, de
gegevensbescherming, de kostenlimiet of de tierstrategie raakt.

### 13.1 Waar hoort een nieuwe architectuurregel? (routeringsregel, vastgelegd na #1291)

Vóór #1291 stonden architectuurregels op drie plekken tegelijk: hier, in een los
uitvoeringsdocument, en woordelijk herhaald in `CLAUDE.md`. Om dat niet te laten terugkomen, geldt
vanaf nu één beslisregel:

1. **Een kwaliteitsdoel, randvoorwaarde, architectuurbesluit, of een regel die voor het hele systeem
   geldt** (zoals de meeste inhoud van §2, §8 en §9) hoort **hier**, in het hoofdstuk waar hij
   inhoudelijk bij past, met een rij in §10. Dit geldt ook voor de concrete uitwerking (checklist,
   codevoorbeeld, tabel) — die staat direct onder de regel, niet in een apart document.
2. **Diepgaand, onderwerp-specifiek uitvoeringsdetail** dat een eigen, groeiend document rechtvaardigt
   (bijv. de volledige multi-tier-strategie, de codekwaliteitsguards, teamresolutie, AI-services, de
   e-mailmodule, de Sportlink Web Extension) hoort in het bijbehorende `ARCHITECTUUR-<ONDERWERP>.md`
   of onderwerpdocument uit `docs/INDEX.md` — **niet hier en niet in `CLAUDE.md`.** Dit document
   verwijst er samenvattend naar (zoals §5.4, §5.5 en §8.4 al doen).
3. **Een instructie voor hóe Claude Code zelf moet werken** (build-commando's, git-workflow,
   statuslabels, de verificatielus) hoort in `CLAUDE.md`. Leunt die instructie op een
   architectuurprincipe (bijv. het kostenplafond in §8.6, of de agent-tokengrens in §5.5), dan geeft
   `CLAUDE.md` een korte samenvatting plus een verwijzing hierheen — nooit de volledige regel nogmaals
   uitgeschreven.

Een regel op twee plekken volledig uitschrijven "voor de zekerheid" is geen redundantie zonder
nadeel: het is precies de plek waar de volgende wijziging er één vergeet bij te werken. `AGENTS.md`
volgt dit automatisch, want dat bestand wordt uit `CLAUDE.md` gegenereerd en nooit met de hand
bewerkt.

### 13.2 Twee onderhoudsregels uit de toetsing van september 2026

1. **Een nieuwe regel krijgt een controle, of komt met bewijs `ontbreekt` in §10 én in §11.** Er is
   geen derde mogelijkheid. Zo ontstaan geen regels die alleen in een document leven.
2. **Een onbeantwoorde vraag is geen afwijking en wordt geen norm.** Zij hoort in een apart
   vragenbestand tot zij beslist is.

Beoordeel dit document opnieuw na een relevante architectuurwijziging, een incident, een nieuwe
gegevensstroom, of een wijziging in de gratis tiers van de leverancier.

---

## Bijlage — gebruikte externe kaders

| Kader | Rol | Toepassing hier |
|---|---|---|
| ISO/IEC/IEEE 42010:2022 | Formele basis voor een architectuurbeschrijving | Belanghebbenden, zorgen, gezichtspunten, besluiten (§1, §9) |
| arc42 | Praktisch sjabloon | De hoofdstukindeling van dit document |
| Azure Well-Architected Framework | Toetsbaseline voor de cloudkeuzes | Vijf pijlers: Reliability, Security, Cost Optimization, Operational Excellence, Performance Efficiency. Het kader kent een maturity-model in vijf niveaus en beveelt een gefaseerde toepassing aan; deze applicatie bevindt zich bewust op niveau 1–2. |
| OWASP ASVS | Securityverificatie | Alleen de controles die op deze webapplicatie van toepassing zijn (§10.2) |
| RFC 9110, RFC 9457, RFC 6585 | HTTP-semantiek en foutmodel | §8.3 en §10.3 |
| OpenAPI 3.0.3 | Contractformaat | §8.3 en §10.3 |
| AVG (Verordening (EU) 2016/679) | Gegevensbescherming | §8.1, §8.5 en §10.1 |

*Zie ook:* [ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md) ·
[SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md) · [EMAIL-VERWERKING.md](EMAIL-VERWERKING.md) ·
[VERSIONING.md](VERSIONING.md) · [ENTRA-AUTH-BEHEER.md](ENTRA-AUTH-BEHEER.md) ·
[SETUP-NIEUWE-CLUB.md](../SETUP-NIEUWE-CLUB.md) · [DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) ·
[CONTRIBUTING.md](../CONTRIBUTING.md) · [SECURITY.md](../SECURITY.md)
