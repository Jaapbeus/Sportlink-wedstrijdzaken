-- #1360: Functie (bijv. "Trainer/coach") naast Teamrol, voor de badge op /teambegeleiding.
-- Nullable: exports van vóór deze wijziging hebben de kolom niet; bestaande rijen blijven NULL.
-- SQL Server-tegenhanger: Database/avg/Tables/Teambegeleiding.sql + Database/Script.PostDeployment1.sql.
ALTER TABLE avg.teambegeleiding
    ADD COLUMN IF NOT EXISTS functie VARCHAR(150) NULL;
