-- 051_syncjobs_attempt.sql — #1839: pogingsteller voor het begrensde herstel van onderbroken
-- sync-jobs. De claim (pending naar running) telt een poging; een afronding is gebonden aan het
-- pogingnummer, zodat een verouderde worker een herstarte poging niet kan overschrijven. Bestaande
-- rijen krijgen 0 (nog nooit geclaimd) en blijven ongewijzigd: afgeronde jobs worden niet aangeraakt.
-- Additief en idempotent; SQL Server-tegenhanger staat in Database/Script.PostDeployment1.sql.
ALTER TABLE public.syncjobs ADD COLUMN IF NOT EXISTS attempt INT NOT NULL DEFAULT 0;
