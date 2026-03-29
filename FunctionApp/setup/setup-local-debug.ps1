# Setup Local Debug Environment for Sportlink Azure Function
# This script sets up the local development environment

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Sportlink Local Debug Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Check if SQL Server is accessible
Write-Host "Step 1: Checking SQL Server connection..." -ForegroundColor Yellow
$sqlServer = "YOUR_SERVER"  # TODO: replace with your local SQL Server instance name
$database = "SportlinkSqlDb"
$connectionString = "Server=$sqlServer;Database=master;Integrated Security=True;TrustServerCertificate=True;"

try {
    $connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $connection.Open()
    Write-Host "✓ SQL Server is accessible" -ForegroundColor Green
    
    # Check if database exists
    $checkDbQuery = "SELECT database_id FROM sys.databases WHERE Name = '$database'"
    $command = New-Object System.Data.SqlClient.SqlCommand($checkDbQuery, $connection)
    $result = $command.ExecuteScalar()
    
    if ($null -eq $result) {
        Write-Host "⚠ Database '$database' does not exist on $sqlServer" -ForegroundColor Red
        Write-Host "  You need to create it or restore from the SportlinkSqlDb repository" -ForegroundColor Yellow
        Write-Host "  Repository location: C:\Repos\VRC\SportlinkSqlDb" -ForegroundColor Yellow
    } else {
        Write-Host "✓ Database '$database' exists" -ForegroundColor Green
    }
    
    $connection.Close()
} catch {
    Write-Host "✗ Cannot connect to SQL Server: $sqlServer" -ForegroundColor Red
    Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Please ensure SQL Server is running and accessible" -ForegroundColor Yellow
}

Write-Host ""

# Step 2: Check if Azurite is installed and running
Write-Host "Step 2: Checking Azurite (Azure Storage Emulator)..." -ForegroundColor Yellow

# Check if Azurite is installed
$azuriteInstalled = Get-Command azurite -ErrorAction SilentlyContinue

if ($null -eq $azuriteInstalled) {
    Write-Host "⚠ Azurite is not installed" -ForegroundColor Red
    Write-Host "  Installing Azurite globally..." -ForegroundColor Yellow
    try {
        npm install -g azurite
        Write-Host "✓ Azurite installed successfully" -ForegroundColor Green
    } catch {
        Write-Host "✗ Failed to install Azurite" -ForegroundColor Red
        Write-Host "  Please install Node.js first, then run: npm install -g azurite" -ForegroundColor Yellow
    }
} else {
    Write-Host "✓ Azurite is installed" -ForegroundColor Green
}

# Check if Azurite is running
$azuriteRunning = Get-Process -Name "azurite" -ErrorAction SilentlyContinue

if ($null -eq $azuriteRunning) {
    Write-Host "⚠ Azurite is not running" -ForegroundColor Yellow
    Write-Host "  Starting Azurite..." -ForegroundColor Yellow
    
    # Start Azurite in a new window
    Start-Process -FilePath "azurite" -ArgumentList "--silent" -WindowStyle Hidden
    Start-Sleep -Seconds 2
    
    $azuriteRunning = Get-Process -Name "azurite" -ErrorAction SilentlyContinue
    if ($null -ne $azuriteRunning) {
        Write-Host "✓ Azurite started successfully" -ForegroundColor Green
    } else {
        Write-Host "✗ Failed to start Azurite automatically" -ForegroundColor Red
        Write-Host "  Please start it manually: azurite" -ForegroundColor Yellow
    }
} else {
    Write-Host "✓ Azurite is running" -ForegroundColor Green
}

Write-Host ""

# Step 3: Verify local.settings.json
Write-Host "Step 3: Verifying local.settings.json..." -ForegroundColor Yellow

if (Test-Path "local.settings.json") {
    $settings = Get-Content "local.settings.json" | ConvertFrom-Json
    
    if ($settings.Values.SqlConnectionString -notlike "*YOUR_SERVER*" -and $settings.Values.SqlConnectionString -notlike "*localhost*") {
        Write-Host "✓ SqlConnectionString is set to local server" -ForegroundColor Green
    } else {
        Write-Host "⚠ SqlConnectionString might not be pointing to local server" -ForegroundColor Yellow
    }
    
    if ($settings.Values.AzureWebJobsStorage -eq "UseDevelopmentStorage=true") {
        Write-Host "✓ AzureWebJobsStorage is set to use local storage emulator" -ForegroundColor Green
    } else {
        Write-Host "⚠ AzureWebJobsStorage is not set to use local emulator" -ForegroundColor Yellow
    }
    
    if ($settings.Values.FUNCTIONS_WORKER_RUNTIME -eq "dotnet-isolated") {
        Write-Host "✓ FUNCTIONS_WORKER_RUNTIME is correctly set" -ForegroundColor Green
    }
} else {
    Write-Host "✗ local.settings.json not found!" -ForegroundColor Red
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Setup Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "1. Ensure the SportlinkSqlDb database is created/restored" -ForegroundColor White
Write-Host "2. Press F5 in Visual Studio to start debugging" -ForegroundColor White
Write-Host ""
