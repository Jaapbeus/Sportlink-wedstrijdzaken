-- #764/#1476: technische context bij een feedbackmelding (console-fouten, mislukte aanroepen,
-- navigatiespoor), al geredigeerd. Kortere bewaartermijn (90 dagen, avg.sp_CleanupFeedback).
CREATE TABLE [avg].[FeedbackTelemetrie] (
    [Id]           INT              IDENTITY (1, 1) NOT NULL,
    [FeedbackId]   UNIQUEIDENTIFIER NOT NULL,
    [ClubCode]     NVARCHAR (20)    NOT NULL,
    [Bron]         NVARCHAR (30)    NOT NULL,
    [Payload]      NVARCHAR (MAX)   NULL,
    [mta_inserted] DATETIME2        NOT NULL CONSTRAINT [DF_avg_FeedbackTelemetrie_mta_inserted] DEFAULT (GETUTCDATE()),
    CONSTRAINT [PK_avg_FeedbackTelemetrie] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_avg_FeedbackTelemetrie_Feedback] FOREIGN KEY ([FeedbackId]) REFERENCES [avg].[Feedback] ([FeedbackId]) ON DELETE CASCADE
);
GO
CREATE NONCLUSTERED INDEX [IX_avg_FeedbackTelemetrie_FeedbackId] ON [avg].[FeedbackTelemetrie] ([FeedbackId] ASC);
GO
CREATE NONCLUSTERED INDEX [IX_avg_FeedbackTelemetrie_Inserted] ON [avg].[FeedbackTelemetrie] ([mta_inserted] ASC);
GO
