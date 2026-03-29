-- Fix the sp_CreateTargetTableFromSource stored procedure
-- This updates the procedure to work correctly without IUD columns

USE SportlinkSqlDb;
GO

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
    IF NOT EXISTS (
        SELECT * FROM sys.objects 
        WHERE object_id = OBJECT_ID(N'[' + @TargetSchema + '].[' + @TargetName +']') 
        AND type in (N'U')
    )
    BEGIN
        SET NOCOUNT ON;
        DECLARE @SqlString NVARCHAR(MAX) = 'CREATE TABLE ' + QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName) + ' (';

        -- Get the target PK from metadata
        DECLARE @MtaTargetKey NVARCHAR(255);
        DECLARE @MtaTargetKeyDataType NVARCHAR(50);
        
        SELECT @MtaTargetKey = stm.target_pk
          FROM mta.source_target_mapping stm
         WHERE stm.source_entity = @SourceName 
           AND stm.source_schema = @SourceSchema 
           AND stm.target_entity = @TargetName
           AND stm.target_schema = @TargetSchema;

        -- Extract just the column name if it includes data type
        IF @MtaTargetKey LIKE '%NVARCHAR%'
        BEGIN
            SET @MtaTargetKeyDataType = SUBSTRING(@MtaTargetKey, CHARINDEX(' ', @MtaTargetKey) + 1, LEN(@MtaTargetKey));
            SET @MtaTargetKey = LTRIM(RTRIM(SUBSTRING(@MtaTargetKey, 1, CHARINDEX(' ', @MtaTargetKey) - 1)));
        END
        ELSE IF @MtaTargetKey LIKE '%BIGINT%'
        BEGIN
            SET @MtaTargetKeyDataType = 'BIGINT';
            SET @MtaTargetKey = LTRIM(RTRIM(SUBSTRING(@MtaTargetKey, 1, CHARINDEX(' ', @MtaTargetKey) - 1)));
        END
        ELSE
        BEGIN
            SET @MtaTargetKeyDataType = 'NVARCHAR(255)';
        END

        -- Add the business key column
        IF @MtaTargetKey IS NOT NULL
            SET @SqlString += QUOTENAME(@MtaTargetKey) + ' ' + @MtaTargetKeyDataType + ' NOT NULL PRIMARY KEY,';

        -- Add all columns from source table
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
                WHEN t.name IN ('decimal', 'numeric')
                THEN '(' + CAST(c.precision AS VARCHAR) + ',' + CAST(c.scale AS VARCHAR) + ')'
                WHEN t.name = 'time'
                THEN '(' + CAST(c.scale AS VARCHAR) + ')'
                ELSE '' 
            END + ' ' +
            'NULL' + ', '  -- Allow NULLs for flexibility
        FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        LEFT JOIN sys.types t ON c.user_type_id = t.user_type_id
        WHERE ss.name = @SourceSchema 
          AND st.name = @SourceName
        ORDER BY c.column_id;

        -- Add metadata columns
        SET @SqlString += '
            mta_inserted DATETIME NULL,
            mta_modified DATETIME NULL,
            mta_deleted  DATETIME NULL
        );';

        -- Execute the CREATE TABLE statement
        EXEC sp_executesql @SqlString;
        
        PRINT 'Table created: [' + @TargetSchema + '].[' + @TargetName + ']';
    END
    ELSE
    BEGIN
        PRINT 'Table already exists: [' + @TargetSchema + '].[' + @TargetName + ']';
    END
END;
GO

PRINT 'sp_CreateTargetTableFromSource updated successfully!';
GO
