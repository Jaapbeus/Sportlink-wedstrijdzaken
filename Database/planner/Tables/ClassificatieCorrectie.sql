CREATE TABLE [planner].[ClassificatieCorrectie] (
    [Id]                        INT             IDENTITY(1,1)   NOT NULL,
    [OrigineleVerwerkingId]     INT                             NULL,   -- NULL bij herkomst Admin (#1568 deel C)
    [CorrectionVerwerkingId]    INT                             NULL,   -- NULL bij herkomst Admin (#1568 deel C)
    [OrigineelVerzoekType]      NVARCHAR(50)                    NOT NULL,
    [AfgeleidJuistType]         NVARCHAR(50)                    NULL,
    [OrigineleSamenvatting]     NVARCHAR(500)                   NULL,
    [CorrectieSamenvatting]     NVARCHAR(500)                   NULL,
    [IsGevalideerd]             BIT                             NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_IsGevalideerd] DEFAULT 0,
    [IsAfgewezen]               BIT                             NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_IsAfgewezen] DEFAULT 0,
    [ClubCode]                  NVARCHAR(20)                    NOT NULL,
    [mta_inserted]              DATETIME        NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_Ins] DEFAULT GETUTCDATE(),
    [mta_modified]              DATETIME        NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_Mod] DEFAULT GETUTCDATE(),
    -- #1568 deel C: 'Reply' (AI-herkende reply-correctie, bestaand) of 'Admin' (beheerder, permanent: de cleanup
    -- raakt alleen 'Reply'). HerkomstVerwerkingId is een los getal zonder FK, zodat de retentie-DELETE van
    -- [planner].[EmailVerwerking] (#424) een admin-leermoment nooit blokkeert of meeneemt.
    [Herkomst]                  NVARCHAR(10)    NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_Herkomst] DEFAULT N'Reply'
        CONSTRAINT [CK_ClassificatieCorrectie_Herkomst] CHECK ([Herkomst] IN (N'Reply', N'Admin')),
    [AangemaaktDoor]            NVARCHAR(64)    NULL,
    [AangemaaktDoorNaam]        NVARCHAR(100)   NULL,
    [AangemaaktOp]              DATETIME2       NULL,
    [HerkomstVerwerkingId]      INT             NULL,
    CONSTRAINT [PK_ClassificatieCorrectie] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_ClassificatieCorrectie_Origineel]  FOREIGN KEY ([OrigineleVerwerkingId])  REFERENCES [planner].[EmailVerwerking]([Id]),
    CONSTRAINT [FK_ClassificatieCorrectie_Correctie]  FOREIGN KEY ([CorrectionVerwerkingId]) REFERENCES [planner].[EmailVerwerking]([Id]),
    CONSTRAINT [CK_ClassificatieCorrectie_ReplyPaar] CHECK ([Herkomst] <> N'Reply' OR ([OrigineleVerwerkingId] IS NOT NULL AND [CorrectionVerwerkingId] IS NOT NULL))
);
GO

-- Eén leermoment per (origineel, correctie)-paar (#715). Sinds de idempotentiefix van #712 wordt een niet-afgeronde
-- verwerking op dezelfde rij hervat, waardoor de correctiedetectie meerdere keren kan draaien voor hetzelfde paar;
-- zonder deze uniciteit levert dat identieke leermomenten op die de beheerder allemaal apart moet valideren en die
-- samen zwaarder in de AI-prompt wegen. Sinds #1568 deel C een gefilterde unique index in plaats van een
-- UNIQUE-constraint: SQL Server telt NULL's als gelijk, dus twee admin-leermomenten (beide NULL, NULL) zouden
-- elkaar anders blokkeren.
CREATE UNIQUE NONCLUSTERED INDEX [UX_ClassificatieCorrectie_Paar]
    ON [planner].[ClassificatieCorrectie] ([OrigineleVerwerkingId], [CorrectionVerwerkingId])
    WHERE [OrigineleVerwerkingId] IS NOT NULL AND [CorrectionVerwerkingId] IS NOT NULL;
