# Sportlink Azure Function - Complete Setup Guide

This guide covers **all prerequisites and configuration steps** required before running the Sportlink Azure Function application.

---

## 🚀 Quick Start (TL;DR)

If you just want to get running quickly:

1. **Database Setup:**
   ```sql
   -- In SSMS connected to YOUR_SERVER, run these scripts in order:
   -- 1. setup-local-database.sql
   -- 2. setup-metadata-tables.sql
   -- 3. Deploy stored procedures from C:\Repos\VRC\SportlinkSqlDb
   ```

2. **Configure API Credentials:**
   ```sql
   UPDATE [dbo].[AppSettings]
   SET sportlinkApiUrl = 'https://data.sportlink.com',
       sportlinkClientId = 'YOUR_ACTUAL_CLIENT_ID'
   WHERE Id = 1;
   ```

3. **Start Azurite:**
   ```powershell
   azurite --silent
   ```

4. **Debug in Visual Studio:**
   - Set configuration to **Debug** (not Release)
   - Press **F5**

For detailed steps, continue reading below.

---

## 📋 Table of Contents

1. [Prerequisites](#prerequisites)
2. [Software Installation](#software-installation)
3. [Git Hooks Setup](#git-hooks-setup)
4. [Database Setup](#database-setup)
5. [Sportlink API Credentials Setup](#sportlink-api-credentials-setup)
6. [Local Settings Configuration](#local-settings-configuration)
7. [Stored Procedures Deployment](#stored-procedures-deployment)
8. [Metadata Table Setup](#metadata-table-setup)
9. [Environment Verification](#environment-verification)
10. [First Run](#first-run)
11. [Troubleshooting](#troubleshooting)

---

## 3. Git Hooks Setup

This repository includes **pre-commit** and **pre-push** hooks that scan for sensitive data (passwords, API keys, server names, credentials) before allowing commits or pushes to GitHub. This prevents accidental exposure of secrets.

### 3.1 Activate Git Hooks

After cloning the repository, configure git to use the versioned hooks directory:

```bash
git config core.hooksPath .githooks
```

### 3.2 Configure Sensitive Patterns

The hooks read patterns from `.githooks/sensitive-patterns.txt` (which is gitignored and never pushed). Create it from the template:

```bash
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Then edit `.githooks/sensitive-patterns.txt` and add your project-specific patterns — real passwords, server names, client IDs, and SQL logins that should never appear in a commit. For example:

```
# Generic patterns (detect credentials in connection strings)
Password=[^;'"`<>{}]{4,}
PWD=[^;'"`<>{}]{4,}
clientId=[A-Za-z0-9]{6,}

# Project-specific patterns (add your own real values)
# MySecretPassword
# MyClientId123
# Server=MYSERVERNAME
# my_sql_login_name
```

### 3.3 Verify Hooks Are Active

Test that the hooks work by staging a file and committing:

```bash
git commit --allow-empty -m "test hooks"
```

You should see: `🔍 Scanning staged files for sensitive data...` followed by `✅ No sensitive data detected.`

If you don't see the scanning message, verify `core.hooksPath` is set:

```bash
git config core.hooksPath
# Should output: .githooks
```

---

## 1. Prerequisites

Before starting, ensure you have:

### Required Software
- [ ] **Visual Studio 2022/2026** (Community, Professional, or Enterprise)
- [ ] **SQL Server** (Local instance or accessible server)
- [ ] **SQL Server Management Studio (SSMS)** 18.0 or higher
- [ ] **Node.js** (Latest LTS version) - for Azurite
- [ ] **.NET 10 SDK** (should be included with VS 2026)
- [ ] **Azure Functions Core Tools** (v4.x)

### Required Access
- [ ] **Sportlink API Access** - You need valid API credentials
- [ ] **SQL Server Access** - Integrated Security or SQL authentication
- [ ] **Network Access** - To reach Sportlink API endpoints

### Required Knowledge
- [ ] Sportlink API URL
- [ ] Sportlink Client ID
- [ ] SQL Server instance name (e.g., `YOUR_SERVER`)

---

## 2. Software Installation

### 2.1 Install Node.js and Azurite

Azurite is the Azure Storage Emulator needed for local development.

```powershell
# Install Node.js from https://nodejs.org/ (if not already installed)
# Then install Azurite globally
npm install -g azurite
```

### 2.2 Verify Azure Functions Core Tools

```powershell
# Check if installed
func --version

# Should return: 4.x.x
# If not installed, run:
npm install -g azure-functions-core-tools@4 --unsafe-perm true
```

---

## 3. Database Setup

### 3.1 Create Local Database

**Option A: Using the provided script**

1. Open **SQL Server Management Studio (SSMS)**
2. Connect to your SQL Server instance: `YOUR_SERVER`
3. Open the file: `setup-local-database.sql`
4. Execute the script (F5)

This creates:
- ✅ `SportlinkSqlDb` database
- ✅ `AppSettings` table
- ✅ Schemas: `stg`, `his`
- ✅ Staging tables: `teams`, `matches`, `matchdetails`

**Option B: Restore from SportlinkSqlDb repository**

If you have the full database project:

```powershell
# Navigate to the SQL database project
cd C:\Repos\VRC\SportlinkSqlDb

# Publish the database project using SSMS or Visual Studio
# Or use SqlPackage.exe to deploy the .dacpac file
```

### 3.2 Verify Database Creation

Run in SSMS:

```sql
-- Verify database exists
USE SportlinkSqlDb;
GO

-- Check schemas
SELECT * FROM sys.schemas WHERE name IN ('stg', 'his', 'mta');

-- Check if AppSettings table exists
SELECT * FROM INFORMATION_SCHEMA.TABLES 
WHERE TABLE_NAME = 'AppSettings';
```

---

## 4. Sportlink API Credentials Setup

### 4.1 Obtain API Credentials

**Where to get your credentials:**

1. **From Sportlink Portal:**
   - Log in to your Sportlink account
   - Navigate to API Settings
   - Copy your Client ID

2. **From Production Environment:**
   - Azure Portal → Your Function App
   - Configuration → Application Settings
   - Copy values for `sportlinkApiUrl` and `sportlinkClientId`

3. **From Team Lead/Admin:**
   - Contact your Sportlink administrator
   - Request API credentials for development

### 4.2 Update Database AppSettings

Once you have your credentials, update the database:

```sql
USE SportlinkSqlDb;
GO

-- View current settings
SELECT * FROM [dbo].[AppSettings];

-- Update with ACTUAL credentials
UPDATE [dbo].[AppSettings]
SET 
    [sportlinkApiUrl] = 'https://data.sportlink.com',  -- ⚠️ REPLACE with actual URL
    [sportlinkClientId] = 'YOUR_ACTUAL_CLIENT_ID',     -- ⚠️ REPLACE with actual Client ID
    [ModifiedDate] = GETDATE()
WHERE Id = 1;

-- Verify the update
SELECT * FROM [dbo].[AppSettings];
GO
```

**Example values (replace with your own):**

| Setting | Example Value | Description |
|---------|---------------|-------------|
| `sportlinkApiUrl` | `https://data.sportlink.com` | Base URL for Sportlink API |
| `sportlinkClientId` | `abc123def456` | Your unique client identifier |

⚠️ **IMPORTANT:** 
- Do NOT use placeholder values like `'your-client-id-here'`
- Do NOT commit real credentials to source control
- Keep production credentials separate from development

---

## 5. Local Settings Configuration

### 5.1 Verify local.settings.json

The file should already exist in your project root. Verify it contains:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SqlConnectionString": "Server=YOUR_SERVER;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;"
  }
}
```

### 5.2 Customize Connection String (if needed)

If your SQL Server is different, update the `SqlConnectionString`:

```json
"SqlConnectionString": "Server=YOUR_SERVER;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;"
```

**SQL Authentication example (if not using Windows Auth):**

```json
"SqlConnectionString": "Server=YOUR_SERVER;Database=SportlinkSqlDb;User Id=sa;Password=<your-password>;TrustServerCertificate=True;"
```

### 5.3 Production Connection String

The `PRDSqlConnectionString` in `local.settings.json` is for reference only and is **NOT used** during local debugging.

---

## 6. Stored Procedures Deployment

The application requires two critical stored procedures that handle data merging.

### 6.1 Required Stored Procedures

| Stored Procedure | Purpose |
|------------------|---------|
| `sp_CreateTargetTableFromSource` | Creates history tables based on staging table structure |
| `sp_MergeStgToHis` | Merges data from staging to history tables |

### 6.2 Deploy Stored Procedures

**Option A: From SportlinkSqlDb Repository**

1. Open Visual Studio
2. Open the solution: `C:\Repos\VRC\SportlinkSqlDb\SportlinkSqlDb.sln`
3. Right-click the database project → **Publish**
4. Target database: `YOUR_SERVER.SportlinkSqlDb`
5. Click **Publish**

**Option B: Manual Deployment**

1. Navigate to: `C:\Repos\VRC\SportlinkSqlDb\dbo\System Stored Procedures\`
2. Open in SSMS:
   - `sp_CreateTargetTableFromSource.sql`
   - `sp_MergeStgToHis.sql`
3. Execute each script on `SportlinkSqlDb`

### 6.3 Verify Stored Procedures

```sql
USE SportlinkSqlDb;
GO

-- Check if stored procedures exist
SELECT name, create_date, modify_date
FROM sys.procedures
WHERE name IN ('sp_CreateTargetTableFromSource', 'sp_MergeStgToHis');

-- Should return 2 rows
```

---

## 7. Metadata Table Setup

The stored procedures use a metadata table to map source and target tables.

### 7.1 Create Metadata Schema and Table

```sql
USE SportlinkSqlDb;
GO

-- Create mta schema if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'mta')
BEGIN
    EXEC('CREATE SCHEMA [mta]');
    PRINT 'Schema [mta] created successfully';
END
GO

-- Create source_target_mapping table
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'source_target_mapping' AND schema_id = SCHEMA_ID('mta'))
BEGIN
    CREATE TABLE [mta].[source_target_mapping]
    (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [source_schema] NVARCHAR(128) NOT NULL,
        [source_entity] NVARCHAR(128) NOT NULL,
        [source_pk] NVARCHAR(255) NULL,
        [target_schema] NVARCHAR(128) NOT NULL,
        [target_entity] NVARCHAR(128) NOT NULL,
        [target_pk] NVARCHAR(255) NULL,
        [merge_type] NVARCHAR(10) DEFAULT 'IUD', -- Insert, Update, Delete
        [is_active] BIT DEFAULT 1,
        [created_date] DATETIME2 DEFAULT GETDATE(),
        [modified_date] DATETIME2 DEFAULT GETDATE()
    );
    PRINT 'Table [mta].[source_target_mapping] created successfully';
END
GO
```

### 7.2 Insert Mapping Data

```sql
USE SportlinkSqlDb;
GO

-- Insert mappings for teams, matches, and matchdetails
INSERT INTO [mta].[source_target_mapping] 
    ([source_schema], [source_entity], [source_pk], [target_schema], [target_entity], [target_pk], [merge_type])
VALUES 
    ('stg', 'teams', 'teamcode', 'his', 'teams', 'bk_teams', 'IUD'),
    ('stg', 'matches', 'wedstrijdcode', 'his', 'matches', 'bk_matches', 'IUD'),
    ('stg', 'matchdetails', 'WedstrijdCode', 'his', 'matchdetails', 'bk_matchdetails', 'IUD');

-- Verify the data
SELECT * FROM [mta].[source_target_mapping];
GO
```

---

## 8. Environment Verification

### 8.1 Run Automated Setup Script

Execute the provided PowerShell script to verify your environment:

```powershell
cd C:\Repos\VRC\fa-dev-sportlink-01
.\setup-local-debug.ps1
```

This script checks:
- ✅ SQL Server connectivity
- ✅ Database existence
- ✅ Azurite installation and status
- ✅ local.settings.json configuration

### 8.2 Manual Verification Checklist

| Component | Verification Step | Expected Result |
|-----------|------------------|-----------------|
| **SQL Server** | Connect via SSMS | ✅ Connected successfully |
| **Database** | `USE SportlinkSqlDb` | ✅ Database exists |
| **Schemas** | Check `stg`, `his`, `mta` | ✅ All 3 schemas exist |
| **AppSettings** | `SELECT * FROM [dbo].[AppSettings]` | ✅ Contains valid API credentials |
| **Stored Procs** | Check `sp_MergeStgToHis` | ✅ Stored procedures exist |
| **Metadata** | `SELECT * FROM [mta].[source_target_mapping]` | ✅ Contains 3 mappings |
| **Azurite** | `azurite --version` | ✅ Version displayed |
| **Functions Tools** | `func --version` | ✅ Version 4.x.x |

### 8.3 Test Database Connection

```sql
-- Test the connection string that the app will use
-- Run this from SSMS connected to YOUR_SERVER

USE SportlinkSqlDb;
GO

-- Verify you can read AppSettings
SELECT 
    [sportlinkApiUrl],
    [sportlinkClientId],
    CASE 
        WHEN [sportlinkClientId] LIKE '%your-%' THEN '⚠️ PLACEHOLDER - Update Required'
        ELSE '✅ Configured'
    END AS Status
FROM [dbo].[AppSettings];

-- Check staging tables
SELECT 'stg.teams' AS TableName, COUNT(*) AS RowCount FROM [stg].[teams]
UNION ALL
SELECT 'stg.matches', COUNT(*) FROM [stg].[matches]
UNION ALL
SELECT 'stg.matchdetails', COUNT(*) FROM [stg].[matchdetails];
```

---

## 9. First Run

### 9.1 Start Azurite

Open a PowerShell terminal and start Azurite:

```powershell
azurite --silent --location c:\azurite --debug c:\azurite\debug.log
```

Or let it run in the background:

```powershell
Start-Process -FilePath "azurite" -ArgumentList "--silent" -WindowStyle Hidden
```

### 9.2 Build the Solution

1. Open Visual Studio
2. Open solution: `fa-dev-sportlink-01.sln`
3. Set build configuration to **Debug** (not Release)
4. Build → Rebuild Solution (Ctrl+Shift+B)
5. Verify: **Build succeeded**

### 9.3 Start Debugging

1. Press **F5** to start debugging
2. Wait for Azure Functions Core Tools to start

**Expected output:**

```
Azure Functions Core Tools
Core Tools Version:       4.8.0
Function Runtime Version: 4.x.x

Functions:
    FetchAndStoreApiData: timerTrigger

[INFO] Azure Function executed at: 2026-03-22 10:02:45
[INFO] Database connection established.
[INFO] App settings loaded successfully.
[INFO] TEAMS - GET: https://data.sportlink.com/teams?clientId=<your-client-id>
[INFO] TEAMS - 25 count.
[INFO] TEAMS - Data inserted into staging table.
[INFO] TEAMS - Merged into his table
```

### 9.4 Verify Data Flow

After the first successful run, check the database:

```sql
USE SportlinkSqlDb;
GO

-- Check staging tables (should have data)
SELECT COUNT(*) AS TeamCount FROM [stg].[teams];
SELECT COUNT(*) AS MatchCount FROM [stg].[matches];
SELECT COUNT(*) AS MatchDetailCount FROM [stg].[matchdetails];

-- Check history tables (should have merged data)
SELECT COUNT(*) AS TeamCount FROM [his].[teams];
SELECT COUNT(*) AS MatchCount FROM [his].[matches];
SELECT COUNT(*) AS MatchDetailCount FROM [his].[matchdetails];
```

---

## 10. Troubleshooting

### Issue: "401 Unauthorized" Error

**Symptom:**
```
Error: Response status code does not indicate success: 401 (Unauthorized).
```

**Solution:**
1. Verify your API credentials are correct
2. Check the `AppSettings` table has **actual** credentials (not placeholders)
3. Test the API URL in a browser or Postman
4. Confirm the Client ID is valid

```sql
-- Check current credentials
SELECT * FROM [dbo].[AppSettings];

-- Update if needed
UPDATE [dbo].[AppSettings]
SET sportlinkApiUrl = 'https://data.sportlink.com',
    sportlinkClientId = 'YOUR_REAL_CLIENT_ID'
WHERE Id = 1;
```

---

### Issue: "Cannot connect to database"

**Symptom:**
```
Error loading app settings: Cannot open database "SportlinkSqlDb"
```

**Solutions:**

1. **Verify SQL Server is running:**
   ```powershell
   # Check SQL Server service
   Get-Service -Name 'MSSQLSERVER' | Select-Object Status, Name
   ```

2. **Check database exists:**
   ```sql
   SELECT name FROM sys.databases WHERE name = 'SportlinkSqlDb';
   ```

3. **Verify connection string in local.settings.json**
4. **Check firewall settings** (if remote server)
5. **Test connection in SSMS** first

---

### Issue: "Stored procedure not found"

**Symptom:**
```
Could not find stored procedure 'sp_MergeStgToHis'.
```

**Solution:**

Deploy stored procedures from the SportlinkSqlDb repository:

```sql
-- Check if procedures exist
SELECT name FROM sys.procedures 
WHERE name IN ('sp_MergeStgToHis', 'sp_CreateTargetTableFromSource');

-- If missing, deploy from:
-- C:\Repos\VRC\SportlinkSqlDb\dbo\System Stored Procedures\
```

---

### Issue: "Azurite not running"

**Symptom:**
```
Error: AzureWebJobsStorage connection failed
```

**Solution:**

```powershell
# Check if Azurite is running
Get-Process -Name "azurite" -ErrorAction SilentlyContinue

# Start Azurite
azurite --silent --location c:\azurite

# Or start in background
Start-Process azurite -ArgumentList "--silent" -WindowStyle Hidden
```

---

### Issue: "Metadata table not found"

**Symptom:**
```
Invalid object name 'mta.source_target_mapping'.
```

**Solution:**

Create the metadata schema and table (see Section 7.1 and 7.2)

---

### Issue: "Just My Code Warning"

**Symptom:**
```
You are debugging a Release build... degraded debugging experience
```

**Solution:**

1. Visual Studio → Top toolbar → Change **Release** to **Debug**
2. Rebuild the solution
3. Restart debugging

---

### Issue: Timer trigger not firing

**Symptom:**
Function starts but timer never executes

**Solution:**

1. Check the timer trigger schedule in `Function1.cs`:
   ```csharp
   [TimerTrigger("0 0 4 * * *")]  // Daily at 04:00
   ```

2. For faster testing, change to every 10 seconds:
   ```csharp
   [TimerTrigger("*/10 * * * * *")]  // Every 10 seconds
   ```

3. Verify Azurite is running (timer history is stored there)

---

## 📚 Additional Resources

### Project Structure

```
fa-dev-sportlink-01/
├── Function1.cs              # Main timer trigger function
├── Utilities.cs              # Database config & AppSettings loader
├── MergeStgToHis.cs         # Merge staging to history
├── CreateTable.cs           # Table creation utilities
├── Enitities.cs             # Data models (Team, Match, etc.)
├── Program.cs               # Function app startup
├── local.settings.json      # Local configuration
├── setup-local-database.sql # Database initialization script
├── setup-local-debug.ps1    # Environment verification script
└── SETUP.md                 # This file
```

### Related Repositories

| Repository | Location | Purpose |
|------------|----------|---------|
| **fa-dev-sportlink-01** | `C:\Repos\VRC\fa-dev-sportlink-01` | Azure Function application |
| **SportlinkSqlDb** | `C:\Repos\VRC\SportlinkSqlDb` | Database project (schemas, stored procedures) |

### Useful SQL Queries

```sql
-- View all tables in database
SELECT 
    s.name AS SchemaName,
    t.name AS TableName,
    SUM(p.rows) AS RowCount
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.partitions p ON t.object_id = p.object_id
WHERE p.index_id IN (0,1)
  AND s.name IN ('stg', 'his', 'dbo', 'mta')
GROUP BY s.name, t.name
ORDER BY s.name, t.name;

-- Check function execution logs (if logging table exists)
-- SELECT TOP 100 * FROM [dbo].[ExecutionLog] ORDER BY ExecutionDate DESC;
```

### Timer Schedule Reference

| Cron Expression | Description |
|----------------|-------------|
| `0 0 4 * * *` | Daily at 04:00 |
| `*/10 * * * * *` | Every 10 seconds (testing) |
| `0 0 8 * * 1` | Every Monday at 8:00 AM |
| `0 0 8 * * 1,4` | Monday and Thursday at 8:00 AM |

---

## ✅ Setup Complete Checklist

Before running the application, ensure all items are checked:

- [ ] Visual Studio 2022/2026 installed
- [ ] SQL Server accessible
- [ ] Node.js and Azurite installed
- [ ] Git hooks activated (`git config core.hooksPath .githooks`)
- [ ] Sensitive patterns file configured (`.githooks/sensitive-patterns.txt`)
- [ ] Database `SportlinkSqlDb` created
- [ ] Schemas `stg`, `his`, `mta` exist
- [ ] Stored procedures deployed (`sp_MergeStgToHis`, `sp_CreateTargetTableFromSource`)
- [ ] Metadata table created and populated
- [ ] **Sportlink API credentials obtained and configured in AppSettings table**
- [ ] `local.settings.json` configured with correct SQL connection string
- [ ] Azurite running
- [ ] Solution builds successfully
- [ ] First test run completed successfully

---

## 🎉 You're Ready!

If all checks pass, you're ready to start developing and debugging the Sportlink Azure Function!

Press **F5** in Visual Studio and watch the data flow from Sportlink API → Staging tables → History tables.

For ongoing support, refer to `LOCAL-DEBUG-README.md` for debugging tips and troubleshooting.

---

**Last Updated:** March 2026  
**Version:** 1.0
