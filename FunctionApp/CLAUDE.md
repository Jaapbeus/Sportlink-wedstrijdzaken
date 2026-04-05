# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is an **Azure Functions application** (.NET 10.0, isolated worker model) that integrates with the Sportlink API to fetch sports data and sync it to a SQL Server database. The application runs on a timer trigger (daily at 04:00) and provides manual sync via HTTP trigger.

## Solution Structure

The solution (`fa-dev-sportlink-01.sln`) contains two projects:

1. **fa-dev-sportlink-01** (Azure Functions, C#)
   - Core application with function triggers and business logic
   - Namespace: `SportlinkFunction`
   - References the SportlinkSqlDb database project

2. **SportlinkSqlDb** (SQL Server Database Project, SQLPROJ)
   - Located at `<repository>\Database`
   - Contains schemas, tables, stored procedures, and deployment definitions
   - Schemas: `dbo`, `stg` (staging), `his` (history), `mta` (metadata), `pub` (public)

## Architecture & Data Flow

### High-Level Flow

```
Sportlink API → HTTP Client → Parse JSON → Staging Tables
  ↓
Stored Procedures (sp_CreateTargetTableFromSource, sp_MergeStgToHis)
  ↓
History Tables (his.teams, his.matches, his.matchdetails)
```

### Key Components

**[Function1.cs](Function1.cs)**
- `FetchAndStoreApiData`: Timer trigger (daily at 04:00) that orchestrates the sync
- `SyncAndStoreViaHttpTrigger`: HTTP endpoint for manual sync (GET `/api/sync`)
- Handles: Teams fetch, matches fetch (last 5 weeks), match details fetch
- Uses retry logic via `SystemUtilities.WaitForDatabaseAsync()`

**[Utilities.cs](Utilities.cs)**
- `SystemUtilities.AppSettings`: Loads settings from `dbo.AppSettings` table
  - Fields: `SportlinkApiUrl`, `SportlinkClientId`
- `SystemUtilities.DatabaseConfig`: Manages connection string from environment variables
- `SystemUtilities.SeasonHelper`: Calculates season end week offsets from `dbo.Season` table
- Database retry logic with 5 retries, 5-second delays between attempts

**[Enitities.cs](Enitities.cs)** (note: typo in filename)
- `Team`: Sportlink team data model
- `Match`: Sportlink match data model
- `MatchDetail`: Match detail data model (incomplete in current version)

**[CreateTable.cs](CreateTable.cs)**
- Utility for dynamically creating staging tables from source schema
- Used by `sp_CreateTargetTableFromSource` stored procedure

**[MergeStgToHis.cs](MergeStgToHis.cs)**
- Merge logic for syncing staging → history tables

## Build & Run

### Visual Studio (Recommended for Development)

1. **Build Solution:**
   ```
   Ctrl+Shift+B
   ```

2. **Run with Debugger:**
   - Select profile: `"fa_dev_sportlink_01 (Debug - Local DB)"`
   - Press `F5` or use Debug menu
   - Runs on `http://localhost:7094`

3. **Manual Sync (while running):**
   - HTTP GET: `http://localhost:7094/api/sync`
   - Query params: `weekOffsetFrom=X&weekOffsetTo=Y` (optional)

### Command Line

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build -c Debug

# Run Azure Functions locally
func start --port 7094
```

### Docker

```bash
dotnet publish -c Release -o ./publish
docker build -t fa-dev-sportlink-01 .
docker run -p 8080:80 fa-dev-sportlink-01
```

## Configuration

### Connection Strings

Stored in `local.settings.json` (development) and Azure Key Vault (production):

- **SqlConnectionString**: Local dev SQL Server
  ```
  Server=YOUR_SERVER;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;
  ```

- **PRDSqlConnectionString**: Azure SQL (production)
  ```
  Server=YOUR_AZURE_SERVER.database.windows.net;Database=YOUR_DB;...
  ```

### Environment Variables Required

- `SqlConnectionString`: Connection string to SQL database (loaded from local.settings.json or Azure Key Vault)
- `FUNCTIONS_WORKER_RUNTIME`: Always `"dotnet-isolated"`
- `AzureWebJobsStorage`: Storage for function state (dev uses `UseDevelopmentStorage=true`)

### Database Settings

App configuration is stored in `dbo.AppSettings` table:
- `SportlinkApiUrl`: `https://data.sportlink.com`
- `SportlinkClientId`: Your API client ID

## Database Schema

### Core Tables

- **dbo.AppSettings**: Configuration (SportlinkApiUrl, SportlinkClientId)
- **dbo.Season**: Season definitions with DateUntil for range calculations
- **stg.*** (Staging): Temporary tables for incoming API data
- **his.*** (History): Historical data tables with metadata columns (mta_inserted, mta_modified)
- **mta.source_target_mapping**: Metadata mapping for dynamic table creation

### Metadata Columns

History tables include:
- `mta_inserted`: Timestamp when record was created
- `mta_modified`: Timestamp when record was last updated

## Common Development Tasks

### Add a New Data Source

1. Create entity class in `Enitities.cs`
2. Add mapping entry in `mta.source_target_mapping` database table
3. Add fetch logic in `Function1.cs`
4. Add merge procedure to database project
5. Update `Utilities.cs` if new settings needed

### Debug Database Issues

Check stored procedure executions:
```sql
USE SportlinkSqlDb;
SELECT TOP 10 * FROM [his].[teams] ORDER BY mta_inserted DESC;
EXEC sp_MergeStgToHis @sourceTable='teams', @targetTable='teams';
```

Reset history tables (will recreate on next run):
```sql
DROP TABLE IF EXISTS [his].[teams];
DROP TABLE IF EXISTS [his].[matches];
DROP TABLE IF EXISTS [his].[matchdetails];
```

### Check Timer Trigger Execution

Monitor function logs in Azure portal or locally in console output. Function runs at `0 0 4 * * *` (daily at 04:00).

Manual execution via HTTP:
```
GET http://localhost:7094/api/sync
```

## Sportlink API Reference

Base URL: `https://data.sportlink.com`
Authentication: query param `clientId=<value>` (stored in `dbo.AppSettings.SportlinkClientId`)
Full docs: https://sportlinkservices.github.io/navajofeeds-json-parser/article/

### Endpoints in use

| Endpoint | URL | Description |
|---|---|---|
| teams | `/teams?clientId=` | All teams for the club |
| matches (uitslagen) | `/uitslagen?clientId=&weekoffset=` | Match results per week offset |
| match details | `/wedstrijd-informatie?clientId=&wedstrijdcode=` | Full detail per match |

### Available endpoints (not yet implemented)

| Endpoint | URL | Description |
|---|---|---|
| programma | `/programma?clientId=` | Upcoming schedule with referee/venue info |
| standen | `/standen?clientId=` | League standings |
| spelers | `/spelers?clientId=` | Player roster |
| wedstrijd-informatie | `/wedstrijd-informatie?clientId=&wedstrijdcode=` | Already in use as matchdetails |

### Programma endpoint — all available fields

Fetched live from `https://data.sportlink.com/programma?clientId=YOUR_CLIENT_ID`:

| Field | Type | Description | Example |
|---|---|---|---|
| `wedstrijddatum` | datetime | Match datetime ISO 8601 | `2026-03-30T20:15:00+0200` |
| `wedstrijdcode` | int | Unique match ID | `19816434` |
| `wedstrijdnummer` | int | Match number | `19780` |
| `teamnaam` | string | Own team name | `VRC 8` |
| `thuisteamclubrelatiecode` | string | Home club code | `BBBZ26B` |
| `uitteamclubrelatiecode` | string | Away club code | `BBBZ77R` |
| `thuisteamid` | int | Home team ID | `99007` |
| `thuisteam` | string | Home team name | `VRC 8` |
| `thuisteamlogo` | string | Home team logo URL | `https://binaries.sportlink.com/...` |
| `uitteamid` | int | Away team ID | `222309` |
| `uitteam` | string | Away team name | `Cobu Boys 8` |
| `uitteamlogo` | string/null | Away team logo URL | `https://binaries.sportlink.com/...` |
| `teamvolgorde` | int | Team sort order | `8` |
| `competitiesoort` | string | Competition type | `regulier`, `Oefenwedstrijd` |
| `competitie` | string | Competition name | `0214 Mannen Zaterdag reserve` |
| `klasse` | string | Division/class | `7e klasse` |
| `poule` | string | Pool/group | `07 (M)` |
| `klassepoule` | string | Class + pool | `7e klasse 07 (M)` |
| `kaledatum` | datetime | Calendar date (SQL format) | `2026-03-30 00:00:00.00` |
| `datum` | string | Human-readable date | `30 mrt.` |
| `vertrektijd` | string | Departure time | `08:35` |
| `verzameltijd` | string | Gathering/meet time | `19:30` |
| `aanvangstijd` | string | Kick-off time | `20:15` |
| `wedstrijd` | string | Match description | `VRC 8 - Cobu Boys 8` |
| `status` | string | Match status | `Te spelen` |
| `scheidsrechters` | string | Full referee description | `A. (Arie) Jansen (Scheidsrechter)` |
| `scheidsrechter` | string | Referee name | `A. (Arie) Jansen` |
| `accommodatie` | string | Venue name | `Sportpark Spitsbergen` |
| `veld` | string | Field designation | `veld 3` |
| `locatie` | string | Location type | `Veld`, `Outdoor` |
| `plaats` | string | City | `VEENENDAAL` |
| `rijders` | string/null | Riders info | `null` |
| `kleedkamerthuisteam` | string | Home changing room | `1` |
| `kleedkameruitteam` | string | Away changing room | `8` |
| `kleedkamerscheidsrechter` | string | Referee changing room | `` |
| `meer` | string | Link to match detail | `wedstrijd-informatie?wedstrijdcode=19816434` |

> **Note:** `programma` returns upcoming fixtures with richer info than `uitslagen` (includes logos, referee, venue, changing rooms, gather/depart times). Consider using this as the primary matches source in a future iteration.

## Key Dependencies

- **Microsoft.Azure.Functions.Worker** (v2.51.0): Azure Functions isolated worker
- **Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore** (v2.1.0): HTTP triggers
- **Microsoft.Azure.Functions.Worker.Extensions.Timer** (v4.3.1): Timer triggers
- **Microsoft.Data.SqlClient** (v7.0.0): SQL Server database access
- **Newtonsoft.Json** (v13.0.4): JSON parsing

## Known Issues & Workarounds

### Debug Build Selection
The project has been configured to use Debug build by default. If you see Release warnings:
1. Clean: Delete `/bin` and `/obj` folders
2. Rebuild: `Ctrl+Shift+B`
3. Verify dropdown shows "Debug" (not Release)

### Database Connection Timeouts
Database connection includes 5 retries with 5-second delays. If still failing:
1. Verify SQL Server is running: `sqlcmd -S YOUR_SERVER -E -Q "SELECT @@VERSION"`
2. Check connection string in `local.settings.json`
3. Ensure Windows Authentication has access to the database

## Testing Notes

- No unit tests currently in the project
- Recommend testing with local SQL Server instance (YOUR_SERVER)
- Manual testing: Use HTTP trigger at `/api/sync` with optional week offset parameters
- Integration testing: Query history tables to verify data insertion

## Code Conventions

- Namespace: `SportlinkFunction`
- Entity properties use camelCase (matching Sportlink API JSON)
- Async/await pattern for all I/O operations
- Exception handling at function entry points; specific errors logged to ILogger
- Column names in SQL queries use exact casing to match schema (e.g., `SportlinkApiUrl`)

## Related Documentation

- [DEBUG-READY.md](DEBUG-READY.md): Quick start guide for debugging
- [FIXES-APPLIED.md](FIXES-APPLIED.md): History of issues and fixes applied
- Azure Functions docs: https://learn.microsoft.com/en-us/azure/azure-functions/
- Sportlink API: https://data.sportlink.com (requires authentication)
