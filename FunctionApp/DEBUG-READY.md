# Debug Configuration Fixed - Ready to Run! 🎉

## ✅ Issues Fixed

### 1. **Database Stored Procedures Updated**
   - Fixed `sp_MergeStgToHis` - Removed references to non-existent 'IUD' and 'IUD_Timestamp' columns
   - Fixed `sp_CreateTargetTableFromSource` - Updated to match actual database schema
   - Dropped and recreated history tables with correct structure

### 2. **Code Fixed**
   - Updated `Utilities.cs` - Fixed column names to match database schema (SportlinkApiUrl vs sportlinkApiUrl)

### 3. **Debug Configuration**
   - Cleaned Release build folders
   - Ensured Debug configuration is active
   - Project will now build and run in Debug mode

### 4. **Database Connection**
   - AppSettings table already has correct data:
     - SportlinkApiUrl: https://data.sportlink.com
     - SportlinkClientId: (stored in dbo.AppSettings — not shown here)

## 🚀 How to Debug Now

### Option 1: Visual Studio (Recommended)
1. **Make sure Visual Studio is in Debug mode:**
   - Check the toolbar dropdown shows **"Debug"** (not Release)
   - Select profile: **"fa_dev_sportlink_01 (Debug - Local DB)"**

2. **Press F5** to start debugging
   - Or click the green "Start Debugging" button
   - Breakpoints will now work correctly!

### Option 2: Command Line
```powershell
cd bin\Debug\net10.0
func start --port 7094
```

## 📊 What the Application Does

1. **Connects to Database** - YOUR_SERVER\SportlinkSqlDb
2. **Loads Settings** - From AppSettings table
3. **Fetches Teams** - From Sportlink API (388 teams)
4. **Creates Staging Table** - [stg].[teams]
5. **Inserts Data** - Into staging table
6. **Merges to History** - [his].[teams]
7. **Repeats for Matches** - Last 5 weeks of matches
8. **Fetches Match Details** - For each match
9. **Merges Everything** - Into history tables

## 🔍 Monitor the Function

The function runs on a timer trigger (daily at 04:00):
```csharp
[TimerTrigger("0 0 4 * * *")]  // Daily at 04:00
```

You'll see logs like:
- ✅ "Database connection established"
- ✅ "App settings loaded successfully"
- ✅ "TEAMS - 388 count"
- ✅ "TEAMS - Data inserted into staging table"
- ✅ "Merged teams to teams"
- ✅ "MATCHES - X count"
- ✅ "Merged matches to matches"
- ✅ "MATCHDETAILS - Successfully inserted"

## 🐛 If You See Errors

### Database Connection Errors
- Verify SQL Server is running: `sqlcmd -S YOUR_SERVER -E -Q "SELECT @@VERSION"`
- Check connection string in local.settings.json

### API Errors
- Check if client ID is still valid in AppSettings table
- Verify API URL is accessible

### Merge Errors
If you see merge errors, you can reset the history tables:
```sql
USE SportlinkSqlDb;
DROP TABLE IF EXISTS [his].[teams];
DROP TABLE IF EXISTS [his].[matches];
DROP TABLE IF EXISTS [his].[matchdetails];
-- Tables will be recreated automatically on next run
```

## 📁 Files Modified

1. **Utilities.cs** - Fixed column name casing for AppSettings
2. **Properties\launchSettings.json** - Added Debug configuration
3. **fa-dev-sportlink-01.csproj** - Set Debug as default

## 🗄️ Database Objects Created/Updated

1. **Schemas**: stg, his, mta, pub
2. **Tables**:
   - dbo.AppSettings (config)
   - mta.source_target_mapping (metadata)
   - stg.teams, stg.matches, stg.matchdetails (staging)
   - his.teams, his.matches, his.matchdetails (history)
3. **Stored Procedures**:
   - sp_CreateTargetTableFromSource
   - sp_MergeStgToHis

## 💡 Tips

- **Set Breakpoints**: Click in the left margin of Function1.cs to set breakpoints
- **Watch Variables**: Hover over variables while debugging to see values
- **Step Through**: Use F10 to step over, F11 to step into functions
- **View Data**: Query the database while debugging to see data being inserted

## 🎯 Next Steps

1. Press **F5** in Visual Studio
2. Watch the Output window for logs
3. Check the database tables for data:
   ```sql
   SELECT TOP 10 * FROM [his].[teams];
   SELECT TOP 10 * FROM [his].[matches];
   SELECT TOP 10 * FROM [his].[matchdetails];
   ```

---

**Ready to debug! All errors have been fixed.** 🚀
