-- 021_enable_row_level_security.sql — #1198, herziening van #985
--
-- SUPABASE SECURITY ADVISOR MELDDE DIT ALS CRITICAL (rls_disabled_in_public): elke tabel in het
-- `public`-schema is zonder RLS standaard extern leesbaar/schrijfbaar/verwijderbaar via Supabase's
-- automatisch gegenereerde PostgREST-REST-API, bereikbaar met de (bewust publieke) anon-key —
-- ONGEACHT of deze applicatie die API ooit gebruikt.
--
-- #985 (2026-09-04) onderzocht RLS al eens en besloot bewust om het niet te implementeren. Die
-- analyse ging over autorisatie tussen databaseclients van déze applicatie (terecht: er is er
-- precies één, de FunctionApp, via één POSTGRES_CONNECTION_STRING-rol) maar niet over wat het
-- platform zelf standaard blootstelt los van onze eigen architectuur. Dit bestand corrigeert dat
-- zonder de rest van #985's conclusie te verwerpen — zie hieronder.
--
-- WAAROM GEEN POLICIES: de rol in POSTGRES_CONNECTION_STRING is eigenaar van elke tabel hierin (zij
-- heeft ze via eerdere migraties aangemaakt) of is de Supabase-superuser via de pooler — in beide
-- gevallen omzeilt Postgres RLS onvoorwaardelijk voor die rol, met of zonder policies. RLS hier is
-- dus uitsluitend een schakelaar die Supabase's eigen `anon`/`authenticated`-PostgREST-rollen
-- buitensluit. Geen per-rij-autorisatie, geen wijziging aan het single-tenant-deploymentmodel
-- (#985/#393): de FunctionApp blijft na deze migratie exact zo werken als ervoor.
--
-- Idempotent: ENABLE ROW LEVEL SECURITY op een tabel waar het al aan staat is een no-op, geen fout.
--
-- Volledige analyse: docs/ARCHITECTUUR-DATABASE-TIERS.md §64.

ALTER TABLE public.appsettings ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.appsettingsaudit ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.emailtemplateinstellingen ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.knvbkalenderdag ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.season ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.speeltijden ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sportlinkcontractcheck ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sportlinkextensierollen ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sportlinkmutationaudit ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sportlinkpublicmatchidcache ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sportlinkservicetokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.syncjobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.teamaliassen ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.teamregels ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.teams ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.teamvoorkeurtijden ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.uitgeslotenemailadressen ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.veldbeschikbaarheid ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.velden ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.veldperiode ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.veldtraining ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.zonsondergang ENABLE ROW LEVEL SECURITY;

-- De migratie-ledger zelf staat ook in public (aangemaakt door MigrationRunner.EnsureLedgerTableAsync,
-- vóórdat dit bestand draait) en bevat geen persoonsgegevens (alleen bestandsnamen/checksums), maar
-- Supabase's advisor onderscheidt niet naar gevoeligheid — elke public-tabel zonder RLS telt mee.
ALTER TABLE public.schema_migrations ENABLE ROW LEVEL SECURITY;

-- avg-schema (AVG/persoonsgegevens) en planner-schema staan standaard niet in Supabase's
-- "Exposed schemas"-lijst (alleen `public` wordt automatisch via PostgREST ontsloten) — maar
-- krijgen RLS toch aangezet voor defense-in-depth en consistentie, tegen verwaarloosbare kosten.
ALTER TABLE avg.importlog ENABLE ROW LEVEL SECURITY;
ALTER TABLE avg.teambegeleiding ENABLE ROW LEVEL SECURITY;
ALTER TABLE planner.classificatiecorrectie ENABLE ROW LEVEL SECURITY;
ALTER TABLE planner.emailverwerking ENABLE ROW LEVEL SECURITY;
ALTER TABLE planner.geplandewedstrijden ENABLE ROW LEVEL SECURITY;
ALTER TABLE planner.herplanverzoeken ENABLE ROW LEVEL SECURITY;
