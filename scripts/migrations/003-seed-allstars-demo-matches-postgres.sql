-- ============================================================
-- Postgres-tier-tegenhanger van scripts/migrations/003-seed-allstars-demo-matches.sql (#862, deel 1)
--
-- ACHTERGROND (zelfde les als #856 op de SQL Server-tier): his.teams/his.matches worden niet door
-- een migratiebestand aangemaakt, maar dynamisch door PostgresSchemaGenerator zodra de Postgres-ETL
-- de eerste Sportlink-sync draait. Op een verse Postgres-installatie bestaan ze dus nog niet op het
-- moment dat de reguliere migraties (Database.Postgres/migrations/*.sql) lopen — de team- en
-- wedstrijddemo voor de democlub ALLSTARS kan daarom niet in een gewone migratie staan.
--
-- UITVOEREN:
--   1. Zorg dat de eerste Postgres-sync al gelopen is (his.teams/his.matches bestaan).
--   2. psql -h <host> -p <port> -U <user> -d <database> -f scripts/migrations/003-seed-allstars-demo-matches-postgres.sql
--
-- Faalt hard (RAISE EXCEPTION) als his.teams/his.matches nog niet bestaan of als de democlub
-- ontbreekt. Alles staat bewust in één DO-blok: RAISE EXCEPTION breekt het hele blok af vóórdat
-- Postgres de latere INSERT-statements (die anders op een niet-bestaande tabel zouden knallen) ooit
-- probeert te plannen.
--
-- Volledig idempotent: elk onderdeel is NOT EXISTS-gated, herhaald uitvoeren doet niets kwaads.
-- Speeldata worden relatief aan de uitvoerdatum berekend, niet hardcoded.
--
-- AVG: uitsluitend fictieve gegevens conform de vastgelegde uitzondering in CLAUDE.md — voornamen
-- zonder achternaam en het gereserveerde .test-TLD (RFC 2606). Alleen rijen met clubcode =
-- 'ALLSTARS'; nooit gebruikt voor een echte club.
-- ============================================================

DO $$
DECLARE
    demo_club CONSTANT VARCHAR(20) := 'ALLSTARS';
    vandaag DATE := CURRENT_DATE;
    zaterdag1 DATE;
    demo_accommodatie VARCHAR(200);
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.appsettings WHERE clubcode = demo_club) THEN
        RAISE EXCEPTION 'AllStars-demodata (#862): democlub ALLSTARS bestaat niet in public.appsettings — voer eerst de reguliere migraties uit (Database.Postgres/migrations/006_allstars_demodata.sql seedt de AppSettings-rij).';
    END IF;

    IF to_regclass('his.teams') IS NULL OR to_regclass('his.matches') IS NULL THEN
        RAISE EXCEPTION 'AllStars-demodata (#862): his.teams/his.matches bestaan nog niet — de eerste Postgres-Sportlink-sync moet eerst lopen voordat dit script team- en wedstrijddemo kan seeden.';
    END IF;

    -- his.teams: twee teams per categorie, zelfde contract als de SQL Server-tier (#853:
    -- teamcode/lokaleteamcode/poulecode expliciet gevuld — bk_teams is hier een GENERATED ALWAYS-
    -- kolom die zich uit die drie afleidt, dus NIET in de INSERT-kolomlijst).
    IF NOT EXISTS (SELECT 1 FROM his.teams WHERE clubcode = demo_club) THEN
        INSERT INTO his.teams
            (teamcode, lokaleteamcode, poulecode, teamnaam, teamsoort,
             geslacht, leeftijdscategorie, competitiesoort, mta_inserted, mta_modified, clubcode)
        SELECT
            9000000 + ROW_NUMBER() OVER (ORDER BY c.cat, n.nr),
            9000000 + ROW_NUMBER() OVER (ORDER BY c.cat, n.nr),
            9000000 + ROW_NUMBER() OVER (ORDER BY c.cat, n.nr),
            'AllStars ' || c.cat || ' ' || n.nr,
            c.soort, c.geslacht, c.leeftijd, 'regulier',
            NOW(), NOW(), demo_club
        FROM (VALUES
                ('JO8',   'JO8',  'Jeugd',    'Jongens'),
                ('JO9',   'JO9',  'Jeugd',    'Jongens'),
                ('JO10',  'JO10', 'Jeugd',    'Jongens'),
                ('JO11',  'JO11', 'Jeugd',    'Jongens'),
                ('JO12',  'JO12', 'Jeugd',    'Jongens'),
                ('JO13',  'JO13', 'Jeugd',    'Jongens'),
                ('JO14',  'JO14', 'Jeugd',    'Jongens'),
                ('JO15',  'JO15', 'Jeugd',    'Jongens'),
                ('JO17',  'JO17', 'Jeugd',    'Jongens'),
                ('JO19',  'JO19', 'Jeugd',    'Jongens'),
                ('MO13',  'MO13', 'Jeugd',    'Meisjes'),
                ('MO15',  'MO15', 'Jeugd',    'Meisjes'),
                ('Heren', '1-99', 'Senioren', 'Mannen'),
                ('VR',    'VR',   'Senioren', 'Vrouwen')
             ) AS c(cat, leeftijd, soort, geslacht)
        CROSS JOIN (VALUES (1), (2)) AS n(nr);
    END IF;

    -- avg.teambegeleiding: fictieve trainer per team, .test-domein, deterministisch via hashtext
    -- i.p.v. SQL Server's CHECKSUM (Postgres-equivalent, zelfde soort niet-cryptografische hash).
    IF NOT EXISTS (SELECT 1 FROM avg.teambegeleiding WHERE clubcode = demo_club) THEN
        INSERT INTO avg.teambegeleiding (team, naam, emailadres, teamrol, clubcode)
        SELECT
            t.teamnaam,
            v.naam,
            LOWER(v.naam) || '.' || ROW_NUMBER() OVER (ORDER BY t.teamnaam) || '@allstars-fc.test',
            'Trainer',
            demo_club
        FROM his.teams t
        CROSS JOIN LATERAL (
            SELECT naam FROM (VALUES
                ('Frenkie'), ('Bas'), ('Stef'), ('Peer'), ('Bram'), ('Ralf'), ('Gijs'),
                ('Jacco'), ('Sjaak'), ('Guus'), ('Ferry'), ('Nico'), ('Edwin'), ('Dirkje')
            ) AS namen(naam)
            OFFSET (ABS(hashtext(t.bk_teams)) % 14) LIMIT 1
        ) v
        WHERE t.clubcode = demo_club;
    END IF;

    -- his.matches: acht speelronden vanaf de eerstvolgende zaterdag, afwisselend thuis/uit.
    -- 1900-01-01 was een maandag (dag 0), dus (vandaag - 1900-01-01) % 7 geeft 5 = zaterdag,
    -- zelfde DATEFIRST-onafhankelijke berekening als de SQL Server-tier.
    IF NOT EXISTS (SELECT 1 FROM his.matches WHERE clubcode = demo_club) THEN
        zaterdag1 := vandaag + ((5 - ((vandaag - DATE '1900-01-01') % 7) + 7) % 7);
        SELECT accommodatie INTO demo_accommodatie FROM public.appsettings WHERE clubcode = demo_club;

        INSERT INTO his.matches
            (wedstrijdcode, datum, kaledatum, wedstrijd, aanvangstijd,
             thuisteam, uitteam, status, teamnaam, competitiesoort, accommodatie,
             mta_inserted, mta_modified, clubcode)
        SELECT
            9000000 + x.code,
            x.datum::text,
            x.datum::text,
            CASE WHEN x.thuis = 1
                 THEN x.team || ' - Tegenstander ' || x.ronde
                 ELSE 'Tegenstander ' || x.ronde || ' - ' || x.team END,
            x.tijd,
            CASE WHEN x.thuis = 1 THEN x.team ELSE 'Tegenstander ' || x.ronde END,
            CASE WHEN x.thuis = 1 THEN 'Tegenstander ' || x.ronde ELSE x.team END,
            'Te spelen',
            x.team,
            'regulier',
            CASE WHEN x.thuis = 1 THEN demo_accommodatie ELSE 'Sportpark Tegenstander' END,
            NOW(), NOW(), demo_club
        FROM (
            SELECT
                t.teamnaam AS team,
                r.ronde,
                ROW_NUMBER() OVER (ORDER BY t.teamnaam, r.ronde) AS code,
                zaterdag1 + ((r.ronde - 1) * 7) AS datum,
                (r.ronde + ROW_NUMBER() OVER (PARTITION BY r.ronde ORDER BY t.teamnaam)) % 2 AS thuis,
                CASE WHEN t.teamsoort = 'Senioren' THEN '14:30' ELSE '09:00' END AS tijd
            FROM his.teams t
            CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7), (8)) AS r(ronde)
            WHERE t.clubcode = demo_club
        ) AS x;
    END IF;

    RAISE NOTICE 'AllStars-demodata (#862): teams, teambegeleiding en wedstrijden geseed (of al aanwezig).';
END $$;
