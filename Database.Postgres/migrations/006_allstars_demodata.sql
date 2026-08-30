-- 006_allstars_demodata.sql — AllStars FC demodata voor de Postgres-tier (#862, deel 1), de
-- tegenhanger van het "#635: AllStars FC demodata"-blok in Database/Script.PostDeployment1.sql.
--
-- Dekt uitsluitend de tabellen die geen ETL-afhankelijkheid hebben (public.velden,
-- public.veldbeschikbaarheid, public.speeltijden, public.teamregels bestaan altijd, aangemaakt door
-- eerdere migraties). his.teams/his.matches/avg.teambegeleiding voor de democlub-teams en
-- -wedstrijden zitten NIET hier — die tabellen bestaan pas na de eerste Sportlink-sync (#856's
-- Optie B-les geldt hier evengoed) en staan daarom in het losse, expliciet aan te roepen
-- scripts/migrations/003-seed-allstars-demo-matches-postgres.sql.
--
-- AVG: uitsluitend fictieve gegevens conform de vastgelegde uitzondering in CLAUDE.md. Alleen rijen
-- met clubcode = 'ALLSTARS'; nooit gebruikt voor een echte club.

DO $$
DECLARE
    demo_club CONSTANT VARCHAR(20) := 'ALLSTARS';
BEGIN
    -- AppSettings-rij voor de democlub zelf — SQL Server maakt deze aan in
    -- Script.PostDeployment1.sql regel ~1495; de Postgres-tier had die rij nog niet.
    IF NOT EXISTS (SELECT 1 FROM public.appsettings WHERE clubcode = demo_club) THEN
        INSERT INTO public.appsettings
            (clubname, clubcode, sportlinkapiurl, sportlinkclientid, seasonstartmonth,
             fetchschedule, syncenabled)
        VALUES
            ('AllStars FC', demo_club, 'https://data.sportlink.com', 'ALLSTARS_NO_SYNC', 8,
             '0 0 4 * * *', FALSE);
    END IF;

    -- Alleen seeden als de democlub bestaat — een fork die hem weghaalt houdt een schone database.
    IF EXISTS (SELECT 1 FROM public.appsettings WHERE clubcode = demo_club) THEN

        -- Velden: nummers 101-103 vermijden een PK-conflict met de primaire club (PK op veldnummer
        -- alleen, zelfde als de SQL Server-tier).
        IF NOT EXISTS (SELECT 1 FROM public.velden WHERE clubcode = demo_club) THEN
            INSERT INTO public.velden (veldnummer, veldnaam, veldtype, heeftkunstlicht, actief, clubcode)
            VALUES (101, 'Kunstgras 1', 'kunstgras',  TRUE,  TRUE, demo_club),
                   (102, 'Kunstgras 2', 'kunstgras',  TRUE,  TRUE, demo_club),
                   (103, 'Gras',        'natuurgras', FALSE, TRUE, demo_club);
        END IF;

        -- VeldBeschikbaarheid: dagvanweek 1=maandag..7=zondag, zelfde conventie als de SQL
        -- Server-tier (#812-les: nooit .NET-native DayOfWeek 0-6 gebruiken).
        INSERT INTO public.veldbeschikbaarheid
            (veldnummer, dagvanweek, beschikbaarvanaf, beschikbaartot, gebruikzonsondergang, clubcode)
        SELECT v.veldnummer, d.dag, '08:30', '22:00', FALSE, demo_club
        FROM (VALUES (101), (102), (103)) AS v(veldnummer)
        CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7)) AS d(dag)
        WHERE NOT EXISTS (
            SELECT 1 FROM public.veldbeschikbaarheid vb
            WHERE vb.clubcode = demo_club AND vb.veldnummer = v.veldnummer AND vb.dagvanweek = d.dag
        );

        -- Speeltijden: overgenomen van de primaire club (KNVB-standaarden), niet hardcoded.
        IF NOT EXISTS (SELECT 1 FROM public.speeltijden WHERE clubcode = demo_club) THEN
            INSERT INTO public.speeltijden
                (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, clubcode)
            SELECT leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, demo_club
            FROM public.speeltijden
            WHERE clubcode = (SELECT MIN(clubcode) FROM public.appsettings WHERE clubcode <> demo_club);
        END IF;

        -- TeamRegels (#862): één voorbeeldrij, zelfde vorm als de SQL Server-tier — teamnaam
        -- "AllStars Heren 1" komt uit de his.teams-seed (los script), geen FK dus onafhankelijk
        -- daarvan te plaatsen.
        IF NOT EXISTS (SELECT 1 FROM public.teamregels WHERE clubcode = demo_club) THEN
            INSERT INTO public.teamregels
                (teamnaam, regeltype, waardeminuten, prioriteit, actief, opmerking, clubcode)
            VALUES ('AllStars Heren 1', 'BufferVoor', 60, 10, TRUE,
                    '1 uur voor de wedstrijd geen andere wedstrijden op hetzelfde veld', demo_club);
        END IF;

    END IF;
END $$;
