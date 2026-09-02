CREATE TABLE [dbo].[VeldBeschikbaarheid] (
    [Id]                   INT  IDENTITY(1,1) NOT NULL,
    [VeldNummer]           INT                NOT NULL,
    [DagVanWeek]           INT                NOT NULL,
    [BeschikbaarVanaf]     TIME               NOT NULL,
    [BeschikbaarTot]       TIME               NOT NULL,
    [GebruikZonsondergang] BIT                NOT NULL CONSTRAINT [DF_VeldBeschikbaarheid_Zon] DEFAULT 0,
    -- #581: NULL = standaardregime (huidig gedrag, geldt buiten elke actieve periode).
    -- Een waarde koppelt deze rij aan een VeldPeriode (bijv. "Zomerstop") — geldt dan uitsluitend
    -- terwijl die periode actief is, in plaats van het hele jaar.
    [PeriodeId]            INT                NULL,
    [ClubCode]             NVARCHAR(20)       NOT NULL,
    CONSTRAINT [PK_VeldBeschikbaarheid] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_VeldBeschikbaarheid_Velden] FOREIGN KEY ([VeldNummer]) REFERENCES [dbo].[Velden]([VeldNummer]),
    CONSTRAINT [FK_VeldBeschikbaarheid_VeldPeriode] FOREIGN KEY ([PeriodeId]) REFERENCES [dbo].[VeldPeriode]([Id])
);
