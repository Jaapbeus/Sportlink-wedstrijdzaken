-- 052_syncjobs_attempt.sql — #1839: pogingsteller voor het begrensde herstel van onderbroken
-- sync-jobs. De claim (pending naar running) telt een poging; een afronding is gebonden aan het
-- pogingnummer, zodat een verouderde worker een herstarte poging niet kan overschrijven. Een bestaande
-- pending-job krijgt 0 (nog nooit geclaimd); een bestaande running-job is al één keer gestart en telt als
-- poging 1, anders zou het herstel hem een extra poging boven de limiet geven. Afgeronde jobs blijven ongewijzigd.
-- Additief en idempotent; SQL Server-tegenhanger staat in Database/Script.PostDeployment1.sql.
ALTER TABLE public.syncjobs ADD COLUMN IF NOT EXISTS attempt INT NOT NULL DEFAULT 0;
UPDATE public.syncjobs SET attempt = 1 WHERE status = 'running' AND attempt = 0;
