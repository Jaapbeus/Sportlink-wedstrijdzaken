-- Aliassen/leermomenten voor teamnaam-schrijfwijzen die niet exact overeenkomen met
-- dbo.Teams.Teamnaam (#692, #696) — bijv. tegenstander-notaties of afwijkende
-- e-mailschrijfwijzen. Zelfde status-workflow als planner.ClassificatieCorrectie:
-- alleen 'validated' aliassen tellen mee als vertrouwde exacte match (geen
-- zelfversterkende foutcirkel bij een foutieve AI-disambiguatie of typefout).
CREATE TABLE [dbo].[TeamAliassen] (
    [Id]                        INT IDENTITY(1,1) NOT NULL,
    [ClubCode]                  NVARCHAR(20)  NOT NULL,
    [RuweTekst]                 NVARCHAR(200) NOT NULL,   -- exact zoals aangetroffen, bijv. "13-1" of "J013 1"
    [RuweTekstGenormaliseerd]   NVARCHAR(200) NOT NULL,   -- na TeamNaamNormalisatie, voor snelle lookup
    [TeamId]                    INT NOT NULL,
    [Bron]                      NVARCHAR(20)  NOT NULL,   -- 'Sync' | 'AiDisambiguatie' | 'CoordinatorCorrectie'
    [Status]                    NVARCHAR(20)  NOT NULL CONSTRAINT [DF_TeamAliassen_Status] DEFAULT 'pending', -- pending|validated|rejected
    [AantalKeerGebruikt]        INT NOT NULL CONSTRAINT [DF_TeamAliassen_AantalKeerGebruikt] DEFAULT 0,
    [mta_inserted]              DATETIME NOT NULL CONSTRAINT [DF_TeamAliassen_Inserted] DEFAULT GETUTCDATE(),
    [mta_modified]              DATETIME NOT NULL CONSTRAINT [DF_TeamAliassen_Modified] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_TeamAliassen] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_TeamAliassen_Teams] FOREIGN KEY ([TeamId]) REFERENCES [dbo].[Teams]([TeamId]),
    CONSTRAINT [UQ_TeamAliassen_Club_Genormaliseerd] UNIQUE ([ClubCode], [RuweTekstGenormaliseerd])
);
