-- #1701: inzagelog van de contactgegevens van teambegeleiding — wie (Entra object-ID + naam-momentopname)
-- vroeg wanneer de begeleiders van welk team op, en hoeveel kaarten dat opleverde. Bewust zonder gegevens
-- van de ingeziene begeleiders en zonder foreign key (het log verloopt volgens zijn eigen termijn, 12 maanden).
CREATE TABLE [avg].[TeambegeleidingInzageLog] (
    [Id]                 INT            IDENTITY (1, 1) NOT NULL,
    [ClubCode]           NVARCHAR (20)  NOT NULL,
    [InzienDoorObjectId] NVARCHAR (64)  NULL,
    [InzienDoorNaam]     NVARCHAR (200) NULL,
    [Team]               NVARCHAR (100) NOT NULL,
    [AantalRijen]        INT            NOT NULL,
    [mta_inserted]       DATETIME2      NOT NULL CONSTRAINT [DF_avg_TeambegeleidingInzageLog_mta_inserted] DEFAULT (GETUTCDATE()),
    CONSTRAINT [PK_avg_TeambegeleidingInzageLog] PRIMARY KEY CLUSTERED ([Id] ASC)
);
GO
CREATE NONCLUSTERED INDEX [IX_avg_TeambegeleidingInzageLog_Club_Datum] ON [avg].[TeambegeleidingInzageLog] ([ClubCode] ASC, [mta_inserted] DESC);
GO
