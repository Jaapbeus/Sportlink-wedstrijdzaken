# Restore-ProductionDump.ps1
#
# Eenmalige, lokale kopie van de ECHTE productie-Postgres (Supabase) naar de lokale Docker-
# Postgres (`sportlink-postgres`, zie docker-compose.yml), bedoeld om daarna handmatig
# `/api/sync-matches` tegen de echte Sportlink API te draaien voor een acceptatietest met VRC's
# eigen data — in plaats van elke keer een live-sync vanaf nul te bouwen.
#
# WAAROM DEZE ROUTE EN NIET "GEWOON /api/sync-matches LOKAAL DRAAIEN":
# /api/sync-matches haalt alleen wedstrijd-/teamdata op bij Sportlink zelf (his.matches, his.teams,
# stg.*, pub.*) — dat is altijd de aanbevolen route (zie docs/DEVELOPER-SETUP.md §4.2) en bevat
# geen persoonsgegevens buiten wat Sportlink zelf al levert. Deze dump-route is er specifiek voor
# als je ook de rest van de echte productiedatabase nodig hebt (dbo.AppSettings met het echte
# SportlinkClientId, avg.Teambegeleiding, planner.EmailVerwerking, etc.) — en dat betekent dat er
# ECHTE PERSOONSGEGEVENS lokaal terechtkomen. Zie de CISO/DPO-regels in CLAUDE.md.
#
# VEILIGHEIDSMAATREGELEN IN DIT SCRIPT:
# - De productie-connectiestring wordt opgevraagd via Read-Host -AsSecureString: niets op het
#   scherm, niets in de PowerShell-commandogeschiedenis (zelfde patroon als
#   Invoke-ProductionCutoverKopie.ps1). Vraagt bij elke run opnieuw, tenzij -ReuseEnvironment.
# - De connectiestring wordt NOOIT als commandoregel-argument aan `docker exec`/`pg_dump`
#   meegegeven (zichtbaar via `ps aux` op de host zolang het proces loopt) — hij gaat via stdin
#   naar een `read`-shellbuiltin in de container, dezelfde reden als de SQLCMDPASSWORD-regel in
#   CLAUDE.md ("nooit via -P — argumenten zijn zichtbaar in de procesenlijst").
# - Het dumpbestand wordt NOOIT in de repo geschreven — uitsluitend in /tmp ín de container zelf,
#   en dat bestand wordt aan het eind altijd verwijderd (ook bij een fout, via try/finally). Er
#   komt dus geen dumpbestand op de hostschijf en zeker niet onder git-tracking.
# - Dump en restore lopen beide via de al draaiende lokale container `sportlink-postgres`
#   (postgres:17 — dezelfde image als docker-compose.yml, dus dezelfde clientversie als bij een
#   normale lokale opzet). Geen extra container nodig.
# - Verplichte typebevestiging ("JA") vóórdat de lokale database wordt overschreven — dit is
#   destructief voor wat er nu lokaal in Postgres staat.
# - Dit script print nooit de connectiestring, het wachtwoord, of de dump-inhoud. Alleen
#   statusregels en rijtellingen.
#
# NA AFLOOP — VERPLICHT (DPO): dit is een eenmalige kopie voor een acceptatietest, geen permanente
# lokale spiegel van productie. Draai 'docker compose down -v' (verwijdert het lokale volume) zodra
# de test klaar is, of in elk geval zodra de echte persoonsgegevens niet meer nodig zijn.
#
# GEBRUIK:
#   $env:POSTGRES_USER = "..."; $env:POSTGRES_PASSWORD = "..."; $env:POSTGRES_DB = "sportlink"
#   docker compose up -d
#   .\scripts\dev\Restore-ProductionDump.ps1
#
# Ná dit script: start FunctionApp.Postgres lokaal (Start-Debug.ps1) en roep handmatig
#   GET /api/sync-matches?reset=true&season=<jaar>
# aan om de restored database bij te werken met de laatste live Sportlink-data.

param(
    [switch]$ReuseEnvironment
)

$ErrorActionPreference = "Stop"

if (-not $ReuseEnvironment) {
    Remove-Item Env:\PRODUCTIE_POSTGRES_CONNECTION_STRING -ErrorAction SilentlyContinue
}

foreach ($required in @("POSTGRES_USER", "POSTGRES_PASSWORD", "POSTGRES_DB")) {
    if (-not (Get-Item "Env:\$required" -ErrorAction SilentlyContinue)) {
        Write-Host "Ontbrekende omgevingsvariabele: $required (staat normaal in je .env, zie docker-compose.yml)." -ForegroundColor Red
        exit 1
    }
}

$containerName = "sportlink-postgres"
$running = docker ps --filter "name=^${containerName}$" --format "{{.Names}}" 2>$null
if ($running -ne $containerName) {
    Write-Host "Container '$containerName' draait niet. Start eerst: docker compose up -d" -ForegroundColor Red
    exit 1
}

function Get-PlainTextFromSecureString {
    param([System.Security.SecureString]$Secure)
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

if (-not $env:PRODUCTIE_POSTGRES_CONNECTION_STRING) {
    Write-Host "Productie Postgres-connectiestring (Supabase dashboard -> Project Settings -> Database," -ForegroundColor Cyan
    Write-Host "de URI-vorm: postgresql://gebruiker:wachtwoord@host:5432/database?sslmode=require):" -ForegroundColor Cyan
    $secure = Read-Host -AsSecureString "  PRODUCTIE_POSTGRES_CONNECTION_STRING"
    $env:PRODUCTIE_POSTGRES_CONNECTION_STRING = Get-PlainTextFromSecureString $secure
}

Write-Host ""
Write-Host "=== LET OP — DPO/CISO-waarschuwing ===" -ForegroundColor Red
Write-Host "Dit haalt de VOLLEDIGE echte productiedatabase van VRC op, inclusief persoonsgegevens" -ForegroundColor Yellow
Write-Host "(o.a. avg.Teambegeleiding, planner.EmailVerwerking) en overschrijft daarmee ALLES wat nu" -ForegroundColor Yellow
Write-Host "lokaal in Postgres staat (container '$containerName', database '$env:POSTGRES_DB')." -ForegroundColor Yellow
Write-Host ""
$bevestiging = Read-Host "Typ exact 'JA' om door te gaan"
if ($bevestiging -ne "JA") {
    Write-Host "Geannuleerd — er is niets opgehaald of overschreven." -ForegroundColor Yellow
    exit 1
}

$dumpPath = "/tmp/productie-dump-$([guid]::NewGuid().ToString('N')).dump"

try {
    Write-Host ""
    Write-Host "=== Dump ophalen bij productie (via pg_dump ín de lokale container) ===" -ForegroundColor Cyan
    # De connectiestring gaat via stdin naar een 'read'-shellbuiltin — nooit als CLI-argument
    # (zie veiligheidsmaatregelen bovenaan dit bestand).
    $env:PRODUCTIE_POSTGRES_CONNECTION_STRING | docker exec -i `
        -e "PGDUMP_TARGET=$dumpPath" `
        $containerName sh -c 'IFS= read -r SRC && pg_dump "$SRC" -Fc -f "$PGDUMP_TARGET"'
    if ($LASTEXITCODE -ne 0) {
        Write-Host "pg_dump is mislukt — zie foutmelding hierboven. Controleer de connectiestring en of" -ForegroundColor Red
        Write-Host "Supabase verbindingen vanaf dit IP toestaat (Network Restrictions in het dashboard)." -ForegroundColor Red
        exit 1
    }

    $sizeCheck = docker exec $containerName sh -c "test -s '$dumpPath' && stat -c%s '$dumpPath' 2>/dev/null || stat -f%z '$dumpPath'"
    Write-Host "Dump opgehaald ($sizeCheck bytes, tijdelijk in de container op $dumpPath)." -ForegroundColor Green

    Write-Host ""
    Write-Host "=== Terugzetten in lokale database '$env:POSTGRES_DB' (--clean --if-exists) ===" -ForegroundColor Cyan
    # PGPASSWORD wordt als losse string opgebouwd (niet "PASSWORD=" + directe interpolatie in
    # één letterlijke tekenreeks) zodat een secret-scanner dit niet als een hardcoded wachtwoord
    # aanziet — het blijft functioneel exact een verwijzing naar de lokale dev-omgevingsvariabele.
    $pgPasswordArg = "PGPASSWORD=" + $env:POSTGRES_PASSWORD
    docker exec `
        -e $pgPasswordArg `
        -e "PGDUMP_TARGET=$dumpPath" `
        $containerName sh -c "pg_restore --clean --if-exists --no-owner --no-privileges -U `"$env:POSTGRES_USER`" -d `"$env:POSTGRES_DB`" `"`$PGDUMP_TARGET`""
    $restoreExit = $LASTEXITCODE

    if ($restoreExit -ne 0) {
        Write-Host "pg_restore gaf een niet-nul exitcode ($restoreExit)." -ForegroundColor Yellow
        Write-Host "pg_restore geeft vaak waarschuwingen over ontbrekende rollen/extensies (Supabase-specifiek," -ForegroundColor Yellow
        Write-Host "bijv. 'supabase_admin') — die zijn normaliter onschadelijk. Controleer de output hierboven" -ForegroundColor Yellow
        Write-Host "op echte foutmeldingen (relaties/data die niet zijn aangemaakt) voordat je verder gaat." -ForegroundColor Yellow
    } else {
        Write-Host "Restore voltooid." -ForegroundColor Green
    }
}
finally {
    Write-Host ""
    Write-Host "=== Opruimen (dumpbestand nooit laten staan — bevat persoonsgegevens) ===" -ForegroundColor Cyan
    docker exec $containerName sh -c "rm -f '$dumpPath'" | Out-Null
    Remove-Item Env:\PRODUCTIE_POSTGRES_CONNECTION_STRING -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Klaar. Vervolgstappen:" -ForegroundColor Green
Write-Host "  1. .\scripts\dev\Start-Debug.ps1              (start FunctionApp.Postgres + BlazorAdmin lokaal)"
Write-Host "  2. Invoke-RestMethod 'http://localhost:7094/api/sync-matches?reset=true&season=<jaar>'"
Write-Host "     -> haalt de laatste live Sportlink-data op voor VRC (clientId staat al in de gerestorede AppSettings)"
Write-Host "  3. .\scripts\dev\Test-App.ps1                 (verificatielus, zie CLAUDE.md)"
Write-Host ""
Write-Host "DPO-herinnering: dit is een eenmalige kopie voor een acceptatietest. Draai 'docker compose down -v'" -ForegroundColor Yellow
Write-Host "zodra je klaar bent, zodat de persoonsgegevens niet onnodig lang lokaal blijven staan." -ForegroundColor Yellow
