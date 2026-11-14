CREATE TABLE [dbo].[SyncJobPhaseLog] (
    [JobId]       UNIQUEIDENTIFIER NOT NULL,
    [ClubCode]    NVARCHAR(20)     NOT NULL,
    [Attempt]     INT              NOT NULL,
    [Phase]       NVARCHAR(30)     NOT NULL,
    [StartedAt]   DATETIME2        NOT NULL,
    [CompletedAt] DATETIME2        NULL,
    [Status]      NVARCHAR(20)     NOT NULL,
    [Fetched]     INT              NULL,
    [Added]       INT              NULL,
    [Updated]     INT              NULL,
    [Failed]      INT              NULL,
    [Progress]    INT              NULL,
    [Total]       INT              NULL,
    [ReasonCode]  NVARCHAR(40)     NULL,
    CONSTRAINT [PK_SyncJobPhaseLog] PRIMARY KEY CLUSTERED ([JobId], [Attempt], [Phase]),
    CONSTRAINT [FK_SyncJobPhaseLog_SyncJobs] FOREIGN KEY ([JobId]) REFERENCES [dbo].[SyncJobs] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [CK_SyncJobPhaseLog_Attempt] CHECK ([Attempt] > 0),
    CONSTRAINT [CK_SyncJobPhaseLog_Phase] CHECK ([Phase] IN ('teams', 'programma', 'uitslagen', 'matchdetails', 'planner-view', 'canonicalisatie', 'reconciliatie', 'configuratie')),
    CONSTRAINT [CK_SyncJobPhaseLog_Status] CHECK ([Status] IN ('pending', 'running', 'succeeded', 'partial', 'failed', 'skipped')),
    CONSTRAINT [CK_SyncJobPhaseLog_ReasonCode] CHECK ([ReasonCode] IS NULL OR [ReasonCode] IN ('operation_failed', 'partial_failure', 'configuration_missing', 'egress_blocked', 'programme_truncated', 'reconciliation_skipped', 'source_failed')),
    CONSTRAINT [CK_SyncJobPhaseLog_Dates] CHECK ([CompletedAt] IS NULL OR [CompletedAt] >= [StartedAt]),
    CONSTRAINT [CK_SyncJobPhaseLog_Counts] CHECK (([Fetched] IS NULL OR [Fetched] >= 0) AND ([Added] IS NULL OR [Added] >= 0) AND ([Updated] IS NULL OR [Updated] >= 0) AND ([Failed] IS NULL OR [Failed] >= 0) AND ([Progress] IS NULL OR [Progress] >= 0) AND ([Total] IS NULL OR [Total] >= 0) AND ([Progress] IS NULL OR [Total] IS NULL OR [Progress] <= [Total]))
);

CREATE NONCLUSTERED INDEX [IX_SyncJobPhaseLog_ClubCode_StartedAt] ON [dbo].[SyncJobPhaseLog] ([ClubCode], [StartedAt] DESC);
CREATE NONCLUSTERED INDEX [IX_SyncJobPhaseLog_StartedAt] ON [dbo].[SyncJobPhaseLog] ([StartedAt]);
