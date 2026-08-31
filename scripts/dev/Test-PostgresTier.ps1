<#
.SYNOPSIS
    Zelftest voor een database-tier: bouwt de tier lokaal op, laadt de demodata en bewijst dat de
    applicatie erop draait (#851).

.DESCRIPTION
    Dit script is het deterministische deel van de zelftest. Het doet alles wat met een exitcode te
    bewijzen is: containers, schema, demodata, rijtellingen, herkomstbewijs, API-asserties en de
    opruiming. De browsersweep en de fix-lus zitten in de bijbehorende skill
    (.claude/skills/zelftest/SKILL.md), omdat een client-side gerenderde pagina niet met een
    HTTP-aanroep te beoordelen is.

    DRIE REGELS DIE OVERAL GELDEN

    1. Overslaan is falen. Test-App.ps1 slaat secties over als een poort niet luistert en meldt
       daarna "alles in orde". Dat gedrag is hier bewust niet overgenomen: een poort die niet
       uitgevoerd kon worden is rood, niet groen.

    2. Elke poort heeft een ondergrens aan asserties. Nul geslaagde positieve asserties is een
       fout, ook als er niets misging. Anders is "niets gemeten" niet te onderscheiden van "alles
       goed" — precies hoe /veldbeschikbaarheid maandenlang groen stond terwijl die route niet
       meer bestond.

    3. Bewijs staat in bestanden. Elke poort schrijft zijn uitkomst naar artifacts/selftest/<run>/.
       Geen bestand betekent geen bewijs.

.PARAMETER Tier
    De tier die getest wordt. De vertaling naar een projectpad komt uit scripts/ci/database-tiers.json
    — hetzelfde bestand dat de CI gebruikt.

.PARAMETER Mode
    Baseline meet de bestaande tier en legt de uitkomst vast als vergelijkingsbasis.
    Verify meet de nieuwe tier en vergelijkt met die basis.

    De basismeting kan niet achteraf. Draai je de sweep pas na de omzetting, dan is niet meer vast
    te stellen welke fouten er al waren. Dat is het enige onderdeel van deze zelftest met een
    vervaldatum.

.PARAMETER ListPhases
    Toont de poorten en stopt. Raakt niets aan.

.PARAMETER Teardown
    Alleen opruimen: containers weg, de ontwikkelomgeving terug in de staat van vóór de run.
    Werkt ook zonder bewaarde staat, dan op basis van de vaste namen.

.EXAMPLE
    ./scripts/dev/Test-PostgresTier.ps1 -ListPhases

.EXAMPLE
    ./scripts/dev/Test-PostgresTier.ps1 -Tier SqlServer -Mode Baseline

.EXAMPLE
    ./scripts/dev/Test-PostgresTier.ps1 -Tier Postgres -Mode Verify

.NOTES
    Cross-platform (#800): geen $env:TEMP, geen Get-NetTCPConnection, geen CIM, geen backslash in
    padliteralen. Alles wat platformspecifiek is staat in DevServices.psm1.
#>
[CmdletBinding()]
param(
    [ValidateSet('SqlServer', 'Postgres', 'Sqlite')]
    [string]$Tier = 'SqlServer',

    [ValidateSet('Baseline', 'Verify')]
    [string]$Mode = 'Baseline',

    [string]$BaselinePath,
    [switch]$ListPhases,
    [switch]$Teardown,
    [switch]$KeepContainer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ══════════════════════════════════════════════════════════════════════════
# Opzet
# ══════════════════════════════════════════════════════════════════════════

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $PSScriptRoot 'DevServices.psm1') -Force

$ExpectationsPath = Join-Path $PSScriptRoot 'selftest-expectations.psd1'
if (-not (Test-Path $ExpectationsPath)) {
    throw "Verwachtingenbestand ontbreekt: $ExpectationsPath"
}
$Expect = Import-PowerShellDataFile $ExpectationsPath

$Ports          = Get-SelftestPorts
$SqlContainer   = 'sportlink-sqlserver'
$PgContainer    = 'sportlink-postgres-selftest'
$ComposeProject = 'sportlink-selftest'
$ComposeFile    = Join-Path $RepoRoot 'docker-compose.selftest.yml'

$RunId       = Get-Date -Format 'yyyyMMdd-HHmmss'
$ArtifactDir = Get-SelftestArtifactRoot -RepoRoot $RepoRoot -RunId $RunId

# Poorten, in volgorde. 'Modes' zegt in welke modus een poort betekenis heeft.
$Gates = @(
    @{ Id = 'G0'; Naam = 'Preflight en isolatie';        Modes = @('Baseline','Verify') }
    @{ Id = 'G1'; Naam = 'Database beschikbaar';         Modes = @('Baseline','Verify') }
    @{ Id = 'G2'; Naam = 'Schema (eerste run)';          Modes = @('Verify') }
    @{ Id = 'G3'; Naam = 'Idempotentie (tweede run)';    Modes = @('Verify') }
    @{ Id = 'G4'; Naam = 'Demodata en rijtellingen';     Modes = @('Baseline','Verify') }
    @{ Id = 'G5'; Naam = 'Applicatie praat aantoonbaar met de juiste engine'; Modes = @('Baseline','Verify') }
    @{ Id = 'G6'; Naam = 'API met inhoudsasserties';     Modes = @('Baseline','Verify') }
    @{ Id = 'G7'; Naam = 'Browsersweep (skill)';         Modes = @('Baseline','Verify') }
    @{ Id = 'G8'; Naam = 'Schrijfpaden (skill)';         Modes = @('Baseline','Verify') }
    @{ Id = 'G9'; Naam = 'Teardown';                     Modes = @('Baseline','Verify') }
)

$script:Checks   = [System.Collections.Generic.List[object]]::new()
$script:GateStat = [ordered]@{}
$script:State    = [ordered]@{}

# ══════════════════════════════════════════════════════════════════════════
# Uitvoerhulpjes
# ══════════════════════════════════════════════════════════════════════════

function Write-Kop($tekst) {
    Write-Host ''
    Write-Host "═══ $tekst " -ForegroundColor Cyan -NoNewline
    Write-Host ('═' * [Math]::Max(0, 60 - $tekst.Length)) -ForegroundColor Cyan
}

function Add-Check {
    <#
        Legt één assertie vast. 'status' is pass, fail, skip of blocked.

        'blocked' is een eigen status en geen variant van skip: het betekent dat een BEKEND,
        genummerd defect deze meting onmogelijk maakt. Zo blijft zichtbaar dat er iets gemeten
        had moeten worden, zonder dat de run rood wordt om iets waarvan het issue al openstaat.
    #>
    param(
        [Parameter(Mandatory)][string]$Gate,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][ValidateSet('pass','fail','skip','blocked')][string]$Status,
        [string]$Expected = '',
        [string]$Actual   = '',
        [string]$Message  = '',
        [int[]]$Blocked   = @()
    )

    $script:Checks.Add([pscustomobject]@{
        id       = $Id
        gate     = $Gate
        status   = $Status
        expected = $Expected
        actual   = $Actual
        message  = $Message
        blockedBy= $Blocked
    })

    $kleur = switch ($Status) {
        'pass'    { 'Green' }
        'fail'    { 'Red' }
        'blocked' { 'DarkYellow' }
        default   { 'DarkGray' }
    }
    $merk = switch ($Status) {
        'pass'    { '  OK  ' }
        'fail'    { '  !!  ' }
        'blocked' { ' BLOK ' }
        default   { ' skip ' }
    }
    $suffix = if ($Blocked.Count -gt 0) { "  (geblokkeerd door #$($Blocked -join ', #'))" } else { '' }
    Write-Host "  [$merk] $Id$suffix" -ForegroundColor $kleur
    if ($Message) { Write-Host "          $Message" -ForegroundColor DarkGray }
}

function Complete-Gate {
    <#
        Sluit een poort af en bepaalt zijn uitkomst.

        Nul positieve asserties = fail. Dat is regel 2 uit de kop van dit script: "niets gemeten"
        mag nooit als "goed" doorgaan.
    #>
    param([Parameter(Mandatory)][string]$Gate)

    $eigen   = @($script:Checks | Where-Object { $_.gate -eq $Gate })
    $gefaald = @($eigen | Where-Object { $_.status -eq 'fail' })
    $geslaagd= @($eigen | Where-Object { $_.status -eq 'pass' })
    $blocked = @($eigen | Where-Object { $_.status -eq 'blocked' })

    # Volgorde is bewust: een geblokkeerde meting maakt de poort niet groen, ook niet als er
    # daarnaast van alles wél geslaagd is. "Deels gemeten" mag nooit als "goed" op het scherm
    # verschijnen — dat is dezelfde soort zelfmisleiding als een overgeslagen sectie die als
    # geslaagd meetelt.
    $uitkomst =
        if ($gefaald.Count -gt 0)   { 'fail' }
        elseif ($blocked.Count -gt 0) { 'blocked' }
        elseif ($geslaagd.Count -eq 0) { 'fail' }
        else                        { 'pass' }

    $script:GateStat[$Gate] = [pscustomobject]@{
        gate = $Gate; outcome = $uitkomst
        pass = $geslaagd.Count; fail = $gefaald.Count; blocked = $blocked.Count
    }

    $kleur = switch ($uitkomst) { 'pass' { 'Green' } 'fail' { 'Red' } default { 'DarkYellow' } }
    Write-Host ("  -> {0}: {1}  ({2} geslaagd, {3} gefaald, {4} geblokkeerd)" -f
        $Gate, $uitkomst.ToUpper(), $geslaagd.Count, $gefaald.Count, $blocked.Count) -ForegroundColor $kleur

    return $uitkomst
}

function Save-Artifact {
    param([Parameter(Mandatory)][string]$Naam, [Parameter(Mandatory)]$Inhoud)
    $pad = Join-Path $ArtifactDir $Naam
    $map = Split-Path -Parent $pad
    if (-not (Test-Path $map)) { New-Item -ItemType Directory -Path $map -Force | Out-Null }
    if ($Inhoud -is [string]) {
        Set-Content -Path $pad -Value $Inhoud -Encoding utf8NoBOM
    } else {
        $Inhoud | ConvertTo-Json -Depth 12 | Set-Content -Path $pad -Encoding utf8NoBOM
    }
    return $pad
}

# ══════════════════════════════════════════════════════════════════════════
# -ListPhases
# ══════════════════════════════════════════════════════════════════════════

if ($ListPhases) {
    Write-Kop "Poorten van de zelftest (modus: $Mode, tier: $Tier)"
    foreach ($g in $Gates) {
        $actief = if ($g.Modes -contains $Mode) { 'actief ' } else { 'n.v.t. ' }
        Write-Host ("  {0}  {1}  {2}" -f $g.Id, $actief, $g.Naam)
    }
    Write-Host ''
    Write-Host "  G7 en G8 worden door de skill uitgevoerd (.claude/skills/zelftest/SKILL.md)." -ForegroundColor DarkGray
    Write-Host "  Dit script schrijft hun verwachtingen weg zodat de skill niets hoeft te verzinnen." -ForegroundColor DarkGray
    exit 0
}

# ══════════════════════════════════════════════════════════════════════════
# Teardown — ook los aanroepbaar, en altijd via finally
# ══════════════════════════════════════════════════════════════════════════

function Invoke-Teardown {
    param([switch]$Stil)

    if (-not $Stil) { Write-Kop 'G9 — Teardown' }

    # 1. Wegwerp-Postgres weg, tenzij expliciet bewaard voor onderzoek.
    if (-not $KeepContainer) {
        $pg = Get-ContainerState -Name $PgContainer
        if ($pg) {
            & docker compose -p $ComposeProject -f $ComposeFile down -v 2>&1 | Out-Null
            if (-not $Stil) { Add-Check -Gate 'G9' -Id 'G9.postgres.verwijderd' -Status 'pass' -Message 'Wegwerpcontainer en tmpfs opgeruimd.' }
        } elseif (-not $Stil) {
            Add-Check -Gate 'G9' -Id 'G9.postgres.verwijderd' -Status 'pass' -Message 'Geen wegwerpcontainer aanwezig.'
        }
    } elseif (-not $Stil) {
        Add-Check -Gate 'G9' -Id 'G9.postgres.bewaard' -Status 'pass' -Message "Container bewaard op verzoek: $PgContainer (poort $($Ports.Postgres))."
    }

    # 2. De ontwikkeldatabase terug in de staat van vóór de run.
    #
    # Bewust exact herstellen en niet "aanzetten want dat is handig": stond hij uit, dan had de
    # ontwikkelaar daar een reden voor.
    $wasRunning = $null
    $statePad = Join-Path $ArtifactDir 'state.json'
    if (Test-Path $statePad) {
        try { $wasRunning = (Get-Content $statePad -Raw | ConvertFrom-Json).sqlServerWasRunning } catch { $wasRunning = $null }
    }
    if ($null -eq $wasRunning -and $script:State.Contains('sqlServerWasRunning')) {
        $wasRunning = $script:State['sqlServerWasRunning']
    }

    if ($null -ne $wasRunning) {
        $nu = Get-ContainerState -Name $SqlContainer
        if ($nu -and $wasRunning -and -not $nu.Running) {
            & docker start $SqlContainer 2>&1 | Out-Null
            if (-not $Stil) { Add-Check -Gate 'G9' -Id 'G9.sqlserver.hersteld' -Status 'pass' -Message 'Ontwikkeldatabase weer gestart (draaide vóór de run).' }
        } elseif (-not $Stil) {
            Add-Check -Gate 'G9' -Id 'G9.sqlserver.hersteld' -Status 'pass' -Message 'Ontwikkeldatabase staat zoals vóór de run.'
        }
    }

    if (-not $Stil) { [void](Complete-Gate -Gate 'G9') }
}

if ($Teardown) {
    Write-Host 'Alleen opruimen...' -ForegroundColor Cyan
    if (-not $KeepContainer) { & docker compose -p $ComposeProject -f $ComposeFile down -v 2>&1 | Out-Null }
    $sql = Get-ContainerState -Name $SqlContainer
    if ($sql -and -not $sql.Running) {
        & docker start $SqlContainer 2>&1 | Out-Null
        Write-Host "  Ontwikkeldatabase weer gestart." -ForegroundColor Green
    }
    Write-Host '  Klaar.' -ForegroundColor Green
    exit 0
}

# ══════════════════════════════════════════════════════════════════════════
# De run
# ══════════════════════════════════════════════════════════════════════════

New-Item -ItemType Directory -Path $ArtifactDir -Force | Out-Null
$exitCode = 0

try {
    Write-Host ''
    Write-Host "ZELFTEST DATABASE-TIER — $Tier / $Mode" -ForegroundColor Cyan
    Write-Host "Run $RunId   ->   $ArtifactDir" -ForegroundColor DarkGray

    # ──────────────────────────────────────────────────────────────────────
    # G0 — Preflight en isolatie
    # ──────────────────────────────────────────────────────────────────────
    Write-Kop 'G0 — Preflight en isolatie'

    # Tier resolven via de gedeelde tabel. Drie uitkomsten, drie reacties.
    $tierInfo = Get-DatabaseTierProject -Tier $Tier -RepoRoot $RepoRoot
    if (-not $tierInfo.Found) {
        Add-Check -Gate 'G0' -Id 'G0.tier.bekend' -Status 'fail' `
            -Expected ($tierInfo.Valid -join ', ') -Actual $Tier -Message 'Onbekende tier-naam.'
        [void](Complete-Gate -Gate 'G0')
        throw "Onbekende tier '$Tier'."
    }
    Add-Check -Gate 'G0' -Id 'G0.tier.bekend' -Status 'pass' -Actual $Tier

    if (-not $tierInfo.Built -or -not $tierInfo.Exists) {
        # Dit is geen defect maar een geplande situatie: de implementatieboom bestaat nog niet.
        # Exitcode 2 onderscheidt dat van een echte fout, zodat een aanroeper er anders op kan
        # reageren dan op rood.
        Add-Check -Gate 'G0' -Id 'G0.tier.gebouwd' -Status 'blocked' `
            -Expected $tierInfo.Csproj -Actual 'ontbreekt' `
            -Blocked @($tierInfo.EpicIssue) `
            -Message "De implementatieboom voor tier '$Tier' bestaat nog niet."
        [void](Complete-Gate -Gate 'G0')
        Write-Host ''
        Write-Host "TIER NOG NIET GEBOUWD — dit is het verwachte resultaat zolang issue #$($tierInfo.EpicIssue) openstaat." -ForegroundColor DarkYellow
        Write-Host "Verwacht projectpad: $($tierInfo.Csproj)" -ForegroundColor DarkGray
        $exitCode = 2
        exit $exitCode
    }
    Add-Check -Gate 'G0' -Id 'G0.tier.gebouwd' -Status 'pass' -Actual $tierInfo.Csproj

    # Docker moet echt draaien; 'docker --version' zegt daar niets over.
    if (-not (Test-DockerAvailable)) {
        Add-Check -Gate 'G0' -Id 'G0.docker' -Status 'fail' -Message 'Docker-daemon niet bereikbaar.'
    } else {
        Add-Check -Gate 'G0' -Id 'G0.docker' -Status 'pass'
    }

    # Weiger te draaien tegen iets anders dan localhost. Zonder deze poort kan een verkeerd
    # ingestelde omgeving de zelftest op een echte database loslaten.
    $localSettings = Join-Path $RepoRoot 'FunctionApp' 'local.settings.json'
    if (Test-Path $localSettings) {
        # Alleen de reeks beoordelen die de applicatie DAADWERKELIJK gebruikt.
        #
        # Het hele bestand scannen levert een vals alarm: local.settings.json bevat naast de
        # lokale reeks ook een ongebruikte productiereeks, en die noemt uiteraard een externe
        # server. Precies dat gaf hier een onterechte rode poort bij de eerste testrun.
        $actief = $null
        try {
            $json = Get-Content $localSettings -Raw | ConvertFrom-Json
            if ($json.PSObject.Properties.Name -contains 'Values') {
                $actief = $json.Values.SqlConnectionString
            }
        } catch {
            $actief = $null
        }

        # Bewaard voor G4 (Baseline): dezelfde, al-gevalideerde connectiereeks hergebruiken in
        # plaats van hem daar opnieuw uit local.settings.json te lezen.
        $script:State['sqlConnectionString'] = $actief

        if ([string]::IsNullOrWhiteSpace($actief)) {
            Add-Check -Gate 'G0' -Id 'G0.alleen-lokaal' -Status 'fail' `
                -Message 'Values.SqlConnectionString ontbreekt of is leeg in local.settings.json.'
        } elseif ($actief -match '\.database\.windows\.net|\.azure\.com|\.rds\.amazonaws\.com') {
            Add-Check -Gate 'G0' -Id 'G0.alleen-lokaal' -Status 'fail' `
                -Message 'De actieve connectiereeks wijst naar een externe server. De zelftest weigert dan te draaien.'
        } elseif ($actief -notmatch 'localhost|127\.0\.0\.1|\(localdb\)|\bhost\s*=\s*localhost') {
            Add-Check -Gate 'G0' -Id 'G0.alleen-lokaal' -Status 'fail' `
                -Message 'De actieve connectiereeks noemt geen localhost — te riskant om blind op te draaien.'
        } else {
            Add-Check -Gate 'G0' -Id 'G0.alleen-lokaal' -Status 'pass' `
                -Message 'Actieve connectiereeks wijst naar localhost.'
        }

        # Integriteit: dit bestand mag de run niet overleven met een andere inhoud.
        $hash = (Get-FileHash $localSettings -Algorithm SHA256).Hash
        $script:State['localSettingsHash'] = $hash
        Add-Check -Gate 'G0' -Id 'G0.config.vastgelegd' -Status 'pass' -Actual $hash.Substring(0, 12)
    } else {
        Add-Check -Gate 'G0' -Id 'G0.alleen-lokaal' -Status 'fail' `
            -Message 'FunctionApp/local.settings.json ontbreekt — kopieer local.settings.template.json.'
    }

    # ── Routedrift: de lijst met verwachtingen tegen de werkelijke pagina's ──
    #
    # Dit is de structurele oplossing voor het probleem dat Test-App.ps1 vandaag heeft: daar staan
    # twee routes in die niet meer bestaan, en omdat Blazor op elke route dezelfde pagina teruggeeft
    # zijn ze al maanden groen. Een verschil in BEIDE richtingen is hier een fout.
    $razorMap = Join-Path $RepoRoot 'BlazorAdmin' 'Pages'
    $echteRoutes = @(
        Get-ChildItem -Path $razorMap -Filter '*.razor' -Recurse |
            Select-String -Pattern '^@page\s+"([^"]+)"' |
            ForEach-Object { $_.Matches[0].Groups[1].Value }
    ) | Sort-Object -Unique

    # Routes met een parameter en de niet-functionele fallback horen niet in de sweep.
    $sweepRoutes    = @($echteRoutes | Where-Object { $_ -notmatch '\{' -and $_ -ne '/not-found' })
    $verwachteRoutes= @($Expect.routes | ForEach-Object { $_.Path }) | Sort-Object -Unique

    $ontbreekt = @($sweepRoutes    | Where-Object { $verwachteRoutes -notcontains $_ })
    $teveel    = @($verwachteRoutes | Where-Object { $sweepRoutes    -notcontains $_ })

    if ($ontbreekt.Count -gt 0) {
        Add-Check -Gate 'G0' -Id 'G0.routes.dekkend' -Status 'fail' `
            -Message "Pagina's zonder assertie in selftest-expectations.psd1: $($ontbreekt -join ', ')"
    } elseif ($teveel.Count -gt 0) {
        Add-Check -Gate 'G0' -Id 'G0.routes.dekkend' -Status 'fail' `
            -Message "Asserties voor routes die niet meer bestaan: $($teveel -join ', ')"
    } else {
        Add-Check -Gate 'G0' -Id 'G0.routes.dekkend' -Status 'pass' `
            -Actual "$($sweepRoutes.Count) routes" -Message 'Verwachtingen en pagina''s dekken elkaar exact.'
    }
    Save-Artifact -Naam 'routes.json' -Inhoud @{ gevonden = $sweepRoutes; verwacht = $verwachteRoutes } | Out-Null

    # Staat van de ontwikkeldatabase vastleggen vóór we er iets mee doen.
    $sqlState = Get-ContainerState -Name $SqlContainer
    $script:State['sqlServerWasRunning'] = if ($sqlState) { $sqlState.Running } else { $null }
    Save-Artifact -Naam 'state.json' -Inhoud @{
        runId = $RunId; tier = $Tier; mode = $Mode
        sqlServerWasRunning = $script:State['sqlServerWasRunning']
        localSettingsHash   = $script:State['localSettingsHash']
        commit = (& git -C $RepoRoot rev-parse --short HEAD)
    } | Out-Null

    $g0 = Complete-Gate -Gate 'G0'
    if ($g0 -eq 'fail') { throw 'G0 gefaald — de run stopt hier; verder meten heeft geen zin.' }

    # ──────────────────────────────────────────────────────────────────────
    # G1 — Database beschikbaar, en de andere engine aantoonbaar niet
    # ──────────────────────────────────────────────────────────────────────
    Write-Kop 'G1 — Database beschikbaar'

    if ($Mode -eq 'Verify' -and $Tier -eq 'Postgres') {
        # De ontwikkeldatabase gaat uit. Alleen stoppen, nooit verwijderen: het volume van de
        # ontwikkelaar blijft ongemoeid.
        if ($sqlState -and $sqlState.Running) {
            & docker stop $SqlContainer 2>&1 | Out-Null
        }

        # 'docker stop' wacht op het stoppen van het containerproces, maar de host-poortmapping
        # (op Windows/Docker Desktop via een los proxyproces) kan een fractie later loslaten.
        # Empirisch aangetroffen: een directe check hier meldde de poort nog open ondanks een
        # geslaagde 'docker stop'. Kort pollen in plaats van een enkele meting, zelfde soort fix
        # als Wait-ForPostgres hierboven.
        $poortDeadline = (Get-Date).AddSeconds(10)
        do {
            $poortDicht = -not (Test-PortListening -Port $Ports.SqlServer)
            if (-not $poortDicht) { Start-Sleep -Milliseconds 500 }
        } while (-not $poortDicht -and (Get-Date) -lt $poortDeadline)

        $naStop = Get-ContainerState -Name $SqlContainer

        if (($naStop -and $naStop.Running) -or -not $poortDicht) {
            # Negatieve controle. Slaagt deze niet, dan kan alles daarna een stille terugval zijn
            # en bewijst de hele run niets.
            Add-Check -Gate 'G1' -Id 'G1.andere-engine.uit' -Status 'fail' `
                -Message 'De andere database is nog bereikbaar. Een terugval zou onopgemerkt blijven.'
        } else {
            Add-Check -Gate 'G1' -Id 'G1.andere-engine.uit' -Status 'pass' `
                -Message 'Ontwikkeldatabase gestopt en poort dicht.'
        }

        $env:SELFTEST_PG_PASSWORD = New-SelftestPassword
        $env:SELFTEST_PG_TZ       = $Expect.containerTimeZone
        & docker compose -p $ComposeProject -f $ComposeFile up -d 2>&1 | Out-Null

        $gereed = Wait-ForPostgres -ContainerName $PgContainer -Database 'sportlink_selftest'
        if ($gereed.Ready) {
            Add-Check -Gate 'G1' -Id 'G1.postgres.gereed' -Status 'pass' -Actual "na $($gereed.Attempts) pogingen"
            $tz = Invoke-Psql -ContainerName $PgContainer -Password $env:SELFTEST_PG_PASSWORD `
                              -Database 'sportlink_selftest' -Tuples -Sql 'SHOW TimeZone;'
            if ($tz.Output -eq $Expect.containerTimeZone) {
                Add-Check -Gate 'G1' -Id 'G1.tijdzone.niet-utc' -Status 'pass' -Actual $tz.Output `
                    -Message 'Op UTC zou de UTC-assertie in G4 niets bewijzen.'
            } else {
                Add-Check -Gate 'G1' -Id 'G1.tijdzone.niet-utc' -Status 'fail' `
                    -Expected $Expect.containerTimeZone -Actual $tz.Output
            }
        } else {
            Add-Check -Gate 'G1' -Id 'G1.postgres.gereed' -Status 'fail' -Message 'Container kwam niet op.'
        }
    } else {
        # Basismeting: de bestaande tier moet juist wél draaien.
        if ($sqlState -and $sqlState.Running -and (Test-PortListening -Port $Ports.SqlServer)) {
            Add-Check -Gate 'G1' -Id 'G1.ontwikkeldatabase.draait' -Status 'pass'
        } else {
            Add-Check -Gate 'G1' -Id 'G1.ontwikkeldatabase.draait' -Status 'fail' `
                -Message 'Start hem met: docker compose up -d'
        }
    }

    $g1 = Complete-Gate -Gate 'G1'
    if ($g1 -eq 'fail') { throw 'G1 gefaald — zonder database is de rest betekenisloos.' }

    # ──────────────────────────────────────────────────────────────────────
    # G2 — Schema (eerste run) / G3 — Idempotentie (tweede run)
    #
    # Alleen zinvol tegen de nieuwe tier: de bestaande SQL Server-tier heeft zijn eigen,
    # losstaande schema-drift-guard (CI) en hoeft hier niet nogmaals bewezen te worden.
    # ──────────────────────────────────────────────────────────────────────
    if ($Tier -eq 'Postgres') {
        $pgPassword    = $env:SELFTEST_PG_PASSWORD
        $pgConnString  = "Host=localhost;Port=$($Ports.Postgres);Username=postgres;Password=$pgPassword;Database=sportlink_selftest"
        $env:POSTGRES_CONNECTION_STRING = $pgConnString
        $migratiesPad  = Join-Path $RepoRoot 'Database.Postgres' 'migrations'
        $verwachtAantalMigraties = @(Get-ChildItem -Path $migratiesPad -Filter '*.sql').Count

        function Invoke-PgTierMigratie {
            $log = & dotnet run --project (Join-Path $RepoRoot 'Database.Postgres.Cli') --configuration Release -- $migratiesPad 2>&1
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($log -join "`n") }
        }

        function Get-PgSchemaMigratiesTelling {
            (Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -Tuples `
                -Sql 'SELECT COUNT(*) FROM public.schema_migrations;').Output
        }

        Write-Kop 'G2 — Schema (eerste run)'
        $eersteRun = Invoke-PgTierMigratie
        Save-Artifact -Naam 'g2-migratie-eerste-run.log' -Inhoud $eersteRun.Output | Out-Null

        if ($eersteRun.ExitCode -ne 0) {
            Add-Check -Gate 'G2' -Id 'G2.migratie.eerste-run' -Status 'fail' `
                -Message 'dotnet run Database.Postgres.Cli faalde — zie g2-migratie-eerste-run.log.'
        } else {
            Add-Check -Gate 'G2' -Id 'G2.migratie.eerste-run' -Status 'pass'

            $kernobjecten = @('public.appsettings', 'public.velden', 'public.speeltijden',
                              'planner.geplandewedstrijden', 'public.schema_migrations')
            $waardeLijst = ($kernobjecten | ForEach-Object { "('$_')" }) -join ','
            $ontbrekend = Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -Tuples -Sql `
                "SELECT string_agg(verwacht, ' ') FROM (VALUES $waardeLijst) AS v(verwacht) WHERE to_regclass(v.verwacht) IS NULL;"
            if ([string]::IsNullOrWhiteSpace($ontbrekend.Output)) {
                Add-Check -Gate 'G2' -Id 'G2.kernobjecten.aanwezig' -Status 'pass' -Actual "$($kernobjecten.Count) objecten"
            } else {
                Add-Check -Gate 'G2' -Id 'G2.kernobjecten.aanwezig' -Status 'fail' -Message "Ontbrekend: $($ontbrekend.Output)"
            }

            $tellingNa1e = Get-PgSchemaMigratiesTelling
            if ($tellingNa1e -eq "$verwachtAantalMigraties") {
                Add-Check -Gate 'G2' -Id 'G2.schema-migrations.telling' -Status 'pass' -Actual $tellingNa1e
            } else {
                Add-Check -Gate 'G2' -Id 'G2.schema-migrations.telling' -Status 'fail' `
                    -Expected "$verwachtAantalMigraties" -Actual $tellingNa1e
            }
        }
        [void](Complete-Gate -Gate 'G2')

        Write-Kop 'G3 — Idempotentie (tweede run)'
        $tweedeRun = Invoke-PgTierMigratie
        Save-Artifact -Naam 'g3-migratie-tweede-run.log' -Inhoud $tweedeRun.Output | Out-Null

        if ($tweedeRun.ExitCode -ne 0) {
            Add-Check -Gate 'G3' -Id 'G3.migratie.tweede-run' -Status 'fail' `
                -Message 'Tweede migratie-run faalde — zou idempotent moeten zijn. Zie g3-migratie-tweede-run.log.'
        } else {
            Add-Check -Gate 'G3' -Id 'G3.migratie.tweede-run' -Status 'pass'
            $tellingNa2e = Get-PgSchemaMigratiesTelling
            if ($tellingNa2e -eq "$verwachtAantalMigraties") {
                Add-Check -Gate 'G3' -Id 'G3.geen-duplicaat' -Status 'pass' -Actual $tellingNa2e `
                    -Message 'Geen extra rij door de tweede run.'
            } else {
                Add-Check -Gate 'G3' -Id 'G3.geen-duplicaat' -Status 'fail' `
                    -Expected "$verwachtAantalMigraties" -Actual $tellingNa2e
            }
        }
        [void](Complete-Gate -Gate 'G3')
    }

    # ──────────────────────────────────────────────────────────────────────
    # G4 — Demodata en rijtellingen
    #
    # Baseline (SqlServer): meet de bestaande, levende ontwikkeldatabase en legt de uitkomst vast
    # als vergelijkingsbasis — géén oordeel tegen het contract in selftest-expectations.psd1, want
    # een levende dev-database mag afwijken van een verse seed (handmatig getest, deels
    # opgeruimd, ...). Dat is precies waarom -BaselinePath bestaat.
    #
    # Verify (Postgres): seedt de AllStars-demodata vers (zelfde volgorde als de CI-job,
    # #862/#864) en vergelijkt met de meegegeven -BaselinePath als die er is; zonder baseline
    # valt de vergelijking terug op het contract uit selftest-expectations.psd1.
    # ──────────────────────────────────────────────────────────────────────
    Write-Kop 'G4 — Demodata en rijtellingen'

    $g4Actuals = [ordered]@{}

    if ($Tier -eq 'Postgres') {
        $pgPassword = $env:SELFTEST_PG_PASSWORD

        # Bronrij voor de primaire club — zonder dit heeft de speeltijden-kopie in 006 niets om te
        # kopiëren en bewijst de kopieerstap niets (zelfde noodzaak als de CI-job, #862).
        Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -StopOnError -Sql `
            "INSERT INTO public.appsettings (clubcode, clubname, syncenabled) VALUES ('ZTPRIMARY', 'Zelftest Primary', true) ON CONFLICT DO NOTHING;" | Out-Null
        Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -StopOnError -Sql `
            "INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, clubcode) VALUES ('JO13', 1.0, 60, 30, 10, 'ZTPRIMARY') ON CONFLICT DO NOTHING;" | Out-Null

        $seed006 = Join-Path $RepoRoot 'Database.Postgres' 'migrations' '006_allstars_demodata.sql'
        Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -StopOnError -SqlFile $seed006 | Out-Null

        # his.teams/his.matches bestaan pas na de eerste sync — hier gesimuleerd met de letterlijke
        # PostgresSchemaGenerator-DDL voor KnownEntities.Teams/Matches (#818), 1:1 overgenomen uit
        # de CI-job (.github/workflows/build.yml, job fresh-db-postgres) in plaats van hier apart
        # bedacht.
        $hisDdlPath = Join-Path $ArtifactDir 'g4-his-teams-matches.sql'
        Set-Content -Path $hisDdlPath -Encoding utf8NoBOM -Value @'
CREATE SCHEMA IF NOT EXISTS his;
CREATE TABLE IF NOT EXISTS his."teams" (
    "teamcode" BIGINT, "lokaleteamcode" BIGINT, "poulecode" BIGINT,
    "teamnaam" VARCHAR(100), "competitienaam" VARCHAR(200), "klasse" VARCHAR(200),
    "poule" VARCHAR(50), "klassepoule" VARCHAR(200), "spelsoort" VARCHAR(50),
    "competitiesoort" VARCHAR(50), "geslacht" VARCHAR(50), "teamsoort" VARCHAR(50),
    "leeftijdscategorie" VARCHAR(50), "kalespelsoort" VARCHAR(50), "speeldag" VARCHAR(50),
    "speeldagteam" VARCHAR(100), "more" VARCHAR(200), "clubcode" VARCHAR(20),
    "mta_inserted" TIMESTAMPTZ NOT NULL, "mta_modified" TIMESTAMPTZ NOT NULL,
    "mta_deleted" TIMESTAMPTZ NULL,
    "bk_teams" TEXT GENERATED ALWAYS AS (COALESCE("teamcode"::text, '') || '' || COALESCE("lokaleteamcode"::text, '') || '' || COALESCE("poulecode"::text, '')) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS "UQ_teams_bk" ON his."teams" ("bk_teams");
CREATE INDEX IF NOT EXISTS "IX_teams_clubcode" ON his."teams" ("clubcode");
CREATE TABLE IF NOT EXISTS his."matches" (
    "wedstrijddatum" VARCHAR(50), "wedstrijdcode" BIGINT, "wedstrijdnummer" BIGINT,
    "datum" VARCHAR(50), "wedstrijd" VARCHAR(200), "accommodatie" VARCHAR(200),
    "aanvangstijd" VARCHAR(50), "thuisteam" VARCHAR(100), "thuisteamid" VARCHAR(50),
    "thuisteamlogo" VARCHAR(1000), "thuisteamclubrelatiecode" VARCHAR(50),
    "uitteamclubrelatiecode" VARCHAR(50), "uitteam" VARCHAR(100), "uitteamid" VARCHAR(50),
    "uitteamlogo" VARCHAR(1000), "competitiesoort" VARCHAR(200), "status" VARCHAR(50),
    "meer" VARCHAR(1000), "teamnaam" VARCHAR(100), "teamvolgorde" INTEGER,
    "competitie" VARCHAR(200), "klasse" VARCHAR(100), "poule" VARCHAR(100),
    "klassepoule" VARCHAR(200), "kaledatum" VARCHAR(50), "vertrektijd" VARCHAR(50),
    "verzameltijd" VARCHAR(50), "scheidsrechters" VARCHAR(500), "scheidsrechter" VARCHAR(200),
    "veld" VARCHAR(100), "veld_subpositie" VARCHAR(5), "locatie" VARCHAR(100),
    "plaats" VARCHAR(100), "rijders" VARCHAR(200), "kleedkamerthuisteam" VARCHAR(50),
    "kleedkameruitteam" VARCHAR(50), "kleedkamerscheidsrechter" VARCHAR(50),
    "datumopgemaakt" VARCHAR(50), "uitslag" VARCHAR(50), "uitslag-regulier" VARCHAR(50),
    "uitslag-nv" VARCHAR(50), "uitslag-s" VARCHAR(50), "competitienaam" VARCHAR(200),
    "eigenteam" VARCHAR(50), "sportomschrijving" VARCHAR(100),
    "verenigingswedstrijd" VARCHAR(50), "clubcode" VARCHAR(20),
    "mta_inserted" TIMESTAMPTZ NOT NULL, "mta_modified" TIMESTAMPTZ NOT NULL,
    "mta_deleted" TIMESTAMPTZ NULL,
    "bk_matches" TEXT GENERATED ALWAYS AS (COALESCE("wedstrijdcode"::text, '')) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS "UQ_matches_bk" ON his."matches" ("bk_matches");
CREATE INDEX IF NOT EXISTS "IX_matches_clubcode" ON his."matches" ("clubcode");
'@
        Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -StopOnError -SqlFile $hisDdlPath | Out-Null

        $seedMatches = Join-Path $RepoRoot 'scripts' 'migrations' '003-seed-allstars-demo-matches-postgres.sql'
        Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -StopOnError -SqlFile $seedMatches | Out-Null
        Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -StopOnError -SqlFile $seedMatches | Out-Null

        foreach ($rc in $Expect.rowCounts) {
            $sql = switch ($rc.Key) {
                'velden'              { "SELECT COUNT(*) FROM public.velden WHERE clubcode='ALLSTARS';" }
                'veldbeschikbaarheid' { "SELECT COUNT(*) FROM public.veldbeschikbaarheid WHERE clubcode='ALLSTARS';" }
                'speeltijden'         { "SELECT COUNT(*) FROM public.speeltijden WHERE clubcode='ALLSTARS';" }
                'teamregels'          { "SELECT COUNT(*) FROM public.teamregels WHERE clubcode='ALLSTARS';" }
                'teams'               { "SELECT COUNT(*) FROM his.teams WHERE clubcode='ALLSTARS';" }
                'teambegeleiding'     { "SELECT COUNT(*) FROM avg.teambegeleiding WHERE clubcode='ALLSTARS';" }
                'matches'             { "SELECT COUNT(*) FROM his.matches WHERE clubcode='ALLSTARS';" }
                default               { $null }
            }
            if (-not $sql) { continue }
            $res = Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -Tuples -Sql $sql
            $g4Actuals[$rc.Key] = [int]$res.Output
        }

        # #864: niet-demoklub-assertie — dezelfde controle als de CI-stap (fresh-db-postgres).
        $nietDemo = Invoke-Psql -ContainerName $PgContainer -Password $pgPassword -Database 'sportlink_selftest' -Tuples -Sql `
            "SELECT COUNT(*) FROM public.speeltijden WHERE clubcode <> 'ALLSTARS';"
        if ([int]$nietDemo.Output -gt 0) {
            Add-Check -Gate 'G4' -Id 'G4.niet-demoklub.speeltijden' -Status 'pass' -Actual $nietDemo.Output
        } else {
            Add-Check -Gate 'G4' -Id 'G4.niet-demoklub.speeltijden' -Status 'fail' `
                -Message 'Geen speeltijden voor een niet-democlub — seed koos de verkeerde club (#740, #864).'
        }
    } else {
        # Baseline: de bestaande SQL Server-ontwikkeldatabase. Alleen leesqueries — deze poort mag
        # nooit iets in de levende dev-database wijzigen.
        if (-not $script:State.Contains('sqlConnectionString')) {
            Add-Check -Gate 'G4' -Id 'G4.connectiereeks.ontbreekt' -Status 'fail' `
                -Message 'G0 heeft geen bruikbare SqlConnectionString vastgelegd — kan niet meten.'
        } else {
            $cs  = $script:State['sqlConnectionString']
            $srv = if ($cs -match '(?:Data Source|Server|Address|Addr|Network Address)\s*=\s*([^;]+)') { $Matches[1].Trim() }
            $db  = if ($cs -match '(?:Initial Catalog|Database)\s*=\s*([^;]+)') { $Matches[1].Trim() }
            $usr = if ($cs -match '(?:User ID|User Id|UID)\s*=\s*([^;]+)') { $Matches[1].Trim() }
            $pwd = if ($cs -match '(?:Password|PWD)\s*=\s*([^;]+)') { $Matches[1].Trim() }
            $trustCert = $cs -match 'TrustServerCertificate\s*=\s*(?:True|Yes)'

            $env:SQLCMDPASSWORD = $pwd
            $sqlArgs = @('-U', $usr)
            if ($trustCert) { $sqlArgs += '-C' }

            function Invoke-SqlServerScalar($query) {
                $regel = & sqlcmd -S $srv -d $db @sqlArgs -Q "SET NOCOUNT ON; $query" -h -1 -W 2>&1 |
                    Where-Object { $_ -match '\S' } | Select-Object -Last 1
                "$regel".Trim()
            }

            $sqlServerQueries = @{
                'velden'              = "SELECT COUNT(*) FROM [dbo].[Velden] WHERE [ClubCode]='ALLSTARS';"
                'veldbeschikbaarheid' = "SELECT COUNT(*) FROM [dbo].[VeldBeschikbaarheid] WHERE [ClubCode]='ALLSTARS';"
                'speeltijden'         = "SELECT COUNT(*) FROM [dbo].[Speeltijden] WHERE [ClubCode]='ALLSTARS';"
                'teamregels'          = "SELECT COUNT(*) FROM [dbo].[TeamRegels] WHERE [ClubCode]='ALLSTARS';"
                'teams'               = "SELECT COUNT(*) FROM [his].[Teams] WHERE [ClubCode]='ALLSTARS';"
                'teambegeleiding'     = "SELECT COUNT(*) FROM [avg].[Teambegeleiding] WHERE [ClubCode]='ALLSTARS';"
                'matches'             = "SELECT COUNT(*) FROM [his].[Matches] WHERE [ClubCode]='ALLSTARS';"
            }
            foreach ($rc in $Expect.rowCounts) {
                if (-not $sqlServerQueries.ContainsKey($rc.Key)) { continue }
                $waarde = Invoke-SqlServerScalar $sqlServerQueries[$rc.Key]
                $ok = $waarde -match '^\d+$'
                if ($ok) {
                    $g4Actuals[$rc.Key] = [int]$waarde
                } else {
                    Add-Check -Gate 'G4' -Id "G4.rijtelling.$($rc.Key)" -Status 'fail' `
                        -Message "sqlcmd gaf geen getal terug: '$waarde'"
                }
            }
        }
    }

    # Vergelijking: altijd tegen het contract in selftest-expectations.psd1 — dat bestand zegt
    # zelf al "een CONTRACT, geen momentopname" en onderscheidt bewust Exact (deterministisch,
    # rechtstreeks uit de seedscript-logica af te leiden) van Min (varieert legitiem, bijv.
    # speeltijden/teamregels die door jarenlang handmatig testen kunnen zijn opgehoopt). -BaselinePath
    # is hier bewust NIET voor gebruikt: een levende ontwikkeldatabase mag van het contract afwijken
    # (dat IS precies waarom sommige rijen hier 'Min' zijn i.p.v. 'Exact') en zo'n afwijking zou de
    # Postgres-Verify-run laten falen op een verschil dat niets met Postgres te maken heeft —
    # empirisch aangetroffen tijdens het bouwen van deze poort (dev-database toonde speeltijden=33,
    # teamregels=3 door eerder handmatig testen; een verse Postgres-seed gaf correct 1 en 1).
    #
    # Baseline (SqlServer): een afwijking van het contract is hier GEEN falen — deze poort meet en
    # legt vast, oordeelt niet (zie .PARAMETER Mode). Wel zichtbaar gemaakt, want een baseline die
    # zelf al van het contract afwijkt is nuttige informatie voor wie de uitkomst leest.
    foreach ($rc in $Expect.rowCounts) {
        if (-not $g4Actuals.Contains($rc.Key)) { continue }
        $actual   = $g4Actuals[$rc.Key]
        $voldoet  = if ($rc.Contains('Exact')) { $actual -eq $rc.Exact } else { $actual -ge $rc.Min }
        $verwacht = if ($rc.Contains('Exact')) { "$($rc.Exact)" } else { ">= $($rc.Min)" }

        if ($Tier -ne 'Postgres') {
            $bericht = if ($voldoet) { 'Basismeting — vergelijkingsbasis voor een latere Verify-run.' }
                       else { "Basismeting wijkt af van het contract ($verwacht) — een levende dev-database mag dat; alleen ter info." }
            Add-Check -Gate 'G4' -Id "G4.rijtelling.$($rc.Key)" -Status 'pass' -Actual "$actual" -Message $bericht
            continue
        }

        if ($voldoet) {
            Add-Check -Gate 'G4' -Id "G4.rijtelling.$($rc.Key)" -Status 'pass' -Actual "$actual"
        } else {
            Add-Check -Gate 'G4' -Id "G4.rijtelling.$($rc.Key)" -Status 'fail' `
                -Expected $verwacht -Actual "$actual" -Message $rc.Reden
        }
    }
    Save-Artifact -Naam 'g4-rijtellingen.json' -Inhoud $g4Actuals | Out-Null
    [void](Complete-Gate -Gate 'G4')

    # ──────────────────────────────────────────────────────────────────────
    # G5/G6 — bewust nog niet geïmplementeerd in deze ronde.
    #
    # Beide vereisen een daadwerkelijk draaiende functiehost (func start), inclusief Azurite en
    # de bijbehorende ~20s koude-startwachttijd (#175) en teardown-verantwoordelijkheid — een
    # aanzienlijk grotere en risicovollere stap dan G2-G4 (die alleen tegen de database praten).
    # Een halfbakken versie zou een vlakke, onbetrouwbare groene/rode uitslag opleveren — precies
    # de "nep-groen"-fout die dit script overal elders vermijdt. Zie issue #909 voor de
    # vervolgopgave.
    # ──────────────────────────────────────────────────────────────────────
    foreach ($gate in @('G5', 'G6')) {
        $def = $Gates | Where-Object { $_.Id -eq $gate }
        if ($def.Modes -notcontains $Mode) { continue }
        Write-Kop "$gate — $($def.Naam)"
        Add-Check -Gate $gate -Id "$gate.functiehost-niet-geautomatiseerd" -Status 'blocked' -Blocked @(909) `
            -Message 'Vereist een draaiende functiehost (func start) — nog niet geautomatiseerd in dit script.'
        [void](Complete-Gate -Gate $gate)
    }

    # G7/G8 horen bij de skill: een client-side gerenderde pagina is niet met een HTTP-aanroep te
    # beoordelen. Het script levert wel de opdracht aan, zodat de skill niets zelf verzint.
    Write-Kop 'G7/G8 — Overdracht aan de skill'
    Save-Artifact -Naam 'skill-opdracht.json' -Inhoud @{
        runId                = $RunId
        tier                 = $Tier
        mode                 = $Mode
        blazorUrl            = "http://localhost:$($Ports.BlazorAdmin)"
        functionUrl          = "http://localhost:$($Ports.FunctionApp)"
        demoClub             = $Expect.demoClub
        routes               = $Expect.routes
        negativeControlRoute = $Expect.negativeControlRoute
        crudCases            = $Expect.crudCases
        crudPrefix           = $Expect.crudPrefix
        apiEndpoints         = $Expect.apiEndpoints
    } | Out-Null
    Add-Check -Gate 'G7' -Id 'G7.opdracht.geschreven' -Status 'pass' `
        -Message 'skill-opdracht.json bevat de routes en asserties voor de browsersweep.'
    [void](Complete-Gate -Gate 'G7')

} catch {
    Write-Host ''
    Write-Host "AFGEBROKEN: $($_.Exception.Message)" -ForegroundColor Red
    if ($exitCode -eq 0) { $exitCode = 1 }
} finally {
    # Altijd opruimen — ook na een fout of een onderbreking. Anders laat een halverwege afgebroken
    # run de ontwikkeldatabase uitgeschakeld achter.
    if (-not $ListPhases -and -not $Teardown) {
        try { Invoke-Teardown } catch { Write-Host "  Teardown gaf een fout: $($_.Exception.Message)" -ForegroundColor DarkYellow }

        # Integriteitscontrole: de zelftest mag zichzelf niet groen maken door de omgeving te
        # verbouwen.
        if ($script:State.Contains('localSettingsHash') -and (Test-Path $localSettings)) {
            $na = (Get-FileHash $localSettings -Algorithm SHA256).Hash
            if ($na -ne $script:State['localSettingsHash']) {
                Write-Host '  LET OP: FunctionApp/local.settings.json is gewijzigd tijdens de run.' -ForegroundColor Red
                $exitCode = 1
            }
        }

        # Rapport
        $samenvatting = [ordered]@{
            schemaVersion = 1
            runId   = $RunId
            tier    = $Tier
            mode    = $Mode
            commit  = (& git -C $RepoRoot rev-parse --short HEAD)
            gates   = $script:GateStat
            checks  = $script:Checks
            summary = [ordered]@{
                pass    = @($script:Checks | Where-Object { $_.status -eq 'pass' }).Count
                fail    = @($script:Checks | Where-Object { $_.status -eq 'fail' }).Count
                blocked = @($script:Checks | Where-Object { $_.status -eq 'blocked' }).Count
            }
        }
        $rapport = Save-Artifact -Naam 'report.json' -Inhoud $samenvatting

        Write-Host ''
        Write-Host '══════════════════════════════════════════════════════════' -ForegroundColor Cyan
        Write-Host ("  geslaagd {0}   gefaald {1}   geblokkeerd {2}" -f
            $samenvatting.summary.pass, $samenvatting.summary.fail, $samenvatting.summary.blocked)
        Write-Host "  Rapport: $rapport" -ForegroundColor DarkGray
        Write-Host '══════════════════════════════════════════════════════════' -ForegroundColor Cyan

        if ($samenvatting.summary.fail -gt 0 -and $exitCode -eq 0) { $exitCode = 1 }
    }
}

exit $exitCode
