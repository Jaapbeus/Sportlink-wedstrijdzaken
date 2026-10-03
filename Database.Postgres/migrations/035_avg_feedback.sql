-- 035_avg_feedback.sql — feedbackmeldingen van alle gebruikers, met telemetrie en inzagelog (#764, #1476, #1478).
-- SQL Server-tegenhanger: Database/avg/Tables/Feedback*.sql + Database/Script.PostDeployment1.sql.
--
-- Persoonsgegevens (AVG): MelderObjectId is de Entra object-ID (pseudoniem, art. 4(5)), MelderNaam is
-- de weergavenaam als momentopname op het moment van indienen. Geen e-mailadres. Beide kolommen worden
-- door de retentietimer op NULL gezet 24 maanden nadat het gekoppelde GitHub-issue is gesloten
-- (IsGeanonimiseerd = TRUE); de meldingstekst zelf blijft bewaard. Technische context (feedbacktelemetrie)
-- wordt na 90 dagen gewist, het inzagelog na 24 maanden. Niets hiervan komt in het publieke issue.
--
-- Casing: lowercase, zoals alle Postgres-objecten. Tijden zijn TIMESTAMPTZ (UTC) — zie 002.
-- Row-Level Security staat aan zonder policies (#1198): de FunctionApp-rol omzeilt RLS, Supabase's
-- anon/authenticated-rollen worden buitengesloten.

CREATE SCHEMA IF NOT EXISTS avg;

CREATE TABLE IF NOT EXISTS avg.feedback (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    feedbackid UUID NOT NULL,
    clubcode VARCHAR(20) NOT NULL,
    type VARCHAR(20) NOT NULL,
    onderwerp VARCHAR(200) NOT NULL,
    beschrijving TEXT NOT NULL,
    vragenantwoorden TEXT NULL,
    issuebody TEXT NOT NULL,
    melderobjectid VARCHAR(64) NULL,
    meldernaam VARCHAR(200) NULL,
    melderrol VARCHAR(20) NOT NULL DEFAULT 'user',
    pagina VARCHAR(200) NULL,
    appversie VARCHAR(20) NULL,
    issuenummer INTEGER NULL,
    issueurl VARCHAR(300) NULL,
    status VARCHAR(30) NOT NULL DEFAULT 'wacht-op-publicatie',
    issuegeslotenoputc TIMESTAMPTZ NULL,
    issuestatusgecontroleerdoputc TIMESTAMPTZ NULL,
    isgeanonimiseerd BOOLEAN NOT NULL DEFAULT FALSE,
    geanonimiseerdoputc TIMESTAMPTZ NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_avg_feedback_feedbackid UNIQUE (feedbackid),
    CONSTRAINT ck_avg_feedback_clubcode CHECK (length(clubcode) > 0)
);

CREATE INDEX IF NOT EXISTS ix_avg_feedback_club_datum ON avg.feedback (clubcode, mta_inserted DESC);
CREATE INDEX IF NOT EXISTS ix_avg_feedback_club_status ON avg.feedback (clubcode, status, mta_inserted DESC);
CREATE INDEX IF NOT EXISTS ix_avg_feedback_melder_datum ON avg.feedback (clubcode, melderobjectid, mta_inserted DESC)
    WHERE melderobjectid IS NOT NULL;

-- Technische context: kortere bewaartermijn (90 dagen), daarom een eigen tabel.
CREATE TABLE IF NOT EXISTS avg.feedbacktelemetrie (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    feedbackid UUID NOT NULL REFERENCES avg.feedback (feedbackid) ON DELETE CASCADE,
    clubcode VARCHAR(20) NOT NULL,
    bron VARCHAR(30) NOT NULL,
    payload TEXT NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_avg_feedbacktelemetrie_feedbackid ON avg.feedbacktelemetrie (feedbackid);
CREATE INDEX IF NOT EXISTS ix_avg_feedbacktelemetrie_inserted ON avg.feedbacktelemetrie (mta_inserted);

-- Inzagelog: wie bekeek welke melding. Bewust zonder foreign key: het log moet blijven bestaan
-- (en volgens zijn eigen termijn verlopen) los van de melding.
CREATE TABLE IF NOT EXISTS avg.feedbackinzagelog (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clubcode VARCHAR(20) NOT NULL,
    inziendoorobjectid VARCHAR(64) NULL,
    inziendoornaam VARCHAR(200) NULL,
    actie VARCHAR(20) NOT NULL,
    feedbackid UUID NULL,
    filter VARCHAR(300) NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_avg_feedbackinzagelog_club_datum ON avg.feedbackinzagelog (clubcode, mta_inserted DESC);

ALTER TABLE avg.feedback ENABLE ROW LEVEL SECURITY;
ALTER TABLE avg.feedbacktelemetrie ENABLE ROW LEVEL SECURITY;
ALTER TABLE avg.feedbackinzagelog ENABLE ROW LEVEL SECURITY;
