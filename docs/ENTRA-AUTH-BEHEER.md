# Azure Entra ID — Auth setup voor Admin GUI

Dit document beschrijft de volledige authenticatie- en autorisatieconfiguratie van de Blazor Admin GUI in Azure Entra ID, en hoe je deze met één commando idempotent kunt verifiëren en herstellen.

> **TL;DR — bij elke wijziging in auth-config:**
>
> ```powershell
> az login
>
> # Diagnose, read-only. -ClientId en -ExpectedTenantId zijn verplicht.
> .\scripts\azure\Verify-AzureAuthSetup.ps1 -ClientId '<app-id>' -ExpectedTenantId '<tenant-id>'
>
> # Toon wat zou veranderen. Alle drie de parameters zijn verplicht.
> .\scripts\azure\Configure-EntraApp.ps1 -ClientId '<app-id>' -ExpectedTenantId '<tenant-id>' -AdminUserPrincipalName '<admin-upn>' -WhatIf
>
> # Apply (idempotent) — zelfde regel zonder -WhatIf.
> .\scripts\azure\Configure-EntraApp.ps1 -ClientId '<app-id>' -ExpectedTenantId '<tenant-id>' -AdminUserPrincipalName '<admin-upn>'
> ```
>
> **Laat geen parameter weg.** `Configure-EntraApp.ps1` heeft `-ClientId`, `-ExpectedTenantId` én
> `-AdminUserPrincipalName` als `Mandatory`; `Verify-AzureAuthSetup.ps1` heeft `-ClientId` en
> `-ExpectedTenantId` als `Mandatory` (`-AdminUserPrincipalName` is daar optioneel). PowerShell valt
> anders terug op een interactieve prompt — precies wat je niet wilt in het scenario waarvoor dit
> document bestaat.
>
> Lees vóór de eerste run ook [Bekende valstrikken](#bekende-valstrikken); dat is de sectie die de
> meeste tijd bespaart.
>
> Daarna: log uit + verse Incognito browser + opnieuw inloggen.

## Doel

De Blazor Admin GUI (Static Web App) authenticeert tegen Entra ID via MSAL (OIDC + PKCE). Alleen gebruikers die expliciet zijn toegewezen met de App Role `admin` of `user` mogen de UI zien en de API aanroepen.

## Architectuur — defense in depth

| Laag | Wat | Waar |
|---|---|---|
| 1 | **Single tenant** App Registration | `signInAudience = AzureADMyOrg` |
| 2 | **Assignment required** op Service Principal | `appRoleAssignmentRequired = true` |
| 3a | **App Roles** in manifest | `admin` en `user`, `isEnabled = true` |
| 3b | **Optional claims** voor `roles` in het **ID-token** | `optionalClaims.idToken[]` — nooit `accessToken`, zie [de valstrik hieronder](#roles-mag-niet-in-optionalclaimsaccesstoken) |
| 4 | **Frontend role-gate** | `App.razor` checkt `IsInRole("admin") \|\| IsInRole("user")` |
| 5 | **Backend role-gate** | `EasyAuthHelper.RequireAdmin()` op elke admin endpoint — aanwezig in **beide** tiers: `FunctionApp.Postgres/Admin/EasyAuthHelper.cs` (productie) en `FunctionApp/Admin/EasyAuthHelper.cs` (SQL Server-tier) |

> **Gedicht in #1276.** De Layer 5-scan doorzocht uitsluitend `FunctionApp/Admin/` — de SQL
> Server-tier — zodat een endpoint zonder rolcontrole in `FunctionApp.Postgres/Admin/` het script
> groen passeerde. Hij leest nu de tierlijst uit `scripts/ci/database-tiers.json` en scant de
> `Admin/`-map van elke tier met `built = true`; een pad dat niet oplost is sindsdien een `FAIL`
> in plaats van stilte, en een ontbrekende tierlijst ook.
>
> Daarbij bleek de scan zelf niet te kloppen: hij vergeleek per bestand het aantal `[Function]`-
> attributen met het aantal letterlijke `EasyAuthHelper.RequireAdmin`-voorkomens, en meldde
> daardoor **15 van de 24 bestanden ten onrechte als onbeschermd** — die endpoints lopen via
> `AdminEndpoint.ExecuteAsync`, dat de rolcontrole centraal doet. De scan redeneert nu per
> endpoint: elk stuk met een `HttpTrigger` moet langs `EasyAuthHelper.RequireAdmin`,
> `AdminEndpoint.ExecuteAsync` of `SportlinkEndpointSupport.Execute*` gaan. Een `TimerTrigger`
> wordt overgeslagen — die heeft geen aanroeper met een rol.
>
> Handmatig te draaien: `pwsh scripts/azure/Verify-AzureAuthSetup.ps1`. Verwacht resultaat op een
> gezonde codebase: 66 HTTP-endpoints per tier, alle bewaakt.

### Wat bewaakt welke laag, en wanneer

Layer 1–3b zijn Azure-config, Layer 4–5 zijn code. Sinds #1277 is er precies één laag die in de
PR-CI bewaakt wordt — laag 4. De overige vier zijn alleen tegen Entra zelf of ná de merge naar
`main` te controleren:

| Laag | Bewaakt door | Wanneer | Automatisch? |
|---|---|---|---|
| 1 — Single tenant | `Verify-AzureAuthSetup.ps1` | Handmatig, tegen Entra | ❌ |
| 2 — Assignment required | `Verify-AzureAuthSetup.ps1` | Handmatig, tegen Entra | ❌ |
| 3a — App Roles | `Verify-AzureAuthSetup.ps1` | Handmatig, tegen Entra | ❌ |
| 3b — Optional claims | `Verify-AzureAuthSetup.ps1` | Handmatig, tegen Entra | ❌ |
| 4 — Frontend role-gate | `BlazorAdmin.Tests/AuthGateTests.cs` + `CustomUserFactoryTests.cs` (#1277) **plus** `Verify-AzureAuthSetup.ps1` (statisch: zoekt `IsInRole("admin")` in `App.razor`) | Automatisch, **in de PR-CI** | ✅ |
| 5 — Backend role-gate | Smoke tests in `deploy.yml` (401 verwacht op een admin-endpoint met alleen een function key, zonder token, en met een gefakete `X-MS-CLIENT-PRINCIPAL`) **plus** `Verify-AzureAuthSetup.ps1` (statisch, **beide gebouwde tiers** sinds #1276) | Automatisch, maar **pas ná de merge naar `main`** | ⚠️ gedeeltelijk |

Layer 4 was tot #1277 de enige laag zonder énige automatische controle: de beslissing stond inline
in het `@code`-blok van `App.razor` en viel daarmee buiten elk testproject. Hij staat nu als pure
functie in `BlazorAdmin/Services/AuthGate.cs` en wordt op twee niveaus getest, omdat deze laag op
twee manieren kan omvallen:

| Faalwijze | Hoe hij eruitziet | Afgedekt door |
|---|---|---|
| **Verwijdering** — de rolcontrole is weg of staat altijd op `true` | Iedereen met een geldig token krijgt de app-shell | `AuthGateTests` — de drie rolgevallen uit de 3-user-test, plus de MSAL-callbackroute |
| **Stille variant** — de controle staat er nog, maar geeft altijd `false` | Ook een echte admin ziet `NoAccess`; er verandert niets zichtbaars in de code | `CustomUserFactoryTests` — bewijst dat een Entra-`roles`-JSON-array daadwerkelijk tot `IsInRole("admin") == true` leidt |

Die tweede is de gevaarlijke: zonder de `roles`-claimmapping of de `CustomUserFactory` cast Blazor
WASM de array `["admin"]` naar één claim met de hele JSON-string als waarde, waarna `IsInRole` faalt
terwijl de rol gewoon in het token staat. Een grep op `App.razor` ziet daar niets van.

Beide testklassen zijn mutatiegetest: met de rolcontrole uitgeschakeld vallen er vier om, met het
uitpakken van de rollen uitgeschakeld drie. Een groene test die niet rood kán worden, bewaakt niets.

`Verify-AzureAuthSetup.ps1` blijft daarnaast de enige plek waar alle vijf lagen in één keer
langskomen — inclusief de vier die alleen tegen Entra zelf te controleren zijn.

## Identifiers (club-specifiek — haal op via Azure Portal)

| Item | Hoe ophalen |
|---|---|
| Tenant ID | Azure Portal → Entra ID → Overview → `Tenant ID` |
| App Registration `displayName` | Sportlink Admin GUI |
| App Registration `clientId` | Azure Portal → Entra ID → App registrations → Sportlink Admin GUI → Application (client) ID |
| Service Principal `objectId` | Azure Portal → Entra ID → Enterprise applications → Sportlink Admin GUI → Object ID |
| Admin user | `admin@voorbeeld.nl` |
| SWA host | Zie Azure Portal → Static Web App → URL |
| SPA redirect URI | `https://<SWA_HOST>/authentication/login-callback` |

## Workflow

### Eerste setup

0. **De App Registration moet al bestaan.** `Configure-EntraApp.ps1` *configureert* een bestaande
   registratie — het maakt er geen aan, en begint met `az ad app show --id $ClientId`. Volg voor een
   nieuwe club eerst [`../SETUP-NIEUWE-CLUB.md`](../SETUP-NIEUWE-CLUB.md) §4a (App Registration +
   SPA-platform met redirect-URI `https://<SWA_HOST>/authentication/login-callback`) en noteer de
   ClientId en de TenantId.
1. Installeer de Azure CLI (`az --version` moet `>= 2.50` zijn) en PowerShell 7 of hoger (beide
   scripts hebben `#requires -Version 7.0`).
2. `az login` op een account met `Application Administrator` of `Cloud Application Administrator` rol in de tenant.
3. `az account show` → controleer dat je op de juiste tenant bent. Zo nee: `az account set --subscription <subscription-naam-of-id>`.
4. Doe eerst een dry-run en daarna de apply. Dit script is idempotent: bestaande configuratie wordt
   niet aangepast, alleen ontbrekende stukken worden bijgevuld.
   ```powershell
   .\scripts\azure\Configure-EntraApp.ps1 -ClientId '<app-id>' -ExpectedTenantId '<tenant-id>' -AdminUserPrincipalName '<admin-upn>' -WhatIf
   .\scripts\azure\Configure-EntraApp.ps1 -ClientId '<app-id>' -ExpectedTenantId '<tenant-id>' -AdminUserPrincipalName '<admin-upn>'
   ```
5. Verifieer. Alle regels moeten ✓ groen zijn.
   ```powershell
   .\scripts\azure\Verify-AzureAuthSetup.ps1 -ClientId '<app-id>' -ExpectedTenantId '<tenant-id>' -AdminUserPrincipalName '<admin-upn>'
   ```
   Zonder `-AdminUserPrincipalName` slaat het script de controle op de admin-toewijzing over.
6. Sluit bestaande Admin GUI browser-tabs. Open een verse Incognito/InPrivate sessie. Log opnieuw in met `admin@voorbeeld.nl` (jouw admin-account).

### Nieuwe gebruiker toevoegen

```powershell
# Haal eerst de objectId van de Service Principal op:
$spObjectId = az ad sp show --display-name "Sportlink Admin GUI" --query "id" -o tsv

# Voor 'user' rol (read-only):
$roleId = az ad sp show --id $spObjectId --query "appRoles[?value=='user'].id" -o tsv
$user = az ad user show --id 'nieuwe.user@voorbeeld.nl' | ConvertFrom-Json
az rest --method POST `
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spObjectId/appRoleAssignedTo" `
    --body "{`"principalId`":`"$($user.id)`",`"resourceId`":`"$spObjectId`",`"appRoleId`":`"$roleId`"}"
```

Of via Azure Portal: **Enterprise applications → Sportlink Admin GUI → Users and groups → Add user/group**.

### Bestaande gebruiker rol wijzigen

In Azure Portal: **Enterprise applications → Sportlink Admin GUI → Users and groups → user selecteren → Edit assignment**. Of via CLI: verwijder oude assignment, voeg nieuwe toe.

### Gebruiker volledig ontzeggen

Verwijder de role-assignment. Door Layer 2 (`appRoleAssignmentRequired = true`) krijgt de user niet eens meer een ID-token — Entra weigert vóór de redirect.

## Bekende valstrikken

### Cached ID-token na config-wijziging

MSAL bewaart een ID-token in `localStorage`. Als je de App Roles of optionalClaims wijzigt nadat een gebruiker is ingelogd, blijft het oude (rolloze) token in cache. **Verplicht na elke wijziging:** logout + verse Incognito sessie + opnieuw inloggen.

### Verkeerde tenant in az CLI

Als je meerdere Entra tenants hebt: `az account show` toont welke actief is. `Configure-EntraApp.ps1` faalt vroeg met een duidelijke melding als de verkeerde tenant actief is. Switch met `az account set --subscription <subscription-naam-of-id>`.

### Role claim niet in token zonder optionalClaims

Hoewel Entra documenteert dat app-rollen "automatisch" in tokens komen, blijkt in de praktijk dat de `roles` claim alleen consistent in het ID-token wordt geleverd als deze óók in `optionalClaims.idToken` staat. Layer 3b is dus géén overbodige verdediging maar noodzakelijk voor Layer 4.

### `roles` mag NIET in `optionalClaims.accessToken`

Microsoft Graph weigert `roles` in `optionalClaims.accessToken` met de fout:

> `Property accessToken in payload has a value that does not match schema.`

`roles` is een implicit access-token claim die Entra zelf toevoegt bij app-role assignments — handmatig zetten is niet ondersteund. `Configure-EntraApp.ps1` patcht daarom alleen `optionalClaims.idToken`, niet `.accessToken`.

### `IsInRole` is case-sensitive

In code: gebruik `IsInRole("admin")` (kleine letters), niet `IsInRole("Admin")`. De App Role `value` is `admin`; de `displayName` (`"Admin"`) wordt niet in tokens geschreven.

### Ander tenant-account ingelogd in browser

Als je dezelfde browser gebruikt voor een persoonlijk Microsoft-account én `admin@voorbeeld.nl`, kan Microsoft Account Switcher het verkeerde account suggereren. Gebruik altijd een Incognito-sessie voor admin-tests, of klik op "Use another account" in de Microsoft loginpagina.

## Verificatie — gebruikersrollentest (verplicht na elke auth-wijziging)

Dit is de test die `CLAUDE.md` de **3-user-test** noemt. Sinds #988 telt hij vijf profielen; de
eerste drie rijen zijn die oorspronkelijke drie. Eén test, drie namen in omloop — houd deze tabel
aan als de bron.

| Test-user | Configuratie in Azure | Verwacht in browser |
|---|---|---|
| `admin@voorbeeld.nl` | Toegewezen, role `admin` | Volledige UI, API werkt, sidebar zichtbaar |
| 2e club-user | Toegewezen, role `user` | UI laadt, GET-API werkt, mutaties (later) geblokkeerd |
| 3e club-user | **Niet** toegewezen | Geen token van Entra → blijft op login → met directe URL alsnog `NoAccess` pagina |
| Guest / andere tenant | n.v.t. | Entra weigert login vóór redirect |
| 5e profiel (#988) | Toegewezen, **alléén** role `Wedstrijdzaken` (geen admin/user) | `App.razor`'s `hasAccessRole` blijft `false` → `NoAccess`-pagina. **Verwacht en gewenst** resultaat: `Wedstrijdzaken` is een aanvullende rol voor Sportlink-mutatie-endpoints (#991+), geen vervanging voor `admin`/`user` — er bestaat nog geen niet-Admin-GUI-oppervlak dat deze rol gebruikt. Niet als regressie lezen. |

Documenteer de uitkomst per release. Geen gebruikersrollentest → geen acceptatie.

**Kanttekening bij de laatste rij (#988):** de server-side handhaving (`EasyAuthHelper.RequireRole`) is
lokaal niet te testen — die geeft altijd `null` (toegestaan) terug zolang `WEBSITE_SITE_NAME`
ontbreekt (elke lokale dev-run). Voor #988 zelf volstaat bevestigen dat de rol in Entra bestaat en
toewijsbaar is; de server-side handhaving wordt inhoudelijk pas getest zodra #991 het eerste
`RequireRole(req, "Wedstrijdzaken")`-endpoint oplevert (via de SWA-CLI-emulator of een echte
staging-deploy met deze testgebruiker).

## Tracking

- Issue [#185](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/185) — Frontend role-gate (Layer 4) — gesloten, geleverd in v2.1.1
- Issue [#187](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/187) — Idempotente Entra-setup scripts (deze docs) — gesloten, geleverd in v2.1.1
- CLAUDE.md sectie "Defense in depth — vijf auth-lagen, allemaal verplicht"
