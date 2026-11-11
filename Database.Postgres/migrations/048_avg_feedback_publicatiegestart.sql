-- 048_avg_feedback_publicatiegestart.sql — issue 1803: begin van de lopende publicatiepoging van een feedbackmelding.
--
-- Na een dubbelzinnige GitHub-POST blijft een melding op 'publiceren' staan en zoekt "opnieuw proberen" via de
-- GitHub-issuelijst naar een issue met de publicatiereferentie van de melding. De ondergrens van dat zoekvenster moet
-- vóór de eerste POST liggen en mag niet opschuiven als een herstelpoging mislukt (GitHub onbereikbaar, receipt niet
-- opgeslagen); anders valt het al aangemaakte issue buiten het venster en volgt een dubbel publiek issue.
-- mta_modified is daarvoor ongeschikt: de herclaim gebruikt het als slot en ververst het bij elke poging.
-- ClaimPublicatieAsync zet deze kolom; de herclaim laat haar staan. NULL = nog nooit geclaimd of geclaimd vóór deze
-- kolom (de code valt dan terug op mta_modified).
--
-- Expand/contract (docs/ARCHITECTUUR-DATABASE-TIERS.md §57): uitsluitend additief en nullable, dus de vorige code,
-- die vóór deze code draait, merkt er niets van. Geen nieuwe tabel, dus geen RLS-stap. Identifier-casing: lowercase,
-- ongequote (§3). Tijden zijn TIMESTAMPTZ (UTC, §8).
-- SQL Server-tegenhanger: Database/avg/Tables/Feedback.sql + Database/Script.PostDeployment1.sql.
ALTER TABLE avg.feedback
    ADD COLUMN IF NOT EXISTS publicatiegestartoputc TIMESTAMPTZ NULL;
