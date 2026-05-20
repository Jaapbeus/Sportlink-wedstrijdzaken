CREATE TABLE [dbo].[EmailTemplateInstellingen] (
    [Id]            INT IDENTITY(1,1) NOT NULL,
    [TemplateKey]   NVARCHAR(100) NOT NULL,   -- bijv. 'beschikbaarheid_vrij', 'herplan_opties'
    [Onderwerp]     NVARCHAR(500) NOT NULL,
    [BodyTemplate]  NVARCHAR(MAX) NOT NULL,
    [Actief]        BIT NOT NULL CONSTRAINT [DF_EmailTemplateInstellingen_Actief] DEFAULT 1,
    [ClubCode]      NVARCHAR(20) NOT NULL CONSTRAINT [DF_EmailTemplateInstellingen_ClubCode] DEFAULT 'VRC',
    [mta_inserted]  DATETIME2 NOT NULL CONSTRAINT [DF_EmailTemplateInstellingen_Inserted] DEFAULT GETUTCDATE(),
    [mta_modified]  DATETIME2 NOT NULL CONSTRAINT [DF_EmailTemplateInstellingen_Modified] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_EmailTemplateInstellingen] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UQ_EmailTemplateInstellingen_Key] UNIQUE ([TemplateKey], [ClubCode])
);
