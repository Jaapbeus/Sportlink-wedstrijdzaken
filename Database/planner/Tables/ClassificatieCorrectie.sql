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
    CONSTRAINT [FK_ClassificatieCorrectie_Correctie]  FOREIGN KEY ([CorrectionVerwerkingId]) REFERENCES [planner].[EmailVerwerking]([Id])
);
