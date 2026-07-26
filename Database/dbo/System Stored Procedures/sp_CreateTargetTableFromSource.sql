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

		/*
			#606: index op de business key en op ClubCode.

			Zonder index doet elke join/filter op de business key of ClubCode een full heap scan, en
			niets in het schema voorkomt duplicaten als de MERGE ON-matching ooit misaligneert.

			De business-key-index is UNIEK omdat de tabel hier net leeg is aangemaakt — dat kan dus
			niet falen. Voor bestaande tabellen gebeurt hetzelfde in Script.PostDeployment1.sql, daar
			wél voorwaardelijk: de Sportlink-feed levert aantoonbaar duplicaten (#569), en een
			mislukte CREATE UNIQUE INDEX zou de hele deploy laten falen.

			@MtaTargetKey heeft de vorm 'kolomnaam DATATYPE' — zelfde parsing als sp_MergeStgToHis.
		*/
		IF @MtaTargetKey IS NOT NULL
		BEGIN
			DECLARE @BkColumn NVARCHAR(128) =
				CASE WHEN CHARINDEX(' ', @MtaTargetKey) > 0
					 THEN LEFT(@MtaTargetKey, CHARINDEX(' ', @MtaTargetKey) - 1)
					 ELSE @MtaTargetKey END;

			DECLARE @IndexSql NVARCHAR(MAX) =
				'CREATE UNIQUE NONCLUSTERED INDEX ' + QUOTENAME('UQ_' + @TargetName + '_bk') +
				' ON ' + QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName) +
				' (' + QUOTENAME(@BkColumn) + ');';
			EXEC sp_executesql @IndexSql;
		END

		-- Ondersteunende index op ClubCode voor his-tabellen die de discriminator hebben.
		IF EXISTS (SELECT 1 FROM sys.columns
				   WHERE object_id = OBJECT_ID(QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName))
					 AND name = 'ClubCode')
		BEGIN
			DECLARE @ClubIndexSql NVARCHAR(MAX) =
				'CREATE NONCLUSTERED INDEX ' + QUOTENAME('IX_' + @TargetName + '_ClubCode') +
				' ON ' + QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName) +
				' ([ClubCode]);';
			EXEC sp_executesql @ClubIndexSql;
		END
	END
	--ELSE
	--BEGIN
	--	PRINT '[' + @TargetSchema + '].[' + @TargetName +'] already exists'
	--END
END;