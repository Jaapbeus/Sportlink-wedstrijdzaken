<#
.SYNOPSIS
    Bewijst dat Test-PortListening (scripts/dev/DevServices.psm1) op het draaiende platform een
    echte listener daadwerkelijk ziet — en een vrije poort niet.

.DESCRIPTION
    Aanleiding is #1171: op macOS geeft GetActiveTcpListeners() uitsluitend de listeners van het
    EIGEN proces terug. Alles wat een ander proces opent — Azurite, func, dotnet watch, oftewel
    precies wat deze scripts moeten detecteren — is daar onzichtbaar, en Test-PortListening gaf
    dus altijd $false. Start-Debug.ps1 startte de services dan niet, en Test-App.ps1 sloeg zijn API- en Blazor-secties stilzwijgend
    over en meldde daarna alsnog "Geslaagd" — een controle die zich als bewijs voordeed.

    Die bug was er jarenlang zonder dat iets hem opmerkte, om één reden: er was geen test die de
    functie tegen een échte socket hield. Dit script is die test.

    Bij het repareren van #1171 ontstond meteen de spiegelbeeldige fout: de schakelaar stond op
    "niet-Windows", waardoor Linux het BCL-pad verloor dat daar juist prima werkt en een
    lsof-afhankelijkheid kreeg die in een kale container kan ontbreken. Gevonden bij de review,
    niet door een test — vandaar dat dit script in CI draait (ubuntu-latest) en zo de Linux-tak
    dekt die op een macOS- of Windows-werkplek per definitie niet langskomt.

    Draait zonder database, zonder secrets en zonder netwerktoegang naar buiten: er wordt alleen
    een TcpListener op de loopback geopend. Dus ook bruikbaar op een fork zonder CI-secrets.

.EXAMPLE
    pwsh scripts/ci/Test-PortDetection.ps1
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $RepoRoot 'scripts' 'dev' 'DevServices.psm1') -Force

$platform = if ($IsWindows) { 'Windows' } elseif ($IsMacOS) { 'macOS' } else { 'Linux' }
Write-Host "Poortdetectie testen op: $platform"

$tmp           = [System.IO.Path]::GetTempPath()
$poortBestand  = Join-Path $tmp ("poortdetectie-$PID.out")
$foutBestand   = Join-Path $tmp ("poortdetectie-$PID.err")

$fouten = 0
function Assert-Detectie {
    param([string]$Omschrijving, [bool]$Werkelijk, [bool]$Verwacht)
    if ($Werkelijk -eq $Verwacht) {
        Write-Host ("  OK   {0,-42} -> {1}" -f $Omschrijving, $Werkelijk) -ForegroundColor Green
    } else {
        Write-Host ("  FOUT {0,-42} -> {1} (verwacht {2})" -f $Omschrijving, $Werkelijk, $Verwacht) -ForegroundColor Red
        $script:fouten++
    }
}

# De listener MOET in een ander proces draaien.
#
# Dit is de kern van de test en de reden dat hij niet simpelweg een TcpListener in dit proces
# opent: op macOS ziet GetActiveTcpListeners() de listeners van het eigen proces namelijk WEL.
# Een in-process listener wordt daar dus ook door de kapotte implementatie gevonden, en zo'n test
# zou groen blijven terwijl hij niets bewijst — empirisch vastgesteld tijdens het schrijven
# hiervan. Wat Start-Debug.ps1 en Test-App.ps1 in werkelijkheid moeten zien zijn Azurite, func en
# dotnet watch: allemaal andere processen.
#
# Poort 0 laat het OS een vrije poort kiezen, zodat deze test niet botst met een dev-service of
# met een parallelle CI-job. Het kindproces schrijft de gekozen poort naar stdout en houdt de
# socket daarna open tot het wordt gestopt.
$kindScript = @'
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$l.Start()
([System.Net.IPEndPoint]$l.LocalEndpoint).Port
[Console]::Out.Flush()
while ($true) { Start-Sleep -Seconds 1 }
'@

$pwshPad = (Get-Process -Id $PID).Path
$kind = Start-Process -FilePath $pwshPad -ArgumentList '-NoProfile','-Command',$kindScript `
                      -PassThru -RedirectStandardOutput $poortBestand -RedirectStandardError $foutBestand

# Wachten tot het kind zijn poort heeft gemeld — nooit een vaste sleep.
$poort = $null
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline -and -not $poort) {
    Start-Sleep -Milliseconds 200
    if (Test-Path $poortBestand) {
        $regel = (Get-Content $poortBestand -ErrorAction SilentlyContinue | Where-Object { "$_".Trim() -match '^\d+$' } | Select-Object -First 1)
        if ($regel) { $poort = [int]"$regel".Trim() }
    }
}
if (-not $poort) {
    Write-Host "FOUT: het kindproces meldde geen poort binnen 30s." -ForegroundColor Red
    if (Test-Path $foutBestand) { Get-Content $foutBestand | Write-Host }
    if (-not $kind.HasExited) { $kind.Kill() }
    exit 1
}
Write-Host "Testlistener in kindproces $($kind.Id) op loopback-poort $poort"

try {
    Assert-Detectie 'listener in ANDER proces wordt gezien' (Test-PortListening -Port $poort) $true

    # Een tweede, niet-geopende poort: bewijst dat de functie niet simpelweg altijd $true zegt.
    $vrijeListener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $vrijeListener.Start()
    $vrijePoort = ([System.Net.IPEndPoint]$vrijeListener.LocalEndpoint).Port
    $vrijeListener.Stop()
    Start-Sleep -Milliseconds 300
    Assert-Detectie 'vrijgegeven poort wordt niet gezien' (Test-PortListening -Port $vrijePoort) $false

    # Get-PortOwnerId hoort hetzelfde beeld te geven. Twee platformnuances:
    #  - Windows: het PID kan zonder verhoogde rechten ontbreken, dus daar alleen melden.
    #  - niet-Windows: deze functie leunt op lsof. Dat zit standaard op macOS en op de
    #    GitHub-runner, maar niet per se in een kale container. Ontbreekt het, dan is dat geen
    #    falende assertie — Test-PortListening werkt op Linux immers zonder — maar wel het
    #    vermelden waard, want de teardown valt dan terug op het PID-bestand.
    $ownerId = Get-PortOwnerId -Port $poort
    if ($IsWindows) {
        Write-Host ("  INFO Get-PortOwnerId gaf '{0}' (op Windows niet geasserteerd)" -f $ownerId)
    } elseif (-not (Get-Command lsof -ErrorAction SilentlyContinue)) {
        Write-Host "  INFO lsof ontbreekt — Get-PortOwnerId niet geasserteerd (teardown valt terug op het PID-bestand)"
    } else {
        Assert-Detectie 'eigenaar-PID is het kindproces' ($ownerId -eq $kind.Id) $true
    }
}
finally {
    if (-not $kind.HasExited) { $kind.Kill(); $kind.WaitForExit(10000) | Out-Null }
    Remove-Item $poortBestand, $foutBestand -ErrorAction SilentlyContinue
}

Start-Sleep -Milliseconds 300
Assert-Detectie 'na Stop() niet meer gezien' (Test-PortListening -Port $poort) $false

if ($fouten -gt 0) {
    Write-Host ""
    Write-Host "$fouten controle(s) gefaald: Test-PortListening klopt niet op $platform." -ForegroundColor Red
    Write-Host "Zie #1171 — een verkeerde uitkomst hier maakt Start-Debug.ps1 en Test-App.ps1 stil onbetrouwbaar." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Poortdetectie correct op $platform." -ForegroundColor Green
exit 0
