-- #1816: exact native planner/fixture predicate, with the required UPPER(status) expression index.
-- Dynamic his.matches can be absent before the first sync. GenerateHisTable provisions the same
-- index on first creation and subsequent ensures; this migration backfills installed tables.
-- Additive and non-unique; existing code and data remain compatible. Standard CREATE INDEX takes
-- its normal write-blocking lock during creation; CONCURRENTLY is unavailable in a migration transaction.
-- No new table or policy: existing RLS remains unchanged. SQL Server uses raw status comparison.
DO $$
BEGIN
    IF to_regclass('his.matches') IS NOT NULL THEN
        CREATE INDEX IF NOT EXISTS ix_matches_club_status_upper_live
            ON his.matches (clubcode, UPPER(status))
            WHERE mta_deleted IS NULL AND UPPER(status) <> 'AFGELAST';
    END IF;
END $$;
