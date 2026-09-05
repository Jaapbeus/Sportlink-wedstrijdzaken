# Invoke-SportlinkMatchProgramLookup.ps1 (#987 vervolg)
#
# #987's mapping-hypothese ("PublicMatchId = 'M' + wedstrijdcode") is weerlegd (zie
# docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §2.2). PublicMatchId moet dus via een
# reverse-lookup bij Sportlink zelf gevonden worden. Dit script test of
# competition/match/MatchProgramOverview — met een SMAL (1-daags) date-bereik in plaats van het
# brede bereik dat in het onderzoek 21 s duurde — bruikbaar is als lookup-endpoint.
#
# Kruisproef ingebouwd: standaardwaarden zijn het al live bevestigde paar uit het onderzoek
# (ExternalMatchId 3403, datum 2026-09-05 -> PublicMatchId M392686417, live gezien in de browser op
# 2026-09-04). Als dit script voor diezelfde wedstrijd ook M392686417 teruggeeft, bewijst dat de
# lookup-methode werkt — onafhankelijk van de (inmiddels weerlegde) formule-hypothese.
#
# AGENT-BARRIÈRE: zelfde patroon als Invoke-SportlinkMatchLookup.ps1 — dit script gebruikt een
# echt, opgeslagen Sportlink refresh_token. Een coding agent mag dit NOOIT zelf draaien (zie
# docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §2.6 / docs/SPORTLINK-WEB-EXTENSION.md §4.4) —
# vandaar de verplichte Read-Host-mensbevestiging hieronder, die in een niet-interactieve
# agent-tool-omgeving automatisch faalt/blokkeert.
#
# GEBRUIK:
#   .\scripts\dev\Invoke-SportlinkMatchProgramLookup.ps1
#   .\scripts\dev\Invoke-SportlinkMatchProgramLookup.ps1 -Datum 2026-09-12 -ExternalMatchId 1234 -Role Wedstrijdzaken

param(
    [string]$Datum = "2026-09-05",
    [Nullable[long]]$ExternalMatchId = 3403,
    # Rol-specifieke instellingennaam (bv. "Wedstrijdzaken"), zie Tools/SportlinkTokenCapture.
    # Zonder opgave wordt de oude, ongeschaalde sleutel SportlinkClubRefreshToken gebruikt.
    [string]$Role
)

$ErrorActionPreference = "Stop"
$tokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token"
$clientId = "sportlink-club-web"
$apiBase = "https://club.sportlink.com/navajo/entity/common/clubweb"
$settingsPath = Join-Path $PSScriptRoot "..\..\FunctionApp.Postgres\local.settings.json"
$settingsKey = if ($Role) { "SportlinkClubRefreshToken__$Role" } else { "SportlinkClubRefreshToken" }

if (-not (Test-Path $settingsPath)) {
    Write-Host "Niet gevonden: $settingsPath — draai eerst Tools/SportlinkTokenCapture." -ForegroundColor Red
    exit 1
}

# VERPLICHTE mensbevestiging — zie toelichting bovenaan dit bestand.
Write-Host "=== Mensbevestiging vereist ===" -ForegroundColor Yellow
Write-Host "Dit script gebruikt een echt, opgeslagen Sportlink refresh_token en roept" -ForegroundColor Yellow
Write-Host "MatchProgramOverview aan (het endpoint dat in het onderzoek 21s duurde bij een breed" -ForegroundColor Yellow
Write-Host "bereik). Een coding agent mag dit NOOIT zelf draaien." -ForegroundColor Yellow
$mensBevestiging = Read-Host "Typ JA om te bevestigen dat een mens dit nu zelf, interactief, uitvoert"
if ($mensBevestiging -ne "JA") {
    Write-Host "Geannuleerd — geen 'JA' ontvangen." -ForegroundColor Red
    exit 1
}

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$refreshToken = $settings.Values.$settingsKey
if ([string]::IsNullOrWhiteSpace($refreshToken)) {
    Write-Host "'$settingsKey' ontbreekt of is leeg in local.settings.json — draai eerst Tools/SportlinkTokenCapture." -ForegroundColor Red
    exit 1
}

Write-Host "=== Refresh ===" -ForegroundColor Cyan
$body = @{ grant_type = "refresh_token"; client_id = $clientId; refresh_token = $refreshToken }
try {
    $tokenResp = Invoke-RestMethod -Uri $tokenEndpoint -Method Post -Body $body -ContentType "application/x-www-form-urlencoded"
    Write-Host "  Geslaagd (expires_in: $($tokenResp.expires_in)s)" -ForegroundColor Green
}
catch {
    Write-Host "  MISLUKT: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ErrorDetails.Message) { Write-Host "  Body: $($_.ErrorDetails.Message)" -ForegroundColor Red }
    exit 1
}

# Nieuw (geroteerd) refresh_token direct terugschrijven — anders is de opgeslagen waarde na deze
# ene run verbruikt/ongeldig (rotatie bevestigd in #990).
$settingsRaw = Get-Content $settingsPath -Raw
$settingsObj = $settingsRaw | ConvertFrom-Json
$settingsObj.Values.$settingsKey = $tokenResp.refresh_token
($settingsObj | ConvertTo-Json -Depth 10) | Set-Content $settingsPath
Write-Host "  Nieuw refresh_token teruggeschreven naar local.settings.json (rotatie)." -ForegroundColor DarkGray

$entityPath = "competition/match/MatchProgramOverview"
$headers = @{
    Authorization       = "Bearer $($tokenResp.access_token)"
    "X-Navajo-Entity"   = $entityPath
    "X-Navajo-Instance" = "KNVB"
    "X-Navajo-Locale"   = "nl"
}

Write-Host ""
Write-Host "=== GET $entityPath`?DateFrom=$Datum&DateTo=$Datum ===" -ForegroundColor Cyan
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $resp = Invoke-RestMethod -Uri "$apiBase/$entityPath`?DateFrom=$Datum&DateTo=$Datum" -Headers $headers -Method Get
}
catch {
    $stopwatch.Stop()
    Write-Host "  MISLUKT na $($stopwatch.Elapsed.TotalSeconds.ToString('0.0'))s" -ForegroundColor Red
    Write-Host "  Exception-type: $($_.Exception.GetType().FullName)" -ForegroundColor Red
    Write-Host "  Message: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) { try { Write-Host "  HTTP-status: $([int]$_.Exception.Response.StatusCode)" -ForegroundColor Red } catch {} }
    if ($_.ErrorDetails.Message) { Write-Host "  Body: $($_.ErrorDetails.Message)" -ForegroundColor Red }
    exit 1
}
$stopwatch.Stop()
Write-Host "  Geslaagd in $($stopwatch.Elapsed.TotalSeconds.ToString('0.0'))s (onderzoek: 21,2s bij een 4-weken-bereik)" -ForegroundColor Green

# De respons-vorm (array direct, of genest onder een property) is niet bevestigd — probeer beide.
$matches = if ($resp -is [System.Array]) { $resp } elseif ($resp.PSObject.Properties.Name -contains "Matches") { $resp.Matches } else { @($resp) }

Write-Host ""
Write-Host "=== Wedstrijden op $Datum ($($matches.Count) gevonden) — uitsluitend niet-persoonsgebonden velden ===" -ForegroundColor Yellow
foreach ($m in $matches) {
    $markering = if ($ExternalMatchId -and $m.ExternalMatchId -eq $ExternalMatchId) { " <== GEZOCHTE WEDSTRIJD" } else { "" }
    Write-Host "  ExternalMatchId=$($m.ExternalMatchId) PublicMatchId=$($m.PublicMatchId) IsHomeMatch=$($m.IsHomeMatch)$markering"
}

if ($ExternalMatchId) {
    $gevonden = $matches | Where-Object { $_.ExternalMatchId -eq $ExternalMatchId } | Select-Object -First 1
    Write-Host ""
    Write-Host "=== #987-vervolgcheck: reverse-lookup bruikbaar? ===" -ForegroundColor Yellow
    if ($gevonden) {
        Write-Host "  ExternalMatchId $ExternalMatchId gevonden. PublicMatchId: $($gevonden.PublicMatchId)"
        if ($gevonden.PublicMatchId -eq "M392686417") {
            Write-Host "  Komt overeen met de al live bevestigde waarde uit het onderzoek (M392686417) — lookup-methode bevestigd." -ForegroundColor Green
        } else {
            Write-Host "  WIJKT AF van de eerder live bevestigde waarde (M392686417) — controleer of de wedstrijd inmiddels gewijzigd is, of dat de respons-vorm hierboven verkeerd geparsed is." -ForegroundColor Red
        }
    } else {
        Write-Host "  ExternalMatchId $ExternalMatchId NIET gevonden in de respons voor $Datum — controleer datum/respons-vorm." -ForegroundColor Red
    }
}
