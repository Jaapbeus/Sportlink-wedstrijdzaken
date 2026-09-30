-- #1411: credentials are authenticated-encrypted by the FunctionApp before storage.
CREATE TABLE IF NOT EXISTS public.sportlinkautologin (
    clubcode             VARCHAR(20) NOT NULL,
    rolnaam              VARCHAR(50) NOT NULL,
    credentialsencrypted TEXT NULL,
    refreshencrypted     TEXT NULL,
    lastloginutc         TIMESTAMPTZ NULL,
    retryafterutc        TIMESTAMPTZ NULL,
    lasterror            VARCHAR(64) NULL,
    failurecount         INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (clubcode, rolnaam)
);

ALTER TABLE public.sportlinkautologin ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.sportlinkautologin FROM PUBLIC;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
        EXECUTE 'REVOKE ALL ON TABLE public.sportlinkautologin FROM anon';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        EXECUTE 'REVOKE ALL ON TABLE public.sportlinkautologin FROM authenticated';
    END IF;
END;
$$;
