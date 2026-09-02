-- #820: teamherkenning moet zich onder Postgres identiek gedragen aan onder SQL Server.
-- Database/SportlinkSqlDb.sqlproj zet het volledige SQL Server-schema op de case-insensitive
-- default-collatie (1033, CI) — UNIQUE(ClubCode, Teamnaam) weigert daar dus vandaag al twee rijen
-- die alleen in hoofdlettergebruik verschillen. Postgres' default-collatie is case-sensitive: de
-- kale UNIQUE-constraints die 003_admin_tables.sql aanmaakte zouden zo'n duplicaat toelaten (een
-- data-integriteitsregressie) en FindExactTeamAsync/FindValidatedAliasAsync zouden bij afwijkende
-- opgeslagen casing stilzwijgend nul rijen teruggeven. Zie docs/ARCHITECTUUR-TEAMRESOLUTIE.md en
-- issue #820 voor de volledige analyse; empirisch bevestigd tegen een wegwerp-Postgres-16-container
-- (2026-08-31) — zowel het duplicaat-scenario als de ON CONFLICT-doeltreffendheid tegen de nieuwe
-- expression-based index.
--
-- Optie B uit de issue-analyse (expliciete UPPER()-vergelijking + expression-based unique index)
-- is gekozen boven citext/custom-collation: inspecteerbaar in codereview, geen per-database
-- aanzet-afhankelijkheid, portable naar een eventuele toekomstige tier.

ALTER TABLE public.teams DROP CONSTRAINT IF EXISTS teams_clubcode_teamnaam_key;
ALTER TABLE public.teams DROP CONSTRAINT IF EXISTS teams_clubcode_teamnaamgenormaliseerd_key;

CREATE UNIQUE INDEX IF NOT EXISTS ux_teams_club_teamnaam_upper
    ON public.teams (clubcode, upper(teamnaam));
CREATE UNIQUE INDEX IF NOT EXISTS ux_teams_club_teamnaamgenormaliseerd_upper
    ON public.teams (clubcode, upper(teamnaamgenormaliseerd));

-- RuweTekst bewust óók ge-upper't (niet alleen RuweTekstGenormaliseerd) — zelfde kanttekening als
-- FunctionApp/TeamResolution/TeamCandidateRepository.cs (#869/#820): onder de SQL Server-CI-
-- collatie is UQ_TeamAliassen_Club_RuweTekst vandaag al feitelijk hoofdletterongevoelig, dus dit
-- behoudt het waargenomen gedrag van vandaag in plaats van het te wijzigen.
ALTER TABLE public.teamaliassen DROP CONSTRAINT IF EXISTS teamaliassen_clubcode_ruwetekst_key;

CREATE UNIQUE INDEX IF NOT EXISTS ux_teamaliassen_club_ruwetekst_upper
    ON public.teamaliassen (clubcode, upper(ruwetekst));
