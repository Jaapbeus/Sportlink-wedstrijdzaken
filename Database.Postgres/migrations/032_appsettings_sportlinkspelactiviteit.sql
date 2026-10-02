-- #1437: clubinstelling "Spelactiviteit" voor oefenwedstrijden die via de Sportlink Web Extension
-- worden aangemaakt: de omschrijving ("Veld - Zaterdag") of de IdTag ("SOCCER-VE-AL/SATURDAY") uit
-- Sportlinks lijst. Leeg = de spelactiviteit van het team, anders Sportlinks standaard.
-- SQL Server-tegenhanger: Database/dbo/Tables/AppSettings.sql + Database/Script.PostDeployment1.sql.
ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS sportlinkspelactiviteit VARCHAR(100) NULL;
