# Architectuurprincipes — Sportlink Wedstrijdzaken

Dit document beschrijft alle architectuurafspraken en -conventies die gelden voor dit project. Ze zijn opgebouwd uit concrete beslissingen en incidents uit de ontwikkelhistorie. Afwijkingen worden geblokkeerd door de Security Gate in CI of teruggegeven bij codereview.

---

## Inhoudsopgave

1. [Systeemoverzicht](#1-systeemoverzicht)
2. [Tijdzones — UTC in database, lokale tijd in GUI](#2-tijdzones--utc-in-database-lokale-tijd-in-gui)
3. [Multi-club isolatie — ClubCode discriminator](#3-multi-club-isolatie--clubcode-discriminator)
4. [Geen club-specifieke waarden in code](#4-geen-club-specifieke-waarden-in-code)
5. [Secrets en configuratie](#5-secrets-en-configuratie)
6. [AVG / GDPR — absolute regels](#6-avg--gdpr--absolute-regels)
7. [Authenticatie en autorisatie — vijf lagen](#7-authenticatie-en-autorisatie--vijf-lagen)
8. [Blazor auth-gate](#8-blazor-auth-gate)
9. [MSAL-configuratie checklist](#9-msal-configuratie-checklist)
10. [Database — schema's en conventies](#10-database--schemas-en-conventies)
11. [Berichtverwerking — kanaal-agnostische pipeline](#11-berichtverwerking--kanaal-agnostische-pipeline)
12. [Lagen altijd synchroon](#12-lagen-altijd-synchroon)
13. [Versiebeheer en releases](#13-versiebeheer-en-releases)
14. [CI/CD en Security Gate](#14-cicd-en-security-gate)
15. [Bekende beperkingen](#15-bekende-beperkingen)

---

## 1. Systeemoverzicht

```
Browser (beheerder)
  └── Azure Static Web Apps (Free tier) — Blazor WebAssembly
        Serveert alleen statische bestanden; geen SWA-proxying naar de API
        MSAL: Bearer token wordt automatisch meegestuurd naar de Function App
        │
        │ HTTPS + Bearer token (Entra ID)
        ▼
  Azure Functions (Consumption plan) — .NET 10, isolated worker
        Easy Auth: valideert Bearer token, injecteert X-MS-CLIENT-PRINCIPAL
        EasyAuthHelper: checkt 'admin' rol op alle /api/beheer/*, /api/test/*, /api/feedback/*
        FunctionApp/Admin/       → admin-beheer endpoints
        FunctionApp/Processing/  → BerichtPipeline (kanaal-agnostisch)
        FunctionApp/Feedback/    → feedbackwidget → GitHub Issues
        │
        ▼
  Azure SQL (Free tier, 32 GB)
        dbo.AppSettings + AppSettingsAudit   (club-configuratie)
        dbo.EmailTemplateInstellingen, TeamVoorkeurTijden, TeamRegels
        dbo.UitgeslotenEmailAdressen, Velden, VeldBeschikbaarheid
        planner.EmailVerwerking              (e-maillog)
        his.* / stg.* / pub.*               (ETL-pipeline)
```

**Technologiestack:** .NET 10 · Azure Functions v4 · Blazor WebAssembly · Azure SQL · Microsoft Graph API · Azure OpenAI · Azure Static Web Apps · Entra ID (single-tenant)

**ETL-data flow:**
```
Sportlink REST API → Azure Function → stg.* (staging, per run leeggemaakt)
                                    → sp_MergeStgToHis → his.* (persistent)
                                                       → pub.* (read-only views)
```

**Auth-stroom:**

| Laag | Mechanisme |
|---|---|
| Frontend | MSAL (`AddMsalAuthentication`) + `AuthorizationMessageHandler` |
| Transport | Bearer token in `Authorization` header |
| Function App | Azure Easy Auth (AllowAnonymous mode) + `EasyAuthHelper.RequireAdmin()` |
| Lokaal (dev) | Bypass: `WEBSITE_SITE_NAME` afwezig → altijd toestaan |

---

## 2. Tijdzones — UTC in database, lokale tijd in GUI

**Alle drie lagen moeten correct zijn. Een fout in één laag stapelt offsets op.**

| Laag | Verplicht | Fout patroon |
|---|---|---|
| **SQL Server** | `GETUTCDATE()` — **nooit `GETDATE()`** | `GETDATE()` retourneert lokale servertijd (CEST = UTC+2). API markeert dit vervolgens als UTC. Blazor telt er +2u bij op. Resultaat: tijdstip 2u in de toekomst. |
| **FunctionApp API** | `DateTime.SpecifyKind(dt, DateTimeKind.Utc)` na elke SQL-read | Zonder SpecifyKind is `Kind = Unspecified`. JSON-serializer voegt geen `Z`-suffix toe. Clients interpreteren het inconsistent. |
| **Blazor WASM** | `.ToLocalTime()` vóór elke `.ToString()` | UTC-tijden tonen zonder conversie geeft tijden die 1–2u achter lijken voor Nederlandse gebruikers. |

**Correct:**
```csharp
// FunctionApp — schrijven naar SQL:
"UPDATE [dbo].[AppSettings] SET [LastSyncTimestamp] = GETUTCDATE()"

// FunctionApp — lezen uit SQL:
DateTime? ts = reader["LastSyncTimestamp"] != DBNull.Value
    ? DateTime.SpecifyKind(Convert.ToDateTime(reader["LastSyncTimestamp"]), DateTimeKind.Utc)
    : null;
// JSON-output: "2026-05-21T13:35:00Z"
```

```razor
@* Blazor — weergave: *@
@model.Timestamp.ToLocalTime().ToString("dd-MM-yyyy HH:mm")
@* → "21-05-2026 15:35" (CEST) *@
```

**Verplichte codereview-checks:**
- [ ] Elke `INSERT`/`UPDATE` die een `DateTime`-kolom schrijft: `GETUTCDATE()` of `DateTime.UtcNow`?
- [ ] Elke DateTime gelezen uit SQL: `DateTime.SpecifyKind(..., DateTimeKind.Utc)` aanwezig?
- [ ] JSON-response van API: heeft elke datetime een `Z`-suffix? (Controleer via DevTools → Network)
- [ ] Elke DateTime-weergave in Blazor: `.ToLocalTime()` aanwezig vóór `.ToString()`?

> **Incident (2026-05-21):** `GETDATE()` in `SaveLastSyncTimestampAsync` en 5 andere C#-bestanden sloeg CEST-tijd op. API markeerde als UTC. Blazor voegde +2u toe. Dashboard toonde 'Laatste sync' als toekomstig tijdstip. Fix in PR #246: alle 6 bestanden `GETDATE()` → `GETUTCDATE()`.

---

## 3. Multi-club isolatie — ClubCode discriminator

De applicatie is ontworpen voor gebruik door meerdere voetbalverenigingen. Elke nieuwe tabel met club-specifieke data krijgt een `ClubCode`-kolom. Queries filteren altijd op de `ClubCode` uit `dbo.AppSettings`.

```sql
-- Correct
SELECT * FROM [dbo].[TeamVoorkeurTijden]
WHERE [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings])

-- Fout: hardcoded waarde
WHERE [ClubCode] = 'ABC'
```

**Verplichte codereview-checks:**
- [ ] Nieuwe databasetabellen: `ClubCode NVARCHAR(20) NOT NULL` aanwezig?
- [ ] Alle SELECT/UPDATE/DELETE op club-data: gefilterd op `ClubCode`?
- [ ] Geen hardcoded teamnaampatronen, accommodatienamen of GPS-coördinaten in SQL?

---

## 4. Geen club-specifieke waarden in code

Fallback-waarden (`?? "..."`) in C# mogen **nooit** een clubnaam, domeinnaam, persoonsnaam, plaatsnaam of adres bevatten. Als een verplichte instelling ontbreekt in `dbo.AppSettings` → `InvalidOperationException`, geen stille fallback.

```csharp
// Correct — faalt snel bij ontbrekende configuratie
var clubCode = GetSetting("clubCode")
    ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

var accommodatie = GetSetting("accommodatie")
    ?? throw new InvalidOperationException("Vereiste instelling 'accommodatie' ontbreekt in dbo.AppSettings");

// Fout — maskeert misconfiguratie en breekt andere clubs
var clubCode = GetSetting("clubCode") ?? "ABC";
var naam = GetSetting("plannerAfzenderNaam") ?? "Veldplanner";
```

Documentatie-voorbeelden gebruiken `[ClubNaam]` als placeholder, nooit echte club-specifieke waarden.

**Codereview-check:** scan op `?? "` gevolgd door een eigennaam, clubnaam of adres.

---

## 5. Secrets en configuratie

Productie-configuratie wordt **nooit** in git opgeslagen. De CI-pipeline genereert club-specifieke configuratie automatisch vanuit templates en GitHub Variables.

| Bestand | In git? | Toelichting |
|---|---|---|
| `BlazorAdmin/wwwroot/appsettings.Production.template.json` | ✓ | Bevat alleen `{{PLACEHOLDER}}` tokens |
| `BlazorAdmin/wwwroot/appsettings.Production.json` | ✗ | Gegenereerd door CI via `sed`-substitutie vanuit template + GitHub Variables |
| `BlazorAdmin/wwwroot/appsettings.json` | ✓ | Localhost-config, geen secrets |
| `FunctionApp/local.settings.json` | ✗ | Bevat `SqlConnectionString` en andere secrets |
| `FunctionApp/local.settings.template.json` | ✓ | Template zonder waarden |
| `exports/*.csv` / `exports/*.xlsx` | ✗ | Persoonsgegevens — zie §6 |

**GitHub Variables** (per fork in te stellen via Settings → Secrets and variables → Actions):

| Variable | Inhoud |
|---|---|
| `AZURE_FUNCTIONAPP_URL` | URL van de Function App |
| `AZURE_AD_TENANT_ID` | Entra Directory (tenant) ID |
| `AZURE_AD_CLIENT_ID` | Entra Application (client) ID |
| `POST_LOGOUT_REDIRECT_URL` | Clubwebsite-URL voor na uitloggen |

**GitHub Secrets:**

| Secret | Inhoud |
|---|---|
| `AZURE_CREDENTIALS` | Service Principal JSON voor deployment |
| `AZURE_FUNCTION_KEY` | Function App-sleutel voor smoke tests |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Deployment token voor Static Web App |

**Entra App Registration** mag niet via de Azure Portal handmatig worden aangepast. Gebruik altijd de idempotente scripts:

```powershell
az login
.\scripts\Verify-AzureAuthSetup.ps1   # read-only diagnose
.\scripts\Configure-EntraApp.ps1 -WhatIf  # toon wat zou wijzigen
.\scripts\Configure-EntraApp.ps1          # apply
```

Na elke Entra-configuratiewijziging: sluit alle browsertabs van de Admin GUI en open een verse Incognito-sessie. MSAL bewaart het ID-token in `localStorage` — zonder verse sessie blijft de oude (rolloze) token in gebruik.

---

## 6. AVG / GDPR — absolute regels

Deze regels gelden altijd, ook voor geautomatiseerde processen.

- `exports/*.csv` en `exports/*.xlsx` bevatten persoonsgegevens (namen, e-mails, telefoonnummers, geboortedatums). Ze mogen **nooit** gecommit of gepusht worden. `.gitignore`, pre-commit hook, pre-push hook en de GitHub Actions Security Gate blokkeren dit elk onafhankelijk.
- Alleen scripts (`.ps1`), `README.md` en handleidingen mogen in de `exports/`-map in git.
- Logging van persoonsgegevens is verboden: geen namen of e-mailadressen in `ILogger`-output.
- E-mailadressen van leden worden uitsluitend via **BCC** gebruikt bij communicatie met derden.
- GitHub issues, PRs en commits bevatten nooit echte e-mailadressen, namen, accounts of club-specifieke locaties. Gebruik placeholders: `<admin-account>`, `@uwclub.nl`, `[CoordinatorNaam]`, `[Accommodatienaam]`.
- Retentiebeleid voor e-mailverwerking: anonimiseren na 30 dagen, verwijderen na 90 dagen (`planner.sp_CleanupEmailVerwerking`).

**Git hooks activeren (verplicht bij elke nieuwe developer-machine):**
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Optioneel: `winget install gitleaks` (Windows) of `brew install gitleaks` (macOS) voor diepere secret-detectie.

---

## 7. Authenticatie en autorisatie — vijf lagen

`IsAuthenticated = true` is **niet** voldoende. Een gebruiker in de Entra-tenant kan inloggen zonder app-rol. Alle vijf lagen moeten onafhankelijk correct werken — een ontbrekende laag is een security-incident.

| Laag | Wat | Waar |
|---|---|---|
| 1 | **Tenant-restrictie** — Single tenant App Registration; externe tenants kunnen niet inloggen | Azure Portal → Entra ID → App registrations |
| 2 | **Assignment required = Yes** — alleen pre-toegewezen gebruikers krijgen een token | Azure Portal → Enterprise applications → Properties |
| 3 | **App Roles** — `admin` en `user` gedefinieerd in App Registration manifest, `allowedMemberTypes: ["User"]` | Azure Portal → App registrations → App roles |
| 4 | **Frontend role-gate (App.razor)** — `IsInRole("admin") \|\| IsInRole("user")` BOVENOP `IsAuthenticated`; zonder rol → `NoAccess`-pagina, géén `MainLayout` | `BlazorAdmin/App.razor` |
| 5 | **Backend role-gate (EasyAuthHelper)** — elke admin-endpoint roept `RequireAdmin()` aan; valideert `roles`-claim in `X-MS-CLIENT-PRINCIPAL` | `FunctionApp/Admin/EasyAuthHelper.cs` |

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

## 8. Blazor auth-gate

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

## 9. MSAL-configuratie checklist

Elk van deze items moet aanwezig zijn in een werkende deployment. Een gemist item veroorzaakt een vastlopende login.

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
| 10 | Easy Auth ingeschakeld op Function App + `EasyAuthHelper.RequireAdmin()` op elk admin-endpoint | Azure Portal + `FunctionApp/Admin/` | Server-side validatie van Bearer token en admin-rol |
| 11 | `Cache-Control: no-cache` voor `/index.html` en `/` | `staticwebapp.config.json` | Zonder dit cachet de browser een oude `index.html` die verwijst naar assets uit een eerdere deploy → 404's en SRI-mismatches |

---

## 10. Database — schema's en conventies

**Schema-indeling:**

| Schema | Doel |
|---|---|
| `dbo` | Configuratie: `AppSettings`, `Season`, `DateTable`, `Speeltijden` |
| `stg` | Tijdelijke staging-tabellen; worden elke sync-run leeggemaakt |
| `his` | Persistente historietabellen met `mta_inserted` / `mta_modified` metadata |
| `mta` | `source_target_mapping`-tabel die dynamische DDL en MERGE-operaties aanstuurt |
| `pub` | Alleen-lezen views voor consumers |
| `planner` | E-mailverwerking en planning |
| `avg` | AVG-beschermde data (teambegeleiding); toegang beperkt |

**Naamconventies:**
- Entity-properties in C# gebruiken **camelCase** overeenkomstig de Sportlink API JSON-veldnamen.
- SQL-kolomnamen gebruiken de **exacte casing** zoals gedefinieerd in het schema (bijv. `SportlinkApiUrl`, niet `sportlinkApiUrl`).
- Configuratie leeft in `dbo.AppSettings`, niet in code of config-bestanden.

**Stored procedures:**
- `sp_CreateTargetTableFromSource` — dynamische DDL voor staging-tabellen.
- `sp_MergeStgToHis` — UPSERT via SQL `MERGE`; verplaatst staging-data naar historietabellen.

**Async/await:** alle I/O is asynchroon. Exceptie-handling op function entry-points, niet diep in helperfuncties.

**Database-migraties:** het PostDeployment-script (`Database/Script.PostDeployment1.sql`) wordt **niet** automatisch uitgevoerd door `deploy.yml`. Nieuwe kolommen en tabellen moeten handmatig worden gemigreerd vóór deployment of de code moet backward-compatible zijn. Zie §15 voor de volledige toelichting.

---

## 11. Berichtverwerking — kanaal-agnostische pipeline

De verwerkingspipeline (classificeer → valideer → verwerk → bouw antwoord) is kanaal-onafhankelijk. Welk kanaal de input levert (e-mail, dry-run, WhatsApp, Socials) maakt niet uit voor de kern van de logica.

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

## 12. Lagen altijd synchroon

Database-schema, API-endpoint en Blazor GUI worden **altijd in dezelfde commit** bijgewerkt.

- Nieuw database-veld → bijbehorend API-veld en Blazor-weergave in dezelfde PR.
- Nieuwe enum, template-sleutel of regeltype in code → GUI-optie in dezelfde commit.
- Nooit een GUI die verwijst naar een API-veld dat nog niet bestaat, en andersom.

---

## 13. Versiebeheer en releases

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

**CHANGELOG.md — verplicht bij elke feature of fix:**
1. Voeg wijziging toe onder `## [Unreleased]`.
2. Gebruik secties `### Added`, `### Changed`, `### Fixed`, `### Security`, `### Removed`.
3. Schrijf voor de gebruiker: "Beheerders kunnen nu X" — niet "Methode Y refactored".

**Vóór een release:**
1. Verplaats `## [Unreleased]` naar `## [x.y.z] — YYYY-MM-DD`.
2. Voeg een lege `## [Unreleased]` terug bovenaan.
3. Bump de versie in `FunctionApp/fa-dev-sportlink-01.csproj` en `BlazorAdmin/BlazorAdmin.csproj`.
4. Tag aanmaken op `main` → triggert `release.yml` → GitHub Release wordt automatisch aangemaakt.

---

## 14. CI/CD en Security Gate

**De Security Gate is leidend.** Zolang de check `Security Gate — blokkeert merge bij fout` rood is, mag er niets gemerged worden — ook niet als alle andere checks groen zijn.

**GitHub Actions checks bij elke PR:**

| Check | Wat |
|---|---|
| Secret Detection (gitleaks) | Detecteert hardcoded secrets en tokens |
| PII File Detection | Blokkeert CSV/Excel-bestanden |
| PII Pattern Scan | Scant op AVG-gevoelige patronen (e-mails, BSN, telefoonnummers) |
| PII in Documentatie | Controleert CHANGELOG.md en docs op PII |
| Dependency Vulnerability Scan (Trivy) | Scant NuGet-packages op bekende CVE's |
| Security Gate | Aggregeert alle bovenstaande checks — merge-blokkade bij fout |

**Na een PR-merge:** controleer ook de `deploy.yml`-workflow op `main` via `gh run list --branch main --limit 3`. Als de build faalt: direct fixen of melden. Niet rapporteren dat de PR geslaagd is vóór de deploy-workflow groen is.

**Branch-strategie:**

```
main  ←──── feature/#<nr>-<slug>   (via PR)
  └──── hotfix/#<nr>-<slug>        (via PR, urgente productiefix)
```

- `main` is altijd deploybaar — de live-branch voor alle clubs.
- `feature/` en `hotfix/` zijn tijdelijke werkbranches vanuit `main`.
- Nooit direct committen of pushen naar `main`.

---

## 15. Bekende beperkingen

**Database-migratie gap:** `deploy.yml` voert het PostDeployment-script (`Database/Script.PostDeployment1.sql`) **niet** automatisch uit. Nieuwe database-objecten (kolommen, tabellen, stored procedures) die via dit script worden toegevoegd, worden niet automatisch aangemaakt in de productiedatabase bij deployment.

*Gevolg:* op 2026-05-20 zijn 13 objecten handmatig gemigreerd via `sqlcmd` na een productie-crash. 

*Workaround:* voer nieuwe migraties handmatig uit via Azure Portal (Query Editor) of `sqlcmd` vóór de code-deploy, of zorg dat de code backward-compatible is met de oude schema-versie totdat de migratie is uitgevoerd.

*Structurele fix:* een DB-migratiestap toevoegen aan `deploy.yml` is gepland maar nog niet geïmplementeerd.

---

*Zie ook:*
- [SETUP-NIEUWE-CLUB.md](../SETUP-NIEUWE-CLUB.md) — installatie voor nieuwe clubs
- [CONTRIBUTING.md](../CONTRIBUTING.md) — bijdrageproces en branch-strategie
- [SECURITY.md](../SECURITY.md) — volledig beveiligingsprotocol
- [docs/VERSIONING.md](VERSIONING.md) — definitie van bug, feature en enhancement
- [docs/ENTRA-AUTH-BEHEER.md](ENTRA-AUTH-BEHEER.md) — Entra ID configuratie in detail
