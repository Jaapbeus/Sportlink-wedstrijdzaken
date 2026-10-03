-- #764/#1476: feedbackmeldingen van alle gebruikers. Postgres-tegenhanger: avg.feedback
-- (Database.Postgres/migrations/035_avg_feedback.sql).
-- AVG: MelderObjectId (Entra object-ID, pseudoniem) en MelderNaam (momentopname) gaan op NULL via
-- avg.sp_CleanupFeedback, 24 maanden na sluiting van het GitHub-issue. Nooit in het publieke issue.
CREATE TABLE [avg].[Feedback] (
    [Id]                            INT              IDENTITY (1, 1) NOT NULL,
    [FeedbackId]                    UNIQUEIDENTIFIER NOT NULL,
    [ClubCode]                      NVARCHAR (20)    NOT NULL CONSTRAINT [CK_avg_Feedback_ClubCode] CHECK (LEN([ClubCode]) > 0),
    [Type]                          NVARCHAR (20)    NOT NULL,
    [Onderwerp]                     NVARCHAR (200)   NOT NULL,
    [Beschrijving]                  NVARCHAR (MAX)   NOT NULL,
    [VragenAntwoorden]              NVARCHAR (MAX)   NULL,
    [IssueBody]                     NVARCHAR (MAX)   NOT NULL,
    [MelderObjectId]                NVARCHAR (64)    NULL,
    [MelderNaam]                    NVARCHAR (200)   NULL,
    [MelderRol]                     NVARCHAR (20)    NOT NULL CONSTRAINT [DF_avg_Feedback_MelderRol] DEFAULT ('user'),
    [Pagina]                        NVARCHAR (200)   NULL,
    [AppVersie]                     NVARCHAR (20)    NULL,
    [IssueNummer]                   INT              NULL,
    [IssueUrl]                      NVARCHAR (300)   NULL,
    [Status]                        NVARCHAR (30)    NOT NULL CONSTRAINT [DF_avg_Feedback_Status] DEFAULT ('wacht-op-publicatie'),
    [IssueGeslotenOpUtc]            DATETIME2        NULL,
    [IssueStatusGecontroleerdOpUtc] DATETIME2        NULL,
    [IsGeanonimiseerd]              BIT              NOT NULL CONSTRAINT [DF_avg_Feedback_IsGeanonimiseerd] DEFAULT (0),
    [GeanonimiseerdOpUtc]           DATETIME2        NULL,
    [mta_inserted]                  DATETIME2        NOT NULL CONSTRAINT [DF_avg_Feedback_mta_inserted] DEFAULT (GETUTCDATE()),
    [mta_modified]                  DATETIME2        NOT NULL CONSTRAINT [DF_avg_Feedback_mta_modified] DEFAULT (GETUTCDATE()),
    CONSTRAINT [PK_avg_Feedback] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UQ_avg_Feedback_FeedbackId] UNIQUE NONCLUSTERED ([FeedbackId])
);
GO
CREATE NONCLUSTERED INDEX [IX_avg_Feedback_Club_Datum]
    ON [avg].[Feedback] ([ClubCode] ASC, [mta_inserted] DESC);
GO
CREATE NONCLUSTERED INDEX [IX_avg_Feedback_Club_Status]
    ON [avg].[Feedback] ([ClubCode] ASC, [Status] ASC, [mta_inserted] DESC);
GO
CREATE NONCLUSTERED INDEX [IX_avg_Feedback_Melder_Datum]
    ON [avg].[Feedback] ([ClubCode] ASC, [MelderObjectId] ASC, [mta_inserted] DESC)
    WHERE [MelderObjectId] IS NOT NULL;
GO
