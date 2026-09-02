-- 011_planner_match_schema.sql — issue 888 vervolg (PlannerMatchRepository: SavePlannedMatchAsync,
-- SaveHerplanVerzoekAsync, BevestigWedstrijd/HerplanBevestig-endpoints).
--
-- Drie dingen, allemaal aangekondigd als "nog te doen" door eerdere migraties:
--
-- 1. planner.geplandewedstrijden mist vier kolommen t.o.v. Database/planner/Tables/GeplandeWedstrijden.sql
--    (dat gat werd al expliciet benoemd in 009_geplandewedstrijden_mta_modified.sql, niet hier
--    voor het eerst ontdekt): wedstrijdduurminuten (NOT NULL, geen default op SQL Server —
--    hier bewust ook NOT NULL zonder default, zodat een ontbrekende waarde een harde fout geeft
--    in plaats van stil een onzinnige duur op te slaan), aangevraagddoor, opmerking, mta_inserted.
--    Ook de UNIQUE-slotconstraint en de FK naar velden ontbraken — beide zijn een echte
--    dataintegriteitsgarantie (voorkomt een dubbele boeking op databaseniveau, niet alleen in de
--    C#-laag) en horen er dus net zo goed bij als op de SQL Server-tier.
--
-- 2. public.zonsondergang — Postgres-tegenhanger van dbo.Zonsondergang. Nodig voor
--    PopulateSunset/GetSunsetAsync (#888's PlannerAvailabilityRepository-pad leunt op
--    zonsondergangdata voor GebruikZonsondergang-velden).
--
-- 3. planner.herplanverzoeken — Postgres-tegenhanger van planner.HerplanVerzoeken. Nodig voor
--    HerplanBevestig (stond tot nu toe als gemotiveerde uitzondering in
--    scripts/ci/check-postgres-table-coverage.sh en scripts/ci/check-postgres-procedure-view-coverage.sh
--    — wordt met deze migratie geen uitzondering meer, dus de EXCEPTIONS-lijst in dat script moet
--    hierna bijgewerkt worden).

ALTER TABLE planner.geplandewedstrijden
    ADD COLUMN IF NOT EXISTS wedstrijdduurminuten INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS aangevraagddoor VARCHAR(200) NULL,
    ADD COLUMN IF NOT EXISTS opmerking VARCHAR(500) NULL,
    ADD COLUMN IF NOT EXISTS mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW();

-- DEFAULT 0 hierboven is uitsluitend nodig om de ALTER op een tabel met bestaande rijen te kunnen
-- draaien (er zijn er in de praktijk nog geen, maar migraties moeten dat niet aannemen). Nieuwe
-- rijen via SavePlannedMatchAsync geven altijd expliciet een waarde mee — zie de C#-laag.

CREATE UNIQUE INDEX IF NOT EXISTS ux_geplandewedstrijden_slot
    ON planner.geplandewedstrijden (clubcode, datum, aanvangstijd, veldnummer, velddeelgebruik);

-- Postgres kent geen 'ADD CONSTRAINT ... IF NOT EXISTS' (in tegenstelling tot kolommen en
-- indexen) — dat maakt deze ALTER zonder deze DO-blok-guard niet idempotent, in strijd met de
-- eis dat elk migratiebestand een tweede keer zonder fout moet kunnen draaien (bewezen door de
-- CI-job fresh-db-postgres, die elk bestand tweemaal toepast). Empirisch aangetroffen tijdens het
-- bouwen van deze migratie: een kale ADD CONSTRAINT faalde de tweede keer op
-- '42710: constraint already exists'.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_geplandewedstrijden_velden'
    ) THEN
        ALTER TABLE planner.geplandewedstrijden
            ADD CONSTRAINT fk_geplandewedstrijden_velden
            FOREIGN KEY (veldnummer) REFERENCES public.velden(veldnummer);
    END IF;
END $$;

-- dbo.Zonsondergang
CREATE TABLE IF NOT EXISTS public.zonsondergang (
    datum DATE NOT NULL PRIMARY KEY,
    zonsondergang TIME NOT NULL
);

-- planner.HerplanVerzoeken
CREATE TABLE IF NOT EXISTS planner.herplanverzoeken (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    wedstrijdcode BIGINT NOT NULL,
    huidigewedstrijd VARCHAR(200) NOT NULL,
    huidigedatum DATE NOT NULL,
    huidigeaanvangstijd TIME NOT NULL,
    huidigeveldnaam VARCHAR(50) NULL,
    gewensteaanvangstijd TIME NOT NULL,
    gewenstveldnummer INTEGER NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Aangevraagd',
    aangevraagddoor VARCHAR(200) NULL,
    opmerking VARCHAR(500) NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    clubcode VARCHAR(20) NOT NULL
);
