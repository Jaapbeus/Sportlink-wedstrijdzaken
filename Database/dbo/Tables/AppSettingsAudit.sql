CREATE TABLE [dbo].[AppSettingsAudit] (
    [Id]            INT IDENTITY(1,1) NOT NULL,
    [Tijdstip]      DATETIME2 NOT NULL CONSTRAINT [DF_AppSettingsAudit_Tijdstip] DEFAULT GETUTCDATE(),
    [GewijzigdDoor] NVARCHAR(100) NOT NULL,
    [Veld]          NVARCHAR(100) NOT NULL,
    [OudeWaarde]    NVARCHAR(MAX) NULL,
    [NieuweWaarde]  NVARCHAR(MAX) NULL,
    [ClubCode]      NVARCHAR(20) NOT NULL CONSTRAINT [DF_AppSettingsAudit_ClubCode] DEFAULT 'VRC',
    CONSTRAINT [PK_AppSettingsAudit] PRIMARY KEY CLUSTERED ([Id] ASC)
);
