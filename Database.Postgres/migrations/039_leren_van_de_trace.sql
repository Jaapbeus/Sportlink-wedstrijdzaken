-- 039_leren_van_de_trace.sql — de beheerder leert het systeem vanuit de trace (#1568, deel C).
-- SQL Server-tegenhanger: Database/dbo/Tables/TeamAliassen.sql, Database/planner/Tables/ClassificatieCorrectie.sql,
-- Database/planner/Tables/OnbekendeTeamTekst.sql en het blok "#1568 deel C" in Database/Script.PostDeployment1.sql.
--
-- Drie dingen, alle drie additief en idempotent (bestaande migraties blijven onaangeroerd):
--
-- 1. public.teamaliassen krijgt een auditspoor voor aliassen die een beheerder aanmaakt of beoordeelt.
--    aangemaaktdoor/beoordeelddoor = Entra object-ID (pseudoniem), *naam = momentopname van de weergavenaam,
--    uitsluitend uit het Easy Auth-principal (nooit uit de requestbody, nooit een e-mailadres).
--    NULL betekent: door het systeem aangemaakt (sync of AI).
-- 2. planner.onbekendeteamtekst — wachtrij met teamteksten die de pipeline niet kon koppelen. Bewust GEEN
--    foreign key naar planner.emailverwerking: laatsteverwerkingid is alleen een aanwijzing en de
--    verwerking wordt na 30/90 dagen geanonimiseerd/verwijderd.
-- 3. planner.classificatiecorrectie krijgt herkomst 'Reply' (bestaand) of 'Admin' (nieuw). Een admin-leermoment
--    heeft geen reply-paar: beide verwerkings-id's worden dus nullable (de CHECK dwingt ze af voor 'Reply').
--    herkomstverwerkingid is een los getal zonder FK — een FK zou de retentie-DELETE van
--    planner.emailverwerking (#424) laten falen of het leermoment laten verdwijnen. Admin-leermomenten zijn
--    permanent (besluit eigenaar 2026-10-06): de cleanup raakt alleen herkomst = 'Reply'.
--
-- Row-Level Security aan zonder policies voor de nieuwe tabel (#1198).

ALTER TABLE public.teamaliassen
    ADD COLUMN IF NOT EXISTS aangemaaktdoor       VARCHAR(64)  NULL,
    ADD COLUMN IF NOT EXISTS aangemaaktdoornaam   VARCHAR(100) NULL,
    ADD COLUMN IF NOT EXISTS aangemaaktop         TIMESTAMPTZ  NULL,
    ADD COLUMN IF NOT EXISTS herkomstverwerkingid INTEGER      NULL,
    ADD COLUMN IF NOT EXISTS reden                VARCHAR(200) NULL,
    ADD COLUMN IF NOT EXISTS beoordeelddoor        VARCHAR(64)  NULL,
    ADD COLUMN IF NOT EXISTS beoordeelddoornaam    VARCHAR(100) NULL,
    ADD COLUMN IF NOT EXISTS beoordeeldop         TIMESTAMPTZ  NULL;

CREATE TABLE IF NOT EXISTS planner.onbekendeteamtekst (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clubcode VARCHAR(20) NOT NULL,
    ruwetekstgenormaliseerd VARCHAR(200) NOT NULL,
    voorbeeldtekst VARCHAR(80) NOT NULL,
    aantal INTEGER NOT NULL DEFAULT 1,
    eerstgezien TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    laatstgezien TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    laatsteverwerkingid INTEGER NULL,
    status VARCHAR(12) NOT NULL DEFAULT 'open',
    CONSTRAINT ck_onbekendeteamtekst_clubcode CHECK (length(clubcode) > 0),
    CONSTRAINT ck_onbekendeteamtekst_status CHECK (status IN ('open', 'afgehandeld', 'genegeerd')),
    CONSTRAINT uq_onbekendeteamtekst_club_sleutel UNIQUE (clubcode, ruwetekstgenormaliseerd)
);

CREATE INDEX IF NOT EXISTS ix_onbekendeteamtekst_club_status
    ON planner.onbekendeteamtekst (clubcode, status, laatstgezien DESC);

ALTER TABLE planner.onbekendeteamtekst ENABLE ROW LEVEL SECURITY;

ALTER TABLE planner.classificatiecorrectie
    ALTER COLUMN origineleverwerkingid DROP NOT NULL,
    ALTER COLUMN correctionverwerkingid DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS herkomst             VARCHAR(10)  NOT NULL DEFAULT 'Reply',
    ADD COLUMN IF NOT EXISTS aangemaaktdoor       VARCHAR(64)  NULL,
    ADD COLUMN IF NOT EXISTS aangemaaktdoornaam   VARCHAR(100) NULL,
    ADD COLUMN IF NOT EXISTS aangemaaktop         TIMESTAMPTZ  NULL,
    ADD COLUMN IF NOT EXISTS herkomstverwerkingid INTEGER      NULL;

ALTER TABLE planner.classificatiecorrectie DROP CONSTRAINT IF EXISTS ck_classificatiecorrectie_herkomst;
ALTER TABLE planner.classificatiecorrectie
    ADD CONSTRAINT ck_classificatiecorrectie_herkomst CHECK (
        herkomst IN ('Reply', 'Admin')
        AND (herkomst <> 'Reply' OR (origineleverwerkingid IS NOT NULL AND correctionverwerkingid IS NOT NULL)));
