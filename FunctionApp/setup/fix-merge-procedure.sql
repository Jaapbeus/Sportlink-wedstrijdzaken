-- Fix the sp_MergeStgToHis stored procedure
-- This updates the procedure to work correctly without IUD and IUD_Timestamp columns

USE SportlinkSqlDb;
GO

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
    
    -- First create target table if it doesn't exist
    EXECUTE [dbo].[sp_CreateTargetTableFromSource] @SourceSchema, @SourceName, @TargetSchema, @TargetName

    DECLARE @ColName NVARCHAR(MAX);
    DECLARE @Index INT = 1;

    DECLARE @SqlString    NVARCHAR(MAX) = ''
    DECLARE @SqlStringTmp NVARCHAR(MAX) = ''

    -- Start building the MERGE statement
    SET @SqlString += 
'
MERGE ['+@TargetSchema+'].['+@TargetName+'] AS target
USING ['+@SourceSchema+'].['+@SourceName+'] AS source
'

    -- Get the primary key columns from metadata table
    DECLARE @SourcePk VARCHAR(255);
    DECLARE @SourcePkColumns VARCHAR(MAX);
    DECLARE @TargetPk VARCHAR(255);
    DECLARE @TargetPkFull VARCHAR(500);

    SELECT @SourcePk = source_pk 
         , @TargetPkFull  = target_pk 
     FROM mta.source_target_mapping 
    WHERE source_entity = @SourceName 
      AND source_schema = @SourceSchema 
      AND target_schema = @TargetSchema
      AND target_entity = @TargetName;

    -- Extract just the column name from "columnname DATATYPE" format
    IF @TargetPkFull IS NOT NULL
    BEGIN
        IF CHARINDEX(' ', @TargetPkFull) > 0
            SET @TargetPk = LTRIM(RTRIM(SUBSTRING(@TargetPkFull, 1, CHARINDEX(' ', @TargetPkFull) - 1)))
        ELSE
            SET @TargetPk = @TargetPkFull
    END
    ELSE
        SET @TargetPk = 'bk_' + @TargetName

    IF @SourcePk IS NOT NULL
    BEGIN
        SET @ColName = '';
        SET @Index = 1;

        DECLARE @ColNames TABLE (ColName NVARCHAR(MAX));
        INSERT INTO @ColNames (ColName)
            SELECT TRIM(value) FROM STRING_SPLIT(@SourcePk, ',');

        -- Build the ON clause for matching records
        WHILE EXISTS (SELECT 1 FROM @ColNames WHERE ColName IS NOT NULL)
        BEGIN
            SELECT TOP 1 @ColName = ColName FROM @ColNames WHERE ColName IS NOT NULL;
            IF @Index = 1
            BEGIN
                SET @SqlString  += 
'ON ISNULL(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)),'''') = ISNULL(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)),'''') ';
                SET @SourcePkColumns = 'source.'+@ColName + ' '
            END
            ELSE
            BEGIN
                SET @SqlString  += '
AND ISNULL(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)),'''') = ISNULL(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)),'''') ';
                SET @SourcePkColumns += ', source.'+@ColName + ' '
            END
            DELETE FROM @ColNames WHERE ColName = @ColName;
            SET @Index += 1;
        END
    END

    SET @SqlString += '
WHEN MATCHED AND ('

    -- Build list of columns excluding PK columns
    DECLARE @SourceTableColumns TABLE (TableColumn NVARCHAR(MAX));
    INSERT INTO @SourceTableColumns (TableColumn)
        SELECT '[' + c.name + ']' 
        FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        WHERE ss.name = @SourceSchema 
            AND st.name = @SourceName;

    DECLARE @SourceTableColumnsNoPk TABLE (TableColumn NVARCHAR(MAX));
    INSERT INTO @SourceTableColumnsNoPk (TableColumn)
        SELECT '[' + c.name + ']' 
        FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        WHERE ss.name = @SourceSchema 
            AND st.name = @SourceName
            AND '[' + c.name + ']' NOT IN (SELECT '[' + TRIM(value) + ']' FROM STRING_SPLIT(@SourcePk, ','));

    -- Build the WHEN MATCHED condition and UPDATE clause
    SET @ColName = '';
    SET @Index = 1;
    
    WHILE EXISTS (SELECT 1 FROM @SourceTableColumnsNoPk WHERE TableColumn IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = TableColumn FROM @SourceTableColumnsNoPk WHERE TableColumn IS NOT NULL;
        IF @Index = 1
        BEGIN
            SET @SqlString    += '
    COALESCE(CAST(target.'+@ColName + ' AS NVARCHAR(MAX)), '''') <> COALESCE(CAST(source.' +@ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SqlStringTmp += '
    target.'+@ColName + ' = source.'  +@ColName + ',';
        END
        ELSE
        BEGIN
            SET @SqlString  += '
 OR COALESCE(CAST(target.'+@ColName + ' AS NVARCHAR(MAX)), '''') <> COALESCE(CAST(source.' +@ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SqlStringTmp += '
    target.'+@ColName + ' = source.' +@ColName + ',';
        END
        DELETE FROM @SourceTableColumnsNoPk WHERE TableColumn = @ColName;
        SET @Index += 1;
    END
    
    SET @SqlString  += ') 
THEN
UPDATE SET ';
    SET @SqlString  += @SqlStringTmp  + '
    target.mta_modified = GETDATE()';
    
    SET @SqlString  += '
WHEN NOT MATCHED BY TARGET THEN ';

    DECLARE @SqlStringTargets NVARCHAR(MAX) = '';
    DECLARE @SqlStringValues  NVARCHAR(MAX) = '';

    -- Build INSERT column list and values
    WHILE EXISTS (SELECT 1 FROM @SourceTableColumns WHERE TableColumn IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = TableColumn FROM @SourceTableColumns WHERE TableColumn IS NOT NULL;
        SET @SqlStringTargets += ','+@ColName + ' ';
        SET @SqlStringValues  += ',source.'+@ColName + ' ';
        DELETE FROM @SourceTableColumns WHERE TableColumn = @ColName;
    END

    SET @SqlString += '
INSERT 
    (' + @TargetPk + @SqlStringTargets + ', mta_inserted, mta_modified)
VALUES 
    (CAST(' + @SourcePkColumns + ' AS NVARCHAR(255))' + @SqlStringValues + ', GETDATE(), GETDATE());';
    
    -- Execute the dynamic SQL
    EXEC sp_executesql @SqlString;
    
    PRINT 'Merged ' + @SourceName + ' to ' + @TargetName;
END;
GO

PRINT 'sp_MergeStgToHis updated successfully!';
GO
