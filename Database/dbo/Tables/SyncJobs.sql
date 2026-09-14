CREATE TABLE [dbo].[SyncJobs] (
    [Id]             UNIQUEIDENTIFIER NOT NULL,
    [ClubCode]       NVARCHAR(20)     NOT NULL,
    [Status]         NVARCHAR(20)     NOT NULL CONSTRAINT [DF_SyncJobs_Status] DEFAULT ('pending'),
    [WeekOffsetFrom] INT              NOT NULL,
    [WeekOffsetTo]   INT              NOT NULL,
    [CreatedAt]      DATETIME2        NOT NULL CONSTRAINT [DF_SyncJobs_CreatedAt] DEFAULT (GETUTCDATE()),
    [StartedAt]      DATETIME2        NULL,
    [CompletedAt]    DATETIME2        NULL,
    [ErrorMessage]   NVARCHAR(1000)   NULL,
    CONSTRAINT [PK_SyncJobs] PRIMARY KEY CLUSTERED ([Id] ASC)
);
GO
CREATE NONCLUSTERED INDEX [IX_SyncJobs_ClubCode_CreatedAt] ON [dbo].[SyncJobs] ([ClubCode], [CreatedAt] DESC);
GO
