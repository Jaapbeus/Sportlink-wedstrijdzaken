#requires -Version 7.0
<#
.SYNOPSIS
    Voegt een eigen (sub)domein toe aan de Admin GUI: Static Web App, Function App CORS
    en de Entra SPA redirect-URI in één idempotente run.

.DESCRIPTION
    Een CNAME-record aanmaken is NIET genoeg. Er zijn drie wijzigingen nodig, en twee
    daarvan geven een verwarrend foutbeeld als je ze vergeet:

      1. Domein registreren in de Static Web App
         Zonder dit: certificaatfout, Azure serveert het domein niet.

      2. CORS-origin toevoegen op de Function App
         Zonder dit: de UI laadt wél, maar de browser blokkeert élke API-call.
         Je krijgt een schijnbaar werkende site met overal lege schermen.

      3. SPA redirect-URI toevoegen in de Entra App Registration
         Zonder dit: login mislukt met AADSTS50011 (redirect URI mismatch).

    Stap 3 is met de hand riskant: de lijst met redirect-URI's moet worden UITGEBREID,
    niet overschreven. Eén verkeerde 'az ad app update --set' wist de bestaande URI en
    dan kan niemand meer inloggen — ook niet op de oude URL. Dit script leest de
    bestaande lijst, voegt toe en schrijft terug.

    Idempotent: op een al-correcte configuratie doet het niets en print het per stap
    '(al correct)'. Veilig om herhaald te runnen.

    Kosten: geen. De Static Web Apps Free tier ondersteunt 2 custom domains per app,
    inclusief gratis automatisch vernieuwende SSL-certificaten. Geen tier-upgrade nodig.
    Zie https://learn.microsoft.com/azure/static-web-apps/plans

    Volledige handleiding, inclusief het DNS-record en de verificatie na afloop:
    docs/CUSTOM-DOMAIN.md

.PARAMETER Domain
    Het volledige domein dat je wilt toevoegen, bijvoorbeeld 'wz.jouwclub.nl'.
    Apex-domeinen (zonder subdomein) vragen een andere DNS-opzet — zie de handleiding.

.PARAMETER StaticWebAppName
    Naam van de Static Web App-resource.

.PARAMETER StaticWebAppResourceGroup
    Resourcegroup van de Static Web App.

.PARAMETER FunctionAppName
    Naam van de Function App die de API host.

.PARAMETER FunctionAppResourceGroup
    Resourcegroup van de Function App. Kan afwijken van die van de Static Web App.

.PARAMETER ClientId
    Application (client) ID van de Entra App Registration van de Admin GUI.

.PARAMETER ExpectedTenantId
    Tenant ID van de Entra tenant. Het script stopt als 'az login' op een andere
    tenant staat (faalt-snel-principe).

.PARAMETER WhatIf
    Print alleen welke wijzigingen zouden gebeuren, doet niets.

.EXAMPLE
    .\scripts\azure\Add-CustomDomain.ps1 `
        -Domain 'wz.jouwclub.nl' `
        -StaticWebAppName '<swa-naam>' -StaticWebAppResourceGroup '<swa-rg>' `
        -FunctionAppName '<func-naam>' -FunctionAppResourceGroup '<func-rg>' `
        -ClientId '<app-id>' -ExpectedTenantId '<tenant-id>' -WhatIf

.NOTES
    Vereist 'az login' op het juiste account. Voer eerst een dry-run met -WhatIf uit.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true, HelpMessage = 'Volledig domein, bijv. wz.jouwclub.nl')]
    [string] $Domain,

    [Parameter(Mandatory = $true, HelpMessage = 'Naam van de Static Web App-resource')]
    [string] $StaticWebAppName,

    [Parameter(Mandatory = $true, HelpMessage = 'Resourcegroup van de Static Web App')]
    [string] $StaticWebAppResourceGroup,

    [Parameter(Mandatory = $true, HelpMessage = 'Naam van de Function App die de API host')]
    [string] $FunctionAppName,

    [Parameter(Mandatory = $true, HelpMessage = 'Resourcegroup van de Function App')]
    [string] $FunctionAppResourceGroup,

    [Parameter(Mandatory = $true, HelpMessage = 'Application (client) ID van de Entra App Registration')]
    [string] $ClientId,

    [Parameter(Mandatory = $true, HelpMessage = 'Tenant ID uit Microsoft Entra ID › Overview')]
    [string] $ExpectedTenantId
)

$ErrorActionPreference = 'Stop'

function Write-Section($t) { Write-Host ''; Write-Host "═══ $t ═══" -ForegroundColor Cyan }
function Write-Step($t)    { Write-Host "  • $t" -ForegroundColor Yellow }
function Write-Done($t)    { Write-Host "  ✓ $t" -ForegroundColor Green }
function Write-Skip($t)    { Write-Host "  → $t (al correct)" -ForegroundColor DarkGreen }
function Write-WouldDo($t) { Write-Host "  ⊘ $t (WhatIf — niet uitgevoerd)" -ForegroundColor Magenta }
function Write-Warn($t)    { Write-Host "  ⚠ $t" -ForegroundColor Yellow }
function Write-Fail($t)    { Write-Host "  ✗ $t" -ForegroundColor Red }

# ── Banner: dit script WIJZIGT Azure ──────────────────────────────────────────
Write-Host ''
if ($WhatIfPreference) {
    Write-Host '┌─────────────────────────────────────────────────────────────────┐' -ForegroundColor Magenta
    Write-Host '│  Add-CustomDomain.ps1 — DRY-RUN (-WhatIf)                       │' -ForegroundColor Magenta
    Write-Host '│  Toont wat zou gebeuren — er worden GEEN wijzigingen toegepast. │' -ForegroundColor Magenta
    Write-Host '└─────────────────────────────────────────────────────────────────┘' -ForegroundColor Magenta
} else {
    Write-Host '┌─────────────────────────────────────────────────────────────────┐' -ForegroundColor Yellow
    Write-Host '│  Add-CustomDomain.ps1 — APPLY MODE                              │' -ForegroundColor Yellow
    Write-Host '│  Past SWA-domein, Function App CORS en Entra-redirect aan.      │' -ForegroundColor Yellow
    Write-Host '│  Idempotent. Voor een dry-run: gebruik -WhatIf.                 │' -ForegroundColor Yellow
    Write-Host '└─────────────────────────────────────────────────────────────────┘' -ForegroundColor Yellow
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
Write-Section 'Pre-flight'

if ($Domain -notmatch '^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)+$') {
    Write-Fail "Domein '$Domain' lijkt geen geldige hostnaam. Gebruik kleine letters, bijv. wz.jouwclub.nl"
    exit 1
}
# Punycode/unicode-domeinen worden door Static Web Apps niet ondersteund.
if ($Domain -like 'xn--*' -or $Domain -like '*.xn--*') {
    Write-Fail 'Unicode/punycode-domeinen worden niet ondersteund door Static Web Apps.'
    exit 1
}
$labelCount = ($Domain -split '\.').Count
if ($labelCount -lt 3) {
    Write-Warn "'$Domain' lijkt een apex-domein (geen subdomein)."
    Write-Warn 'Een apex-domein vraagt een andere DNS-opzet (ALIAS/A + TXT) — zie docs/CUSTOM-DOMAIN.md.'
}

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Fail 'Niet ingelogd bij Azure CLI. Voer eerst `az login` uit.'
    exit 1
}
if ($account.tenantId -ne $ExpectedTenantId) {
    Write-Fail "Verkeerde tenant: $($account.tenantId) (verwacht $ExpectedTenantId)"
    exit 1
}
Write-Done "Ingelogd als: $($account.user.name)"

$swa = az staticwebapp show --name $StaticWebAppName --resource-group $StaticWebAppResourceGroup 2>$null | ConvertFrom-Json
if (-not $swa) {
    Write-Fail "Static Web App '$StaticWebAppName' niet gevonden in resourcegroup '$StaticWebAppResourceGroup'."
    exit 2
}
$swaHost = $swa.defaultHostname
Write-Done "Static Web App gevonden (SKU: $($swa.sku.name))"

$func = az functionapp show --name $FunctionAppName --resource-group $FunctionAppResourceGroup 2>$null | ConvertFrom-Json
if (-not $func) {
    Write-Fail "Function App '$FunctionAppName' niet gevonden in resourcegroup '$FunctionAppResourceGroup'."
    exit 2
}
Write-Done 'Function App gevonden'

$app = az ad app show --id $ClientId 2>$null | ConvertFrom-Json
if (-not $app) {
    Write-Fail "Entra App Registration met ClientId '$ClientId' niet gevonden."
    exit 2
}
Write-Done 'Entra App Registration gevonden'

# Free tier staat 2 custom domains per app toe. Waarschuw vóór we tegen de limiet lopen.
$existingHosts = az staticwebapp hostname list --name $StaticWebAppName --resource-group $StaticWebAppResourceGroup 2>$null | ConvertFrom-Json
$customHostCount = @($existingHosts | Where-Object { $_.name -ne $swaHost }).Count
if ($swa.sku.name -eq 'Free' -and $customHostCount -ge 2 -and -not ($existingHosts.name -contains $Domain)) {
    Write-Fail "Free tier staat 2 custom domains per app toe; er zijn er al $customHostCount geconfigureerd."
    Write-Fail 'Verwijder eerst een bestaand domein, of stap over op de Standard tier (niet gratis).'
    exit 3
}

# ── DNS-controle ─────────────────────────────────────────────────────────────
# Static Web Apps valideert het domein pas als het CNAME-record wereldwijd resolvet.
# Dit is een waarschuwing, geen blokkade: propagatie kan tot enkele uren duren.
Write-Section 'DNS-controle'
Write-Step "Verwacht CNAME: $Domain → $swaHost"

$dnsOk = $false
try {
    $resolved = Resolve-DnsName -Name $Domain -Type CNAME -ErrorAction Stop 2>$null
    $target = ($resolved | Where-Object { $_.NameHost } | Select-Object -First 1).NameHost
    if ($target -and $target.TrimEnd('.') -ieq $swaHost.TrimEnd('.')) {
        Write-Done "CNAME resolvet correct naar $target"
        $dnsOk = $true
    } elseif ($target) {
        Write-Warn "CNAME wijst naar '$target' in plaats van '$swaHost'."
        Write-Warn 'Static Web Apps kan het domein dan niet valideren. Corrigeer het record.'
    } else {
        Write-Warn 'Geen CNAME-record gevonden.'
    }
} catch {
    Write-Warn "CNAME nog niet vindbaar voor '$Domain'."
}
if (-not $dnsOk) {
    Write-Warn 'Maak eerst het CNAME-record aan; DNS-propagatie kan tot enkele uren duren.'
    Write-Warn 'Het script gaat door — de SWA-validatie hieronder faalt dan mogelijk nog.'
}

# ── Stap 1: domein registreren in de Static Web App ──────────────────────────
Write-Section 'Stap 1 — Static Web App: custom domain'

if ($existingHosts.name -contains $Domain) {
    $state = ($existingHosts | Where-Object { $_.name -eq $Domain }).status
    Write-Skip "Domein al geregistreerd (status: $state)"
} else {
    if ($PSCmdlet.ShouldProcess($Domain, 'Custom domain toevoegen aan Static Web App')) {
        Write-Step "Registreren van $Domain ..."
        az staticwebapp hostname set `
            --name $StaticWebAppName `
            --resource-group $StaticWebAppResourceGroup `
            --hostname $Domain `
            --output none
        Write-Done 'Domein geregistreerd — Azure valideert nu het DNS-record'
        Write-Warn 'Validatie en certificaatuitgifte kunnen enkele minuten tot uren duren.'
    } else {
        Write-WouldDo "Custom domain $Domain toevoegen aan de Static Web App"
    }
}

# ── Stap 2: CORS-origin op de Function App ───────────────────────────────────
Write-Section 'Stap 2 — Function App: CORS-origin'

$newOrigin = "https://$Domain"
$cors = az functionapp cors show --name $FunctionAppName --resource-group $FunctionAppResourceGroup 2>$null | ConvertFrom-Json
$currentOrigins = @($cors.allowedOrigins)

if ($currentOrigins -contains $newOrigin) {
    Write-Skip "CORS-origin $newOrigin staat er al"
} else {
    if ($PSCmdlet.ShouldProcess($newOrigin, 'CORS-origin toevoegen aan Function App')) {
        Write-Step "Toevoegen van $newOrigin ..."
        # 'cors add' voegt toe aan de bestaande lijst; bestaande origins blijven staan.
        az functionapp cors add `
            --name $FunctionAppName `
            --resource-group $FunctionAppResourceGroup `
            --allowed-origins $newOrigin `
            --output none
        Write-Done 'CORS-origin toegevoegd'
    } else {
        Write-WouldDo "CORS-origin $newOrigin toevoegen (bestaande origins blijven staan)"
    }
}

# ── Stap 3: Entra SPA redirect-URI ───────────────────────────────────────────
Write-Section 'Stap 3 — Entra App Registration: SPA redirect-URI'

$newRedirect = "https://$Domain/authentication/login-callback"
$currentRedirects = @($app.spa.redirectUris)

if ($currentRedirects -contains $newRedirect) {
    Write-Skip "Redirect-URI staat er al"
} else {
    if ($PSCmdlet.ShouldProcess($newRedirect, 'SPA redirect-URI toevoegen aan Entra App Registration')) {
        Write-Step "Toevoegen van $newRedirect ..."
        # KRITIEK: uitbreiden, niet overschrijven. Zou de bestaande URI verdwijnen, dan
        # kan niemand meer inloggen — ook niet op de oude URL.
        $merged = @($currentRedirects) + $newRedirect | Select-Object -Unique
        $payload = @{ spa = @{ redirectUris = @($merged) } } | ConvertTo-Json -Depth 6 -Compress
        $tmp = [System.IO.Path]::GetTempFileName()
        try {
            [System.IO.File]::WriteAllText($tmp, $payload, (New-Object System.Text.UTF8Encoding($false)))
            az rest --method PATCH `
                --uri "https://graph.microsoft.com/v1.0/applications/$($app.id)" `
                --headers 'Content-Type=application/json' `
                --body "@$tmp" `
                --output none
        } finally {
            Remove-Item $tmp -ErrorAction SilentlyContinue
        }

        # Terugcontrole: de bestaande URI's moeten er nog staan.
        $after = @((az ad app show --id $ClientId | ConvertFrom-Json).spa.redirectUris)
        $lost = @($currentRedirects | Where-Object { $after -notcontains $_ })
        if ($lost.Count -gt 0) {
            Write-Fail "Bestaande redirect-URI(s) verdwenen: $($lost -join ', ')"
            Write-Fail 'Herstel dit direct in de Portal, anders kan niemand meer inloggen.'
            exit 4
        }
        Write-Done "Redirect-URI toegevoegd ($($after.Count) in totaal, bestaande behouden)"
    } else {
        Write-WouldDo "Redirect-URI $newRedirect toevoegen (bestaande $($currentRedirects.Count) blijven staan)"
    }
}

# ── Samenvatting ─────────────────────────────────────────────────────────────
Write-Section 'Samenvatting'
if ($WhatIfPreference) {
    Write-Host ''
    Write-Host '  DRY-RUN — er is niets gewijzigd. Run zonder -WhatIf om toe te passen.' -ForegroundColor Magenta
    Write-Host ''
    exit 0
}

Write-Host ''
Write-Host '  Verifieer daarna handmatig:' -ForegroundColor Cyan
Write-Host "    1. https://$Domain opent met een geldig certificaat (geen waarschuwing)" -ForegroundColor Gray
Write-Host '    2. Login via Microsoft werkt — geen AADSTS50011' -ForegroundColor Gray
Write-Host '    3. Een pagina met data laadt daadwerkelijk gegevens (bewijst dat CORS klopt)' -ForegroundColor Gray
Write-Host '    4. De oude .azurestaticapps.net-URL werkt nog steeds' -ForegroundColor Gray
Write-Host ''
Write-Host '  Doe stap 2 in een verse incognito-sessie: MSAL bewaart het ID-token in' -ForegroundColor Gray
Write-Host '  localStorage, dus zonder verse sessie test je de oude token.' -ForegroundColor Gray
Write-Host ''
if (-not $dnsOk) {
    Write-Warn 'Het CNAME-record resolveerde nog niet. Controleer de domeinstatus later met:'
    Write-Host "    az staticwebapp hostname list --name $StaticWebAppName --resource-group $StaticWebAppResourceGroup -o table" -ForegroundColor Gray
    Write-Host ''
}
exit 0
