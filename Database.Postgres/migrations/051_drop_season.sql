-- 051_drop_season.sql — issue 1747: contract-stap van #1715. Verwijdert het clubneutrale public.season.
--
-- Migratie 043 (#1715) voegde public.clubseason toe en liet public.season onaangeroerd staan, omdat
-- deploy.yml migraties vóór de code toepast en de toenmalige codeversie nog in public.season schreef
-- (ON CONFLICT (name), zonder clubcode). Die versie is vervangen: alle consumenten (sync-venster, KNVB-seizoen,
-- planner, bewaartermijn) en de doorrol (PostgresSeasonProcedures.EnsureSeasonsAsync) gebruiken uitsluitend
-- public.clubseason. Er is geen view, functie of trigger die naar public.season verwijst.
--
-- Bewust zonder CASCADE: verwijst er nog een object naar de tabel, dan faalt deze migratie hard en blijft alles
-- staan, in plaats van dat dat object stil mee verdwijnt. De index ux_season_name en de RLS-instelling van
-- migratie 021 gaan met de tabel mee. Geen nieuwe tabel, dus geen nieuwe RLS-plicht.
--
-- Veiligheidscontrole: bevat public.season rijen terwijl public.clubseason helemaal leeg is, dan is de
-- eenmalige vulling van migratie 043 niet gedraaid en zou het verwijderen de enige kopie van het seizoensbereik
-- wegnemen. Dan stopt de migratie met een duidelijke fout.
--
-- Bevroren migraties 008, 021 en 043 blijven ongewijzigd (het checksum-verbod van #821); op een verse database
-- maken zij de tabel eerst aan en verwijdert deze migratie haar daarna weer.

-- Geneste IF: plpgsql plant een statement pas wanneer het wordt bereikt. Eén gecombineerde conditie
-- (to_regclass(..) IS NOT NULL AND EXISTS (SELECT .. FROM public.season)) faalt met 42P01 zodra de tabel al weg is.
DO $$
BEGIN
    IF to_regclass('public.season') IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM public.season)
           AND NOT EXISTS (SELECT 1 FROM public.clubseason) THEN
            RAISE EXCEPTION 'public.season bevat rijen maar public.clubseason is leeg; migratie 043 is niet toegepast of niet gevuld (#1747)';
        END IF;
    END IF;
END
$$;

DROP TABLE IF EXISTS public.season;
