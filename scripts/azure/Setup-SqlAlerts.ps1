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
    is beschikbaar via Azure Portal → SQL Database → Metrics → New alert rule.
    Controleer de actuele kosten in CLAUDE.md (kostenbeleid) vóór aanmaken.

.PARAMETER ResourceGroup
    Resource group van de SQL Server (bijv. "myResourceGroup").

.PARAMETER SqlServerName
    Naam van de Azure SQL Server (zonder .database.windows.net).

.PARAMETER DatabaseName
    Naam van de Azure SQL Database.

.PARAMETER NotificationEmail
    E-mailadres dat alerts ontvangt.

.PARAMETER ActionGroupName
    Naam voor de Action Group (default: "ag-sql-db-alerts").

.EXAMPLE
    .\Setup-SqlAlerts.ps1 `
        -ResourceGroup "myResourceGroup" `
        -SqlServerName "myserver" `
        -DatabaseName "mydb" `
        -NotificationEmail "beheerder@[club-domein]"
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

# Verificeer login
$account = az account show --query "{sub:id,name:name}" -o json 2>/dev/null | ConvertFrom-Json
if (-not $account) {
    Write-Error "Niet ingelogd bij Azure CLI. Voer 'az login' uit."
    exit 1
}
$SubscriptionId = $account.sub
Write-Host "Subscription : $($account.name) ($SubscriptionId)"
Write-Host "Resource group: $ResourceGroup"
Write-Host "SQL Server    : $SqlServerName"
Write-Host "Database      : $DatabaseName"
Write-Host "E-mail        : $NotificationEmail"
Write-Host ""

$dbResourceId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Sql/servers/$SqlServerName/databases/$DatabaseName"

# ── STAP 1: Action Group aanmaken (gratis) ────────────────────────────────
Write-Host "[1/2] Action Group aanmaken..." -ForegroundColor Yellow
$agExists = az monitor action-group show `
    --resource-group $ResourceGroup `
    --name $ActionGroupName `
    --query id -o tsv 2>/dev/null

if ($agExists) {
    Write-Host "      Al aanwezig: $agExists" -ForegroundColor Green
    $ActionGroupId = $agExists
} else {
    $ActionGroupId = az monitor action-group create `
        --resource-group $ResourceGroup `
        --name $ActionGroupName `
        --short-name "sqlDbAlert" `
        --email-receiver name="Beheerder" email="$NotificationEmail" `
        --query id -o tsv
    Write-Host "      Aangemaakt: $ActionGroupId" -ForegroundColor Green
}

# ── STAP 2: Resource Health Alert voor SQL Database (gratis) ─────────────
Write-Host "[2/2] Resource Health Alert aanmaken voor SQL Database..." -ForegroundColor Yellow
$alertName = "sql-db-resource-health"
$alertExists = az monitor activity-log alert show `
    --resource-group $ResourceGroup `
    --name $alertName `
    --query id -o tsv 2>/dev/null

if ($alertExists) {
    Write-Host "      Al aanwezig: $alertExists" -ForegroundColor Green
} else {
    az monitor activity-log alert create `
        --name $alertName `
        --resource-group $ResourceGroup `
        --scope $dbResourceId `
        --condition "category=ResourceHealth" `
        --action-group $ActionGroupId `
        --description "Waarschuwing als Azure SQL database niet beschikbaar is (incl. Free-tier limiet bereikt)"
    Write-Host "      Resource Health Alert aangemaakt." -ForegroundColor Green
}

Write-Host ""
Write-Host "=== Klaar ===" -ForegroundColor Green
Write-Host ""
Write-Host "Alerts zijn actief. Je ontvangt een e-mail op '$NotificationEmail' als:"
Write-Host "  - De database onbereikbaar wordt (bijv. Free-tier limiet bereikt)"
Write-Host "  - De database herstelt (Resolved)"
Write-Host ""
Write-Host "TIP — Vroege waarschuwing (vóór limiet bereikt):" -ForegroundColor Cyan
Write-Host "  Azure Portal → SQL Database '$DatabaseName' → Monitoring → Metrics"
Write-Host "  Metric: 'Free amount remaining' → New alert rule → Threshold: 10.000"
Write-Host "  Controleer kosten in CLAUDE.md (kostenbeleid) vóór aanmaken."
Write-Host ""
Write-Host "Nieuw instellen? Voeg als GitHub variabele toe:"
Write-Host "  AZURE_SQL_DATABASE_NAME = $DatabaseName"
Write-Host "  (vereist door db-check job in deploy.yml)"
