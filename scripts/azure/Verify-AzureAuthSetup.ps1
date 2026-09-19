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
    Zie SETUP-NIEUWE-CLUB.md en docs/ENTRA-AUTH-BEHEER.md voor het volledige protocol.
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
    Write-Info 'Switch met: az account set --subscription <sub-id-van-de-juiste-tenant>'
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

# Dit script staat in scripts/azure/, dus de repo-root ligt twee niveaus hoger.
# Stond hier '..', waardoor het pad scripts/BlazorAdmin/App.razor werd — dat bestaat niet,
# -Resolve gaf $null en Layer 4 rapporteerde daardoor altijd FAIL (#800).
$appRazor = Join-Path $PSScriptRoot '../../BlazorAdmin/App.razor' -Resolve -ErrorAction SilentlyContinue
if ($appRazor -and (Get-Content $appRazor -Raw) -match 'IsInRole\("admin"\)') {
    Write-Pass "App.razor bevat IsInRole-check (Layer 4 actief in code)"
} else {
    Write-Fail "App.razor bevat GEEN IsInRole-check — Layer 4 in code ontbreekt"
}

# ── Layer 5 — Backend RequireAdmin op alle protected endpoints ────────────────
Write-Section 'Layer 5 — Backend RequireAdmin (EasyAuthHelper) — code-side'

# #1276 — drie dingen gingen hier mis, en ze faalden alle drie de verkeerde kant op:
#
#   1. Er werd één tier gescand (FunctionApp/Admin). Sinds #1266 zijn beide tiers gelijkwaardig
#      (built=true in scripts/ci/database-tiers.json), dus juist de tier die déze installatie
#      draait kon ongecontroleerd blijven. De tierlijst komt nu uit dat bestand — nooit een
#      tweede hardcoded lijst, zie de tier-regels in CLAUDE.md.
#   2. -ErrorAction SilentlyContinue maakte $adminFns leeg als het pad niet oploste, en er was
#      geen else-tak. Het script zei dan NIETS over laag 5, wat leest als "in orde". Een
#      onoplosbaar pad is nu een Write-Fail.
#   3. De telling [Function] > RequireAdmin gaf 15 van de 24 bestanden vals als onbeschermd op.
#      Die endpoints lopen via AdminEndpoint.ExecuteAsync, dat de rolcontrole centraal doet —
#      de tekst "EasyAuthHelper.RequireAdmin" staat dan niet in het bestand zelf. Een controle
#      die op 15 plekken ten onrechte rood staat, wordt niet gelezen, en dan bewaakt hij niets.
#
# De controle redeneert nu per endpoint in plaats van per bestand te tellen: knip de inhoud bij
# elke [Function("...")], houd de stukken met een HttpTrigger over (een TimerTrigger heeft geen
# aanroeper met een rol) en eis dat zo'n stuk langs een van de bekende poorten gaat.
$guardPatronen = @(
    'EasyAuthHelper\.RequireAdmin',                 # rechtstreeks in het endpoint
    'AdminEndpoint\.ExecuteAsync',                  # centrale poort: doet RequireAdmin
    'SportlinkEndpointSupport\.Execute'             # Wedstrijdzaken-rol + AdminEndpoint erachter
)

$tierBestand = Join-Path $PSScriptRoot '../ci/database-tiers.json'
if (-not (Test-Path $tierBestand)) {
    Write-Fail "Tierlijst niet gevonden op $tierBestand — laag 5 is NIET gecontroleerd"
} else {
    $tiers = (Get-Content $tierBestand -Raw | ConvertFrom-Json).tiers | Where-Object { $_.built }
    if (-not $tiers) {
        Write-Fail "Geen enkele tier met built=true in $tierBestand — laag 5 is NIET gecontroleerd"
    }
    foreach ($tier in $tiers) {
        # csproj -> projectmap -> Admin-map. Zo blijft database-tiers.json de enige vertaaltabel.
        $projectMap = Split-Path $tier.csproj -Parent
        $adminFns   = Join-Path $PSScriptRoot "../../$projectMap/Admin"

        if (-not (Test-Path $adminFns)) {
            Write-Fail "$($tier.name): Admin-map niet gevonden op $adminFns — laag 5 is voor deze tier NIET gecontroleerd"
            continue
        }

        $files = Get-ChildItem -Path $adminFns -Filter '*.cs' | Where-Object { $_.Name -ne 'EasyAuthHelper.cs' }
        if ($files.Count -eq 0) {
            Write-Fail "$($tier.name): geen .cs-bestanden in $adminFns — laag 5 is voor deze tier NIET gecontroleerd"
            continue
        }

        $missing   = @()
        $gecontroleerd = 0
        foreach ($f in $files) {
            $content = Get-Content $f.FullName -Raw
            # Knip bij elke [Function("...")]; stuk 0 is alles ervóór (usings, class-declaratie).
            $stukken = [regex]::Split($content, '(?=\[Function\("[^"]+"\)\])')
            foreach ($stuk in $stukken) {
                if ($stuk -notmatch '\[Function\("([^"]+)"\)\]') { continue }
                $naam = $Matches[1]
                if ($stuk -notmatch 'HttpTrigger') { continue }   # timer: geen aanroeper met een rol
                $gecontroleerd++
                $bewaakt = $false
                foreach ($patroon in $guardPatronen) {
                    if ($stuk -match $patroon) { $bewaakt = $true; break }
                }
                if (-not $bewaakt) { $missing += "$($f.Name) → $naam" }
            }
        }

        if ($gecontroleerd -eq 0) {
            Write-Fail "$($tier.name): geen enkel HTTP-endpoint aangetroffen in $adminFns — dat kan niet kloppen"
        } elseif ($missing.Count -eq 0) {
            Write-Pass "$($tier.name): alle $gecontroleerd HTTP-endpoints in Admin/ gaan langs een rolcontrole"
        } else {
            Write-Fail "$($tier.name): $($missing.Count) van $gecontroleerd HTTP-endpoint(s) zonder rolcontrole:"
            $missing | ForEach-Object { Write-Info $_ }
        }
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
