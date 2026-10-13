CREATE PROCEDURE [dbo].[sp_UpdateSeasonTable]
    @SeasonStartMonth INT
AS
BEGIN
	DECLARE @YearStart INT;
	DECLARE @YearEnd   INT;

	-- No seasons found! Add last two seasons
	IF (SELECT YEAR(MAX(DateUntil)) FROM [dbo].[Season]) IS NULL 
	BEGIN
		INSERT INTO [dbo].[Season]
			(
			[Name],
			[DateFrom],
			[DateUntil]
			)
		VALUES 
			(
			CONCAT(YEAR(GETDATE())-2,'-',YEAR(GETDATE())-1),
			DATEFROMPARTS(YEAR(GETDATE())-2,@SeasonStartMonth,1),
			EOMONTH(DATEFROMPARTS(YEAR(GETDATE())-1,@SeasonStartMonth-1,1))
			),
			(
			CONCAT(YEAR(GETDATE())-1,'-',YEAR(GETDATE())),
			DATEFROMPARTS(YEAR(GETDATE())-1,@SeasonStartMonth,1),
			EOMONTH(DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth-1,1))
			);
	END

	-- Create 2 months before start of a new season a new record in season table
	--
	-- #631: de guard controleerde YEAR(MAX(DateUntil)) <> YEAR(GETDATE())+1. Zodra er een
	-- TOEKOMSTIG seizoen in de tabel staat is MAX(DateUntil) niet meer het huidige seizoen en kan
	-- die vergelijking nooit meer onwaar worden. In productie stond 2027-2028, dus de conditie was
	-- permanent waar en werd 2026-2027 bij ELKE deploy opnieuw ingevoegd (3 identieke rijen).
	-- Nu wordt gecontroleerd of het seizoen dat we gaan maken al bestaat. Dat is idempotent en
	-- werkt ook met toekomstige seizoenen in de tabel.
	DECLARE @NewSeasonName NCHAR(9) = CONCAT(YEAR(GETDATE()),'-',YEAR(GETDATE())+1);

	IF NOT EXISTS (SELECT 1 FROM [dbo].[Season] WHERE [Name] = @NewSeasonName)
		AND GETDATE() >= DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth-2,1)
	BEGIN
		INSERT INTO [dbo].[Season]
			(
			[Name],
			[DateFrom],
			[DateUntil]
			)
		 VALUES
			(
			@NewSeasonName,
			DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth,1),
			EOMONTH(DATEFROMPARTS(YEAR(GETDATE())+1,@SeasonStartMonth-1,1))
			)
	END;

	-- Create a new DateTable based on the new start and enddate in seasons table
	SELECT @YearStart = YEAR(MIN(DateFrom))  FROM [dbo].[Season];
	SELECT @YearEnd   = YEAR(MAX(DateUntil)) FROM [dbo].[Season];
	EXEC [dbo].[sp_CreateDateTable] @YearStart, @YearEnd;
END;