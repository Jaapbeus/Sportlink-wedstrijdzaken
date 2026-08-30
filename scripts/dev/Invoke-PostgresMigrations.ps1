# Invoke-PostgresMigrations.ps1
# Dunne wrapper om Database.Postgres.Cli (MigrationRunner, #821) tegen een Postgres-instantie te
# draaien. De daadwerkelijke logica (checksum-verificatie, advisory lock, transacties) staat in
# Database.Postgres/MigrationRunner.cs — dit script kent geen eigen migratielogica.
#
# Gebruik:
#   $env:POSTGRES_CONNECTION_STRING = "Host=localhost;Port=5432;Username=postgres;Password=...;Database=sportlink"
#   .\scripts\dev\Invoke-PostgresMigrations.ps1
#
# Wachtwoord uitsluitend via de omgevingsvariabele — nooit als scriptparameter, want
# scriptparameters staan op elk platform zichtbaar in de processenlijst.
#
# Exit code: 0 = alle migraties toegepast (of al up-to-date), 1 = fout (zie foutmelding).

param(
    [string]$MigrationsPath
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")

if (-not $env:POSTGRES_CONNECTION_STRING) {
    Write-Host "Fout: omgevingsvariabele POSTGRES_CONNECTION_STRING is niet gezet." -ForegroundColor Red
    exit 1
}

$cliProject = Join-Path $root "Database.Postgres.Cli/Database.Postgres.Cli.csproj"
$runArgs = @("run", "--project", $cliProject, "--configuration", "Release")
if ($MigrationsPath) {
    $runArgs += "--"
    $runArgs += $MigrationsPath
}

& dotnet @runArgs
exit $LASTEXITCODE
