# Architectuurprincipes — Sportlink Wedstrijdzaken (V3)

Dit document beschrijft alle architectuurafspraken en -conventies die gelden voor dit project. Ze zijn opgebouwd uit concrete beslissingen en incidents uit de ontwikkelhistorie. Afwijkingen worden geblokkeerd door de Security Gate in CI of teruggegeven bij codereview.

> **V3, sinds #976 (2026-09-04).** Dit document beschrijft de huidige, multi-tier architectuur:
> Postgres is de productietier, Azure SQL is een volwaardige rollback-tier, en de Sportlink Web
> Extension (epic #986) voegt een schrijfrichting webapp→Sportlink Club toe naast de bestaande
> alleen-lezen ETL-sync. De vorige, single-tier (Azure SQL-only) architectuur staat gearchiveerd in
> [ARCHITECTURE-V2.md](ARCHITECTURE-V2.md) — wijzig dat bestand niet meer.

---

## Inhoudsopgave

1. [Systeemoverzicht](#1-systeemoverzicht)
2. [Multi-tier databasestrategie](#2-multi-tier-databasestrategie)
3. [Tijdzones — UTC in database, lokale tijd in GUI](#3-tijdzones--utc-in-database-lokale-tijd-in-gui)
4. [Multi-club isolatie — ClubCode discriminator](#4-multi-club-isolatie--clubcode-discriminator)
5. [Geen club-specifieke waarden in code](#5-geen-club-specifieke-waarden-in-code)
6. [Secrets en configuratie](#6-secrets-en-configuratie)
7. [AVG / GDPR — absolute regels](#7-avg--gdpr--absolute-regels)
8. [Authenticatie en autorisatie — vijf lagen](#8-authenticatie-en-autorisatie--vijf-lagen)
9. [Blazor auth-gate](#9-blazor-auth-gate)
10. [MSAL-configuratie checklist](#10-msal-configuratie-checklist)
11. [Database — schema's en conventies (per tier)](#11-database--schemas-en-conventies-per-tier)
12. [Berichtverwerking — kanaal-agnostische pipeline](#12-berichtverwerking--kanaal-agnostische-pipeline)
13. [Sportlink Web Extension — schrijfrichting naar Sportlink Club](#13-sportlink-web-extension--schrijfrichting-naar-sportlink-club)
14. [Lagen altijd synchroon](#14-lagen-altijd-synchroon)
15. [Versiebeheer en releases](#15-versiebeheer-en-releases)
16. [CI/CD en Security Gate](#16-cicd-en-security-gate)
17. [Bekende beperkingen](#17-bekende-beperkingen)

---

## 1. Systeemoverzicht

**Eén fork kiest exact één databasetier**, bepaald op build/deploytijd door de repository-variabele
`DatabaseTier` (zie §2). De frontend, auth-laag en algemene architectuur zijn voor beide tiers
identiek; alleen de backend-boom en het databaseschema verschillen.

```
Browser (beheerder)
  └── Azure Static Web Apps (Free tier) — Blazor WebAssembly
        Serveert alleen statische bestanden; geen SWA-proxying naar de API
        MSAL: Bearer token wordt automatisch meegestuurd naar de Function App
        │
        │ HTTPS + Bearer token (Entra ID)
        ▼
  Azure Functions (Linux Consumption plan) — net9.0, isolated worker
        Migratie naar Flex Consumption + .NET 10 loopt via epic #1063
        Easy Auth: valideert Bearer token, injecteert X-MS-CLIENT-PRINCIPAL
        EasyAuthHelper: checkt 'admin' rol op alle /api/beheer/*, /api/test/*, /api/feedback/*
        │
        ├── DatabaseTier=SqlServer          ├── DatabaseTier=Postgres
        │   FunctionApp/                    │   FunctionApp.Postgres/
        │   (rollback-tier, ongewijzigd)    │   (PRODUCTIETIER sinds #976)
        │                                   │
        ▼                                   ▼
  Azure SQL (Free tier, 32 GB)         Postgres (Docker lokaal / Supabase cloud)
    dbo.AppSettings + AppSettingsAudit   public.appsettings
    dbo.Velden, VeldBeschikbaarheid      public.velden, veldbeschikbaarheid
    planner.EmailVerwerking             planner.emailverwerking
    his.* / stg.* / pub.* (ETL)         his.* / stg.* (ETL, geen pub.*-views — zie §17)
        │                                   │
        └───────────────┬───────────────────┘
                         ▼
              Sportlink REST API (data.sportlink.com)
                alleen-lezen sync (beide tiers) +
                schrijfrichting webapp→Sportlink Club
                (Postgres-tier only, epic #986, zie §13)
```

**Technologiestack:** `FunctionApp` `net9.0` (SQL Server-tier) · `FunctionApp.Postgres` `net9.0`
(Postgres-tier) · `BlazorAdmin` `net10.0` · `Planner.Shared` (tier-agnostische bibliotheek, §2) ·
Azure Functions v4 · Blazor WebAssembly · Azure SQL / Postgres · Microsoft Graph API · OpenAI
(direct, model via `AiModelName`) · Azure Static Web Apps · Entra ID (single-tenant)

> **Runtimeversies zijn niet uitwisselbaar — niet upgraden zonder infrastructuurwijziging (#579).**
>
> | Project | Target | Reden |
> |---|---|---|
> | `FunctionApp/fa-dev-sportlink-01.csproj` | **`net9.0`** | Linux Consumption Plan ondersteunt `net10.0` niet → 503 "Function host is not running" |
> | `FunctionApp.Postgres/FunctionApp.Postgres.csproj` | **`net9.0`** | Zelfde beperking als hierboven |
> | `BlazorAdmin/BlazorAdmin.csproj` | `net10.0` | Browser-runtime, geen Azure-beperking |
>
> `.NET 10` voor Azure Functions vereist het **Flex Consumption Plan**. Dat plan heeft wél een
> gratis tegoed — 250.000 executies + 100.000 GB-s per maand per subscription, tegenover 1M + 400K
> op Consumption — maar het is een ander plan, en een planwijziging vraagt altijd expliciete
> goedkeuring van de eigenaar. Zie CLAUDE.md → Kostenbeleid en **epic #1063** voor de migratie.
>
> **Dit is een toestand met een einddatum:** .NET 9 gaat op 10 november 2026 uit support en is de
> laatste .NET-versie die Linux Consumption krijgt; dat plan wordt zelf op 30 september 2028
> uitgefaseerd. In-place migratie naar Flex bestaat niet — er moet een nieuwe app komen.
>
> Bij elke documentatiewijziging: controleer of vermelde runtimeversies nog met de csproj's overeenkomen.

**ETL-data flow (identiek patroon op beide tiers):**
```
Sportlink REST API → Azure Function → stg.* (staging, per run leeggemaakt)
                                    → merge-orchestrator → his.* (persistent)
                                                          → pub.* (SQL Server: read-only views;
                                                                    Postgres: geen consumenten, §17)
```

**Auth-stroom (identiek op beide tiers):**

| Laag | Mechanisme |
|---|---|
| Frontend | MSAL (`AddMsalAuthentication`) + `AuthorizationMessageHandler` |
| Transport | Bearer token in `Authorization` header |
| Function App | Azure Easy Auth (AllowAnonymous mode) + `EasyAuthHelper.RequireAdmin()` |
| Lokaal (dev) | Bypass: `WEBSITE_SITE_NAME` afwezig → altijd toestaan |

---

## 2. Multi-tier databasestrategie

> **Volledig besluit + index van alle sub-issues: [ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md).**
> Dit is uitsluitend een samenvatting voor de systeemarchitectuur — de tier-strategie zelf niet
> hier dupliceren; wijzig de strategie in het aangewezen document (epic #815).

**Vaste bouwvolgorde:** SQL Server (bestaand) → **Postgres (eerste prioriteit, productietier sinds
#976)** → SQLite (voorbereidend, #826) → Cosmos DB (uitsluitend het e-mailverwerkingslog, #828).
Niet gelijktijdig, niet in een andere volgorde.

**Eén tier per club-deployment, nooit een gedeelde C#-providerabstractie.** Elke tier is een
volledig gescheiden, parallelle implementatieboom (`FunctionApp/` + `Database/` voor SQL Server,
`FunctionApp.Postgres/` + `Database.Postgres/` voor Postgres), gekozen op build/deploytijd — nooit
een runtime-switch in gedeelde code. **Uitzondering:** pure, provider-agnostische business-logica
(geen SQL, geen ADO.NET/Npgsql-afhankelijkheid) leeft in `Planner.Shared/` en wordt door beide
tiers gebruikt — bijvoorbeeld `TeamNaamNormalisatie` en de Sportlink-veldstring-matching.

**Tier-keuze is onveranderlijk na de eerste deploy.** De repository-variabele `DatabaseTier`
(waarden: `SqlServer` of `Postgres`) bepaalt welke boom `scripts/ci/resolve-database-tier.sh`
bouwt en deployt. Een tweede variabele, `DatabaseTierSwitchConfirmation`, moet exact gelijk zijn aan
`DatabaseTier` — bij een enkele, per ongeluk gewijzigde `DatabaseTier` faalt de build hard
(exitcode 3) in plaats van production stilzwijgend naar een andere database te laten omschakelen.
Een bewuste wissel vereist het expliciet bijwerken van *beide* variabelen in dezelfde actie.

**Identifier-casing per tier:** SQL Server gebruikt PascalCase (`dbo.AppSettings`, `ClubCode`);
Postgres gebruikt uitsluitend lowercase snake_case, nooit gequote (`public.appsettings`,
`clubcode`) — Postgres vouwt een ongequote identifier automatisch naar lowercase, waardoor een
latere gequote referentie (`"ClubCode"`) niet meer matcht. Zie §11 voor het volledige
schemaoverzicht per tier.

**Huidige status (2026-09-07):** Postgres draait in productie sinds #976. De admin-endpoints
(#887), de eerste planner-endpoints (#888), e-mailpersistentie/teamresolutie (#889) en de
AVG-opschoonprocedures (#861) zijn vertaald; de volledige planner-optimalisatie-engine
(`AutoPlanService`/`RescheduleService`, elf resterende endpoints) en de synchronisatie-orkestratie
(#890) staan nog open. `docs/ARCHITECTUUR-DATABASE-TIERS.md` is de gezaghebbende, actuele bron voor
precies welke onderdelen al vertaald zijn.

---

## 3. Tijdzones — UTC in database, lokale tijd in GUI

**Alle drie lagen moeten correct zijn. Een fout in één laag stapelt offsets op.** De regel is
identiek op beide tiers; alleen het databasemechanisme verschilt.

| Laag | SQL Server | Postgres |
|---|---|---|
| **Database** | `GETUTCDATE()` — **nooit `GETDATE()`** | Audit-kolommen zijn `TIMESTAMPTZ` (niet naïef `TIMESTAMP`) — Postgres normaliseert een `TIMESTAMPTZ`-waarde intern altijd naar UTC, ongeacht de sessietijdzone. `NOW()` hoeft dus niet aangepast te worden, in tegenstelling tot een naïeve kolom |
| **FunctionApp API** | `DateTime.SpecifyKind(dt, DateTimeKind.Utc)` na elke SQL-read | Npgsql leest een `TIMESTAMPTZ`-kolom terug als `DateTime` met `Kind=Utc` — geen aparte `SpecifyKind`-aanroep nodig voor deze kolommen |
| **Blazor WASM** | `.ToLocalTime()` vóór elke `.ToString()` | Idem — ongewijzigd, tier-onafhankelijk |

**Correct (SQL Server):**
```csharp
"UPDATE [dbo].[AppSettings] SET [LastSyncTimestamp] = GETUTCDATE()"

DateTime? ts = reader["LastSyncTimestamp"] != DBNull.Value
    ? DateTime.SpecifyKind(Convert.ToDateTime(reader["LastSyncTimestamp"]), DateTimeKind.Utc)
    : null;
// JSON-output: "2026-05-21T13:35:00Z"
```

```razor
@* Blazor — weergave, identiek op beide tiers: *@
@model.Timestamp.ToLocalTime().ToString("dd-MM-yyyy HH:mm")
@* → "21-05-2026 15:35" (CEST) *@
```

**Verplichte codereview-checks:**
- [ ] SQL Server: elke `INSERT`/`UPDATE` die een `DateTime`-kolom schrijft: `GETUTCDATE()` of `DateTime.UtcNow`?
- [ ] Postgres: elke nieuwe audit-/tijdstempelkolom gedefinieerd als `TIMESTAMPTZ`, niet naïef `TIMESTAMP`?
- [ ] JSON-response van API: heeft elke datetime een `Z`-suffix? (Controleer via DevTools → Network)
- [ ] Elke DateTime-weergave in Blazor: `.ToLocalTime()` aanwezig vóór `.ToString()`?

> **Incident (2026-05-21, SQL Server):** `GETDATE()` in `SaveLastSyncTimestampAsync` en 5 andere
> C#-bestanden sloeg CEST-tijd op. Fix in PR #246: alle 6 bestanden `GETDATE()` → `GETUTCDATE()`.
> **Zelfde klasse fout empirisch bevestigd voor Postgres (#854):** een naïeve `TIMESTAMP`-kolom +
> `NOW()` week 2 uur af van de werkelijke UTC-tijd op een `Europe/Amsterdam`-sessietijdzone — reden
> voor de `TIMESTAMPTZ`-keuze hierboven in plaats van een handmatige `timezone('utc', ...)`-wrap.

---

## 4. Multi-club isolatie — ClubCode discriminator

De applicatie is ontworpen voor gebruik door meerdere voetbalverenigingen. Elke nieuwe tabel met
club-specifieke data krijgt een ClubCode-kolom (SQL Server: `ClubCode`; Postgres: `clubcode`, §11).
Queries filteren altijd op de ClubCode uit de settingstabel van de actieve tier.

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

**Verplichte codereview-checks:**
- [ ] Nieuwe databasetabellen: ClubCode-kolom aanwezig (op beide tiers, in de conventie van §11)?
- [ ] Alle SELECT/UPDATE/DELETE op club-data: gefilterd op ClubCode?
- [ ] Geen hardcoded teamnaampatronen, accommodatienamen of GPS-coördinaten in SQL?

---

## 5. Geen club-specifieke waarden in code

Fallback-waarden (`?? "..."`) in C# mogen **nooit** een clubnaam, domeinnaam, persoonsnaam, plaatsnaam of adres bevatten. Als een verplichte instelling ontbreekt in de settingstabel → `InvalidOperationException`, geen stille fallback. Geldt identiek voor beide tiers.

```csharp
// Correct — faalt snel bij ontbrekende configuratie
var clubCode = GetSetting("clubCode")
    ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt");

// Fout — maskeert misconfiguratie en breekt andere clubs
var clubCode = GetSetting("clubCode") ?? "ABC";
```

Documentatie-voorbeelden gebruiken `[ClubNaam]` als placeholder, nooit echte club-specifieke waarden.

**Codereview-check:** scan op `?? "` gevolgd door een eigennaam, clubnaam of adres.

---

## 6. Secrets en configuratie

Productie-configuratie wordt **nooit** in git opgeslagen. De CI-pipeline genereert club-specifieke configuratie automatisch vanuit templates en GitHub Variables.

| Bestand | In git? | Toelichting |
|---|---|---|
| `BlazorAdmin/wwwroot/appsettings.Production.template.json` | ✓ | Bevat alleen `{{PLACEHOLDER}}` tokens |
| `BlazorAdmin/wwwroot/appsettings.Production.json` | ✗ | Gegenereerd door CI via `sed`-substitutie vanuit template + GitHub Variables |
| `BlazorAdmin/wwwroot/appsettings.json` | ✓ | Localhost-config, geen secrets |
| `FunctionApp/local.settings.json` | ✗ | Bevat `SqlConnectionString` en andere secrets |
| `FunctionApp/local.settings.template.json` | ✓ | Template zonder waarden |
| `FunctionApp.Postgres/local.settings.json` | ✗ | Bevat `PostgresConnectionString` |
| `FunctionApp.Postgres/local.settings.template.json` | ✓ | Template zonder waarden |
| `exports/*.csv` / `exports/*.xlsx` | ✗ | Persoonsgegevens — zie §7 |

**GitHub Variables** (per fork in te stellen via Settings → Secrets and variables → Actions):

| Variable | Inhoud | Alleen bij |
|---|---|---|
| `AZURE_FUNCTIONAPP_URL` | URL van de Function App | beide tiers |
| `AZURE_AD_TENANT_ID` | Entra Directory (tenant) ID | beide tiers |
| `AZURE_AD_CLIENT_ID` | Entra Application (client) ID | beide tiers |
| `POST_LOGOUT_REDIRECT_URL` | Clubwebsite-URL voor na uitloggen | beide tiers |
| `DatabaseTier` | `SqlServer` of `Postgres` — bepaalt welke boom gebouwd/deployed wordt | beide tiers |
| `DatabaseTierSwitchConfirmation` | Moet exact gelijk zijn aan `DatabaseTier`, anders faalt de deploy (exitcode 3) | beide tiers |
| `AZURE_SQL_SERVER_NAME` / `AZURE_SQL_RESOURCE_GROUP` | Azure SQL-resourcenamen | alleen `DatabaseTier=SqlServer` |

**GitHub Secrets:**

| Secret | Inhoud | Alleen bij |
|---|---|---|
| `AZURE_CREDENTIALS` | Service Principal JSON voor deployment | beide tiers |
| `AZURE_FUNCTION_KEY` | Function App-sleutel voor smoke tests | beide tiers |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Deployment token voor Static Web App | beide tiers |
| `SQL_CONNECTION_STRING` | Connectiestring naar Azure SQL | alleen `DatabaseTier=SqlServer` |
| `POSTGRES_CONNECTION_STRING` | Connectiestring naar Postgres. Norm: `sslmode=verify-full` mét het CA-certificaat van de provider (#1004); ontbreekt dat, dan draait de app op `Require` en meldt `/api/health` een `tlsWarning` (#1095) | alleen `DatabaseTier=Postgres` |

**Sportlink refresh-token (epic #986)** is een apart, door de club-beheerder zelf via de
Instellingen-UI gecaptured secret, niet via GitHub Secrets — zie §13 en `docs/SECRET-ROTATION.md`.

**Entra App Registration** mag niet via de Azure Portal handmatig worden aangepast. Gebruik altijd de idempotente scripts:

```powershell
az login
.\scripts\azure\Verify-AzureAuthSetup.ps1   # read-only diagnose
.\scripts\azure\Configure-EntraApp.ps1 -WhatIf  # toon wat zou wijzigen
.\scripts\azure\Configure-EntraApp.ps1          # apply
```

Na elke Entra-configuratiewijziging: sluit alle browsertabs van de Admin GUI en open een verse Incognito-sessie. MSAL bewaart het ID-token in `localStorage` — zonder verse sessie blijft de oude (rolloze) token in gebruik.

---

## 7. AVG / GDPR — absolute regels

Deze regels gelden altijd, op beide tiers, ook voor geautomatiseerde processen.

- `exports/*.csv` en `exports/*.xlsx` bevatten persoonsgegevens (namen, e-mails, telefoonnummers, geboortedatums). Ze mogen **nooit** gecommit of gepusht worden. `.gitignore`, pre-commit hook, pre-push hook en de GitHub Actions Security Gate blokkeren dit elk onafhankelijk.
- Alleen scripts (`.ps1`), `README.md` en handleidingen mogen in de `exports/`-map in git.
- Logging van persoonsgegevens is verboden: geen namen, geboortedatums, foto's of e-mailadressen in `ILogger`-output — ook niet tijdelijk tijdens diagnose van een Sportlink-endpoint (zie §13).
- E-mailadressen van leden worden uitsluitend via **BCC** gebruikt bij communicatie met derden.
- GitHub issues, PRs en commits bevatten nooit echte e-mailadressen, namen, accounts of club-specifieke locaties. Gebruik placeholders: `<admin-account>`, `@uwclub.nl`, `[CoordinatorNaam]`, `[Accommodatienaam]`.
- Retentiebeleid voor e-mailverwerking: anonimiseren na 30 dagen, verwijderen na 90 dagen (`sp_CleanupEmailVerwerking` op SQL Server; `PostgresCleanupProcedures` op Postgres, zelfde vensters).

**Git hooks activeren (verplicht bij elke nieuwe developer-machine):**
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Optioneel: `winget install gitleaks` (Windows) of `brew install gitleaks` (macOS) voor diepere secret-detectie.

---

## 8. Authenticatie en autorisatie — vijf lagen

`IsAuthenticated = true` is **niet** voldoende. Een gebruiker in de Entra-tenant kan inloggen zonder app-rol. Alle vijf lagen moeten onafhankelijk correct werken — een ontbrekende laag is een security-incident. Identiek op beide databasetiers.

| Laag | Wat | Waar |
|---|---|---|
| 1 | **Tenant-restrictie** — Single tenant App Registration; externe tenants kunnen niet inloggen | Azure Portal → Entra ID → App registrations |
| 2 | **Assignment required = Yes** — alleen pre-toegewezen gebruikers krijgen een token | Azure Portal → Enterprise applications → Properties |
| 3 | **App Roles** — `admin` en `user` gedefinieerd in App Registration manifest, `allowedMemberTypes: ["User"]` | Azure Portal → App registrations → App roles |
| 4 | **Frontend role-gate (App.razor)** — `IsInRole("admin") \|\| IsInRole("user")` BOVENOP `IsAuthenticated`; zonder rol → `NoAccess`-pagina, géén `MainLayout` | `BlazorAdmin/App.razor` |
| 5 | **Backend role-gate (EasyAuthHelper)** — elke admin-endpoint roept `RequireAdmin()` aan; valideert `roles`-claim in `X-MS-CLIENT-PRINCIPAL` | `FunctionApp/Admin/EasyAuthHelper.cs` (identieke kopie in `FunctionApp.Postgres/Admin/`, §2) |

**De server is de waarheid.** Een aanvaller kan de Blazor WASM modificeren. Laag 5 is leidend voor databeveiliging. Laag 4 is voor UX (geen app-shell voor niet-geautoriseerde gebruikers).

**Verplichte 3-user-test bij elke auth-gerelateerde wijziging:**

| Testgebruiker | Configuratie | Verwacht resultaat |
|---|---|---|
| Admin (eigen tenant) | Toegewezen met rol `admin` | Volledige UI, alle API-calls slagen |
| Gebruiker (eigen tenant) | Toegewezen met rol `user` | UI laadt, mutaties geblokkeerd |
| Geen rol (eigen tenant) | Niet toegewezen | `NoAccess`-pagina; géén sidebar, navigatie of FEEDBACK-knop |
| Externe gebruiker (andere tenant) | n.v.t. | Kan niet inloggen — Entra weigert vóór redirect |

Documenteer per release welke 3-user-tests zijn uitgevoerd. Een security-wijziging zonder deze tests wordt niet geaccepteerd.

**`CustomUserFactory` is verplicht.** Blazor WASM cast een `"roles": ["admin"]` JSON-array uit het ID-token naar één claim met de JSON-string als waarde, waardoor `IsInRole("admin")` altijd `false` retourneert ook al staat de rol in het token. De custom factory pakt de array uit naar losse claims. Registratie: `.AddAccountClaimsPrincipalFactory<CustomUserFactory>()` in `Program.cs`. Zie `BlazorAdmin/Services/CustomUserFactory.cs`.

**`options.UserOptions.RoleClaim = "roles"` is verplicht.** Entra schrijft app-rollen in de claim `roles`. Zonder deze mapping leest `ClaimsPrincipal.IsInRole()` uit `ClaimTypes.Role` en geeft altijd `false` terug.

---

## 9. Blazor auth-gate

**Kritieke regel — drie keer overtreden (PR #178, PR #179, auth-redirect-loop hotfix).**

De Blazor Admin UI mag nooit zichtbaar zijn voor niet-ingelogde gebruikers — ook niet kortstondig, ook niet de sidebar, navigatie of FEEDBACK-knop. Een ongeauthenticeerde gebruiker moet binnen 2–3 seconden naar de Microsoft-loginpagina worden gestuurd.

**Verboden patroon:**
```razor
<AuthorizeRouteView DefaultLayout="@typeof(MainLayout)">
    <NotAuthorized><RedirectToLogin /></NotAuthorized>
```
`AuthorizeRouteView` rendert `MainLayout` voor **alle** states, inclusief Authorizing en NotAuthorized.

**Verboden anti-patroon — blocking delay vóór auth-check:**
```razor
@if (_phase is Phase.Checking or Phase.Ready) { ... }  @* 1-2s vertraging *@
else if (_isAuthenticated) { ... }
```
De auth-check start pas ná de health-check delay. In InPrivate-sessies faalt MSAL silent-SSO; `NavigateToLogin` wordt te laat aangeroepen en de gebruiker blijft hangen op een laadscherm.

**Verplicht patroon:**
```razor
@if (_state == AppState.Initializing)      { @* spinner, geen layout *@ }
else if (_state == AppState.OnAuthRoute)   { <Router><RouteView /></Router> @* geen layout *@ }
else if (_state == AppState.Authenticated) { <Router><RouteView DefaultLayout="MainLayout" /></Router> }
@* RedirectingToLogin: NavigateToLogin is al aangeroepen, geen UI nodig *@
```

**Implementatieregels:**
1. `App.razor` injecteert `AuthenticationStateProvider` en roept `GetAuthenticationStateAsync()` aan als **eerste** actie — vóór elke andere check, health-call of splash.
2. `MainLayout` (sidebar, navigatie, FEEDBACK-knop) wordt **alleen** gerenderd voor de `Authenticated` state.
3. `/authentication/...` routes (MSAL callbacks) krijgen een aparte `Router`-branch zonder layout.
4. `NavigationManager.LocationChanged` bewaken om state opnieuw te evalueren na MSAL-callback.
5. `options.ProviderOptions.LoginMode = "redirect"` — geen popup-mode (wordt geblokt in Incognito/InPrivate).

**Verificatie bij elke Blazor auth-wijziging:**
1. Open de site in een verse Incognito/InPrivate sessie (geen oude cookies).
2. Microsoft-loginpagina moet binnen 2–3 seconden verschijnen.
3. Vóór login: géén sidebar, navigatie, FEEDBACK-knop of "An unhandled error" zichtbaar.
4. Na inloggen: volledige admin UI laadt, alle API-calls slagen met de Bearer token.
5. F12 → Network tab: MSAL redirect naar `login.microsoftonline.com` bevestigen.

---

## 10. MSAL-configuratie checklist

Elk van deze items moet aanwezig zijn in een werkende deployment, ongeacht databasetier. Een gemist item veroorzaakt een vastlopende login.

| # | Item | Locatie | Reden |
|---|---|---|---|
| 1 | `<script src="_content/Microsoft.Authentication.WebAssembly.Msal/AuthenticationService.js">` vóór `blazor.webassembly.js` | `wwwroot/index.html` | MSAL JS-bridge — zonder dit script doet `RemoteAuthenticatorView` niets |
| 2 | `options.ProviderOptions.LoginMode = "redirect"` | `Program.cs` | Voorkomt popup-blocker failures in InPrivate/Incognito |
| 3 | `options.UserOptions.RoleClaim = "roles"` | `Program.cs` | Entra schrijft rollen in de claim `roles`, niet in `ClaimTypes.Role` |
| 4 | `.AddAccountClaimsPrincipalFactory<CustomUserFactory>()` | `Program.cs` | Pakt `"roles": ["admin"]` JSON-array uit naar losse claims |
| 5 | `appsettings.Production.json` met `AzureAd.Authority` en `AzureAd.ClientId` | `wwwroot/` | Gegenereerd door CI — zonder deze waarden crasht MSAL bij initialisatie |
| 6 | `<WasmApplicationEnvironmentName>Production</WasmApplicationEnvironmentName>` voor Release | `BlazorAdmin.csproj` | .NET 10: zonder dit laadt Blazor `appsettings.json` (localhost) in productie |
| 7 | `<CompressionEnabled>false</CompressionEnabled>` | `BlazorAdmin.csproj` | Azure SWA serveert pre-compressed `.wasm.br` zonder correcte `Content-Encoding: br` header → SRI integrity check faalt in Chrome Incognito |
| 8 | SPA redirect URI `https://<host>/authentication/login-callback` in App Registration | Azure Portal | Entra weigert de redirect als deze URI ontbreekt |
| 9 | `Authentication.razor` op `@page "/authentication/{action}"` met `<RemoteAuthenticatorView>` | `Pages/` | Verwerkt MSAL login-callback en logout-callback |
| 10 | Easy Auth ingeschakeld op Function App + `EasyAuthHelper.RequireAdmin()` op elk admin-endpoint | Azure Portal + `FunctionApp/Admin/` of `FunctionApp.Postgres/Admin/` | Server-side validatie van Bearer token en admin-rol |
| 11 | `Cache-Control: no-cache` voor `/index.html` en `/` | `staticwebapp.config.json` | Zonder dit cachet de browser een oude `index.html` die verwijst naar assets uit een eerdere deploy → 404's en SRI-mismatches |

---

## 11. Database — schema's en conventies (per tier)

**SQL Server (`FunctionApp`, rollback-tier):**

| Schema | Doel |
|---|---|
| `dbo` | Configuratie: `AppSettings`, `Season`, `DateTable`, `Speeltijden` |
| `stg` | Tijdelijke staging-tabellen; worden elke sync-run leeggemaakt |
| `his` | Persistente historietabellen met `mta_inserted` / `mta_modified` metadata |
| `mta` | `source_target_mapping`-tabel die dynamische DDL en MERGE-operaties aanstuurt |
| `pub` | Alleen-lezen views voor consumers |
| `planner` | E-mailverwerking en planning |
| `avg` | AVG-beschermde data (teambegeleiding); toegang beperkt |

**Postgres (`FunctionApp.Postgres`, productietier sinds #976):**

| Schema | Doel |
|---|---|
| `public` | Configuratie én de meeste beheertabellen: `appsettings`, `velden`, `speeltijden`, `teams`, `teamaliassen` (dbo-equivalent) |
| `stg` | Staging, zelfde rol als SQL Server |
| `his` | Historietabellen — `bk_*`-sleutelkolommen zijn hier `GENERATED ALWAYS AS (...) STORED`, niet een gewone, ETL-gevulde kolom (#818, #853) |
| `planner` | E-mailverwerking en classificatiecorrectie |
| `avg` | AVG-beschermde data — identieke functie als SQL Server |
| *(geen `mta`, geen `pub`)* | Merge-orchestratie leeft in C# (`PostgresMergeOrchestrator`), niet in een `mta`-mappingtabel; de drie `pub.*`-rapportageviews zijn bewust niet vertaald — nul consumenten gevonden, zie §17 |

**Naamconventies:**
- Entity-properties in C# gebruiken **camelCase** overeenkomstig de Sportlink API JSON-veldnamen, op beide tiers.
- SQL Server: SQL-kolomnamen gebruiken de **exacte casing** zoals gedefinieerd in het schema (bijv. `SportlinkApiUrl`).
- Postgres: kolom- en tabelnamen zijn **altijd lowercase snake_case, nooit gequote** (bijv. `sportlinkapiurl`) — zie §2 voor de rationale. `KnownEntities.cs`/`EntityDefinition.Create` valideert dit al bij het schrijven van een nieuwe entiteit.
- Configuratie leeft in de settingstabel van de actieve tier, niet in code of config-bestanden.

**Stored procedures / equivalenten:**
- SQL Server: `sp_CreateTargetTableFromSource` (dynamische DDL), `sp_MergeStgToHis` (UPSERT via `MERGE`).
- Postgres: dezelfde stappen leven in C# (`PostgresSchemaGenerator`, `PostgresMergeOrchestrator`) — geen Postgres-functie/-procedure, zelfde architectuurbeslissing als de AVG-opschoonprocedures (§7).

**Async/await:** alle I/O is asynchroon op beide tiers. Exceptie-handling op function entry-points, niet diep in helperfuncties.

**Database-migraties:**
- **SQL Server:** `deploy.yml` voert het PostDeployment-script (`Database/Script.PostDeployment1.sql`) inmiddels automatisch uit bij elke productie-deploy.
- **Postgres:** heeft **geen** CI-automatisering voor migraties. Nieuwe `Database.Postgres/migrations/*.sql`-bestanden moeten handmatig worden toegepast via `.\scripts\dev\Invoke-PostgresMigrations.ps1` vóór de code-deploy. Zie §17 voor de volledige toelichting van dit gat.

---

## 12. Berichtverwerking — kanaal-agnostische pipeline

De verwerkingspipeline (classificeer → valideer → verwerk → bouw antwoord) is kanaal-onafhankelijk. Welk kanaal de input levert (e-mail, dry-run, WhatsApp, Socials) maakt niet uit voor de kern van de logica. Dit geldt vandaag voor de SQL Server-tier; de Postgres-vertaling van de volledige AI-pijplijn (`BerichtAiService`, `EmailProcessorFunction`, >2700 regels) is bewust nog niet gestart (#889's scope-afbakening).

**Klassen zijn hernoemd naar kanaal-agnostische namen:**
- `BerichtAiService` — classificatie
- `BerichtClassificatie` — classificatieresultaat
- `InkomendBericht` — kanaal-agnostisch inputmodel
- `BerichtResponseGenerator` — antwoordgeneratie

**Elke nieuwe kanaal-koppeling:**
1. Implementeert een input-adapter: kanaalbericht → `InkomendBericht`
2. Voert kanaalspecifieke guards uit (idempotency, domeinfilter)
3. Roept de gezamenlijke pipeline aan
4. Verwerkt het resultaat via de kanaalspecifieke output-router

Nooit de pipeline herhalen of `EmailProcessorFunction`-methoden direct aanroepen vanuit een nieuw kanaal.

---

## 13. Sportlink Web Extension — schrijfrichting naar Sportlink Club

> **Volledig protocol, endpoint-contracten en de agent-tokengrens:
> [SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md).** Dit is uitsluitend de
> architectuurplaatsing — het volledige mechanisme niet hier dupliceren.

Sinds epic #986 heeft de applicatie, naast de bestaande alleen-lezen ETL-sync (Sportlink → eigen
database), ook een **schrijfrichting**: de Admin GUI kan wijzigingen (kleedkamertoewijzing, veld,
en meer) rechtstreeks terugschrijven naar Sportlink Club namens de club. Deze schrijfrichting is
**uitsluitend op de Postgres-tier** gebouwd — er is geen SQL Server-equivalent en dat is niet
gepland, omdat nieuwe functionaliteit sinds #976 primair op de productietier landt.

**Kernonderdelen:**
- `Planner.Shared/Integrations/SportlinkClub/SportlinkClubClient.cs` — centrale HTTP-client voor alle mutaties, met een consistent fetch-snapshot-en-echo-patroon: eerst het volledige actuele record ophalen, dan alleen het gewijzigde veld overschrijven en het geheel terugsturen — Sportlink accepteert geen partiële updates op meerdere onderzochte endpoints.
- `SportlinkMutationGuard` / `ISportlinkMutationAuditService` — elke schrijfactie wordt afgedwongen en gelogd vóór uitvoering.
- Rolgebaseerde serviceaccount-koppeling per club (#988): de club kiest zelf welke Sportlink-rol de extensie gebruikt, geen gedeelde of hardcoded credential.

**Harde architectuurregel — de agent-tokengrens.** Een coding agent (Claude Code) mag **nooit**
zelf een Sportlink refresh- of accesstoken lezen, vasthouden of gebruiken om de Sportlink API aan
te roepen — dit is zowel technisch afgedwongen als beleidsmatig vastgelegd. Een token dat toch
zichtbaar wordt in een agent-sessie (ook via een paste van de gebruiker zelf) geldt als verbrand en
mag nooit worden opgeslagen, gelogd of hergebruikt — alleen de niet-geheime payload (bijv. een
JSON-body uit een netwerktrace) mag geëxtraheerd worden. Het token zelf leeft uitsluitend in de
FunctionApp.Postgres-runtime, gecaptured door een mens via de Instellingen-UI.

**"Geen aannames"-principe:** het schrijf-contract van een Sportlink-endpoint wordt nooit
gefabriceerd. Elke mutatie-implementatie is gegrond in een echte, door de eigenaar aangeleverde
netwerktrace van de Sportlink Club-UI zelf — nooit een geraden JSON-schema. Meerdere keren dit
epic gebleken nodig (#992's kleedkamer-identifierformaat, #993's daadwerkelijke endpoint en
veldtype), zie `docs/SPORTLINK-WEB-EXTENSION.md` §4 voor de volledige geschiedenis van gevonden en
gecorrigeerde aannames.

**AVG-grens specifiek voor deze laag:** het Sportlink Match-detailendpoint bevat officials-PII
(naam, geboortedatum, foto). Diagnose van dit endpoint gebeurt uitsluitend via veld-scoped
extractie (bijv. `JsonDocument`-gebaseerde single-field lookup) — nooit een volledige
response-body-log, ook niet tijdelijk.

---

## 14. Lagen altijd synchroon

Database-schema, API-endpoint en Blazor GUI worden **altijd in dezelfde commit** bijgewerkt — op de tier waar de wijziging landt.

- Nieuw database-veld → bijbehorend API-veld en Blazor-weergave in dezelfde PR.
- Nieuwe enum, template-sleutel of regeltype in code → GUI-optie in dezelfde commit.
- Nooit een GUI die verwijst naar een API-veld dat nog niet bestaat, en andersom.
- Een endpoint dat alleen op één tier bestaat (bijv. de Sportlink Web Extension, uitsluitend Postgres): de GUI toont het feature-gedeelte alleen als de actieve tier het ondersteunt, nooit een knop die op de andere tier een 404 geeft.

---

## 15. Versiebeheer en releases

**Semantic Versioning:**

| Type | Wanneer |
|---|---|
| `MAJOR` (x.0.0) | Nieuwe architectuurlaag, breaking API-wijziging, grote nieuwe functie-set |
| `MINOR` (x.y.0) | Nieuwe feature, backwards compatible |
| `PATCH` (x.y.z) | Bugfix, beveiligingspatch, documentatie zonder gedragswijziging |

**Conventional Commits → versie-bump:**

| Commit-prefix | Bump |
|---|---|
| `feat:` | MINOR |
| `fix:` | PATCH |
| `security:` | PATCH |
| `BREAKING CHANGE:` in commit-body | MAJOR |
| `chore:`, `docs:`, `refactor:` | geen bump |

**Verplicht: alle drie de csproj's synchroon bumpen** —
`FunctionApp/fa-dev-sportlink-01.csproj`, `BlazorAdmin/BlazorAdmin.csproj` **én
`FunctionApp.Postgres/FunctionApp.Postgres.csproj`**. De derde wordt gemakkelijk gemist als een
wijziging alleen Postgres-tier-bestanden raakt — geen van de eerste twee verandert dan mee, en
niets waarschuwt ervoor vóór een release. Controleer bij twijfel `/api/health`'s `version`-veld op
de Postgres-tier.

**CHANGELOG.md — verplicht bij elke feature of fix:**
1. Voeg wijziging toe onder `## [Unreleased]`.
2. Gebruik secties `### Added`, `### Changed`, `### Fixed`, `### Security`, `### Removed`.
3. Schrijf voor de gebruiker: "Beheerders kunnen nu X" — niet "Methode Y refactored".
4. **Notatie van issuenummers:** `(#N)` sluit dat issue automatisch bij de volgende release-tag
   (`close-released-issues.yml`). Gebruik dit alleen voor werk dat in díe versie zit. Een
   kruisverwijzing naar een vervolgpunt hoort in proza: "zie issue #N" — nooit `(#N)`.

**Vóór een release:**
1. Verplaats `## [Unreleased]` naar `## [x.y.z] — YYYY-MM-DD`.
2. Voeg een lege `## [Unreleased]` terug bovenaan.
3. Bump de versie in alle drie de csproj's (zie boven).
4. Tag aanmaken op `main` → triggert `release.yml` → GitHub Release wordt automatisch aangemaakt.

---

## 16. CI/CD en Security Gate

**De Security Gate is leidend.** Zolang de check `Security Gate — blokkeert merge bij fout` rood is, mag er niets gemerged worden — ook niet als alle andere checks groen zijn. Geldt ongeacht welke tier een PR raakt.

**GitHub Actions checks bij elke PR:**

| Check | Wat |
|---|---|
| Secret Detection (gitleaks) | Detecteert hardcoded secrets en tokens |
| PII File Detection | Blokkeert CSV/Excel-bestanden |
| PII Pattern Scan | Scant op AVG-gevoelige patronen (e-mails, BSN, telefoonnummers) |
| PII in Documentatie | Controleert CHANGELOG.md en docs op PII |
| Dependency Vulnerability Scan (Trivy) | Scant NuGet-packages op bekende CVE's |
| `fresh-db` / `fresh-db-postgres` | Verse-database-verificatie per tier: kernobjecten, identifier-casing, demodata-aantallen |
| Security Gate | Aggregeert alle bovenstaande checks — merge-blokkade bij fout |

**Na een PR-merge:** controleer ook de `deploy.yml`-workflow op `main` via `gh run list --branch main --limit 3`. Als de build faalt: direct fixen of melden. Niet rapporteren dat de PR geslaagd is vóór de deploy-workflow groen is.

**Branch-strategie:**

```
main     ← develop              (via PR, release naar productie)
  └──── hotfix/#<nr>-<slug>       (via PR, urgente productiefix)

develop  ← feature/#<nr>-<slug> (via PR, per issue)
```

- `main` is altijd deploybaar — de live-branch voor alle clubs.
- `develop` is de integratiebranch — geen deploy, voor lokaal combineren en testen van features.
- `feature/`/`hotfix/`/`docs/`/`chore/` branches zijn tijdelijke werkbranches.
- Nooit direct committen of pushen naar `main` of `develop`.

---

## 17. Bekende beperkingen

**SQL Server-migratiegap — opgelost.** `deploy.yml` voert het PostDeployment-script
(`Database/Script.PostDeployment1.sql`) inmiddels automatisch uit bij elke productie-deploy. Dit
was tot 2026 een open gat (13 objecten moesten op 2026-05-20 handmatig gemigreerd worden na een
productie-crash) — zie de gearchiveerde §15 in [ARCHITECTURE-V2.md](ARCHITECTURE-V2.md) voor de
volledige incidentgeschiedenis.

**Postgres-migratiegap — nog open.** In tegenstelling tot de SQL Server-tier heeft de Postgres-tier
**geen** CI- of deploy-automatisering voor migraties. Nieuwe `Database.Postgres/migrations/*.sql`
moeten handmatig worden toegepast via `.\scripts\dev\Invoke-PostgresMigrations.ps1` vóór elke
deploy die er een oplevert. Structurele fix nog niet gepland.

**Postgres mist de drie `pub.*`-rapportageviews.** Een zoekactie over de volledige broncode leverde
nul consumenten op voor `pub.Matches`/`pub.Teams`/`pub.DateTable` — expliciet en gemotiveerd niet
vertaald (#861). Een toekomstige externe-rapportagebehoefte kan deze alsnog toevoegen als een
aparte, bewuste beslissing.

**Postgres-planner is grotendeels nog niet vertaald.** Alleen `GET /api/planner/veldbezetting`
(#888) is af; de overige elf planner-endpoints — inclusief de eigenlijke dagplanning-
optimalisatie-engine (`AutoPlanService`) — bestaan nog uitsluitend op de SQL Server-tier.

**Sportlink Web Extension is Postgres-only, met vier van zeven schrijfacties nog geblokkeerd.**
Officials (#994), wijzigingsverzoek datum/tijd/accommodatie (#995), oefenwedstrijd aanmaken (#997)
en de actiepad van #996 (goedkeuren/afwijzen) wachten op een door een mens uitgevoerde,
live netwerktrace van het echte Sportlink Club-scherm — zie §13's "geen aannames"-principe.

---

*Zie ook:*
- [ARCHITECTURE-V2.md](ARCHITECTURE-V2.md) — gearchiveerde, single-tier voorganger van dit document
- [ARCHITECTUUR-DATABASE-TIERS.md](ARCHITECTUUR-DATABASE-TIERS.md) — volledige multi-tier strategie en sub-issue-index
- [SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md) — volledig protocol van de schrijfrichting naar Sportlink Club
- [SETUP-NIEUWE-CLUB.md](../SETUP-NIEUWE-CLUB.md) — installatie voor nieuwe clubs, beide tiers
- [DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) — lokale ontwikkelomgeving, beide tiers
- [CONTRIBUTING.md](../CONTRIBUTING.md) — bijdrageproces en branch-strategie
- [SECURITY.md](../SECURITY.md) — volledig beveiligingsprotocol
- [docs/VERSIONING.md](VERSIONING.md) — definitie van bug, feature en enhancement
- [docs/ENTRA-AUTH-BEHEER.md](ENTRA-AUTH-BEHEER.md) — Entra ID configuratie in detail
