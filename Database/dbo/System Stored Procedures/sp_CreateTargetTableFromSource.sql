CREATE PROCEDURE [dbo].[sp_CreateTargetTableFromSource]
	@SourceSchema NVARCHAR(128),
	@SourceName   NVARCHAR(128),
	@TargetSchema NVARCHAR(128),
	@TargetName   NVARCHAR(128)
AS
BEGIN
	/*
	version | date			| name					| description
	1.0		| 12-01-2025	| Jaap van Beusekom		| Initial setup
	1.1		| 2025			| Jaap van Beusekom		| Fixed target table name using @TargetName instead of @SourceName
	*/

	IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[' + @TargetSchema + '].[' + @TargetName +']') AND type in (N'U'))
	BEGIN
		SET NOCOUNT ON;
		DECLARE @SqlString NVARCHAR(MAX) = 'CREATE TABLE ' + QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName) + ' (';

		-- Fetch the primary key field from the mapping table
		DECLARE @MtaTargetKey NVARCHAR(128);
    
		SELECT @MtaTargetKey = stm.target_pk
		  FROM mta.source_target_mapping stm
		 WHERE stm.source_entity = @SourceName 
		   AND stm.source_schema = @SourceSchema 
		   AND stm.target_entity = @TargetName
		   AND stm.target_schema = @TargetSchema;

		-- Add keyfield if exist at first
		IF @MtaTargetKey IS NOT NULL
			SET @SqlString += '' + @MtaTargetKey + ' NOT NULL,'		

		-- Fetch metadata sourcetable columns
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
			CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NOT NULL' END + ', '
		FROM sys.tables st
		INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
		INNER JOIN sys.all_columns c ON c.object_id = st.object_id
		LEFT JOIN sys.types t ON c.user_type_id = t.user_type_id
		WHERE ss.name = @SourceSchema 
		  AND st.name = @SourceName;

		-- Add additional metadata columns
		SET @SqlString += '
			mta_inserted DATETIME NULL,
			mta_modified DATETIME NULL,
			mta_deleted  DATETIME NULL
		);';

		-- Execute this SQL command
		EXEC sp_executesql @SqlString;
		-- Output the generated SQL for verification
		-- PRINT @SqlString;
	END	
	--ELSE
	--BEGIN
	--	PRINT '[' + @TargetSchema + '].[' + @TargetName +'] already exists'
	--END
END;