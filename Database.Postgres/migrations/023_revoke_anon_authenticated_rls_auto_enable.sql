-- 023_revoke_anon_authenticated_rls_auto_enable.sql — #1198, correctie op 022
--
-- Migratie 022 haalde `EXECUTE` bij `PUBLIC` weg van `public.rls_auto_enable()`. Na deploy bleef
-- Supabase's Security Advisor de twee WARN-bevindingen (anon/authenticated kunnen de functie
-- aanroepen via /rest/v1/rpc/rls_auto_enable) echter tonen — 022 loste het probleem dus niet
-- volledig op.
--
-- OORZAAK: Supabase kent bij projectaanmaak standaard EXECUTE op elke `public`-functie toe aan de
-- rollen `anon` en `authenticated` — een GRANT die **los staat** van de PUBLIC-grant die Postgres
-- zelf standaard zet. Het intrekken van de PUBLIC-grant (022) raakt die twee Supabase-eigen,
-- rechtstreekse grants dus niet. Beide moeten expliciet ingetrokken worden.
--
-- WAAROM DIT LOKAAL NIET EERDER OPVIEL: de lokale/CI-Postgres kent de rollen `anon` en
-- `authenticated` helemaal niet — dat zijn cluster-brede rollen die Supabase's controlplane bij
-- projectaanmaak aanmaakt, geen onderdeel van een gewone database-restore (rollen zijn
-- cluster-objecten, geen database-objecten; een dump van alleen de database neemt ze niet mee).
-- Migratie 022 kon dus lokaal wél slagen zonder het echte productiegat te dekken. Deze migratie
-- controleert daarom expliciet of elke rol bestaat vóór de REVOKE, zodat hij op elke omgeving
-- (lokaal, CI, Supabase) foutloos en met het juiste effect draait.
--
-- Volledige analyse: docs/ARCHITECTUUR-DATABASE-TIERS.md §66/§67.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public' AND p.proname = 'rls_auto_enable'
    ) THEN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
            EXECUTE 'REVOKE EXECUTE ON FUNCTION public.rls_auto_enable() FROM anon';
        END IF;

        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
            EXECUTE 'REVOKE EXECUTE ON FUNCTION public.rls_auto_enable() FROM authenticated';
        END IF;
    END IF;
END;
$$;
