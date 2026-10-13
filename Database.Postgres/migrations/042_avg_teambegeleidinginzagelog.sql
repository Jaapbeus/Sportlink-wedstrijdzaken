-- 042_avg_teambegeleidinginzagelog.sql — inzagelog voor de contactgegevens van teambegeleiding (#1701).
-- SQL Server-tegenhanger: Database/avg/Tables/TeambegeleidingInzageLog.sql + Database/Script.PostDeployment1.sql.
--
-- Eén rij per opvraging van GET /api/beheer/teambegeleiding/{team}: wie (Entra object-ID als pseudoniem
-- + weergavenaam als momentopname, uit het Easy Auth-principal), wanneer (mta_inserted, UTC), welk team en
-- hoeveel contactkaarten dat opleverde. Bewust GEEN gegevens van de ingeziene begeleiders (geen naam,
-- e-mailadres, telefoonnummer of functie) en geen foreign key: de teambegeleiding wordt bij elke import
-- volledig vervangen, het log verloopt volgens zijn eigen termijn (12 maanden, timer CleanupTeambegeleiding;
-- termijn in Planner.Shared/Teambegeleiding/TeambegeleidingInzage.cs).
--
-- Additief: een nieuwe tabel die de vorige codeversie niet kent, dus veilig vóór de code uitgerold.
-- Casing: lowercase (ARCHITECTUUR-DATABASE-TIERS.md §3). Tijden TIMESTAMPTZ (UTC, §8).
-- Row-Level Security staat aan zonder policies (#1198): de FunctionApp-rol omzeilt RLS, Supabase's
-- anon/authenticated-rollen worden buitengesloten.

CREATE SCHEMA IF NOT EXISTS avg;

CREATE TABLE IF NOT EXISTS avg.teambegeleidinginzagelog (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clubcode VARCHAR(20) NOT NULL,
    inziendoorobjectid VARCHAR(64) NULL,
    inziendoornaam VARCHAR(200) NULL,
    team VARCHAR(100) NOT NULL,
    aantalrijen INTEGER NOT NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_avg_teambegeleidinginzagelog_club_datum
    ON avg.teambegeleidinginzagelog (clubcode, mta_inserted DESC);

ALTER TABLE avg.teambegeleidinginzagelog ENABLE ROW LEVEL SECURITY;
