# Invoke-SportlinkTokenSpike.ps1 (#990)
#
# Test of een refresh_token voor de Keycloak-client 'sportlink-club-web' (club.sportlink.com)
# buiten de browser om herhaaldelijk ververst kan worden, en of het resulterende access_token
# geldig is voor een echte Navajo-API-call. Dit is het go/no-go-bewijs voor SLX-04/#990: kan onze
# FunctionApp straks zelfstandig (zonder browser) bij Sportlink Club inloggen?
#
# WAAROM DIT SCRIPT EN NIET EEN AUTOMATISCHE BROWSER-EXTRACTIE:
# Het refresh_token is een volwaardige inlog-credential van jouw Sportlink-account. Dit script
# haalt het NOOIT zelf uit de browser — jij kopieert het zelf uit je eigen DevTools (zie
# instructies onderaan dit bestand) en plakt het hier in een -AsSecureString-prompt, exact het
# patroon van Invoke-ProductionCutoverKopie.ps1 (#976): niets op het scherm, niets in de
# PowerShell-geschiedenis, nergens naar schijf geschreven. Dit script print ZELF ook nooit een
# token-waarde — alleen lengtes, statuscodes en booleans (wel/niet gelijk, wel/niet geslaagd).
#
# GEBRUIK:
#   .\scripts\dev\Invoke-SportlinkTokenSpike.ps1
#
# Vraagt bij ELKE run opnieuw naar het refresh_token (zelfde reden als het cutover-script: een
# foutieve waarde uit een vorige poging mag niet stilzwijgend blijven hangen).
#
# AGENT-BARRIÈRE: de Read-Host-prompt hieronder is niet alleen voor jouw veiligheid maar ook een
# technische barrière tegen coding agents — die draaien in een niet-interactieve tool-omgeving
# (stdin op /dev/null) en kunnen een Read-Host-prompt dus niet invullen. Een coding agent mag dit
# mechanisme sowieso NOOIT zelf uitvoeren, zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §2.6:
# een eerder incident dwong een token-intrekking af nadat een token per ongeluk in een
# agent-chatsessie belandde.

param(
    [switch]$ReuseEnvironment
)

$ErrorActionPreference = "Stop"

if (-not $ReuseEnvironment) {
    Remove-Item Env:\SPORTLINK_REFRESH_TOKEN -ErrorAction SilentlyContinue
}

function Get-PlainTextFromSecureString {
    param([System.Security.SecureString]$Secure)
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$tokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token"
$clientId = "sportlink-club-web"
$apiBase = "https://club.sportlink.com/navajo/entity/common/clubweb"

if (-not $env:SPORTLINK_REFRESH_TOKEN) {
    Write-Host ""
    Write-Host "=== Refresh-token nodig (uit jouw eigen browser, nooit door mij uitgelezen) ===" -ForegroundColor Cyan
    Write-Host "1. Log in op https://club.sportlink.com" -ForegroundColor Cyan
    Write-Host "2. Open DevTools (F12) -> tab Application -> Local Storage -> https://club.sportlink.com" -ForegroundColor Cyan
    Write-Host "3. Zoek de sleutel 'SLC_OAUTH_TOKEN', klik erop, kopieer de waarde van het veld dat het" -ForegroundColor Cyan
    Write-Host "   refresh-token bevat (in de Console-tab kun je ook zelf uitzoeken welk veld dat is," -ForegroundColor Cyan
    Write-Host "   bv. met: JSON.parse(localStorage.getItem('SLC_OAUTH_TOKEN')) en de structuur bekijken" -ForegroundColor Cyan
    Write-Host "   -- dit gebeurt alleen in jouw eigen browserconsole, dat zie ik niet)." -ForegroundColor Cyan
    Write-Host ""
    $secure = Read-Host -AsSecureString "  SPORTLINK_REFRESH_TOKEN"
    $env:SPORTLINK_REFRESH_TOKEN = Get-PlainTextFromSecureString $secure
}

function Invoke-RefreshGrant {
    param([string]$RefreshToken, [string]$Label)

    Write-Host ""
    Write-Host "=== ${Label}: refresh_token-grant ===" -ForegroundColor Cyan
    $body = @{
        grant_type    = "refresh_token"
        client_id     = $clientId
        refresh_token = $RefreshToken
    }
    try {
        $resp = Invoke-RestMethod -Uri $tokenEndpoint -Method Post -Body $body -ContentType "application/x-www-form-urlencoded"
        Write-Host "  Status: geslaagd" -ForegroundColor Green
        Write-Host "  expires_in: $($resp.expires_in)s | refresh_expires_in: $($resp.refresh_expires_in)s | token_type: $($resp.token_type)"
        Write-Host "  access_token lengte: $($resp.access_token.Length) tekens"
        Write-Host "  refresh_token (nieuw) lengte: $($resp.refresh_token.Length) tekens"
        return $resp
    }
    catch {
        Write-Host "  Status: MISLUKT" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        if ($_.ErrorDetails.Message) { Write-Host "  Body: $($_.ErrorDetails.Message)" -ForegroundColor Red }
        return $null
    }
}

function Invoke-ApiCall {
    param([string]$AccessToken, [switch]$WithNavajoHeaders)

    # Live bevestigde headerwaarden (2026-09-04, #990) — X-Navajo-Entity is NIET een vaste
    # appnaam maar het aangeroepen entity/pad zelf (hier "user/UserInfo"); X-Navajo-Instance is
    # een vaste realm-waarde "KNVB"; X-Navajo-Locale is de korte taalcode "nl" (geen cultuurcode).
    $entityPath = "user/UserInfo"
    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($WithNavajoHeaders) {
        $headers["X-Navajo-Entity"] = $entityPath
        $headers["X-Navajo-Instance"] = "KNVB"
        $headers["X-Navajo-Locale"] = "nl"
    }
    $label = if ($WithNavajoHeaders) { "MET X-Navajo-*-headers" } else { "ZONDER X-Navajo-*-headers" }
    Write-Host ""
    Write-Host "=== API-call $entityPath ($label) ===" -ForegroundColor Cyan
    try {
        $resp = Invoke-WebRequest -Uri "$apiBase/$entityPath" -Headers $headers -Method Get
        Write-Host "  HTTP $($resp.StatusCode) — geslaagd" -ForegroundColor Green
        $json = $resp.Content | ConvertFrom-Json
        Write-Host "  Top-level velden in respons: $($json.PSObject.Properties.Name -join ', ')"
        return $true
    }
    catch {
        # Volledige diagnostiek i.p.v. alleen een (mogelijk zinloze) statuscode — een eerdere
        # versie van dit script printte alleen $_.Exception.Response.StatusCode.value__, wat bij
        # een non-HTTP-fout (DNS/TLS/timeout) een onzinnige waarde als "602" kan opleveren omdat
        # .Response dan niet bestaat. Toon nu altijd het volledige exception-type en de body.
        Write-Host "  MISLUKT" -ForegroundColor Red
        Write-Host "  Aangeroepen URL: $apiBase/$entityPath" -ForegroundColor Red
        Write-Host "  Exception-type: $($_.Exception.GetType().FullName)" -ForegroundColor Red
        Write-Host "  Message: $($_.Exception.Message)" -ForegroundColor Red
        if ($_.Exception.Response) {
            try { Write-Host "  HTTP-status: $([int]$_.Exception.Response.StatusCode)" -ForegroundColor Red } catch {}
        }
        if ($_.ErrorDetails.Message) { Write-Host "  Body: $($_.ErrorDetails.Message)" -ForegroundColor Red }
        return $false
    }
}

# --- Test 1: eerste refresh ---
$refresh1 = Invoke-RefreshGrant -RefreshToken $env:SPORTLINK_REFRESH_TOKEN -Label "Test 1"
if (-not $refresh1) {
    Write-Host ""
    Write-Host "STOP: eerste refresh is al mislukt. Refresh-token onjuist, verlopen, of client staat" -ForegroundColor Red
    Write-Host "grant_type=refresh_token niet toe voor deze client. Zie foutmelding hierboven." -ForegroundColor Red
    exit 1
}

# --- Test 2: API-call zonder de X-Navajo-*-headers (lost een [onzeker] uit het onderzoek op) ---
$apiWithoutHeaders = Invoke-ApiCall -AccessToken $refresh1.access_token -WithNavajoHeaders:$false
$apiWithHeaders = $null
if (-not $apiWithoutHeaders) {
    $apiWithHeaders = Invoke-ApiCall -AccessToken $refresh1.access_token -WithNavajoHeaders:$true
}

# --- Test 3: tweede refresh, MET het NIEUWE refresh_token uit test 1 ---
# Bewijst dat de refresh-cyclus herhaalbaar is (niet eenmalig) — cruciaal voor een backend die
# dagelijks of per verzoek een nieuw access_token nodig heeft.
$refresh2 = Invoke-RefreshGrant -RefreshToken $refresh1.refresh_token -Label "Test 2 (met het NIEUWE refresh_token uit Test 1)"

$rotated = $null
if ($refresh1 -and $refresh2) {
    $rotated = ($refresh1.refresh_token -ne $refresh2.refresh_token)
}

Write-Host ""
Write-Host "=== SAMENVATTING ===" -ForegroundColor Yellow
Write-Host "Refresh #1 (bestaand token):        $(if ($refresh1) { 'GESLAAGD' } else { 'MISLUKT' })"
Write-Host "API-call zonder X-Navajo-headers:   $(if ($apiWithoutHeaders) { 'GESLAAGD (headers dus NIET verplicht)' } else { 'MISLUKT' })"
if ($apiWithHeaders -ne $null) {
    Write-Host "API-call MET X-Navajo-headers:      $(if ($apiWithHeaders) { 'GESLAAGD (headers dus WEL verplicht)' } else { 'OOK MISLUKT' })"
}
Write-Host "Refresh #2 (nieuw token uit #1):     $(if ($refresh2) { 'GESLAAGD' } else { 'MISLUKT' })"
if ($rotated -ne $null) {
    Write-Host "Refresh-token wordt geroteerd:       $(if ($rotated) { 'JA (elke refresh geeft een nieuw refresh_token)' } else { 'NEE (zelfde refresh_token blijft geldig)' })"
}
Write-Host ""
Write-Host "Geen enkele token-waarde is hierboven afgedrukt. Bewaar het (laatst geldige) refresh_token" -ForegroundColor Cyan
Write-Host "zelf veilig als Function App-setting (zie #990-comment) als je hiermee verder gaat." -ForegroundColor Cyan
