#Requires -Version 7
<#
.SYNOPSIS
    Smoke test: verifieer dat FunctionApp en BlazorAdmin bouwen en opstarten.

.DESCRIPTION
    Voert de minimale verificatie uit die bij elke oplevering verplicht is:
      1. dotnet build FunctionApp
      2. dotnet build BlazorAdmin
      3. func start op poort 7094 → health endpoint aanroepen
      4. Alle beheer-endpoint groepen aanroepen (settings, sync, templates, voorkeurstijden, email-log)
      5. dotnet run BlazorAdmin → index.html ophalen

    Exitcode 0 = alles groen. Exitcode 1 = minimaal één check gefaald.

.PARAMETER SkipBlazor
    Sla de Blazor startup check over (sneller, alleen FunctionApp).

.PARAMETER FuncPort
    Poort voor de FunctionApp. Default: 7094.

.EXAMPLE
    .\scripts\smoke-test.ps1
    .\scripts\smoke-test.ps1 -SkipBlazor
#>
param(
    [switch]$SkipBlazor,
    [int]$FuncPort = 7094
)

$ErrorActionPreference = 'Continue'
$Root = Split-Path $PSScriptRoot -Parent

# ── Helpers ─────────────────────────────────────────────────────────────────

$Pass = 0; $Fail = 0
function Check($label, $ok, $detail = "") {
    if ($ok) {
        Write-Host "  [PASS] $label" -ForegroundColor Green
        $script:Pass++
    } else {
        Write-Host "  [FAIL] $label$(if ($detail) { ": $detail" })" -ForegroundColor Red
        $script:Fail++
    }
}

function WaitForUrl($url, $timeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
            if ($r.StatusCode -lt 500) { return $r }
        } catch { }
        Start-Sleep 2
    }
    return $null
}

# ── 1. Build FunctionApp ─────────────────────────────────────────────────────

Write-Host "`n── Stap 1: Build FunctionApp ──" -ForegroundColor Cyan
$buildFunc = dotnet build "$Root\FunctionApp\fa-dev-sportlink-01.csproj" -c Debug --nologo 2>&1
$funcBuildOk = ($LASTEXITCODE -eq 0) -and ($buildFunc -notmatch " Error\(s\)")
Check "dotnet build FunctionApp" $funcBuildOk ($buildFunc | Where-Object { $_ -match "error" } | Select-Object -First 3 | Out-String)

# ── 2. Build BlazorAdmin ─────────────────────────────────────────────────────

Write-Host "`n── Stap 2: Build BlazorAdmin ──" -ForegroundColor Cyan
$buildBlazor = dotnet build "$Root\BlazorAdmin\BlazorAdmin.csproj" -c Debug --nologo 2>&1
$blazorBuildOk = ($LASTEXITCODE -eq 0) -and ($buildBlazor -notmatch " Error\(s\)")
Check "dotnet build BlazorAdmin" $blazorBuildOk ($buildBlazor | Where-Object { $_ -match "error" } | Select-Object -First 3 | Out-String)

if (-not $funcBuildOk) {
    Write-Host "`n[AFGEBROKEN] FunctionApp bouwt niet — runtime tests overgeslagen." -ForegroundColor Yellow
    exit 1
}

# ── 3. FunctionApp starten ────────────────────────────────────────────────────

Write-Host "`n── Stap 3: FunctionApp starten op poort $FuncPort ──" -ForegroundColor Cyan

# Stop eventueel lopende instantie op deze poort
Get-Process -Name "func","dotnet" -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -eq "" } |
    ForEach-Object { $_.Kill() }
Start-Sleep 1

$funcLog = "$env:TEMP\smoke-func.log"
# func is een .ps1 script (npm global) — Start-Process vereist pwsh als host
$funcProc = Start-Process -FilePath "pwsh" `
    -ArgumentList "-NoProfile","-NonInteractive","-Command","func start --port $FuncPort" `
    -WorkingDirectory "$Root\FunctionApp" `
    -RedirectStandardOutput $funcLog `
    -RedirectStandardError "$env:TEMP\smoke-func-err.log" `
    -PassThru -NoNewWindow

# Wacht tot "Worker process started"
$started = $false
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
    Start-Sleep 2
    if (Test-Path $funcLog) {
        $content = Get-Content $funcLog -Raw -ErrorAction SilentlyContinue
        if ($content -match "Worker process started and initialized") { $started = $true; break }
        if ($content -match "A host error has occurred") { break }
    }
}
Check "func start — worker initialized" $started

# Wacht tot HTTP listener actief is (na "worker initialized" kan de HTTP poort nog even op zich laten wachten)
if ($started) {
    $healthUrl = "http://localhost:$FuncPort/api/health"
    $r = WaitForUrl $healthUrl 30
    if (-not $r) {
        # health endpoint bestaat misschien niet; probeer dan een willekeurig endpoint
        $r = WaitForUrl "http://localhost:$FuncPort/api/beheer/settings" 15
    }
    if (-not $r) {
        Write-Host "  [WARN] HTTP luistert nog niet na 45s — endpoint checks kunnen mislukken" -ForegroundColor Yellow
    }
}

# ── 4. FunctionApp endpoint checks ──────────────────────────────────────────

Write-Host "`n── Stap 4: Endpoint checks ──" -ForegroundColor Cyan

$base = "http://localhost:$FuncPort/api"

function CallEndpoint($label, $url) {
    try {
        $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        Check $label ($r.StatusCode -lt 500) "HTTP $($r.StatusCode)"
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        # 401 = function key vereist maar endpoint bestaat wel → PASS
        if ($status -eq 401) { Check $label $true "HTTP 401 (endpoint bestaat, key vereist)" }
        else { Check $label $false $_.Exception.Message }
    }
}

# Azure Functions geeft 404 op GET voor POST-only routes → POST verzenden
function CallPostEndpoint($label, $url, $body = "{}") {
    try {
        $r = Invoke-WebRequest -Uri $url -Method Post -Body $body -ContentType "application/json" `
            -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        Check $label ($r.StatusCode -lt 500) "HTTP $($r.StatusCode)"
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -eq 401) { Check $label $true "HTTP 401 (endpoint bestaat, key vereist)" }
        elseif ($status -ge 400 -and $status -lt 500) {
            # 400/422/429 = endpoint bestaat maar input ongeldig of rate-limit → PASS
            Check $label $true "HTTP $status (endpoint bestaat)"
        }
        else { Check $label $false $_.Exception.Message }
    }
}

CallEndpoint "GET /api/health"                      "$base/health"
CallEndpoint "GET /api/beheer/settings"             "$base/beheer/settings"
CallEndpoint "GET /api/beheer/sync/status"          "$base/beheer/sync/status"
CallEndpoint "GET /api/beheer/templates"            "$base/beheer/templates"
CallEndpoint "GET /api/beheer/voorkeurstijden"      "$base/beheer/voorkeurstijden"
CallEndpoint "GET /api/beheer/email-log"            "$base/beheer/email-log"
CallPostEndpoint "POST /api/test/email (rate-check)" "$base/test/email" '{"onderwerp":"smoke","afzender":"test@test.nl","body":"test"}'

# Check of admin-routes NIET in error staan (geen route-conflict meldingen)
if (Test-Path $funcLog) {
    $logContent = Get-Content $funcLog -Raw -ErrorAction SilentlyContinue
    $routeConflicts = ($logContent | Select-String "conflicts with one or more built in routes").Count
    Check "Geen route-conflicten in func log" ($routeConflicts -eq 0) "$routeConflicts conflict(en) gevonden"

    $targetFrameworkOk = $logContent -notmatch "app-launch-failed"
    Check "Geen .NET runtime mismatch" $targetFrameworkOk
}

# ── 5. BlazorAdmin starten ────────────────────────────────────────────────────

if (-not $SkipBlazor -and $blazorBuildOk) {
    Write-Host "`n── Stap 5: BlazorAdmin starten ──" -ForegroundColor Cyan

    $blazorLog = "$env:TEMP\smoke-blazor.log"
    $blazorProc = Start-Process -FilePath "dotnet" `
        -ArgumentList "run","--no-build" `
        -WorkingDirectory "$Root\BlazorAdmin" `
        -RedirectStandardOutput $blazorLog `
        -RedirectStandardError "$env:TEMP\smoke-blazor-err.log" `
        -PassThru -NoNewWindow

    # Bepaal de Blazor URL uit de log
    $blazorUrl = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep 2
        if (Test-Path $blazorLog) {
            $content = Get-Content $blazorLog -Raw -ErrorAction SilentlyContinue
            if ($content -match "Now listening on: (http://\S+)") {
                $blazorUrl = $Matches[1]
                break
            }
        }
    }

    if ($blazorUrl) {
        Check "BlazorAdmin started — listening on $blazorUrl" $true
        try {
            $r = Invoke-WebRequest -Uri $blazorUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
            $hasDoctype = $r.Content -match "<!DOCTYPE html"
            Check "GET $blazorUrl → HTML response" $hasDoctype "Content: $($r.Content.Substring(0, [Math]::Min(100,$r.Content.Length)))"
        } catch {
            Check "GET $blazorUrl → HTML response" $false $_.Exception.Message
        }
    } else {
        Check "BlazorAdmin started" $false "Timeout — geen 'Now listening on' in log"
        $blazorProc = $null
    }
} elseif ($SkipBlazor) {
    Write-Host "`n── Stap 5: BlazorAdmin overgeslagen (-SkipBlazor) ──" -ForegroundColor DarkGray
}

# ── Opruimen ──────────────────────────────────────────────────────────────────

Write-Host "`n── Opruimen ──" -ForegroundColor Cyan
if ($funcProc -and -not $funcProc.HasExited) {
    $funcProc.Kill()
    Write-Host "  FunctionApp gestopt (PID $($funcProc.Id))" -ForegroundColor DarkGray
}
if ((Get-Variable blazorProc -ErrorAction SilentlyContinue) -and $blazorProc -and -not $blazorProc.HasExited) {
    $blazorProc.Kill()
    Write-Host "  BlazorAdmin gestopt (PID $($blazorProc.Id))" -ForegroundColor DarkGray
}

# ── Samenvatting ──────────────────────────────────────────────────────────────

Write-Host "`n════════════════════════════════════" -ForegroundColor White
$total = $Pass + $Fail
if ($Fail -eq 0) {
    Write-Host "  SMOKE TEST GESLAAGD  $Pass/$total checks groen" -ForegroundColor Green
} else {
    Write-Host "  SMOKE TEST GEFAALD   $Pass/$total groen, $Fail rood" -ForegroundColor Red
}
Write-Host "════════════════════════════════════`n" -ForegroundColor White

exit ($Fail -gt 0 ? 1 : 0)
