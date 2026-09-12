<#
.SYNOPSIS
    Zet de AllStars FC-demoteams en -wedstrijden klaar op een lokale Postgres-ontwikkeldatabase (#1060).

.DESCRIPTION
    De AllStars-demodata is over twee plekken verdeeld, en alleen de eerste is een migratie:

      1. Database.Postgres/migrations/006_allstars_demodata.sql  — AppSettings-rij, velden,
         veldbeschikbaarheid, speeltijden. Draait mee met Invoke-PostgresMigrations.ps1.
      2. scripts/migrations/003-seed-allstars-demo-matches-postgres.sql — 28 teams, 224 wedstrijden.
         Kan GEEN migratie zijn: die seedt in his.teams/his.matches, en die tabellen worden door
         geen enkel migratiebestand aangemaakt. PostgresSchemaGenerator maakt ze dynamisch zodra
         de ETL zijn eerste sync draait — op een verse ontwikkeldatabase bestaan ze dus nog niet
         en weigert het seed-script (terecht, met een duidelijke melding) te draaien.

    Dit script overbrugt dat gat in drie stappen:

      a. his-tabellen aanmaken via Database.Postgres.Cli --ensure-his-tables, dat
         PostgresMergeOrchestrator.EnsureHisTableAsync aanroept — exact de weg die de ETL zelf
         neemt. Bewust GEEN handgeschreven DDL hier: die bestaat al twee keer (de zelftest en de
         CI-job fresh-db-postgres) en een derde kopie zou bij de eerstvolgende schemawijziging
         stilzwijgend uit de pas gaan lopen.
      b. het seed-script draaien.
      c. de canonieke teamlijst (public.teams/public.teamaliassen) opbouwen via
         POST /api/beheer/teams/herstel (#946) — public.teams is een AFGELEIDE tabel; zonder deze
         stap blijft de GUI leeg ook al staat his.teams vol. Dit is hetzelfde pad dat een beheerder
         via de knop op de pagina Teamaliassen neemt, geen fixture die levert wat productie hoort
         te leveren.

    Idempotent: elke stap is NOT EXISTS-gated of expliciet idempotent.

.PARAMETER ClubCode
    De club waarvoor stap c de canonieke lijst opbouwt, meegegeven als X-Club-Code. Standaard
    ALLSTARS — de demodata staat onder die clubcode, terwijl het endpoint zonder header terugvalt
    op de PRIMAIRE club (die met syncenabled = TRUE). Zonder deze header geeft het endpoint dus een
    409: er zijn geen gesynchroniseerde teams voor de primaire club, en dat is een correct antwoord
    op de verkeerde vraag.

.PARAMETER FunctionAppUrl
    Basis-URL van de draaiende functiehost voor stap c. Draait hij niet, dan slaat het script die
    stap NIET stilzwijgend over: het meldt hem als openstaand en geeft exitcode 1.

.PARAMETER SkipHerstel
    Alleen stap a en b uitvoeren. Gebruik dit als je stap c bewust later via de GUI doet.

.EXAMPLE
    $env:POSTGRES_CONNECTION_STRING = "Host=localhost;Port=5432;Username=dev;Password=...;Database=sportlink"
    ./scripts/dev/Seed-AllStarsDemodata.ps1

.NOTES
    Verbindingsreeks uitsluitend via POSTGRES_CONNECTION_STRING — nooit als scriptparameter:
    argumenten zijn op elk platform zichtbaar in de processenlijst (#800).
    Cross-platform: geen $env:TEMP, geen Get-NetTCPConnection, geen CIM, geen backslash in paden.
#>
[CmdletBinding()]
param(
    [string]$ClubCode = 'ALLSTARS',
    [string]$FunctionAppUrl = 'http://localhost:7094',
    [switch]$SkipHerstel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $PSScriptRoot 'DevServices.psm1') -Force

if (-not $env:POSTGRES_CONNECTION_STRING) {
    Write-Host "Fout: omgevingsvariabele POSTGRES_CONNECTION_STRING is niet gezet." -ForegroundColor Red
    Write-Host "  Zie docs/DEVELOPER-SETUP.md sectie 4.2." -ForegroundColor Yellow
    exit 1
}
$connStr = $env:POSTGRES_CONNECTION_STRING

# ── Verbindingsreeks ontleden (keyword- én URI-vorm, zoals #976) ─────────────
if ($connStr -match '^postgres(ql)?://') {
    $uri    = [Uri]$connStr
    $pgHost = $uri.Host
    $pgPort = if ($uri.Port -gt 0) { $uri.Port } else { 5432 }
    $deel   = $uri.UserInfo -split ':', 2
    $pgUser = [Uri]::UnescapeDataString($deel[0])
    $pgPass = if ($deel.Count -gt 1) { [Uri]::UnescapeDataString($deel[1]) } else { $null }
    $pgDb   = $uri.AbsolutePath.TrimStart('/')
} else {
    $pgHost = if ($connStr -match '(?:Host|Server)\s*=\s*([^;]+)')                 { $Matches[1].Trim() } else { $null }
    $pgPort = if ($connStr -match 'Port\s*=\s*([^;]+)')                            { $Matches[1].Trim() } else { 5432 }
    $pgUser = if ($connStr -match '(?:Username|User ID|User Id)\s*=\s*([^;]+)')    { $Matches[1].Trim() } else { $null }
    $pgPass = if ($connStr -match 'Password\s*=\s*([^;]+)')                        { $Matches[1].Trim() } else { $null }
    $pgDb   = if ($connStr -match 'Database\s*=\s*([^;]+)')                        { $Matches[1].Trim() } else { $null }
}
if (-not ($pgHost -and $pgUser -and $pgDb)) {
    Write-Host "Fout: kon host/gebruiker/database niet uit POSTGRES_CONNECTION_STRING halen." -ForegroundColor Red
    exit 1
}

# psql bij voorkeur IN de container: geen eigen installatie nodig, en het wachtwoord blijft in de
# omgeving van het kindproces in plaats van in de processenlijst.
$pgContainer  = 'sportlink-postgres'
$viaContainer = $false
if (Get-Command docker -ErrorAction SilentlyContinue) {
    $draait = (& docker ps --filter "name=^/$pgContainer$" --filter 'status=running' --format '{{.Names}}' 2>$null)
    if ($draait -eq $pgContainer) { $viaContainer = $true }
}
if (-not $viaContainer -and -not (Get-Command psql -ErrorAction SilentlyContinue)) {
    Write-Host "Fout: container '$pgContainer' draait niet en psql ontbreekt." -ForegroundColor Red
    Write-Host "  Start de database met: docker compose up -d" -ForegroundColor Yellow
    exit 1
}

function Invoke-SeedSql([string]$Pad) {
    if ($viaContainer) {
        return Invoke-Psql -ContainerName $pgContainer -SqlFile $Pad -Password $pgPass `
                           -User $pgUser -Database $pgDb -StopOnError
    }
    $env:PGPASSWORD = $pgPass
    $uit = & psql -h $pgHost -p "$pgPort" -U $pgUser -d $pgDb -v ON_ERROR_STOP=1 -f $Pad 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($uit -join "`n").Trim() }
}

# ── a. his-tabellen aanmaken langs de weg van de ETL ─────────────────────────
Write-Host "1/3  his-tabellen aanmaken (via PostgresMergeOrchestrator)..." -ForegroundColor Cyan
$cli = Join-Path $RepoRoot 'Database.Postgres.Cli' 'Database.Postgres.Cli.csproj'
& dotnet run --project $cli --configuration Release -- --ensure-his-tables
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Mislukt — zie de melding hierboven." -ForegroundColor Red
    exit 1
}

# ── b. demoteams en -wedstrijden seeden ─────────────────────────────────────
Write-Host "2/3  AllStars-demoteams en -wedstrijden seeden..." -ForegroundColor Cyan
$seed = Join-Path $RepoRoot 'scripts' 'migrations' '003-seed-allstars-demo-matches-postgres.sql'
if (-not (Test-Path $seed)) {
    Write-Host "  Seed-script niet gevonden: $seed" -ForegroundColor Red
    exit 1
}
$seedResultaat = Invoke-SeedSql $seed
if ($seedResultaat.ExitCode -ne 0) {
    Write-Host "  Seeden mislukt: $($seedResultaat.Output)" -ForegroundColor Red
    exit 1
}
Write-Host "  Demodata geseed." -ForegroundColor Green

# ── c. canonieke teamlijst opbouwen ─────────────────────────────────────────
if ($SkipHerstel) {
    Write-Host "3/3  Herstelpad overgeslagen (-SkipHerstel)." -ForegroundColor DarkYellow
    Write-Host "     public.teams blijft leeg tot je POST /api/beheer/teams/herstel aanroept," -ForegroundColor DarkGray
    Write-Host "     of de knop gebruikt op de pagina Teamaliassen." -ForegroundColor DarkGray
    exit 0
}

Write-Host "3/3  Canonieke teamlijst opbouwen via POST /api/beheer/teams/herstel (club $ClubCode)..." -ForegroundColor Cyan
try {
    $resp = Invoke-RestMethod -Method Post -Uri "$FunctionAppUrl/api/beheer/teams/herstel" `
                              -Headers @{ 'X-Club-Code' = $ClubCode } `
                              -TimeoutSec 120 -ErrorAction Stop
    # De vorm van het antwoord kan per versie verschillen; toon wat er is zonder erop te leunen.
    $samenvatting = ($resp | ConvertTo-Json -Compress -Depth 4)
    Write-Host "  Teamlijst opgebouwd: $samenvatting" -ForegroundColor Green
} catch {
    # Bewust GEEN stille overslag: dan zou "niet uitgevoerd" niet te onderscheiden zijn van
    # "uitgevoerd, niets te doen" — zelfde regel als 'overslaan is falen' in de zelftest.
    Write-Host "  Herstelpad NIET uitgevoerd: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "    Draait de functiehost op $FunctionAppUrl ? Start met: ./scripts/dev/Start-Debug.ps1" -ForegroundColor Yellow
    Write-Host "    Een 409 betekent: geen gesynchroniseerde teams voor club '$ClubCode'. Controleer of" -ForegroundColor Yellow
    Write-Host "    stap 2 rijen in his.teams heeft gezet voor die clubcode, of geef -ClubCode mee." -ForegroundColor Yellow
    Write-Host "    Daarna dit script opnieuw, of gebruik de knop op de pagina Teamaliassen." -ForegroundColor Yellow
    exit 1
}
