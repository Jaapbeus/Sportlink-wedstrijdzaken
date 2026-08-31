-- 009_geplandewedstrijden_mta_modified.sql — issue 890 (Postgres-vertaling van
-- MarkeerVervallenGeplandeWedstrijdenAsync). planner.geplandewedstrijden (001_baseline.sql) mist
-- mta_modified t.o.v. planner.GeplandeWedstrijden (Database/planner/Tables/GeplandeWedstrijden.sql)
-- — nodig om bij te houden wanneer een geplande wedstrijd als vervallen gemarkeerd is.
--
-- TIMESTAMPTZ, niet naïeve TIMESTAMP + timezone-wrap — zelfde besluit als #854, zie
-- docs/ARCHITECTUUR-DATABASE-TIERS.md §8.
--
-- Nog steeds niet gedekt door deze migratie (bewust, buiten scope van dit issue):
-- wedstrijdduurminuten, aangevraagddoor, opmerking, mta_inserted. Geen van deze kolommen is nodig
-- voor MarkeerVervallenGeplandeWedstrijdenAsync; ze horen bij de overige, nog niet vertaalde
-- GeplandeWedstrijden-functionaliteit (BevestigWedstrijd, HerplanVerzoeken, ...) uit #888's
-- resterende scope. Toevoegen zodra die functionaliteit daadwerkelijk vertaald wordt, niet
-- vooruitlopend hierop.
ALTER TABLE planner.geplandewedstrijden
    ADD COLUMN IF NOT EXISTS mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW();
