-- Complete Database Setup for Sportlink Function - Local Development
-- Run this script on your local SQL Server instance
-- This script will create all necessary objects for the application to run

USE master;
GO

PRINT '========================================';
PRINT 'Sportlink Database Complete Setup';
PRINT '========================================';
PRINT '';

-- Create database if it doesn't exist
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'SportlinkSqlDb')
BEGIN
    CREATE DATABASE SportlinkSqlDb;
    PRINT '✓ Database SportlinkSqlDb created successfully';
END
ELSE
BEGIN
    PRINT '✓ Database SportlinkSqlDb already exists';
END
GO

USE SportlinkSqlDb;
GO

-- ========================================
-- STEP 1: Create Schemas
-- ========================================
PRINT '';
PRINT '1. Creating Schemas...';

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'stg')
BEGIN
    EXEC('CREATE SCHEMA [stg]');
    PRINT '  ✓ Schema [stg] created';
END
ELSE PRINT '  ✓ Schema [stg] exists';

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'his')
BEGIN
    EXEC('CREATE SCHEMA [his]');
    PRINT '  ✓ Schema [his] created';
END
ELSE PRINT '  ✓ Schema [his] exists';

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'mta')
BEGIN
    EXEC('CREATE SCHEMA [mta]');
    PRINT '  ✓ Schema [mta] created';
END
ELSE PRINT '  ✓ Schema [mta] exists';

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'pub')
BEGIN
    EXEC('CREATE SCHEMA [pub]');
    PRINT '  ✓ Schema [pub] created';
END
ELSE PRINT '  ✓ Schema [pub] exists';
GO

-- ========================================
-- STEP 2: Create AppSettings Table
-- ========================================
PRINT '';
PRINT '2. Creating AppSettings Table...';

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[AppSettings]
    (
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        [sportlinkApiUrl] NVARCHAR(500) NOT NULL,
        [sportlinkClientId] NVARCHAR(500) NOT NULL,
        [CreatedDate] DATETIME2 DEFAULT GETDATE(),
        [ModifiedDate] DATETIME2 DEFAULT GETDATE()
    );
    PRINT '  ✓ AppSettings table created';
END
ELSE PRINT '  ✓ AppSettings table exists';
GO

-- Insert default settings if table is empty
IF NOT EXISTS (SELECT 1 FROM [dbo].[AppSettings])
BEGIN
    INSERT INTO [dbo].[AppSettings] ([sportlinkApiUrl], [sportlinkClientId])
    VALUES 
    (
        'https://data.sportlink.com/poule',  -- Default Sportlink API URL
        'REPLACE_WITH_YOUR_CLIENT_ID'        -- REPLACE THIS with your actual client ID
    );
    PRINT '  ✓ Default AppSettings inserted - REMEMBER TO UPDATE sportlinkClientId!';
END
ELSE
BEGIN
    PRINT '  ✓ AppSettings already contains data';
END
GO

-- ========================================
-- STEP 3: Create Metadata Table
-- ========================================
PRINT '';
PRINT '3. Creating Metadata Tables...';

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
        [merge_type] NVARCHAR(10) DEFAULT 'IUD',
        [is_active] BIT DEFAULT 1,
        [created_date] DATETIME2 DEFAULT GETDATE(),
        [modified_date] DATETIME2 DEFAULT GETDATE()
    );
    PRINT '  ✓ Table [mta].[source_target_mapping] created';
END
ELSE PRINT '  ✓ Table [mta].[source_target_mapping] exists';
GO

-- Insert mappings
DELETE FROM [mta].[source_target_mapping];
INSERT INTO [mta].[source_target_mapping] 
    ([source_schema], [source_entity], [source_pk], [target_schema], [target_entity], [target_pk], [merge_type])
VALUES 
    ('stg', 'teams', 'teamcode', 'his', 'teams', 'bk_teams', 'IUD'),
    ('stg', 'matches', 'wedstrijdcode', 'his', 'matches', 'bk_matches', 'IUD'),
    ('stg', 'matchdetails', 'WedstrijdCode', 'his', 'matchdetails', 'bk_matchdetails', 'IUD');
PRINT '  ✓ Mappings inserted for teams, matches, matchdetails';
GO

-- ========================================
-- STEP 4: Create Stored Procedures
-- ========================================
PRINT '';
PRINT '4. Creating Stored Procedures...';

-- Drop existing procedures
IF OBJECT_ID('[dbo].[sp_CreateTargetTableFromSource]', 'P') IS NOT NULL
    DROP PROCEDURE [dbo].[sp_CreateTargetTableFromSource];
GO

CREATE PROCEDURE [dbo].[sp_CreateTargetTableFromSource]
    @SourceSchema NVARCHAR(128),
    @SourceName   NVARCHAR(128),
    @TargetSchema NVARCHAR(128),
    @TargetName   NVARCHAR(128)
AS
BEGIN
    IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[' + @TargetSchema + '].[' + @TargetName +']') AND type in (N'U'))
    BEGIN
        SET NOCOUNT ON;
        DECLARE @SqlString NVARCHAR(MAX) = 'CREATE TABLE ' + QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName) + ' (';

        DECLARE @MtaTargetKey NVARCHAR(128);
        SELECT @MtaTargetKey = stm.target_pk
          FROM mta.source_target_mapping stm
         WHERE stm.source_entity = @SourceName 
           AND stm.source_schema = @SourceSchema 
           AND stm.target_entity = @TargetName
           AND stm.target_schema = @TargetSchema;

        IF @MtaTargetKey IS NOT NULL
            SET @SqlString += '' + @MtaTargetKey + ' NVARCHAR(255) NOT NULL PRIMARY KEY,'        

        SELECT @SqlString += 
            QUOTENAME(c.name) + ' ' + 
            t.name + 
            CASE 
            WHEN t.name IN ('varchar', 'nvarchar', 'char', 'nchar') 
            THEN '(' + 
                CASE 
                WHEN c.max_length = -1 THEN 'MAX' 
                WHEN t.name IN ('nvarchar','nchar') THEN CAST(c.max_length / 2 AS VARCHAR) 
                ELSE CAST(c.max_length AS VARCHAR) 
                END + ')'
            ELSE '' 
            END + ' ' +
            CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NULL' END + ', '  -- Changed to NULL for flexibility
        FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        LEFT JOIN sys.types t ON c.user_type_id = t.user_type_id
        WHERE ss.name = @SourceSchema 
          AND st.name = @SourceName;

        SET @SqlString += '
            mta_inserted DATETIME NULL,
            mta_modified DATETIME NULL,
            mta_deleted  DATETIME NULL
        );';

        EXEC sp_executesql @SqlString;
    END
END;
GO

PRINT '  ✓ sp_CreateTargetTableFromSource created';

IF OBJECT_ID('[dbo].[sp_MergeStgToHis]', 'P') IS NOT NULL
    DROP PROCEDURE [dbo].[sp_MergeStgToHis];
GO

CREATE PROCEDURE [dbo].[sp_MergeStgToHis]
    @SourceSchema    nvarchar(128),
    @SourceName        nvarchar(128),  
    @TargetSchema    nvarchar(128),
    @TargetName        nvarchar(128)  
AS
BEGIN
    SET NOCOUNT ON;
    EXECUTE [dbo].[sp_CreateTargetTableFromSource] @SourceSchema , @SourceName , @TargetSchema , @TargetName

    DECLARE @ColName NVARCHAR(MAX);
    DECLARE @Index INT = 1;

    DECLARE @SqlString    NVARCHAR(MAX) = ''
    DECLARE @SqlStringTmp NVARCHAR(MAX) = ''

    SET @SqlString += 
'
MERGE ['+@TargetSchema+'].['+@TargetName+'] AS target
USING ['+@SourceSchema+'].['+@SourceName+'] AS source
'

    DECLARE @SourcePk VARCHAR(255) ;
    DECLARE @SourcePkColumns VARCHAR(MAX) ;
    DECLARE @TargetPk VARCHAR(255) ;

    SELECT @SourcePk = source_pk 
         , @TargetPk  = target_pk 
     FROM mta.source_target_mapping 
    WHERE source_entity = @SourceName 
      AND source_schema = @SourceSchema 
      AND target_schema = @TargetSchema
      AND target_entity = @TargetName ;

    IF @TargetPk IS NULL
        SET @TargetPk = 'bk_' + @TargetName

    IF @SourcePk IS NOT NULL
    BEGIN
        SET @ColName = '';
        SET @Index = 1;

        DECLARE @ColNames TABLE (ColName NVARCHAR(MAX));
        INSERT INTO @ColNames (ColName)
            SELECT value FROM STRING_SPLIT(@SourcePk, ',');

        WHILE EXISTS (SELECT 1 FROM @ColNames WHERE ColName IS NOT NULL)
        BEGIN
            SELECT TOP 1 @ColName = ColName FROM @ColNames WHERE ColName IS NOT NULL;
            IF @Index = 1
            BEGIN
                SET @SqlString  += 
'ON ISNULL(CAST(target.' + @ColName + ' AS NVARCHAR(255)),'''') = ISNULL(CAST(source.' + @ColName + ' AS NVARCHAR(255)),'''') ';
                SET @SourcePkColumns = 'source.'+@ColName + ' '
            END
            ELSE
            BEGIN
                SET @SqlString  += '
AND ISNULL(CAST(target.' + @ColName + ' AS NVARCHAR(255)),'''') = ISNULL(CAST(source.' + @ColName + ' AS NVARCHAR(255)),'''') ';
                SET @SourcePkColumns += ', source.'+@ColName + ' '
            END
            DELETE FROM @ColNames WHERE ColName = @ColName;
            SET @Index += 1;
        END
    END

SET @SqlString += 
'
WHEN MATCHED AND ('

    DECLARE @ColNamesSourcePK TABLE (ColName NVARCHAR(MAX));
    INSERT INTO @ColNamesSourcePK (ColName)
        SELECT value FROM STRING_SPLIT(@SourcePk, ',');

    DECLARE @SourceTableColumns TABLE (TableColumn NVARCHAR(MAX));
    INSERT INTO @SourceTableColumns (TableColumn )
        SELECT '[' + c.name + ']' FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        LEFT JOIN sys.types t ON c.user_type_id = t.user_type_id
        WHERE ss.name = @SourceSchema 
            AND st.name = @SourceName;

    DECLARE @SourceTableColumnsNoPk TABLE (TableColumn NVARCHAR(MAX));
    INSERT INTO @SourceTableColumnsNoPk (TableColumn )
        SELECT '[' + c.name + ']' FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        LEFT JOIN sys.types t ON c.user_type_id = t.user_type_id
        WHERE ss.name = @SourceSchema 
            AND st.name = @SourceName
            AND '[' + c.name + ']' NOT IN (SELECT value FROM STRING_SPLIT(@SourcePk, ','));

    SET @ColName = '';
    SET @Index = 1;
    WHILE EXISTS (SELECT 1 FROM @SourceTableColumnsNoPk WHERE TableColumn IS NOT NULL)
        BEGIN
            SELECT TOP 1 @ColName = TableColumn FROM @SourceTableColumnsNoPk WHERE TableColumn IS NOT NULL;
            IF @Index = 1
                BEGIN
                SET @SqlString    += '
    COALESCE(CAST(target.'+@ColName + ' AS NVARCHAR(MAX)), '''') <> COALESCE(CAST(source.' +@ColName + ' AS NVARCHAR(MAX)), '''') ' ;
                SET @SqlStringTmp += '
    target.'+@ColName + ' = source.'  +@ColName + ',' ;
                END
            ELSE
                BEGIN
                SET @SqlString  += '
 OR COALESCE(CAST(target.'+@ColName + ' AS NVARCHAR(MAX)), '''') <> COALESCE(CAST(source.' +@ColName + ' AS NVARCHAR(MAX)), '''') ' ; 
                SET @SqlStringTmp += '
    target.'+@ColName + ' = source.' +@ColName + ',' ;
                END
            DELETE FROM @SourceTableColumnsNoPk WHERE TableColumn = @ColName;
            SET @Index += 1;
        END
    
    SET @SqlString  += ') 
THEN
UPDATE SET ' 
    SET @SqlString  += @SqlStringTmp  + '
    target.mta_modified = GETDATE()'
    SET @SqlString  += '
WHEN NOT MATCHED BY TARGET THEN '

    DECLARE @SqlStringTargets NVARCHAR(MAX) = '';
    DECLARE @SqlStringValues  NVARCHAR(MAX) = '';

    WHILE EXISTS (SELECT 1 FROM @SourceTableColumns WHERE TableColumn IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = TableColumn FROM @SourceTableColumns WHERE TableColumn IS NOT NULL;
        SET @SqlStringTargets += ','+@ColName + ' ' ; 
        SET @SqlStringValues  += ',source.'+@ColName + ' ' ;
        DELETE FROM @SourceTableColumns WHERE TableColumn = @ColName;
    END

    SET @SqlString += '
INSERT 
    (' + @TargetPk + @SqlStringTargets + ', mta_inserted,mta_modified)
VALUES 
    (CAST(' + @SourcePkColumns + ' AS NVARCHAR(255))' + @SqlStringValues + ', GETDATE(), GETDATE());';
    
    EXEC sp_sqlexec @SqlString ;
END;
GO

PRINT '  ✓ sp_MergeStgToHis created';

-- ========================================
-- FINAL MESSAGE
-- ========================================
PRINT '';
PRINT '========================================';
PRINT 'Database Setup Complete!';
PRINT '========================================';
PRINT '';
PRINT 'IMPORTANT: Please update the sportlinkClientId in dbo.AppSettings table!';
PRINT '';
PRINT 'To view current settings:';
PRINT 'SELECT * FROM dbo.AppSettings;';
PRINT '';
PRINT 'To update client ID:';
PRINT 'UPDATE dbo.AppSettings SET sportlinkClientId = ''YOUR_ACTUAL_CLIENT_ID'';';
PRINT '';
PRINT 'Ready to run the Azure Function!';
PRINT '';
GO
