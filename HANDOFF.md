# HANDOFF — release v3.8.0.0 (#1424)

Status (2026-10-01): #1421 gemerged (3cfc9c1), lokale branches/worktrees opgeruimd, versie 3.8.0.0 en
CHANGELOG-sectie voorbereid in PR naar `develop`, daarna release-PR `develop` → `main` en tag `v3.8.0.0`.
Restpunten (productie-sleutel `SportlinkAutoLoginEncryptionKey`, Sportlink-koppeling opnieuw inrichten
na migratie 030, dependabot-PR's #1404/#1405/#1414, opruimpunten) staan in issue #1423.

---

# HANDOFF — #1418 documentatie-uitlijning vóór release v3.8.0.0 (PR #1420)

Status (2026-10-01): alle documentatie voor beheerder/gebruiker, technisch/architectuur en developer is
tegen de code op `develop` (HEAD 8363105) gecontroleerd en waar nodig bijgewerkt — zie de PR-body van
[PR #1420](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/pull/1420) voor de lijst per doelgroep.
Uitsluitend documentatie; geen versiebump.

Open vóór de release:
- [#1421](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/1421) — drie GUI-teksten melden
  nog "altijd gesimuleerd" (oefenwedstrijd-banner, `SportlinkActieStatus.CodeLockTekst`, dry-run-hulptekst)
  terwijl de acties sinds #1319 echt doorgaan. Codewijziging, bewust buiten #1418 gehouden; aanbevolen
  vóór v3.8.0.0.
- Beide `local.settings.template.json`-bestanden bevatten `SportlinkAutoLoginEncryptionKey` twee keer
  in `Values` (regel 8 en 23) — functioneel onschadelijk, wel een merge-artefact van #1411.
- Release zelf: `[Unreleased]` bevat `feat:`-items → MINOR-bump naar 3.8.0.0 in alle drie csproj's,
  `info.version` in `openapi.yaml`/`.json` mee, `docs/QUICK-REFERENCE.md` noemt al v3.8.

---

# HANDOFF — #1411 automatische Sportlink-login (na merge PR #1412)

Status: implementatie en runbook zijn via [PR #1412](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/pull/1412) gemerged naar `develop` op 2026-10-01. Merge-commit: `27009f9574a191e8536adb6c88c92e2749f4aee8`; feature-head: `1a0ffb7e2fbf9a22f45f903e61bec9c0b0853457`. De productiebranch is niet aangepast en er is geen productie-deployment uitgevoerd.

Implementatie: begrensde TOTP/PKCE-loginprovider, AES-GCM-encrypted opslag van credentials en refresh-tokens op beide databasetiers, databaselease en begrensde herlogin, admin-API en beheerinterface. MFA blijft actief. Productie-initialisatie, secretbeheer, herstel en veiligheidsgrenzen staan in `docs/SPORTLINK-AUTOLOGIN.md`.

Verificatie op PR #1412: alle Build-, Postgres- en SQL Server-fresh-databasejobs en alle Security Scan-checks zijn geslaagd. Op de gemergde develop-head `27009f9` zijn beide FunctionApp-tiers en BlazorAdmin gebouwd; 107 gerichte auto-login/TOTP-tests en 823 `FunctionApp.Tests` zijn geslaagd, met 5 bestaande skips. `git diff --check` was schoon. Lokale .NET 9-tests op deze host gebruikten `DOTNET_ROLL_FORWARD=Major` vanwege de aanwezige .NET 10-runtime.

Eigenaarstest en acceptatie: de eigenaar heeft de oorspronkelijke duur van 24 uur bijgesteld naar 14 uur en 39 minuten en de lokale auth-proef na 14u39 zonder onderbreking als geslaagd en afgerond verklaard. De acceptatietest is daarmee compleet volgens de door de eigenaar vastgestelde grens; er staat hiervoor geen aanvullende 24-uursproef open.

Vervolg na deze afgeronde feature-acceptatie:
- Productie-initialisatie volgt de stappen in `docs/SPORTLINK-AUTOLOGIN.md`, inclusief de bestaande Function App-secretsetting.
- Een productie-release vereist een aparte releasegang van `develop` naar `main`; die is geen onderdeel van PR #1412 en is niet uitgevoerd.

Uitgevoerde taak: Astra orkestreerde de implementatie, CI-opvolging, merge naar `develop` en develop-hercontrole. Bij afronding van de merge-hercontrole is deze sessie geen lopende auth-test.

---

# HANDOFF — #1350 API-autorisatie geconsolideerd

Branch `feature/#1350-api-auth-consolidatie` → PR naar `develop`. Versie `3.5.6.1` (REVISION-bump:
refactor + tests, geen nieuwe feature).

## Wat is gedaan

### 1. Eén autorisatiepoort voor alle HTTP-endpoints, op beide tiers

- `AdminEndpoint` (`FunctionApp.Postgres/Admin/`, `FunctionApp/Admin/`) is nu de enige plek waar
  de admin-poort staat: private `Poort(req)` (correlatie-id + `EasyAuthHelper.RequireAdmin`),
  `ExecuteAsync` (poort → databasewacht → clubcode → werk → uniforme 500) en de nieuwe
  `ExecuteZonderDatabaseAsync` (zelfde poort, geen databasewacht — voor de drie feedback-endpoints,
  die juist moeten blijven werken als de database plat ligt).
- Gemigreerd naar de wrapper, per tier identiek: `PlannerFunction` (11; op SQL Server verving dit
  ook de eigen `HandleAsync`-wrapper en `RequireClubCode`), `AdminThemeFunction` (Get/Put),
  `AdminTeambegeleidingFunction` (4), `AdminSyncFunction` (2), `AdminClubsFunction` (1),
  `AdminTemplatesFunction` (3), `EmailTestFunction` (1), `AdminSettingsFunction` (Get/Put),
  `FeedbackFunction` (3, DB-loos), `AdminTestDataFunction` (6, alleen SQL Server — Postgres liep al
  via de wrapper) en de twee sync-routes (zie 3).
- Bewust **niet** gemigreerd, met reden in `scripts/ci/endpoint-autorisatie-allowlist.txt`:
  `AdminThemeExtract` (luie URL-check vóór de databaseaanroep) en `AdminGeocodeGet` (geen
  database). Zelfde `RequireAdmin`-poort, direct aangeroepen. `Health` is het enige anonieme
  endpoint en staat als zodanig op die lijst.
- Gedragsverschillen die een beheerder kan merken: een interne fout geeft overal uniform
  `{ "error": "Interne fout" }` (was per endpoint "Ophalen mislukt", "Verzoek mislukt", …); de GUI
  toont de servermelding of haar eigen fallback, dus niets breekt. Validatie-400's, 404, 409, 429,
  503 en de speciale paden (AutoPlanToepassen → 400/403, EmailTest → lokale foutdetails, sync →
  automatische foutrapportage) zijn ongewijzigd.
- **CI-guard** `scripts/ci/check-endpoint-autorisatie.sh` (+ allowlist, in `build.yml`, in het
  register van `docs/ARCHITECTUUR-CODEKWALITEIT.md` als Regel 9, negatieve tests in
  `check-codekwaliteit.test.sh`): per `[Function]`-blok met `HttpTrigger` — geen directe
  `EasyAuthHelper.Require*`, wél `AdminEndpoint.Execute*Async` of
  `SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync`, en `AuthorizationLevel.Anonymous`. Dode
  allowlist-regels en regels zonder reden laten hem falen. Draagbaar op bash 3.2.

### 2. Dode code verwijderd

`EasyAuthHelper.RequireAuthenticated` (rol `admin` óf `user`) is uit beide tiers weg; het werd
sinds #310 nergens aangeroepen. Docs (`API.md`, `openapi.yaml/json`) zeggen dat nu ook.

### 3. `sync-matches` — beslissing en onderbouwing (aanname, na te rekenen)

**Beide routes zijn geconverteerd** van `AuthorizationLevel.Admin` (Azure master key via `?code=`)
naar `AuthorizationLevel.Anonymous` + `AdminEndpoint.ExecuteAsync` (Easy Auth, rol `admin`).

Onderzocht, met uitkomst:

| Waar | Gevonden | Gevolg |
|---|---|---|
| `.github/workflows/deploy.yml:450-476` | Smoke-test die `sync-matches?code=<function key>` aanroept en **401** verwacht | Negatieve test, geen caller; blijft slagen (een key levert geen Easy Auth-principal). Commentaar bijgewerkt |
| `scripts/dev/Restore-ProductionDump.ps1` | Lokale aanroep zonder key | Lokaal geldt de `WEBSITE_SITE_NAME`-bypass — ongewijzigd |
| Timer `FetchAndStoreApiData` / `PostgresFetchAndStoreApiData` | Nachtelijke sync in-proces | Raakt het HTTP-endpoint niet |
| Admin GUI | Knop "Sync starten" → `POST /api/beheer/sync/trigger` (al Entra) | Ongewijzigd |
| bruno, docs | Documentatie van de master key | Bijgewerkt |

Geen geautomatiseerde aanroeper hing dus van de master key af. Wat de eigenaar wél merkt: de
**volledige-seizoen-herhaling `?reset=true&season=YYYY` buiten de Admin GUI** vereist voortaan een
Entra-token met de admin-rol in plaats van `?code=`. De GUI-knop kent geen reset-modus; wil de
eigenaar die zonder token kunnen starten, dan is "reset-modus in de GUI-trigger" een klein
vervolgissue. De clubcode uit de wrapper wordt door de sync bewust genegeerd (sync = primaire club).

Dit is de enige plek waar ik een productkeuze heb gemaakt in plaats van alleen te consolideren;
het label op #1350 staat daarom bewust op `review-needed` als de eigenaar dit wil narekenen —
zie het PR-rapport.

## Tests — waarom dit de regressie-eis dekt

`FunctionApp.Postgres.Tests/EndpointAutorisatieTests.cs` en de spiegel
`FunctionApp.Tests/Admin/EndpointAutorisatieTests.cs` (elk 306 tests, allemaal groen):

- Vindt via reflectie **élk** `[Function]` met een `HttpTrigger` in de tier-assembly (94 per tier,
  ondergrens 90 zodat een kapotte ontdekking niet stil groen wordt). Nieuwe endpoints vallen er
  automatisch onder.
- Per beveiligd endpoint: zonder principal → `UnauthorizedResult` (401) én de poort niet
  gepasseerd; alleen rol `user` → 403; admin (+ `Wedstrijdzaken` voor `/sportlink/*`) → de poort
  gepasseerd **via de wrapper**. Dat laatste bewijst de `internal` testhaak
  `AdminEndpoint.PoortGepasseerdVoorTests`, die ná de rolcontrole en vóór de databasewacht een
  sentinel-`IActionResult` teruggeeft dat geen productiepad kan maken. Zo raakt de test nooit een
  database en doet geen enkel endpoint echt werk — essentieel, want in de CI-job
  `fresh-db-postgres` staat `POSTGRES_CONNECTION_STRING` als job-env en zou een admin-aanroep van
  een DELETE- of sync-endpoint daar écht uitgevoerd worden. De haak zit ná de poort en kan die
  niet verzwakken; productie heeft geen pad dat hem zet.
- Voor de twee allowlist-endpoints: admin → hun eigen 400 (bewijst "geen 401/403" plus dat de
  DB niet nodig is).
- Sportlink-endpoints: alleen-admin → 403 én alleen-Wedstrijdzaken → 403 (beide poorten, #1272).
- Structureel: geen enkel endpoint op een ander niveau dan `Anonymous`; anonieme allowlist =
  uitsluitend `health`; wrapper-tests voor beide `Execute*`-varianten (401 zonder werk, admin →
  werk, uitzondering → 500 zonder details).

Bestaande suites: Postgres 469 groen / 141 skipped (integratie zonder DB-variabele), SQL Server
797 groen / 5 skipped.

## Verificatie uitgevoerd

- `dotnet build` beide tiers groen; `dotnet test` beide testprojecten groen.
- Guards op de eindtoestand: endpoint-autorisatie (188 endpoints, 4 direct, 2 anoniem),
  tier-duplicatie 5405 → 5324, interne duplicatie 847 → 663, bestandsgrootte 23 → 20 en
  methodes 28 → 25 (alle vier plafonds verlaagd, met toelichting in `codekwaliteit-plafonds.txt`),
  analyzer 20/20, regelregister, tier-pariteit, valkuilen, path-casing,
  `genereer-agents-md.py` (AGENTS.md geregenereerd), `check-codekwaliteit.test.sh` incl. de twee
  nieuwe negatieve tests.
- Smoke op een eigen Functions-host (poort 7194; de acceptatie-instantie op 7094/5242 is niet
  aangeraakt): `/api/health` 200 v3.5.6.1, gemigreerde GET's 200/404, POST's bereiken hun
  400-validatie. Migratie 027 (al op develop) op de lokale database toegepast via
  `Database.Postgres.Cli`; health daarna `ok` zonder `pendingMigrations`.
- `Test-App.ps1 -Fix` kon in deze sessie niet draaien: de sandbox weigert elke `pwsh`-aanroep.
  Het equivalent (build + migraties + health) is handmatig gedaan; BlazorAdmin is bewust niet
  gebouwd (backend-only, dev-server draait).

## Open punten voor de eigenaar

1. De sync-matches-beslissing hierboven (reset-modus vereist nu een token buiten de GUI).
2. Bijvangst van de docs-sweep: tientallen `bruno/**/*.yml` dragen al langer een onjuiste footer
   `Spec declares security scheme "functionKey"` terwijl hun spec `easyAuth` is — generator-drift
   van vóór #1350, niet aangeraakt; een apart documentatie-issue waard.
3. `docs/ARCHITECTUUR-EMAIL-MODULE.md` §1 blijft een historische analyse; er staat nu een
   "Opgelost in #1350"-noot boven §1.7 in plaats van een herschrijving.

## Modelgebruik

- Fable (orchestrator): analyse, ontwerp (wrapper, testhaak, guard), Postgres-tier, beide
  testbestanden, guard + zelftest, register/CLAUDE.md, changelog, review van alle subagent-diffs,
  alle verificatie.
- Sonnet-subagent 1: mechanische spiegel van de Postgres-migratie naar `FunctionApp/` (SQL Server),
  met build/test-verificatie; diff volledig door Fable gereviewd (wrapper, helper, feedback,
  sync-matches, PlannerFunction regel voor regel; overige bestanden via gerichte greps).
- Sonnet-subagent 2: documentatie-sweep (API.md, openapi.yaml/json, bruno, ARCHITECTUUR-*,
  ENTRA-AUTH, FunctionApp/CLAUDE.md, deploy.yml-commentaar, Verify-AzureAuthSetup.ps1-patroon);
  door Fable gereviewd, twee formuleringen gecorrigeerd.
