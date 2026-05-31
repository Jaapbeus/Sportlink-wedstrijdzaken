<#
.SYNOPSIS
    Maakt gratis Azure Monitor alerts aan voor de Azure SQL Free-tier database.

.DESCRIPTION
    Eenmalig uitvoeren per club. Maakt aan:
      1. Action Group met e-mailnotificatie (gratis)
      2. Resource Health Alert voor de SQL database (gratis)

    Kosten: NIETS — Resource Health Alerts zijn altijd gratis.
    Bron: https://learn.microsoft.com/azure/azure-monitor/fundamentals/best-practices-cost#alerts

    TIP: Metric Alert "Free amount remaining < 10.000 vCore-seconden" (vroege waarschuwing)
    is beschikbaar via Azure Portal -> SQL Database -> Metrics -> New alert rule.
    Controleer de actuele kosten in CLAUDE.md (kostenbeleid) voor aanmaken.
#>
param(
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$SqlServerName,
    [Parameter(Mandatory)][string]$DatabaseName,
    [Parameter(Mandatory)][string]$NotificationEmail,
    [string]$ActionGroupName = "ag-sql-db-alerts"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Write-Host ""
Write-Host "=== Setup-SqlAlerts.ps1 ===" -ForegroundColor Cyan
Write-Host "Gratis Azure Monitor alerts voor Azure SQL Free-tier database"
Write-Host ""

# Verificeer login — 2>$null is de PowerShell-equivalent van bash 2>/dev/null
$accountJson = az account show --query "{sub:id,name:name}" -o json 2>$null
if (-not $accountJson) {
    Write-Error "Niet ingelogd bij Azure CLI. Voer 'az login' uit."
    exit 1
}
$account = $accountJson | ConvertFrom-Json
$SubscriptionId = $account.sub

Write-Host "Subscription  : $($account.name) ($SubscriptionId)"
Write-Host "Resource group: $ResourceGroup"
Write-Host "SQL Server    : $SqlServerName"
Write-Host "Database      : $DatabaseName"
Write-Host "E-mail        : $NotificationEmail"
Write-Host ""

$dbResourceId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Sql/servers/$SqlServerName/databases/$DatabaseName"

# ── STAP 1: Action Group aanmaken (gratis) ────────────────────────────────
Write-Host "[1/2] Action Group aanmaken..." -ForegroundColor Yellow

$agJson = az monitor action-group show --resource-group $ResourceGroup --name $ActionGroupName -o json 2>$null
if ($agJson) {
    $ActionGroupId = ($agJson | ConvertFrom-Json).id
    Write-Host "      Al aanwezig: $ActionGroupId" -ForegroundColor Green
} else {
    # az CLI positional syntax: --action email NAME EMAIL_ADDRESS
    $agResult = az monitor action-group create `
        --resource-group $ResourceGroup `
        --name $ActionGroupName `
        --short-name "sqlDbAlert" `
        --action email Beheerder $NotificationEmail `
        -o json 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Action Group aanmaken mislukt:`n$agResult"
        exit 1
    }
    $ActionGroupId = ($agResult | ConvertFrom-Json).id
    Write-Host "      Aangemaakt: $ActionGroupId" -ForegroundColor Green
}

if ([string]::IsNullOrEmpty($ActionGroupId)) {
    Write-Error "Action Group ID is leeg — kan alert niet aanmaken."
    exit 1
}

# ── STAP 2: Resource Health Alert voor SQL Database (gratis) ─────────────
Write-Host "[2/2] Resource Health Alert aanmaken voor SQL Database..." -ForegroundColor Yellow
$alertName = "sql-db-resource-health"

$alertJson = az monitor activity-log alert show --resource-group $ResourceGroup --name $alertName -o json 2>$null
if ($alertJson) {
    $AlertId = ($alertJson | ConvertFrom-Json).id
    Write-Host "      Al aanwezig: $AlertId" -ForegroundColor Green
} else {
    $alertResult = az monitor activity-log alert create `
        --name $alertName `
        --resource-group $ResourceGroup `
        --scope $dbResourceId `
        --condition "category=ResourceHealth" `
        --action-group $ActionGroupId `
        --description "Waarschuwing als Azure SQL database niet beschikbaar is (incl. Free-tier limiet bereikt)" `
        -o json 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Resource Health Alert aanmaken mislukt:`n$alertResult"
        exit 1
    }
    Write-Host "      Aangemaakt." -ForegroundColor Green
}

Write-Host ""
Write-Host "=== Klaar ===" -ForegroundColor Green
Write-Host ""
Write-Host "Alerts zijn actief. Je ontvangt een e-mail op '$NotificationEmail' als:"
Write-Host "  - De database onbereikbaar wordt (bijv. Free-tier limiet bereikt)"
Write-Host "  - De database herstelt (Resolved)"
Write-Host ""
Write-Host "TIP: Vroege waarschuwing (voor limiet bereikt):" -ForegroundColor Cyan
Write-Host "  Azure Portal -> SQL Database '$DatabaseName' -> Monitoring -> Metrics"
Write-Host "  Metric: 'Free amount remaining' -> New alert rule -> Threshold: 10.000"
Write-Host "  Controleer kosten in CLAUDE.md (kostenbeleid) voor aanmaken."
Write-Host ""
Write-Host "Vergeet niet als GitHub variabele toe te voegen:"
Write-Host "  AZURE_SQL_DATABASE_NAME = $DatabaseName"
Write-Host "  (vereist door db-check job in deploy.yml)"
