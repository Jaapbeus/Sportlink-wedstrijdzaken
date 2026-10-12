-- 041_emailverwerking_conversatie_index.sql — index voor opzoekingen per conversatie (#1674).
-- De antwoordlimiet telt de verzonden antwoorden in een conversatie en de replydetectie zoekt het
-- laatste antwoord in dezelfde conversatie; beide filteren op clubcode + conversationid.
-- Additief en niet-uniek (een conversatie heeft meerdere rijen), dus veilig voor de vorige codeversie.
-- Geen nieuwe tabel, dus geen RLS-wijziging (planner.emailverwerking heeft RLS al).
-- Bewust geen CONCURRENTLY: MigrationRunner draait elk bestand in één transactie; de tabel is klein
-- (bewaartermijn via de cleanup-procedure).
-- SQL Server-tegenhanger: Database/planner/Tables/EmailVerwerking.sql + Database/Script.PostDeployment1.sql.
CREATE INDEX IF NOT EXISTS ix_emailverwerking_club_conversatie
    ON planner.emailverwerking (clubcode, conversationid);
