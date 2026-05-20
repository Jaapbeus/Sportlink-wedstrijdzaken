CREATE TABLE [dbo].[UitgeslotenEmailAdressen] (
    [Id]           INT           IDENTITY (1, 1) NOT NULL,
    [EmailAdres]   NVARCHAR (200) NOT NULL,
    [Omschrijving] NVARCHAR (500) NULL,
    [Actief]       BIT            NOT NULL CONSTRAINT [DF_UitgeslotenEmailAdressen_Actief]    DEFAULT 1,
    [ClubCode]     NVARCHAR (20)  NOT NULL CONSTRAINT [DF_UitgeslotenEmailAdressen_ClubCode]  DEFAULT 'VRC', -- migratie-backwards-compat; inserts geven altijd expliciet ClubCode mee
    [mta_inserted] DATETIME2 (7)  NOT NULL CONSTRAINT [DF_UitgeslotenEmailAdressen_Inserted] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_UitgeslotenEmailAdressen] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UQ_UitgeslotenEmailAdressen_Adres] UNIQUE ([EmailAdres], [ClubCode])
);
