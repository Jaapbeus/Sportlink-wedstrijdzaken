# Invoke-SportlinkMatchLookup.ps1 (#987)
#
# Sluit de allereerste openstaande vraag van epic #986 af: klopt de hypothese
# PublicMatchId = "M" + wedstrijdcode / ExternalMatchId = wedstrijdnummer?
#
# Gebruikt het refresh_token dat Tools/SportlinkTokenCapture al heeft opgeslagen in
# FunctionApp.Postgres/local.settings.json (sleutel SportlinkClubRefreshToken) — geen handmatige
# invoer meer nodig. Ververst het, roept GET competition/match/Match?PublicMatchId=... aan met de
# live bevestigde X-Navajo-*-headerwaarden (#990), en toont ALLEEN een veilige, AVG-vrije
# selectie van velden (geen scheidsrechters/officials — die bevatten persoonsgegevens, zie #991).
#
# BELANGRIJK: elke refresh roteert het refresh_token (bevestigd in #990). Dit script schrijft het
# NIEUWE refresh_token na afloop terug naar local.settings.json, anders raakt de opgeslagen waarde
# na één run verbruikt/ongeldig.
#
# GEBRUIK:
#   .\scripts\dev\Invoke-SportlinkMatchLookup.ps1 -PublicMatchId M392686417
#   .\scripts\dev\Invoke-SportlinkMatchLookup.ps1 -ExternalMatchId 3403 -WedstrijdCode 392686417   # verifieert de mapping zelf

param(
    [string]$PublicMatchId,
    [Nullable[long]]$ExternalMatchId,
    [Nullable[long]]$WedstrijdCode,
    # Rol-specifieke instellingennaam (bv. "Wedstrijdzaken"), zie Tools/SportlinkTokenCapture.
    # Zonder opgave wordt de oude, ongeschaalde sleutel SportlinkClubRefreshToken gebruikt
    # (bestaand, al gevuld token uit vóór de rolindeling blijft dus bruikbaar).
    [string]$Role
)

$ErrorActionPreference = "Stop"
$tokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token"
$clientId = "sportlink-club-web"
$apiBase = "https://club.sportlink.com/navajo/entity/common/clubweb"
$settingsPath = Join-Path $PSScriptRoot "..\..\FunctionApp.Postgres\local.settings.json"
$settingsKey = if ($Role) { "SportlinkClubRefreshToken__$Role" } else { "SportlinkClubRefreshToken" }

if (-not $PublicMatchId) {
    if (-not $WedstrijdCode) {
        Write-Host "Geef -PublicMatchId M<wedstrijdcode> of -WedstrijdCode <getal> op." -ForegroundColor Red
        exit 1
    }
    $PublicMatchId = "M$WedstrijdCode"
}

if (-not (Test-Path $settingsPath)) {
    Write-Host "Niet gevonden: $settingsPath — draai eerst Tools/SportlinkTokenCapture." -ForegroundColor Red
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
# ene run verbruikt.
$settingsRaw = Get-Content $settingsPath -Raw
$settingsObj = $settingsRaw | ConvertFrom-Json
$settingsObj.Values.$settingsKey = $tokenResp.refresh_token
($settingsObj | ConvertTo-Json -Depth 10) | Set-Content $settingsPath
Write-Host "  Nieuw refresh_token teruggeschreven naar local.settings.json (rotatie)." -ForegroundColor DarkGray

$entityPath = "competition/match/Match"
$headers = @{
    Authorization      = "Bearer $($tokenResp.access_token)"
    "X-Navajo-Entity"   = $entityPath
    "X-Navajo-Instance" = "KNVB"
    "X-Navajo-Locale"   = "nl"
}

Write-Host ""
Write-Host "=== GET $entityPath`?PublicMatchId=$PublicMatchId ===" -ForegroundColor Cyan
try {
    $resp = Invoke-RestMethod -Uri "$apiBase/$entityPath`?PublicMatchId=$PublicMatchId" -Headers $headers -Method Get
}
catch {
    Write-Host "  MISLUKT" -ForegroundColor Red
    Write-Host "  Exception-type: $($_.Exception.GetType().FullName)" -ForegroundColor Red
    Write-Host "  Message: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) { try { Write-Host "  HTTP-status: $([int]$_.Exception.Response.StatusCode)" -ForegroundColor Red } catch {} }
    if ($_.ErrorDetails.Message) { Write-Host "  Body: $($_.ErrorDetails.Message)" -ForegroundColor Red }
    exit 1
}

# Uitsluitend niet-persoonsgebonden velden tonen — GEEN scheidsrechters/officials/contactgegevens.
Write-Host "  Geslaagd" -ForegroundColor Green
Write-Host ""
Write-Host "=== Veilige velden (geen persoonsgegevens) ===" -ForegroundColor Yellow
Write-Host "  PublicMatchId:    $($resp.PublicMatchId)"
Write-Host "  ExternalMatchId:  $($resp.ExternalMatchId)"
Write-Host "  MatchDate:        $($resp.MatchDate)"
Write-Host "  MatchStatus:      $($resp.MatchStatus)"
Write-Host "  IsHomeMatch:      $($resp.IsHomeMatch)"
Write-Host "  IsCanceledMatch:  $($resp.IsCanceledMatch)"
Write-Host "  IsConceptMatch:   $($resp.IsConceptMatch)"
Write-Host "  TaskStatus:       $($resp.TaskStatus -join ', ')"
Write-Host "  IsAssignDressingRoomsAllowed: $($resp.IsAssignDressingRoomsAllowed)"
Write-Host "  IsEditFieldAllowed:           $($resp.IsEditFieldAllowed)"
Write-Host "  IsAssignOfficialsAllowed:     $($resp.IsAssignOfficialsAllowed)"
Write-Host "  IsAddScoreAllowed:            $($resp.IsAddScoreAllowed)"
Write-Host ""

if ($ExternalMatchId -and $WedstrijdCode) {
    $extMatch = ($resp.ExternalMatchId -eq $ExternalMatchId)
    $mappingHolds = ($PublicMatchId -eq "M$WedstrijdCode")
    Write-Host "=== #987-mappingcheck ===" -ForegroundColor Yellow
    Write-Host "  ExternalMatchId komt overeen met opgegeven wedstrijdnummer: $extMatch"
    Write-Host "  PublicMatchId = 'M' + wedstrijdcode klopt voor dit record:  $mappingHolds"
}
