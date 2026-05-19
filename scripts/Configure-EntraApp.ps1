#requires -Version 7.0
<#
.SYNOPSIS
    Idempotent configuratie van de Entra ID App Registration + Enterprise App
    voor de Sportlink Admin GUI (Defense in depth lagen 1-3).

.DESCRIPTION
    Past de volgende configuraties toe als ze nog niet correct staan:
      1. App Roles 'admin' en 'user' aanmaken (Layer 3a)
      2. optionalClaims.idToken en optionalClaims.accessToken met 'roles' (Layer 3b)
      3. appRoleAssignmentRequired = true op Service Principal (Layer 2)
      4. AdminUserPrincipalName toewijzen met 'admin' role als die ontbreekt

    Script is idempotent: op een al-correcte configuratie doet het niets en
    print het '✓ already configured' per stap. Veilig om herhaald te runnen.

    Verplichte pre-conditie: 'az login' op het juiste account in de [club-domein]
    tenant. Wordt aan het begin gecontroleerd; als de tenant niet matcht stopt
    het script direct (faalt-snel-principe).

.PARAMETER WhatIf
    Print alleen welke wijzigingen zouden gebeuren, doet niets.

.EXAMPLE
    .\scripts\Configure-EntraApp.ps1            # apply changes
    .\scripts\Configure-EntraApp.ps1 -WhatIf    # dry-run

.NOTES
    Doelapp: Sportlink Admin GUI (clientId [CLIENT_ID]).
    Zie docs/AZURE-ENTRA-SETUP.md voor het volledige protocol en handmatige
    Portal-stappen als alternatief.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ClientId = '[CLIENT_ID]',
    [string] $ExpectedTenantId = '[TENANT_ID]',
    [string] $AdminUserPrincipalName = 'jaapadmin@[club-domein]'
)

$ErrorActionPreference = 'Stop'

function Write-Section($t) { Write-Host ''; Write-Host "═══ $t ═══" -ForegroundColor Cyan }
function Write-Step($t)    { Write-Host "  • $t" -ForegroundColor Yellow }
function Write-Done($t)    { Write-Host "  ✓ $t" -ForegroundColor Green }
function Write-Skip($t)    { Write-Host "  → $t (al correct)" -ForegroundColor DarkGreen }
function Write-WouldDo($t) { Write-Host "  ⊘ $t (WhatIf — niet uitgevoerd)" -ForegroundColor Magenta }

# ── Pre-flight ────────────────────────────────────────────────────────────────
Write-Section 'Pre-flight'

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host '  ✗ Niet ingelogd bij Azure CLI. Voer eerst `az login` uit.' -ForegroundColor Red
    exit 1
}
if ($account.tenantId -ne $ExpectedTenantId) {
    Write-Host "  ✗ Verkeerde tenant: $($account.tenantId) (verwacht $ExpectedTenantId)" -ForegroundColor Red
    exit 1
}
Write-Done "Tenant: $($account.tenantDefaultDomain)"
Write-Done "Ingelogd als: $($account.user.name)"

# ── Load current state ───────────────────────────────────────────────────────
$app = az ad app show --id $ClientId | ConvertFrom-Json
$sp  = az ad sp list --filter "appId eq '$ClientId'" | ConvertFrom-Json | Select-Object -First 1
if (-not $app -or -not $sp) {
    Write-Host '  ✗ App Registration of Service Principal niet gevonden — handmatig aanmaken.' -ForegroundColor Red
    exit 2
}
$appObjectId = $app.id
$spObjectId  = $sp.id

# ── Step 1: App Roles ─────────────────────────────────────────────────────────
Write-Section 'Step 1 — App Roles (admin + user)'

$wantedRoles = @(
    @{
        id = 'a2a9a5d6-0000-4abc-8def-000000000001'
        value = 'admin'
        displayName = 'Admin'
        description = 'Beheerder met volledige toegang tot Admin GUI'
    },
    @{
        id = 'b3b0b6e7-0000-4def-9012-000000000002'
        value = 'user'
        displayName = 'User'
        description = 'Gebruiker met leestoegang tot Admin GUI'
    }
)

$currentRoles = @($app.appRoles)
$rolesChanged = $false
$mergedRoles = New-Object System.Collections.ArrayList
foreach ($r in $currentRoles) { [void]$mergedRoles.Add($r) }

foreach ($want in $wantedRoles) {
    $exists = $currentRoles | Where-Object { $_.value -eq $want.value }
    if ($exists -and $exists.isEnabled) {
        Write-Skip "App Role '$($want.value)' aanwezig"
    } else {
        Write-Step "App Role '$($want.value)' toevoegen"
        $newRole = [ordered]@{
            id = $want.id
            value = $want.value
            displayName = $want.displayName
            description = $want.description
            isEnabled = $true
            origin = 'Application'
            allowedMemberTypes = @('User')
        }
        if ($exists) {
            # vervang bestaande (disabled) role
            $idx = 0
            foreach ($r in $mergedRoles) {
                if ($r.value -eq $want.value) { $mergedRoles[$idx] = $newRole; break }
                $idx++
            }
        } else {
            [void]$mergedRoles.Add($newRole)
        }
        $rolesChanged = $true
    }
}

if ($rolesChanged) {
    if ($PSCmdlet.ShouldProcess('App Registration', 'Update appRoles')) {
        $payload = @{ appRoles = @($mergedRoles) } | ConvertTo-Json -Depth 8 -Compress
        $tmp = New-TemporaryFile
        $payload | Out-File -FilePath $tmp -Encoding utf8 -NoNewline
        az rest --method PATCH `
            --uri "https://graph.microsoft.com/v1.0/applications/$appObjectId" `
            --headers 'Content-Type=application/json' `
            --body "@$($tmp.FullName)" | Out-Null
        Remove-Item $tmp -ErrorAction SilentlyContinue
        Write-Done 'appRoles bijgewerkt'
    } else {
        Write-WouldDo 'appRoles zou bijgewerkt worden'
    }
}

# ── Step 2: Optional claims ──────────────────────────────────────────────────
Write-Section "Step 2 — Optional claims ('roles' in id/access token)"

$idHas = ($app.optionalClaims.idToken | Where-Object { $_.name -eq 'roles' })
$atHas = ($app.optionalClaims.accessToken | Where-Object { $_.name -eq 'roles' })

if ($idHas -and $atHas) {
    Write-Skip "optionalClaims.idToken en .accessToken bevatten beide 'roles'"
} else {
    Write-Step "optionalClaims uitbreiden met 'roles'"
    $newOptional = @{
        idToken = @(@{ name = 'roles'; essential = $false; additionalProperties = @() })
        accessToken = @(@{ name = 'roles'; essential = $false; additionalProperties = @() })
        saml2Token = @()
    }
    $existingIdToken = @($app.optionalClaims.idToken | Where-Object { $_.name -ne 'roles' })
    $existingAccessToken = @($app.optionalClaims.accessToken | Where-Object { $_.name -ne 'roles' })
    foreach ($c in $existingIdToken)    { $newOptional.idToken    += $c }
    foreach ($c in $existingAccessToken){ $newOptional.accessToken += $c }

    if ($PSCmdlet.ShouldProcess('App Registration', 'Update optionalClaims')) {
        $payload = @{ optionalClaims = $newOptional } | ConvertTo-Json -Depth 8 -Compress
        $tmp = New-TemporaryFile
        $payload | Out-File -FilePath $tmp -Encoding utf8 -NoNewline
        az rest --method PATCH `
            --uri "https://graph.microsoft.com/v1.0/applications/$appObjectId" `
            --headers 'Content-Type=application/json' `
            --body "@$($tmp.FullName)" | Out-Null
        Remove-Item $tmp -ErrorAction SilentlyContinue
        Write-Done "optionalClaims bijgewerkt"
    } else {
        Write-WouldDo "optionalClaims zou bijgewerkt worden"
    }
}

# ── Step 3: Assignment required ──────────────────────────────────────────────
Write-Section 'Step 3 — appRoleAssignmentRequired = true'

if ($sp.appRoleAssignmentRequired -eq $true) {
    Write-Skip 'appRoleAssignmentRequired = true'
} else {
    Write-Step 'appRoleAssignmentRequired = true zetten'
    if ($PSCmdlet.ShouldProcess('Service Principal', 'Set appRoleAssignmentRequired=true')) {
        az ad sp update --id $spObjectId --set 'appRoleAssignmentRequired=true' | Out-Null
        Write-Done 'appRoleAssignmentRequired = true'
    } else {
        Write-WouldDo 'appRoleAssignmentRequired zou op true gezet worden'
    }
}

# ── Step 4: Admin-user role assignment ──────────────────────────────────────
Write-Section "Step 4 — Admin role assignment voor $AdminUserPrincipalName"

$adminUser = az ad user show --id $AdminUserPrincipalName 2>$null | ConvertFrom-Json
if (-not $adminUser) {
    Write-Host "  ✗ User $AdminUserPrincipalName niet gevonden in tenant" -ForegroundColor Red
    exit 4
}

$appReread = az ad app show --id $ClientId | ConvertFrom-Json
$adminRole = $appReread.appRoles | Where-Object { $_.value -eq 'admin' } | Select-Object -First 1

$assignments = az rest --method GET `
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spObjectId/appRoleAssignedTo" `
    | ConvertFrom-Json

$existingAssignment = $assignments.value | Where-Object {
    $_.principalId -eq $adminUser.id -and $_.appRoleId -eq $adminRole.id
}

if ($existingAssignment) {
    Write-Skip "$AdminUserPrincipalName heeft al 'admin' role assignment"
} else {
    Write-Step "$AdminUserPrincipalName toewijzen met 'admin' role"
    if ($PSCmdlet.ShouldProcess($AdminUserPrincipalName, 'Assign admin role')) {
        $body = @{
            principalId = $adminUser.id
            resourceId  = $spObjectId
            appRoleId   = $adminRole.id
        } | ConvertTo-Json -Compress
        $tmp = New-TemporaryFile
        $body | Out-File -FilePath $tmp -Encoding utf8 -NoNewline
        az rest --method POST `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spObjectId/appRoleAssignedTo" `
            --headers 'Content-Type=application/json' `
            --body "@$($tmp.FullName)" | Out-Null
        Remove-Item $tmp -ErrorAction SilentlyContinue
        Write-Done "$AdminUserPrincipalName toegewezen aan 'admin' role"
    } else {
        Write-WouldDo "$AdminUserPrincipalName zou aan 'admin' role toegewezen worden"
    }
}

# ── Done ───────────────────────────────────────────────────────────────────────
Write-Section 'Klaar'
Write-Host ''
Write-Host '  NA DEZE WIJZIGINGEN — verplichte gebruikersactie:' -ForegroundColor Yellow
Write-Host '    1. Sluit alle bestaande browser-tabs van de Admin GUI.' -ForegroundColor Yellow
Write-Host '    2. Open een verse Incognito/InPrivate sessie.' -ForegroundColor Yellow
Write-Host '    3. Navigeer naar https://[swa-unique-id].7.azurestaticapps.net' -ForegroundColor Yellow
Write-Host '    4. Log opnieuw in met jaapadmin@[club-domein].' -ForegroundColor Yellow
Write-Host ''
Write-Host '  Reden: MSAL kan een oud ID-token in localStorage hebben (vóór deze wijziging).' -ForegroundColor DarkGray
Write-Host '  Verse sessie garandeert een nieuw token mét de roles claim.' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Run .\scripts\Verify-AzureAuthSetup.ps1 om de eindstaat te checken.' -ForegroundColor Cyan
