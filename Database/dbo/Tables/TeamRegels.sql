CREATE TABLE [dbo].[TeamRegels] (
    [Id]                INT            IDENTITY(1,1) NOT NULL,
    [TeamNaam]          NVARCHAR(100)  NOT NULL,
    [RegelType]         NVARCHAR(50)   NOT NULL,
    [WaardeMinuten]     INT            NULL,
    [WaardeVeldNummer]  INT            NULL,
    [WaardeTijd]        TIME           NULL,
    [Prioriteit]        INT            NOT NULL CONSTRAINT [DF_TeamRegels_Prioriteit] DEFAULT 0,
    [Actief]            BIT            NOT NULL CONSTRAINT [DF_TeamRegels_Actief] DEFAULT 1,
    [Opmerking]         NVARCHAR(500)  NULL,
    CONSTRAINT [PK_TeamRegels] PRIMARY KEY CLUSTERED ([Id] ASC)
);
