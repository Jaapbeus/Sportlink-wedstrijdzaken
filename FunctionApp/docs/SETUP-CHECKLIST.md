# Sportlink Function - Setup Checklist

Use this checklist to track your setup progress. Check off each item as you complete it.

---

## Prerequisites Installation

### Software
- [ ] Visual Studio 2022/2026 installed
- [ ] SQL Server installed and accessible
- [ ] SQL Server Management Studio (SSMS) installed
- [ ] Node.js installed (verify: `node --version`)
- [ ] Azurite installed (verify: `azurite --version`)
- [ ] Azure Functions Core Tools installed (verify: `func --version`)

### Access & Credentials
- [ ] Sportlink API URL obtained: `___________________________`
- [ ] Sportlink Client ID obtained: `___________________________`
- [ ] SQL Server instance accessible: `_______________`

---

## Git Hooks Setup (Sensitive Data Protection)

- [ ] Activated git hooks: `git config core.hooksPath .githooks`
- [ ] Copied pattern template: `cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt`
- [ ] Added project-specific patterns (passwords, server names, client IDs) to `sensitive-patterns.txt`
- [ ] Verified hooks are active (commit shows scanning message)

---

## Database Setup

### Database Creation
- [ ] Connected to SQL Server in SSMS
- [ ] Executed `setup-local-database.sql`
- [ ] Database `SportlinkSqlDb` created
- [ ] Schemas created: `stg`, `his`
- [ ] AppSettings table created
- [ ] Staging tables created: `teams`, `matches`, `matchdetails`

### Metadata Setup
- [ ] Executed `setup-metadata-tables.sql`
- [ ] Schema `mta` created
- [ ] Table `mta.source_target_mapping` created
- [ ] Mapping data inserted (3 rows)

### Stored Procedures
- [ ] Located SportlinkSqlDb repository at: `<repository>\Database`
- [ ] Deployed `sp_CreateTargetTableFromSource`
- [ ] Deployed `sp_MergeStgToHis`
- [ ] Verified both procedures exist in database

---

## API Credentials Configuration

### Sportlink API Settings
- [ ] Opened SSMS and connected to `SportlinkSqlDb`
- [ ] Executed UPDATE statement on `AppSettings` table
- [ ] Replaced placeholder API URL with actual URL
- [ ] Replaced placeholder Client ID with actual Client ID
- [ ] Verified credentials using SELECT statement

**API Credentials Used:**
```
sportlinkApiUrl:  _________________________________
sportlinkClientId: _________________________________
```

---

## Local Settings Configuration

### local.settings.json
- [ ] File exists in project root
- [ ] `SqlConnectionString` points to correct server
- [ ] `AzureWebJobsStorage` set to `UseDevelopmentStorage=true`
- [ ] `FUNCTIONS_WORKER_RUNTIME` set to `dotnet-isolated`

**Connection String Used:**
```
Server=____________; Database=SportlinkSqlDb; Integrated Security=True; TrustServerCertificate=True;
```

---

## Environment Verification

### Automated Checks
- [ ] Executed `setup-local-debug.ps1`
- [ ] SQL Server connection: ✓ Success
- [ ] Database existence: ✓ Success
- [ ] Azurite installation: ✓ Success
- [ ] Azurite running: ✓ Success

### Manual Verification
- [ ] Ran query: `SELECT * FROM [dbo].[AppSettings]` → Contains real credentials (not placeholders)
- [ ] Ran query: `SELECT * FROM [mta].[source_target_mapping]` → Contains 3 rows
- [ ] Ran query: `SELECT name FROM sys.procedures WHERE name LIKE 'sp_%'` → Shows 2 stored procedures
- [ ] Ran query: `SELECT name FROM sys.schemas WHERE name IN ('stg','his','mta')` → Shows 3 schemas

---

## Visual Studio Configuration

### Project Setup
- [ ] Opened solution in Visual Studio
- [ ] Build configuration set to **Debug** (not Release)
- [ ] Solution builds successfully (Ctrl+Shift+B)
- [ ] No build errors or warnings

---

## First Run Test

### Pre-Run
- [ ] Azurite is running in background or terminal
- [ ] Visual Studio is in Debug mode
- [ ] All checklist items above are complete

### Debug Session
- [ ] Pressed F5 to start debugging
- [ ] Azure Functions Core Tools started successfully
- [ ] Function `FetchAndStoreApiData` loaded
- [ ] Function executed (check console output)

### Expected Console Output
Look for these messages:
- [ ] `Database connection established.`
- [ ] `App settings loaded successfully.`
- [ ] `TEAMS - GET: https://...`
- [ ] `TEAMS - X count.` (X = number of teams)
- [ ] `TEAMS - Data inserted into staging table.`
- [ ] `TEAMS - Merged into his table`
- [ ] `MATCHES - GET: https://...`
- [ ] `MATCHDETAILS - GET: https://...`

### Error Checks
- [ ] No `401 Unauthorized` errors
- [ ] No `Cannot connect to database` errors
- [ ] No `Stored procedure not found` errors
- [ ] No `Invalid object name 'mta.source_target_mapping'` errors

---

## Data Verification

### Check Staging Tables
Run in SSMS:
```sql
SELECT COUNT(*) FROM [stg].[teams];       -- Should be > 0
SELECT COUNT(*) FROM [stg].[matches];     -- Should be > 0
SELECT COUNT(*) FROM [stg].[matchdetails]; -- Should be > 0
```

- [ ] Staging tables contain data
  - Teams: _______ rows
  - Matches: _______ rows
  - Match Details: _______ rows

### Check History Tables
```sql
SELECT COUNT(*) FROM [his].[teams];       -- Should be > 0
SELECT COUNT(*) FROM [his].[matches];     -- Should be > 0
SELECT COUNT(*) FROM [his].[matchdetails]; -- Should be > 0
```

- [ ] History tables contain data
  - Teams: _______ rows
  - Matches: _______ rows
  - Match Details: _______ rows

---

## Post-Setup Configuration (Optional)

### Timer Schedule Adjustment
Current schedule: `0 0 4 * * *` (daily at 04:00)

For testing, you can change to run more frequently:
- [ ] Modified timer to `*/10 * * * * *` (every 10 seconds)
- [ ] Restored timer to `0 */1 * * * *` after testing

### Logging Configuration
- [ ] Reviewed `host.json` logging settings
- [ ] Adjusted log levels if needed (optional)

---

## Troubleshooting Applied

Check any issues encountered and solutions applied:

- [ ] Issue: 401 Unauthorized → Fixed by updating API credentials
- [ ] Issue: Cannot connect to database → Fixed by: `_______________________`
- [ ] Issue: Stored procedure not found → Fixed by deploying from SportlinkSqlDb repo
- [ ] Issue: Metadata table not found → Fixed by running setup-metadata-tables.sql
- [ ] Issue: Azurite not running → Fixed by starting Azurite manually
- [ ] Issue: Just My Code Warning → Fixed by switching to Debug configuration
- [ ] Other: `_____________________________________________`

---

## Final Verification

### Complete Setup Confirmation
- [ ] All previous checklist items completed
- [ ] Function runs successfully without errors
- [ ] Data flows from API → Staging → History
- [ ] Can set breakpoints and debug code
- [ ] Ready for development work

**Setup Completed By:** ______________________  
**Date:** ______________________  
**Notes:** 
```
_________________________________________________________
_________________________________________________________
_________________________________________________________
```

---

## Quick Reference

### Start Debugging
1. Start Azurite: `azurite --silent`
2. Open Visual Studio
3. Press F5

### Stop Debugging
1. Stop debugging in Visual Studio (Shift+F5)
2. Stop Azurite: `Stop-Process -Name "azurite"`

### Restart with Clean Database
```sql
-- Truncate staging tables
TRUNCATE TABLE [stg].[teams];
TRUNCATE TABLE [stg].[matches];
TRUNCATE TABLE [stg].[matchdetails];

-- Truncate history tables
TRUNCATE TABLE [his].[teams];
TRUNCATE TABLE [his].[matches];
TRUNCATE TABLE [his].[matchdetails];
```

### View Latest API Credentials
```sql
SELECT * FROM [dbo].[AppSettings];
```

### View Metadata Mappings
```sql
SELECT 
    source_schema + '.' + source_entity AS [Source],
    target_schema + '.' + target_entity AS [Target],
    source_pk, target_pk, merge_type
FROM [mta].[source_target_mapping];
```

---

**Need Help?** Refer to `SETUP.md` for detailed instructions or `LOCAL-DEBUG-README.md` for troubleshooting.
