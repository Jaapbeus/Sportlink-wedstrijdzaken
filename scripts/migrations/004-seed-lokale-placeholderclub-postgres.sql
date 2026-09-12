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

    -- Velden 1-3 voor de placeholder-club. AllStars houdt bewust 101+ aan, dus er is geen
    -- PK-conflict (de PK staat op veldnummer alleen, zelfde als op de SQL Server-tier).
    IF NOT EXISTS (SELECT 1 FROM public.velden WHERE clubcode = lokale_club) THEN
        INSERT INTO public.velden (veldnummer, veldnaam, veldtype, heeftkunstlicht, actief, clubcode)
        VALUES (1, 'Veld 1', 'kunstgras',  TRUE,  TRUE, lokale_club),
               (2, 'Veld 2', 'kunstgras',  TRUE,  TRUE, lokale_club),
               (3, 'Veld 3', 'natuurgras', FALSE, TRUE, lokale_club);
    END IF;

    -- dagvanweek 1=maandag..7=zondag — nooit .NET-native DayOfWeek 0-6 (#812).
    INSERT INTO public.veldbeschikbaarheid
        (veldnummer, dagvanweek, beschikbaarvanaf, beschikbaartot, gebruikzonsondergang, clubcode)
    SELECT v.veldnummer, d.dag, '08:30', '22:00', FALSE, lokale_club
    FROM (VALUES (1), (2), (3)) AS v(veldnummer)
    CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7)) AS d(dag)
    WHERE NOT EXISTS (
        SELECT 1 FROM public.veldbeschikbaarheid vb
        WHERE vb.clubcode = lokale_club AND vb.veldnummer = v.veldnummer AND vb.dagvanweek = d.dag
    );
END $$;
