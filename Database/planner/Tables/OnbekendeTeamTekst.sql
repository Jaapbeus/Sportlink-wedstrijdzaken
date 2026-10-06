-- #1568 deel C: wachtrij met teamteksten die de e-mailpipeline niet kon koppelen aan een eigen team.
-- Postgres-tegenhanger: planner.onbekendeteamtekst (Database.Postgres/migrations/039_leren_van_de_trace.sql).
-- Bewust GEEN foreign key naar [planner].[EmailVerwerking]: [LaatsteVerwerkingId] is alleen een aanwijzing
-- en die verwerking wordt na 30 dagen geanonimiseerd en na 90 dagen verwijderd.
CREATE TABLE [planner].[OnbekendeTeamTekst] (
    [Id]                      INT            IDENTITY (1, 1) NOT NULL,
    [ClubCode]                NVARCHAR (20)  NOT NULL CONSTRAINT [CK_OnbekendeTeamTekst_ClubCode] CHECK (LEN([ClubCode]) > 0),
    [RuweTekstGenormaliseerd] NVARCHAR (200) NOT NULL,
    [VoorbeeldTekst]          NVARCHAR (80)  NOT NULL,
    [Aantal]                  INT            NOT NULL CONSTRAINT [DF_OnbekendeTeamTekst_Aantal] DEFAULT (1),
    [EerstGezien]             DATETIME2      NOT NULL CONSTRAINT [DF_OnbekendeTeamTekst_EerstGezien] DEFAULT (GETUTCDATE()),
    [LaatstGezien]            DATETIME2      NOT NULL CONSTRAINT [DF_OnbekendeTeamTekst_LaatstGezien] DEFAULT (GETUTCDATE()),
    [LaatsteVerwerkingId]     INT            NULL,
    [Status]                  NVARCHAR (12)  NOT NULL CONSTRAINT [DF_OnbekendeTeamTekst_Status] DEFAULT (N'open')
        CONSTRAINT [CK_OnbekendeTeamTekst_Status] CHECK ([Status] IN (N'open', N'afgehandeld', N'genegeerd')),
    CONSTRAINT [PK_OnbekendeTeamTekst] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UQ_OnbekendeTeamTekst_Club_Sleutel] UNIQUE ([ClubCode], [RuweTekstGenormaliseerd])
);
GO

CREATE NONCLUSTERED INDEX [IX_OnbekendeTeamTekst_Club_Status]
    ON [planner].[OnbekendeTeamTekst] ([ClubCode], [Status], [LaatstGezien] DESC);
