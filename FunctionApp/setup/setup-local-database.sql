-- Setup Local Database for Sportlink Function
-- Run this script on your local SQL Server instance

USE master;
GO

-- Create database if it doesn't exist
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'SportlinkSqlDb')
BEGIN
    CREATE DATABASE SportlinkSqlDb;
    PRINT 'Database SportlinkSqlDb created successfully';
END
ELSE
BEGIN
    PRINT 'Database SportlinkSqlDb already exists';
END
GO

USE SportlinkSqlDb;
GO

-- Create AppSettings table if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[AppSettings]
    (
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        [sportlinkApiUrl] NVARCHAR(500) NOT NULL,
        [sportlinkClientId] NVARCHAR(500) NOT NULL,
        [CreatedDate] DATETIME2 DEFAULT GETDATE(),
        [ModifiedDate] DATETIME2 DEFAULT GETDATE()
    );
    PRINT 'AppSettings table created successfully';
END
ELSE
BEGIN
    PRINT 'AppSettings table already exists';
END
GO

-- Insert default settings for local development (update these with your actual values)
IF NOT EXISTS (SELECT 1 FROM [dbo].[AppSettings])
BEGIN
    INSERT INTO [dbo].[AppSettings] ([sportlinkApiUrl], [sportlinkClientId])
    VALUES 
    (
        'https://api.sportlink.com',  -- Replace with your actual API URL
        'your-client-id-here'         -- Replace with your actual client ID
    );
    PRINT 'Default AppSettings inserted';
END
ELSE
BEGIN
    PRINT 'AppSettings already contains data';
END
GO

-- Create schemas if they don't exist
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'stg')
BEGIN
    EXEC('CREATE SCHEMA [stg]');
    PRINT 'Schema [stg] created successfully';
END
GO

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'his')
BEGIN
    EXEC('CREATE SCHEMA [his]');
    PRINT 'Schema [his] created successfully';
END
GO

-- Create staging tables
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'teams' AND schema_id = SCHEMA_ID('stg'))
BEGIN
    CREATE TABLE [stg].[teams] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [teamcode] NVARCHAR(50) NOT NULL,
        [lokaleteamcode] NVARCHAR(50),
        [poulecode] NVARCHAR(50),
        [teamnaam] NVARCHAR(200),
        [competitienaam] NVARCHAR(200),
        [klasse] NVARCHAR(100),
        [poule] NVARCHAR(100),
        [klassepoule] NVARCHAR(100),
        [spelsoort] NVARCHAR(100),
        [competitiesoort] NVARCHAR(100),
        [geslacht] NVARCHAR(50),
        [teamsoort] NVARCHAR(100),
        [leeftijdscategorie] NVARCHAR(100),
        [kalespelsoort] NVARCHAR(100),
        [speeldag] NVARCHAR(100),
        [speeldagteam] NVARCHAR(100),
        [more] NVARCHAR(MAX),
        [LoadedDate] DATETIME2 DEFAULT GETDATE()
    );
    PRINT 'Table [stg].[teams] created successfully';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'matches' AND schema_id = SCHEMA_ID('stg'))
BEGIN
    CREATE TABLE [stg].[matches] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [wedstrijddatum] DATETIME2,
        [wedstrijdcode] NVARCHAR(50) NOT NULL,
        [wedstrijdnummer] NVARCHAR(50),
        [datum] NVARCHAR(50),
        [wedstrijd] NVARCHAR(200),
        [datumopgemaakt] NVARCHAR(50),
        [accommodatie] NVARCHAR(200),
        [aanvangstijd] NVARCHAR(50),
        [thuisteam] NVARCHAR(200),
        [thuisteamid] NVARCHAR(50),
        [thuisteamclubrelatiecode] NVARCHAR(50),
        [uitteamclubrelatiecode] NVARCHAR(50),
        [uitteam] NVARCHAR(200),
        [uitteamid] NVARCHAR(50),
        [uitslag] NVARCHAR(50),
        [uitslag-regulier] NVARCHAR(50),
        [uitslag-nv] NVARCHAR(50),
        [uitslag-s] NVARCHAR(50),
        [competitienaam] NVARCHAR(200),
        [competitiesoort] NVARCHAR(100),
        [eigenteam] NVARCHAR(100),
        [sportomschrijving] NVARCHAR(200),
        [verenigingswedstrijd] NVARCHAR(100),
        [status] NVARCHAR(50),
        [LoadedDate] DATETIME2 DEFAULT GETDATE()
    );
    PRINT 'Table [stg].[matches] created successfully';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'matchdetails' AND schema_id = SCHEMA_ID('stg'))
BEGIN
    CREATE TABLE [stg].[matchdetails] (
        [id] INT IDENTITY(1,1) PRIMARY KEY,
        [WedstrijdCode] NVARCHAR(50) NOT NULL,
        [InternCode] NVARCHAR(50),
        [VeldNaam] NVARCHAR(200),
        [VeldLocatie] NVARCHAR(200),
        [VertrekTijd] NVARCHAR(50),
        [Rijder] NVARCHAR(200),
        [ThuisScore] NVARCHAR(50),
        [ThuisScoreRegulier] NVARCHAR(50),
        [ThuisScoreNV] NVARCHAR(50),
        [ThuisScoreS] NVARCHAR(50),
        [UitScore] NVARCHAR(50),
        [UitScoreRegulier] NVARCHAR(50),
        [UitScoreNV] NVARCHAR(50),
        [UitScoreS] NVARCHAR(50),
        [Klasse] NVARCHAR(100),
        [WedstrijdType] NVARCHAR(100),
        [CompetitieType] NVARCHAR(100),
        [Categorie] NVARCHAR(100),
        [MatchDateTime] NVARCHAR(50),
        [MatchDate] NVARCHAR(50),
        [Aanvangstijd] NVARCHAR(50),
        [Duration] NVARCHAR(50),
        [SpelType] NVARCHAR(100),
        [Aanduiding] NVARCHAR(100),
        [PouleCode] NVARCHAR(50),
        [Poule] NVARCHAR(100),
        [ThuisTeamID] NVARCHAR(50),
        [ThuisTeam] NVARCHAR(200),
        [UitTeamID] NVARCHAR(50),
        [UitTeam] NVARCHAR(200),
        [Opmerkingen] NVARCHAR(MAX),
        [VerenigingScheidsrechterCode] NVARCHAR(50),
        [VerenigingScheidsrechter] NVARCHAR(200),
        [OverigeOfficialCode] NVARCHAR(50),
        [OverigeOfficial] NVARCHAR(200),
        [Scheidsrechters] NVARCHAR(500),
        [KleedkamerThuis] NVARCHAR(100),
        [KleedkamerUit] NVARCHAR(100),
        [KleedkamerOfficial] NVARCHAR(100),
        [AccommodatieNaam] NVARCHAR(200),
        [AccommodatieStraat] NVARCHAR(200),
        [AccommodatiePlaats] NVARCHAR(200),
        [AccommodatieTelefoon] NVARCHAR(50),
        [AccommodatieRouteplanner] NVARCHAR(500),
        [ThuisTeamNaam] NVARCHAR(200),
        [ThuisTeamCode] NVARCHAR(50),
        [ThuisTeamWebsite] NVARCHAR(500),
        [ThuisTeamShirtKleur] NVARCHAR(100),
        [ThuisTeamStraat] NVARCHAR(200),
        [ThuisTeamPostcodePlaats] NVARCHAR(200),
        [ThuisTeamTelefoon] NVARCHAR(50),
        [ThuisTeamEmail] NVARCHAR(200),
        [UitTeamNaam] NVARCHAR(200),
        [UitTeamCode] NVARCHAR(50),
        [UitTeamWebsite] NVARCHAR(500),
        [UitTeamShirtKleur] NVARCHAR(100),
        [UitTeamStraat] NVARCHAR(200),
        [UitTeamPostcodePlaats] NVARCHAR(200),
        [UitTeamTelefoon] NVARCHAR(50),
        [UitTeamEmail] NVARCHAR(200),
        [LoadedDate] DATETIME2 DEFAULT GETDATE()
    );
    PRINT 'Table [stg].[matchdetails] created successfully';
END
GO

PRINT '';
PRINT '========================================';
PRINT 'Local Database Setup Complete!';
PRINT '========================================';
PRINT '';
PRINT 'Next Steps:';
PRINT '1. Update the AppSettings table with your actual API URL and Client ID';
PRINT '2. Run setup-local-debug.ps1 to verify the environment';
PRINT '3. Start debugging in Visual Studio';
PRINT '';
