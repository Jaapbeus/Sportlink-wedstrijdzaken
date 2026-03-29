-- Test Database Setup and Verify Everything is Ready
-- Run this to verify all components are in place

USE SportlinkSqlDb;
GO

PRINT '';
PRINT '========================================';
PRINT 'Database Setup Verification';
PRINT '========================================';
PRINT '';

-- 1. Check Schemas
PRINT '1. Checking Schemas...';
IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'stg')
    PRINT '   ✓ Schema [stg] exists'
ELSE
    PRINT '   ✗ Schema [stg] MISSING!';

IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'his')
    PRINT '   ✓ Schema [his] exists'
ELSE
    PRINT '   ✗ Schema [his] MISSING!';

IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'mta')
    PRINT '   ✓ Schema [mta] exists'
ELSE
    PRINT '   ✗ Schema [mta] MISSING!';
PRINT '';

-- 2. Check AppSettings
PRINT '2. Checking AppSettings...';
IF EXISTS (SELECT 1 FROM dbo.AppSettings)
BEGIN
    DECLARE @ApiUrl NVARCHAR(500), @ClientId NVARCHAR(500);
    SELECT @ApiUrl = SportlinkApiUrl, @ClientId = SportlinkClientId FROM dbo.AppSettings;
    PRINT '   ✓ AppSettings exists';
    PRINT '   - API URL: ' + @ApiUrl;
    PRINT '   - Client ID: ' + @ClientId;
END
ELSE
    PRINT '   ✗ AppSettings is EMPTY!';
PRINT '';

-- 3. Check Stored Procedures
PRINT '3. Checking Stored Procedures...';
IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'sp_CreateTargetTableFromSource')
    PRINT '   ✓ sp_CreateTargetTableFromSource exists'
ELSE
    PRINT '   ✗ sp_CreateTargetTableFromSource MISSING!';

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'sp_MergeStgToHis')
    PRINT '   ✓ sp_MergeStgToHis exists'
ELSE
    PRINT '   ✗ sp_MergeStgToHis MISSING!';
PRINT '';

-- 4. Check Metadata Mappings
PRINT '4. Checking Metadata Mappings...';
IF EXISTS (SELECT 1 FROM mta.source_target_mapping WHERE source_entity = 'teams')
    PRINT '   ✓ Mapping for teams exists'
ELSE
    PRINT '   ✗ Mapping for teams MISSING!';

IF EXISTS (SELECT 1 FROM mta.source_target_mapping WHERE source_entity = 'matches')
    PRINT '   ✓ Mapping for matches exists'
ELSE
    PRINT '   ✗ Mapping for matches MISSING!';

IF EXISTS (SELECT 1 FROM mta.source_target_mapping WHERE source_entity = 'matchdetails')
    PRINT '   ✓ Mapping for matchdetails exists'
ELSE
    PRINT '   ✗ Mapping for matchdetails MISSING!';
PRINT '';

-- 5. Check History Tables (should be empty or not exist before first run)
PRINT '5. Checking History Tables...';
IF EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('his') AND name = 'teams')
    PRINT '   ⚠ Table [his].[teams] exists (will be used if structure is correct)'
ELSE
    PRINT '   ℹ Table [his].[teams] does not exist (will be created on first run)';

IF EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('his') AND name = 'matches')
    PRINT '   ⚠ Table [his].[matches] exists (will be used if structure is correct)'
ELSE
    PRINT '   ℹ Table [his].[matches] does not exist (will be created on first run)';

IF EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID('his') AND name = 'matchdetails')
    PRINT '   ⚠ Table [his].[matchdetails] exists (will be used if structure is correct)'
ELSE
    PRINT '   ℹ Table [his].[matchdetails] does not exist (will be created on first run)';
PRINT '';

PRINT '========================================';
PRINT 'Verification Complete!';
PRINT '========================================';
PRINT '';
PRINT 'System is ready for Azure Function debugging.';
PRINT 'Press F5 in Visual Studio to start!';
PRINT '';
GO
