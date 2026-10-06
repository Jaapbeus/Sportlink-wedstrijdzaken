-- 038_planner_emailtrace.sql — permanente, PII-arme beslissingstrace per verwerkt e-mailbericht (#1568, deel B).
-- SQL Server-tegenhanger: Database/planner/Tables/EmailTrace.sql + Database/Script.PostDeployment1.sql.
--
-- Besluit eigenaar 2026-10-06: de trace is PERMANENT. Dat kan alleen omdat hij PII-arm is: hij wordt gebouwd
-- door Planner.Shared.Email.Trace.TraceBuilder, die uitsluitend gesaneerde, afgekapte waarden toelaat
-- (nooit mailbody, afzender, onderwerp of vrije tekst; e-mailadressen en lange cijferreeksen gemaskeerd).
-- planner.emailverwerking zelf wordt na 30 dagen geanonimiseerd en na 90 dagen verwijderd; de trace blijft.
--
-- Bewust GEEN foreign key naar planner.emailverwerking: een FK zou de dagelijkse/wekelijkse cleanup
-- (DELETE FROM planner.emailverwerking, PostgresCleanupProcedures) laten falen of de trace meenemen
-- (CASCADE) — beide ondermijnen "de trace overleeft de verwerking". verwerkingid is een identity-waarde
-- die nooit hergebruikt wordt, en geen persoonsgegeven. Het endpoint filtert altijd ook op clubcode.
--
-- UNIQUE (verwerkingid) maakt een retry idempotent (INSERT ... ON CONFLICT DO UPDATE) en dient tegelijk als
-- opzoekindex. Tijden zijn TIMESTAMPTZ (UTC). Row-Level Security aan zonder policies (#1198).

CREATE TABLE IF NOT EXISTS planner.emailtrace (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    verwerkingid INTEGER NOT NULL,
    clubcode VARCHAR(20) NOT NULL,
    aangemaakt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    verzoektype VARCHAR(50) NOT NULL,
    zekerheid VARCHAR(10) NOT NULL,
    sjabloonsleutel VARCHAR(60) NULL,
    tracejson JSONB NOT NULL,
    appversie VARCHAR(20) NOT NULL,
    CONSTRAINT uq_emailtrace_verwerkingid UNIQUE (verwerkingid),
    CONSTRAINT ck_emailtrace_clubcode CHECK (length(clubcode) > 0),
    CONSTRAINT ck_emailtrace_zekerheid CHECK (zekerheid IN ('Zeker', 'Onzeker', 'Mislukt'))
);

ALTER TABLE planner.emailtrace ENABLE ROW LEVEL SECURITY;
