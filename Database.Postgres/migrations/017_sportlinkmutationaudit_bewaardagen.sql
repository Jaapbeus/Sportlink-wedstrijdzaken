-- 017_sportlinkmutationaudit_bewaardagen.sql — #1114 (epic #986)
--
-- Bewaartermijn (dagen) voor public.sportlinkmutationaudit (migratie 013). Die tabel legt bij
-- elke Sportlink-mutatiepoging een rij vast, inclusief het e-mailadres/UPN van de beheerder die de
-- actie triggerde (kolom triggerddoor) — een persoonsgegeven. Anders dan public.appsettingsaudit
-- (appsettingsauditbewaardagen, migratie 003, opgeruimd door CleanupAppSettingsAuditAsync) had deze
-- tabel geen retentiebeleid: rijen bleven voor altijd staan, in strijd met AVG art. 5 lid 1 sub e
-- (opslagbeperking). Gesignaleerd tijdens #998 als "apart issue", dit is dat issue.
--
-- DEFAULT 365 IS EEN UITGANGSPUNT, GEEN DEFINITIEF BELEID. Het doel van dit log is operationele
-- traceerbaarheid van wijzigingen die deze app in Sportlink doorvoert; één seizoen plus marge
-- volstaat daarvoor. Korter dan appsettingsaudit (730) omdat dit log per mutatie groeit, niet per
-- zeldzame instellingswijziging, en omdat Sportlinks eigen log de mutatie zelf óók bewaart. De
-- eigenaar (DPO-rol) stelt de definitieve termijn vast en past hem aan met een UPDATE op deze kolom
-- — geen redeploy nodig. Een numerieke default is geen club-specifieke waarde (CLAUDE.md).
--
-- Gelezen door Database.Postgres/PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync,
-- aangeroepen door FunctionApp.Postgres/Admin/CleanupSportlinkMutationAuditFunction (maandelijks).
-- Additief (ADD COLUMN IF NOT EXISTS): de vorige code draait ongestoord door (§57).

ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS sportlinkmutationauditbewaardagen INTEGER NOT NULL DEFAULT 365;
