-- 044_teams_categorie.sql — issue 1787: KNVB-verplaatsingscategorie per team (A of B), alvast voorbereid.
--
-- Het AI-model kende de categorie van een team niet en leidde die af uit de teamnaam; de KNVB-notitie
-- staat daarom niet meer in het antwoord aan de afzender. Zodra per team vastligt welke categorie geldt,
-- kan een KNVB-zin terugkomen, maar alleen als de categorie bekend is. Deze kolom legt dat vast. Nu leest
-- of schrijft nog geen code haar, en de teamcanonicalisatie (sync) laat haar ongemoeid, zodat een
-- handmatig gezette waarde blijft staan.
--
-- Expand/contract (docs/ARCHITECTUUR-DATABASE-TIERS.md §57): uitsluitend additief en nullable, dus de
-- vorige code, die vóór deze code draait, merkt er niets van. Bestaande rijen blijven NULL (= onbekend).
-- Geen nieuwe tabel, dus geen RLS-stap. Identifier-casing: lowercase, ongequote (§3).
-- SQL Server-tegenhanger: Database/dbo/Tables/Teams.sql + Database/Script.PostDeployment1.sql.
ALTER TABLE public.teams
    ADD COLUMN IF NOT EXISTS categorie VARCHAR(1) NULL
        CONSTRAINT ck_teams_categorie CHECK (categorie IN ('A', 'B'));
