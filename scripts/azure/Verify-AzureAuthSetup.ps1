#requires -Version 7.0
<#
.SYNOPSIS
    Read-only diagnose van de Entra ID auth-configuratie voor de Sportlink Admin GUI.

.DESCRIPTION
    Loopt alle vijf defense-in-depth lagen langs en print per laag de actuele
    state. Maakt GEEN wijzigingen. Te gebruiken vóór en na Configure-EntraApp.ps1.

    Verplichte pre-conditie: 'az login' op het juiste account in de tenant van
    jouw club. Wordt aan het begin gecontroleerd; als de tenant niet matcht stopt
    het script direct.

    Waar vind ik mijn waarden?
      ClientId        → Azure Portal › App registrations › jouw app › Overview
      ExpectedTenantId → Azure Portal › Microsoft Entra ID › Overview › Tenant ID

.PARAMETER ClientId
    Application (client) ID van de Entra App Registration van jouw club.

.PARAMETER ExpectedTenantId
    Tenant ID van de Microsoft Entra tenant van jouw club.

.PARAMETER AdminUserPrincipalName
    UPN van de te controleren admin-gebruiker (optioneel).

.EXAMPLE
    .\scripts\Verify-AzureAuthSetup.ps1 -ClientId '<jouw-app-id>' -ExpectedTenantId '<jouw-tenant-id>'
    .\scripts\Verify-AzureAuthSetup.ps1 -ClientId '<jouw-app-id>' -ExpectedTenantId '<jouw-tenant-id>' -AdminUserPrincipalName 'admin@jouwclub.nl'

.NOTES
    Zie SETUP.md en docs/AZURE-ENTRA-SETUP.md voor het volledige protocol.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = 'Application (client) ID uit jouw Entra App Registration')]
    [string] $ClientId,

    [Parameter(Mandatory = $true, HelpMessage = 'Tenant ID uit Microsoft Entra ID › Overview')]
    [string] $ExpectedTenantId,

    [string] $AdminUserPrincipalName = ''
)

$ErrorActionPreference = 'Stop'

$script:Failures = 0

function Write-Section($text) {
    Write-Host ''
    Write-Host "═══ $text ═══" -ForegroundColor Cyan
}

function Write-Pass($text) { Write-Host "  ✓ $text" -ForegroundColor Green }
function Write-Fail($text) { Write-Host "  ✗ $text" -ForegroundColor Red; $script:Failures++ }
function Write-Warn($text) { Write-Host "  ⚠ $text" -ForegroundColor Yellow }
function Write-Info($text) { Write-Host "    $text" -ForegroundColor DarkGray }

# ── Banner: dit script wijzigt NIETS ──────────────────────────────────────────
Write-Host ''
Write-Host '┌─────────────────────────────────────────────────────────────────┐' -ForegroundColor DarkCyan
Write-Host '│  Verify-AzureAuthSetup.ps1 — READ-ONLY diagnose                 │' -ForegroundColor DarkCyan
Write-Host '│  Dit script wijzigt NIETS in Azure. Het toont alleen de state.  │' -ForegroundColor DarkCyan
Write-Host '│  Voor de fix: scripts\Configure-EntraApp.ps1                    │' -ForegroundColor DarkCyan
Write-Host '└─────────────────────────────────────────────────────────────────┘' -ForegroundColor DarkCyan

# ── Pre-flight ────────────────────────────────────────────────────────────────
Write-Section 'Pre-flight'

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Fail 'Niet ingelogd bij Azure CLI. Voer eerst `az login` uit.'
    exit 1
}
if ($account.tenantId -ne $ExpectedTenantId) {
    Write-Fail "Verkeerde tenant: $($account.tenantId) (verwacht $ExpectedTenantId)"
    Write-Info 'Switch met: az account set --subscription <sub-in-vv-vrc.nl-tenant>'
    exit 1
}
Write-Pass "Tenant: $($account.tenantDefaultDomain) ($($account.tenantId))"
Write-Pass "Subscription: $($account.name)"
Write-Pass "Ingelogd als: $($account.user.name)"

# ── Layer 1 — App Registration (Single tenant) ────────────────────────────────
Write-Section 'Layer 1 — App Registration (Single tenant)'

$app = az ad app show --id $ClientId 2>$null | ConvertFrom-Json
if (-not $app) {
    Write-Fail "App Registration met clientId $ClientId niet gevonden."
    exit 2
}
Write-Pass "App: '$($app.displayName)' (objectId $($app.id))"

if ($app.signInAudience -eq 'AzureADMyOrg') {
    Write-Pass "signInAudience = AzureADMyOrg (Single tenant)"
} else {
    Write-Fail "signInAudience = $($app.signInAudience) — verwacht 'AzureADMyOrg'"
}

$spaUris = @($app.spa.redirectUris)
if ($spaUris -match '^https://.*\.azurestaticapps\.net/authentication/login-callback$') {
    Write-Pass "SPA redirect URI aanwezig: $($spaUris -join ', ')"
} else {
    Write-Fail "SPA redirect URI ontbreekt of verkeerd: $($spaUris -join ', ')"
    Write-Info 'Verwacht: https://<host>.azurestaticapps.net/authentication/login-callback'
}

# ── Layer 2 — Service Principal: Assignment required ──────────────────────────
Write-Section 'Layer 2 — Enterprise Application (Assignment required)'

$sp = az ad sp list --filter "appId eq '$ClientId'" 2>$null | ConvertFrom-Json | Select-Object -First 1
if (-not $sp) {
    Write-Fail 'Service Principal (Enterprise Application) niet gevonden.'
    exit 3
}
Write-Pass "Service Principal: '$($sp.displayName)' (id $($sp.id))"

if ($sp.appRoleAssignmentRequired -eq $true) {
    Write-Pass 'appRoleAssignmentRequired = true — alleen toegewezen users krijgen een token'
} else {
    Write-Fail 'appRoleAssignmentRequired = false — IEDEREEN in tenant kan inloggen zonder rol'
    Write-Info 'Fix: .\scripts\Configure-EntraApp.ps1'
}

# ── Layer 3 — App Roles in manifest ───────────────────────────────────────────
Write-Section 'Layer 3 — App Roles in manifest'

$adminRole = $app.appRoles | Where-Object { $_.value -eq 'admin' }
$userRole  = $app.appRoles | Where-Object { $_.value -eq 'user' }

if ($adminRole -and $adminRole.isEnabled) {
    Write-Pass "App Role 'admin' aanwezig en enabled (id $($adminRole.id))"
} else {
    Write-Fail "App Role 'admin' ontbreekt of is disabled"
}
if ($userRole -and $userRole.isEnabled) {
    Write-Pass "App Role 'user' aanwezig en enabled (id $($userRole.id))"
} else {
    Write-Fail "App Role 'user' ontbreekt of is disabled"
}

# ── Layer 3b — Optional claims voor 'roles' in id token ──────────────────────
# 'roles' hoort alleen in optionalClaims.idToken. Voor accessToken voegt Entra
# de roles claim automatisch toe (app-role claim is impliciet); expliciet zetten
# geeft een schema-fout in Microsoft Graph.
Write-Section "Layer 3b — Optional claims ('roles' in idToken)"

$idClaims = $app.optionalClaims.idToken | Where-Object { $_.name -eq 'roles' }

if ($idClaims) {
    Write-Pass "'roles' aanwezig in optionalClaims.idToken"
} else {
    Write-Fail "'roles' ontbreekt in optionalClaims.idToken"
    Write-Info "Zonder dit komt de role claim niet in het ID token van Blazor WASM."
}

# ── Layer 4 — Frontend role-gate (App.razor) ──────────────────────────────────
Write-Section 'Layer 4 — Frontend role-gate (App.razor) — code-side'

$appRazor = Join-Path $PSScriptRoot '..\BlazorAdmin\App.razor' -Resolve -ErrorAction SilentlyContinue
if ($appRazor -and (Get-Content $appRazor -Raw) -match 'IsInRole\("admin"\)') {
    Write-Pass "App.razor bevat IsInRole-check (Layer 4 actief in code)"
} else {
    Write-Fail "App.razor bevat GEEN IsInRole-check — Layer 4 in code ontbreekt"
}

# ── Layer 5 — Backend RequireAdmin op alle protected endpoints ────────────────
Write-Section 'Layer 5 — Backend RequireAdmin (EasyAuthHelper) — code-side'

$adminFns = Join-Path $PSScriptRoot '..\FunctionApp\Admin' -Resolve -ErrorAction SilentlyContinue
if ($adminFns) {
    $files = Get-ChildItem -Path $adminFns -Filter '*.cs' | Where-Object { $_.Name -ne 'EasyAuthHelper.cs' }
    $missing = @()
    foreach ($f in $files) {
        $content = Get-Content $f.FullName -Raw
        $functions = [regex]::Matches($content, '\[Function\("[^"]+"\)\]')
        $requireAdmin = [regex]::Matches($content, 'EasyAuthHelper\.RequireAdmin')
        if ($functions.Count -gt $requireAdmin.Count) {
            $missing += "$($f.Name) — $($functions.Count) [Function], $($requireAdmin.Count) RequireAdmin"
        }
    }
    if ($missing.Count -eq 0) {
        Write-Pass "Alle Admin*Function bestanden hebben RequireAdmin op elk endpoint"
    } else {
        Write-Fail 'Endpoint(s) zonder RequireAdmin:'
        $missing | ForEach-Object { Write-Info $_ }
    }
}

# ── Admin user assignment ─────────────────────────────────────────────────────
Write-Section "Admin-user assignment ($AdminUserPrincipalName)"

$adminUser = az ad user show --id $AdminUserPrincipalName 2>$null | ConvertFrom-Json
if (-not $adminUser) {
    Write-Fail "User $AdminUserPrincipalName niet gevonden in tenant"
} else {
    Write-Pass "User gevonden: $($adminUser.displayName) (id $($adminUser.id))"

    $assignments = az rest --method GET `
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignedTo" 2>$null `
        | ConvertFrom-Json

    $myAssignment = $assignments.value | Where-Object { $_.principalId -eq $adminUser.id }
    if ($myAssignment) {
        $roleId = $myAssignment.appRoleId
        $matched = $app.appRoles | Where-Object { $_.id -eq $roleId }
        if ($matched.value -eq 'admin') {
            Write-Pass "$AdminUserPrincipalName heeft 'admin' role assignment"
        } else {
            Write-Warn "$AdminUserPrincipalName heeft assignment, maar role-value = '$($matched.value)' (verwacht 'admin')"
        }
    } else {
        Write-Fail "$AdminUserPrincipalName heeft GEEN role-assignment"
        Write-Info 'Fix: .\scripts\Configure-EntraApp.ps1 (maakt assignment aan met admin-role)'
    }
}

Write-Host ''
Write-Host '─────────────────────────────────────────────────────────────────' -ForegroundColor DarkGray
if ($script:Failures -gt 0) {
    Write-Host ''
    Write-Host "  ❌ $script:Failures probleem(en) gevonden." -ForegroundColor Red
    Write-Host ''
    Write-Host '  ⚠ Dit Verify-script wijzigt NIETS. Configure-EntraApp.ps1 is de fix.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  Volgende stap — run ONDERSTAAND commando (zonder -WhatIf = apply):' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '      .\scripts\Configure-EntraApp.ps1' -ForegroundColor White -BackgroundColor DarkBlue
    Write-Host ''
    Write-Host '  Daarna opnieuw deze Verify draaien om te bevestigen dat alle regels groen zijn.' -ForegroundColor DarkGray
    Write-Host '  Tot slot: sluit alle browser-tabs, verse Incognito, opnieuw inloggen.' -ForegroundColor DarkGray
} else {
    Write-Host ''
    Write-Host '  ✓ Alle 5 lagen + admin-assignment correct. Geen actie nodig.' -ForegroundColor Green
    Write-Host '  Als login alsnog faalt: verse Incognito sessie (MSAL bewaart oude tokens in localStorage).' -ForegroundColor DarkGray
}
Write-Host ''
