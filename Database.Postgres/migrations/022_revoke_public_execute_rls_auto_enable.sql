-- 022_revoke_public_execute_rls_auto_enable.sql — #1198, vervolg op 021
--
-- Na migratie 021 meldde Supabase's Security Advisor twee nieuwe WARN-bevindingen
-- (anon_security_definer_function_executable, authenticated_security_definer_function_executable)
-- op `public.rls_auto_enable()`: een SECURITY DEFINER-functie, aanroepbaar door zowel de anonieme
-- als elke ingelogde PostgREST-rol via `/rest/v1/rpc/rls_auto_enable`.
--
-- EERST VERKEERD BEOORDEELD, TER PLEKKE GECORRIGEERD: deze functie staat in geen enkele migratie
-- of C#-bestand in dit repository (bevestigd met een volledige grep), dus leek ze op het eerste
-- gezicht een overbodig artefact om te droppen. `pg_get_functiondef` en `pg_event_trigger` lieten
-- echter zien dat het een `RETURNS event_trigger`-functie is, gekoppeld aan het event-trigger
-- `ensure_rls` (`ddl_command_end`) — geregistreerd naast onmiskenbaar Supabase-eigen
-- platform-triggers (`pgrst_ddl_watch`, `issue_pg_cron_access`, `issue_pg_graphql_access`, etc.).
-- Dit is Supabase's eigen aanbevolen "auto-enable RLS op elke nieuwe public-tabel"-mechanisme: een
-- blijvend vangnet tegen precies het probleem uit migratie 021, voor elke tabel die hierna wordt
-- aangemaakt. **Deze functie droppen was dus de verkeerde fix** — de eerdere poging faalde ook
-- meteen hard op "cannot drop function ... because other objects depend on it" (het event trigger
-- zelf), wat dit aan het licht bracht vóórdat er iets kapot kon gaan.
--
-- DE WERKELIJKE OORZAAK VAN DE WARN: Postgres kent standaard EXECUTE op een nieuwe functie toe aan
-- de rol PUBLIC. Supabase's PostgREST-laag ontsluit elke `public`-functie met EXECUTE voor
-- PUBLIC/anon/authenticated automatisch als RPC-endpoint — ongeacht of de functie voor menselijk/
-- extern gebruik bedoeld is. Een event-trigger-functie is dat per definitie niet: Postgres roept
-- hem uitsluitend intern aan via de event-trigger-machinerie, nooit via een gewone SQL-aanroep.
--
-- DE JUISTE FIX: EXECUTE bij PUBLIC weghalen, functie en event trigger ongemoeid laten. Het
-- vangnet blijft daarmee voor elke toekomstige tabel werken; alleen de onbedoelde externe
-- aanroepbaarheid verdwijnt.
--
-- IF EXISTS-achtige veiligheid: deze functie bestaat niet overal (bijv. niet aangemaakt door onze
-- eigen migraties), dus een DO-block met een existence-check voorkomt een fout op omgevingen waar
-- ze ontbreekt — inclusief een lokale/CI-Postgres zonder productie-restore.
--
-- Volledige analyse: docs/ARCHITECTUUR-DATABASE-TIERS.md §65.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public' AND p.proname = 'rls_auto_enable'
    ) THEN
        REVOKE EXECUTE ON FUNCTION public.rls_auto_enable() FROM PUBLIC;
    END IF;
END;
$$;
