# Test-PostgresConnection.ps1
# Minimale psql-gedreven verificatie dat de lokale Postgres-container (#822, epic #815) bereikbaar
# is — het Postgres-equivalent van hoe Test-App.ps1 sqlcmd gebruikt voor de SQL Server-tier.
#
# Gebruik:
#   docker compose --profile postgres up -d
#   $env:PGPASSWORD = "<jouw lokale wachtwoord>"
#   .\scripts\dev\Test-PostgresConnection.ps1
#
# Wachtwoord uitsluitend via de omgevingsvariabele PGPASSWORD — nooit als scriptparameter of
# psql-argument, zelfde regel als SQLCMDPASSWORD elders in dit project: argumenten staan op elk
# platform zichtbaar in de processenlijst.
#
# Exit code: 0 = verbinding + testquery geslaagd, 1 = fout (zie foutmelding).

param(
    [string]$HostName = "localhost",
    [int]$Port = 5432,
    [string]$User = $env:POSTGRES_USER,
    [string]$Database = $(if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { "sportlink" })
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
    Write-Host "Fout: 'psql' is niet gevonden op dit systeem." -ForegroundColor Red
    Write-Host "Installeren:" -ForegroundColor Yellow
    Write-Host "  Windows : winget install PostgreSQL.PostgreSQL.16" -ForegroundColor Yellow
    Write-Host "  macOS   : brew install libpq && brew link --force libpq" -ForegroundColor Yellow
    exit 1
}

if (-not $User) {
    Write-Host "Fout: geen gebruiker opgegeven en POSTGRES_USER is niet gezet." -ForegroundColor Red
    exit 1
}

if (-not $env:PGPASSWORD) {
    Write-Host "Fout: omgevingsvariabele PGPASSWORD is niet gezet." -ForegroundColor Red
    exit 1
}

Write-Host "Verbinden met $User@${HostName}:${Port}/$Database..." -ForegroundColor Cyan
$result = psql -h $HostName -p $Port -U $User -d $Database -c "SELECT version();" -t -A 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Fout: verbinding of testquery mislukt." -ForegroundColor Red
    Write-Host $result -ForegroundColor Red
    exit 1
}

Write-Host "OK — verbonden. Serverversie:" -ForegroundColor Green
Write-Host "  $($result.Trim())" -ForegroundColor Green
exit 0
