CREATE TABLE [avg].[ImportLog] (
    [Id]               INT            IDENTITY (1, 1) NOT NULL,
    [ImportDatum]      DATETIME       CONSTRAINT [DF_avg_ImportLog_ImportDatum] DEFAULT (GETUTCDATE()) NOT NULL,
    [AantalRijen]      INT            NOT NULL,
    [CsvBestand]       NVARCHAR (500) NULL,
    [ImporterendeDoor] NVARCHAR (200) NULL,
    [Duur_ms]          INT            NULL,
    [ClubCode]         NVARCHAR (20)  NOT NULL CONSTRAINT [DF_avg_ImportLog_ClubCode] DEFAULT '',
    CONSTRAINT [PK_avg_ImportLog] PRIMARY KEY CLUSTERED ([Id] ASC)
);
