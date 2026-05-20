# Azure Entra ID — Auth setup voor Admin GUI

Dit document beschrijft de volledige authenticatie- en autorisatieconfiguratie van de Blazor Admin GUI in Azure Entra ID, en hoe je deze met één commando idempotent kunt verifiëren en herstellen.

> **TL;DR — bij elke wijziging in auth-config:**
>
> ```powershell
> az login
> .\scripts\Verify-AzureAuthSetup.ps1       # diagnose, read-only
> .\scripts\Configure-EntraApp.ps1 -WhatIf  # toon wat zou veranderen
> .\scripts\Configure-EntraApp.ps1          # apply (idempotent)
> ```
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
| 3b | **Optional claims** voor `roles` in tokens | `optionalClaims.idToken[]` en `optionalClaims.accessToken[]` |
| 4 | **Frontend role-gate** | `App.razor` checkt `IsInRole("admin") \|\| IsInRole("user")` |
| 5 | **Backend role-gate** | `EasyAuthHelper.RequireAdmin()` op elke admin endpoint |

Layer 1-3b zijn Azure-config, Layer 4-5 zijn code. Layer 4-5 worden gevalideerd in CI; Layer 1-3b worden gevalideerd door `Verify-AzureAuthSetup.ps1`.

## Identifiers (Sportlink VRC)

| Item | Waarde |
|---|---|
| Tenant | v.v. V.R.C. (`[TENANT_ID]`) |
| App Registration `displayName` | Sportlink Admin GUI |
| App Registration `clientId` | `[CLIENT_ID]` |
| Service Principal (Enterprise App) `objectId` | `7948575e-4849-45bc-a0d0-122900c91808` |
| Admin user | `jaapadmin@[club-domein]` |
| SWA host | `[swa-unique-id].7.azurestaticapps.net` |
| SPA redirect URI | `https://[swa-unique-id].7.azurestaticapps.net/authentication/login-callback` |

## Workflow

### Eerste setup

1. Installeer de Azure CLI (`az --version` moet `>= 2.50` zijn).
2. `az login` op een account met `Application Administrator` of `Cloud Application Administrator` rol in de tenant.
3. `az account show` → controleer dat `tenantId` = `[TENANT_ID]`. Zo nee: `az account set --subscription <id-binnen-[club-domein]>`.
4. Run `.\scripts\Configure-EntraApp.ps1`. Dit script is idempotent: bestaande configuratie wordt niet aangepast, alleen ontbrekende stukken worden bijgevuld.
5. Verifieer met `.\scripts\Verify-AzureAuthSetup.ps1`. Alle regels moeten ✓ groen zijn.
6. Sluit bestaande Admin GUI browser-tabs. Open een verse Incognito/InPrivate sessie. Log opnieuw in met jaapadmin@[club-domein].

### Nieuwe gebruiker toevoegen

```powershell
# Voor 'user' rol (read-only):
az ad sp show --id 7948575e-4849-45bc-a0d0-122900c91808 --query "appRoles[?value=='user'].id" -o tsv  # → role-id
$user = az ad user show --id 'nieuwe.user@[club-domein]' | ConvertFrom-Json
az rest --method POST `
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/7948575e-4849-45bc-a0d0-122900c91808/appRoleAssignedTo" `
    --body "{`"principalId`":`"$($user.id)`",`"resourceId`":`"7948575e-4849-45bc-a0d0-122900c91808`",`"appRoleId`":`"<role-id>`"}"
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

Als je meerdere Entra tenants hebt: `az account show` toont welke actief is. `Configure-EntraApp.ps1` faalt vroeg met een duidelijke melding als de tenant niet `[TENANT_ID...]` is. Switch met `az account set --subscription <id>`.

### Role claim niet in token zonder optionalClaims

Hoewel Entra documenteert dat app-rollen "automatisch" in tokens komen, blijkt in de praktijk dat de `roles` claim alleen consistent in het ID-token wordt geleverd als deze óók in `optionalClaims.idToken` staat. Layer 3b is dus géén overbodige verdediging maar noodzakelijk voor Layer 4.

### `roles` mag NIET in `optionalClaims.accessToken`

Microsoft Graph weigert `roles` in `optionalClaims.accessToken` met de fout:

> `Property accessToken in payload has a value that does not match schema.`

`roles` is een implicit access-token claim die Entra zelf toevoegt bij app-role assignments — handmatig zetten is niet ondersteund. `Configure-EntraApp.ps1` patcht daarom alleen `optionalClaims.idToken`, niet `.accessToken`.

### `IsInRole` is case-sensitive

In code: gebruik `IsInRole("admin")` (kleine letters), niet `IsInRole("Admin")`. De App Role `value` is `admin`; de `displayName` (`"Admin"`) wordt niet in tokens geschreven.

### Ander tenant-account ingelogd in browser

Als je dezelfde browser gebruikt voor jaap.vanbeusekom@profile.nl én jaapadmin@[club-domein], kan Microsoft Account Switcher het verkeerde account suggereren. Gebruik altijd een Incognito-sessie voor jaapadmin-tests, of klik op "Use another account" in de Microsoft loginpagina.

## Verificatie — 3-user-test (verplicht na elke auth-wijziging)

| Test-user | Configuratie in Azure | Verwacht in browser |
|---|---|---|
| `jaapadmin@[club-domein]` | Toegewezen, role `admin` | Volledige UI, API werkt, sidebar zichtbaar |
| 2e [club-domein] user | Toegewezen, role `user` | UI laadt, GET-API werkt, mutaties (later) geblokkeerd |
| 3e [club-domein] user | **Niet** toegewezen | Geen token van Entra → blijft op login → met directe URL alsnog `NoAccess` pagina |
| Guest / andere tenant | n.v.t. | Entra weigert login vóór redirect |

Documenteer de uitkomst per release. Geen 3-user-test → geen acceptatie.

## Tracking

- Issue [#185](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/185) — Frontend role-gate (Layer 4), gemerged in v2/develop
- Issue [#187](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues/187) — Idempotente Entra-setup scripts (deze docs)
- Memory `security_defense_in_depth.md` — 5-laags model, lessons learned
- CLAUDE.md sectie "Defense in depth — vijf auth-lagen, allemaal verplicht"
