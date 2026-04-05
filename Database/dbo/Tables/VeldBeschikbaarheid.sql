CREATE TABLE [dbo].[VeldBeschikbaarheid] (
    [Id]                   INT  IDENTITY(1,1) NOT NULL,
    [VeldNummer]           INT                NOT NULL,
    [DagVanWeek]           INT                NOT NULL,
    [BeschikbaarVanaf]     TIME               NOT NULL,
    [BeschikbaarTot]       TIME               NOT NULL,
    [GebruikZonsondergang] BIT                NOT NULL CONSTRAINT [DF_VeldBeschikbaarheid_Zon] DEFAULT 0,
    CONSTRAINT [PK_VeldBeschikbaarheid] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_VeldBeschikbaarheid_Velden] FOREIGN KEY ([VeldNummer]) REFERENCES [dbo].[Velden]([VeldNummer])
);
