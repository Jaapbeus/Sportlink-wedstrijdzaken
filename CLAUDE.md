# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug

# Run locally (requires Azurite running for storage emulation)
cd FunctionApp && func start --port 7094

# Manual sync trigger while running
# GET http://localhost:7094/api/sync?weekOffsetFrom=X&weekOffsetTo=Y
```

**Prerequisites:** .NET 10.0 SDK, Azure Functions Core Tools v4, Azurite (Azure Storage Emulator), SQL Server with `SportlinkSqlDb` database.

**Configuration:** Copy `FunctionApp/local.settings.template.json` to `local.settings.json` and set `SqlConnectionString` to your SQL Server instance.

No automated tests or linter configured.

## Architecture

Serverless ETL pipeline: **Sportlink REST API -> Azure Function -> SQL Server**

**Two trigger functions** in `FunctionApp/Function1.cs`:
- `FetchAndStoreApiData` — Timer trigger (hourly, `0 0/1 * * * *`), fetches teams, matches (last 5 weeks via weekoffset), and match details
- `SyncMatchesHttp` — HTTP GET `/api/sync`, manual trigger with optional weekoffset params

**Data flow:** Sportlink JSON -> C# entity models -> staging tables (`stg.*`) -> stored procedure MERGE -> history tables (`his.*`) -> public views (`pub.*`)

**Database schemas:**
- `stg` — transient staging tables, truncated each run
- `his` — persistent history with `mta_inserted`/`mta_modified` metadata columns
- `mta` — `source_target_mapping` table drives dynamic table creation and merge operations
- `pub` — read-only views for consumers
- `dbo` — `AppSettings` (API URL, client ID), `Season`, `DateTable`, `Speeltijden`

**Key stored procedures:** `sp_CreateTargetTableFromSource` (dynamic DDL), `sp_MergeStgToHis` (UPSERT via MERGE)

## Solution Structure

Two projects in `sportlink-wedstrijdzaken.sln`:

1. **FunctionApp/** (`fa-dev-sportlink-01.csproj`) — .NET 10 isolated worker Azure Function
   - `Function1.cs` — trigger functions and API fetch/store orchestration
   - `Utilities.cs` — AppSettings loader, DatabaseConfig, SeasonHelper, retry logic (5 retries, 5s delay)
   - `Enitities.cs` — Team, Match, MatchDetail models (note: filename typo is intentional legacy)
   - `CreateTable.cs` — dynamic staging table DDL
   - `MergeStgToHis.cs` — merge orchestration
   - Namespace: `SportlinkFunction`

2. **Database/** (`SportlinkSqlDb.sqlproj`) — SQL Server Database Project with schemas, tables, stored procedures, views

## Code Conventions

- Entity properties use **camelCase** matching Sportlink API JSON field names
- SQL column names use **exact casing** as defined in schema (e.g., `SportlinkApiUrl`, not `sportlinkApiUrl`)
- Async/await for all I/O; exception handling at function entry points
- App configuration lives in `dbo.AppSettings` table, not in code/config files

## Sportlink API

Base URL: `https://data.sportlink.com`, auth via `?clientId=` query param (from `dbo.AppSettings`).
Full docs: https://sportlinkservices.github.io/navajofeeds-json-parser/article/

| Endpoint | Path | Notes |
|---|---|---|
| Teams | `/teams?clientId=` | All club teams |
| Match results | `/uitslagen?clientId=&weekoffset=` | Results by week offset |
| Match details | `/wedstrijd-informatie?clientId=&wedstrijdcode=` | Per-match detail |
| *Programma* | `/programma?clientId=` | Not yet implemented; richer than uitslagen (includes referee, venue, logos) |

See `FunctionApp/CLAUDE.md` for detailed field reference including all `/programma` fields.
