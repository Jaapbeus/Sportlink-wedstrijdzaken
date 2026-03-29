# Quick Debug Readiness Check
$sqlServer = 'YOUR_SERVER'  # TODO: replace with your local SQL Server instance name
$database = 'SportlinkSqlDb'

Write-Host ''
Write-Host '========================================'  -ForegroundColor Cyan
Write-Host 'DEBUG READINESS CHECK' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

$allGood = $true

Write-Host '[1] SQL Server Connection...' -ForegroundColor Yellow
$result = sqlcmd -S $sqlServer -E -C -d $database -Q 'SELECT 1' -h -1 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host '    [OK] Connected to '$sqlServer'\'$database -ForegroundColor Green
} else {
    Write-Host '    [FAIL] Cannot connect to SQL Server' -ForegroundColor Red
    $allGood = $false
}

Write-Host '[2] Azurite Storage Emulator...' -ForegroundColor Yellow
$azurite = Get-Process azurite -ErrorAction SilentlyContinue
if ($azurite) {
    Write-Host '    [OK] Azurite is running' -ForegroundColor Green
} else {
    Write-Host '    [WARN] Azurite not running' -ForegroundColor Yellow
}

Write-Host '[3] Configuration File...' -ForegroundColor Yellow
if (Test-Path 'local.settings.json') {
    Write-Host '    [OK] local.settings.json found' -ForegroundColor Green
} else {
    Write-Host '    [FAIL] local.settings.json missing' -ForegroundColor Red
    $allGood = $false
}

Write-Host '[4] Database Objects...' -ForegroundColor Yellow
$result = sqlcmd -S $sqlServer -E -C -d $database -Q 'SELECT COUNT(*) FROM sys.procedures WHERE name IN (''sp_MergeStgToHis'', ''sp_CreateTargetTableFromSource'')' -h -1 2>&1
if ($result -match '2') {
    Write-Host '    [OK] Stored procedures exist' -ForegroundColor Green
} else {
    Write-Host '    [FAIL] Required stored procedures missing' -ForegroundColor Red
    $allGood = $false
}

Write-Host '[5] API Configuration...' -ForegroundColor Yellow
$result = sqlcmd -S $sqlServer -E -C -d $database -Q 'SELECT COUNT(*) FROM dbo.AppSettings WHERE sportlinkApiUrl IS NOT NULL' -h -1 2>&1
if ($result -match '1') {
    Write-Host '    [OK] API settings configured' -ForegroundColor Green
} else {
    Write-Host '    [WARN] API settings may need configuration' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan

if ($allGood) {
    Write-Host '[SUCCESS] READY TO DEBUG!' -ForegroundColor Green
    Write-Host ''
    Write-Host 'Next steps:' -ForegroundColor Cyan
    Write-Host '  1. Press F5 in Visual Studio'
    Write-Host '  2. Function runs every minute'
    Write-Host '  3. Watch console for logs'
    Write-Host ''
    Write-Host 'Local endpoint: http://localhost:7071' -ForegroundColor Gray
} else {
    Write-Host '[ERROR] NOT READY - Fix the issues above' -ForegroundColor Red
}

Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''
