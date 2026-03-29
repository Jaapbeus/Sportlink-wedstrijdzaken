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
	IF (SELECT YEAR(MAX(DateUntil)) FROM [dbo].[Season]) <> YEAR(GETDATE()) +1
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
			CONCAT(YEAR(GETDATE()),'-',YEAR(GETDATE())+1),
			DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth,1),
			EOMONTH(DATEFROMPARTS(YEAR(GETDATE())+1,@SeasonStartMonth-1,1))
			)
	END;

	-- Create a new DateTable based on the new start and enddate in seasons table
	SELECT @YearStart = YEAR(MIN(DateFrom))  FROM [dbo].[Season];
	SELECT @YearEnd   = YEAR(MAX(DateUntil)) FROM [dbo].[Season];
	EXEC [dbo].[sp_CreateDateTable] @YearStart, @YearEnd;
END;