-- 004-seed-lokale-placeholderclub-postgres.sql  (#1060)
--
-- ONTWIKKELSCRIPT — draait NOOIT automatisch mee. Dit bestand staat bewust in scripts/migrations/
-- en niet in Database.Postgres/migrations/: alles in die tweede map wordt door MigrationRunner op
-- elke database toegepast, productie inbegrepen, en een placeholder-club hoort daar nooit terecht
-- te komen.
--
-- WAAROM DIT NODIG IS
-- Een verse lokale database bevat na de migraties precies één club: AllStars FC, en die staat per
-- ontwerp op syncenabled = FALSE (006_allstars_demodata.sql). PostgresAppSettings.LoadSettingsAsync
-- selecteert echter 'WHERE syncenabled = true' — de democlub mag nooit stilzwijgend de primaire
-- club worden. Gevolg op een verse database: de instellingencache blijft leeg, en élk
-- /api/beheer/*-endpoint antwoordt 500 ("Vereiste instelling 'clubCode' ontbreekt"), terwijl
-- /api/health wel 200 geeft met status 'degraded'.
--
-- Dit script vult dat gat met een club-NEUTRALE placeholder: clubcode 'CLUB'. Geen echte clubnaam,
-- geen echt domein, geen echt clientId — die horen niet in een openbare repository (CLAUDE.md,
-- "Geen club-specifieke strings in code"). Een echte installatie overschrijft deze rij via de
-- Admin GUI of via de cutover-kopieertool.
--
-- Uitvoeren:
--   docker cp scripts/migrations/004-seed-lokale-placeholderclub-postgres.sql sportlink-postgres:/tmp/seed.sql
--   docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" sportlink-postgres \
--       psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -f /tmp/seed.sql
--
-- Idempotent: een tweede run doet niets.

DO $$
DECLARE
    lokale_club CONSTANT VARCHAR(20) := 'CLUB';
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.appsettings WHERE clubcode = lokale_club) THEN
        INSERT INTO public.appsettings
            (clubname, clubcode, sportlinkapiurl, sportlinkclientid, seasonstartmonth,
             fetchschedule, syncenabled, plannerafzendernaam, accommodatie)
        VALUES
            ('Lokale Ontwikkelclub', lokale_club, 'https://data.sportlink.com',
             'LOKAAL_GEEN_SYNC', 8, '0 0 4 * * *', TRUE, 'Lokale Planner', 'Sportpark');

        RAISE NOTICE 'Placeholder-club % aangemaakt (syncenabled = TRUE).', lokale_club;
    ELSE
        RAISE NOTICE 'Placeholder-club % bestaat al — niets gedaan.', lokale_club;
    END IF;

    -- Veldnummers 201-203 — een GERESERVEERD bereik, geen willekeurige keuze.
    --
    -- De primaire sleutel van public.velden staat op veldnummer ALLEEN, niet op
    -- (veldnummer, clubcode). Veldnummers zijn dus globaal uniek over alle clubs heen, en elke
    -- club in dit schema houdt daarom een eigen bereik aan: AllStars 101-103, testclub-matchsearch
    -- 301-302, testclub-planner 401, testclub-availsvc 501-502.
    --
    -- Deze seed nam aanvankelijk 1-3 (#1060) en botste daarmee op
    -- PlannerAvailabilityRepositoryIntegrationTests, dat 1 en 2 gebruikt: wie de setupinstructies
    -- volgde en daarna de testsuite draaide, kreeg zes keer
    -- '23505: duplicate key value violates unique constraint "velden_pkey"' — een foutmelding die
    -- op een kapotte testsuite lijkt in plaats van op een botsing met een seed (#1080).
    IF NOT EXISTS (SELECT 1 FROM public.velden WHERE clubcode = lokale_club) THEN
        INSERT INTO public.velden (veldnummer, veldnaam, veldtype, heeftkunstlicht, actief, clubcode)
        VALUES (201, 'Veld 1', 'kunstgras',  TRUE,  TRUE, lokale_club),
               (202, 'Veld 2', 'kunstgras',  TRUE,  TRUE, lokale_club),
               (203, 'Veld 3', 'natuurgras', FALSE, TRUE, lokale_club);
    END IF;

    -- dagvanweek 1=maandag..7=zondag — nooit .NET-native DayOfWeek 0-6 (#812).
    INSERT INTO public.veldbeschikbaarheid
        (veldnummer, dagvanweek, beschikbaarvanaf, beschikbaartot, gebruikzonsondergang, clubcode)
    SELECT v.veldnummer, d.dag, '08:30', '22:00', FALSE, lokale_club
    FROM (VALUES (201), (202), (203)) AS v(veldnummer)
    CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7)) AS d(dag)
    WHERE NOT EXISTS (
        SELECT 1 FROM public.veldbeschikbaarheid vb
        WHERE vb.clubcode = lokale_club AND vb.veldnummer = v.veldnummer AND vb.dagvanweek = d.dag
    );
END $$;
