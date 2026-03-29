# ⚡ Quick Reference - Sportlink Function

## 🔑 Critical Configuration Required

### 1. Sportlink API Credentials (REQUIRED)

Update in database before first run:

```sql
USE SportlinkSqlDb;
UPDATE [dbo].[AppSettings]
SET sportlinkApiUrl = 'https://data.sportlink.com',    -- ⚠️ REPLACE
    sportlinkClientId = 'YOUR_ACTUAL_CLIENT_ID'        -- ⚠️ REPLACE
WHERE Id = 1;
```

**Where to get credentials:**
- Sportlink Portal → API Settings
- Azure Portal → Production Function App → Configuration
- Contact your Sportlink administrator

---

## 📝 Setup Scripts (Run in Order)

### SQL Scripts (in SSMS)
```sql
-- 1. Create database, schemas, basic tables
C:\Repos\VRC\fa-dev-sportlink-01\setup-local-database.sql

-- 2. Create metadata schema and mappings
C:\Repos\VRC\fa-dev-sportlink-01\setup-metadata-tables.sql

-- 3. Deploy stored procedures from:
C:\Repos\VRC\SportlinkSqlDb\dbo\System Stored Procedures\
   - sp_CreateTargetTableFromSource.sql
   - sp_MergeStgToHis.sql
```

### PowerShell Script
```powershell
# Verify environment setup
.\setup-local-debug.ps1
```

---

## ⚙️ Local Configuration

### local.settings.json
```json
{
  "Values": {
    "SqlConnectionString": "Server=YOUR_SERVER;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"
  }
}
```

---

## 🚀 Run the Application

### 1. Start Azurite (Azure Storage Emulator)
```powershell
azurite --silent
```

### 2. Start Debugging in Visual Studio
- Configuration: **Debug** (not Release)
- Press **F5**

---

## ✅ Verification Queries

### Check API Credentials
```sql
SELECT * FROM [dbo].[AppSettings];
-- Should NOT contain 'your-client-id-here'
```

### Check Metadata Mappings
```sql
SELECT * FROM [mta].[source_target_mapping];
-- Should return 3 rows (teams, matches, matchdetails)
```

### Check Stored Procedures
```sql
SELECT name FROM sys.procedures 
WHERE name IN ('sp_MergeStgToHis', 'sp_CreateTargetTableFromSource');
-- Should return 2 rows
```

### Check Data After Run
```sql
-- Staging tables
SELECT COUNT(*) FROM [stg].[teams];
SELECT COUNT(*) FROM [stg].[matches];
SELECT COUNT(*) FROM [stg].[matchdetails];

-- History tables
SELECT COUNT(*) FROM [his].[teams];
SELECT COUNT(*) FROM [his].[matches];
SELECT COUNT(*) FROM [his].[matchdetails];
```

---

## 🔧 Common Issues & Quick Fixes

| Error | Quick Fix |
|-------|-----------|
| `401 Unauthorized` | Update API credentials in AppSettings table |
| `Cannot connect to database` | Verify SQL Server running, check connection string |
| `Stored procedure not found` | Deploy from SportlinkSqlDb repository |
| `mta.source_target_mapping not found` | Run setup-metadata-tables.sql |
| `Azurite connection failed` | Start Azurite: `azurite --silent` |
| `Just My Code Warning` | Switch to Debug configuration |

---

## 📂 Important Files

| File | Purpose |
|------|---------|
| `SETUP.md` | Complete setup guide |
| `SETUP-CHECKLIST.md` | Interactive checklist |
| `LOCAL-DEBUG-README.md` | Debugging guide |
| `setup-local-database.sql` | Database initialization |
| `setup-metadata-tables.sql` | Metadata setup |
| `setup-local-debug.ps1` | Environment verification |
| `local.settings.json` | Local configuration |

---

## 🔄 Timer Schedule

Current: `0 */1 * * * *` (every minute)

Change in `Function1.cs`:
```csharp
[TimerTrigger("*/10 * * * * *")]  // Every 10 seconds (testing)
```

---

## 📞 Resources

- **Database Project:** `C:\Repos\VRC\SportlinkSqlDb`
- **Function Project:** `C:\Repos\VRC\fa-dev-sportlink-01`
- **Azure DevOps:** `https://dev.azure.com/YOUR_ORG/YOUR_PROJECT`

---

**Last Updated:** March 2026
