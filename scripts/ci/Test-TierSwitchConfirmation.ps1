<#
.SYNOPSIS
    Bewijst dat resolve-database-tier.sh de DatabaseTierSwitchConfirmation-veiligheidscheck (#976)
    daadwerkelijk afdwingt: een ontbrekende of niet-matchende bevestiging faalt hard (exitcode 3),
    een matchende bevestiging blokkeert niets.

.DESCRIPTION
    Los van Test-TierMappingConsistency.ps1 (#865), dat de tier-naamresolutie zelf toetst en de
    confirmation-check bewust neutraliseert door 'm altijd te laten matchen. Dit script toetst
    precies het omgekeerde: dat de check ook echt blokkeert wanneer hij hoort te blokkeren.
    Draait zonder database en zonder secrets — ook bruikbaar op een fork.

.EXAMPLE
    pwsh scripts/ci/Test-TierSwitchConfirmation.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = (Get-Item (Join-Path $PSScriptRoot '..' '..')).FullName

$bashCmd = Get-Command bash -ErrorAction SilentlyContinue
if (-not $bashCmd) {
    Write-Host "SKIP: 'bash' niet gevonden op PATH — deze test kan alleen daar draaien waar het" -ForegroundColor Yellow
    Write-Host "bash-script zelf ook draait (CI: ubuntu-latest; lokaal: Git Bash/WSL)." -ForegroundColor Yellow
    exit 0
}

# Zelfde WSL-launcher-valkuil als Test-TierMappingConsistency.ps1 (#865): de WSL-bash-launcher
# vertaalt een los Windows-pad niet automatisch naar /mnt/<letter>/...
$isWslLauncher = $IsWindows -and ($bashCmd.Source -match '\\System32\\bash\.exe$')

function ConvertTo-ShellPath {
    param([string]$WindowsPath)
    if (-not $isWslLauncher) { return $WindowsPath }
    $full = (Resolve-Path $WindowsPath).Path
    $drive = $full.Substring(0, 1).ToLowerInvariant()
    $rest = $full.Substring(2) -replace '\\', '/'
    return "/mnt/$drive$rest"
}

$resolverShellPath = ConvertTo-ShellPath (Join-Path $RepoRoot 'scripts' 'ci' 'resolve-database-tier.sh')

function Invoke-Resolver {
    param([string]$Tier, [string]$Confirmation)
    $env:DatabaseTier = $Tier
    if ($null -ne $Confirmation) { $env:DatabaseTierSwitchConfirmation = $Confirmation }
    else { Remove-Item Env:\DatabaseTierSwitchConfirmation -ErrorAction SilentlyContinue }
    if ($isWslLauncher) { $env:WSLENV = 'DatabaseTier,DatabaseTierSwitchConfirmation' }
    try {
        $output = & bash $resolverShellPath 2>&1
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }
    finally {
        Remove-Item Env:\DatabaseTier -ErrorAction SilentlyContinue
        Remove-Item Env:\DatabaseTierSwitchConfirmation -ErrorAction SilentlyContinue
        Remove-Item Env:\WSLENV -ErrorAction SilentlyContinue
    }
}

$fouten = [System.Collections.Generic.List[string]]::new()

# Geval 1: geen confirmation-variabele gezet -> moet blokkeren (exit 3), niet stil doorlopen.
$r1 = Invoke-Resolver -Tier 'SqlServer' -Confirmation $null
if ($r1.ExitCode -ne 3) {
    $fouten.Add("Geval 'geen confirmation': verwacht exitcode 3, kreeg $($r1.ExitCode). Output: $($r1.Output)")
}

# Geval 2: confirmation gezet maar voor een ANDERE tier -> moet blokkeren (exit 3).
$r2 = Invoke-Resolver -Tier 'SqlServer' -Confirmation 'Postgres'
if ($r2.ExitCode -ne 3) {
    $fouten.Add("Geval 'niet-matchende confirmation': verwacht exitcode 3, kreeg $($r2.ExitCode). Output: $($r2.Output)")
}

# Geval 3: confirmation matcht exact -> mag NIET op de confirmation-check blokkeren (exit 3 komt
# niet voor); voor de bestaande, gebouwde tier 'SqlServer' hoort dat gewoon exit 0 te zijn.
$r3 = Invoke-Resolver -Tier 'SqlServer' -Confirmation 'SqlServer'
if ($r3.ExitCode -ne 0) {
    $fouten.Add("Geval 'matchende confirmation': verwacht exitcode 0 (SqlServer is gebouwd), kreeg $($r3.ExitCode). Output: $($r3.Output)")
}

if ($fouten.Count -gt 0) {
    Write-Host "Tier-switch-confirmation-check werkt niet zoals verwacht:" -ForegroundColor Red
    foreach ($f in $fouten) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

Write-Host "OK: DatabaseTierSwitchConfirmation blokkeert bij ontbreken/mismatch, laat door bij match." -ForegroundColor Green
exit 0
