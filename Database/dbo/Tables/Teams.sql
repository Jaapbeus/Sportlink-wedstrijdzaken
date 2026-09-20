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
GO

-- #1280: sargability van de UPPER()-lookup in TeamCandidateRepository.FindExactTeamAsync.
--
-- Die query vergelijkt `UPPER([TeamnaamGenormaliseerd]) = UPPER(@sleutel)` (#820, bewust — zie het
-- klassecommentaar daar). UQ_Teams_Club_Genormaliseerd ligt op de KALE kolom en kan zo'n predicaat
-- niet bedienen: SQL Server verwijdert een overbodige UPPER() niet, ook niet onder de
-- case-insensitieve modelcollatie (1033, CI). De vergelijking belandt dan als residueel predicaat
-- ná de seek, of de seek vervalt helemaal.
--
-- Een persisted computed column is op SQL Server de tegenhanger van Postgres' expressie-index
-- (Database.Postgres/migrations/007_teams_collation_fix.sql: ux_teams_club_teamnaamgenormaliseerd_upper).
-- SQL Server matcht de expressie UPPER(kolom) uit de query automatisch tegen deze kolom, dus de
-- querytekst hoeft niet te wijzigen en blijft gelijk aan die van de Postgres-tier.
--
-- Bewust NIET uniek: UQ_Teams_Club_Genormaliseerd blijft de integriteitsgrens. Een UNIQUE index op
-- de uppercase-vorm zou op een installatie met een case-SENSITIEVE collatie kunnen falen bij
-- aanmaak (twee rijen die alleen in casing verschillen zijn daar vandaag toegestaan) en zo de
-- deploy breken — zie docs/ARCHITECTUUR-DATABASE-TIERS.md §75.
ALTER TABLE [dbo].[Teams]
    ADD [TeamnaamGenormaliseerdUpper] AS UPPER([TeamnaamGenormaliseerd]) PERSISTED;
GO

CREATE NONCLUSTERED INDEX [IX_Teams_Club_GenormaliseerdUpper]
    ON [dbo].[Teams] ([ClubCode], [TeamnaamGenormaliseerdUpper])
    INCLUDE ([Teamnaam], [LeeftijdsCategorie], [IsActief]);
