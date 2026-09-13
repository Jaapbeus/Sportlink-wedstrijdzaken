# Start-Debug.ps1
# Start alle lokale services voor v2 ontwikkelen en testen, en wacht tot ze daadwerkelijk
# klaar zijn (readiness-polling — geen vaste sleeps).
#
# Vereisten: .NET 9 runtime + .NET 10 SDK, Azure Functions Core Tools v4, Azurite,
#            en de database van de gekozen tier (docker compose up -d).
#
# TIER-KEUZE (#1060): standaard Postgres — de tier die deze installatie in productie draait
# (docs/ARCHITECTUUR-DATABASE-TIERS.md §49). Lokaal ontwikkelen op de andere tier zou betekenen
# dat de dagelijkse verificatie een tier meet die niet gedeployd wordt; dat is precies hoe #972
# dagenlang onopgemerkt bleef. De vertaling tier -> projectpad komt uit
# scripts/ci/database-tiers.json via Get-DatabaseTierProject — nooit een tweede lijst hier.
#
# Gebruik:
#   .\Start-Debug.ps1            → Azurite + FunctionApp (Postgres-tier) + BlazorAdmin
#   .\Start-Debug.ps1 -Tier SqlServer → idem, maar op de SQL Server-tier
#   .\Start-Debug.ps1 -Swa       → bovenstaande + SWA emulator op http://localhost:4280
#   .\Start-Debug.ps1 -NoWatch   → BlazorAdmin zonder hot reload (dotnet run i.p.v. dotnet watch)
#   .\Start-Debug.ps1 -Tail      → één samengevoegde logstroom i.p.v. losse vensters
#   .\Start-Debug.ps1 -Clean     → stop + dotnet clean BlazorAdmin vóór het starten
#
# Exit code: 0 = alle services bereikbaar, 1 = minstens één service niet opgestart.
#
# Hot reload gedrag:
#   BlazorAdmin  :5242  → HOT RELOAD actief via 'dotnet watch'. Wijzigingen in .razor/.cs/.css
#                          worden automatisch herladen zonder herstart.
#   FunctionApp  :7094  → GEEN hot reload. Azure Functions isolated worker ondersteunt dit
#                          niet. Na elke C#-wijziging in FunctionApp: Stop-Debug.ps1 +
#                          Start-Debug.ps1 opnieuw.
#
# Poorten:
#   Azurite      :10000 (blob), :10001 (queue), :10002 (table)
#   FunctionApp  :7094  → http://localhost:7094/api/health
#   BlazorAdmin  :5242  → http://localhost:5242  (direct, zonder auth-emulatie)
#   SWA emulator :4280  → http://localhost:4280  (met auth-emulatie en routeregels)

param(
    # Standaard Postgres: de tier die in productie draait. Zie de toelichting bovenaan.
    [ValidateSet('SqlServer', 'Postgres', 'Sqlite')]
    [string]$Tier = 'Postgres',

    [switch]$Swa,      # Start ook de Azure SWA emulator (vereist swa CLI)
    [switch]$NoWatch,  # Gebruik dotnet run i.p.v. dotnet watch voor BlazorAdmin
    [switch]$Tail,     # Voeg alle service-output samen in één venster
    [switch]$Clean     # dotnet clean op BlazorAdmin vóór het starten
)

$root    = Resolve-Path (Join-Path $PSScriptRoot "../..")
$logDir  = Join-Path ([System.IO.Path]::GetTempPath()) 'sportlink-debug-logs'
$started = Get-Date

# Cross-platform (#800):
#  - Op Windows start elke service in een eigen console-venster (ongewijzigd gedrag).
#  - Op macOS/Linux kan dat niet: Start-Process opent daar nooit een venster en
#    -WindowStyle is er een no-op (gedocumenteerd in de Start-Process-docs). Daarom
#    loopt de output daar altijd naar een logbestand, precies zoals bij -Tail.
$onWindows    = [bool]$IsWindows
$shellExe     = if ($onWindows) { 'powershell' } else { 'pwsh' }
$useLogFiles  = $Tail -or -not $onWindows

Import-Module (Join-Path $PSScriptRoot 'DevServices.psm1') -Force

$ports   = Get-DebugPorts
$pidFile = Get-DebugPidFile

# --- Tier resolven (#1060) ---------------------------------------------------
# Drie verschillende fouten, drie verschillende meldingen: een tikfout, een geldige-maar-nog-niet-
# gebouwde tier, en een tier waarvan het project ontbreekt. Stilzwijgend terugvallen op een andere
# tier is de ene uitkomst die we hier nooit willen.
$tierInfo = Get-DatabaseTierProject -Tier $Tier -RepoRoot $root
if (-not $tierInfo.Found) {
    Write-Host "Onbekende tier '$Tier'. Geldige waarden: $($tierInfo.Valid -join ', ')" -ForegroundColor Red
    exit 1
}
if (-not $tierInfo.Built) {
    Write-Host "Tier '$Tier' is geldig maar nog niet gebouwd (zie issue #$($tierInfo.EpicIssue))." -ForegroundColor Red
    exit 1
}
if (-not $tierInfo.Exists) {
    Write-Host "Projectbestand van tier '$Tier' ontbreekt: $($tierInfo.Csproj)" -ForegroundColor Red
    exit 1
}

$funcProjectDir = Split-Path -Parent $tierInfo.FullPath
$funcSettings   = Join-Path $funcProjectDir 'local.settings.json'
if (-not (Test-Path $funcSettings)) {
    Write-Host ""
    Write-Host "local.settings.json ontbreekt in $(Split-Path -Leaf $funcProjectDir)/ — de functiehost start niet." -ForegroundColor Red
    Write-Host "  Kopieer local.settings.template.json ernaast en vul de verbindingsreeks in." -ForegroundColor Yellow
    Write-Host "  Zie docs/DEVELOPER-SETUP.md sectie 5." -ForegroundColor Yellow
    exit 1
}

# Controleer of de machine-lokale git-hook patronen aanwezig zijn (#514)
$hooksPatterns = Join-Path $root ".githooks/sensitive-patterns.txt"
if (-not (Test-Path $hooksPatterns)) {
    Write-Host ""
    Write-Host "SETUP ONVOLLEDIG: .githooks/sensitive-patterns.txt ontbreekt!" -ForegroundColor Yellow
    Write-Host "   Secrets worden niet beschermd door de lokale commit/push hooks." -ForegroundColor Yellow
    Write-Host "   Fix (eenmalig):" -ForegroundColor Yellow
    Write-Host "     git config core.hooksPath .githooks" -ForegroundColor Cyan
    Write-Host "     cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt" -ForegroundColor Cyan
    Write-Host "   Vul sensitive-patterns.txt daarna aan met installatie-specifieke secrets." -ForegroundColor Yellow
    Write-Host ""
}

# --- Vorige services stoppen (gedeelde teardown, wacht tot poorten vrij zijn) ---
Write-Host "Controleer op draaiende services..." -ForegroundColor DarkGray
$teardown = Stop-DebugServices
if (-not $teardown.AllPortsFree) {
    Write-Host ""
    Write-Host "Poorten zijn niet vrijgegeven — starten afgebroken." -ForegroundColor Red
    if ($IsWindows) {
        Write-Host "Controleer met: Get-NetTCPConnection -LocalPort 7094,5242,4280 -State Listen" -ForegroundColor Yellow
    } else {
        Write-Host "Controleer met: lsof -nP -iTCP:7094 -iTCP:5242 -iTCP:4280 -sTCP:LISTEN" -ForegroundColor Yellow
    }
    exit 1
}

if ($Clean) {
    Write-Host "BlazorAdmin cleanen (verwijdert stale fingerprints)..." -ForegroundColor Cyan
    dotnet clean (Join-Path $root 'BlazorAdmin/BlazorAdmin.csproj') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "dotnet clean mislukt — starten afgebroken." -ForegroundColor Red
        exit 1
    }
}

if ($useLogFiles) {
    if (Test-Path $logDir) { Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue }
    else { New-Item -ItemType Directory -Path $logDir | Out-Null }
}

# Regels in het PID-bestand krijgen het formaat 'naam=pid', zodat Stop-Debug.ps1 Azurite
# kan overslaan zolang -All niet is meegegeven.
$debugPids  = [System.Collections.Generic.List[string]]::new()
$logSources = [ordered]@{}

function Start-Service {
    <#
        Start één service.

        Windows : zonder -Tail krijgt elke service een eigen console-venster (-NoExit),
                  met -Tail gaat de output naar een logbestand.
        macOS   : altijd naar een logbestand. Start-Process opent daar nooit een venster
                  en -WindowStyle is er een gedocumenteerde no-op, dus zonder redirect
                  zou alle service-output verloren gaan.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Command,
        [string]$Banner = '',
        [string]$BannerColor = 'Cyan',
        [switch]$Minimized
    )

    if ($useLogFiles) {
        $logFile = Join-Path $logDir "$Name.log"
        $startArgs = @{
            FilePath               = $shellExe
            ArgumentList           = @('-NoProfile', '-Command', $Command)
            PassThru               = $true
            RedirectStandardOutput = $logFile
            RedirectStandardError  = "$logFile.err"
        }
        # -WindowStyle bestaat alleen zinvol op Windows; op macOS wordt de parameter genegeerd.
        if ($onWindows) { $startArgs.WindowStyle = 'Hidden' }
        $proc = Start-Process @startArgs
        $logSources[$Name] = $logFile
    } else {
        $inner = if ($Banner) {
            "Write-Host '$Banner' -ForegroundColor $BannerColor; $Command"
        } else { $Command }
        $style = if ($Minimized) { 'Minimized' } else { 'Normal' }
        $proc = Start-Process -FilePath $shellExe -ArgumentList @('-NoExit', '-Command', $inner) `
            -WindowStyle $style -PassThru
    }

    $debugPids.Add("$Name=$($proc.Id)")
    return $proc
}

# --- Azurite ---
if (Test-PortListening -Port $ports.Azurite) {
    Write-Host "Azurite actief (poort $($ports.Azurite))." -ForegroundColor DarkGray
} else {
    Write-Host "Azurite niet gevonden - starten..." -ForegroundColor Yellow
    $azuriteDir = Join-Path ([System.IO.Path]::GetTempPath()) 'azurite'
    if (-not (Test-Path $azuriteDir)) { New-Item -ItemType Directory -Path $azuriteDir | Out-Null }
    $azuriteLog = Join-Path $azuriteDir 'debug.log'
    Start-Service -Name 'azurite' -Minimized `
        -Command "azurite --location '$azuriteDir' --debug '$azuriteLog'" | Out-Null

    if (-not (Wait-ForPort -Port $ports.Azurite -TimeoutSeconds 30 -Label 'Azurite')) {
        Write-Host "Azurite is niet binnen 30s gestart." -ForegroundColor Red
        Write-Host "  Installeer eenmalig via: npm install -g azurite" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "  Azurite gestart." -ForegroundColor Green
}

# --- FunctionApp ---
Write-Host "FunctionApp ($Tier-tier) starten op http://localhost:$($ports.FunctionApp) ..." -ForegroundColor Cyan
Write-Host "  FunctionApp heeft GEEN hot reload. Na codewijzigingen: Stop-Debug.ps1 + Start-Debug.ps1." -ForegroundColor DarkYellow
Start-Service -Name 'func' `
    -Banner "FunctionApp $Tier - poort $($ports.FunctionApp)  (geen hot reload - herstart vereist na codewijziging)" `
    -Command "Set-Location '$funcProjectDir'; func start --port $($ports.FunctionApp)" | Out-Null

# --- BlazorAdmin ---
if ($NoWatch) {
    Write-Host "BlazorAdmin starten op http://localhost:$($ports.BlazorAdmin) (geen hot reload) ..." -ForegroundColor Cyan
    Start-Service -Name 'blazor' `
        -Banner "BlazorAdmin - poort $($ports.BlazorAdmin)  (geen hot reload)" `
        -Command "Set-Location '$root/BlazorAdmin'; dotnet run --launch-profile http" | Out-Null
} else {
    Write-Host "BlazorAdmin starten op http://localhost:$($ports.BlazorAdmin) (hot reload actief) ..." -ForegroundColor Cyan
    Start-Service -Name 'blazor' -BannerColor 'Green' `
        -Banner "BlazorAdmin - poort $($ports.BlazorAdmin)  (hot reload: wijzigingen in .razor/.cs/.css herladen automatisch)" `
        -Command "Set-Location '$root/BlazorAdmin'; `$env:MSBUILDDISABLENODEREUSE = '1'; dotnet watch run --launch-profile http --non-interactive" | Out-Null
}

# --- SWA emulator (optioneel) ---
$swaStarted = $false
if ($Swa) {
    if (-not (Get-Command swa -ErrorAction SilentlyContinue)) {
        Write-Host ""
        Write-Host "SWA CLI niet gevonden. Installeer eenmalig via:" -ForegroundColor Yellow
        Write-Host "  npm install -g @azure/static-web-apps-cli" -ForegroundColor Yellow
        Write-Host "SWA emulator wordt overgeslagen." -ForegroundColor Yellow
    } else {
        Write-Host "SWA emulator starten op http://localhost:$($ports.Swa) ..." -ForegroundColor Cyan
        Start-Service -Name 'swa' `
            -Banner "SWA emulator - poort $($ports.Swa)" `
            -Command "Set-Location '$root'; swa start sportlink-admin" | Out-Null
        $swaStarted = $true
    }
}

# --- PIDs opslaan zodat Stop-Debug.ps1 ze kan opruimen ---
$debugPids | Set-Content $pidFile
Write-Host "  Debug-proces PIDs opgeslagen: $($debugPids -join ', ')" -ForegroundColor DarkGray

# ──────────────────────────────────────────────────────────────────────
# READINESS — pollen tot de services echt reageren
# ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Wachten tot de services bereikbaar zijn..." -ForegroundColor Cyan

$failures = [System.Collections.Generic.List[string]]::new()

# FunctionApp: health-endpoint is de enige echte readiness-indicator.
# De host heeft een bekende koude-start van ~20s (#175), daarom een ruime timeout.
$health = Wait-ForHealth -Url "http://localhost:$($ports.FunctionApp)/api/health" -TimeoutSeconds 120
if ($health) {
    $versie = if ($health.version) { $health.version } else { 'onbekend' }
    # #863 legt de tier in de assembly-metadata vast en /api/health geeft hem terug. Dat is het
    # enige bewijs dat de host die nu luistert ook echt de gevraagde tier is — een oude functiehost
    # op dezelfde poort zou anders stilzwijgend als 'gestart' worden gerapporteerd.
    $gemeten = if ($health.PSObject.Properties.Name -contains 'tier' -and $health.tier) { $health.tier } else { $null }
    if ($gemeten -and $gemeten -ne $Tier) {
        Write-Host "  FunctionApp antwoordt, maar meldt tier '$gemeten' terwijl '$Tier' gevraagd is." -ForegroundColor Red
        Write-Host "    Draait er nog een oude functiehost op poort $($ports.FunctionApp)? Stop-Debug.ps1 en probeer opnieuw." -ForegroundColor Yellow
        $failures.Add('FunctionApp (verkeerde tier)')
    } else {
        $tierLabel = if ($gemeten) { $gemeten } else { "$Tier (niet gemeld)" }
        Write-Host "  FunctionApp OK - versie $versie, tier $tierLabel" -ForegroundColor Green
    }

    # #859: /api/health geeft 200 met status 'degraded' zodra de instellingencache leeg is. Dat is
    # geen detail: zonder instellingen antwoordt élk /api/beheer/*-endpoint met 500, terwijl dit
    # script tot #1060 gewoon 'FunctionApp OK' meldde. Een verse database heeft alleen de democlub
    # (syncenabled = FALSE) en loopt daar altijd tegenaan.
    $statusOk = -not ($health.PSObject.Properties.Name -contains 'status') -or $health.status -eq 'ok'
    $settingsOk = -not ($health.PSObject.Properties.Name -contains 'settingsLoaded') -or $health.settingsLoaded
    # #1098: health meldt ook welke migraties de database nog mist (pendingMigrations) en of de
    # code een kolom verwacht die er niet is (schemaWarning). Dat was het tweede v3.3.0.0-incident:
    # code vooruit op het schema, elk beheerscherm 500. Hier wijzen we dan naar de migratie, niet
    # naar het seed-script — dat is een andere oorzaak.
    $pending = if (($health.PSObject.Properties.Name -contains 'pendingMigrations') -and $health.pendingMigrations) { @($health.pendingMigrations) } else { @() }
    $schemaWarning = if ($health.PSObject.Properties.Name -contains 'schemaWarning') { $health.schemaWarning } else { $null }
    if (-not ($statusOk -and $settingsOk)) {
        Write-Host "  Health meldt status '$($health.status)' (settingsLoaded=$($health.settingsLoaded))." -ForegroundColor Red
        if ($pending.Count -gt 0) {
            Write-Host "    Openstaande migraties: $($pending -join ', ')" -ForegroundColor Yellow
            Write-Host "    Pas ze toe met: dotnet run --project Database.Postgres.Cli" -ForegroundColor Yellow
            Write-Host "    (POSTGRES_CONNECTION_STRING als omgevingsvariabele) en herstart daarna de functiehost." -ForegroundColor Yellow
        }
        if ($schemaWarning) {
            Write-Host "    $schemaWarning" -ForegroundColor Yellow
        }
        if (-not $settingsOk -and $pending.Count -eq 0 -and -not $schemaWarning) {
            Write-Host "    Er is geen primaire club met syncenabled = TRUE in de database." -ForegroundColor Yellow
            Write-Host "    Los op met: scripts/migrations/004-seed-lokale-placeholderclub-postgres.sql" -ForegroundColor Yellow
            Write-Host "    (zie docs/DEVELOPER-SETUP.md sectie 4.2) en herstart daarna de functiehost." -ForegroundColor Yellow
        }
        $failures.Add('FunctionApp (degraded)')
    }
} else {
    Write-Host "  FunctionApp reageerde niet binnen 120s op /api/health" -ForegroundColor Red
    $failures.Add('FunctionApp')
}

# BlazorAdmin: eerste build kan lang duren, vooral na een clean.
if (Wait-ForHttp -Url "http://localhost:$($ports.BlazorAdmin)/" -TimeoutSeconds 180) {
    Write-Host "  BlazorAdmin OK" -ForegroundColor Green
} else {
    Write-Host "  BlazorAdmin reageerde niet binnen 180s" -ForegroundColor Red
    $failures.Add('BlazorAdmin')
}

if ($swaStarted) {
    if (Wait-ForHttp -Url "http://localhost:$($ports.Swa)/" -TimeoutSeconds 90) {
        Write-Host "  SWA emulator OK" -ForegroundColor Green
    } else {
        Write-Host "  SWA emulator reageerde niet binnen 90s" -ForegroundColor Red
        $failures.Add('SWA emulator')
    }
}

$duur = [int]((Get-Date) - $started).TotalSeconds

# ──────────────────────────────────────────────────────────────────────
# SAMENVATTING
# ──────────────────────────────────────────────────────────────────────
Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "NIET ALLE SERVICES ZIJN GESTART ($duur s)" -ForegroundColor Red
    Write-Host "  Mislukt: $($failures -join ', ')" -ForegroundColor Red
    Write-Host ""
    if ($useLogFiles) {
        Write-Host "  Bekijk de logs in: $logDir" -ForegroundColor Yellow
    } else {
        Write-Host "  Controleer de foutmelding in het bijbehorende venster." -ForegroundColor Yellow
    }
    Write-Host "  Veelvoorkomend: .NET 9 runtime ontbreekt (503 'Function host is not running')" -ForegroundColor DarkGray
    Write-Host "  of de database van de $Tier-tier is niet bereikbaar (docker compose up -d)." -ForegroundColor DarkGray
    exit 1
}

Write-Host "Alle services gereed in $duur seconden ($Tier-tier):" -ForegroundColor Green
Write-Host "  FunctionApp   http://localhost:$($ports.FunctionApp)/api/health  (herstart vereist na C#-wijziging)" -ForegroundColor White
if ($NoWatch) {
    Write-Host "  BlazorAdmin   http://localhost:$($ports.BlazorAdmin)  (geen hot reload)" -ForegroundColor White
} else {
    Write-Host "  BlazorAdmin   http://localhost:$($ports.BlazorAdmin)  (hot reload actief - browser ververst automatisch)" -ForegroundColor Green
}
if ($swaStarted) {
    Write-Host "  SWA emulator  http://localhost:$($ports.Swa)  (auth-emulatie actief)" -ForegroundColor White
    Write-Host ""
    Write-Host "Gebruik http://localhost:$($ports.Swa) voor de Admin GUI met SWA routeregels." -ForegroundColor DarkGray
    Write-Host "Mock-login: http://localhost:$($ports.Swa)/.auth/login/aad  (vul username + rol 'admin' in)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "HTTP 200 bewijst NIET dat de Blazor-app rendert. Open de GUI en controleer op" -ForegroundColor DarkYellow
Write-Host "een foutbanner + zichtbaar versienummer voordat je iets oplevert." -ForegroundColor DarkYellow
Write-Host ""
Write-Host "Stoppen: .\scripts\dev\Stop-Debug.ps1  (of -Clean om ook fingerprints op te ruimen)" -ForegroundColor DarkGray

# ──────────────────────────────────────────────────────────────────────
# GEAGGREGEERDE LOGSTROOM (-Tail)
# ──────────────────────────────────────────────────────────────────────
if ($Tail) {
    $prefixColor = @{ azurite = 'DarkGray'; func = 'Cyan'; blazor = 'Green'; swa = 'Magenta' }
    $printed     = @{}
    foreach ($name in $logSources.Keys) { $printed[$name] = 0 }

    Write-Host ""
    Write-Host "=== Samengevoegde logstroom (Ctrl+C om te stoppen; services blijven draaien) ===" -ForegroundColor Cyan
    Write-Host ""

    while ($true) {
        foreach ($name in $logSources.Keys) {
            foreach ($file in @($logSources[$name], "$($logSources[$name]).err")) {
                if (-not (Test-Path $file)) { continue }
                try {
                    $lines = @(Get-Content $file -ErrorAction Stop)
                } catch { continue }

                $key = "$name|$file"
                if (-not $printed.ContainsKey($key)) { $printed[$key] = 0 }
                if ($lines.Count -le $printed[$key]) { continue }

                $new = $lines[$printed[$key]..($lines.Count - 1)]
                $printed[$key] = $lines.Count

                $label = "[{0}]" -f $name.ToUpper()
                $color = if ($prefixColor.ContainsKey($name)) { $prefixColor[$name] } else { 'White' }
                foreach ($line in $new) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    Write-Host ("{0,-10}" -f $label) -NoNewline -ForegroundColor $color
                    Write-Host $line
                }
            }
        }
        Start-Sleep -Milliseconds 400
    }
}

exit 0
