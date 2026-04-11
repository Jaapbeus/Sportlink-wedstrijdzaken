/*
Post-Deployment Script Template							
--------------------------------------------------------------------------------------
 This file contains SQL statements that will be appended to the build script.		
 Use SQLCMD syntax to include a file in the post-deployment script.			
 Example:      :r .\myfile.sql								
 Use SQLCMD syntax to reference a variable in the post-deployment script.		
 Example:      :setvar TableName MyTable							
               SELECT * FROM [$(TableName)]					
--------------------------------------------------------------------------------------
*/
-- New setup needed for the first time
IF (SELECT [ClubName] FROM [dbo].[AppSettings]) IS NULL
BEGIN
	INSERT INTO [dbo].[AppSettings]
		([ClubName]
		,[SportlinkApiUrl]
		,[SportlinkClientId]
		,[SeasonStartMonth]
		,[FetchSchedule])
	VALUES
		('Uw clubnaam'
		,'https://data.sportlink.com'
		,'APIKEY'
		,7
		,'0 0 4 * * *')
END
GO

-- Speeltijden: insert static reference data once
IF NOT EXISTS (SELECT 1 FROM [dbo].[Speeltijden])
BEGIN
    INSERT INTO [dbo].[Speeltijden] ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust])
    VALUES
        ('JO7',  0.25, 50,  20, 10),
        ('JO8',  0.25, 50,  20, 10),
        ('JO9',  0.25, 50,  20, 10),
        ('JO10', 0.25, 65,  25, 15),
        ('JO11', 0.50, 75,  30, 15),
        ('JO12', 0.50, 75,  30, 15),
        ('JO13', 1.00, 75,  30, 15),
        ('JO14', 1.00, 85,  35, 15),
        ('JO15', 1.00, 85,  35, 15),
        ('JO16', 1.00, 95,  40, 15),
        ('JO17', 1.00, 95,  40, 15),
        ('JO18', 1.00, 105, 45, 15),
        ('JO19', 1.00, 105, 45, 15),
        ('JO23', 1.00, 105, 45, 15),
        ('MO13', 1.00, 75,  30, 15),
        ('MO15', 1.00, 85,  35, 15),
        ('MO17', 1.00, 95,  40, 15),
        ('MO19', 1.00, 105, 45, 15),
        ('MO20', 1.00, 105, 45, 15),
        ('VR',   1.00, 105, 45, 15),
        ('G',    0.50, 75,  30, 15),
        ('1-99', 1.00, 105, 45, 15)
END
GO

-- Velden: field definitions
IF NOT EXISTS (SELECT 1 FROM [dbo].[Velden])
BEGIN
    INSERT INTO [dbo].[Velden] ([VeldNummer], [VeldNaam], [VeldType], [HeeftKunstlicht], [Actief])
    VALUES
        (1, 'veld 1', 'kunstgras', 1, 1),
        (2, 'veld 2', 'kunstgras', 1, 1),
        (3, 'veld 3', 'kunstgras', 1, 1),
        (4, 'veld 4', 'kunstgras', 1, 1),
        (5, 'veld 5', 'natuurgras', 0, 1),
        (6, 'veld 6', 'natuurgras', 0, 0)  -- niet functioneel
END
GO

-- VeldBeschikbaarheid: field availability per day-of-week
-- DagVanWeek: 1=Monday, 2=Tuesday, ..., 6=Saturday, 7=Sunday
IF NOT EXISTS (SELECT 1 FROM [dbo].[VeldBeschikbaarheid])
BEGIN
    -- Monday-Thursday (1-4): only veld 5, until sunset
    INSERT INTO [dbo].[VeldBeschikbaarheid] ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang])
    VALUES
        (5, 1, '18:00', '22:00', 1),
        (5, 2, '18:00', '22:00', 1),
        (5, 3, '18:00', '22:00', 1),
        (5, 4, '18:00', '22:00', 1)
    -- Friday (5): no rows = no matches
    -- Sunday (7): no rows = no matches

    -- Saturday (6): all fields
    INSERT INTO [dbo].[VeldBeschikbaarheid] ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang])
    VALUES
        (1, 6, '08:30', '22:00', 0),
        (2, 6, '08:30', '22:00', 0),
        (3, 6, '08:30', '22:00', 0),
        (4, 6, '08:30', '22:00', 0),
        (5, 6, '08:30', '17:00', 0)
END
GO

-- TeamRegels: team-specific scheduling exceptions
IF NOT EXISTS (SELECT 1 FROM [dbo].[TeamRegels])
BEGIN
    INSERT INTO [dbo].[TeamRegels] ([TeamNaam], [RegelType], [WaardeMinuten], [Prioriteit], [Actief], [Opmerking])
    VALUES
        ('VRC 1', 'BufferVoor', 60, 10, 1, '1 uur voor de wedstrijd geen andere wedstrijden op hetzelfde veld'),
        ('VRC 1', 'BufferNa',   30, 10, 1, '30 min na de wedstrijd geen andere wedstrijden op hetzelfde veld')
END
GO

-- Update the Season and datetable
DECLARE @SeasonStartMonth INT = (SELECT [SeasonStartMonth] FROM [dbo].[AppSettings])
EXEC [dbo].[sp_UpdateSeasonTable] @SeasonStartMonth;
GO