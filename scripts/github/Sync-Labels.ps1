<#
.SYNOPSIS
    Synchroniseert GitHub labels met de definitie in .github/labels.json.

.DESCRIPTION
    - Maakt ontbrekende labels aan
    - Updatet bestaande labels met afwijkende kleur of beschrijving
    - Verwijdert verouderde labels (optioneel, met -Prune)
    - Idempotent: veilig om meerdere keren uit te voeren

.PARAMETER Prune
    Verwijder labels die NIET in .github/labels.json staan.
    Standaard: $false (alleen aanmaken/updaten).

.PARAMETER WhatIf
    Toon wat er zou gebeuren zonder iets te wijzigen.

.EXAMPLE
    .\scripts\github\Sync-Labels.ps1
    .\scripts\github\Sync-Labels.ps1 -Prune
    .\scripts\github\Sync-Labels.ps1 -WhatIf

.NOTES
    Vereist: gh CLI, ingelogd via 'gh auth login'
    Labels definitie: .github/labels.json
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Prune
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Configuratie ─────────────────────────────────────────────────────────────

$repo = (gh repo view --json nameWithOwner -q .nameWithOwner 2>$null)
if (-not $repo) { throw "Niet ingelogd bij GitHub of repository niet gevonden. Voer 'gh auth login' uit." }

$labelsFile = Join-Path $PSScriptRoot "..\..\\.github\labels.json"
if (-not (Test-Path $labelsFile)) { throw "Definitiebestand niet gevonden: $labelsFile" }

$desired = Get-Content $labelsFile -Raw | ConvertFrom-Json

# ── Huidige labels ophalen ────────────────────────────────────────────────────

Write-Host "`n📋 Repository: $repo" -ForegroundColor Cyan
Write-Host "📄 Labels definitie: $labelsFile`n" -ForegroundColor Cyan

$currentJson = gh label list --repo $repo --limit 100 --json name,color,description 2>$null
$current = $currentJson | ConvertFrom-Json
$currentMap = @{}
foreach ($l in $current) { $currentMap[$l.name] = $l }

# ── Verouderde labels voor migratie ──────────────────────────────────────────
# Mapping: oude naam → nieuwe naam (voor issues die al het oude label hebben)
$migrations = @{
    'prioriteit: hoog'              = 'priority: high'
    'prioriteit: normaal'           = 'priority: medium'
    'prioriteit: laag'              = 'priority: low'
    'type: verbetering'             = 'type: feature'
    'type: documentatie'            = 'type: docs'
    'type: beveiliging'             = 'type: security'
    'security'                      = 'type: security'
    'blocker: productie'            = 'priority: critical'
    'wacht op: eigenaar'            = 'status: waiting-owner'
    'via-feedback-widget'           = 'via: feedback-widget'
    'kapstok'                       = 'epic'
    'architectuur'                  = 'discipline: architect'
    'privacy'                       = 'type: security'
    'review'                        = 'status: review-needed'
    'discipline: senior-dev'        = 'type: chore'
}

# Labels die verwijderd worden zonder vervanging (verouderde fase-labels etc.)
$obsolete = @(
    'uitgesteld', 'soak-test',
    'fase: 1-database', 'fase: 2-services', 'fase: 3-api',
    'fase: 4-wasm-setup', 'fase: 5-schermen', 'fase: 6-email-tester',
    'fase: 7-auth', 'fase: 8-cicd', 'fase: pre-version',
    'fase: 2.1-error-reporting', 'fase: 2.1-auto-heal', 'fase: 2.1-feature-form',
    'fase: pre-v2.1', 'wontfix'
)

$created = 0; $updated = 0; $migrated = 0; $pruned = 0; $skipped = 0

# ── Stap 1: Gewenste labels aanmaken of updaten ───────────────────────────────

Write-Host "── Stap 1: Aanmaken / updaten ──────────────────────────────────────" -ForegroundColor Yellow
foreach ($label in $desired) {
    $name  = $label.name
    $color = $label.color -replace '^#', ''
    $desc  = $label.description

    if ($currentMap.ContainsKey($name)) {
        $cur = $currentMap[$name]
        $curColor = $cur.color -replace '^#', ''
        if ($curColor -ne $color -or $cur.description -ne $desc) {
            if ($PSCmdlet.ShouldProcess($repo, "Update label '$name'")) {
                gh label edit $name --repo $repo --color $color --description $desc 2>$null
                Write-Host "  ✏️  Updated  : $name" -ForegroundColor Blue
                $updated++
            }
        } else {
            Write-Host "  ✅ Unchanged: $name" -ForegroundColor DarkGray
            $skipped++
        }
    } else {
        if ($PSCmdlet.ShouldProcess($repo, "Create label '$name'")) {
            gh label create $name --repo $repo --color $color --description $desc 2>$null
            Write-Host "  ➕ Created  : $name" -ForegroundColor Green
            $created++
        }
    }
}

# ── Stap 2: Migraties (hernoemen + issues bijwerken) ─────────────────────────

Write-Host "`n── Stap 2: Migraties (oude → nieuwe labels) ────────────────────────" -ForegroundColor Yellow
foreach ($old in $migrations.Keys) {
    $new = $migrations[$old]
    if ($currentMap.ContainsKey($old)) {
        Write-Host "  🔄 Migreren : '$old' → '$new'" -ForegroundColor Magenta
        if ($PSCmdlet.ShouldProcess($repo, "Migrate label '$old' to '$new'")) {
            # Hernoem het label naar de nieuwe naam (GitHub API)
            gh api --method PATCH "repos/$repo/labels/$([uri]::EscapeDataString($old))" `
                --field "name=$new" 2>$null
            Write-Host "     ↳ Issues bijgewerkt (alle issues behouden het label automatisch na hernoemen)" -ForegroundColor DarkGray
            $migrated++
        }
    }
}

# ── Stap 3: Verouderde labels verwijderen (met -Prune) ───────────────────────

if ($Prune) {
    Write-Host "`n── Stap 3: Verouderde labels verwijderen (-Prune) ──────────────────" -ForegroundColor Yellow

    # Herlaad huidige labels na migraties
    $currentJson = gh label list --repo $repo --limit 100 --json name -q '.[].name' 2>$null
    $currentNames = $currentJson -split "`n" | Where-Object { $_ }
    $desiredNames = $desired | ForEach-Object { $_.name }

    foreach ($name in $currentNames) {
        $isDesired   = $desiredNames -contains $name
        $isMigrated  = $migrations.Values -contains $name -or $migrations.Keys -contains $name
        $isObsolete  = $obsolete -contains $name

        if ((-not $isDesired) -and ($isObsolete -or -not $isMigrated)) {
            if ($PSCmdlet.ShouldProcess($repo, "Delete label '$name'")) {
                gh label delete $name --repo $repo --yes 2>$null
                Write-Host "  🗑️  Deleted  : $name" -ForegroundColor Red
                $pruned++
            }
        }
    }
}

# ── Samenvatting ──────────────────────────────────────────────────────────────

Write-Host "`n── Resultaat ────────────────────────────────────────────────────────" -ForegroundColor Cyan
Write-Host "  ➕ Aangemaakt : $created"    -ForegroundColor Green
Write-Host "  ✏️  Bijgewerkt : $updated"   -ForegroundColor Blue
Write-Host "  🔄 Gemigreerd : $migrated"   -ForegroundColor Magenta
Write-Host "  🗑️  Verwijderd : $pruned"    -ForegroundColor Red
Write-Host "  ✅ Ongewijzigd: $skipped"    -ForegroundColor DarkGray

if (-not $Prune -and $pruned -eq 0) {
    Write-Host "`n  ℹ️  Verouderde labels nog aanwezig. Voer uit met -Prune om ze te verwijderen." -ForegroundColor Yellow
}

Write-Host "`nLabel-sync voltooid voor $repo`n" -ForegroundColor Cyan
