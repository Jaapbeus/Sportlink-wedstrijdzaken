-- 040_appsettings_zekerheidspoort.sql — clubinstelling "zekerheidspoort" (#1568, deel D).
-- Aan (standaard, ook voor bestaande rijen): een antwoord dat de beslissingstrace als onzeker of mislukt
-- beoordeelt gaat NIET automatisch naar de afzender maar krijgt status Review. Uit: gedrag van vóór #1568.
-- SQL Server-tegenhanger: Database/dbo/Tables/AppSettings.sql + Database/Script.PostDeployment1.sql.
-- Geen nieuwe tabel, dus geen RLS-wijziging nodig (public.appsettings heeft RLS sinds 021).
ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS zekerheidspoortactief BOOLEAN NOT NULL DEFAULT true;
