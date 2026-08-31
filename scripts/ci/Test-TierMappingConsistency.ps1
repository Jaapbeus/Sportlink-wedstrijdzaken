<#
.SYNOPSIS
    Bewijst dat resolve-database-tier.sh (bash-kant) en Get-DatabaseTierProject
    (PowerShell-kant, scripts/dev/DevServices.psm1) voor elke tier in
    scripts/ci/database-tiers.json exact dezelfde uitkomst geven (#865, laatste openstaande
    acceptatiecriterium: "Er is een test die faalt als de twee kanten verschillende uitkomsten
    geven").

.DESCRIPTION
    Draait zonder database en zonder secrets — alleen het bash-script, het JSON-bestand en de
    PowerShell-module — dus ook bruikbaar op een fork zonder CI-secrets (#816-eis, hergebruikt
    door #864). Test elke bekende tier-naam plus één bewust onbekende naam, zodat ook het
    "onbekende waarde"-pad op beide kanten hetzelfde gedrag laat zien.

    Exitcode-contract van resolve-database-tier.sh (ongewijzigd, alleen hier expliciet getoetst):
      0 = gevonden + gebouwd, 1 = onbekende tier-naam, 2 = gevonden, boom bestaat nog niet.

.EXAMPLE
    pwsh scripts/ci/Test-TierMappingConsistency.ps1
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $RepoRoot 'scripts' 'dev' 'DevServices.psm1') -Force

$bashCmd = Get-Command bash -ErrorAction SilentlyContinue
if (-not $bashCmd) {
    Write-Host "SKIP: 'bash' niet gevonden op PATH — deze test kan alleen daar draaien waar het" -ForegroundColor Yellow
    Write-Host "bash-script zelf ook draait (CI: ubuntu-latest; lokaal: Git Bash/WSL)." -ForegroundColor Yellow
    exit 0
}

# Op Windows kan 'bash' naar de WSL-launcher wijzen (System32\bash.exe) i.p.v. Git Bash. Die
# launcher vertaalt een los meegegeven Windows-pad NIET automatisch naar /mnt/<letter>/... —
# ontdekt tijdens het bouwen van deze test (#865): een kale Windows-pad-aanroep gaf overal
# exitcode 127 ("No such file or directory"), niet omdat het script fout was maar omdat WSL het
# pad niet herkende. Git Bash/macOS/Linux (CI) hebben dit probleem niet en gebruiken het pad
# ongewijzigd.
$isWslLauncher = $IsWindows -and ($bashCmd.Source -match '\\System32\\bash\.exe$')

function ConvertTo-ShellPath {
    param([string]$WindowsPath)
    if (-not $isWslLauncher) { return $WindowsPath }
    $full = (Resolve-Path $WindowsPath).Path
    $drive = $full.Substring(0, 1).ToLowerInvariant()
    $rest = $full.Substring(2) -replace '\\', '/'
    return "/mnt/$drive$rest"
}

$tiersJsonPath = Join-Path $RepoRoot 'scripts' 'ci' 'database-tiers.json'
if (-not (Test-Path $tiersJsonPath)) { throw "Tier-tabel ontbreekt: $tiersJsonPath" }
$tiersData = Get-Content $tiersJsonPath -Raw | ConvertFrom-Json
$tierNamen = @($tiersData.tiers | ForEach-Object { $_.name })
# Bewust ook een niet-bestaande naam: toetst dat "onbekende waarde" op beide kanten hetzelfde is.
$gevalNamen = $tierNamen + @('DitBestaatNiet')

$resolverShellPath = ConvertTo-ShellPath (Join-Path $RepoRoot 'scripts' 'ci' 'resolve-database-tier.sh')

function Invoke-ShellSide {
    param([string]$Tier)
    $env:DatabaseTier = $Tier
    # WSL forwardt Windows-omgevingsvariabelen alleen als ze in WSLENV staan (gedocumenteerd
    # WSL-gedrag) — zonder deze regel ziet het bash-script $DatabaseTier als leeg. Onschadelijk
    # voor Git Bash/macOS/Linux: die negeren WSLENV gewoon.
    if ($isWslLauncher) { $env:WSLENV = 'DatabaseTier' }
    try {
        $output = & bash $resolverShellPath 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Remove-Item Env:\DatabaseTier -ErrorAction SilentlyContinue
        Remove-Item Env:\WSLENV -ErrorAction SilentlyContinue
    }
    $csproj = $null
    foreach ($line in $output) {
        if ($line -match '^Tier: .* -> (.+)$') { $csproj = $Matches[1] }
    }
    [pscustomobject]@{ ExitCode = $exitCode; Csproj = $csproj }
}

$fouten = [System.Collections.Generic.List[string]]::new()
foreach ($tier in $gevalNamen) {
    $shell = Invoke-ShellSide -Tier $tier
    $ps = Get-DatabaseTierProject -Tier $tier -RepoRoot $RepoRoot

    $verwachtExit = if (-not $ps.Found) { 1 } elseif (-not $ps.Built) { 2 } else { 0 }

    if ($shell.ExitCode -ne $verwachtExit) {
        $fouten.Add("Tier '$tier': shell-exitcode=$($shell.ExitCode), verwacht (o.b.v. PowerShell-kant: Found=$($ps.Found), Built=$($ps.Built))=$verwachtExit")
    }
    if ($verwachtExit -eq 0 -and $shell.Csproj -ne $ps.Csproj) {
        $fouten.Add("Tier '$tier': shell csproj='$($shell.Csproj)' vs PowerShell csproj='$($ps.Csproj)'")
    }
}

if ($fouten.Count -gt 0) {
    Write-Host "MISMATCH tussen resolve-database-tier.sh en Get-DatabaseTierProject:" -ForegroundColor Red
    foreach ($f in $fouten) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

Write-Host "OK: alle $($gevalNamen.Count) tier-waarden geven identieke uitkomst op beide kanten." -ForegroundColor Green
exit 0
