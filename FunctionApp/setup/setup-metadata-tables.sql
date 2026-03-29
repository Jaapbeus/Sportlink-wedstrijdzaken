-- Additional Database Setup for Sportlink Function
-- This script creates the metadata (mta) schema and mapping table
-- Required by the sp_MergeStgToHis stored procedure

USE SportlinkSqlDb;
GO

PRINT '========================================';
PRINT 'Creating Metadata Schema and Tables';
PRINT '========================================';
PRINT '';

-- Create mta schema if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'mta')
BEGIN
    EXEC('CREATE SCHEMA [mta]');
    PRINT '✓ Schema [mta] created successfully';
END
ELSE
BEGIN
    PRINT '✓ Schema [mta] already exists';
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
    PRINT '✓ Table [mta].[source_target_mapping] created successfully';
END
ELSE
BEGIN
    PRINT '✓ Table [mta].[source_target_mapping] already exists';
END
GO

-- Insert mapping data if not exists
IF NOT EXISTS (SELECT 1 FROM [mta].[source_target_mapping] WHERE source_entity = 'teams')
BEGIN
    INSERT INTO [mta].[source_target_mapping] 
        ([source_schema], [source_entity], [source_pk], [target_schema], [target_entity], [target_pk], [merge_type])
    VALUES 
        ('stg', 'teams', 'teamcode', 'his', 'teams', 'bk_teams', 'IUD');
    PRINT '✓ Inserted mapping for teams';
END
ELSE
BEGIN
    PRINT '✓ Mapping for teams already exists';
END

IF NOT EXISTS (SELECT 1 FROM [mta].[source_target_mapping] WHERE source_entity = 'matches')
BEGIN
    INSERT INTO [mta].[source_target_mapping] 
        ([source_schema], [source_entity], [source_pk], [target_schema], [target_entity], [target_pk], [merge_type])
    VALUES 
        ('stg', 'matches', 'wedstrijdcode', 'his', 'matches', 'bk_matches', 'IUD');
    PRINT '✓ Inserted mapping for matches';
END
ELSE
BEGIN
    PRINT '✓ Mapping for matches already exists';
END

IF NOT EXISTS (SELECT 1 FROM [mta].[source_target_mapping] WHERE source_entity = 'matchdetails')
BEGIN
    INSERT INTO [mta].[source_target_mapping] 
        ([source_schema], [source_entity], [source_pk], [target_schema], [target_entity], [target_pk], [merge_type])
    VALUES 
        ('stg', 'matchdetails', 'WedstrijdCode', 'his', 'matchdetails', 'bk_matchdetails', 'IUD');
    PRINT '✓ Inserted mapping for matchdetails';
END
ELSE
BEGIN
    PRINT '✓ Mapping for matchdetails already exists';
END
GO

PRINT '';
PRINT '========================================';
PRINT 'Metadata Setup Complete!';
PRINT '========================================';
PRINT '';

-- Display the mappings
SELECT 
    id,
    source_schema + '.' + source_entity AS [Source],
    source_pk AS [Source PK],
    target_schema + '.' + target_entity AS [Target],
    target_pk AS [Target PK],
    merge_type AS [Merge Type],
    CASE WHEN is_active = 1 THEN 'Yes' ELSE 'No' END AS [Active]
FROM [mta].[source_target_mapping]
ORDER BY id;

PRINT '';
PRINT 'Next Steps:';
PRINT '1. Ensure stored procedures are deployed (sp_MergeStgToHis, sp_CreateTargetTableFromSource)';
PRINT '2. Update AppSettings table with your actual Sportlink API credentials';
PRINT '3. Run setup-local-debug.ps1 to verify the environment';
PRINT '';
