<#
.SYNOPSIS
    Herschrijft de git-geschiedenis om club-specifieke waarden te verwijderen.

.DESCRIPTION
    Dit script gebruikt git-filter-repo om alle club-specifieke infrastructuur-
    identifiers (Azure resourcenamen, URLs, Tenant-/Client-GUIDs) uit de volledige
    git-geschiedenis te vervangen door placeholders.

    Het script bevat GEEN hardcoded gevoelige waarden. Je voert de waarden in
    tijdens het uitvoeren, of je plaatst ze vooraf in een lokale omgevingsvariabele.

.NOTES
    VEREISTEN (eenmalig installeren):
      pip install git-filter-repo
      of: winget install Python.Python.3; pip install git-filter-repo

    WAARSCHUWINGEN:
      - Dit herschrijft de VOLLEDIGE git-geschiedenis — alle commit-SHAs veranderen.
      - Na uitvoeren MOET je force-pushen naar GitHub (--force-with-lease).
      - Open pull requests worden ongeldig; sluit ze en maak ze opnieuw aan.
      - Alle lokale klonen van andere developers worden stale — zij moeten opnieuw klonen.
      - Gitleaks commits-allowlist in .gitleaks.toml wordt leeg gezet (SHAs veranderen).

    PROCEDURE:
      1. Maak een lokale backup (stap 1 hieronder doet dit automatisch).
      2. Voer dit script uit — het vraagt om de gevoelige waarden.
      3. Verifieer het resultaat: git log --all -S "func-vrc" --oneline
         → geen output = alle waarden verwijderd.
      4. Force-push: git push --force-with-lease --all
                     git push --force-with-lease --tags
      5. Neem contact op met GitHub Support voor cache/GC-aanvraag.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RepoPath = (Get-Location).Path,
    [switch]$SkipBackup,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Kleurhulp ────────────────────────────────────────────────────────────────
function Write-Step  ([string]$msg) { Write-Host "`n[STAP] $msg" -ForegroundColor Cyan }
function Write-Ok    ([string]$msg) { Write-Host "  ✅  $msg" -ForegroundColor Green }
function Write-Warn  ([string]$msg) { Write-Host "  ⚠️  $msg" -ForegroundColor Yellow }
function Write-Err   ([string]$msg) { Write-Host "  ❌  $msg" -ForegroundColor Red }
function Write-Info  ([string]$msg) { Write-Host "  ℹ️  $msg" -ForegroundColor Gray }

# ── Veiligheidsbanner ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Red
Write-Host "  GIT HISTORY CLEANUP — DESTRUCTIEVE OPERATIE                  " -ForegroundColor Red
Write-Host "  Alle commit-SHAs in deze repository worden gewijzigd.        " -ForegroundColor Red
Write-Host "  Dit is ONOMKEERBAAR als de backup verloren gaat.             " -ForegroundColor Red
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Red
Write-Host ""

if (-not $Force) {
    $confirm = Read-Host "Bevestig door 'JA IK WIL DIT UITVOEREN' te typen"
    if ($confirm -ne 'JA IK WIL DIT UITVOEREN') {
        Write-Warn "Geannuleerd — geen wijzigingen gemaakt."
        exit 0
    }
}

# ── Stap 0: Vereisten controleren ────────────────────────────────────────────
Write-Step "0 — Vereisten controleren"

$gitFilterRepo = (Get-Command git-filter-repo -ErrorAction SilentlyContinue)
if (-not $gitFilterRepo) {
    # Probeer als Python module
    $pyCheck = & python -m git_filter_repo --version 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Err "git-filter-repo niet gevonden."
        Write-Info "Installeer met: pip install git-filter-repo"
        Write-Info "Of: py -m pip install git-filter-repo"
        exit 1
    }
    $filterRepoCmd = @('python', '-m', 'git_filter_repo')
} else {
    $filterRepoCmd = @('git-filter-repo')
}
Write-Ok "git-filter-repo beschikbaar"

if (-not (Test-Path (Join-Path $RepoPath '.git'))) {
    Write-Err "Geen git-repository gevonden op: $RepoPath"
    exit 1
}
Write-Ok "Repository gevonden: $RepoPath"

# Zorg dat de werkdirectory de repo-root is
Set-Location $RepoPath

# ── Stap 1: Backup aanmaken ──────────────────────────────────────────────────
Write-Step "1 — Backup aanmaken"

if ($SkipBackup) {
    Write-Warn "Backup overgeslagen (--SkipBackup opgegeven)"
} else {
    $backupName = "sportlink-wedstrijdzaken-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss').bundle"
    $backupPath = Join-Path $env:USERPROFILE $backupName
    Write-Info "Aanmaken: $backupPath"
    git bundle create $backupPath --all
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Backup aanmaken mislukt — script gestopt."
        exit 1
    }
    $size = [math]::Round((Get-Item $backupPath).Length / 1MB, 1)
    Write-Ok "Backup aangemaakt: $backupPath ($size MB)"
    Write-Info "Bewaar dit bestand totdat je zeker weet dat alles correct is."
}

# ── Stap 2: Gevoelige waarden ophalen ────────────────────────────────────────
Write-Step "2 — Gevoelige waarden opgeven"
Write-Info "Vul de EXACTE waarden in die uit de git-geschiedenis verwijderd moeten worden."
Write-Info "Lege invoer = waarde overslaan."
Write-Host ""

function Prompt-Value ([string]$label, [string]$placeholder, [string]$envVar = '') {
    $val = ''
    if ($envVar -and (Test-Path "env:$envVar")) {
        $val = (Get-Item "env:$envVar").Value
    }
    if (-not $val) {
        $val = Read-Host "  $label (placeholder wordt: $placeholder)"
    } else {
        Write-Info "  $label geladen uit `$$envVar"
    }
    return $val.Trim()
}

# Haal waarden op — ofwel uit omgevingsvariabelen, ofwel interactief
$vals = [ordered]@{
    FunctionAppNaam    = Prompt-Value "Azure Function App naam"     "[func-CLUB-sportlink]"  "CLEANUP_FUNC_APP"
    SwaSubdomein       = Prompt-Value "SWA uniek subdomain (het deel vóór .N.azurestaticapps.net)" "[swa-unique-id]" "CLEANUP_SWA_SUB"
    SwaFullUrl         = Prompt-Value "Volledige SWA-URL (inclusief .azurestaticapps.net)" "[swa-url].azurestaticapps.net" "CLEANUP_SWA_URL"
    TenantId           = Prompt-Value "Azure Tenant GUID (8-4-4-4-12 formaat)"  "[TENANT_ID]"   "CLEANUP_TENANT_ID"
    ClientId           = Prompt-Value "Azure Client GUID (8-4-4-4-12 formaat)"  "[CLIENT_ID]"   "CLEANUP_CLIENT_ID"
    SqlServer          = Prompt-Value "Azure SQL Server naam (zonder .database.windows.net)" "[sql-servernaam]" "CLEANUP_SQL_SERVER"
    StorageAccount     = Prompt-Value "Azure Storage Account naam"    "[storage-account]"        "CLEANUP_STORAGE"
    ClubDomein         = Prompt-Value "Club-domein (bijv. mijnclub.nl)"  "[club-domein]"         "CLEANUP_CLUB_DOMAIN"
    BeheerderLogin     = Prompt-Value "Beheerder-loginname"           "[beheerder]"              "CLEANUP_BEHEERDER"
    SwaName            = Prompt-Value "Azure SWA resource naam (swa-XXX-sportlink formaat)" "[swa-CLUB-sportlink]" "CLEANUP_SWA_NAME"
    AppInsights        = Prompt-Value "Application Insights naam"     "[ai-CLUB-sportlink]"  "CLEANUP_AI_NAME"
}

# Filter lege waarden eruit
$replacements = [ordered]@{}
foreach ($key in $vals.Keys) {
    if ($vals[$key]) { $replacements[$key] = $vals[$key] }
}

if ($replacements.Count -eq 0) {
    Write-Warn "Geen waarden opgegeven — niets te vervangen. Script gestopt."
    exit 0
}

Write-Host ""
Write-Host "  Te vervangen waarden:" -ForegroundColor Cyan
foreach ($key in $replacements.Keys) {
    Write-Host "    $key = $($replacements[$key].Substring(0, [Math]::Min(20, $replacements[$key].Length)))..." -ForegroundColor Gray
}

# ── Stap 3: Tijdelijk replacements-bestand aanmaken ─────────────────────────
Write-Step "3 — Replacements-bestand aanmaken (tijdelijk)"

$tempDir   = Join-Path $env:TEMP "git-cleanup-$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir | Out-Null
$replFile  = Join-Path $tempDir "replacements.txt"

# Placeholder-mapping  (exacte waarde ==> placeholder)
$lines = @()

# Azure Function App naam (ook als deel van URL)
if ($replacements.ContainsKey('FunctionAppNaam')) {
    $v = $replacements.FunctionAppNaam
    $lines += "literal:$v==>func-[clubcode]-sportlink"
    # URL variant
    $lines += "literal:$v.azurewebsites.net==>[func-url].azurewebsites.net"
}

# SWA uniek subdomain / URL
if ($replacements.ContainsKey('SwaSubdomein')) {
    $v = $replacements.SwaSubdomein
    $lines += "literal:$v==>[swa-unique-id]"
}
if ($replacements.ContainsKey('SwaFullUrl')) {
    $v = $replacements.SwaFullUrl
    $lines += "literal:$v==>[swa-url].azurestaticapps.net"
}

# SWA resource naam
if ($replacements.ContainsKey('SwaName')) {
    $v = $replacements.SwaName
    $lines += "literal:$v==>swa-[clubcode]-sportlink"
}

# Tenant ID (ook afgekorte variant: eerste 8 tekens + -)
if ($replacements.ContainsKey('TenantId')) {
    $v = $replacements.TenantId
    $lines += "literal:$v==>[TENANT_ID]"
    # Afgekorte variant (bijv. "74f2b2fe-...")
    $short = $v.Substring(0, 8)
    $lines += "literal:$short-...==>[TENANT_ID...]"
    $lines += "literal:api://$v==>api://[CLIENT_ID]"
}

# Client ID
if ($replacements.ContainsKey('ClientId')) {
    $v = $replacements.ClientId
    $lines += "literal:$v==>[CLIENT_ID]"
    $lines += "literal:api://$v==>api://[CLIENT_ID]"
}

# SQL Server
if ($replacements.ContainsKey('SqlServer')) {
    $v = $replacements.SqlServer
    $lines += "literal:$v==>[sql-servernaam]"
    $lines += "literal:$v.database.windows.net==>[sql-servernaam].database.windows.net"
}

# Storage Account
if ($replacements.ContainsKey('StorageAccount')) {
    $v = $replacements.StorageAccount
    $lines += "literal:$v==>[storage-account]"
}

# Club-domein
if ($replacements.ContainsKey('ClubDomein')) {
    $v = $replacements.ClubDomein
    $lines += "literal:$v==>[club-domein]"
}

# Beheerder-login
if ($replacements.ContainsKey('BeheerderLogin')) {
    $v = $replacements.BeheerderLogin
    $lines += "literal:$v==>[beheerder]"
}

# App Insights
if ($replacements.ContainsKey('AppInsights')) {
    $v = $replacements.AppInsights
    $lines += "literal:$v==>ai-[clubcode]-sportlink"
}

$lines | Set-Content -Path $replFile -Encoding UTF8
Write-Ok "Replacements-bestand aangemaakt: $replFile ($($lines.Count) regels)"
Write-Info "Inhoud:"
$lines | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }

# ── Stap 4: Fresh kloon in tijdelijke map ────────────────────────────────────
Write-Step "4 — Tijdelijke mirror-kloon aanmaken"
Write-Info "git-filter-repo vereist een 'fresh clone' (geen uncommitted wijzigingen of worktrees)"

$mirrorDir = Join-Path $tempDir "repo-mirror"
Write-Info "Klonen naar: $mirrorDir"

git clone --mirror $RepoPath $mirrorDir
if ($LASTEXITCODE -ne 0) {
    Write-Err "Klonen mislukt — script gestopt."
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Ok "Mirror-kloon aangemaakt"

# ── Stap 5: git-filter-repo uitvoeren ───────────────────────────────────────
Write-Step "5 — git-filter-repo uitvoeren (kan enkele minuten duren)"

Push-Location $mirrorDir
try {
    $cmd = $filterRepoCmd + @('--replace-text', $replFile, '--force')
    Write-Info "Commando: $($cmd -join ' ')"
    & $cmd[0] $cmd[1..($cmd.Length-1)]
    if ($LASTEXITCODE -ne 0) {
        Write-Err "git-filter-repo mislukt (exit $LASTEXITCODE) — script gestopt."
        exit 1
    }
    Write-Ok "git-filter-repo voltooid"
} finally {
    Pop-Location
}

# ── Stap 6: Resultaat verifiëren ────────────────────────────────────────────
Write-Step "6 — Verificatie: controleer of waarden nog aanwezig zijn"

$verifyOk = $true
Push-Location $mirrorDir
try {
    foreach ($key in $replacements.Keys) {
        $val = $replacements[$key]
        $hits = & git log --all -S "$val" --oneline 2>$null
        if ($hits) {
            Write-Err "WAARDE NOG AANWEZIG in git-geschiedenis: $key"
            Write-Info "Gevonden in: $($hits -join ', ')"
            $verifyOk = $false
        } else {
            Write-Ok "Niet meer aanwezig: $key"
        }
    }
} finally {
    Pop-Location
}

if (-not $verifyOk) {
    Write-Err "Verificatie mislukt — het resultaat is NIET teruggezet naar de originele repo."
    Write-Warn "Controleer het replacements-bestand en probeer opnieuw."
    Write-Info "Tijdelijke mirror staat nog in: $mirrorDir"
    exit 1
}

# ── Stap 7: Originele repo bijwerken ────────────────────────────────────────
Write-Step "7 — Originele repo bijwerken vanuit de gecleande mirror"

Push-Location $RepoPath
try {
    # Haal alle herschreven refs op vanuit de mirror
    git fetch $mirrorDir --force 'refs/heads/*:refs/heads/*' 'refs/tags/*:refs/tags/*'
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Fetch vanuit mirror mislukt — script gestopt."
        exit 1
    }
    Write-Ok "Herschreven commits opgehaald in lokale repo"
} finally {
    Pop-Location
}

# ── Stap 8: Gitleaks commits-allowlist leegmaken ────────────────────────────
Write-Step "8 — .gitleaks.toml commits-allowlist leegmaken (SHAs zijn gewijzigd)"

$gitleaksPath = Join-Path $RepoPath '.gitleaks.toml'
if (Test-Path $gitleaksPath) {
    $content = Get-Content $gitleaksPath -Raw
    # Vervang de commits-array door lege array
    $content = $content -replace 'commits\s*=\s*\[[^\]]*\]', 'commits = []'
    Set-Content $gitleaksPath $content -Encoding UTF8 -NoNewline
    Write-Ok ".gitleaks.toml commits-allowlist leeggemaakt"
} else {
    Write-Warn ".gitleaks.toml niet gevonden — overgeslagen"
}

# ── Stap 9: Opruimen ─────────────────────────────────────────────────────────
Write-Step "9 — Tijdelijke bestanden opruimen"

Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Ok "Tijdelijke map verwijderd: $tempDir"

# ── Eindrapport ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  ✅  GIT HISTORY CLEANUP VOLTOOID                             " -ForegroundColor Green
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "  Volgende stappen (VERPLICHT):" -ForegroundColor Yellow
Write-Host ""
Write-Host "  1. Force-push naar GitHub:" -ForegroundColor White
Write-Host "       git push --force-with-lease --all" -ForegroundColor Cyan
Write-Host "       git push --force-with-lease --tags" -ForegroundColor Cyan
Write-Host ""
Write-Host "  2. Verifieer op GitHub dat de gevoelige waarden weg zijn:" -ForegroundColor White
Write-Host "       Kijk in een oude commit via de GitHub UI" -ForegroundColor Cyan
Write-Host "       → de waarden moeten vervangen zijn door placeholders" -ForegroundColor Cyan
Write-Host ""
Write-Host "  3. Neem contact op met GitHub Support voor cache-invalidatie:" -ForegroundColor White
Write-Host "       https://support.github.com/contact" -ForegroundColor Cyan
Write-Host "       Vraag: 'Please run git garbage collection and invalidate" -ForegroundColor Cyan
Write-Host "       caches for repository Jaapbeus/Sportlink-wedstrijdzaken'" -ForegroundColor Cyan
Write-Host "       GitHub bewaart objecten 90 dagen na GC-aanvraag niet meer." -ForegroundColor Cyan
Write-Host ""
Write-Host "  4. Informeer andere developers dat zij opnieuw moeten klonen." -ForegroundColor White
Write-Host ""
Write-Host "  5. Backup bewaren tot je zeker weet dat alles correct werkt:" -ForegroundColor White
if (-not $SkipBackup) {
    Write-Host "       $backupPath" -ForegroundColor Cyan
}
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Green
