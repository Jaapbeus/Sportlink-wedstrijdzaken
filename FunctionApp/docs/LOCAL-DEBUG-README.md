# Local Debug Setup Guide

## Quick Start

Follow these steps to run the Azure Function locally with your local database:

### 1. Setup Local SQL Server Database

Run the database setup script in SQL Server Management Studio (SSMS):

```sql
-- Connect to: YOUR_SERVER
-- Open and execute: setup-local-database.sql
```

This will:
- Create the `SportlinkSqlDb` database
- Create the `AppSettings` table
- Create the required schemas (`stg`, `his`)
- Create staging tables (`teams`, `matches`, `matchdetails`)

### 2. Configure API Settings

Update the `AppSettings` table with your actual Sportlink API credentials:

```sql
USE SportlinkSqlDb;
GO

UPDATE [dbo].[AppSettings]
SET 
    [sportlinkApiUrl] = 'https://data.sportlink.com',  -- Your actual API URL
    [sportlinkClientId] = 'your-actual-client-id'      -- Your actual client ID
WHERE Id = 1;
```

### 3. Verify Environment Setup

Run the PowerShell setup script to verify your local environment:

```powershell
cd C:\Repos\VRC\fa-dev-sportlink-01
.\setup-local-debug.ps1
```

This script will:
- ✓ Check SQL Server connectivity
- ✓ Verify database exists
- ✓ Install/start Azurite (Azure Storage Emulator)
- ✓ Verify `local.settings.json` configuration

### 4. Start Debugging

In Visual Studio:
1. Set `fa-dev-sportlink-01` as the startup project
2. Press **F5** to start debugging

The function will:
- Connect to your local SQL Server (`YOUR_SERVER`)
- Use local storage emulator (Azurite)
- Execute the timer trigger daily at 04:00 (use HTTP trigger for manual sync)

---

## Configuration Files

### local.settings.json

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

- **AzureWebJobsStorage**: Uses local Azurite emulator
- **SqlConnectionString**: Points to local SQL Server
- **PRDSqlConnectionString**: Production connection (not used in local debug)

---

## Troubleshooting

### Issue: "Cannot connect to database"

**Solution:**
1. Verify SQL Server is running on `YOUR_SERVER`
2. Check Windows Authentication is enabled
3. Verify `SportlinkSqlDb` database exists
4. Run `setup-local-database.sql` if database doesn't exist

### Issue: "AzureWebJobsStorage connection failed"

**Solution:**
1. Install Azurite: `npm install -g azurite`
2. Start Azurite: `azurite` or run `setup-local-debug.ps1`
3. Verify it's running on default ports (10000, 10001, 10002)

### Issue: "The connection string is not set"

**Solution:**
1. Ensure `local.settings.json` exists in the project root
2. Verify it contains `SqlConnectionString` in the `Values` section
3. Rebuild the solution

### Issue: "sportlinkApiUrl is not configured"

**Solution:**
1. Run `setup-local-database.sql` to create the AppSettings table
2. Update the AppSettings table with your actual API credentials:
   ```sql
   UPDATE [dbo].[AppSettings]
   SET sportlinkApiUrl = 'your-api-url',
       sportlinkClientId = 'your-client-id'
   ```

---

## Timer Schedule

The function runs with this schedule: `"0 0 4 * * *"`

- **Means:** Daily at 04:00
- **For manual sync:** Use HTTP trigger `GET /api/sync-matches`
- **Location:** `Function1.cs`, line with `[TimerTrigger(...)]`

---

## Project Structure

```
fa-dev-sportlink-01/
├── Function1.cs              # Main Azure Function (timer trigger)
├── Utilities.cs              # Database config & AppSettings loader
├── CreateTable.cs            # Table creation utilities
├── MergeStgToHis.cs         # Staging to history merge logic
├── Enitities.cs             # Data models (Team, Match, MatchDetails)
├── Program.cs               # Function app startup
├── local.settings.json      # Local development settings
├── host.json                # Azure Functions host configuration
├── setup-local-debug.ps1    # Environment setup script
└── setup-local-database.sql # Database initialization script
```

---

## Related Repository

The SQL database project is located at:
```
C:\Repos\VRC\SportlinkSqlDb
```

This contains:
- Database schema definitions
- Stored procedures (if any)
- Table definitions

If you need to sync with the latest schema, pull the latest changes from:
```
https://dev.azure.com/VV-VRC/vrc-sportlink/_git/SportlinkSqlDb
```

---

## Next Steps

After successful local debugging:

1. **Test with real data**: Verify API calls and data storage
2. **Check staging tables**: Query `[stg].[teams]`, `[stg].[matches]`, `[stg].[matchdetails]`
3. **Verify merge operations**: Check data in `[his]` schema tables
4. **Monitor logs**: Watch the Function console output for errors

---

## Need Help?

- Check Visual Studio Output window for detailed logs
- Review Azure Functions Core Tools output
- Verify SQL Server Profiler for database queries
- Check Azurite logs if storage issues occur
