# DevServices.psm1
# Gedeelde helpers voor de lokale dev-scripts (Start-Debug.ps1, Stop-Debug.ps1, Test-App.ps1).
#
# Bevat readiness-detectie en teardown, zodat geen enkel script nog vertrouwt op een
# vaste Start-Sleep. Zie #684 voor de aanleiding.
#
# CROSS-PLATFORM (#800): deze module draait op Windows én macOS onder PowerShell 7.
# Regels bij het uitbreiden ervan:
#   1. Poortdetectie loopt via de .NET BCL (IPGlobalProperties), niet via Get-NetTCPConnection:
#      die zit in de module NetTCPIP en bestaat alleen op Windows.
#   2. Alles wat een PID bij een poort of een procesboom nodig heeft is per definitie
#      OS-specifiek — Windows via NetTCPIP/CIM, macOS via lsof/ps. Kapsel dat in achter
#      een functie in deze module, nooit inline in een script.
#   3. Gebruik nooit $env:TEMP: dat bestaat niet op macOS (daar heet het TMPDIR).
#      Gebruik Get-DebugTempDir.
#   4. Gebruik nooit een backslash in een padliteral. Op Unix is '\' een geldig teken in
#      een bestandsnaam, dus Join-Path 'a' 'b\c' levert daar één bestand 'b\c' op in
#      plaats van 'b/c'. Forward slashes werken op beide platforms.

$script:DebugPorts = @{
    Azurite     = 10000
    FunctionApp = 7094
    BlazorAdmin = 5242
    Swa         = 4280
}

# $IsWindows bestaat vanaf PowerShell 6 op alle platforms; deze module vereist PowerShell 7.
$script:OnWindows = [bool]$IsWindows

# Het pad naar de NATIVE ps, expliciet.
#
# 'ps' kaal aanroepen is riskant: op Windows is 'ps' een PowerShell-alias voor Get-Process.
# PowerShell verwijdert die alias op Unix juist om de systeem-ps niet te overschaduwen, dus
# in de praktijk zou het goed gaan — maar dan hangt correcte werking af van een alias die er
# níet is. Expliciet het pad gebruiken haalt die aanname weg.
$script:PsExe = if ($script:OnWindows) { $null }
                elseif (Test-Path '/bin/ps') { '/bin/ps' }
                else { '/usr/bin/ps' }

function Get-DebugTempDir {
    <#
        De tijdelijke map van de huidige gebruiker, cross-platform.

        $env:TEMP bestaat NIET op macOS/Linux. GetTempPath() honoreert daar TMPDIR
        (macOS zet die per gebruiker bij inloggen) en valt anders terug op /tmp;
        op Windows levert het dezelfde map als $env:TEMP. Het resultaat eindigt
        altijd op een directory-separator.
    #>
    [System.IO.Path]::GetTempPath()
}

function Get-DebugPidFile {
    Join-Path (Get-DebugTempDir) 'sportlink-debug-pids.txt'
}

function Get-DebugPorts {
    $script:DebugPorts
}

function Test-PortListening {
    <#
        Luistert er een proces op $Port?

        Gebruikt de .NET BCL in plaats van Get-NetTCPConnection: dat laatste zit in de
        module NetTCPIP, die alleen op Windows bestaat. GetActiveTcpListeners() somt de
        daadwerkelijke listeners op (IPv4 én IPv6) en werkt op alle platforms.

        Bewust géén TcpClient-connectiepoging: die maakt een echte verbinding (socket,
        TIME_WAIT) en bewijst minder precies dát er een listener is.
    #>
    param([Parameter(Mandatory)][int]$Port)

    $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    [bool]($listeners | Where-Object { $_.Port -eq $Port })
}

function Get-PortOwnerId {
    <#
        Het PID van het proces dat op $Port luistert, of $null.

        Onvermijdelijk OS-specifiek: de .NET-API die de listeners oplevert geeft alleen
        IPEndPoints terug, zonder proces-eigenaar.
          Windows → Get-NetTCPConnection (OwningProcess)
          macOS   → lsof -t (alleen PIDs)
        lsof zit standaard in macOS. Ontbreekt het toch, dan levert deze functie $null
        en valt de teardown terug op het PID-bestand.
    #>
    param([Parameter(Mandatory)][int]$Port)

    if ($script:OnWindows) {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
                Select-Object -First 1
        if (-not $conn) { return $null }
        return [int]$conn.OwningProcess
    }

    if (-not (Get-Command lsof -ErrorAction SilentlyContinue)) { return $null }

    # -n/-P: geen DNS- en servicenaam-lookups (sneller en voorspelbaarder).
    # -sTCP:LISTEN: alleen echte listeners.  -t: alleen PIDs, één per regel.
    #
    # De argumenten worden eerst als losse strings opgebouwd. Een token als '-iTCP:$Port'
    # direct in de aanroep zetten leest als PowerShell's parameter-dubbelepunt-syntax; bij
    # een native commando gaat dat goed, maar expliciet is hier veiliger dan impliciet.
    $iArg = '-iTCP:' + $Port
    $found = & lsof '-nP' $iArg '-sTCP:LISTEN' '-t' 2>$null
    $first = @($found | Where-Object { "$_".Trim() -match '^\d+$' }) | Select-Object -First 1
    if (-not $first) { return $null }
    return [int]("$first".Trim())
}

function Get-PortOwner {
    <#
        Retourneert het proces dat op $Port luistert, of $null.
    #>
    param([Parameter(Mandatory)][int]$Port)

    $ownerPid = Get-PortOwnerId -Port $Port
    if (-not $ownerPid) { return $null }
    Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
}

function Get-ParentProcessId {
    <#
        Het PID van de parent van $ProcessId, of $null.

        System.Diagnostics.Process kent geen ParentProcessId, dus dit kan niet puur in .NET.
          Windows → CIM (Win32_Process bestaat alleen daar)
          macOS   → ps -o ppid=
    #>
    param([Parameter(Mandatory)][int]$ProcessId)

    if ($script:OnWindows) {
        $parentId = (Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue).ParentProcessId
        if ($parentId) { return [int]$parentId }
        return $null
    }

    $out = & $script:PsExe '-o' 'ppid=' '-p' $ProcessId 2>$null
    if (-not $out) { return $null }
    # ps vult de kolom op met spaties, dus altijd eerst trimmen.
    $trimmed = "$(@($out)[0])".Trim()
    if ($trimmed -match '^\d+$') { return [int]$trimmed }
    return $null
}

function Get-ChildProcessId {
    <#
        De directe kindprocessen van $ProcessId.

        Op Unix bewust één 'ps'-aanroep met de hele pid/ppid-tabel in plaats van pgrep:
        dat scheelt een externe afhankelijkheid en levert alles in één keer.
    #>
    param([Parameter(Mandatory)][int]$ProcessId)

    if ($script:OnWindows) {
        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue
        return @($children | ForEach-Object { [int]$_.ProcessId })
    }

    $lines = & $script:PsExe '-Ao' 'pid=,ppid=' 2>$null
    if (-not $lines) { return @() }

    $result = foreach ($line in $lines) {
        $parts = ("$line".Trim() -split '\s+')
        if ($parts.Count -ge 2 -and $parts[1] -eq "$ProcessId" -and $parts[0] -match '^\d+$') {
            [int]$parts[0]
        }
    }
    return @($result)
}

function Wait-ForPort {
    <#
        Pollt tot $Port luistert (of tot $Port vrij is met -Free). Retourneert $true bij
        succes, $false bij timeout. Toont een puntjes-voortgangsindicatie.
    #>
    param(
        [Parameter(Mandatory)][int]$Port,
        [int]$TimeoutSeconds = 60,
        [string]$Label = '',
        [switch]$Free,
        [switch]$Quiet
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $shown    = $false

    while ((Get-Date) -lt $deadline) {
        $listening = Test-PortListening -Port $Port
        $done      = if ($Free) { -not $listening } else { $listening }

        if ($done) {
            if ($shown -and -not $Quiet) { Write-Host '' }
            return $true
        }

        if (-not $Quiet) {
            if (-not $shown) {
                $what = if ($Free) { 'vrijgeven' } else { 'starten' }
                Write-Host ("  Wachten op {0} ({1} :{2})" -f $what, $Label, $Port) -NoNewline -ForegroundColor DarkGray
                $shown = $true
            }
            Write-Host '.' -NoNewline -ForegroundColor DarkGray
        }
        Start-Sleep -Milliseconds 500
    }

    if ($shown -and -not $Quiet) { Write-Host '' }
    return $false
}

function Wait-ForHealth {
    <#
        Pollt een health-endpoint tot HTTP 200. Retourneert het geparseerde
        response-object (met .version) bij succes, anders $null.

        De FunctionApp-host heeft een bekende koude-start van ~20s (#175), dus de
        default-timeout is ruim.
    #>
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$TimeoutSeconds = 90,
        [switch]$Quiet
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $shown    = $false

    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-RestMethod -Uri $Url -TimeoutSec 5 -ErrorAction Stop
            if ($shown -and -not $Quiet) { Write-Host '' }
            return $response
        } catch {
            if (-not $Quiet) {
                if (-not $shown) {
                    Write-Host "  Wachten op health ($Url)" -NoNewline -ForegroundColor DarkGray
                    $shown = $true
                }
                Write-Host '.' -NoNewline -ForegroundColor DarkGray
            }
            Start-Sleep -Milliseconds 1000
        }
    }

    if ($shown -and -not $Quiet) { Write-Host '' }
    return $null
}

function Wait-ForHttp {
    <#
        Pollt een URL tot een HTTP-status < 500 terugkomt. Voor de Blazor dev server:
        die serveert index.html zodra hij luistert, maar de eerste request kan nog
        tijdens compilatie afgewezen worden.

        Let op: HTTP 200 van een Blazor WASM-route bewijst NIET dat de app rendert —
        daarvoor is de browsercheck nodig. Dit is uitsluitend een readiness-signaal.
    #>
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$TimeoutSeconds = 120,
        [switch]$Quiet
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $shown    = $false

    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $Url -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
            if ($r.StatusCode -lt 500) {
                if ($shown -and -not $Quiet) { Write-Host '' }
                return $true
            }
        } catch {
            # nog niet klaar — blijven pollen
        }

        if (-not $Quiet) {
            if (-not $shown) {
                Write-Host "  Wachten op $Url" -NoNewline -ForegroundColor DarkGray
                $shown = $true
            }
            Write-Host '.' -NoNewline -ForegroundColor DarkGray
        }
        Start-Sleep -Milliseconds 1000
    }

    if ($shown -and -not $Quiet) { Write-Host '' }
    return $false
}

function Get-ProcessTree {
    <#
        Retourneert $RootPid plus alle (klein)kinderen, diepste eerst.

        Leaf-first is essentieel: 'dotnet watch' start zijn kindproces opnieuw op zodra dat
        wegvalt. Kill je de parent niet eerst of tegelijk, dan bindt een nieuw kindproces
        poort 5242 opnieuw en lijkt de teardown mislukt.
    #>
    param([Parameter(Mandatory)][int]$RootPid)

    $all = @()
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($RootPid)
    $seen = [System.Collections.Generic.HashSet[int]]::new()

    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        if (-not $seen.Add($current)) { continue }
        $all += $current

        foreach ($child in (Get-ChildProcessId -ProcessId $current)) { $queue.Enqueue($child) }
    }

    # Omkeren: de laatst gevonden PIDs zitten het diepst in de boom.
    [array]::Reverse($all)
    return $all
}

function Stop-ProcessTree {
    <#
        Stopt een proces inclusief al zijn nakomelingen, diepste eerst.
        Retourneert het aantal daadwerkelijk gestopte processen.
    #>
    param(
        [Parameter(Mandatory)][int]$RootPid,
        [switch]$Quiet
    )

    $stopped = 0
    foreach ($treePid in (Get-ProcessTree -RootPid $RootPid)) {
        $proc = Get-Process -Id $treePid -ErrorAction SilentlyContinue
        if (-not $proc) { continue }
        # Nooit de eigen sessie of zijn parent afsluiten.
        if ($treePid -eq $PID) { continue }

        if (-not $Quiet) {
            Write-Host "  Stoppen: $($proc.ProcessName) (PID $treePid)" -ForegroundColor DarkGray
        }
        Stop-Process -Id $treePid -Force -ErrorAction SilentlyContinue
        $stopped++
    }
    return $stopped
}

function Stop-DebugServices {
    <#
        Stopt alle lokale debug-services: eerst via het PID-bestand, daarna als fallback
        via de processen die op de bekende poorten luisteren. Wacht tot de poorten
        daadwerkelijk vrij zijn — geen vaste sleep.

        Idempotent: draaien zonder actieve services doet niets.

        -IncludeAzurite stopt ook Azurite (default: laten draaien, want die heeft geen
        state die per herstart verse initialisatie nodig heeft).
    #>
    param(
        [switch]$IncludeAzurite,
        [switch]$Quiet
    )

    $pidFile = Get-DebugPidFile
    $stopped = 0

    $ports = @($script:DebugPorts.FunctionApp, $script:DebugPorts.BlazorAdmin, $script:DebugPorts.Swa)
    if ($IncludeAzurite) { $ports += $script:DebugPorts.Azurite }

    # Ronde 1 — de processen uit het PID-bestand, inclusief hun hele boom.
    # De boom is nodig omdat 'dotnet watch' zijn kindproces herstart zodra dat wegvalt.
    #
    # Formaat: 'naam=pid' per regel (oude bestanden met alleen een getal blijven werken).
    # Het label is nodig om Azurite over te kunnen slaan: zonder -IncludeAzurite moet die
    # blijven draaien, en zonder label zou hij als kind van zijn wrapper alsnog sneuvelen.
    if (Test-Path $pidFile) {
        $keptAzurite = $false
        foreach ($line in (Get-Content $pidFile)) {
            $name = $null
            $targetPid = 0

            if ($line -match '^(?<name>[a-z]+)=(?<pid>\d+)$') {
                $name = $Matches['name']
                $targetPid = [int]$Matches['pid']
            } elseif ($line -match '^\d+$') {
                $targetPid = [int]$line
            } else {
                continue
            }

            if ($name -eq 'azurite' -and -not $IncludeAzurite) {
                $keptAzurite = $true
                continue
            }

            $stopped += Stop-ProcessTree -RootPid $targetPid -Quiet:$Quiet
        }
        Remove-Item $pidFile -Force -ErrorAction SilentlyContinue

        if ($keptAzurite -and -not $Quiet) {
            Write-Host "  Azurite blijft draaien (gebruik -All om ook Azurite te stoppen)." -ForegroundColor DarkGray
        }
    }

    # Ronde 2 — poort-eigenaren die het PID-bestand niet kende (bijv. na een crash of een
    # sessie die met de hand is gestart). Meerdere passes: een watcher die net een nieuw
    # kindproces heeft gestart, wordt in de volgende pass alsnog opgeruimd.
    $allFree = $true
    foreach ($port in $ports) {
        $attempt = 0
        while ($attempt -lt 4) {
            $proc = Get-PortOwner -Port $port
            if (-not $proc) { break }

            if (-not $Quiet) {
                Write-Host "  Stoppen: $($proc.ProcessName) (PID $($proc.Id)) op poort $port" -ForegroundColor Yellow
            }
            # Ook hier de hele boom: de poort-eigenaar is vaak het kind van een watcher.
            $stopped += Stop-ProcessTree -RootPid $proc.Id -Quiet:$Quiet

            # De parent (watcher) staat niet in de boom van het kind — stop die ook.
            $parentId = Get-ParentProcessId -ProcessId $proc.Id
            if ($parentId) {
                $parent = Get-Process -Id $parentId -ErrorAction SilentlyContinue
                if ($parent -and $parent.ProcessName -in @('dotnet', 'func', 'node', 'powershell', 'pwsh')) {
                    if (-not $Quiet) {
                        Write-Host "  Stoppen watcher-parent: $($parent.ProcessName) (PID $parentId)" -ForegroundColor DarkGray
                    }
                    $stopped += Stop-ProcessTree -RootPid $parentId -Quiet:$Quiet
                }
            }

            Wait-ForPort -Port $port -TimeoutSeconds 8 -Free -Quiet -Label 'teardown' | Out-Null
            $attempt++
        }

        # Wacht tot de poort echt vrij is — dit ving voorheen een Start-Sleep -Seconds 4 op,
        # bedoeld om file handles (runtimeconfig.json) vrij te laten geven.
        if (-not (Wait-ForPort -Port $port -TimeoutSeconds 20 -Free -Quiet:$Quiet -Label 'teardown')) {
            $allFree = $false
            if (-not $Quiet) {
                Write-Host "  Poort $port is na 20s nog bezet." -ForegroundColor Red
            }
        }
    }

    return [pscustomobject]@{
        StoppedCount  = $stopped
        AllPortsFree  = $allFree
    }
}

# ══════════════════════════════════════════════════════════════════════════
# Zelftest-helpers (#851)
#
# Alles wat de zelftest aan OS- of Docker-specifiek gedrag nodig heeft staat hier, niet inline
# in Test-PostgresTier.ps1 — zelfde regel als hierboven: één plek voor platformverschillen.
# ══════════════════════════════════════════════════════════════════════════

function Get-SelftestPorts {
    <#
        Poorten van de zelftest. Bewust ANDERE poorten dan Get-DebugPorts waar dat kan, zodat een
        draaiende dev-omgeving of een eigen Postgres niet in de weg zit.

        Uitzondering: de FunctionApp-poort blijft 7094. BlazorAdmin/wwwroot/appsettings.json
        hardcodeert die URL en dat bestand staat in git — een andere poort zou een getrackt
        bestand moeten wijzigen. De zelftest neemt 7094 dus over in plaats van ernaast te gaan
        zitten, en stopt daarom eerst de dev-omgeving (zie Stop-DebugServices).
    #>
    @{
        Postgres    = 55432   # niet 5432: een zelf geïnstalleerde Postgres blijft ongemoeid
        SqlServer   = 1433    # de bestaande dev-container; wordt gestopt, niet verplaatst
        FunctionApp = 7094
        BlazorAdmin = 5242
        Fixture     = 7099    # lokale stub voor de externe databron (#867)
    }
}

function Test-DockerAvailable {
    <#
        Draait de Docker-daemon? 'docker info' is de goedkoopste betrouwbare controle: 'docker
        --version' slaagt ook als de daemon stilstaat, want dat leest alleen de client.
    #>
    try {
        $null = & docker info 2>&1
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    }
}

function Get-ContainerState {
    <#
        De staat van één container, of $null als hij niet bestaat.

        Gebruikt door de zelftest om vóór de run vast te leggen of de SQL Server-container draaide,
        zodat de teardown hem exact zo kan achterlaten. Zonder die vastlegging zou een zelftest
        die op een gestopte container start, hem daarna 'behulpzaam' aanzetten.
    #>
    param([Parameter(Mandatory)][string]$Name)

    $out = & docker inspect --format '{{.State.Running}}' $Name 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }

    [pscustomobject]@{
        Name    = $Name
        Exists  = $true
        Running = ($out.Trim() -eq 'true')
    }
}

function Wait-ForPostgres {
    <#
        Pollt tot Postgres verbindingen accepteert, of tot de time-out verstrijkt.

        pg_isready draait ín de container, dus dit werkt zonder een lokale psql-installatie —
        op Windows en macOS gelijk. Geen Start-Sleep vooraf: eerst proberen, dan pas wachten.

        VERPLICHT -d <database> meegeven, niet alleen -U: de officiële Postgres-image draait
        initdb.d (incl. het aanmaken van POSTGRES_DB) via een tijdelijke, alleen-lokale
        server vóórdat de "echte" server extern gaat luisteren. pg_isready zonder -d verbindt
        impliciet met een database die naar de OS-gebruiker is genoemd (hier: 'postgres',
        altijd meteen aanwezig) en kan daardoor "ready" melden vóórdat de aangevraagde
        POSTGRES_DB-database daadwerkelijk bestaat — empirisch aangetroffen bij de #851-zelftest:
        "gereed na 4 pogingen" gevolgd door "database sportlink_selftest does not exist" op de
        allereerstvolgende query. -d checkt dezelfde database die de aanroeper zo meteen gebruikt.
    #>
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Database,
        [string]$User = 'postgres',
        [int]$TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $poging = 0
    while ((Get-Date) -lt $deadline) {
        $poging++
        $null = & docker exec $ContainerName pg_isready -U $User -d $Database 2>&1
        if ($LASTEXITCODE -eq 0) {
            # #851 (vervolg op de -d-fix hierboven): pg_isready kan "ready" melden vlak vóórdat de
            # server daadwerkelijk queries accepteert — empirisch aangetroffen: "gereed na 3
            # pogingen" gevolgd door "FATAL: the database system is starting up" op de eerstvolgende
            # échte query. Als postgres OS-gebruiker via het Unix-socket (peer-auth, geen wachtwoord
            # nodig) een verbinding proberen sluit dat gat, met dezelfde retry-discipline.
            & docker exec -u postgres $ContainerName psql -U $User -d $Database -c 'SELECT 1;' 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                return [pscustomobject]@{ Ready = $true; Attempts = $poging }
            }
        }
        Start-Sleep -Milliseconds 1000
    }
    return [pscustomobject]@{ Ready = $false; Attempts = $poging }
}

function Invoke-Psql {
    <#
        Voert SQL uit in de Postgres-container en geeft de ruwe uitvoer terug.

        Het wachtwoord gaat via de omgevingsvariabele PGPASSWORD van het KINDproces (docker exec
        -e), nooit als argument: argumenten zijn op beide platforms zichtbaar in de processenlijst.
        Zelfde afweging als SQLCMDPASSWORD in Test-App.ps1 (#800).

        -Tuples geeft alleen de waarden terug (psql -t -A), handig voor asserties op één getal.

        -SqlFile voert een lokaal .sql-bestand uit (bijv. een migratie- of seedbestand) in plaats
        van -Sql: het bestand wordt eerst naar de container gekopieerd (#864/#851, nodig zodra een
        meting een bestaand .sql-bestand moet hergebruiken in plaats van het inline te herhalen).
    #>
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(ParameterSetName = 'Inline', Mandatory)][string]$Sql,
        [Parameter(ParameterSetName = 'File', Mandatory)][string]$SqlFile,
        [Parameter(Mandatory)][string]$Password,
        [string]$User     = 'postgres',
        [string]$Database = 'postgres',
        [switch]$Tuples,
        [switch]$StopOnError
    )

    $args = @('exec', '-e', "PGPASSWORD=$Password", $ContainerName,
              'psql', '-U', $User, '-d', $Database)
    if ($StopOnError) { $args += @('-v', 'ON_ERROR_STOP=1') }
    if ($Tuples)      { $args += @('-t', '-A') }

    if ($PSCmdlet.ParameterSetName -eq 'File') {
        $doelPad = "/tmp/$([Guid]::NewGuid().ToString('N')).sql"
        & docker cp $SqlFile "${ContainerName}:${doelPad}" 2>&1 | Out-Null
        $args += @('-f', $doelPad)
    } else {
        $args += @('-c', $Sql)
    }

    $out = & docker @args 2>&1
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = ($out -join "`n").Trim()
    }
}

function New-SelftestPassword {
    <#
        Wegwerpwachtwoord per run. Cryptografisch willekeurig, blijft in het procesgeheugen en
        wordt nergens weggeschreven — zelfde patroon als de CI-job 'fresh-db', die per run een
        wachtwoord genereert en maskeert.
    #>
    $bytes = [byte[]]::new(16)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    # Alfanumeriek houden: sommige tools struikelen over leestekens in een wachtwoord dat via een
    # omgevingsvariabele door meerdere lagen heen gaat.
    (( $bytes | ForEach-Object { $_.ToString('x2') } ) -join '') + 'Aa1'
}

function Get-DatabaseTierProject {
    <#
        Vertaalt een tier-naam naar zijn projectpad, door scripts/ci/database-tiers.json te lezen —
        hetzelfde bestand dat resolve-database-tier.sh gebruikt (#865).

        Dupliceer deze mapping nooit: #816 legt vast dat er precies één vertaalpunt is, en die
        belofte sneuvelt zodra een tweede plek zijn eigen lijst bijhoudt.

        Geeft een object terug in plaats van te gooien, zodat de aanroeper de drie gevallen kan
        onderscheiden:
          Found=$false              -> onbekende waarde (tikfout)
          Found=$true, Built=$false -> geldige tier, boom bestaat nog niet
          Found=$true, Built=$true  -> bruikbaar; Exists zegt of het bestand er ook echt staat
    #>
    param(
        [Parameter(Mandatory)][string]$Tier,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $tabel = Join-Path $RepoRoot 'scripts' 'ci' 'database-tiers.json'
    if (-not (Test-Path $tabel)) {
        throw "Tier-tabel ontbreekt: $tabel"
    }

    $data = Get-Content $tabel -Raw | ConvertFrom-Json
    $entry = $data.tiers | Where-Object { $_.name -eq $Tier } | Select-Object -First 1

    if (-not $entry) {
        return [pscustomobject]@{
            Found = $false; Built = $false; Tier = $Tier
            Csproj = $null; Exists = $false
            Valid = ($data.tiers | ForEach-Object { $_.name })
        }
    }

    $pad = Join-Path $RepoRoot $entry.csproj
    [pscustomobject]@{
        Found     = $true
        Built     = [bool]$entry.built
        Tier      = $entry.name
        Csproj    = $entry.csproj
        FullPath  = $pad
        Exists    = (Test-Path $pad)
        EpicIssue = $entry.epicIssue
        Valid     = ($data.tiers | ForEach-Object { $_.name })
    }
}

function Get-SelftestArtifactRoot {
    <#
        De map waar één zelftestrun zijn bewijsmateriaal neerzet. In de repo (niet in temp), zodat
        het bij de code blijft die het beoordeelt; 'artifacts/' staat in .gitignore.
    #>
    param([Parameter(Mandatory)][string]$RepoRoot,
          [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))

    Join-Path $RepoRoot 'artifacts' 'selftest' $RunId
}

Export-ModuleMember -Function Get-DebugTempDir, Get-DebugPidFile, Get-DebugPorts,
    Test-PortListening, Get-PortOwner, Get-PortOwnerId, Get-ParentProcessId,
    Get-ChildProcessId, Get-ProcessTree, Stop-ProcessTree, Wait-ForPort, Wait-ForHealth,
    Wait-ForHttp, Stop-DebugServices,
    Get-SelftestPorts, Test-DockerAvailable, Get-ContainerState, Wait-ForPostgres,
    Invoke-Psql, New-SelftestPassword, Get-SelftestArtifactRoot, Get-DatabaseTierProject
