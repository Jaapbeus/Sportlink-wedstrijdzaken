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
		,[SeasonStartMonth])
	VALUES
		('Uw clubnaam'
		,'https://data.sportlink.com'
		,'APIKEY'
		,7)
END
GO

-- Speeltijden: insert static reference data once
IF NOT EXISTS (SELECT 1 FROM [dbo].[Speeltijden])
BEGIN
    INSERT INTO [dbo].[Speeltijden] ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust])
    VALUES
        ('JO7',  0.13, 50,  20, 10),
        ('JO8',  1.00, 50,  20, 10),
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
        ('1-99', 1.00, 105, 45, 15)
END
GO

-- Update the Season and datetable
DECLARE @SeasonStartMonth INT = (SELECT [SeasonStartMonth] FROM [dbo].[AppSettings])
EXEC [dbo].[sp_UpdateSeasonTable] @SeasonStartMonth;
GO