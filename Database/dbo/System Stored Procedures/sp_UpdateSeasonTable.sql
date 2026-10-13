CREATE PROCEDURE [dbo].[sp_UpdateSeasonTable]
    @ClubCode NVARCHAR(20),
    @SeasonStartMonth INT
AS
BEGIN
	-- #1715: de seizoenen worden per club bijgehouden (dbo.Season.ClubCode) met de eigen
	-- SeasonStartMonth van die club. Een ongeldige maand valt terug op juli (de kolomdefault).
	-- De datumrekenkunde gebruikt DATEADD i.p.v. DATEFROMPARTS(.., @SeasonStartMonth-1/-2, ..): die
	-- vorm gaf bij januari/februari een ongeldige maand (0, -1) en liet de deploy falen.
	IF @SeasonStartMonth IS NULL OR @SeasonStartMonth NOT BETWEEN 1 AND 12
		SET @SeasonStartMonth = 7;

	DECLARE @YearStart INT;
	DECLARE @YearEnd   INT;

	-- No seasons found for this club! Add last two seasons
	IF NOT EXISTS (SELECT 1 FROM [dbo].[Season] WHERE [ClubCode] = @ClubCode)
	BEGIN
		INSERT INTO [dbo].[Season]
			(
			[ClubCode],
			[Name],
			[DateFrom],
			[DateUntil]
			)
		VALUES 
			(
			@ClubCode,
			CONCAT(YEAR(GETDATE())-2,'-',YEAR(GETDATE())-1),
			DATEFROMPARTS(YEAR(GETDATE())-2,@SeasonStartMonth,1),
			DATEADD(DAY,-1,DATEFROMPARTS(YEAR(GETDATE())-1,@SeasonStartMonth,1))
			),
			(
			@ClubCode,
			CONCAT(YEAR(GETDATE())-1,'-',YEAR(GETDATE())),
			DATEFROMPARTS(YEAR(GETDATE())-1,@SeasonStartMonth,1),
			DATEADD(DAY,-1,DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth,1))
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

	IF NOT EXISTS (SELECT 1 FROM [dbo].[Season] WHERE [ClubCode] = @ClubCode AND [Name] = @NewSeasonName)
		AND CAST(GETDATE() AS DATE) >= DATEADD(MONTH,@SeasonStartMonth-3,DATEFROMPARTS(YEAR(GETDATE()),1,1))
	BEGIN
		INSERT INTO [dbo].[Season]
			(
			[ClubCode],
			[Name],
			[DateFrom],
			[DateUntil]
			)
		 VALUES
			(
			@ClubCode,
			@NewSeasonName,
			DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth,1),
			DATEADD(DAY,-1,DATEFROMPARTS(YEAR(GETDATE())+1,@SeasonStartMonth,1))
			)
	END;

	-- Create a new DateTable based on the new start and enddate in seasons table. De DateTable is een
	-- kalender zonder club en omvat daarom het bereik van de seizoenen van alle clubs.
	SELECT @YearStart = YEAR(MIN(DateFrom))  FROM [dbo].[Season];
	SELECT @YearEnd   = YEAR(MAX(DateUntil)) FROM [dbo].[Season];
	EXEC [dbo].[sp_CreateDateTable] @YearStart, @YearEnd;
END;
