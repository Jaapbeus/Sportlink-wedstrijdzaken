CREATE PROCEDURE [dbo].[sp_MergeStgToHis]
    @SourceSchema nvarchar(128),
    @SourceName   nvarchar(128),
    @TargetSchema nvarchar(128),
    @TargetName   nvarchar(128)
AS
BEGIN
    /*
    version | date       | name              | description
    1.0     | 12-01-2025 | Jaap van Beusekom | Initial setup
    1.1     | 25-01-2025 | Jaap van Beusekom | NULL handling for non-string columns using CAST AS NVARCHAR(MAX)
    1.2     | 2025       | Jaap van Beusekom | Multi-column business key support using CONCAT
    */
    SET NOCOUNT ON;
    -- Create the target table from source structure if it does not yet exist
    EXECUTE [dbo].[sp_CreateTargetTableFromSource] @SourceSchema, @SourceName, @TargetSchema, @TargetName;

    DECLARE @ColName        NVARCHAR(MAX);
    DECLARE @Index          INT = 1;
    DECLARE @SqlString      NVARCHAR(MAX) = '';
    DECLARE @SqlStringTmp   NVARCHAR(MAX) = '';

    SET @SqlString +=
'MERGE [' + @TargetSchema + '].[' + @TargetName + '] AS target
USING [' + @SourceSchema + '].[' + @SourceName + '] AS source
';
    -- Retrieve primary key columns and target key definition from metadata
    DECLARE @SourcePk       VARCHAR(255);
    DECLARE @SourcePkColumns VARCHAR(MAX);
    DECLARE @TargetPk       VARCHAR(255);
    DECLARE @TargetPkFull   VARCHAR(255);

    SELECT @SourcePk     = source_pk
         , @TargetPkFull = target_pk
      FROM mta.source_target_mapping
     WHERE source_entity = @SourceName
       AND source_schema = @SourceSchema
       AND target_schema = @TargetSchema
       AND target_entity = @TargetName;

    -- Extract just the column name from target_pk (format: 'columnname DATATYPE')
    IF @TargetPkFull IS NOT NULL
    BEGIN
        IF CHARINDEX(' ', @TargetPkFull) > 0
            SET @TargetPk = LTRIM(RTRIM(SUBSTRING(@TargetPkFull, 1, CHARINDEX(' ', @TargetPkFull) - 1)));
        ELSE
            SET @TargetPk = @TargetPkFull;
    END
    ELSE
        SET @TargetPk = 'bk_' + @TargetName;

    IF @SourcePk IS NULL
        RETURN;

    -- Build the ON clause using source PK columns, casting to NVARCHAR to support any data type
    DECLARE @ColNames TABLE (ColName NVARCHAR(MAX));
    INSERT INTO @ColNames (ColName)
        SELECT TRIM(value) FROM STRING_SPLIT(@SourcePk, ',');

    WHILE EXISTS (SELECT 1 FROM @ColNames WHERE ColName IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = ColName FROM @ColNames WHERE ColName IS NOT NULL;
        IF @Index = 1
        BEGIN
            SET @SqlString +=
'ON ISNULL(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)), '''') = ISNULL(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SourcePkColumns = 'source.' + @ColName + ' ';
        END
        ELSE
        BEGIN
            SET @SqlString +=
'AND ISNULL(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)), '''') = ISNULL(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SourcePkColumns += ', source.' + @ColName + ' ';
        END
        DELETE FROM @ColNames WHERE ColName = @ColName;
        SET @Index += 1;
    END

    SET @SqlString += '
WHEN MATCHED AND (';

    -- Build all source columns and non-PK source columns
    DECLARE @SourceTableColumns TABLE (TableColumn NVARCHAR(MAX));
    INSERT INTO @SourceTableColumns (TableColumn)
        SELECT '[' + c.name + ']'
        FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        WHERE ss.name = @SourceSchema AND st.name = @SourceName;

    DECLARE @SourceTableColumnsNoPk TABLE (TableColumn NVARCHAR(MAX));
    INSERT INTO @SourceTableColumnsNoPk (TableColumn)
        SELECT '[' + c.name + ']'
        FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        WHERE ss.name = @SourceSchema AND st.name = @SourceName
          AND '[' + c.name + ']' NOT IN (SELECT '[' + TRIM(value) + ']' FROM STRING_SPLIT(@SourcePk, ','));

    -- Build the WHEN MATCHED condition and UPDATE SET clause
    SET @Index = 1;
    WHILE EXISTS (SELECT 1 FROM @SourceTableColumnsNoPk WHERE TableColumn IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = TableColumn FROM @SourceTableColumnsNoPk WHERE TableColumn IS NOT NULL;
        IF @Index = 1
        BEGIN
            SET @SqlString  += '
    COALESCE(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)), '''') <> COALESCE(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SqlStringTmp += '
    target.' + @ColName + ' = source.' + @ColName + ',';
        END
        ELSE
        BEGIN
            SET @SqlString  += '
 OR COALESCE(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)), '''') <> COALESCE(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SqlStringTmp += '
    target.' + @ColName + ' = source.' + @ColName + ',';
        END
        DELETE FROM @SourceTableColumnsNoPk WHERE TableColumn = @ColName;
        SET @Index += 1;
    END

    SET @SqlString += ')
THEN UPDATE SET '
        + @SqlStringTmp + '
    target.mta_modified = GETDATE()';

    SET @SqlString += '
WHEN NOT MATCHED BY TARGET THEN ';

    -- Build the INSERT column list and VALUES for new records
    DECLARE @SqlStringTargets NVARCHAR(MAX) = '';
    DECLARE @SqlStringValues  NVARCHAR(MAX) = '';

    WHILE EXISTS (SELECT 1 FROM @SourceTableColumns WHERE TableColumn IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = TableColumn FROM @SourceTableColumns WHERE TableColumn IS NOT NULL;
        SET @SqlStringTargets += ', ' + @ColName;
        SET @SqlStringValues  += ', source.' + @ColName;
        DELETE FROM @SourceTableColumns WHERE TableColumn = @ColName;
    END

    -- CONCAT supports both single and multi-column source keys
    SET @SqlString += '
INSERT (' + @TargetPk + @SqlStringTargets + ', mta_inserted, mta_modified)
VALUES (CONCAT('''', ' + @SourcePkColumns + ')' + @SqlStringValues + ', GETDATE(), GETDATE());';

    EXEC sp_executesql @SqlString;

    PRINT 'Merged ' + @SourceName + ' into ' + @TargetName;
END;
