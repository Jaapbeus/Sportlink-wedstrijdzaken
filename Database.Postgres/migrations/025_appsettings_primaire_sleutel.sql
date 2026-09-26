-- 025_appsettings_primaire_sleutel.sql — #1218
--
-- PROBLEEM: public.appsettings had GEEN ENKELE constraint. `pg_constraint` gaf voor deze tabel nul
-- rijen terug: geen primaire sleutel, en geen unique op `clubcode`. Niets verhinderde dus twee
-- rijen met dezelfde clubcode, terwijl de applicatie instellingen leest met `LIMIT 1`. Een dubbele
-- rij geeft dan geen foutmelding maar stilzwijgend de verkeerde configuratie — precies het soort
-- stille misconfiguratie dat de regel "geen stille fallback" in CLAUDE.md wil voorkomen.
--
-- Gevonden door Supabase's Performance Advisor (lint `no_primary_key`), maar dat is de verkeerde
-- bril: met twee rijen is dit geen performanceprobleem. Het echte punt is correctheid.
--
-- WAAROM `clubcode` DE JUISTE SLEUTEL IS: het deploymentmodel (CLAUDE.md, "Deployment-model") legt
-- vast dat één installatie precies één primaire club plus AllStars FC als democlub bedient — één
-- rij per clubcode dus. `clubcode VARCHAR(20) NOT NULL` (migratie 001) is daarmee de natuurlijke
-- primaire sleutel. Een PK in plaats van alleen een unique index lost meteen ook de
-- advisor-bevinding op.
--
-- WAAROM DIT NIET BIJ #1211/MIGRATIE 024 IS MEEGENOMEN: sinds #1093 draaien migraties automatisch
-- bij elke deploy, vóór de code live gaat. Een constraint die hard faalt omdat productie al
-- dubbele rijen heeft, neemt dan de volledige deploy mee (zie §57). Dat vereist eerst een controle
-- op de productiedatabase, en die is nu gedaan: op 2026-09-17 via de read-only Supabase MCP
-- geteld — 2 rijen, 2 unieke clubcodes, 0 clubcodes met meer dan één rij. Zie §71 voor waarom dat
-- resultaat betrouwbaar is (de MCP-rol omzeilt RLS, dus een leeg resultaat is hier écht leeg en
-- geen RLS-artefact).
--
-- De controle hieronder herhaalt die toets op het moment van toepassen, en faalt met een
-- leesbare melding in plaats van met een kale index-fout. Wie deze migratie op een andere
-- installatie toepast krijgt zo direct te zien wát er opgeruimd moet worden.
--
-- LET OP bij het lezen van die melding: `Database.Postgres.Cli` onderdrukt sinds #1225 bewust de
-- volledige databasefoutmelding, omdat de CI-uitvoer van een publieke repository publiek is en een
-- databasefout vaak host- of gebruikersnaam meedraagt. In de deploy-log zie je dus alleen
-- "SQLSTATE P0001" en niet de tekst hieronder. De CLI eindigt wel met exitcode 1, dus de deploy
-- stopt. Om de melding zelf te zien: draai dit bestand lokaal met psql tegen dezelfde database —
-- precies wat de onderdrukkingsmelding van #1225 ook voorstelt. Geverifieerd bij #1218.
--
-- Idempotent: is de primaire sleutel er al, dan doet dit bestand niets.

DO $$
DECLARE
    dubbele_codes TEXT;
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public.appsettings'::regclass
          AND contype = 'p'
    ) THEN
        RAISE NOTICE 'public.appsettings heeft al een primaire sleutel — niets te doen.';
        RETURN;
    END IF;

    SELECT string_agg(clubcode, ', ' ORDER BY clubcode)
    INTO dubbele_codes
    FROM (
        SELECT clubcode
        FROM public.appsettings
        GROUP BY clubcode
        HAVING COUNT(*) > 1
    ) AS d;

    IF dubbele_codes IS NOT NULL THEN
        RAISE EXCEPTION
            'Kan geen primaire sleutel op public.appsettings zetten: meer dan één rij voor clubcode(s) %. '
            'Ruim eerst handmatig op — behoud per clubcode de rij met de juiste instellingen — en pas '
            'daarna deze migratie opnieuw toe. Zie issue #1218.',
            dubbele_codes;
    END IF;

    ALTER TABLE public.appsettings
        ADD CONSTRAINT pk_appsettings PRIMARY KEY (clubcode);

    RAISE NOTICE 'Primaire sleutel pk_appsettings (clubcode) toegevoegd aan public.appsettings.';
END;
$$;
