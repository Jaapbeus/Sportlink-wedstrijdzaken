-- 008_season.sql — issue 890 (Postgres-tier synchronisatiepad): dbo.Season had nog geen
-- Postgres-migratie. SeasonHelper.GetSeasonEndWeekOffsetAsync/GetSeasonStartWeekOffsetAsync (de
-- Postgres-tier gebruikte tot nu toe een vaste fallback van 30 weken, en de reset-modus van
-- SyncFunction gaf een expliciete 501) hebben deze tabel nodig. Zie docs/ARCHITECTUUR-DATABASE-TIERS.md.
--
-- Identifier-casing: lowercase, ongequote — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §3.
--
-- Bewust NIET meegenomen: dbo.DateTable/sp_CreateDateTable. Die tabel heeft in de hele applicatie
-- precies één consument: de view pub.DateTable — en #861 liet de drie pub.*-rapportageviews al
-- expliciet en gemotiveerd vervallen (nul consumenten binnen de applicatie). Een Postgres-
-- tegenhanger van dbo.DateTable zou dus uitsluitend een tabel zijn die nergens gelezen wordt.
CREATE TABLE IF NOT EXISTS public.season (
    name VARCHAR(9) NOT NULL,
    datefrom DATE NULL,
    dateuntil DATE NULL,
    CONSTRAINT ux_season_name UNIQUE (name)
);

-- Eenmalige seed, dezelfde logica als dbo.sp_UpdateSeasonTable (Database/dbo/System Stored
-- Procedures/sp_UpdateSeasonTable.sql), berekend op het moment dat déze migratie wordt toegepast.
--
-- Belangrijk, structureel verschil met de SQL Server-tier: Script.PostDeployment1.sql roept
-- sp_UpdateSeasonTable bij ELKE deploy opnieuw aan, en rolt zo het seizoen vanzelf door zodra de
-- kalender twee maanden voor de volgende seizoensstart zit. Een Postgres-migratiebestand draait
-- precies één keer, ooit — er bestaat op deze tier nog geen mechanisme dat automatisch een
-- volgend seizoen toevoegt naarmate de tijd verstrijkt. Dat is een bewust, gedocumenteerd gat
-- (zie docs/ARCHITECTUUR-DATABASE-TIERS.md): deze seed geeft een verse installatie twee afgeronde
-- seizoenen plus (indien van toepassing) het huidige/aankomende seizoen, precies zoals een verse
-- SQL Server-installatie er na de eerste deploy ook bij zou staan — maar zonder de jaarlijkse
-- automatische aanvulling die de SQL Server-tier wel heeft.
DO $$
DECLARE
    season_start_month INTEGER;
    year_start INTEGER := EXTRACT(YEAR FROM CURRENT_DATE)::INTEGER;
    threshold_date DATE;
BEGIN
    SELECT MIN(seasonstartmonth) INTO season_start_month FROM public.appsettings;
    IF season_start_month IS NULL THEN
        season_start_month := 7;
    END IF;

    -- Geen seizoenen aanwezig: voeg de laatste twee afgeronde seizoenen toe (zelfde tak als
    -- sp_UpdateSeasonTable se "No seasons found").
    IF NOT EXISTS (SELECT 1 FROM public.season) THEN
        INSERT INTO public.season (name, datefrom, dateuntil)
        VALUES
            (
                (year_start - 2)::text || '-' || (year_start - 1)::text,
                make_date(year_start - 2, season_start_month, 1),
                make_date(year_start - 1, season_start_month, 1) - INTERVAL '1 day'
            ),
            (
                (year_start - 1)::text || '-' || year_start::text,
                make_date(year_start - 1, season_start_month, 1),
                make_date(year_start, season_start_month, 1) - INTERVAL '1 day'
            );
    END IF;

    -- Twee maanden vóór de start van een nieuw seizoen: voeg het toe als het nog niet bestaat.
    -- Interval-rekenkunde (niet make_date met een mogelijk negatieve/nul maand) om een geldige
    -- datum te garanderen ongeacht season_start_month (bijv. januari zou month-2 = -1 geven).
    threshold_date := (make_date(year_start, 1, 1) + ((season_start_month - 2 - 1) * INTERVAL '1 month'))::date;

    IF CURRENT_DATE >= threshold_date
       AND NOT EXISTS (SELECT 1 FROM public.season WHERE name = year_start::text || '-' || (year_start + 1)::text)
    THEN
        INSERT INTO public.season (name, datefrom, dateuntil)
        VALUES (
            year_start::text || '-' || (year_start + 1)::text,
            make_date(year_start, season_start_month, 1),
            make_date(year_start + 1, season_start_month, 1) - INTERVAL '1 day'
        );
    END IF;
END $$;
