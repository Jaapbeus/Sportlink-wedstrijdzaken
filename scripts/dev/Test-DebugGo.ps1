# Test-DebugGo.ps1 (#1576)
# De GO/NO-GO-controle van de lokale debugomgeving. Pas als dit script "GO" meldt (exit 0) mag
# iemand — mens of agent — zeggen dat de debug werkt.
#
# Waarom dit bestaat: na een herstart meldde Start-Debug alles OK (health 200, juiste versie) terwijl
# de FunctionApp en Azurite kort daarna wegvielen en élk beheerscherm "Failed to fetch" gaf.
# Health 200 en HTTP 200 op index.html bewijzen niets over een Blazor WASM-app die client-side
# rendert en data bij de API ophaalt. Dit script test wat een beheerder ook ziet.
#
# Controles, in volgorde:
#   1. Services luisteren (Azurite, FunctionApp, BlazorAdmin).
#   2. Versie: /api/health.version = versie in het csproj van de draaiende worktree, en (tenzij
#      -HuidigeWerkmap) die worktree staat op de laatste origin/develop.
#   3. Health-status ok (alleen een verouderde sync is een waarschuwing), geen openstaande migraties.
#   4. Standaard sync en Sportlink-extensie: /api/beheer/sync/status en
#      /api/beheer/sportlink-extensie/health antwoorden 200; en, tenzij -Offline, de primaire club is
#      live-klaar (Get-SportlinkLiveBlockers leeg).
#   4b. AI: OpenAiApiKey aanwezig (alleen aanwezigheid, nooit de waarde), anders werkt de e-mailtester niet.
#   5. Stabiliteit: na -Stabiliteit seconden luisteren dezelfde services nog.
#   6. Browser (Playwright, headless Chromium): de belangrijkste schermen renderen zonder
#      foutbanner, zonder mislukte of 5xx-aanroep naar de API, zonder console-fout, met het juiste
#      versienummer in de header. Screenshots komen in -UitvoerMap.
#   7. Nogmaals de services: ze moeten ook ná de browsercontrole nog luisteren.
#
# BELANGRIJK voor agent-sessies: een controle in dezelfde aanroep als de start bewijst niet dat de
# services de aanroep overleven. Draai dit script daarom in een VOLGENDE aanroep.
#
# Gebruik:
#   .\Test-DebugGo.ps1                    # volledige controle
#   .\Test-DebugGo.ps1 -Offline           # extensie hoeft niet live-klaar te zijn
#   .\Test-DebugGo.ps1 -ZonderAI         # e-mailtester hoeft niet te werken (geen OpenAiApiKey)
#   .\Test-DebugGo.ps1 -ZonderBrowser     # alleen stap 1-5 en 7 (geen Playwright)
#   .\Test-DebugGo.ps1 -HuidigeWerkmap    # niet eisen dat de worktree op origin/develop staat
#
# Exit code: 0 = GO, 1 = NO-GO (minstens één harde fout).

param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [ValidateSet('SqlServer', 'Postgres', 'Sqlite')]
    [string]$Tier = 'Postgres',
    [int]$Stabiliteit = 15,
    [switch]$Offline,
    [switch]$ZonderBrowser,
    [switch]$ZonderAI,       # De e-mailtester en classificatie hoeven niet te werken (geen OpenAiApiKey lokaal)
    [switch]$HuidigeWerkmap,
    [string]$UitvoerMap = (Join-Path ([System.IO.Path]::GetTempPath()) 'sportlink-debug-go')
)

Import-Module (Join-Path $PSScriptRoot 'DevServices.psm1') -Force
$ports   = Get-DebugPorts
$api     = "http://localhost:$($ports.FunctionApp)"
$blazor  = "http://localhost:$($ports.BlazorAdmin)"
$fouten  = [System.Collections.Generic.List[string]]::new()
$waarsch = [System.Collections.Generic.List[string]]::new()

function Test-Diensten {
    param([string]$Moment)
    foreach ($naam in 'Azurite', 'FunctionApp', 'BlazorAdmin') {
        if (Test-PortListening -Port $ports[$naam]) {
            Write-Host ("  OK     {0,-12} :{1}  {2}" -f $naam, $ports[$naam], $Moment) -ForegroundColor DarkGray
        } else {
            Write-Host ("  FOUT   {0,-12} :{1} luistert niet {2}" -f $naam, $ports[$naam], $Moment) -ForegroundColor Red
            $fouten.Add("$naam luistert niet $Moment")
        }
    }
}

Write-Host "=== Test-DebugGo — GO/NO-GO van de debugomgeving ($Root) ===" -ForegroundColor Cyan

# 1 — services
Write-Host "1. Services" -ForegroundColor Cyan
Test-Diensten -Moment '(direct)'

# 2 — versie en branchstand
Write-Host "2. Versie" -ForegroundColor Cyan
$health = try { Invoke-RestMethod "$api/api/health" -TimeoutSec 10 -ErrorAction Stop } catch { $null }
$tierInfo = Get-DatabaseTierProject -Tier $Tier -RepoRoot $Root
$verwacht = $null
if ($tierInfo.Found -and $tierInfo.Exists) {
    $m = [regex]::Match((Get-Content $tierInfo.FullPath -Raw), '<Version>([^<]+)</Version>')
    if ($m.Success) { $verwacht = $m.Groups[1].Value }
}
if (-not $health) {
    Write-Host "  FOUT   /api/health antwoordt niet" -ForegroundColor Red
    $fouten.Add('FunctionApp health antwoordt niet')
} elseif ($verwacht -and $health.version -ne $verwacht) {
    Write-Host "  FOUT   draaiende versie $($health.version), maar de code in $Root is $verwacht" -ForegroundColor Red
    $fouten.Add("versie $($health.version) ≠ code $verwacht (oude host op de poort?)")
} else {
    Write-Host "  OK     versie $($health.version) komt overeen met de code" -ForegroundColor Green
}
if (-not $HuidigeWerkmap) {
    git -C $Root fetch origin develop 2>&1 | Out-Null
    $kop = (git -C $Root rev-parse HEAD).Trim()
    $dev = (git -C $Root rev-parse origin/develop).Trim()
    if ($kop -eq $dev) {
        Write-Host "  OK     worktree staat op origin/develop ($($kop.Substring(0, 8)))" -ForegroundColor Green
    } else {
        Write-Host "  FOUT   worktree staat op $($kop.Substring(0, 8)), origin/develop is $($dev.Substring(0, 8))" -ForegroundColor Red
        $fouten.Add('worktree loopt achter op origin/develop')
    }
}

# 3 — health-status
Write-Host "4. Health, sync en Sportlink-extensie" -ForegroundColor Cyan
if ($health) {
    $pending = if (($health.PSObject.Properties.Name -contains 'pendingMigrations') -and $health.pendingMigrations) { @($health.pendingMigrations) } else { @() }
    $statusOk = -not ($health.PSObject.Properties.Name -contains 'status') -or $health.status -eq 'ok'
    $alleenSyncOud = -not $statusOk -and $pending.Count -eq 0 -and ($health.PSObject.Properties.Name -contains 'syncStale') -and $health.syncStale
    if ($statusOk) { Write-Host "  OK     health-status ok" -ForegroundColor Green }
    elseif ($alleenSyncOud) { Write-Host "  LET OP health 'degraded' door verouderde sync (laatste: $($health.lastSync))" -ForegroundColor DarkYellow; $waarsch.Add('sync verouderd') }
    else { Write-Host "  FOUT   health-status '$($health.status)'" -ForegroundColor Red; $fouten.Add("health-status $($health.status)") }
    if ($pending.Count -gt 0) { Write-Host "  FOUT   openstaande migraties: $($pending -join ', ')" -ForegroundColor Red; $fouten.Add('openstaande migraties') }
}

# 4 — standaard sync en extensie
$syncStatus = try { Invoke-RestMethod "$api/api/beheer/sync/status" -TimeoutSec 15 -ErrorAction Stop } catch { $null }
if ($syncStatus) { Write-Host "  OK     /api/beheer/sync/status antwoordt" -ForegroundColor Green }
else { Write-Host "  FOUT   /api/beheer/sync/status antwoordt niet" -ForegroundColor Red; $fouten.Add('sync/status antwoordt niet') }

$slHealth = try { Invoke-RestMethod "$api/api/beheer/sportlink-extensie/health" -TimeoutSec 15 -ErrorAction Stop } catch { $null }
if (-not $slHealth) {
    Write-Host "  FOUT   /api/beheer/sportlink-extensie/health antwoordt niet" -ForegroundColor Red
    $fouten.Add('Sportlink-extensie health antwoordt niet')
} else {
    Write-Host "  OK     Sportlink-extensie antwoordt" -ForegroundColor Green
    if (-not $Offline) {
        $settings = Join-Path (Split-Path -Parent $tierInfo.FullPath) 'local.settings.json'
        $blokkades = Get-SportlinkLiveBlockers -SettingsPath $settings -Health $slHealth
        if ($blokkades.Count -eq 0) {
            $modus = if ($slHealth.dryRun) { 'dry-run AAN' } else { 'dry-run UIT' }
            Write-Host "  OK     Sportlink live-klaar voor de primaire club ($modus)" -ForegroundColor Green
        } else {
            foreach ($b in $blokkades) { Write-Host "  FOUT   Sportlink niet live: $b" -ForegroundColor Red }
            $fouten.Add('Sportlink niet live-klaar (start zonder -Offline, of gebruik -Offline bij deze test)')
        }
    }
}

# 4b — AI voor de e-mailtester
# De e-mailtester classificeert via een taalmodel. Zonder lokale OpenAiApiKey registreert de host geen
# IChatClient en geeft elke dry-run "IChatClient niet geconfigureerd". Dat gaat om een geheim van de
# eigenaar (en betaald verkeer), dus dit script controleert alleen of de sleutel er is — nooit de waarde.
$sleutelAanwezig = -not [string]::IsNullOrWhiteSpace($env:OpenAiApiKey)
if (-not $sleutelAanwezig -and $tierInfo.Exists) {
    $lsPad = Join-Path (Split-Path -Parent $tierInfo.FullPath) 'local.settings.json'
    $waarde = try { (Get-Content $lsPad -Raw | ConvertFrom-Json).Values.OpenAiApiKey } catch { $null }
    $sleutelAanwezig = -not [string]::IsNullOrWhiteSpace($waarde)
}
if ($sleutelAanwezig) {
    Write-Host "  OK     OpenAiApiKey is ingesteld (e-mailtester kan classificeren)" -ForegroundColor Green
} elseif ($ZonderAI) {
    Write-Host "  LET OP OpenAiApiKey ontbreekt — e-mailtester en classificatie werken niet (-ZonderAI)" -ForegroundColor DarkYellow
    $waarsch.Add('e-mailtester werkt niet (geen OpenAiApiKey)')
} else {
    Write-Host "  FOUT   OpenAiApiKey ontbreekt in local.settings.json: de e-mailtester geeft 'IChatClient niet geconfigureerd'" -ForegroundColor Red
    Write-Host "         Vul de sleutel in FunctionApp.Postgres/local.settings.json van de draaiende worktree (door jou, nooit door een agent) en herstart, of gebruik -ZonderAI." -ForegroundColor Yellow
    $fouten.Add('OpenAiApiKey ontbreekt (e-mailtester)')
}

# 5 — stabiliteit
if ($Stabiliteit -gt 0) {
    Write-Host "5. Stabiliteit ($Stabiliteit s wachten)" -ForegroundColor Cyan
    Start-Sleep -Seconds $Stabiliteit
    Test-Diensten -Moment "(na $Stabiliteit s)"
}

# 6 — browser
if (-not $ZonderBrowser) {
    Write-Host "6. Browser (Playwright)" -ForegroundColor Cyan
    if (-not (Get-Command node -ErrorAction SilentlyContinue) -or -not (Get-Command npm -ErrorAction SilentlyContinue)) {
        Write-Host "  FOUT   node/npm ontbreken — de browsercontrole kan niet draaien" -ForegroundColor Red
        $fouten.Add('browsercontrole kon niet draaien (node/npm ontbreken)')
    } else {
        $pwDir = Join-Path ([System.IO.Path]::GetTempPath()) 'sportlink-playwright'
        if (-not (Test-Path (Join-Path $pwDir 'node_modules/playwright'))) {
            Write-Host "  Playwright installeren in $pwDir (eenmalig) ..." -ForegroundColor DarkGray
            New-Item -ItemType Directory -Force -Path $pwDir | Out-Null
            npm install --prefix $pwDir playwright --no-audit --no-fund --silent 2>&1 | Out-Null
        }
        $env:NODE_PATH = Join-Path $pwDir 'node_modules'
        $uitvoer = node (Join-Path $PSScriptRoot 'debug-browsercheck.cjs') $blazor $api $(if ($health) { $health.version } else { '' }) $UitvoerMap 2>&1
        $code = $LASTEXITCODE
        $json = try { ($uitvoer -join "`n") | ConvertFrom-Json } catch { $null }
        if (-not $json) {
            Write-Host "  FOUT   browsercontrole gaf geen leesbaar resultaat:" -ForegroundColor Red
            $uitvoer | Select-Object -First 8 | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray }
            $fouten.Add('browsercontrole gaf geen resultaat')
        } elseif ($json.fout) {
            Write-Host "  FOUT   browsercontrole kon niet starten: $($json.fout)" -ForegroundColor Red
            $fouten.Add('browsercontrole kon niet starten')
        } else {
            foreach ($s in $json.schermen) {
                if (@($s.fouten).Count -eq 0) {
                    Write-Host ("  OK     {0,-20} {1}  v{2}" -f $s.scherm, $s.pad, $s.versie) -ForegroundColor Green
                } else {
                    Write-Host ("  FOUT   {0,-20} {1}" -f $s.scherm, $s.pad) -ForegroundColor Red
                    foreach ($f in $s.fouten) { Write-Host "           - $f" -ForegroundColor Red }
                    $fouten.Add("browser: $($s.scherm) ($($s.pad))")
                }
            }
            Write-Host "  Screenshots: $UitvoerMap" -ForegroundColor DarkGray
        }
    }
}

# 7 — nogmaals de services
Write-Host "7. Services na de controles" -ForegroundColor Cyan
Test-Diensten -Moment '(na browser)'

Write-Host ""
if ($fouten.Count -gt 0) {
    Write-Host "NO-GO — $($fouten.Count) probleem/problemen:" -ForegroundColor Red
    $fouten | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
if ($waarsch.Count -gt 0) { Write-Host "Waarschuwingen: $($waarsch -join '; ')" -ForegroundColor DarkYellow }
Write-Host "GO — de debugomgeving draait op versie $($health.version) en alle controles zijn geslaagd." -ForegroundColor Green
exit 0
