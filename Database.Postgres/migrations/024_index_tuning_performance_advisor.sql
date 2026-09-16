-- 024_index_tuning_performance_advisor.sql — #1211
--
-- Drie indexcorrecties naar aanleiding van de Supabase Performance Advisor. Alle 22 meldingen van
-- die run zijn getoetst; alleen deze drie bleken een echt defect. De onderbouwing waarom de
-- overige negentien bewust ongemoeid blijven staat in docs/ARCHITECTUUR-DATABASE-TIERS.md §69 —
-- lees die eerst voordat je hier iets aan toevoegt naar aanleiding van een nieuwe advisor-run.
--
-- Alle drie de wijzigingen zijn veilig voor de vórige codeversie (§57): er wordt geen kolom,
-- type of constraint gewijzigd. De enige DROP betreft een index die geen enkele query kan
-- gebruiken (zie punt 1), dus ook de draaiende code verliest er niets door.
--
-- Bewust GEEN `CREATE INDEX CONCURRENTLY`: MigrationRunner voert elk migratiebestand binnen één
-- transactie uit en CONCURRENTLY is in een transactieblok niet toegestaan. De betrokken tabellen
-- zijn klein (honderden rijen), dus de kortstondige lock van een gewone CREATE INDEX is hier
-- verwaarloosbaar.

-- ---------------------------------------------------------------------------------------------
-- 1. teamaliassen: expressie-index in plaats van een index die per definitie dood is
-- ---------------------------------------------------------------------------------------------
-- Migratie 003 legde `ix_teamaliassen_club_genormaliseerd` aan op de kále kolom
-- (clubcode, ruwetekstgenormaliseerd). Migratie 007 (#820) zette daarna alle vergelijkingen op
-- deze kolom om naar UPPER(...), maar liet de index ongemoeid — terwijl dezelfde migratie
-- `public.teams` en `teamaliassen.ruwetekst` juist wél naar expressie-indexen omzette. Dit is dus
-- een overgebleven gat uit 007, geen nieuwe optimalisatie.
--
-- Een gewone b-tree op de kolom kan een predicaat `UPPER(kolom) = ...` niet bedienen. De index
-- werd daardoor nooit gebruikt — niet omdat de app stil is (wat de advisor concludeerde), maar
-- omdat het onmogelijk was. Alle drie de filterende plekken gebruiken UPPER():
--   - FunctionApp.Postgres/TeamResolution/TeamCandidateRepository.cs:53
--   - FunctionApp.Postgres/Planner/Repositories/PlannerMatchRepository.cs:66
-- (TeamCanonicalisatieService.cs:303 filtert alleen op clubcode en raakt deze kolom niet.)
--
-- Gemeten op Postgres 17, 60.000 rijen, met de ECHTE queryvorm uit beide bovenstaande plekken
-- (een OR over ruwetekst en ruwetekstgenormaliseerd), voor het meest voorkomende pad in
-- teamherkenning — een lookup die geen gevalideerde alias vindt:
--   voor  : Seq Scan, 14,50 ms, 609 buffers (alle 60.000 rijen gefilterd)
--   na    : BitmapOr over beide expressie-indexen, 0,046 ms, 6 buffers
--
-- De OR is hier het punt: Postgres kan hem alleen efficiënt afhandelen als BEIDE takken een
-- bruikbare index hebben. Zolang deze tak onbruikbaar was, viel het hele predicaat terug op een
-- volledige scan — de expressie-index op ruwetekst uit 007 leverde in deze query dus niets op.
-- Dat verklaart waarom dit lang onzichtbaar bleef.
--
-- Bij de huidige productieomvang (honderden rijen) is dat verschil niet merkbaar; de winst is dat
-- de dode schrijflast verdwijnt en de index meegroeit zodra de aliastabel dat doet.
DROP INDEX IF EXISTS public.ix_teamaliassen_club_genormaliseerd;

CREATE INDEX IF NOT EXISTS ix_teamaliassen_club_genormaliseerd_upper
    ON public.teamaliassen (clubcode, upper(ruwetekstgenormaliseerd));

-- ---------------------------------------------------------------------------------------------
-- 2. classificatiecorrectie: onindexeerde FK onder een bulk-retentie-delete
-- ---------------------------------------------------------------------------------------------
-- planner.classificatiecorrectie heeft twee foreign keys naar planner.emailverwerking(id):
--   - origineleverwerkingid   -> gedekt, want EERSTE kolom van de unique index
--                                classificatiecorrectie_origineleverwerkingid_correctionverw_key
--   - correctionverwerkingid  -> TWEEDE kolom van diezelfde index, en dus niet bruikbaar voor een
--                                lookup op alleen die kolom
--
-- Dat weegt hier zwaarder dan bij de andere FK-meldingen van de advisor, omdat de ouder-tabel een
-- bulk-delete kent — PostgresCleanupProcedures.cs:81:
--     DELETE FROM planner.emailverwerking WHERE mta_inserted < @verwijderVoor
-- Voor élke verwijderde ouderrij moet Postgres controleren of er nog een kind naar verwijst via
-- correctionverwerkingid. Zonder bruikbare index is dat een volledige scan van
-- classificatiecorrectie per verwijderde rij: O(verwijderde ouders x kindrijen). De
-- retentie-opschoning is precies de operatie die veel ouderrijen tegelijk verwijdert, dus dit
-- verslechtert met de omvang van de e-mailhistorie.
CREATE INDEX IF NOT EXISTS ix_classificatiecorrectie_correctionverwerkingid
    ON planner.classificatiecorrectie (correctionverwerkingid);

-- ---------------------------------------------------------------------------------------------
-- 3. geplandewedstrijden: FK-index op een tabel die per seizoen groeit
-- ---------------------------------------------------------------------------------------------
-- fk_geplandewedstrijden_velden (veldnummer -> public.velden) wordt niet gedekt door
-- ux_geplandewedstrijden_slot, want daar is veldnummer pas de vierde kolom.
--
-- Dit is de enige van de vier resterende FK-meldingen die meegenomen wordt. De reden is de
-- omvang van het KIND, niet die van de ouder: planner.geplandewedstrijden groeit per seizoen door,
-- terwijl veldbeschikbaarheid en veldtraining structureel begrensd zijn door velden x dagen van de
-- week (circa 63 rijen) — een FK-index daarop kan per definitie nooit iets opleveren en blijft
-- daarom achterwege. public.velden wordt in productiecode nergens verwijderd (alleen in tests),
-- dus dit is goedkope verzekering voor een toekomstige opschoon- of beheeractie, geen fix voor een
-- bestaand knelpunt.
CREATE INDEX IF NOT EXISTS ix_geplandewedstrijden_veldnummer
    ON planner.geplandewedstrijden (veldnummer);
