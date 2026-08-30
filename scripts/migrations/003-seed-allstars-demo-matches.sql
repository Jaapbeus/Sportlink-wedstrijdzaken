-- ============================================================
-- Migratie 003 — AllStars FC demo-teams en -wedstrijden (#856)
-- Issue: #856 — demodata-seed sloeg zichzelf stil over op een verse database
--
-- ACHTERGROND: his.teams/his.matches worden niet door Database/Script.PostDeployment1.sql
-- aangemaakt, maar dynamisch door de ETL bij de eerste Sportlink-sync
-- (FunctionApp/CreateTable.cs + sp_CreateTargetTableFromSource). Op een verse installatie bestaan
-- ze dus nog niet op het moment dat PostDeployment draait — de team- en wedstrijddemo voor de
-- democlub ALLSTARS kon daarom nooit in dat script staan zonder een stille no-op te worden
-- (de vorige aanpak: PRINT + RETURN, geen enkele foutmelding, geen exitcode). Optie B
-- (architectuurbesluit #856, 2026-08-30): deze demodata verhuist naar dit losse, expliciet aan te
-- roepen script — uit te voeren NADAT de eerste Sportlink-sync gelopen heeft (zodra his.teams/
-- his.matches dus daadwerkelijk bestaan).
--
-- UITVOEREN:
--   1. Zorg dat de eerste Sportlink-sync al gelopen is (his.teams/his.matches bestaan).
--   2. sqlcmd -S <server> -d <database> -i scripts/migrations/003-seed-allstars-demo-matches.sql
--
-- Faalt hard (RAISERROR, severity 16) als his.teams/his.matches nog niet bestaan — in
-- tegenstelling tot de vorige stille PRINT+RETURN-aanpak in Script.PostDeployment1.sql.
--
-- Volledig idempotent: elk blok is IF NOT EXISTS-gated, herhaald uitvoeren doet niets kwaads.
-- Speeldata worden relatief aan de uitvoerdatum berekend (niet hardcoded), zodat de demo nooit
-- veroudert naar alleen-verleden-wedstrijden.
--
-- AVG: uitsluitend fictieve gegevens conform de vastgelegde uitzondering in CLAUDE.md — voornamen
-- zonder achternaam en het gereserveerde .test-TLD (RFC 2606), dat publiek niet bestaat. Alleen
-- rijen met ClubCode = 'ALLSTARS'; nooit gebruikt voor een echte club.
-- ============================================================

DECLARE @DemoClub NVARCHAR(20) = 'ALLSTARS';

IF NOT EXISTS (SELECT 1 FROM [dbo].[AppSettings] WHERE [ClubCode] = @DemoClub)
BEGIN
    RAISERROR('AllStars-demodata (#856): democlub ALLSTARS bestaat niet in dbo.AppSettings — dit script doet niets. Voer eerst Script.PostDeployment1.sql uit.', 16, 1);
    RETURN;
END

IF OBJECT_ID('his.teams') IS NULL OR OBJECT_ID('his.matches') IS NULL
BEGIN
    RAISERROR('AllStars-demodata (#856): his.teams/his.matches bestaan nog niet — de eerste Sportlink-sync moet eerst lopen voordat dit script team- en wedstrijddemo kan seeden.', 16, 1);
    RETURN;
END
GO

DECLARE @DemoClub NVARCHAR(20) = 'ALLSTARS';

-- his.teams: twee teams per categorie. Set-based gegenereerd zodat de lijst compact blijft; de
-- leeftijdscategorieen sluiten aan op de sleutels in dbo.Speeltijden.
--
-- #853: teamcode/lokaleteamcode/poulecode expliciet gevuld met unieke, herkenbaar-fictieve waarden
-- (9000000+, dezelfde gereserveerde-demo-range als wedstrijdcode verderop in dit script). Deze drie
-- kolommen vormen de business-key voor de Postgres-tier (KnownEntities.Teams.businessKey);
-- Postgres' GENERATED ALWAYS bk_-kolom leidt zich af uit deze drie kolommen, dus met drie keer NULL
-- kreeg elk team dezelfde afgeleide sleutel en overleefde de unieke index er maar één. SQL Server's
-- eigen bk_teams-kolom blijft de leesbare CONCAT-vorm; deze drie kolommen zijn puur additioneel en
-- raken geen bestaande join of query (alleen [teamnaam] wordt verderop gebruikt om
-- AllStars-wedstrijden aan teams te koppelen).
IF NOT EXISTS (SELECT 1 FROM [his].[teams] WHERE [ClubCode] = @DemoClub)
    INSERT INTO [his].[teams]
        ([bk_teams], [teamcode], [lokaleteamcode], [poulecode], [teamnaam], [teamsoort],
         [geslacht], [leeftijdscategorie], [competitiesoort], [mta_inserted], [mta_modified],
         [ClubCode])
    SELECT
        CONCAT('ALLSTARS-', c.[Cat], '-', n.[Nr]),
        9000000 + ROW_NUMBER() OVER (ORDER BY c.[Cat], n.[Nr]),
        9000000 + ROW_NUMBER() OVER (ORDER BY c.[Cat], n.[Nr]),
        9000000 + ROW_NUMBER() OVER (ORDER BY c.[Cat], n.[Nr]),
        CONCAT('AllStars ', c.[Cat], ' ', n.[Nr]),
        c.[Soort], c.[Geslacht], c.[Leeftijd], 'regulier',
        GETUTCDATE(), GETUTCDATE(), @DemoClub
    FROM (VALUES
            ('JO8',   'JO8',  'Jeugd',    'Jongens'),
            ('JO9',   'JO9',  'Jeugd',    'Jongens'),
            ('JO10',  'JO10', 'Jeugd',    'Jongens'),
            ('JO11',  'JO11', 'Jeugd',    'Jongens'),
            ('JO12',  'JO12', 'Jeugd',    'Jongens'),
            ('JO13',  'JO13', 'Jeugd',    'Jongens'),
            ('JO14',  'JO14', 'Jeugd',    'Jongens'),
            ('JO15',  'JO15', 'Jeugd',    'Jongens'),
            ('JO17',  'JO17', 'Jeugd',    'Jongens'),
            ('JO19',  'JO19', 'Jeugd',    'Jongens'),
            ('MO13',  'MO13', 'Jeugd',    'Meisjes'),
            ('MO15',  'MO15', 'Jeugd',    'Meisjes'),
            ('Heren', '1-99', 'Senioren', 'Mannen'),
            ('VR',    'VR',   'Senioren', 'Vrouwen')
         ) AS c([Cat], [Leeftijd], [Soort], [Geslacht])
    CROSS JOIN (VALUES (1), (2)) AS n([Nr]);
GO

DECLARE @DemoClub NVARCHAR(20) = 'ALLSTARS';

-- avg.Teambegeleiding: een fictieve trainer per team. Voornaam zonder achternaam, .test-domein.
-- Het rijnummer in het e-mailadres houdt de adressen uniek zonder achternamen te verzinnen.
IF NOT EXISTS (SELECT 1 FROM [avg].[Teambegeleiding] WHERE [ClubCode] = @DemoClub)
    INSERT INTO [avg].[Teambegeleiding] ([Team], [Naam], [Emailadres], [Teamrol], [ClubCode])
    SELECT
        t.[teamnaam],
        v.[Naam],
        CONCAT(LOWER(v.[Naam]), '.',
               ROW_NUMBER() OVER (ORDER BY t.[teamnaam]), '@allstars-fc.test'),
        'Trainer',
        @DemoClub
    FROM [his].[teams] t
    CROSS APPLY (
        SELECT [Naam] FROM (VALUES
            ('Frenkie'), ('Bas'), ('Stef'), ('Peer'), ('Bram'), ('Ralf'), ('Gijs'),
            ('Jacco'), ('Sjaak'), ('Guus'), ('Ferry'), ('Nico'), ('Edwin'), ('Dirkje')
        ) AS namen([Naam])
        ORDER BY (SELECT NULL)
        OFFSET (ABS(CHECKSUM(t.[bk_teams])) % 14) ROWS FETCH NEXT 1 ROWS ONLY
    ) v
    WHERE t.[ClubCode] = @DemoClub;
GO

DECLARE @DemoClub NVARCHAR(20) = 'ALLSTARS';

-- his.matches: acht speelronden vanaf de eerstvolgende zaterdag, afwisselend thuis en uit. De
-- zaterdagberekening is onafhankelijk van SET DATEFIRST: 1900-01-01 was een maandag, dus
-- DATEDIFF % 7 geeft 0 = maandag en 5 = zaterdag. Wedstrijdcodes vanaf 9000001 overlappen niet met
-- echte Sportlink-codes.
IF NOT EXISTS (SELECT 1 FROM [his].[matches] WHERE [ClubCode] = @DemoClub)
BEGIN
    DECLARE @Vandaag DATE = CAST(GETDATE() AS DATE);
    DECLARE @Zaterdag1 DATE =
        DATEADD(DAY, (5 - (DATEDIFF(DAY, '19000101', @Vandaag) % 7) + 7) % 7, @Vandaag);

    -- De planner filtert thuiswedstrijden op m.[accommodatie] LIKE de AppSettings-waarde van de
    -- club (PlannerMatchRepository). Zonder die waarde vindt de Dagplanner niets.
    DECLARE @DemoAccommodatie NVARCHAR(200) =
        (SELECT [Accommodatie] FROM [dbo].[AppSettings] WHERE [ClubCode] = @DemoClub);

    INSERT INTO [his].[matches]
        ([bk_matches], [wedstrijdcode], [datum], [kaledatum], [wedstrijd], [aanvangstijd],
         [thuisteam], [uitteam], [status], [teamnaam], [competitiesoort],
         [accommodatie], [mta_inserted], [mta_modified], [ClubCode])
    SELECT
        CONCAT('ALLSTARS-', 9000000 + x.[Code]),
        9000000 + x.[Code],
        x.[Datum],
        -- kaledatum is de kolom waarop de planner filtert; datum alleen is niet genoeg.
        x.[Datum],
        CASE WHEN x.[Thuis] = 1
             THEN CONCAT(x.[Team], ' - Tegenstander ', x.[Ronde])
             ELSE CONCAT('Tegenstander ', x.[Ronde], ' - ', x.[Team]) END,
        x.[Tijd],
        CASE WHEN x.[Thuis] = 1 THEN x.[Team] ELSE CONCAT('Tegenstander ', x.[Ronde]) END,
        CASE WHEN x.[Thuis] = 1 THEN CONCAT('Tegenstander ', x.[Ronde]) ELSE x.[Team] END,
        'Te spelen',
        x.[Team],
        'regulier',
        -- Uitwedstrijden staan op het complex van de tegenstander, dus bewust NIET de eigen
        -- accommodatie: anders zou de planner ze als thuiswedstrijd meenemen in de bezetting.
        CASE WHEN x.[Thuis] = 1 THEN @DemoAccommodatie ELSE 'Sportpark Tegenstander' END,
        GETUTCDATE(), GETUTCDATE(), @DemoClub
    FROM (
        SELECT
            t.[teamnaam] AS [Team],
            r.[Ronde],
            ROW_NUMBER() OVER (ORDER BY t.[teamnaam], r.[Ronde]) AS [Code],
            DATEADD(WEEK, r.[Ronde] - 1, @Zaterdag1) AS [Datum],
            -- Thuis/uit wisselt per ronde EN per team, zodat op elke speeldag ongeveer de helft
            -- thuis speelt. Alleen op ronde alterneren zou alle teams op dezelfde dag thuis zetten
            -- - onrealistisch en niet in te plannen op drie velden.
            (r.[Ronde] + ROW_NUMBER() OVER (PARTITION BY r.[Ronde] ORDER BY t.[teamnaam])) % 2 AS [Thuis],
            CASE WHEN t.[teamsoort] = 'Senioren' THEN '14:30' ELSE '09:00' END AS [Tijd]
        FROM [his].[teams] t
        CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7), (8)) AS r([Ronde])
        WHERE t.[ClubCode] = @DemoClub
    ) AS x;
END
GO

PRINT 'AllStars-demodata (#856): teams, teambegeleiding en wedstrijden geseed (of al aanwezig).';
GO
