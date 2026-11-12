-- #1799: NULL is legacy unknown; only new inserts start at NietGestart.
ALTER TABLE planner.emailverwerking
    ADD COLUMN IF NOT EXISTS teamcontactdoorstuurstatus VARCHAR(20) NULL;
