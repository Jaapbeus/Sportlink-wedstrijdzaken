CREATE PROCEDURE [dbo].[sp_UpdateClubSeasonTable]
    @ClubCode NVARCHAR(20),
    @Vandaag  DATE = NULL
AS
BEGIN
	-- #1715: de seizoenen per club (dbo.ClubSeason), elk uit de eigen SeasonStartMonth van die club. Opvolger
	-- van sp_UpdateSeasonTable (clubneutraal dbo.Season met MIN() over alle clubs), dat voor de overgang
	-- blijft bestaan maar niet meer wordt aangeroepen (expand/contract). Zelfde regel als
	-- Planner.Shared/Sync/SeizoenKalender.cs en Database.Postgres/PostgresSeasonProcedures.cs.
	--
	-- @Vandaag is er voor tests rond de jaarwisseling; zonder waarde geldt de huidige datum. De datumrekenkunde
	-- gebruikt DATEADD i.p.v. DATEFROMPARTS(.., @SeasonStartMonth-1/-2, ..): die vorm gaf bij januari/februari
	-- een ongeldige maand (0, -1).
	SET NOCOUNT ON;
	SET XACT_ABORT ON;
	IF @Vandaag IS NULL
		SET @Vandaag = CAST(GETDATE() AS DATE);

	DECLARE @SeasonStartMonth INT;
	DECLARE @Jaar      INT = YEAR(@Vandaag);
	DECLARE @Startjaar INT;
	DECLARE @YearStart INT;
	DECLARE @YearEnd   INT;

	BEGIN TRANSACTION;

	-- De startmaand van de club, onder lock (Codex-review #1743 ronde 1): een gelijktijdige wijziging in
	-- Beheer > Instellingen wacht tot de seizoenen van deze club klaar zijn, en omgekeerd.
	SELECT @SeasonStartMonth = [SeasonStartMonth]
	FROM [dbo].[AppSettings] WITH (UPDLOCK, HOLDLOCK)
	WHERE [ClubCode] = @ClubCode;

	IF @@ROWCOUNT = 0
	BEGIN
		COMMIT TRANSACTION;
		RETURN;
	END

	IF @SeasonStartMonth IS NULL OR @SeasonStartMonth NOT BETWEEN 1 AND 12
		SET @SeasonStartMonth = 7;

	-- No seasons found for this club! Add last two seasons
	IF NOT EXISTS (SELECT 1 FROM [dbo].[ClubSeason] WHERE [ClubCode] = @ClubCode)
	BEGIN
		INSERT INTO [dbo].[ClubSeason] ([ClubCode], [Name], [DateFrom], [DateUntil])
		VALUES
			(@ClubCode, CONCAT(@Jaar-2,'-',@Jaar-1),
			 DATEFROMPARTS(@Jaar-2,@SeasonStartMonth,1), DATEADD(DAY,-1,DATEFROMPARTS(@Jaar-1,@SeasonStartMonth,1))),
			(@ClubCode, CONCAT(@Jaar-1,'-',@Jaar),
			 DATEFROMPARTS(@Jaar-1,@SeasonStartMonth,1), DATEADD(DAY,-1,DATEFROMPARTS(@Jaar,@SeasonStartMonth,1)));
	END

	-- Doorrol: vanaf twee maanden vóór de start bestaat het seizoen. Zowel het seizoen dat dit kalenderjaar
	-- begint als dat van volgend jaar: bij een start in januari/februari ligt de drempel op 1 november resp.
	-- 1 december van dit jaar (SeizoenKalender.DoorTeRollenStartjaren).
	SET @Startjaar = @Jaar;
	WHILE @Startjaar <= @Jaar + 1
	BEGIN
		IF @Vandaag >= DATEADD(MONTH, @SeasonStartMonth - 3, DATEFROMPARTS(@Startjaar, 1, 1))
		   AND NOT EXISTS (SELECT 1 FROM [dbo].[ClubSeason]
		                   WHERE [ClubCode] = @ClubCode AND [Name] = CONCAT(@Startjaar,'-',@Startjaar+1))
			INSERT INTO [dbo].[ClubSeason] ([ClubCode], [Name], [DateFrom], [DateUntil])
			VALUES (@ClubCode, CONCAT(@Startjaar,'-',@Startjaar+1),
			        DATEFROMPARTS(@Startjaar,@SeasonStartMonth,1),
			        DATEADD(DAY,-1,DATEFROMPARTS(@Startjaar+1,@SeasonStartMonth,1)));
		SET @Startjaar = @Startjaar + 1;
	END

	COMMIT TRANSACTION;

	-- Create a new DateTable based on the seasons. De DateTable is een kalender zonder club en omvat daarom
	-- het bereik van de seizoenen van alle clubs (geen seizoensafleiding, alleen het datumbereik).
	SELECT @YearStart = YEAR(MIN([DateFrom])), @YearEnd = YEAR(MAX([DateUntil])) FROM [dbo].[ClubSeason];
	IF @YearStart IS NOT NULL
		EXEC [dbo].[sp_CreateDateTable] @YearStart, @YearEnd;
END;
