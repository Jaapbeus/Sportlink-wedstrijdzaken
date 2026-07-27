-- Canonieke teamidentiteit (#692, #696). Bewust in dbo-schema, niet in his.* —
-- his.teams wordt nachtelijks herbouwd zodra Sportlink-staging-velden wijzigen
-- (zie commentaar in Database/his/Tables/Teams.sql), een identiteitstabel mag
-- daar nooit in leven. Vulling gebeurt door een aparte sync-stap (#696, vervolgwerk)
-- die his.teams normaliseert via FunctionApp/TeamResolution/TeamNaamNormalisatie.cs.
CREATE TABLE [dbo].[Teams] (
    [TeamId]             INT IDENTITY(1,1) NOT NULL,
    [ClubCode]           NVARCHAR(20)  NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
    [Teamnaam]           NVARCHAR(100) NOT NULL, -- canonieke schrijfwijze zoals in his.teams.teamnaam
    [TeamnaamGenormaliseerd] NVARCHAR(100) NOT NULL, -- via TeamNaamNormalisatie.NormaliseerVoorVergelijking, voor lookup
    [LeeftijdsCategorie] NVARCHAR(50)  NULL,     -- genormaliseerd (JO13, MO15, VR, ZO, 1-99, ...)
    [LeeftijdNummer]     INT NULL,               -- bijv. 13 uit "JO13-1"; NULL bij VR/ZO zonder leeftijdnummer
    [TeamNummer]         INT NULL,               -- bijv. 1 uit "JO13-1"
    [BkTeams]            NVARCHAR(100) NULL,     -- koppeling naar his.teams.bk_teams (sync-sleutel)
    [IsActief]           BIT NOT NULL CONSTRAINT [DF_Teams_IsActief] DEFAULT 1,
    [mta_inserted]       DATETIME NOT NULL CONSTRAINT [DF_Teams_Inserted] DEFAULT GETUTCDATE(),
    [mta_modified]       DATETIME NOT NULL CONSTRAINT [DF_Teams_Modified] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_Teams] PRIMARY KEY CLUSTERED ([TeamId] ASC),
    CONSTRAINT [UQ_Teams_Club_Teamnaam] UNIQUE ([ClubCode], [Teamnaam]),
    CONSTRAINT [UQ_Teams_Club_Genormaliseerd] UNIQUE ([ClubCode], [TeamnaamGenormaliseerd])
);
