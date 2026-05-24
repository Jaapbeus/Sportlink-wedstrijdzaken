<#
.SYNOPSIS
    Verhoogt de 4e versiecomponent (build-teller) in beide .csproj-bestanden.

.PARAMETER NewPatch
    Verhoogt de patch-versie (3e component) en reset de build-teller naar 0.
    Synchroniseert ook <Version> en <FileVersion>.

.EXAMPLE
    .\scripts\dev\Bump-Build.ps1             # 2.2.0.4 → 2.2.0.5
    .\scripts\dev\Bump-Build.ps1 -NewPatch   # 2.2.0.5 → 2.2.1.0  (nieuwe functionaliteit)
#>
param([switch]$NewPatch)

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")

$projects = @(
    (Join-Path $root "FunctionApp\fa-dev-sportlink-01.csproj"),
    (Join-Path $root "BlazorAdmin\BlazorAdmin.csproj")
)

$newVersionLabel = $null

foreach ($proj in $projects) {
    $content = Get-Content $proj -Raw -Encoding UTF8

    if ($content -notmatch '<AssemblyVersion>(\d+)\.(\d+)\.(\d+)\.(\d+)</AssemblyVersion>') {
        Write-Error "Geen <AssemblyVersion> gevonden in $proj"
        exit 1
    }

    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3]
    $build = [int]$Matches[4]

    if ($NewPatch) {
        $patch++
        $build = 0
    } else {
        $build++
    }

    $newAssembly = "$major.$minor.$patch.$build"
    $newSemver   = "$major.$minor.$patch"

    $content = $content -replace '<AssemblyVersion>\d+\.\d+\.\d+\.\d+</AssemblyVersion>', "<AssemblyVersion>$newAssembly</AssemblyVersion>"
    $content = $content -replace '<FileVersion>\d+\.\d+\.\d+\.\d+</FileVersion>',         "<FileVersion>$newAssembly</FileVersion>"

    if ($NewPatch) {
        $content = $content -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$newSemver</Version>"
    }

    # Bewaar zonder BOM (UTF-8 zonder BOM, zoals de originelen)
    [System.IO.File]::WriteAllText((Resolve-Path $proj).Path, $content, [System.Text.UTF8Encoding]::new($false))

    $newVersionLabel = $newAssembly
    Write-Host "✓ $proj  →  $newAssembly"
}

Write-Host ""
Write-Host "┌─────────────────────────────────────┐" -ForegroundColor Cyan
Write-Host "│  Build versie:  $newVersionLabel$((' ' * (20 - $newVersionLabel.Length)))│" -ForegroundColor Cyan
Write-Host "└─────────────────────────────────────┘" -ForegroundColor Cyan
Write-Host ""
Write-Host "Volgende stap: dotnet build + .\scripts\dev\Start-Debug.ps1" -ForegroundColor DarkGray
