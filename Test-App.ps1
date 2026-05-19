# Test-App.ps1
# Zelfherstellend verificatiescript voor Sportlink Wedstrijdzaken.
# Controleert: database-schema vs. code, build, en alle API-endpoints.
# Gebruik: .\Test-App.ps1 [-Fix] [-Verbose]
#   -Fix     : pas automatisch fixbare problemen direct aan (ALTER TABLE etc.)
#   -Verbose : toon ook succesvolle checks
#
# Exit code: 0 = alles ok, 1 = fouten gevonden (of niet gefixed)

param(
    [switch]$Fix,
    [switch]$Verbose
)

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$issues  = [System.Collections.Generic.List[string]]::new()
$fixed   = [System.Collections.Generic.List[string]]::new()
$ok      = [System.Collections.Generic.List[string]]::new()

function Write-Ok($msg)    { if ($Verbose) { Write-Host "  [OK]  $msg" -ForegroundColor Green } ; $ok.Add($msg) }
function Write-Issue($msg) { Write-Host "  [!!]  $msg" -ForegroundColor Red    ; $issues.Add($msg) }
function Write-Fixed($msg) { Write-Host "  [FIX] $msg" -ForegroundColor Yellow ; $fixed.Add($msg) }
function Write-Section($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# ──────────────────────────────────────────────────────────────────────
# 1. CONNECTION STRING ophalen
# ──────────────────────────────────────────────────────────────────────
Write-Section "Database verbinding"

$settingsPath = Join-Path $root "FunctionApp\local.settings.json"
if (-not (Test-Path $settingsPath)) {
    Write-Issue "local.settings.json niet gevonden — kopieer van local.settings.template.json"
    exit 1
}

$settings = Get-Content $settingsPath | ConvertFrom-Json
$connStr  = $settings.Values.SqlConnectionString
if (-not $connStr) {
    Write-Issue "SqlConnectionString niet gevonden in local.settings.json"
    exit 1
}

# Extraheer Server en Database uit connection string
$server = if ($connStr -match 'Data Source=([^;]+)') { $Matches[1] } else { $null }
$db     = if ($connStr -match 'Initial Catalog=([^;]+)') { $Matches[1] } else { $null }

if (-not $server -or -not $db) {
    Write-Issue "Kon server/database niet parsen uit connection string"
    exit 1
}
Write-Ok "Connection: $server / $db"

function Invoke-Sql($query) {
    sqlcmd -S $server -d $db -E -Q $query -h -1 -W 2>&1
}

# ──────────────────────────────────────────────────────────────────────
# 2. SCHEMA VALIDATIE — vergelijk .sql bestanden met live database
# ──────────────────────────────────────────────────────────────────────
Write-Section "Schema validatie"

# Verwachte kolommen per tabel (afgeleid uit .sql schema-bestanden + API-code)
$expectedColumns = @{
    "dbo.AppSettings" = @(
        "ClubName","ClubCode","SportlinkApiUrl","SportlinkClientId","SeasonStartMonth",
        "Accommodatie","LastSyncTimestamp","FetchSchedule","PlannerAfzenderNaam",
        "CoordinatorNaam","CoordinatorFunctie","PlannerEmailAdres",
        "HerplanDeadlineDagen","BufferMinuten",
        "AccommodatiePlaats","AccommodatieLatitude","AccommodatieLongitude",
        "EmailVoetnoot"
    )
    "dbo.TeamVoorkeurTijden" = @(
        "Id","TeamNaam","DagVanWeek","VoorkeurTijd","Prioriteit","Actief","ClubCode",
        "mta_inserted","mta_modified"
    )
    "dbo.VeldBeschikbaarheid" = @(
        "Id","VeldNummer","DagVanWeek","BeschikbaarVanaf","BeschikbaarTot",
        "GebruikZonsondergang","ClubCode"
    )
    "dbo.UitgeslotenEmailAdressen" = @(
        "Id","EmailAdres","Omschrijving","Actief","ClubCode","mta_inserted"
    )
    "dbo.EmailTemplateInstellingen" = @(
        "Id","TemplateKey","Onderwerp","BodyTemplate","Actief","ClubCode",
        "mta_inserted","mta_modified"
    )
    "dbo.AppSettingsAudit" = @(
        "Id","GewijzigdDoor","Veld","OudeWaarde","NieuweWaarde","ClubCode","Tijdstip"
    )
    "dbo.TeamRegels" = @(
        "Id","TeamNaam","RegelType","WaardeMinuten","WaardeVeldNummer","WaardeTijd",
        "Prioriteit","Actief","ClubCode","Opmerking"
    )
    "dbo.Velden" = @(
        "VeldNummer","VeldNaam","VeldType","HeeftKunstlicht","Actief"
    )
}

# SQL schema-bestanden (voor ontbrekende tabellen opnieuw aanmaken)
$schemaSqlMap = @{
    "dbo.UitgeslotenEmailAdressen" = Join-Path $root "Database\dbo\Tables\UitgeslotenEmailAdressen.sql"
    "dbo.AppSettingsAudit"         = Join-Path $root "Database\dbo\Tables\AppSettingsAudit.sql"
    "dbo.TeamRegels"               = Join-Path $root "Database\dbo\Tables\TeamRegels.sql"
    "dbo.Velden"                   = Join-Path $root "Database\dbo\Tables\Velden.sql"
    "dbo.VeldBeschikbaarheid"      = Join-Path $root "Database\dbo\Tables\VeldBeschikbaarheid.sql"
    "dbo.TeamVoorkeurTijden"       = Join-Path $root "Database\dbo\Tables\TeamVoorkeurTijden.sql"
    "dbo.EmailTemplateInstellingen"= Join-Path $root "Database\dbo\Tables\EmailTemplateInstellingen.sql"
}


foreach ($tableKey in $expectedColumns.Keys) {
    $parts  = $tableKey -split '\.'
    $schema = $parts[0]
    $table  = $parts[1]

    # Controleer of tabel bestaat
    $existsQ = "SELECT COUNT(1) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.name='$table' AND s.name='$schema'"
    $exists  = (Invoke-Sql $existsQ | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1).Trim()

    if ($exists -ne "1") {
        if ($Fix -and $schemaSqlMap.ContainsKey($tableKey)) {
            $sqlFile = $schemaSqlMap[$tableKey]
            if (Test-Path $sqlFile) {
                $createSql = Get-Content $sqlFile -Raw
                $result = sqlcmd -S $server -d $db -E -Q $createSql 2>&1
                if ($LASTEXITCODE -eq 0) {
                    Write-Fixed "Tabel $tableKey aangemaakt"
                } else {
                    Write-Issue "Tabel $tableKey aanmaken mislukt: $result"
                }
            } else {
                Write-Issue "Tabel $tableKey ontbreekt en schema-bestand niet gevonden: $sqlFile"
            }
        } else {
            Write-Issue "Tabel $tableKey ONTBREEKT in database (gebruik -Fix om te herstellen)"
        }
        continue
    }

    # Controleer kolommen
    $colQ   = "SELECT c.name FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.name='$table' AND s.name='$schema'"
    $dbCols = (Invoke-Sql $colQ | Where-Object { $_ -match '\S' } | ForEach-Object { $_.Trim() })

    foreach ($col in $expectedColumns[$tableKey]) {
        if ($col -notin $dbCols) {
            # Bepaal ALTER TABLE statement op basis van bekende typen
            $colDef = switch ($col) {
                "AccommodatiePlaats"   { "NVARCHAR(100) NULL" }
                "AccommodatieLatitude" { "FLOAT NULL" }
                "AccommodatieLongitude"{ "FLOAT NULL" }
                "EmailVoetnoot"        { "NVARCHAR(MAX) NULL" }
                "ClubCode"             { "NVARCHAR(20) NOT NULL CONSTRAINT [DF_${table}_ClubCode] DEFAULT 'VRC'" }
                "mta_inserted"         { "DATETIME2 NOT NULL CONSTRAINT [DF_${table}_Inserted] DEFAULT GETDATE()" }
                "mta_modified"         { "DATETIME2 NOT NULL CONSTRAINT [DF_${table}_Modified] DEFAULT GETDATE()" }
                "Actief"               { "BIT NOT NULL CONSTRAINT [DF_${table}_Actief] DEFAULT 1" }
                default                { $null }
            }

            if ($Fix -and $colDef) {
                $alterSql = "ALTER TABLE [$schema].[$table] ADD [$col] $colDef"
                $result   = Invoke-Sql $alterSql
                if ($LASTEXITCODE -eq 0) {
                    Write-Fixed "$tableKey.$col toegevoegd ($colDef)"
                } else {
                    Write-Issue "$tableKey.$col ontbreekt, ALTER mislukt: $result"
                }
            } else {
                Write-Issue "$tableKey.$col ONTBREEKT (gebruik -Fix om te herstellen)"
            }
        }
    }

    Write-Ok "Schema $tableKey OK"
}

# ──────────────────────────────────────────────────────────────────────
# 3. BUILD VERIFICATIE
# ──────────────────────────────────────────────────────────────────────
Write-Section "Build verificatie"

$funcProj   = Join-Path $root "FunctionApp\fa-dev-sportlink-01.csproj"
$blazorProj = Join-Path $root "BlazorAdmin\BlazorAdmin.csproj"

foreach ($proj in @($funcProj, $blazorProj)) {
    $name   = Split-Path $proj -Parent | Split-Path -Leaf
    $output = dotnet build $proj -c Debug 2>&1
    $errors = $output | Where-Object { $_ -match "\serror\s" }
    if ($errors) {
        Write-Issue "Build MISLUKT: $name"
        $errors | ForEach-Object { Write-Issue "  $_" }
    } else {
        Write-Ok "Build OK: $name"
    }
}

# ──────────────────────────────────────────────────────────────────────
# 4. API ENDPOINT SMOKE TEST (alleen als FunctionApp draait)
# ──────────────────────────────────────────────────────────────────────
Write-Section "API smoke tests"

$funcBase = "http://localhost:7094"
$funcRunning = [bool](Get-NetTCPConnection -LocalPort 7094 -State Listen -ErrorAction SilentlyContinue)

# Lees func-sleutel uit local.settings.json indien aanwezig
$funcKey = $settings.Values.FunctionAppKey

$headers = @{}
if ($funcKey) { $headers["x-functions-key"] = $funcKey }

$endpoints = @(
    @{ Method="GET";  Path="api/beheer/settings";            Desc="Instellingen laden" }
    @{ Method="GET";  Path="api/beheer/sync/status";         Desc="Sync status" }
    @{ Method="GET";  Path="api/beheer/templates";           Desc="E-mailtemplates" }
    @{ Method="GET";  Path="api/beheer/voorkeurstijden";     Desc="Voorkeurstijden" }
    @{ Method="GET";  Path="api/beheer/teamregels";          Desc="Teamregels" }
    @{ Method="GET";  Path="api/beheer/uitgesloten-emails";  Desc="Uitgesloten e-mails" }
    @{ Method="GET";  Path="api/beheer/velden";              Desc="Velden" }
    @{ Method="GET";  Path="api/beheer/veldbeschikbaarheid"; Desc="Veldbeschikbaarheid" }
    @{ Method="GET";  Path="api/beheer/email-log";           Desc="E-maillog" }
    @{ Method="GET";  Path="api/beheer/teams";               Desc="Teams" }
)

if (-not $funcRunning) {
    Write-Host "  FunctionApp niet actief op :7094 — API-tests overgeslagen." -ForegroundColor DarkGray
    Write-Host "  Start met: .\Start-Debug.ps1 en wacht 10s; daarna .\Test-App.ps1" -ForegroundColor DarkGray
} else {
    foreach ($ep in $endpoints) {
        try {
            $resp = Invoke-WebRequest -Uri "$funcBase/$($ep.Path)" -Method $ep.Method `
                -Headers $headers -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
            if ($resp.StatusCode -in 200..299) {
                Write-Ok "$($ep.Method) $($ep.Path) → $($resp.StatusCode) ($($ep.Desc))"
            } else {
                Write-Issue "$($ep.Method) $($ep.Path) → $($resp.StatusCode) ($($ep.Desc))"
            }
        } catch {
            $code = $_.Exception.Response?.StatusCode.value__
            Write-Issue "$($ep.Method) $($ep.Path) → $code — $($_.Exception.Message) ($($ep.Desc))"
        }
    }
}

# ──────────────────────────────────────────────────────────────────────
# 5. FEEDBACK WIDGET — GitHub-integratie smoke test
# ──────────────────────────────────────────────────────────────────────
Write-Section "Feedback widget (GitHub-integratie)"

$ghPat   = $settings.Values.GitHubPat
$ghOwner = $settings.Values.GitHubOwner
$ghRepo  = if ($settings.Values.GitHubRepo) { $settings.Values.GitHubRepo } else { "Sportlink-wedstrijdzaken" }

if (-not $ghPat -or -not $ghOwner) {
    Write-Issue "GitHubPat of GitHubOwner niet geconfigureerd in local.settings.json — feedbackknop werkt niet"
} else {
    $ghHeaders = @{
        "Authorization"        = "Bearer $ghPat"
        "Accept"               = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent"           = "Test-App.ps1/SportlinkWedstrijdzaken"
    }
    $testTitle = "[TEST] Smoke test feedback widget — $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    $testBody  = "Automatisch aangemaakt door Test-App.ps1 als smoke test voor de feedbackknop. Wordt direct gesloten."
    $createPayload = @{ title = $testTitle; body = $testBody } | ConvertTo-Json -Compress

    try {
        $created  = Invoke-RestMethod -Uri "https://api.github.com/repos/$ghOwner/$ghRepo/issues" `
                        -Method POST -Headers $ghHeaders -Body $createPayload -ContentType "application/json" -ErrorAction Stop
        $issueNr  = $created.number
        Write-Ok "GitHub issue aangemaakt: #$issueNr"

        # Sluit het test-issue direct (GitHub ondersteunt geen delete via REST)
        $closePayload = '{"state":"closed"}'
        Invoke-RestMethod -Uri "https://api.github.com/repos/$ghOwner/$ghRepo/issues/$issueNr" `
            -Method PATCH -Headers $ghHeaders -Body $closePayload -ContentType "application/json" -ErrorAction Stop | Out-Null
        Write-Ok "GitHub issue #$issueNr gesloten (cleanup)"
    } catch {
        $code = $_.Exception.Response?.StatusCode.value__
        Write-Issue "GitHub issue aanmaken mislukt (HTTP $code) — feedbackknop werkt niet: $($_.Exception.Message)"
    }
}

# ──────────────────────────────────────────────────────────────────────
# 7. BLAZOR PAGINA CHECK (alleen als Blazor draait)
# ──────────────────────────────────────────────────────────────────────
Write-Section "Blazor pagina checks"

$blazorBase    = "http://localhost:5242"
$blazorRunning = [bool](Get-NetTCPConnection -LocalPort 5242 -State Listen -ErrorAction SilentlyContinue)

$pages = @(
    @{ Path="/";                    Desc="Home / Dashboard" }
    @{ Path="/instellingen";        Desc="Instellingen" }
    @{ Path="/email-templates";     Desc="E-mailtemplates" }
    @{ Path="/voorkeurstijden";     Desc="Voorkeurstijden" }
    @{ Path="/veldbeschikbaarheid"; Desc="Veldbeschikbaarheid" }
    @{ Path="/uitgesloten-emails";  Desc="Uitgesloten e-mails" }
    @{ Path="/email-tester";        Desc="E-mail tester" }
)

if (-not $blazorRunning) {
    Write-Host "  BlazorAdmin niet actief op :5242 — pagina-tests overgeslagen." -ForegroundColor DarkGray
    Write-Host "  Start met: .\Start-Debug.ps1 en wacht 15s; daarna .\Test-App.ps1" -ForegroundColor DarkGray
} else {
    foreach ($page in $pages) {
        try {
            $resp = Invoke-WebRequest -Uri "$blazorBase$($page.Path)" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
            # Blazor WASM serveert altijd dezelfde index.html (inclusief blazor-error-ui als hidden div).
            # Runtime-fouten zijn niet detecteerbaar via statische HTTP GET — alleen HTTP-statuscode is betrouwbaar.
            if ($resp.StatusCode -in 200..299) {
                Write-Ok "GET $($page.Path) → $($resp.StatusCode) ($($page.Desc))"
            } else {
                Write-Issue "GET $($page.Path) → $($resp.StatusCode) ($($page.Desc))"
            }
        } catch {
            $code = $_.Exception.Response?.StatusCode.value__
            Write-Issue "GET $($page.Path) → $code — $($_.Exception.Message) ($($page.Desc))"
        }
    }
}

# ──────────────────────────────────────────────────────────────────────
# 8. SWA PROXY CHECKS (alleen als SWA emulator draait op poort 4280)
# ──────────────────────────────────────────────────────────────────────
Write-Section "SWA emulator checks"

$swaBase    = "http://localhost:4280"
$swaRunning = [bool](Get-NetTCPConnection -LocalPort 4280 -State Listen -ErrorAction SilentlyContinue)

if (-not $swaRunning) {
    Write-Host "  SWA emulator niet actief op :4280 — SWA-tests overgeslagen." -ForegroundColor DarkGray
    Write-Host "  Start met: .\Start-Debug.ps1 -Swa" -ForegroundColor DarkGray
} else {
    # 1. Mock-login pagina bereikbaar
    try {
        $resp = Invoke-WebRequest -Uri "$swaBase/.auth/login/aad" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        if ($resp.StatusCode -in 200..299) {
            Write-Ok "SWA mock-login pagina bereikbaar (/.auth/login/aad → $($resp.StatusCode))"
        } else {
            Write-Issue "SWA mock-login → $($resp.StatusCode) (verwacht 200)"
        }
    } catch {
        Write-Issue "SWA mock-login niet bereikbaar: $($_.Exception.Message)"
    }

    # 2. Onbeveiligde route / geeft redirect naar login (SWA route-enforcement werkt)
    try {
        $resp = Invoke-WebRequest -Uri "$swaBase/" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        # Verwacht: 200 (Blazor index.html of redirect naar mock-login die automatisch gevolgd wordt)
        if ($resp.StatusCode -in 200..299) {
            Write-Ok "SWA GUI bereikbaar (/ → $($resp.StatusCode))"
        } else {
            Write-Issue "SWA GUI → $($resp.StatusCode)"
        }
    } catch {
        $code = $_.Exception.Response?.StatusCode.value__
        if ($code -eq 401) {
            Write-Ok "SWA route-enforcement actief (/ → 401 zonder login — correct gedrag)"
        } else {
            Write-Issue "SWA GUI niet bereikbaar: $($_.Exception.Message)"
        }
    }

    # 3. API-proxy werkt: route wordt doorgestuurd naar FunctionApp (200 = proxy actief)
    # Let op: de SWA CLI met apiDevserverUrl dwingt GEEN auth af op API-routes in dev-modus.
    # Auth op /api/beheer/* wordt pas afgedwongen door Azure SWA in productie (linked backend).
    try {
        $resp = Invoke-WebRequest -Uri "$swaBase/api/beheer/settings" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        if ($resp.StatusCode -in 200..299) {
            Write-Ok "SWA API-proxy actief (/api/beheer/settings → $($resp.StatusCode) via proxy)"
        } else {
            Write-Issue "SWA API-proxy → $($resp.StatusCode) (verwacht 200 in dev-modus)"
        }
    } catch {
        $code = $_.Exception.Response?.StatusCode.value__
        Write-Issue "SWA API-proxy niet bereikbaar: $($_.Exception.Message)"
    }
}

# ──────────────────────────────────────────────────────────────────────
# 9. SAMENVATTING
# ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  VERIFICATIE RESULTAAT" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Geslaagd : $($ok.Count)" -ForegroundColor Green
if ($fixed.Count -gt 0) {
    Write-Host "  Gefixed   : $($fixed.Count)" -ForegroundColor Yellow
}
if ($issues.Count -gt 0) {
    Write-Host "  Fouten    : $($issues.Count)" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Openstaande problemen:" -ForegroundColor Red
    $issues | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    Write-Host ""
    if (-not $Fix) {
        Write-Host "  TIP: Voer .\Test-App.ps1 -Fix uit om automatisch fixbare problemen te herstellen." -ForegroundColor Yellow
    }
    exit 1
} else {
    Write-Host ""
    Write-Host "  Alles in orde." -ForegroundColor Green
    exit 0
}
