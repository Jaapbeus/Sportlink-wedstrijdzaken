-- 003_speeltijden_kolommen.sql — #893: public.speeltijden mistte drie kolommen ten opzichte van
-- dbo.Speeltijden (Database/dbo/Tables/Speeltijden.sql), ontdekt tijdens #887's vertaling van
-- AdminSpeeltijdenRepository. Zonder deze kolommen kan die repository niet functioneel
-- gelijkwaardig vertaald worden.
--
-- wedstrijdhelft/wedstrijdrust zijn NOT NULL in de SQL Server-tier — DEFAULT 0 hier is uitsluitend
-- nodig om ADD COLUMN op een tabel met bestaande rijen toe te staan, geen bewuste
-- business-default (elke rij die de applicatie zelf schrijft, vult beide altijd expliciet).
-- standaardvoorkeurtijd is nullable, zelfde semantiek als de SQL Server-kolom (#666: geen
-- streeftijd = planner valt terug op het eerst beschikbare slot).
--
-- Identifier-casing: lowercase, ongequote — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §3.

ALTER TABLE public.speeltijden
    ADD COLUMN IF NOT EXISTS wedstrijdhelft INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS wedstrijdrust INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS standaardvoorkeurtijd TIME NULL;
