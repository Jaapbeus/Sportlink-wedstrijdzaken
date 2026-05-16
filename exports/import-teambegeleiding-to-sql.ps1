# import-teambegeleiding-to-sql.ps1
# Importeert de lokale teambegeleiding CSV naar avg.Teambegeleiding in SQL Server.
# Strategie: TRUNCATE TABLE + volledige bulk-insert — altijd de laatste CSV als bron.
#
# Flexibel: het script detecteert automatisch de aanwezige CSV-kolommen via een alias-map.
# Ontbrekende vereiste kolommen worden gemeld; extra kolommen worden genegeerd.
#
# Samengestelde velden:
#   Naam           = Roepnaam [Tussenvoegsel] Achternaam
#   Telefoonnummer = Mobiel nummer als gevuld, anders Telefoonnummer
#
# 🚨 AVG/GDPR: avg.Teambegeleiding bevat persoonsgegevens.
#              Beperk SELECT-rechten tot bevoegde gebruikers en rollen.
#
# Gebruik:
#   .\exports\import-teambegeleiding-to-sql.ps1
#   .\exports\import-teambegeleiding-to-sql.ps1 -CsvPath "C:\pad\naar\bestand.csv"
#   .\exports\import-teambegeleiding-to-sql.ps1 -DeleteCsvAfterImport $true

param (
    [string] $CsvPath              = "",
    [bool]   $DeleteCsvAfterImport = $false
)

$ErrorActionPreference = "Stop"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot  = Split-Path -Parent $PSScriptRoot

# Alias-map: canonieke veldnaam → mogelijke CSV-kolomnamen (case-insensitief)
# Voeg hier varianten toe als andere clubs andere kolomnamen gebruiken.
$columnAliases = [ordered]@{
    # Vereiste velden
    "Team"                   = @("Team", "Teamnaam", "Team naam")
    "Teamrol"                = @("Teamrol", "Rol", "Rol in team", "Rol team")
    "Roepnaam"               = @("Roepnaam", "Voornaam", "First name")
    "Achternaam"             = @("Achternaam", "Familienaam", "Last name")
    "Emailadres"             = @("E-mailadres", "Email", "E-mail", "Emailadres", "Mailadres")
    # Optionele maar aanbevolen velden
    "LeeftijdscategorieTeam" = @("Leeftijdscategorie team", "Leeftijdscategorie", "Age category")
    "Tussenvoegsel"          = @("Tussenvoegsel(s)", "Tussenvoegsel", "Infix", "Tussenv.")
    "MobielNummer"           = @("Mobiel nummer", "Mobiel", "Mobiele telefoon", "Mobile")
    "TelefoonnummerKolom"    = @("Telefoonnummer", "Telefoon", "Vaste telefoon", "Phone")
}

# Velden die verplicht aanwezig moeten zijn in de CSV
$vereist = @("Team", "Teamrol", "Roepnaam", "Achternaam", "Emailadres")

Write-Host "`n=== Teambegeleiding CSV → SQL import ===" -ForegroundColor Cyan
Write-Host (Get-Date -Format "yyyy-MM-dd HH:mm:ss") -ForegroundColor Gray

# ── Stap 1: CSV-bestand bepalen ───────────────────────────────────────────────
Write-Host "`n[1/5] CSV-bestand zoeken..." -ForegroundColor Yellow

if (-not $CsvPath) {
    $candidates = @(
        "$repoRoot\exports\BegeleidingTeams.csv",
        "$repoRoot\exports\teambegeleiding.csv"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $CsvPath = $c; break }
    }
}

if (-not $CsvPath -or -not (Test-Path $CsvPath)) {
    Write-Host "  FOUT: geen CSV gevonden. Download eerst via club.sportlink.com of via:" -ForegroundColor Red
    Write-Host "  .\exports\sync-teambegeleiding.ps1" -ForegroundColor Yellow
    exit 1
}

Write-Host "  Bestand: $CsvPath" -ForegroundColor Green

# ── Stap 2: Verbindingsstring ophalen uit local.settings.json ─────────────────
Write-Host "`n[2/5] SQL-verbindingsstring ophalen..." -ForegroundColor Yellow

$settingsPath = "$repoRoot\FunctionApp\local.settings.json"
if (-not (Test-Path $settingsPath)) {
    Write-Host "  FOUT: $settingsPath niet gevonden." -ForegroundColor Red
    Write-Host "  Kopieer local.settings.template.json naar local.settings.json en vul SqlConnectionString in." -ForegroundColor Yellow
    exit 1
}

$connectionStr = (Get-Content $settingsPath -Raw | ConvertFrom-Json).Values.SqlConnectionString
if (-not $connectionStr) {
    Write-Host "  FOUT: SqlConnectionString ontbreekt in local.settings.json." -ForegroundColor Red
    exit 1
}

Write-Host "  Verbindingsstring gevonden." -ForegroundColor Green

# ── Stap 3: CSV inlezen en kolomdetectie ──────────────────────────────────────
Write-Host "`n[3/5] CSV inlezen en kolomdetectie..." -ForegroundColor Yellow

$csvRows = Import-Csv -Path $CsvPath -Delimiter ";" -Encoding UTF8

if ($csvRows.Count -eq 0) {
    Write-Host "  FOUT: CSV bevat geen gegevensrijen." -ForegroundColor Red
    exit 1
}

# Detecteer aanwezige CSV-headers (case-insensitief)
$csvHeaders = $csvRows[0].PSObject.Properties.Name

function Find-CsvColumn([string[]]$aliases, [string[]]$headers) {
    $headersNorm = $headers | ForEach-Object { $_.Trim().ToLower() }
    foreach ($alias in $aliases) {
        $idx = [array]::IndexOf($headersNorm, $alias.Trim().ToLower())
        if ($idx -ge 0) { return $headers[$idx] }
    }
    return $null
}

# Bouw mapping: canonieke naam → werkelijke CSV-kolomnaam (of $null)
$resolved = @{}
foreach ($canonical in $columnAliases.Keys) {
    $resolved[$canonical] = Find-CsvColumn $columnAliases[$canonical] $csvHeaders
}

# Valideer vereiste velden
$ontbrekend = $vereist | Where-Object { -not $resolved[$_] }
if ($ontbrekend) {
    Write-Host "  FOUT: de volgende vereiste kolommen zijn niet gevonden in de CSV:" -ForegroundColor Red
    foreach ($v in $ontbrekend) {
        Write-Host "    - $v  (verwacht één van: $($columnAliases[$v] -join ', '))" -ForegroundColor Red
    }
    Write-Host "`n  Aanwezige CSV-kolommen:" -ForegroundColor Yellow
    $csvHeaders | ForEach-Object { Write-Host "    * $_" -ForegroundColor Gray }
    exit 1
}

# Toon kolomresolutie
Write-Host "  Gevonden kolommen:" -ForegroundColor Green
foreach ($canonical in $resolved.Keys) {
    if ($resolved[$canonical]) {
        Write-Host "    $canonical → '$($resolved[$canonical])'" -ForegroundColor Gray
    }
}

$heeftTussenvoegsel = $null -ne $resolved["Tussenvoegsel"]
$heeftMobiel        = $null -ne $resolved["MobielNummer"]
$heeftTelefoon      = $null -ne $resolved["TelefoonnummerKolom"]

if (-not $heeftMobiel -and -not $heeftTelefoon) {
    Write-Host "  WAARSCHUWING: geen telefoonnummer-kolom gevonden — Telefoonnummer wordt NULL." -ForegroundColor Yellow
}
if (-not $resolved["LeeftijdscategorieTeam"]) {
    Write-Host "  WAARSCHUWING: kolom 'Leeftijdscategorie team' niet gevonden — wordt NULL." -ForegroundColor Yellow
}

# ── DataTable vullen ───────────────────────────────────────────────────────────
$table = New-Object System.Data.DataTable
$null  = $table.Columns.Add("Team",                   [object])
$null  = $table.Columns.Add("LeeftijdscategorieTeam", [object])
$null  = $table.Columns.Add("Teamrol",                [object])
$null  = $table.Columns.Add("Naam",                   [object])
$null  = $table.Columns.Add("Emailadres",             [object])
$null  = $table.Columns.Add("Telefoonnummer",         [object])

function Get-CsvValue([object]$row, [string]$col) {
    if (-not $col) { return [DBNull]::Value }
    $v = $row.$col
    if ([string]::IsNullOrWhiteSpace($v)) { return [DBNull]::Value }
    return $v.Trim()
}

foreach ($row in $csvRows) {
    # Naam aggregeren: Roepnaam [Tussenvoegsel] Achternaam
    $delen = @(
        ($row.($resolved["Roepnaam"])).Trim(),
        $(if ($heeftTussenvoegsel) { ($row.($resolved["Tussenvoegsel"])).Trim() } else { "" }),
        ($row.($resolved["Achternaam"])).Trim()
    ) | Where-Object { $_ -ne "" }
    $naam = if ($delen) { $delen -join " " } else { [DBNull]::Value }

    # Telefoonnummer: Mobiel als gevuld, anders vaste telefoon
    $telefoon = [DBNull]::Value
    if ($heeftMobiel) {
        $m = ($row.($resolved["MobielNummer"])).Trim()
        if ($m) { $telefoon = $m }
    }
    if ($telefoon -eq [DBNull]::Value -and $heeftTelefoon) {
        $t = ($row.($resolved["TelefoonnummerKolom"])).Trim()
        if ($t) { $telefoon = $t }
    }

    $r = $table.NewRow()
    $r["Team"]                   = Get-CsvValue $row $resolved["Team"]
    $r["LeeftijdscategorieTeam"] = Get-CsvValue $row $resolved["LeeftijdscategorieTeam"]
    $r["Teamrol"]                = Get-CsvValue $row $resolved["Teamrol"]
    $r["Naam"]                   = $naam
    $r["Emailadres"]             = Get-CsvValue $row $resolved["Emailadres"]
    $r["Telefoonnummer"]         = $telefoon
    $table.Rows.Add($r)
}

Write-Host "  $($table.Rows.Count) rijen verwerkt." -ForegroundColor Green

# ── Stap 4: TRUNCATE + SqlBulkCopy ────────────────────────────────────────────
Write-Host "`n[4/5] Importeren naar SQL Server..." -ForegroundColor Yellow

$conn = New-Object System.Data.SqlClient.SqlConnection($connectionStr)
$conn.Open()

try {
    $cmd             = $conn.CreateCommand()
    $cmd.CommandText = "TRUNCATE TABLE [avg].[Teambegeleiding]"
    $cmd.ExecuteNonQuery() | Out-Null
    Write-Host "  avg.Teambegeleiding leeggemaakt (TRUNCATE)." -ForegroundColor Gray

    $bulk                      = New-Object System.Data.SqlClient.SqlBulkCopy($conn)
    $bulk.DestinationTableName = "[avg].[Teambegeleiding]"
    $bulk.BulkCopyTimeout      = 60
    foreach ($col in $table.Columns) {
        $null = $bulk.ColumnMappings.Add($col.ColumnName, $col.ColumnName)
    }
    $bulk.WriteToServer($table)
    $bulk.Close()
    Write-Host "  $($table.Rows.Count) rijen geïmporteerd naar avg.Teambegeleiding." -ForegroundColor Green

    $stopwatch.Stop()
    $cmdLog             = $conn.CreateCommand()
    $cmdLog.CommandText = @"
INSERT INTO [avg].[ImportLog] (AantalRijen, CsvBestand, ImporterendeDoor, Duur_ms)
VALUES (@rijen, @csv, SYSTEM_USER, @duur)
"@
    $null = $cmdLog.Parameters.AddWithValue("@rijen", $table.Rows.Count)
    $null = $cmdLog.Parameters.AddWithValue("@csv",   $CsvPath)
    $null = $cmdLog.Parameters.AddWithValue("@duur",  [int]$stopwatch.ElapsedMilliseconds)
    $cmdLog.ExecuteNonQuery() | Out-Null
    Write-Host "  Importlog weggeschreven (avg.ImportLog)." -ForegroundColor Gray
}
finally {
    $conn.Close()
}

# ── Stap 5: Optioneel CSV verwijderen ─────────────────────────────────────────
Write-Host "`n[5/5] Afronden..." -ForegroundColor Yellow

if ($DeleteCsvAfterImport) {
    Remove-Item -Path $CsvPath -Force
    Write-Host "  CSV verwijderd na succesvolle import: $CsvPath" -ForegroundColor Green
} else {
    Write-Host "  CSV bewaard lokaal. Gebruik -DeleteCsvAfterImport `$true om te verwijderen na import." -ForegroundColor Gray
}

# ── Rapport ───────────────────────────────────────────────────────────────────
Write-Host "`nKlaar!" -ForegroundColor Cyan
Write-Host "  Geïmporteerd : $($table.Rows.Count) personen" -ForegroundColor White
Write-Host "  Tabel        : avg.Teambegeleiding" -ForegroundColor White
Write-Host "  Duur         : $($stopwatch.ElapsedMilliseconds) ms" -ForegroundColor White
Write-Host "  Datum        : $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -ForegroundColor White
Write-Host "`n  ⚠️  avg.Teambegeleiding bevat AVG/GDPR-persoonsgegevens." -ForegroundColor Yellow
Write-Host "     Beperk SELECT-rechten tot bevoegde gebruikers en rollen.`n" -ForegroundColor Yellow
