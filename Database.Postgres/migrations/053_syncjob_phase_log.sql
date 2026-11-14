-- #1912: privacyveilige fasevoortgang van sync-jobs, met 30 dagen bewaartermijn.
-- SQL Server-tegenhanger: dbo.SyncJobPhaseLog.sql en Script.PostDeployment1.sql.
CREATE UNIQUE INDEX IF NOT EXISTS ux_syncjobs_id_clubcode ON public.syncjobs (id, clubcode);
CREATE TABLE IF NOT EXISTS public.syncjobphaselog (
    jobid       UUID         NOT NULL,
    clubcode    VARCHAR(20)  NOT NULL,
    attempt     INT          NOT NULL CHECK (attempt > 0),
    phase       VARCHAR(30)  NOT NULL CHECK (phase IN (
                    'teams', 'programma', 'uitslagen', 'matchdetails',
                    'planner-view', 'canonicalisatie', 'reconciliatie', 'configuratie')),
    startedat   TIMESTAMPTZ  NOT NULL,
    completedat TIMESTAMPTZ  NULL,
    status      VARCHAR(20)  NOT NULL CHECK (status IN (
                    'pending', 'running', 'succeeded', 'partial', 'failed', 'skipped')),
    fetched     INT          NULL CHECK (fetched IS NULL OR fetched >= 0),
    added       INT          NULL CHECK (added IS NULL OR added >= 0),
    updated     INT          NULL CHECK (updated IS NULL OR updated >= 0),
    failed      INT          NULL CHECK (failed IS NULL OR failed >= 0),
    progress    INT          NULL CHECK (progress IS NULL OR progress >= 0),
    total       INT          NULL CHECK (total IS NULL OR total >= 0),
    reasoncode  VARCHAR(40)  NULL CHECK (reasoncode IS NULL OR reasoncode IN (
                    'operation_failed', 'partial_failure', 'configuration_missing', 'egress_blocked',
                    'programme_truncated', 'reconciliation_skipped', 'source_failed', 'not_applicable')),
    CONSTRAINT pk_syncjobphaselog PRIMARY KEY (jobid, attempt, phase),
    CONSTRAINT fk_syncjobphaselog_job FOREIGN KEY (jobid, clubcode) REFERENCES public.syncjobs (id, clubcode) ON DELETE CASCADE,
    CONSTRAINT ck_syncjobphaselog_dates CHECK (completedat IS NULL OR completedat >= startedat),
    CONSTRAINT ck_syncjobphaselog_progress CHECK (progress IS NULL OR total IS NULL OR progress <= total)
);

CREATE INDEX IF NOT EXISTS ix_syncjobphaselog_clubcode_startedat
    ON public.syncjobphaselog (clubcode, startedat DESC);
CREATE INDEX IF NOT EXISTS ix_syncjobphaselog_startedat
    ON public.syncjobphaselog (startedat);

-- Defense-in-depth als bij de overige public-tabellen; bewust zonder policies.
ALTER TABLE public.syncjobphaselog ENABLE ROW LEVEL SECURITY;
