# DevServices.psm1
# Gedeelde helpers voor de lokale dev-scripts (Start-Debug.ps1, Stop-Debug.ps1, Test-App.ps1).
#
# Bevat readiness-detectie en teardown, zodat geen enkel script nog vertrouwt op een
# vaste Start-Sleep. Zie #684 voor de aanleiding.

$script:DebugPorts = @{
    Azurite     = 10000
    FunctionApp = 7094
    BlazorAdmin = 5242
    Swa         = 4280
}

function Get-DebugPidFile {
    Join-Path $env:TEMP 'sportlink-debug-pids.txt'
}

function Get-DebugPorts {
    $script:DebugPorts
}

function Test-PortListening {
    param([Parameter(Mandatory)][int]$Port)
    [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Get-PortOwner {
    <#
        Retourneert het proces dat op $Port luistert, of $null.
    #>
    param([Parameter(Mandatory)][int]$Port)

    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -First 1
    if (-not $conn) { return $null }
    Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
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

        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $current" -ErrorAction SilentlyContinue
        foreach ($child in $children) { $queue.Enqueue([int]$child.ProcessId) }
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
            $parentId = (Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.Id)" -ErrorAction SilentlyContinue).ParentProcessId
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

Export-ModuleMember -Function Get-DebugPidFile, Get-DebugPorts, Test-PortListening,
    Get-PortOwner, Get-ProcessTree, Stop-ProcessTree, Wait-ForPort, Wait-ForHealth,
    Wait-ForHttp, Stop-DebugServices
