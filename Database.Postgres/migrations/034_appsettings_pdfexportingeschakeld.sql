-- #1459: clubinstelling "PDF-export". PDF-export (planning delen) gebruikt QuestPDF, dat onder de
-- Community-licentie alleen gratis is voor organisaties met minder dan 1 miljoen USD jaaromzet.
-- Standaard UIT (fail-closed) voor elke club, ook voor bestaande rijen: een beheerder bevestigt zelf
-- dat de voorwaarden gelden door de schakelaar aan te zetten. Bewust geen club-specifieke waarde.
-- SQL Server-tegenhanger: Database/dbo/Tables/AppSettings.sql + Database/Script.PostDeployment1.sql.
-- Geen nieuwe tabel, dus geen RLS-wijziging nodig (public.appsettings heeft RLS sinds 021).
ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS pdfexportingeschakeld BOOLEAN NOT NULL DEFAULT false;
