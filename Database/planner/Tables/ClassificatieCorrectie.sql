CREATE TABLE [planner].[ClassificatieCorrectie] (
    [Id]                        INT             IDENTITY(1,1)   NOT NULL,
    [OrigineleVerwerkingId]     INT                             NOT NULL,
    [CorrectionVerwerkingId]    INT                             NOT NULL,
    [OrigineelVerzoekType]      NVARCHAR(50)                    NOT NULL,
    [AfgeleidJuistType]         NVARCHAR(50)                    NULL,
    [OrigineleSamenvatting]     NVARCHAR(500)                   NULL,
    [CorrectieSamenvatting]     NVARCHAR(500)                   NULL,
    [IsGevalideerd]             BIT                             NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_IsGevalideerd] DEFAULT 0,
    [IsAfgewezen]               BIT                             NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_IsAfgewezen] DEFAULT 0,
    [ClubCode]                  NVARCHAR(20)                    NOT NULL,
    [mta_inserted]              DATETIME        NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_Ins] DEFAULT GETUTCDATE(),
    [mta_modified]              DATETIME        NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_Mod] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_ClassificatieCorrectie] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_ClassificatieCorrectie_Origineel]  FOREIGN KEY ([OrigineleVerwerkingId])  REFERENCES [planner].[EmailVerwerking]([Id]),
    CONSTRAINT [FK_ClassificatieCorrectie_Correctie]  FOREIGN KEY ([CorrectionVerwerkingId]) REFERENCES [planner].[EmailVerwerking]([Id]),
    -- Eén leermoment per (origineel, correctie)-paar (#715). Sinds de idempotentiefix van #712 wordt
    -- een niet-afgeronde verwerking op dezelfde rij hervat, waardoor de correctiedetectie meerdere
    -- keren kan draaien voor hetzelfde paar. Zonder deze uniciteit levert dat bij drie toegestane
    -- pogingen tot drie identieke leermomenten op, die de beheerder allemaal apart moet valideren —
    -- en meervoudig goedgekeurd weegt hetzelfde voorbeeld onbedoeld zwaarder in de AI-prompt.
    CONSTRAINT [UQ_ClassificatieCorrectie_Paar] UNIQUE ([OrigineleVerwerkingId], [CorrectionVerwerkingId])
);
