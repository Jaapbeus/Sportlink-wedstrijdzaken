CREATE TABLE [dbo].[SportlinkMutationAudit] (
    [Id]                      BIGINT IDENTITY(1,1) NOT NULL,
    [ClubCode]                NVARCHAR(20)  NOT NULL,
    [FunctioneleRol]          NVARCHAR(50)  NOT NULL,
    [TriggerdDoor]            NVARCHAR(200) NOT NULL,
    [PublicMatchId]           NVARCHAR(50)  NOT NULL,
    [Actie]                   NVARCHAR(100) NOT NULL,
    [WaardeVoor]              NVARCHAR(MAX) NULL,
    [WaardeNa]                NVARCHAR(MAX) NULL,
    [Resultaat]               NVARCHAR(20)  NOT NULL CONSTRAINT [DF_SportlinkMutationAudit_Resultaat] DEFAULT ('Pending'),
    [FoutmeldingSamenvatting] NVARCHAR(500) NULL,
    [CorrelationId]           NVARCHAR(50)  NULL,
    [Tijdstip]                DATETIME2     NOT NULL CONSTRAINT [DF_SportlinkMutationAudit_Tijdstip] DEFAULT (GETUTCDATE()),
    CONSTRAINT [PK_SportlinkMutationAudit] PRIMARY KEY CLUSTERED ([Id] ASC)
);
GO
CREATE NONCLUSTERED INDEX [IX_SportlinkMutationAudit_ClubCode_Tijdstip] ON [dbo].[SportlinkMutationAudit] ([ClubCode], [Tijdstip] DESC);
GO
CREATE NONCLUSTERED INDEX [IX_SportlinkMutationAudit_PublicMatchId] ON [dbo].[SportlinkMutationAudit] ([PublicMatchId]);
GO
