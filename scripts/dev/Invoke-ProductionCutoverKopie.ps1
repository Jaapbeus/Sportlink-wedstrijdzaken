# Invoke-ProductionCutoverKopie.ps1 (#976)
#
# Dunne, veilige wrapper om MigrationTools/SqlServerToPostgresCopy tegen de ECHTE productie SQL
# Server en Supabase Postgres te draaien. De kopieerlogica zelf staat volledig in dat .csproj —
# dit script voegt alleen een veilige manier toe om de drie benodigde waarden aan te leveren.
#
# WAAROM DIT SCRIPT EN NIET GEWOON $env:X = "..." OP DE COMMANDOREGEL:
# Een wachtwoord in een `$env:VAR = "..."`-toewijzing op de PowerShell-commandoregel komt in de
# PowerShell-commandogeschiedenis terecht (Get-History / de PSReadLine-geschiedenisfile) — een
# permanent, makkelijk over het hoofd te ziene lek van een productiewachtwoord. Dit script vraagt
# de twee connectiestrings in plaats daarvan op via Read-Host -AsSecureString: die tonen niets op
# het scherm en belanden niet in de geschiedenis. De wachtwoorden bestaan alleen kort in het
# geheugen van dit PowerShell-proces, worden nergens naar een bestand geschreven, en zijn na
# afloop van het script weer weg.
#
# GEBRUIK:
#   .\scripts\dev\Invoke-ProductionCutoverKopie.ps1                # dry-run (standaard, altijd eerst)
#   .\scripts\dev\Invoke-ProductionCutoverKopie.ps1 -Execute        # echte kopie, met bevestigingsvraag
#
# Vraagt bij ELKE run opnieuw — ook al staat de omgevingsvariabele al gezet in deze PowerShell-
# sessie van een vorige (mogelijk foutieve) poging. Een eerdere versie hergebruikte een al gezette
# waarde stilzwijgend: bij een tikfout in de eerste poging bleef die foutieve waarde dan actief
# voor elke volgende poging in dezelfde sessie, zonder enige aanwijzing waarom het script opeens
# niets meer vroeg en meteen dezelfde fout gaf. -ReuseEnvironment herstelt het oude gedrag voor het
# uitzonderingsgeval dat je de waarden zelf al via een secrets-manager hebt gezet.

param(
    [switch]$Execute,
    [switch]$ReuseEnvironment
)

if (-not $ReuseEnvironment) {
    Remove-Item Env:\SQLSERVER_CONNECTION_STRING -ErrorAction SilentlyContinue
    Remove-Item Env:\POSTGRES_CONNECTION_STRING -ErrorAction SilentlyContinue
    Remove-Item Env:\PRODUCTIE_CLUBCODE -ErrorAction SilentlyContinue
}

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")

function Get-PlainTextFromSecureString {
    param([System.Security.SecureString]$Secure)
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

if (-not $env:SQLSERVER_CONNECTION_STRING) {
    Write-Host "SQL Server-connectiestring van de PRODUCTIE-database (Azure Portal -> Function App ->" -ForegroundColor Cyan
    Write-Host "Configuration -> Application settings -> SqlConnectionString):" -ForegroundColor Cyan
    $secure = Read-Host -AsSecureString "  SQLSERVER_CONNECTION_STRING"
    $env:SQLSERVER_CONNECTION_STRING = Get-PlainTextFromSecureString $secure
}

if (-not $env:POSTGRES_CONNECTION_STRING) {
    Write-Host "Postgres-connectiestring (Supabase dashboard -> Project Settings -> Database)." -ForegroundColor Cyan
    Write-Host "Zowel de URI-vorm (postgresql://gebruiker:wachtwoord@host:5432/database, de eerste" -ForegroundColor Cyan
    Write-Host "die Supabase toont) als de keyword=value-vorm (tab 'Connection parameters') werken." -ForegroundColor Cyan
    $secure = Read-Host -AsSecureString "  POSTGRES_CONNECTION_STRING"
    $env:POSTGRES_CONNECTION_STRING = Get-PlainTextFromSecureString $secure
}

if (-not $env:PRODUCTIE_CLUBCODE) {
    # Geen secret, gewoon zichtbaar vragen — geen -AsSecureString nodig.
    $env:PRODUCTIE_CLUBCODE = Read-Host "ClubCode van de echte productieclub (uit dbo.AppSettings, NIET 'ALLSTARS')"
}

$toolProject = Join-Path $root "MigrationTools/SqlServerToPostgresCopy/SqlServerToPostgresCopy.csproj"
$runArgs = @("run", "--project", $toolProject, "--configuration", "Release", "--")

if (-not $Execute) {
    Write-Host ""
    Write-Host "=== DRY-RUN — telt rijen, schrijft niets. Draai met -Execute voor de echte kopie. ===" -ForegroundColor Yellow
    & dotnet @runArgs "--dry-run"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "=== LIVE-KOPIE — schrijft naar de Postgres-doeldatabase voor club '$env:PRODUCTIE_CLUBCODE'. ===" -ForegroundColor Red
$bevestiging = Read-Host "Typ exact 'JA' om door te gaan"
if ($bevestiging -ne "JA") {
    Write-Host "Geannuleerd — er is niets geschreven." -ForegroundColor Yellow
    exit 1
}

& dotnet @runArgs
exit $LASTEXITCODE
