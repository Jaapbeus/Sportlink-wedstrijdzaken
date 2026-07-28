CREATE TABLE [dbo].[VeldTraining] (
    [Id]           INT           IDENTITY(1,1) NOT NULL,
    [VeldNummer]   INT           NOT NULL,
    [DagVanWeek]   INT           NOT NULL,
    [VanTijd]      TIME          NOT NULL,
    [TotTijd]      TIME          NOT NULL,
    [Omschrijving] NVARCHAR(100) NULL,
    [Actief]       BIT           NOT NULL CONSTRAINT [DF_VeldTraining_Actief] DEFAULT 1,
    [ClubCode]     NVARCHAR(20)  NOT NULL,
    CONSTRAINT [PK_VeldTraining] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_VeldTraining_Velden] FOREIGN KEY ([VeldNummer]) REFERENCES [dbo].[Velden]([VeldNummer])
);
