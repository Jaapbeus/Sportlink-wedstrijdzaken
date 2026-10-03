-- #764/#1478: inzagelog van het feedbackoverzicht — wie bekeek welke melding, wanneer. Bewust zonder
-- foreign key (het log verloopt volgens zijn eigen termijn, 24 maanden) en zonder meldingsinhoud.
CREATE TABLE [avg].[FeedbackInzageLog] (
    [Id]                 INT              IDENTITY (1, 1) NOT NULL,
    [ClubCode]           NVARCHAR (20)    NOT NULL,
    [InzienDoorObjectId] NVARCHAR (64)    NULL,
    [InzienDoorNaam]     NVARCHAR (200)   NULL,
    [Actie]              NVARCHAR (20)    NOT NULL,
    [FeedbackId]         UNIQUEIDENTIFIER NULL,
    [Filter]             NVARCHAR (300)   NULL,
    [mta_inserted]       DATETIME2        NOT NULL CONSTRAINT [DF_avg_FeedbackInzageLog_mta_inserted] DEFAULT (GETUTCDATE()),
    CONSTRAINT [PK_avg_FeedbackInzageLog] PRIMARY KEY CLUSTERED ([Id] ASC)
);
GO
CREATE NONCLUSTERED INDEX [IX_avg_FeedbackInzageLog_Club_Datum] ON [avg].[FeedbackInzageLog] ([ClubCode] ASC, [mta_inserted] DESC);
GO
