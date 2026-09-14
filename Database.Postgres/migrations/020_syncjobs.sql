-- Durable sync-job status (#1138). AdminSyncTrigger start niet langer fire-and-forget via
-- Task.Run, maar zet een rij hier vóór het bericht op de Storage Queue "sync-jobs" komt. De
-- queue-consumer (SyncJobProcessor) werkt status bij; AdminSyncStatus leest deze tabel uit zodat
-- de GUI de voortgang van een specifieke job kan pollen in plaats van alleen lastsynctimestamp.
CREATE TABLE IF NOT EXISTS public.syncjobs (
    id             UUID         PRIMARY KEY,
    clubcode       VARCHAR(20)  NOT NULL,
    status         VARCHAR(20)  NOT NULL DEFAULT 'pending',
    weekoffsetfrom INT          NOT NULL,
    weekoffsetto   INT          NOT NULL,
    createdat      TIMESTAMPTZ  NOT NULL DEFAULT now(),
    startedat      TIMESTAMPTZ  NULL,
    completedat    TIMESTAMPTZ  NULL,
    errormessage   VARCHAR(1000) NULL
);

CREATE INDEX IF NOT EXISTS ix_syncjobs_clubcode_createdat ON public.syncjobs (clubcode, createdat DESC);
