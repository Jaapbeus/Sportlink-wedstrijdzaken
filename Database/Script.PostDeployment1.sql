/*
Post-Deployment Script Template							
--------------------------------------------------------------------------------------
 This file contains SQL statements that will be appended to the build script.		
 Use SQLCMD syntax to include a file in the post-deployment script.			
 Example:      :r .\myfile.sql								
 Use SQLCMD syntax to reference a variable in the post-deployment script.		
 Example:      :setvar TableName MyTable							
               SELECT * FROM [$(TableName)]					
--------------------------------------------------------------------------------------
*/
-- New setup needed for the first time (#435: gebruik IF NOT EXISTS i.p.v. scalar subquery die faalt bij >1 rij)
--
-- #598: ClubCode wordt expliciet meegegeven met de neutrale placeholder 'CLUB'. Voorheen leunde deze
-- INSERT op een DEFAULT-constraint met een clubnaam erin; op een verse dacpac-deploy (waar
-- dbo.AppSettings.ClubCode NOT NULL is zonder default) faalde de INSERT juist daardoor.
-- De beheerder stelt de echte ClubCode in via Beheer → Instellingen.
--
-- Dynamische SQL omdat [ClubCode] op een pre-multi-club database nog niet bestaat: SQL Server bindt
-- kolomnamen bij batch-compilatie, dus een statische verwijzing zou de hele batch laten falen
-- (zelfde valkuil als #564).
IF NOT EXISTS (SELECT 1 FROM [dbo].[AppSettings])
BEGIN
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ClubCode')
        EXEC('
            INSERT INTO [dbo].[AppSettings]
                ([ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId], [SeasonStartMonth], [FetchSchedule])
            VALUES
                (''Uw clubnaam'', ''CLUB'', ''https://data.sportlink.com'', ''APIKEY'', 7, ''0 0 4 * * *'');
        ');
    ELSE
        INSERT INTO [dbo].[AppSettings]
            ([ClubName], [SportlinkApiUrl], [SportlinkClientId], [SeasonStartMonth], [FetchSchedule])
        VALUES
            ('Uw clubnaam', 'https://data.sportlink.com', 'APIKEY', 7, '0 0 4 * * *');
END
GO

-- ============================================================
-- Initiële referentie-/voorbeelddata voor de primaire club.
--
-- #598: alle onderstaande seeds geven [ClubCode] expliciet mee, gelezen uit dbo.AppSettings.
-- Voorheen leunden ze op een DEFAULT-constraint met een clubnaam erin — dat brak de multi-club
-- invariant én faalde op een verse dacpac-deploy waar die default niet bestaat.
--
-- Alles staat in dynamische SQL: op een pre-multi-club database bestaat de kolom [ClubCode] nog
-- niet en zou een statische verwijzing de hele batch laten falen op naam-binding (vgl. #564).
-- Op zulke databases zijn de tabellen al gevuld, dus de seeds worden overgeslagen.
-- ============================================================
-- Speeltijden: insert static reference data once
IF NOT EXISTS (SELECT 1 FROM [dbo].[Speeltijden])
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Speeltijden') AND name = 'ClubCode')
    EXEC('
    INSERT INTO [dbo].[Speeltijden] ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust], [ClubCode])
    SELECT v.[Leeftijd], v.[Veldafmeting], v.[WedstrijdTotaal], v.[WedstrijdHelft], v.[WedstrijdRust], a.[ClubCode]
    FROM (VALUES
        (''JO7'',  0.25, 50,  20, 10),
        (''JO8'',  0.25, 50,  20, 10),
        (''JO9'',  0.25, 50,  20, 10),
        (''JO10'', 0.25, 65,  25, 15),
        (''JO11'', 0.50, 75,  30, 15),
        (''JO12'', 0.50, 75,  30, 15),
        (''JO13'', 1.00, 75,  30, 15),
        (''JO14'', 1.00, 85,  35, 15),
        (''JO15'', 1.00, 85,  35, 15),
        (''JO16'', 1.00, 95,  40, 15),
        (''JO17'', 1.00, 95,  40, 15),
        (''JO18'', 1.00, 105, 45, 15),
        (''JO19'', 1.00, 105, 45, 15),
        (''JO23'', 1.00, 105, 45, 15),
        (''MO13'', 1.00, 75,  30, 15),
        (''MO15'', 1.00, 85,  35, 15),
        (''MO17'', 1.00, 95,  40, 15),
        (''MO19'', 1.00, 105, 45, 15),
        (''MO20'', 1.00, 105, 45, 15),
        (''VR'',   1.00, 115, 45, 15),
        (''G'',    0.50, 75,  30, 15),
        (''1-99'', 1.00, 115, 45, 15)
    ) AS v([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust])
    CROSS APPLY (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] ORDER BY [ClubCode]) a;
    ');
GO

-- Velden: voorbeeld-velddefinities voor de primaire club (bij multi-club handmatig per club inserten)
IF NOT EXISTS (SELECT 1 FROM [dbo].[Velden])
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Velden') AND name = 'ClubCode')
    EXEC('
    INSERT INTO [dbo].[Velden] ([VeldNummer], [VeldNaam], [VeldType], [HeeftKunstlicht], [Actief], [ClubCode])
    SELECT v.[VeldNummer], v.[VeldNaam], v.[VeldType], v.[HeeftKunstlicht], v.[Actief], a.[ClubCode]
    FROM (VALUES
        (1, ''veld 1'', ''kunstgras'',  1, 1),
        (2, ''veld 2'', ''kunstgras'',  1, 1),
        (3, ''veld 3'', ''kunstgras'',  1, 1),
        (4, ''veld 4'', ''kunstgras'',  1, 1),
        (5, ''veld 5'', ''natuurgras'', 0, 1),
        (6, ''veld 6'', ''natuurgras'', 0, 0)   -- niet functioneel
    ) AS v([VeldNummer], [VeldNaam], [VeldType], [HeeftKunstlicht], [Actief])
    CROSS APPLY (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] ORDER BY [ClubCode]) a;
    ');
GO

-- VeldBeschikbaarheid: voorbeeld-beschikbaarheid per dag van de week
-- DagVanWeek: 1=Monday, 2=Tuesday, ..., 6=Saturday, 7=Sunday
-- Vrijdag (5) en zondag (7): geen rijen = geen wedstrijden
IF NOT EXISTS (SELECT 1 FROM [dbo].[VeldBeschikbaarheid])
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.VeldBeschikbaarheid') AND name = 'ClubCode')
    EXEC('
    INSERT INTO [dbo].[VeldBeschikbaarheid] ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang], [ClubCode])
    SELECT v.[VeldNummer], v.[DagVanWeek], v.[BeschikbaarVanaf], v.[BeschikbaarTot], v.[GebruikZonsondergang], a.[ClubCode]
    FROM (VALUES
        -- maandag t/m donderdag: alleen veld 5, tot zonsondergang
        (5, 1, ''18:00'', ''22:00'', 1),
        (5, 2, ''18:00'', ''22:00'', 1),
        (5, 3, ''18:00'', ''22:00'', 1),
        (5, 4, ''18:00'', ''22:00'', 1),
        -- zaterdag: alle velden
        (1, 6, ''08:30'', ''22:00'', 0),
        (2, 6, ''08:30'', ''22:00'', 0),
        (3, 6, ''08:30'', ''22:00'', 0),
        (4, 6, ''08:30'', ''22:00'', 0),
        (5, 6, ''08:30'', ''17:00'', 0)
    ) AS v([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang])
    CROSS APPLY (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] ORDER BY [ClubCode]) a;
    ');
GO

-- TeamRegels: voorbeeld-uitzonderingen voor het standaardteam van de primaire club.
-- #598: de teamnaam wordt club-neutraal opgebouwd als "<ClubCode> 1" — nooit een hardcoded clubnaam.
IF NOT EXISTS (SELECT 1 FROM [dbo].[TeamRegels])
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TeamRegels') AND name = 'ClubCode')
    EXEC('
    INSERT INTO [dbo].[TeamRegels] ([TeamNaam], [RegelType], [WaardeMinuten], [Prioriteit], [Actief], [Opmerking], [ClubCode])
    SELECT a.[ClubCode] + '' 1'', v.[RegelType], v.[WaardeMinuten], v.[Prioriteit], v.[Actief], v.[Opmerking], a.[ClubCode]
    FROM (VALUES
        (''BufferVoor'', 60, 10, 1, ''1 uur voor de wedstrijd geen andere wedstrijden op hetzelfde veld''),
        (''BufferNa'',   30, 10, 1, ''30 min na de wedstrijd geen andere wedstrijden op hetzelfde veld'')
    ) AS v([RegelType], [WaardeMinuten], [Prioriteit], [Actief], [Opmerking])
    CROSS APPLY (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] ORDER BY [ClubCode]) a;
    ');
GO

-- AppSettings: UseRealtimeApi kolom toevoegen (idempotent)
IF NOT EXISTS (
    SELECT 1 FROM [sys].[columns]
    WHERE [object_id] = OBJECT_ID('[dbo].[AppSettings]') AND [name] = 'UseRealtimeApi'
)
BEGIN
    ALTER TABLE [dbo].[AppSettings] ADD [UseRealtimeApi] BIT NOT NULL DEFAULT 1
END
GO

-- AppSettings: FaviconUrl en LogoUrl toevoegen voor club-thema (#339, idempotent)
IF NOT EXISTS (
    SELECT 1 FROM [sys].[columns]
    WHERE [object_id] = OBJECT_ID('[dbo].[AppSettings]') AND [name] = 'FaviconUrl'
)
BEGIN
    ALTER TABLE [dbo].[AppSettings] ADD [FaviconUrl] NVARCHAR(2048) NULL
END
GO

IF NOT EXISTS (
    SELECT 1 FROM [sys].[columns]
    WHERE [object_id] = OBJECT_ID('[dbo].[AppSettings]') AND [name] = 'LogoUrl'
)
BEGIN
    ALTER TABLE [dbo].[AppSettings] ADD [LogoUrl] NVARCHAR(2048) NULL
END
GO

-- AppSettings: email-integratie velden vullen
IF EXISTS (SELECT 1 FROM [dbo].[AppSettings] WHERE [PlannerAfzenderNaam] IS NULL)
BEGIN
    UPDATE [dbo].[AppSettings]
    SET [PlannerAfzenderNaam] = 'Veldplanner',
        [CoordinatorFunctie] = N'Coördinator thuiswedstrijden'
    WHERE [PlannerAfzenderNaam] IS NULL
END
GO

-- KnvbKalenderDag: KNVB speeldagenkalender seizoen 2025/2026 (West + Landelijk)
-- Bron: https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/seizoensplanning/speeldagenkalenders
-- Geseed per regio+seizoen; her-runs zijn idempotent dankzij IF NOT EXISTS.
IF NOT EXISTS (SELECT 1 FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] = '2025/2026' AND [Regio] = 'West')
BEGIN
    INSERT INTO [dbo].[KnvbKalenderDag]
        ([Seizoen],[Regio],[Datum],[DagType],[HeeftSenioren],[HeeftJeugd],[HeeftMeiden],[PupillenToernooi],[Schoolvakantie],[Feestdag],[Opmerking],[Bron])
    VALUES
        -- Augustus / september 2025
        ('2025/2026','West','2025-08-16','Vrij',       0,0,0,0,'Z',  NULL,                  N'Volledig vrij',                                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-08-23','Beker',      0,1,0,0,'N',  NULL,                  N'Beker O23 categorie A',                                  'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-08-30','Beker',      1,1,1,0,'M',  NULL,                  N'Bekerpoule senioren+jeugd; start fase 1 meiden MO17/MO20', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-09-06','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+jeugd; meiden week 2 / start fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-09-13','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+jeugd',                              'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-09-19','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-09-20','Competitie', 1,1,1,0,NULL, NULL,                  N'R1 senioren; WD1 NJ jeugd',                              'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-09-27','Competitie', 1,1,1,0,NULL, NULL,                  N'R2 / WD2 NJ',                                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- Oktober 2025
        ('2025/2026','West','2025-10-03','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-10-04','Competitie', 1,1,1,0,NULL, NULL,                  N'R3 / WD3 NJ',                                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-10-11','Competitie', 1,1,1,0,'Z',  NULL,                  N'R4 / WD4 NJ; herfstvak Zuid 11-19 okt',                  'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-10-18','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek alle categorieen; herfstvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-10-25','Competitie', 1,1,1,0,'MN', NULL,                  N'R5 / WD5 NJ; herfstvak Midden+Noord',                    'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-10-31','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- November 2025
        ('2025/2026','West','2025-11-01','Competitie', 1,1,1,0,NULL, NULL,                  N'R6 / WD6 NJ; start fase 2 meiden+jeugd',                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-11-08','Competitie', 1,1,1,0,NULL, NULL,                  N'R7 / WD7 NJ',                                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-11-14','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-11-15','Competitie', 1,1,1,0,NULL, NULL,                  N'R8/Inh./Bek; WD8 NJ',                                    'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-11-22','Competitie', 1,1,1,0,NULL, NULL,                  N'R9/R8; WD9 NJ',                                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-11-28','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-11-29','Competitie', 1,1,1,0,NULL, NULL,                  N'R10/R9; WD10 NJ',                                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- December 2025
        ('2025/2026','West','2025-12-06','Competitie', 1,1,1,0,NULL, NULL,                  N'R11/R10; WD11 NJ',                                       'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-12-13','Inhaal',     1,1,1,0,NULL, NULL,                  N'Inh./Bek alle senioren+jeugd',                           'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2025-12-20','Vrij',       0,0,1,0,'MNZ',NULL,                  N'Kerstvakantie 20 dec - 4 jan; alleen meiden inhaal mogelijk', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- Januari 2026
        ('2025/2026','West','2026-01-10','Inhaal',     1,0,0,0,NULL, NULL,                  N'Inh./Bek senioren cat A; rest vrij',                     'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-01-17','Competitie', 1,1,1,0,NULL, NULL,                  N'R12 schema 14; rest Inh./Bek',                           'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-01-24','Competitie', 1,1,1,0,NULL, NULL,                  N'R13/R11; WD1 VJ start; beker O13-O19; start fase 3',     'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-01-31','Competitie', 1,1,1,0,NULL, NULL,                  N'R14/R12; WD1-2 VJ; fase 3',                              'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- Februari 2026
        ('2025/2026','West','2026-02-07','Competitie', 1,1,1,0,NULL, NULL,                  N'R15/R13; WD2-3 VJ; fase 3',                              'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-02-14','Competitie', 1,1,1,0,'MZ', NULL,                  N'Schema 12B R14 (Vak.regio N) of Inh./Bek; voorjaarsvak M+Z 14-22 feb (Carnaval)', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-02-21','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek alle; voorjaarsvakantie alle regio''s',         'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-02-28','Competitie', 1,1,1,0,'N',  NULL,                  N'R16/R14; WD3 VJ; voorjaarsvak Noord 21 feb-1 mrt',       'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- Maart 2026
        ('2025/2026','West','2026-03-06','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-03-07','Competitie', 1,1,1,0,NULL, NULL,                  N'R17/R15; WD4 VJ; fase 3',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-03-14','Competitie', 1,1,1,0,NULL, NULL,                  N'R18/R16; WD5 VJ; fase 3',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-03-21','Competitie', 1,1,1,0,NULL, NULL,                  N'R19/R17; WD6 VJ; fase 3',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-03-27','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-03-28','Competitie', 1,1,1,0,NULL, NULL,                  N'R20/R18; WD7 VJ; fase 3',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- April 2026
        ('2025/2026','West','2026-04-04','Inhaal',     1,1,1,0,NULL, N'Paaszaterdag',       N'Inh./Bek senioren+jeugd; Vrij/Bek meiden+JunB',          'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-04-06','Feestdag',   1,0,0,0,NULL, N'2e Paasdag',         N'Inh./Bek schema 14/12; rest geen wedstrijden',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-04-11','Competitie', 1,1,1,0,NULL, NULL,                  N'R21/R19; WD8 VJ; start fase 4 pupillen',                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-04-17','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-04-18','Competitie', 1,1,1,0,NULL, NULL,                  N'R22/R20; WD9 VJ; fase 3',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-04-25','Competitie', 1,1,1,0,'M',  NULL,                  N'R23 schema 14; rest Inh./Bek; meivak 25 apr-3 mei',      'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- Mei 2026
        ('2025/2026','West','2026-05-02','Inhaal',     1,1,1,0,'M',  NULL,                  N'Inh./Bek alle; meivak einde',                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-08','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-09','Competitie', 1,1,1,0,NULL, NULL,                  N'R24/R21; WD10 VJ; fase 3',                               'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-13','Inhaal',     1,0,0,0,NULL, N'Bekerfinale 1e elftallen mannen', N'Inhaal schema 12B (woensdag)',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-14','Inhaal',     1,1,0,0,NULL, N'Hemelvaartsdag',     N'Inhaal schema 14/12 + O23 cat A (donderdag)',            'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-16','Competitie', 1,1,1,0,NULL, NULL,                  N'R25/R22; WD11 VJ; fase 3',                               'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-23','Competitie', 1,1,1,0,NULL, N'Pinksterzaterdag',   N'Zat: R26 schema 14; NC schema 12; WD14 VJ',              'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-25','Feestdag',   1,0,0,0,NULL, N'2e Pinksterdag',     N'Zon: R26 schema 14; NC schema 12',                       'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-29','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-05-30','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren; jeugd inhaal; finales districtsbeker; fase 4 pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        -- Juni 2026
        ('2025/2026','West','2026-06-06','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren; final league meiden+O23',                   'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026'),
        ('2025/2026','West','2026-06-13','NC',         1,0,0,0,NULL, NULL,                  N'NC alleen senioren',                                     'https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026');
END
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] = '2025/2026' AND [Regio] = 'Landelijk')
BEGIN
    INSERT INTO [dbo].[KnvbKalenderDag]
        ([Seizoen],[Regio],[Datum],[DagType],[HeeftSenioren],[HeeftJeugd],[HeeftMeiden],[PupillenToernooi],[Schoolvakantie],[Feestdag],[Opmerking],[Bron])
    VALUES
        -- Augustus / september 2025
        ('2025/2026','Landelijk','2025-08-16','Competitie', 1,0,0,0,'Z',  NULL,                 N'2e/3e divisie ronde 1',                                  'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-08-23','Competitie', 1,1,0,0,'N',  NULL,                 N'2e/3e + 4e divisie; jeugd: Beker Jeugdcup',              'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-08-30','Competitie', 1,1,1,0,'M',  NULL,                 N'Divisies + Bekerpoule sen mannen; Q1 Beker BV vrouwen; landelijke jeugd start', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-09-06','Competitie', 1,1,1,0,NULL, NULL,                 N'Divisies + bekerpoule + Q2 Beker BV vrouwen',            'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-09-13','Competitie', 1,1,0,0,NULL, NULL,                 N'Divisies + bekerpoule sen mannen',                       'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-09-20','Competitie', 1,1,1,0,NULL, NULL,                 N'Divisies + sen mannen/vrouwen ronde 1',                  'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-09-27','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 7/6/2',                                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- Oktober 2025
        ('2025/2026','Landelijk','2025-10-04','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 8/7/3',                                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-10-11','Competitie', 1,1,1,0,'Z',  NULL,                 N'Ronde 9/8/4; herfstvak Zuid',                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-10-18','Inhaal',     1,1,1,0,'MNZ',NULL,                 N'Inh./Bek alle; herfstvak alle regio''s',                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-10-25','Competitie', 1,1,1,0,'MN', NULL,                 N'Ronde 11/9/5; herfstvak Midden+Noord',                   'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- November 2025
        ('2025/2026','Landelijk','2025-11-01','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 10/6; 2e/3e div inhaal',                           'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-11-08','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 12/11/7',                                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-11-15','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 13/12/8',                                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-11-22','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 14/9; 4e div inhaal',                              'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-11-29','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 15/13/10; jeugd inhaal',                           'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- December 2025
        ('2025/2026','Landelijk','2025-12-06','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 16/14/11',                                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-12-13','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 17/15; senioren Inh./Bek; jeugd 14',               'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-12-20','Inhaal',     1,1,0,0,'MNZ',NULL,                 N'2e/3e div inhaal + 4e div Inh./Bek; senioren vrij; kerstvak start', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2025-12-27','Vrij',       0,0,0,0,'MNZ',NULL,                 N'Kerstvakantie',                                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- Januari 2026
        ('2025/2026','Landelijk','2026-01-03','Vrij',       0,0,0,0,'MNZ',NULL,                 N'Kerstvakantie',                                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-01-10','Competitie', 1,0,1,0,NULL, NULL,                 N'2e/3e div ronde 18; sen mannen/vrouwen Inh./Bek; rest vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-01-17','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 19/16; jeugd ronde 1; senioren Inh./Bek',          'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-01-24','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 20/17; jeugd ronde 2; senioren Inh./Bek',          'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-01-31','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 21/18/12; jeugd Beker 1/8 finale',                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- Februari 2026
        ('2025/2026','Landelijk','2026-02-07','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 22/19/13',                                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-02-14','Competitie', 1,1,1,0,'MZ', NULL,                 N'Senioren ronde 14 of Inh./Bek; voorjaarsvak M+Z (Carnaval)', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-02-21','Competitie', 1,1,1,0,'MNZ',NULL,                 N'2e/3e div ronde 23; jeugd Beker 1/4 finale; voorjaarsvak alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-02-28','Competitie', 1,1,1,0,'N',  NULL,                 N'Ronde 24/20; senioren ronde 14 of Inh.; voorjaarsvak Noord', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- Maart 2026
        ('2025/2026','Landelijk','2026-03-07','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 25/21/15',                                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-03-14','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 26/22/16',                                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-03-21','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 27/23/17',                                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-03-28','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 28/24/18',                                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- April 2026
        ('2025/2026','Landelijk','2026-04-04','Inhaal',     1,1,1,0,NULL, N'Paaszaterdag',      N'Inh./Bek alle; jeugd Beker 1/2 finale',                  'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-04-06','Feestdag',   1,0,1,0,NULL, N'2e Paasdag',        N'Inh./Bek senioren mannen+vrouwen',                       'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-04-11','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 29/25/19; jeugd ronde 9',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-04-18','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 30/26/20; jeugd ronde 10',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-04-25','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 31/27; senioren Inh./Bek; jeugd Bekerfinale',      'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- Mei 2026
        ('2025/2026','Landelijk','2026-05-02','Competitie', 1,1,1,0,NULL, NULL,                 N'Jeugd ronde 11; rest Inh./Bek',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-05-09','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 32/28/21; jeugd ronde 12',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-05-13','Feestdag',   1,0,0,0,NULL, N'Bekerfinale 1e elftallen', N'Bekerfinale 4e divisie (woe/do 13-14 mei)',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-05-14','Inhaal',     1,1,0,0,NULL, N'Hemelvaartsdag',    N'Inhaal 2e/3e div + jeugd; bekerfinale 4e div',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-05-16','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 33/29/22; jeugd ronde 13',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-05-23','Competitie', 1,1,1,0,NULL, N'Pinksterzaterdag',  N'Ronde 34/30 divisies; NC senioren; jeugd inhaal',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-05-25','NC',         1,0,1,0,NULL, N'2e Pinksterdag',    N'NC senioren mannen+vrouwen',                             'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-05-30','NC',         1,1,1,0,NULL, NULL,                 N'NC alle senioren+divisies; jeugd ronde 14',              'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        -- Juni 2026
        ('2025/2026','Landelijk','2026-06-06','NC',         1,1,1,0,NULL, NULL,                 N'NC alle senioren; finale divisie 1 jeugd',               'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026'),
        ('2025/2026','Landelijk','2026-06-13','NC',         1,0,0,0,NULL, NULL,                 N'NC alleen senioren',                                     'https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026');
END
GO

-- ============================================================
-- #521: KnvbKalenderDag seizoen 2026/2027 (West + Landelijk)
-- Bron: https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/seizoensplanning/speeldagenkalenders
-- Weekendrijen gebruiken de zaterdagdatum; vrijdagrijen zijn pupillen 7x7-toernooien.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] = '2026/2027' AND [Regio] = 'West')
BEGIN
    INSERT INTO [dbo].[KnvbKalenderDag]
        ([Seizoen],[Regio],[Datum],[DagType],[HeeftSenioren],[HeeftJeugd],[HeeftMeiden],[PupillenToernooi],[Schoolvakantie],[Feestdag],[Opmerking],[Bron])
    VALUES
        -- Augustus / september 2026
        ('2026/2027','West','2026-08-15','Vrij',       0,0,0,0,'N',  NULL,                  N'Volledig vrij; schoolvak. Noord t/m 16 aug',              'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-08-22','Vrij',       0,0,0,0,'Z',  NULL,                  N'Volledig vrij; schoolvak. Zuid t/m 23 aug',               'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-08-29','Beker',      1,1,1,0,'M',  NULL,                  N'Bekerpoule senioren+junioren; Beker KO O23 cat A; start fase 1 meiden div+hoofdkl', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-09-05','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23 cat A; Beker KO O23 cat B; start fase 1 junioren+pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-09-12','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23; meiden week 3 / fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-09-18','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-09-19','Competitie', 1,1,1,0,NULL, NULL,                  N'Start competitie: WD senioren; WD NJ O23+junioren; meiden week 4 / fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-09-26','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden week 5 / fase 1',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- Oktober 2026
        ('2026/2027','West','2026-10-02','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag, week 1)',                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-10-03','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden week 6 / fase 1',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-10-09','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag, week 2)',                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-10-10','Competitie', 1,1,1,0,'N',  NULL,                  N'WD senioren; junioren inhaal; meiden week 7 / inhaal; herfstvak. Noord 10-18 okt', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-10-17','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. senioren+O23+junioren cat A; meiden inhaal; herfstvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-10-24','Competitie', 1,1,0,0,'MZ', NULL,                  N'WD senioren; WD NJ O23; start fase 2 junioren cat A+pupillen; meiden vrij; herfstvak. M+Z 17-25 okt', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-10-30','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-10-31','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden div fase 2 / hoofdkl fase 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- November 2026
        ('2026/2027','West','2026-11-07','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-11-13','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-11-14','Competitie', 1,1,1,0,NULL, NULL,                  N'WD schema 14; Inh./Bek. schema 12; WD NJ O23+junioren; meiden fase 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-11-21','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-11-27','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-11-28','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- December 2026
        ('2026/2027','West','2026-12-05','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-12-12','Competitie', 1,1,1,0,NULL, NULL,                  N'Laatste speelronde najaar: WD schema 14 cat A; rest Inh./Bek.; pupillen uitwijk', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2026-12-19','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. schema 14 cat A + O23 cat A + junioren cat B; meiden hoofdkl inhaal; kerstvakantie 19 dec-3 jan', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- Januari 2027
        ('2026/2027','West','2027-01-09','Vrij',       0,0,0,0,NULL, NULL,                  N'Volledig vrij',                                           'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-01-16','Competitie', 1,1,0,0,NULL, NULL,                  N'Start voorjaar cat B: WD schema 14 cat B; Inh./Bek. schema 12 cat B; Beker O23 cat A; junioren cat B inhaal', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-01-23','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren (Inh./Bek. schema 12 cat A); WD VJ O23 cat A; beker junioren; start fase 3 meiden+pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-01-30','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23; beker junioren; meiden+pupillen fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- Februari 2027
        ('2026/2027','West','2027-02-06','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; start fase 3 junioren districtscomp.; carnavalsweekend', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-02-13','Competitie', 1,1,1,0,'Z',  NULL,                  N'WD cat B senioren; Inh./Bek. cat A; WD VJ O23 cat B+junioren; voorjaarsvak. Zuid 13-21 feb', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-02-20','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. alle categorieen; meiden inhaal; voorjaarsvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-02-27','Competitie', 1,1,1,0,'MN', NULL,                  N'WD cat A senioren; Inh./Bek. cat B+junioren; WD VJ O23 cat A; voorjaarsvak. Noord+Midden 20-28 feb', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- Maart 2027
        ('2026/2027','West','2027-03-06','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-03-13','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-03-19','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-03-20','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-03-27','Inhaal',     1,1,1,0,NULL, N'Paaszaterdag',       N'Inh./Bek. cat A senioren+O23; Vrij/Bek. cat B; meiden inhaal; junioren Inh./Bek.', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-03-29','Feestdag',   1,0,0,0,NULL, N'2e Paasdag',         N'Inh./Bek. cat A senioren; Vrij/Bek. cat B senioren; rest geen wedstrijden', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- April 2027
        ('2026/2027','West','2027-04-02','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-04-03','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; start fase 4 pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-04-10','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-04-16','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-04-17','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-04-24','Competitie', 1,1,1,0,'MNZ',NULL,                  N'WD schema 14; Inh./Bek. schema 12+junioren; WD VJ O23 cat A; meivakantie 24 apr-2 mei', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- Mei 2027
        ('2026/2027','West','2027-05-01','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. alle categorieen; evt. finale bekerkampioenschap standaardteams; meivakantie', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-05-06','Feestdag',   1,0,0,0,NULL, N'Hemelvaartsdag',     N'Evt. bekerfinale standaardteams; rest geen wedstrijden',   'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-05-07','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-05-08','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-05-15','Competitie', 1,1,1,0,NULL, N'Pinksterzaterdag',   N'WD cat A senioren + WD (zat) schema 14 cat B; Inh./Bek. O23+junioren; meiden inhaal', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-05-17','Feestdag',   1,1,0,0,NULL, N'2e Pinksterdag',     N'WD cat A senioren + WD (zon) schema 14 cat B; Inh./Bek. schema 12 cat B + junioren cat A', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-05-21','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-05-22','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-05-29','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren cat A; inhaal cat B; WD VJ O23 cat A+junioren; meiden hoofdkl fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        -- Juni 2027
        ('2026/2027','West','2027-06-05','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren cat A; Inh./Bek. cat B; beker O23+junioren; final league meiden; finales districtsbeker', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-06-12','NC',         1,0,0,0,NULL, NULL,                  N'NC alleen senioren cat A',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027'),
        ('2026/2027','West','2027-06-19','NC',         1,0,0,0,NULL, NULL,                  N'NC alleen senioren cat A',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027');
END
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] = '2026/2027' AND [Regio] = 'Landelijk')
BEGIN
    INSERT INTO [dbo].[KnvbKalenderDag]
        ([Seizoen],[Regio],[Datum],[DagType],[HeeftSenioren],[HeeftJeugd],[HeeftMeiden],[PupillenToernooi],[Schoolvakantie],[Feestdag],[Opmerking],[Bron])
    VALUES
        -- Augustus / september 2026
        ('2026/2027','Landelijk','2026-08-08','Vrij',       0,0,0,0,NULL, NULL,                 N'Geen competitie',                                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-08-15','Competitie', 1,0,0,0,'N',  NULL,                 N'2e/3e divisie ronde 1; schoolvak. Noord t/m 16 aug',      'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-08-22','Competitie', 1,1,0,0,'Z',  NULL,                 N'2e/3e div ronde 2; O23 inhaal; jeugd inhaal/Jeugdcup; schoolvak. Zuid t/m 23 aug', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-08-29','Competitie', 1,1,1,0,'M',  NULL,                 N'2e/3e div ronde 3; 4e div ronde 1; bekerpoule vrouwen 1e klassen; O23+jeugd ronde 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-09-05','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 4/2; Beker Q1 vrouwen top+hoofdklasse; bekerpoule vrouwen 1e klassen; O23+jeugd ronde 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-09-12','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 5/3; Beker Q2 vrouwen top+hoofdklasse; bekerpoule vrouwen 1e klassen; O23+jeugd ronde 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-09-19','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 6/4; vrouwen ronde 1; O23+jeugd ronde 4',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-09-26','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 7/5; vrouwen ronde 2; O23+jeugd ronde 5',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- Oktober 2026
        ('2026/2027','Landelijk','2026-10-03','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 8/6; vrouwen ronde 3; O23+jeugd ronde 6',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-10-10','Competitie', 1,1,1,0,'N',  NULL,                 N'Ronde 9/7; vrouwen ronde 4; O23+jeugd ronde 7; herfstvak. Noord', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-10-17','Competitie', 1,1,1,0,'MNZ',NULL,                 N'2e/3e div ronde 10; rest Inh./Bek.; jeugd inhaal/Jeugdcup; herfstvak. alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-10-24','Competitie', 1,1,1,0,'MZ', NULL,                 N'Ronde 11/8; vrouwen Inh./Bek.; O23+jeugd ronde 8; herfstvak. Midden en Zuid', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-10-31','Competitie', 1,1,1,0,NULL, NULL,                 N'2e/3e div inhaal; 4e div ronde 9; vrouwen ronde 5; O23+jeugd ronde 9', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- November 2026
        ('2026/2027','Landelijk','2026-11-07','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 12/10; vrouwen ronde 6; O23+jeugd ronde 10',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-11-14','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 13/11; vrouwen ronde 7; O23+jeugd ronde 11',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-11-21','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 14/12; vrouwen ronde 8; O23+jeugd ronde 12',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-11-28','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 15/13; vrouwen ronde 9; O23 inhaal; jeugd inhaal/Jeugdcup', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- December 2026
        ('2026/2027','Landelijk','2026-12-05','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 16/14; vrouwen ronde 10; O23+jeugd ronde 13',       'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-12-12','Competitie', 1,1,1,0,NULL, NULL,                 N'Laatste speelronde najaar (verplaatsingsdeadline 13 dec 2026): ronde 17/15; vrouwen Inh./Bek.; O23+jeugd ronde 14', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-12-19','Inhaal',     1,1,0,0,'MNZ',NULL,                 N'Inhaalmoment uitsluitend voor calamiteiten of gelijktijdige laatste speelronde; vrouwen vrij; kerstvakantie', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2026-12-26','Vrij',       0,0,0,0,'MNZ',NULL,                 N'Kerstvakantie',                                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- Januari 2027
        ('2026/2027','Landelijk','2027-01-02','Vrij',       0,0,0,0,'MNZ',NULL,                 N'Kerstvakantie',                                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-01-09','Competitie', 1,0,0,0,NULL, NULL,                 N'2e/3e divisie ronde 18; rest vrij',                      'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-01-16','Competitie', 1,1,1,0,NULL, NULL,                 N'2e/3e div ronde 19; vrouwen Inh./Bek.; O23+jeugd voorjaar ronde 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-01-23','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 20/16; vrouwen ronde 11; O23+jeugd ronde 2',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-01-30','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 21/17; vrouwen ronde 12; O23+jeugd ronde 3',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- Februari 2027
        ('2026/2027','Landelijk','2027-02-06','Inhaal',     1,1,1,0,NULL, NULL,                 N'Inhaal alle divisies; vrouwen Inh./Bek.; jeugd inhaal/Jeugdcup; carnavalsweekend', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-02-13','Competitie', 1,1,1,0,'Z',  NULL,                 N'Ronde 22/18; vrouwen ronde 13; O23+jeugd inhaal; voorjaarsvak. Zuid', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-02-20','Competitie', 1,1,1,0,'MNZ',NULL,                 N'Ronde 23/19; vrouwen ronde 14; O23 inhaal; Jeugdcup kwartfinale; voorjaarsvak. alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-02-27','Competitie', 1,1,1,0,'M',  NULL,                 N'Ronde 24/20; vrouwen Inh./Bek.; O23+jeugd ronde 4; voorjaarsvak. Midden/West', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- Maart 2027
        ('2026/2027','Landelijk','2027-03-06','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 25/21; vrouwen ronde 15; O23+jeugd ronde 5',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-03-13','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 26/22; vrouwen ronde 16; O23+jeugd ronde 6',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-03-20','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 27/23; vrouwen ronde 17; O23+jeugd ronde 7',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-03-27','Inhaal',     1,1,1,0,NULL, N'Paaszaterdag',      N'Inhaal alle divisies; vrouwen Inh./Bek.; jeugd inhaal/Jeugdcup; paasweekend', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- April 2027
        ('2026/2027','Landelijk','2027-04-03','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 28/24; vrouwen ronde 18; O23+jeugd ronde 8',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-04-10','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 29/25; vrouwen ronde 19; O23+jeugd ronde 9',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-04-17','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 30/26; vrouwen ronde 20; O23+jeugd ronde 10',       'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-04-24','Competitie', 1,1,1,0,'MNZ',NULL,                 N'Ronde 31/27; vrouwen Inh./Bek.; O23+jeugd ronde 11; meivakantie', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- Mei 2027
        ('2026/2027','Landelijk','2027-05-01','Inhaal',     1,1,1,0,'MNZ',NULL,                 N'Inhaal alle divisies; vrouwen Inh./Bek.; Jeugdcup finale; meivakantie', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-05-06','Inhaal',     1,1,1,0,NULL, N'Hemelvaartsdag',    N'Midweeks inhaalmoment 5-6 mei; vrouwen Inh./Bek.',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-05-08','Competitie', 1,1,1,0,NULL, NULL,                 N'Ronde 32/28; vrouwen ronde 21; O23+jeugd ronde 12',       'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-05-15','Competitie', 1,1,1,0,NULL, N'Pinksterzaterdag',  N'Speelweekend 15-17 mei: ronde 33/29; vrouwen ronde 22; O23+jeugd ronde 13', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-05-22','Competitie', 1,1,1,0,NULL, NULL,                 N'Laatste inhaalmoment voorjaar 23 mei 2027: ronde 34/30; vrouwen NC; O23+jeugd inhaal', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-05-26','NC',         1,0,1,0,NULL, NULL,                 N'NC divisies mannen + vrouwen (woensdag)',                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-05-29','NC',         1,1,1,0,NULL, NULL,                 N'NC divisies + vrouwen; O23+jeugd ronde 14',               'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        -- Juni 2027
        ('2026/2027','Landelijk','2027-06-03','NC',         1,0,1,0,NULL, NULL,                 N'NC divisies mannen + vrouwen (donderdag)',                'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-06-05','NC',         1,1,1,0,NULL, NULL,                 N'NC divisies + vrouwen; finale divisie 1 O23+jeugd',       'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-06-09','NC',         1,0,1,0,NULL, NULL,                 N'NC divisies mannen + vrouwen (woensdag)',                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027'),
        ('2026/2027','Landelijk','2027-06-12','NC',         1,0,1,0,NULL, NULL,                 N'NC divisies mannen + vrouwen',                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027');
END
GO

-- ============================================================
-- #71: KnvbKalenderDag seizoen 2026/2027 — resterende regio's
-- Noord, Oost, Zuid en LandelijkJeugd. Samen met West + Landelijk (#521)
-- dekt dit alle zes KNVB-kalenders voor 2026/2027.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] = '2026/2027' AND [Regio] = 'Noord')
BEGIN
    INSERT INTO [dbo].[KnvbKalenderDag]
        ([Seizoen],[Regio],[Datum],[DagType],[HeeftSenioren],[HeeftJeugd],[HeeftMeiden],[PupillenToernooi],[Schoolvakantie],[Feestdag],[Opmerking],[Bron])
    VALUES
        -- Augustus / september 2026
        ('2026/2027','Noord','2026-08-15','Vrij',       0,0,0,0,'N',  NULL,                  N'Volledig vrij; schoolvak. Noord t/m 16 aug',               'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-08-22','Beker',      0,1,0,0,'Z',  NULL,                  N'Alleen Beker KO O23 cat A; rest vrij; schoolvak. Zuid t/m 23 aug', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-08-29','Beker',      1,1,1,0,'M',  NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23 cat A; meiden vrij/inhaal', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-09-05','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23 cat A; start fase 1 meiden+junioren+pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-09-12','Competitie', 1,1,1,0,NULL, NULL,                  N'WD schema 14+12 cat A; bekerpoule cat B+junioren; meiden fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-09-18','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-09-19','Competitie', 1,1,1,0,NULL, NULL,                  N'Start competitie alle categorieen: WD senioren; WD NJ O23+junioren; meiden fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-09-26','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 1',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- Oktober 2026
        ('2026/2027','Noord','2026-10-02','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-10-03','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 1',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-10-10','Competitie', 1,1,1,0,'N',  NULL,                  N'WD senioren; WD NJ O23; meiden+junioren inhaal; pupillen vrij; herfstvak. Noord 10-18 okt', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-10-17','Inhaal',     1,1,0,0,'MNZ',NULL,                  N'Inh./Bek. senioren+O23+junioren cat A; meiden vrij; herfstvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-10-24','Competitie', 1,1,1,0,'MZ', NULL,                  N'WD senioren; WD NJ O23+junioren; start fase 2 meiden+junioren+pupillen; herfstvak. M+Z 17-25 okt', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-10-30','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-10-31','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden div fase 2 / hoofdkl fase 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- November 2026
        ('2026/2027','Noord','2026-11-07','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-11-13','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-11-14','Competitie', 1,1,1,0,NULL, NULL,                  N'WD cat A; Inh./Bek. schema 12 cat B; WD NJ O23+junioren; meiden fase 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-11-21','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-11-27','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-11-28','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- December 2026
        ('2026/2027','Noord','2026-12-05','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-12-12','Inhaal',     1,1,1,0,NULL, NULL,                  N'Inh./Bek. alle senioren+O23+junioren; meiden inhaal; pupillen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2026-12-19','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. schema 14 cat A + cat B + O23 + junioren; meiden div inhaal; kerstvakantie 19 dec-3 jan', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- Januari 2027
        ('2026/2027','Noord','2027-01-09','Vrij',       0,0,0,0,NULL, NULL,                  N'Volledig vrij',                                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-01-16','Inhaal',     1,1,0,0,NULL, NULL,                  N'Inh./Bek. senioren; Beker O23 cat A + junioren cat A; meiden vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-01-23','Competitie', 1,1,1,0,NULL, NULL,                  N'Start voorjaar: WD schema 14 cat A; Inh./Bek. schema 12; WD VJ O23; start fase 3 meiden+pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-01-30','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- Februari 2027
        ('2026/2027','Noord','2027-02-06','Competitie', 1,1,1,0,NULL, NULL,                  N'WD schema 14 cat A + schema 12 cat B; Inh./Bek. schema 12 cat A; meiden inhaal; carnavalsweekend', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-02-13','Competitie', 1,1,1,0,'Z',  NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; voorjaarsvak. Zuid 13-21 feb', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-02-20','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. alle categorieen; meiden inhaal; voorjaarsvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-02-27','Inhaal',     1,1,1,0,'MN', NULL,                  N'Inh./Bek. alle categorieen; meiden div inhaal / hoofdkl fase 3; voorjaarsvak. N+M 20-28 feb', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- Maart 2027
        ('2026/2027','Noord','2027-03-06','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-03-13','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-03-19','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-03-20','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-03-27','Inhaal',     1,1,1,0,NULL, N'Paaszaterdag',       N'Inh./Bek. senioren+O23+junioren; meiden inhaal; pupillen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-03-29','Feestdag',   1,1,0,0,NULL, N'2e Paasdag',         N'Inh./Bek. senioren+O23+junioren cat A; meiden vrij; pupillen geen wedstrijden', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- April 2027
        ('2026/2027','Noord','2027-04-02','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-04-03','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; start fase 4 pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-04-10','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-04-16','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-04-17','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-04-24','Competitie', 1,1,1,0,'MNZ',NULL,                  N'WD schema 14 + schema 12 cat B; Inh./Bek. schema 12 cat A + junioren; meivakantie 24 apr-2 mei', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- Mei 2027
        ('2026/2027','Noord','2027-05-01','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inhaal senioren cat A; Inh./Bek. cat B+O23+junioren; bekerfinale standaardteams volgens PDF-notitie; meivakantie', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-05-07','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-05-08','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-05-15','Competitie', 1,1,1,0,NULL, N'Pinksterzaterdag',   N'WD schema 14+12 cat A; Inh./Bek. cat B+O23+junioren; meiden div inhaal / hoofdkl fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-05-17','Feestdag',   1,1,0,0,NULL, N'2e Pinksterdag',     N'WD schema 14+12 cat A; Inh./Bek. schema 12 cat B + O23 + junioren; meiden vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-05-21','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-05-22','Competitie', 1,1,1,0,NULL, NULL,                  N'WD schema 14 + cat B; NC schema 12 cat A; WD VJ O23+junioren; meiden fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-05-29','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren cat A; Inh./Bek. cat B; WD VJ O23 cat A+junioren; meiden div inhaal / hoofdkl fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        -- Juni 2027
        ('2026/2027','Noord','2027-06-05','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren cat A; Inh./Bek. cat B; beker O23+junioren; final league meiden; finales districtsbeker', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-06-12','NC',         1,0,0,0,NULL, NULL,                  N'NC alleen senioren cat A',                                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027'),
        ('2026/2027','Noord','2027-06-19','NC',         1,0,0,0,NULL, NULL,                  N'NC alleen senioren cat A',                                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027');
END
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] = '2026/2027' AND [Regio] = 'Oost')
BEGIN
    INSERT INTO [dbo].[KnvbKalenderDag]
        ([Seizoen],[Regio],[Datum],[DagType],[HeeftSenioren],[HeeftJeugd],[HeeftMeiden],[PupillenToernooi],[Schoolvakantie],[Feestdag],[Opmerking],[Bron])
    VALUES
        -- Augustus / september 2026
        ('2026/2027','Oost','2026-08-15','Vrij',       0,0,0,0,'N',  NULL,                  N'Volledig vrij; schoolvak. Noord t/m 16 aug',               'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-08-22','Beker',      0,1,0,0,'Z',  NULL,                  N'Alleen Beker KO O23 cat A; rest vrij; schoolvak. Zuid t/m 23 aug', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-08-29','Beker',      1,1,1,0,'M',  NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23 cat A; meiden vrij/inhaal', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-09-05','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23 cat A; start fase 1 meiden+junioren+pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-09-12','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23 cat A; meiden+pupillen fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-09-18','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-09-19','Competitie', 1,1,1,0,NULL, NULL,                  N'Start competitie: WD senioren; WD NJ O23+junioren; meiden fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-09-26','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 1',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- Oktober 2026
        ('2026/2027','Oost','2026-10-02','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-10-03','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 1',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-10-10','Competitie', 1,1,1,0,'N',  NULL,                  N'WD senioren; WD NJ O23; meiden+junioren inhaal; pupillen vrij; herfstvak. Noord 10-18 okt', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-10-17','Inhaal',     1,1,0,0,'MNZ',NULL,                  N'Inh./Bek. senioren+O23+junioren cat A; meiden vrij; herfstvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-10-24','Competitie', 1,1,1,0,'MZ', NULL,                  N'WD senioren; WD NJ O23+junioren; start fase 2 meiden+junioren+pupillen; herfstvak. M+Z 17-25 okt', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-10-30','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-10-31','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden div fase 2 / hoofdkl fase 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- November 2026
        ('2026/2027','Oost','2026-11-07','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-11-13','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-11-14','Competitie', 1,1,1,0,NULL, NULL,                  N'WD cat A; Inh./Bek. schema 12 cat B; WD NJ O23+junioren; meiden fase 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-11-21','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-11-27','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-11-28','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- December 2026
        ('2026/2027','Oost','2026-12-05','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-12-12','Competitie', 1,1,1,0,NULL, NULL,                  N'WD schema 14 cat A; Inh./Bek. schema 12+O23+junioren; meiden inhaal; pupillen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2026-12-19','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. alle senioren+O23+junioren; meiden div inhaal / hoofdkl vrij; kerstvakantie 19 dec-3 jan', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- Januari 2027
        ('2026/2027','Oost','2027-01-09','Vrij',       0,0,0,0,NULL, NULL,                  N'Volledig vrij',                                            'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-01-16','Inhaal',     1,1,0,0,NULL, NULL,                  N'Inh./Bek. senioren; Beker O23 cat A + junioren cat A; meiden vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-01-23','Competitie', 1,1,1,0,NULL, NULL,                  N'Start voorjaar: WD senioren; WD VJ O23 cat A; Inh./Bek. junioren; start fase 3 meiden+pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-01-30','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- Februari 2027
        ('2026/2027','Oost','2027-02-06','Inhaal',     1,1,1,0,NULL, NULL,                  N'Inh./Bek. alle categorieen; meiden inhaal; pupillen vrij; carnavalsweekend', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-02-13','Competitie', 1,1,1,0,'Z',  NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; voorjaarsvak. Zuid 13-21 feb', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-02-20','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. alle categorieen; meiden inhaal; voorjaarsvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-02-27','Competitie', 1,1,1,0,'MN', NULL,                  N'WD schema 14 cat A; Inh./Bek. rest; meiden div inhaal / hoofdkl fase 3; voorjaarsvak. N+M 20-28 feb', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- Maart 2027
        ('2026/2027','Oost','2027-03-06','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-03-13','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-03-19','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-03-20','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-03-27','Inhaal',     1,1,1,0,NULL, N'Paaszaterdag',       N'Inh./Bek. senioren+O23+junioren; meiden inhaal; pupillen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-03-29','Feestdag',   1,1,0,0,NULL, N'2e Paasdag',         N'Inh./Bek. senioren+O23+junioren cat A; meiden vrij; pupillen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- April 2027
        ('2026/2027','Oost','2027-04-02','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-04-03','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; start fase 4 pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-04-10','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-04-16','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-04-17','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-04-24','Competitie', 1,1,1,0,'MNZ',NULL,                  N'WD schema 14 + schema 12 cat B; Inh./Bek. schema 12 cat A + junioren; meivakantie 24 apr-2 mei', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- Mei 2027
        ('2026/2027','Oost','2027-05-01','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inhaal senioren cat A; Inh./Bek. cat B+O23+junioren; bekerfinale standaardteams volgens PDF-notitie; meivakantie', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-05-07','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-05-08','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-05-15','Competitie', 1,1,1,0,NULL, N'Pinksterzaterdag',   N'WD schema 14+12 cat A; Inh./Bek. cat B+junioren; WD VJ O23 cat A; meiden div inhaal / hoofdkl fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-05-17','Feestdag',   1,1,0,0,NULL, N'2e Pinksterdag',     N'WD schema 14+12 cat A; Inh./Bek. schema 12 cat B + O23; meiden vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-05-21','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-05-22','Competitie', 1,1,1,0,NULL, NULL,                  N'WD schema 14 + cat B; vrij/NC schema 12 cat A; WD VJ O23+junioren; meiden fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-05-29','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren cat A; Inh./Bek. cat B; WD VJ O23+junioren; meiden div inhaal / hoofdkl fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        -- Juni 2027
        ('2026/2027','Oost','2027-06-05','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren cat A; Inh./Bek. cat B; beker O23+junioren; final league meiden; finales districtsbeker', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-06-12','NC',         1,0,0,0,NULL, NULL,                  N'NC alleen senioren cat A',                                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027'),
        ('2026/2027','Oost','2027-06-19','NC',         1,0,0,0,NULL, NULL,                  N'NC alleen senioren cat A',                                 'https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027');
END
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] = '2026/2027' AND [Regio] = 'Zuid')
BEGIN
    INSERT INTO [dbo].[KnvbKalenderDag]
        ([Seizoen],[Regio],[Datum],[DagType],[HeeftSenioren],[HeeftJeugd],[HeeftMeiden],[PupillenToernooi],[Schoolvakantie],[Feestdag],[Opmerking],[Bron])
    VALUES
        -- Augustus / september 2026
        ('2026/2027','Zuid','2026-08-15','Vrij',       0,0,0,0,'N',  NULL,                  N'Volledig vrij; schoolvak. Noord t/m 16 aug',               'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-08-22','Vrij',       0,0,0,0,'Z',  NULL,                  N'Volledig vrij; schoolvak. Zuid t/m 23 aug',                'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-08-29','Beker',      1,1,1,0,'M',  NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23 cat A; start fase 1 meiden+junioren+pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-09-05','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23; meiden+pupillen fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-09-12','Beker',      1,1,1,0,NULL, NULL,                  N'Bekerpoule senioren+junioren; WD NJ O23; meiden+pupillen fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-09-18','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-09-19','Competitie', 1,1,1,0,NULL, NULL,                  N'Start competitie: WD senioren; WD NJ O23+junioren; meiden fase 1', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-09-26','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 1',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- Oktober 2026
        ('2026/2027','Zuid','2026-10-02','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-10-03','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 1',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-10-10','Competitie', 1,1,1,0,'N',  NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 1; herfstvak. Noord 10-18 okt', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-10-17','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. senioren+O23+junioren; meiden inhaal; pupillen vrij; herfstvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-10-24','Competitie', 1,1,1,0,'MZ', NULL,                  N'WD senioren; WD NJ O23+junioren; meiden inhaal; start fase 2 hoofdkl junioren; herfstvak. M+Z 17-25 okt', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-10-30','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-10-31','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden div fase 2 / hoofdkl fase 2; start fase 2 pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- November 2026
        ('2026/2027','Zuid','2026-11-07','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-11-13','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-11-14','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-11-21','Competitie', 1,1,1,0,NULL, NULL,                  N'WD schema 14+12 cat A; Inh./Bek. schema 12 cat B; WD NJ O23+junioren; meiden fase 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-11-27','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-11-28','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- December 2026
        ('2026/2027','Zuid','2026-12-05','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD NJ O23+junioren; meiden fase 2',           'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-12-12','Inhaal',     1,1,1,0,NULL, NULL,                  N'Inh./Bek. senioren+junioren cat A; inhaal O23 cat A; WD NJ O23 cat B; meiden div fase 2 / hoofdkl fase 2', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2026-12-19','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. senioren+O23 cat B+junioren; inhaal O23 cat A; meiden inhaal; kerstvakantie 19 dec-3 jan', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- Januari 2027
        ('2026/2027','Zuid','2027-01-09','Inhaal',     1,0,0,0,NULL, NULL,                  N'Alleen schema 12 cat B Inh./Bek.; overige categorieen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-01-16','Competitie', 1,1,0,0,NULL, NULL,                  N'WD schema 14 cat A; Inh./Bek. schema 12; beker O23+junioren; meiden vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-01-23','Competitie', 1,1,1,0,NULL, NULL,                  N'Start voorjaar: WD schema 14 + schema 12 cat B; WD VJ O23+junioren; start fase 3 junioren+pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-01-30','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden div fase 2 / hoofdkl fase 3; pupillen fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- Februari 2027
        ('2026/2027','Zuid','2027-02-06','Vrij',       0,0,0,0,NULL, NULL,                  N'Volledig vrij — carnavalsweekend',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-02-13','Inhaal',     1,1,1,0,'Z',  NULL,                  N'Inh./Bek. alle categorieen; meiden inhaal; pupillen vrij; voorjaarsvak. Zuid 13-21 feb', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-02-20','Competitie', 1,1,1,0,'MNZ',NULL,                  N'WD senioren; WD VJ O23; Inh./Bek. junioren; meiden div inhaal / hoofdkl fase 3; voorjaarsvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-02-27','Competitie', 1,1,1,0,'MN', NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; voorjaarsvak. N+M 20-28 feb', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- Maart 2027
        ('2026/2027','Zuid','2027-03-06','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-03-12','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-03-13','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-03-20','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden+pupillen fase 3',  'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-03-27','Inhaal',     1,1,1,0,NULL, N'Paaszaterdag',       N'Inh./Bek. senioren+O23+junioren; meiden inhaal; pupillen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-03-29','Feestdag',   1,1,0,0,NULL, N'2e Paasdag',         N'Inh./Bek. senioren+O23+junioren cat A; meiden vrij; pupillen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- April 2027
        ('2026/2027','Zuid','2027-04-02','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-04-03','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; start fase 4 pupillen', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-04-10','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-04-17','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-04-23','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-04-24','Competitie', 1,1,1,0,'MNZ',NULL,                  N'WD schema 14 cat A; Inh./Bek. schema 12+O23+junioren; meiden inhaal; meivakantie 24 apr-2 mei', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- Mei 2027
        ('2026/2027','Zuid','2027-05-01','Inhaal',     1,1,1,0,'MNZ',NULL,                  N'Inh./Bek. alle categorieen; meiden inhaal; pupillen vrij; meivakantie', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-05-06','Feestdag',   1,0,0,0,NULL, N'Hemelvaartsdag',     N'Bekerfinale standaardteams; overige categorieen vrij',      'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-05-07','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-05-08','Competitie', 1,1,1,0,NULL, NULL,                  N'WD senioren; WD VJ O23+junioren; meiden div fase 2 / hoofdkl fase 3; pupillen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-05-15','Competitie', 1,1,1,0,NULL, N'Pinksterzaterdag',   N'WD schema 14+12 cat A; Inh./Bek. cat B+junioren; WD VJ O23; meiden div inhaal / hoofdkl fase 3', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-05-17','Feestdag',   1,0,0,0,NULL, N'2e Pinksterdag',     N'WD schema 14+12 cat A; Inh./Bek. schema 12 cat B; overige categorieen vrij', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-05-21','Toernooi',   0,0,0,1,NULL, NULL,                  N'Pupillen 7x7 toernooi (vrijdag)',                          'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-05-22','Competitie', 1,1,1,0,NULL, NULL,                  N'WD schema 14 + cat B; inhaal schema 12 cat A; WD VJ O23+junioren; meiden fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-05-29','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren cat A; Inh./Bek. cat B; WD VJ O23; meiden hoofdkl fase 3; pupillen fase 4', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        -- Juni 2027
        ('2026/2027','Zuid','2027-06-05','NC',         1,1,1,0,NULL, NULL,                  N'NC senioren cat A; inhaal cat B+O23+junioren; final league meiden; finales districtsbeker', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-06-12','NC',         1,0,0,0,NULL, NULL,                  N'NC senioren cat A; inhaal schema 12 cat B',                'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027'),
        ('2026/2027','Zuid','2027-06-19','NC',         1,0,0,0,NULL, NULL,                  N'NC senioren cat A; inhaal schema 12 cat B',                'https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027');
END
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[KnvbKalenderDag] WHERE [Seizoen] = '2026/2027' AND [Regio] = 'LandelijkJeugd')
BEGIN
    INSERT INTO [dbo].[KnvbKalenderDag]
        ([Seizoen],[Regio],[Datum],[DagType],[HeeftSenioren],[HeeftJeugd],[HeeftMeiden],[PupillenToernooi],[Schoolvakantie],[Feestdag],[Opmerking],[Bron])
    VALUES
        -- Najaar 2026 — O21 t/m O13, divisies landelijk (alle leeftijden gelijk speelplan)
        ('2026/2027','LandelijkJeugd','2026-08-08','Vrij',       0,0,0,0,NULL, NULL,                 N'Geen competitie',                                    'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-08-15','Vrij',       0,0,0,0,'N',  NULL,                 N'Geen competitie; schoolvak. Noord t/m 16 aug',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-08-22','Inhaal',     1,1,0,0,'Z',  NULL,                 N'Inhaal / Jeugdcup alle leeftijden; schoolvak. Zuid t/m 23 aug', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-08-29','Competitie', 1,1,0,0,'M',  NULL,                 N'Start competitie: ronde 1 O21 t/m O13; schoolvak. Midden t/m 30 aug', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-09-05','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 2 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-09-12','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 3 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-09-19','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 4 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-09-26','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 5 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-10-03','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 6 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-10-10','Competitie', 1,1,0,0,'N',  NULL,                 N'Ronde 7 O21 t/m O13; herfstvak. Noord',              'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-10-17','Inhaal',     1,1,0,0,'MNZ',NULL,                 N'Inhaal / Jeugdcup; herfstvakantie alle regio''s',     'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-10-24','Competitie', 1,1,0,0,'MZ', NULL,                 N'Ronde 8 O21 t/m O13; herfstvak. Midden en Zuid',     'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-10-31','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 9 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-11-07','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 10 O21 t/m O13',                               'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-11-14','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 11 O21 t/m O13',                               'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-11-21','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 12 O21 t/m O13',                               'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-11-28','Inhaal',     1,1,0,0,NULL, NULL,                 N'Inhaal / Jeugdcup alle leeftijden',                  'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-12-05','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 13 O21 t/m O13',                               'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-12-12','Competitie', 1,1,0,0,NULL, NULL,                 N'Laatste speelronde najaar (verplaatsingsdeadline 13 dec 2026): ronde 14 O21 t/m O13', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-12-19','Inhaal',     1,1,0,0,'MNZ',NULL,                 N'Inhaalmoment uitsluitend voor calamiteiten of gelijktijdige laatste speelronde; kerstvakantie', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2026-12-26','Vrij',       0,0,0,0,'MNZ',NULL,                 N'Kerstvakantie',                                      'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        -- Voorjaar 2027
        ('2026/2027','LandelijkJeugd','2027-01-02','Vrij',       0,0,0,0,'MNZ',NULL,                 N'Kerstvakantie',                                      'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-01-09','Vrij',       0,0,0,0,NULL, NULL,                 N'Geen competitie',                                    'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-01-16','Competitie', 1,1,0,0,NULL, NULL,                 N'Start voorjaar: ronde 1 O21 t/m O13',                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-01-23','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 2 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-01-30','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 3 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-02-06','Inhaal',     1,1,0,0,NULL, NULL,                 N'Inhaal / Jeugdcup; carnavalsweekend',                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-02-13','Inhaal',     1,1,0,0,'Z',  NULL,                 N'Inhaal alle leeftijden; voorjaarsvak. Zuid',         'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-02-20','Beker',      1,1,0,0,'MNZ',NULL,                 N'Jeugdcup alle leeftijden; voorjaarsvakantie alle regio''s', 'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-02-27','Competitie', 1,1,0,0,'M',  NULL,                 N'Ronde 4 O21 t/m O13; voorjaarsvak. Midden/West',     'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-03-06','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 5 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-03-13','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 6 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-03-20','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 7 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-03-27','Inhaal',     1,1,0,0,NULL, N'Paaszaterdag',      N'Inhaal / Jeugdcup; paasweekend',                     'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-04-03','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 8 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-04-10','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 9 O21 t/m O13',                                'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-04-17','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 10 O21 t/m O13',                               'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-04-24','Competitie', 1,1,0,0,'MNZ',NULL,                 N'Ronde 11 O21 t/m O13; meivakantie',                  'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-05-01','Inhaal',     1,1,0,0,'MNZ',NULL,                 N'Inhaal / Jeugdcup finale; meivakantie',              'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-05-06','Inhaal',     1,1,0,0,NULL, N'Hemelvaartsdag',    N'Midweeks inhaalmoment 5-6 mei',                      'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-05-08','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 12 O21 t/m O13',                               'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-05-15','Competitie', 1,1,0,0,NULL, N'Pinksterzaterdag',  N'Speelweekend 15-17 mei: ronde 13 O21 t/m O13',        'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-05-22','Inhaal',     1,1,0,0,NULL, NULL,                 N'Laatste inhaalmoment voorjaar 23 mei 2027',          'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-05-26','Vrij',       0,0,0,0,NULL, NULL,                 N'Geen competitie (woensdag)',                         'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-05-29','Competitie', 1,1,0,0,NULL, NULL,                 N'Ronde 14 O21 t/m O13 — laatste speelronde',          'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        -- Juni 2027
        ('2026/2027','LandelijkJeugd','2027-06-03','Vrij',       0,0,0,0,NULL, NULL,                 N'Geen competitie (donderdag)',                        'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027'),
        ('2026/2027','LandelijkJeugd','2027-06-05','NC',         1,1,0,0,NULL, NULL,                 N'Finale divisie 1 O21 t/m O13',                       'https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027');
END
GO

-- De vier AVG-retentieprocedures (planner.sp_CleanupClassificatieCorrectie, avg.sp_CleanupImportLog,
-- planner.sp_CleanupEmailVerwerking, avg.sp_CleanupTeambegeleiding) stonden hier als
-- 'IF NOT EXISTS ... EXEC(N''CREATE PROCEDURE ...'')'. Daardoor kon hun definitie na de eerste
-- release nooit meer wijzigen (#707) en werden ze bovendien aangemaakt vóórdat de schema's
-- planner/avg bestaan. Ze staan nu als CREATE OR ALTER onderaan dit script — zie de sectie
-- "#707: AVG-retentieprocedures".

-- ============================================================
-- #631: duplicaten in dbo.Season opruimen en een unique constraint aanbrengen.
--
-- De oude guard in sp_UpdateSeasonTable vergeleek YEAR(MAX(DateUntil)) met YEAR(GETDATE())+1.
-- Zodra er een toekomstig seizoen in de tabel stond kon die conditie nooit meer onwaar worden,
-- waardoor bij ELKE deploy hetzelfde seizoen opnieuw werd ingevoegd. In productie stonden zo
-- 3 identieke rijen voor 2026-2027. Omdat pub.DateTable een INNER JOIN op dbo.Season doet,
-- leverde die view 3 rijen per datum voor het huidige seizoen (2557 i.p.v. 2192 rijen).
--
-- De applicatie zelf was niet geraakt: alle C#-consumers gebruiken MAX()/MIN()-aggregaten.
--
-- Deze block draait VOOR de EXEC hieronder, zodat de deduplicatie klaar is voordat de procedure
-- iets kan invoegen. Volgorde is essentieel: de constraint kan pas na het opruimen, anders faalt
-- de deploy op de bestaande duplicaten.
-- ============================================================
IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.Season'))
BEGIN
    -- Rijen zonder naam zijn onbruikbaar (de naam is de business key) en blokkeren NOT NULL.
    DELETE FROM [dbo].[Season] WHERE [Name] IS NULL;

    -- Houd per seizoensnaam één rij over. ORDER BY op de datums zodat de bewaarde rij
    -- deterministisch is en niet afhangt van de fysieke rijvolgorde.
    ;WITH Duplicaten AS (
        SELECT ROW_NUMBER() OVER (
                   PARTITION BY [Name]
                   ORDER BY [DateFrom], [DateUntil]
               ) AS rn
        FROM [dbo].[Season]
    )
    DELETE FROM Duplicaten WHERE rn > 1;

    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID('dbo.Season')
          AND name = 'Name'
          AND is_nullable = 1
    )
        ALTER TABLE [dbo].[Season] ALTER COLUMN [Name] NCHAR(9) NOT NULL;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('dbo.Season') AND name = 'UQ_Season_Name'
    )
        ALTER TABLE [dbo].[Season] ADD CONSTRAINT [UQ_Season_Name] UNIQUE ([Name]);
END
GO

-- Update the Season and datetable
-- Een scalar subquery zonder aggregatie faalt met Msg 512 zodra dbo.AppSettings meer dan één rij
-- heeft — en dat is altijd het geval nadat de AllStars FC demo-club verderop in dit script is
-- geseed. Gevolg: vanaf de tweede deploy brak dit statement en werd dbo.Season/dbo.DateTable niet
-- meer bijgewerkt, waardoor een nieuw seizoen niet automatisch werd aangemaakt. Zelfde klasse
-- fout als #435.
--
-- MIN() is hier de juiste keuze: dbo.Season en dbo.DateTable zijn clubneutraal (geen ClubCode),
-- dus het bereik moet de vroegst startende club omvatten. Bewust géén filter op SyncEnabled of
-- ORDER BY [Id]: SyncEnabled wordt pas verderop in dit script toegevoegd en een Id-kolom bestaat
-- niet in dbo.AppSettings — beide zouden een compile-fout geven op oudere installaties.
DECLARE @SeasonStartMonth INT = (SELECT MIN([SeasonStartMonth]) FROM [dbo].[AppSettings]);

IF @SeasonStartMonth IS NOT NULL
    EXEC [dbo].[sp_UpdateSeasonTable] @SeasonStartMonth;
GO
-- ============================================================
-- #30: Multi-club fundament — ClubCode + Accommodatie
-- De DEFAULT is uitsluitend migratie-backwards-compat (bestaande rijen krijgen hier een waarde) en
-- gebruikt de neutrale placeholder 'CLUB' — nooit een clubnaam (#598). Constraints worden hierna gedropt.
-- Alle nieuwe inserts geven ClubCode altijd expliciet mee vanuit AppSettings.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ClubCode')
    ALTER TABLE [dbo].[AppSettings] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_AppSettings_ClubCode] DEFAULT 'CLUB'; -- neutrale placeholder, constraint wordt direct hierna gedropt (#598/#610)
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'Accommodatie')
    ALTER TABLE [dbo].[AppSettings] ADD [Accommodatie] NVARCHAR(200) NULL; -- geen default — in te stellen via Beheer → Instellingen per club
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Velden') AND name = 'ClubCode')
    ALTER TABLE [dbo].[Velden] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_Velden_ClubCode] DEFAULT 'CLUB'; -- neutrale placeholder, constraint wordt direct hierna gedropt (#598/#610)
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Speeltijden') AND name = 'ClubCode')
    ALTER TABLE [dbo].[Speeltijden] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_Speeltijden_ClubCode] DEFAULT 'CLUB'; -- neutrale placeholder, constraint wordt direct hierna gedropt (#598/#610)
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TeamRegels') AND name = 'ClubCode')
    ALTER TABLE [dbo].[TeamRegels] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_TeamRegels_ClubCode] DEFAULT 'CLUB'; -- neutrale placeholder, constraint wordt direct hierna gedropt (#598/#610)
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.VeldBeschikbaarheid') AND name = 'ClubCode')
    ALTER TABLE [dbo].[VeldBeschikbaarheid] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_VeldBeschikbaarheid_ClubCode] DEFAULT 'CLUB'; -- neutrale placeholder, constraint wordt direct hierna gedropt (#598/#610)
GO

-- #435: verwijder VRC-default constraints — clubnaam heeft geen plek als DB-default
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_Speeltijden_ClubCode')
    ALTER TABLE [dbo].[Speeltijden] DROP CONSTRAINT [DF_Speeltijden_ClubCode];
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_TeamRegels_ClubCode')
    ALTER TABLE [dbo].[TeamRegels] DROP CONSTRAINT [DF_TeamRegels_ClubCode];
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_VeldBeschikbaarheid_ClubCode')
    ALTER TABLE [dbo].[VeldBeschikbaarheid] DROP CONSTRAINT [DF_VeldBeschikbaarheid_ClubCode];
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_Velden_ClubCode')
    ALTER TABLE [dbo].[Velden] DROP CONSTRAINT [DF_Velden_ClubCode];
GO

-- ============================================================
-- #29: IsVervallen + SportlinkWedstrijdCode + status rename
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.GeplandeWedstrijden') AND name = 'IsVervallen')
    ALTER TABLE [planner].[GeplandeWedstrijden] ADD [IsVervallen] BIT NOT NULL CONSTRAINT [DF_GeplandeWedstrijden_IsVervallen] DEFAULT 0;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.GeplandeWedstrijden') AND name = 'SportlinkWedstrijdCode')
    ALTER TABLE [planner].[GeplandeWedstrijden] ADD [SportlinkWedstrijdCode] BIGINT NULL;
GO
-- Verander default constraint Status: 'Gepland' → 'Te bevestigen'
IF EXISTS (
    SELECT 1 FROM sys.default_constraints dc
    JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID('planner.GeplandeWedstrijden')
      AND c.name = 'Status'
      AND dc.definition = '(''Gepland'')'
)
BEGIN
    DECLARE @constraintName NVARCHAR(200);
    SELECT @constraintName = dc.name
    FROM sys.default_constraints dc
    JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID('planner.GeplandeWedstrijden') AND c.name = 'Status';
    EXEC('ALTER TABLE [planner].[GeplandeWedstrijden] DROP CONSTRAINT [' + @constraintName + ']');
    ALTER TABLE [planner].[GeplandeWedstrijden]
        ADD CONSTRAINT [DF_GeplandeWedstrijden_Status] DEFAULT 'Te bevestigen' FOR [Status];
END
GO
-- Bestaande 'Gepland' rijen bijwerken naar 'Te bevestigen'
UPDATE [planner].[GeplandeWedstrijden]
SET [Status] = 'Te bevestigen', [mta_modified] = GETUTCDATE()
WHERE [Status] = 'Gepland';
GO

-- ============================================================
-- v2 — #86: AppSettings schema uitbreiden
-- HerplanDeadlineDagen, BufferMinuten
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'HerplanDeadlineDagen')
    ALTER TABLE [dbo].[AppSettings] ADD [HerplanDeadlineDagen] INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'BufferMinuten')
    ALTER TABLE [dbo].[AppSettings] ADD [BufferMinuten] INT NULL;
GO

-- v2 — #139: AccommodatiePlaats + GPS-coördinaten voor geocoding en zonsondergangsberekening
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'AccommodatiePlaats')
    ALTER TABLE [dbo].[AppSettings] ADD [AccommodatiePlaats] NVARCHAR(100) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'AccommodatieLatitude')
    ALTER TABLE [dbo].[AppSettings] ADD [AccommodatieLatitude] FLOAT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'AccommodatieLongitude')
    ALTER TABLE [dbo].[AppSettings] ADD [AccommodatieLongitude] FLOAT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'EmailVoetnoot')
    ALTER TABLE [dbo].[AppSettings] ADD [EmailVoetnoot] NVARCHAR(MAX) NULL;
GO

-- v2 — #88: AppSettingsAudit — append-only auditlog van AppSettings/template wijzigingen
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.AppSettingsAudit'))
BEGIN
    CREATE TABLE [dbo].[AppSettingsAudit] (
        [Id]            INT IDENTITY(1,1) NOT NULL,
        [Tijdstip]      DATETIME2 NOT NULL CONSTRAINT [DF_AppSettingsAudit_Tijdstip] DEFAULT GETUTCDATE(),
        [GewijzigdDoor] NVARCHAR(100) NOT NULL,
        [Veld]          NVARCHAR(100) NOT NULL,
        [OudeWaarde]    NVARCHAR(MAX) NULL,
        [NieuweWaarde]  NVARCHAR(MAX) NULL,
        [ClubCode]      NVARCHAR(20) NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
        CONSTRAINT [PK_AppSettingsAudit] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END
GO
-- #501: Verwijder club-specifieke DEFAULT 'VRC' uit AppSettingsAudit.ClubCode
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_AppSettingsAudit_ClubCode')
    ALTER TABLE [dbo].[AppSettingsAudit] DROP CONSTRAINT [DF_AppSettingsAudit_ClubCode];
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AppSettingsAudit_ClubCode')
    ALTER TABLE [dbo].[AppSettingsAudit] ADD CONSTRAINT [CK_AppSettingsAudit_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO

-- v2 — #62: TeamVoorkeurTijden
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.TeamVoorkeurTijden'))
BEGIN
    CREATE TABLE [dbo].[TeamVoorkeurTijden] (
        [Id]            INT IDENTITY(1,1) NOT NULL,
        [TeamNaam]      NVARCHAR(100) NOT NULL,
        [DagVanWeek]    INT NOT NULL,           -- 6=zaterdag, 7=zondag, 1-5=doordeweeks
        [VoorkeurTijd]  TIME NOT NULL,
        [Prioriteit]    INT NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Prioriteit] DEFAULT 5,
        [Actief]        BIT NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Actief] DEFAULT 1,
        [ClubCode]      NVARCHAR(20) NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
        [mta_inserted]  DATETIME2 NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Inserted] DEFAULT GETUTCDATE(),
        [mta_modified]  DATETIME2 NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Modified] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_TeamVoorkeurTijden] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END
GO
-- #501: Verwijder club-specifieke DEFAULT 'VRC' uit TeamVoorkeurTijden.ClubCode
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_TeamVoorkeurTijden_ClubCode')
    ALTER TABLE [dbo].[TeamVoorkeurTijden] DROP CONSTRAINT [DF_TeamVoorkeurTijden_ClubCode];
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_TeamVoorkeurTijden_ClubCode')
    ALTER TABLE [dbo].[TeamVoorkeurTijden] ADD CONSTRAINT [CK_TeamVoorkeurTijden_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO

-- #679: VeldTraining — terugkerende trainingsbezetting per veld per weekdag, vrij per club
-- instelbaar. Aparte tabel naast VeldBeschikbaarheid: beschikbaarheid is een venster, training
-- is een bezetter (zie #581 voor de onderbouwing).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.VeldTraining'))
BEGIN
    CREATE TABLE [dbo].[VeldTraining] (
        [Id]           INT IDENTITY(1,1) NOT NULL,
        [VeldNummer]   INT NOT NULL,
        [DagVanWeek]   INT NOT NULL,
        [VanTijd]      TIME NOT NULL,
        [TotTijd]      TIME NOT NULL,
        [Omschrijving] NVARCHAR(100) NULL,
        [Actief]       BIT NOT NULL CONSTRAINT [DF_VeldTraining_Actief] DEFAULT 1,
        [ClubCode]     NVARCHAR(20) NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
        CONSTRAINT [PK_VeldTraining] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [FK_VeldTraining_Velden] FOREIGN KEY ([VeldNummer]) REFERENCES [dbo].[Velden]([VeldNummer])
    );
END
GO

-- v2 — UitgeslotenEmailAdressen: expliciete uitsluitingslijst voor email-verwerking
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.UitgeslotenEmailAdressen'))
BEGIN
    CREATE TABLE [dbo].[UitgeslotenEmailAdressen] (
        [Id]           INT IDENTITY(1,1) NOT NULL,
        [EmailAdres]   NVARCHAR(200) NOT NULL,
        [Omschrijving] NVARCHAR(500) NULL,
        [Actief]       BIT NOT NULL CONSTRAINT [DF_UitgeslotenEmailAdressen_Actief]    DEFAULT 1,
        [ClubCode]     NVARCHAR(20) NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
        [mta_inserted] DATETIME2 NOT NULL CONSTRAINT [DF_UitgeslotenEmailAdressen_Inserted] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_UitgeslotenEmailAdressen] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [UQ_UitgeslotenEmailAdressen_Adres] UNIQUE ([EmailAdres], [ClubCode])
    );
END
GO
-- #501: Verwijder club-specifieke DEFAULT 'VRC' uit UitgeslotenEmailAdressen.ClubCode
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_UitgeslotenEmailAdressen_ClubCode')
    ALTER TABLE [dbo].[UitgeslotenEmailAdressen] DROP CONSTRAINT [DF_UitgeslotenEmailAdressen_ClubCode];
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_UitgeslotenEmailAdressen_ClubCode')
    ALTER TABLE [dbo].[UitgeslotenEmailAdressen] ADD CONSTRAINT [CK_UitgeslotenEmailAdressen_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO

-- v2 — #119: ClubCode toevoegen aan planner.EmailVerwerking (multi-club isolatie email-log)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.EmailVerwerking') AND name = 'ClubCode')
    ALTER TABLE [planner].[EmailVerwerking] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_EmailVerwerking_ClubCode] DEFAULT 'CLUB'; -- neutrale placeholder, constraint wordt bij #242 gedropt
GO

-- #242: Verwijder de placeholder-DEFAULT uit EmailVerwerking.ClubCode
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_EmailVerwerking_ClubCode')
    ALTER TABLE [planner].[EmailVerwerking] DROP CONSTRAINT [DF_EmailVerwerking_ClubCode];
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_EmailVerwerking_ClubCode')
    ALTER TABLE [planner].[EmailVerwerking] ADD CONSTRAINT [CK_EmailVerwerking_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO

-- avg schema + avg.Teambegeleiding (AVG/GDPR persoonsgegevens teambegeleiders)
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'avg')
    EXEC('CREATE SCHEMA [avg]');
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('avg.Teambegeleiding'))
BEGIN
    CREATE TABLE [avg].[Teambegeleiding] (
        [Id]                     INT            IDENTITY (1, 1) NOT NULL,
        [Team]                   NVARCHAR (100) NULL,
        [LeeftijdscategorieTeam] NVARCHAR (50)  NULL,
        [Teamrol]                NVARCHAR (100) NULL,
        [Naam]                   NVARCHAR (300) NULL,
        [Emailadres]             NVARCHAR (200) NULL,
        [Telefoonnummer]         NVARCHAR (50)  NULL,
        [mta_imported]           DATETIME       CONSTRAINT [DF_avg_Teambegeleiding_mta_imported] DEFAULT (GETUTCDATE()) NOT NULL,
        [ClubCode]               NVARCHAR (20)  NOT NULL CONSTRAINT [DF_avg_Teambegeleiding_ClubCode] DEFAULT '',
        CONSTRAINT [PK_avg_Teambegeleiding] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END
GO
-- ClubCode toevoegen aan bestaande avg.Teambegeleiding installaties (idempotent)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('avg.Teambegeleiding') AND name = 'ClubCode')
    ALTER TABLE [avg].[Teambegeleiding] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_avg_Teambegeleiding_ClubCode] DEFAULT '';
GO

-- ============================================================
-- #291: Speeltijden correcties en ontbrekende categorieën
-- Senioren (1-99, VR) = 115 min (2x45 + 15 rust + 10 buffer)
-- JO6 = 40 min; ontbrekende MO-categorieën = equivalent JO
-- Idempotent: UPDATE voor bestaande rijen, MERGE voor ontbrekende
-- ============================================================

-- Senioren correctie: 105 → 115
UPDATE [dbo].[Speeltijden]
SET [WedstrijdTotaal] = 115, [WedstrijdHelft] = 45, [WedstrijdRust] = 15
WHERE [Leeftijd] IN ('1-99', 'VR') AND [WedstrijdTotaal] < 115;
GO

-- Ontbrekende categorieën aanvullen
MERGE [dbo].[Speeltijden] AS target
USING (VALUES
    ('JO6',  0.25, 40,  15, 10),
    ('MO7',  0.25, 50,  20, 10),
    ('MO8',  0.25, 50,  20, 10),
    ('MO9',  0.25, 50,  20, 10),
    ('MO10', 0.25, 65,  25, 15),
    ('MO11', 0.50, 75,  30, 15),
    ('MO12', 0.50, 75,  30, 15),
    ('MO14', 1.00, 85,  35, 15),
    ('MO16', 1.00, 95,  40, 15),
    ('MO18', 1.00, 105, 45, 15),
    ('MO23', 1.00, 105, 45, 15)
) AS src ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust])
ON target.[Leeftijd] = src.[Leeftijd]
WHEN NOT MATCHED THEN
    INSERT ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust])
    VALUES (src.[Leeftijd], src.[Veldafmeting], src.[WedstrijdTotaal], src.[WedstrijdHelft], src.[WedstrijdRust]);
GO

-- ============================================================
-- #323: Email feedback loop — IsReplyOpOnsAntwoord, ReplyOpVerwerkingId
--       en planner.ClassificatieCorrectie tabel
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.EmailVerwerking') AND name = 'IsReplyOpOnsAntwoord')
    ALTER TABLE [planner].[EmailVerwerking] ADD [IsReplyOpOnsAntwoord] BIT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.EmailVerwerking') AND name = 'ReplyOpVerwerkingId')
    ALTER TABLE [planner].[EmailVerwerking] ADD [ReplyOpVerwerkingId] INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('planner.ClassificatieCorrectie'))
BEGIN
    CREATE TABLE [planner].[ClassificatieCorrectie] (
        [Id]                        INT             IDENTITY(1,1)   NOT NULL,
        [OrigineleVerwerkingId]     INT                             NOT NULL,
        [CorrectionVerwerkingId]    INT                             NOT NULL,
        [OrigineelVerzoekType]      NVARCHAR(50)                    NOT NULL,
        [AfgeleidJuistType]         NVARCHAR(50)                    NULL,
        [OrigineleSamenvatting]     NVARCHAR(500)                   NULL,
        [CorrectieSamenvatting]     NVARCHAR(500)                   NULL,
        [IsGevalideerd]             BIT             NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_IsGevalideerd] DEFAULT 0,
        [IsAfgewezen]               BIT             NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_IsAfgewezen] DEFAULT 0,
        [ClubCode]                  NVARCHAR(20)                    NOT NULL,
        [mta_inserted]              DATETIME        NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_Ins] DEFAULT GETUTCDATE(),
        [mta_modified]              DATETIME        NOT NULL CONSTRAINT [DF_ClassificatieCorrectie_Mod] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_ClassificatieCorrectie] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [FK_ClassificatieCorrectie_Origineel] FOREIGN KEY ([OrigineleVerwerkingId]) REFERENCES [planner].[EmailVerwerking]([Id]),
        CONSTRAINT [FK_ClassificatieCorrectie_Correctie] FOREIGN KEY ([CorrectionVerwerkingId]) REFERENCES [planner].[EmailVerwerking]([Id])
    );
END
GO

-- ============================================================
-- #324: AllStars FC — multi-club infrastructure
-- ============================================================
-- ClubCode kolom in his.* tabellen (idempotent)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('his.teams') AND name = 'ClubCode')
    ALTER TABLE [his].[teams] ADD [ClubCode] NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('his.matches') AND name = 'ClubCode')
    ALTER TABLE [his].[matches] ADD [ClubCode] NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('his.matchdetails') AND name = 'ClubCode')
    ALTER TABLE [his].[matchdetails] ADD [ClubCode] NVARCHAR(20) NULL;
GO

-- SyncEnabled kolom in dbo.AppSettings
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'SyncEnabled')
    ALTER TABLE [dbo].[AppSettings] ADD [SyncEnabled] BIT NOT NULL DEFAULT 1;
GO

-- #561: KnvbPdfBijlageIngeschakeld / KnvbStandaardRegio in dbo.AppSettings.
-- Deze twee kolommen stonden alleen in het DB-project. De deploy publiceert geen dacpac
-- (zie de schema-drift check in build.yml), dus zonder deze guard ontbreken ze in productie
-- en faalt GET /api/beheer/settings met "Invalid column name" — dat is de hele
-- Instellingen-pagina. De guard in build.yml controleert alleen tabellen, geen kolommen.
-- KnvbStandaardRegio blijft bewust NULL: zonder regio verandert het herplan-gedrag niet.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'KnvbPdfBijlageIngeschakeld')
    ALTER TABLE [dbo].[AppSettings] ADD [KnvbPdfBijlageIngeschakeld] BIT NOT NULL DEFAULT 1;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'KnvbStandaardRegio')
    ALTER TABLE [dbo].[AppSettings] ADD [KnvbStandaardRegio] NVARCHAR(20) NULL;
GO

-- UNIQUE constraint op ClubCode in dbo.AppSettings (slechts één rij per club)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'UQ_AppSettings_ClubCode')
    ALTER TABLE [dbo].[AppSettings] ADD CONSTRAINT [UQ_AppSettings_ClubCode] UNIQUE ([ClubCode]);
GO

-- ClubCode kolom in dbo.AppSettings (indien ontbreekt — pre-multi-club installaties)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ClubCode')
BEGIN
    ALTER TABLE [dbo].[AppSettings] ADD [ClubCode] NVARCHAR(20) NOT NULL DEFAULT 'CLUB';
END
GO

-- Bestaande his.* rijen koppelen aan de primaire club (eenmalig)
UPDATE [his].[teams]    SET [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1) WHERE [ClubCode] IS NULL;
UPDATE [his].[matches]  SET [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1) WHERE [ClubCode] IS NULL;
UPDATE [his].[matchdetails] SET [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1) WHERE [ClubCode] IS NULL;
GO

-- AllStars FC demo-club seed (idempotent)
IF NOT EXISTS (SELECT 1 FROM [dbo].[AppSettings] WHERE [ClubCode] = 'ALLSTARS')
BEGIN
    INSERT INTO [dbo].[AppSettings]
        ([ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId], [SeasonStartMonth],
         [FetchSchedule], [SyncEnabled])
    VALUES
        ('AllStars FC', 'ALLSTARS', 'https://data.sportlink.com', 'ALLSTARS_NO_SYNC', 8,
         '0 0 4 * * *', 0);
END
GO

-- ============================================================
-- #325: Club-thema — ThemeColor* kolommen in dbo.AppSettings
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ThemeColorPrimary')
    ALTER TABLE [dbo].[AppSettings] ADD [ThemeColorPrimary] NVARCHAR(7) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ThemeColorSecondary')
    ALTER TABLE [dbo].[AppSettings] ADD [ThemeColorSecondary] NVARCHAR(7) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ThemeColorAccent')
    ALTER TABLE [dbo].[AppSettings] ADD [ThemeColorAccent] NVARCHAR(7) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ThemeColorTextOnPrimary')
    ALTER TABLE [dbo].[AppSettings] ADD [ThemeColorTextOnPrimary] NVARCHAR(7) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ThemeClubWebsiteUrl')
    ALTER TABLE [dbo].[AppSettings] ADD [ThemeClubWebsiteUrl] NVARCHAR(300) NULL;
GO

-- v2 — #84: EmailTemplateInstellingen
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.EmailTemplateInstellingen'))
BEGIN
    CREATE TABLE [dbo].[EmailTemplateInstellingen] (
        [Id]            INT IDENTITY(1,1) NOT NULL,
        [TemplateKey]   NVARCHAR(100) NOT NULL,
        [Onderwerp]     NVARCHAR(500) NOT NULL,
        [BodyTemplate]  NVARCHAR(MAX) NOT NULL,
        [Actief]        BIT NOT NULL CONSTRAINT [DF_EmailTemplateInstellingen_Actief] DEFAULT 1,
        [ClubCode]      NVARCHAR(20) NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
        [mta_inserted]  DATETIME2 NOT NULL CONSTRAINT [DF_EmailTemplateInstellingen_Inserted] DEFAULT GETUTCDATE(),
        [mta_modified]  DATETIME2 NOT NULL CONSTRAINT [DF_EmailTemplateInstellingen_Modified] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_EmailTemplateInstellingen] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [UQ_EmailTemplateInstellingen_Key] UNIQUE ([TemplateKey], [ClubCode])
    );
END
GO

-- ============================================================
-- #365: veld_subpositie — ALLSTARS testdata veldsplitsing
-- Slaat het velddeel op (A, B, A1, A2, B1, B2) zodat de planner
-- deelvelden correct visualiseert voor leeftijden met Veldafmeting < 1.00
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('his.matches') AND name = 'veld_subpositie')
    ALTER TABLE [his].[matches] ADD [veld_subpositie] NVARCHAR(5) NULL;
GO

-- ============================================================
-- #428: ClubCode in planner.GeplandeWedstrijden + unique constraint
-- ============================================================
-- Deze migratie was permanent gebroken: SQL Server compileert een batch volledig vóór uitvoering,
-- dus de UPDATE en ALTER COLUMN die [ClubCode] noemen faalden op naam-binding (Msg 207/1911/1750)
-- terwijl de kolom in diezelfde batch nog moest worden toegevoegd. Omdat een batch die niet
-- compileert in zijn geheel niet draait, werd de kolom ook nooit aangemaakt — bij elke deploy
-- opnieuw. Opgelost door de DDL af te sluiten met GO en de DML in een aparte batch te zetten.
-- Ook verwijderd: ORDER BY [Id] — dbo.AppSettings heeft geen Id-kolom.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.GeplandeWedstrijden') AND name = 'ClubCode')
    ALTER TABLE [planner].[GeplandeWedstrijden] ADD [ClubCode] NVARCHAR(20) NULL;
GO

-- Backfill: koppel bestaande rijen aan de primaire club (de enige met SyncEnabled = 1).
-- SyncEnabled bestaat op dit punt zeker — het wordt hierboven toegevoegd.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.GeplandeWedstrijden') AND name = 'ClubCode')
   AND EXISTS (SELECT 1 FROM [planner].[GeplandeWedstrijden] WHERE [ClubCode] IS NULL)
BEGIN
    UPDATE [planner].[GeplandeWedstrijden]
    SET [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1)
    WHERE [ClubCode] IS NULL;
END
GO

-- NOT NULL na backfill — alleen als er geen NULL's meer over zijn (anders faalt de ALTER)
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('planner.GeplandeWedstrijden')
             AND name = 'ClubCode' AND is_nullable = 1)
   AND NOT EXISTS (SELECT 1 FROM [planner].[GeplandeWedstrijden] WHERE [ClubCode] IS NULL)
    ALTER TABLE [planner].[GeplandeWedstrijden] ALTER COLUMN [ClubCode] NVARCHAR(20) NOT NULL;
GO

-- Update unique constraint om ClubCode op te nemen (drop + recreate, idempotent)
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('planner.GeplandeWedstrijden') AND name = 'UQ_GeplandeWedstrijden_Slot')
AND NOT EXISTS (
    SELECT 1 FROM sys.index_columns ic
    JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
    WHERE ic.object_id = OBJECT_ID('planner.GeplandeWedstrijden') AND ic.index_id =
        (SELECT index_id FROM sys.indexes WHERE object_id = OBJECT_ID('planner.GeplandeWedstrijden') AND name = 'UQ_GeplandeWedstrijden_Slot')
    AND c.name = 'ClubCode'
)
BEGIN
    ALTER TABLE [planner].[GeplandeWedstrijden] DROP CONSTRAINT [UQ_GeplandeWedstrijden_Slot];
    ALTER TABLE [planner].[GeplandeWedstrijden] ADD CONSTRAINT [UQ_GeplandeWedstrijden_Slot]
        UNIQUE ([ClubCode], [Datum], [AanvangsTijd], [VeldNummer], [VeldDeelGebruik]);
END
GO

-- ============================================================
-- #580 / #574: ClubCode en Wedstrijdcode in planner.AlleWedstrijdenOpVeld
--
-- De bezettingsquery filterde alleen op datum. In een database met productieclub +
-- ALLSTARS-demodata leverde dat clubvreemde bezetting op → onjuiste beschikbaarheid
-- en foutieve automatische "niet mogelijk"-antwoorden.
-- Herplan-exclusie gebruikte daarnaast een tekst-contains op de wedstrijdnaam; dat
-- matcht ook deelstrings (code 123 in 3123). Wedstrijdcode is nu een echte kolom.
--
-- his.matches.ClubCode is nullable (migratie 001): niet-gestempelde rijen horen bij de
-- primaire club — vandaar ISNULL(m.ClubCode, a.ClubCode). Zonder die tolerantie zouden
-- legacy-wedstrijden uit de bezetting vallen → onderschatte bezetting → dubbele boekingen.
--
-- CREATE OR ALTER is idempotent en houdt de definitie gelijk aan
-- Database/planner/Views/AlleWedstrijdenOpVeld.sql — houd beide synchroon.
--
-- ORDER BY [ClubCode] i.p.v. ORDER BY [Id]: dbo.AppSettings heeft geen Id-kolom, dus de
-- definitie in het DB-project compileerde niet (Msg 207) — zelfde valkuil als #564.
--
-- #719: veldkoppeling via OUTER APPLY i.p.v. RTRIM(LEFT(m.[veld], 6)). Die afkap registreerde een
-- wedstrijd op "veld 10" als bezetting op "veld 1" — veld 10 leek daardoor vrij, waarop er een tweede
-- wedstrijd op hetzelfde veld en tijdstip bij kon: een dubbele boeking. Een veldnaam langer dan zes
-- tekens ("hoofdveld") viel volledig uit de bezetting weg. De matching is nu gelijk aan
-- FunctionApp/Planner/VeldResolutie.cs en PlannerShared.ResolveVeld. VeldResolutieDriftTests bewaakt
-- dat deze kopie en die in het DB-project niet uit elkaar lopen — CI rolt alléén dit script uit.
-- ============================================================
CREATE OR ALTER VIEW [planner].[AlleWedstrijdenOpVeld]
AS
SELECT
    CAST(m.[kaledatum] AS DATE)                                                     AS Datum,
    CAST(m.[aanvangstijd] AS TIME)                                                  AS AanvangsTijd,
    DATEADD(MINUTE,
        s.[WedstrijdTotaal],
        CAST(CAST(m.[kaledatum] AS DATE) AS DATETIME) + CAST(m.[aanvangstijd] AS DATETIME)
    )                                                                               AS EindTijd,
    v.[VeldNummer],
    COALESCE(s.[Veldafmeting], 1.00)                                                AS VeldDeelGebruik,
    t.[leeftijdscategorie]                                                          AS LeeftijdsCategorie,
    m.[teamnaam]                                                                    AS TeamNaam,
    m.[wedstrijd]                                                                   AS Wedstrijd,
    v.[Subpositie]                                                                  AS VeldSubpositie,
    'Competitie'                                                                    AS Bron,
    ISNULL(m.[ClubCode], a.[ClubCode])                                              AS ClubCode,
    CAST(m.[wedstrijdcode] AS BIGINT)                                               AS Wedstrijdcode
FROM [his].[matches] m
CROSS APPLY (SELECT TOP 1 [ClubCode], [Accommodatie] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1 ORDER BY [ClubCode]) a
LEFT JOIN [his].[teams] t
    ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
   AND ISNULL(t.[ClubCode], a.[ClubCode]) = ISNULL(m.[ClubCode], a.[ClubCode])
LEFT JOIN [dbo].[Speeltijden] s
    ON s.[Leeftijd] = CASE
        WHEN m.[teamnaam] LIKE a.[ClubCode] + ' G[0-9]%' THEN 'G'
        ELSE REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
    END
   AND s.[ClubCode] = a.[ClubCode]
OUTER APPLY (
    SELECT TOP 1
        vv.[VeldNummer],
        NULLIF(LTRIM(SUBSTRING(LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' '))), LEN(vn.[Naam]) + 1, 100)), '') AS [Subpositie]
    FROM [dbo].[Velden] vv
    CROSS APPLY (SELECT LTRIM(RTRIM(REPLACE(ISNULL(vv.[VeldNaam], ''), '  ', ' '))) AS [Naam]) vn
    WHERE vv.[ClubCode] = a.[ClubCode]
      AND LEN(vn.[Naam]) > 0
      AND (
            LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' '))) = vn.[Naam]
            OR (
                 LEN(LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' ')))) > LEN(vn.[Naam])
                 AND LEFT(LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' '))), LEN(vn.[Naam])) = vn.[Naam]
                 AND SUBSTRING(LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' '))), LEN(vn.[Naam]) + 1, 1) = ' '
               )
          )
    ORDER BY LEN(vn.[Naam]) DESC
) v
WHERE m.[accommodatie] LIKE '%' + a.[Accommodatie] + '%'
  AND m.[status] <> 'Afgelast'
  AND m.[aanvangstijd] IS NOT NULL
  AND v.[VeldNummer] IS NOT NULL
  AND s.[WedstrijdTotaal] IS NOT NULL

UNION ALL

SELECT
    [Datum],
    [AanvangsTijd],
    [EindTijd],
    [VeldNummer],
    [VeldDeelGebruik],
    [LeeftijdsCategorie],
    [TeamNaam],
    COALESCE([TeamNaam], '') + ' - ' + COALESCE([Tegenstander], '')                 AS Wedstrijd,
    ''                                                                              AS VeldSubpositie,
    'Planner'                                                                       AS Bron,
    [ClubCode],
    [SportlinkWedstrijdCode]                                                        AS Wedstrijdcode
FROM [planner].[GeplandeWedstrijden]
WHERE [Status] <> 'Geannuleerd'
  AND [IsVervallen] = 0;
GO

-- ============================================================
-- #598: verwijder de resterende club-specifieke DEFAULT-constraints op ClubCode.
--
-- Een clubnaam als DB-default breekt de multi-club architectuur voor elke fork. Deze vijf objecten
-- zijn bij eerdere opschoonrondes (#435 voor Speeltijden/TeamRegels/VeldBeschikbaarheid/Velden,
-- #242 voor EmailVerwerking) gemist. Patroon gelijk aan #242: DROP DEFAULT + CHECK (LEN > 0),
-- zodat de NOT NULL-garantie blijft maar er geen waarde meer stilzwijgend wordt ingevuld.
-- ============================================================
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_AppSettings_ClubCode')
    ALTER TABLE [dbo].[AppSettings] DROP CONSTRAINT [DF_AppSettings_ClubCode];
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_AppSettingsAudit_ClubCode')
    ALTER TABLE [dbo].[AppSettingsAudit] DROP CONSTRAINT [DF_AppSettingsAudit_ClubCode];
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_TeamVoorkeurTijden_ClubCode')
    ALTER TABLE [dbo].[TeamVoorkeurTijden] DROP CONSTRAINT [DF_TeamVoorkeurTijden_ClubCode];
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_UitgeslotenEmailAdressen_ClubCode')
    ALTER TABLE [dbo].[UitgeslotenEmailAdressen] DROP CONSTRAINT [DF_UitgeslotenEmailAdressen_ClubCode];
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_EmailTemplateInstellingen_ClubCode')
    ALTER TABLE [dbo].[EmailTemplateInstellingen] DROP CONSTRAINT [DF_EmailTemplateInstellingen_ClubCode];
GO

-- Onbedoeld naamloze DEFAULT op dbo.AppSettings.ClubCode (uit de oudere ADD COLUMN-migratie zonder
-- expliciete constraintnaam) — opzoeken via de kolom i.p.v. de naam.
DECLARE @dfNaam SYSNAME = (
    SELECT dc.name FROM sys.default_constraints dc
    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID('dbo.AppSettings') AND c.name = 'ClubCode');
IF @dfNaam IS NOT NULL
    EXEC('ALTER TABLE [dbo].[AppSettings] DROP CONSTRAINT [' + @dfNaam + ']');
GO

-- NOT NULL-garantie behouden zonder default: lege ClubCode expliciet verbieden.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AppSettings_ClubCode')
    ALTER TABLE [dbo].[AppSettings] ADD CONSTRAINT [CK_AppSettings_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AppSettingsAudit_ClubCode')
    ALTER TABLE [dbo].[AppSettingsAudit] ADD CONSTRAINT [CK_AppSettingsAudit_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_TeamVoorkeurTijden_ClubCode')
    ALTER TABLE [dbo].[TeamVoorkeurTijden] ADD CONSTRAINT [CK_TeamVoorkeurTijden_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_UitgeslotenEmailAdressen_ClubCode')
    ALTER TABLE [dbo].[UitgeslotenEmailAdressen] ADD CONSTRAINT [CK_UitgeslotenEmailAdressen_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_EmailTemplateInstellingen_ClubCode')
    ALTER TABLE [dbo].[EmailTemplateInstellingen] ADD CONSTRAINT [CK_EmailTemplateInstellingen_ClubCode] CHECK (LEN([ClubCode]) > 0);
GO

-- ============================================================
-- #595: tabellen die alleen in het DB-project (.sqlproj) bestonden en daardoor ontbraken op een
-- verse deploy. De deploy-pipeline publiceerde nooit een dacpac, dus elk schema-object moet ook
-- hier idempotent staan. Zonder deze guards faalden productiefuncties met "Invalid object name":
--   - avg.ImportLog          — exports/import-teambegeleiding-to-sql.ps1, avg.sp_CleanupImportLog
--   - planner.HerplanVerzoeken — PlannerMatchRepository.cs (mist bovendien ClubCode)
--   - dbo.Zonsondergang      — AutoPlanService.cs e.a.
-- Definities gelijk houden aan Database/{avg,planner,dbo}/Tables/*.sql.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'avg')
    EXEC('CREATE SCHEMA [avg]');
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('avg.ImportLog'))
BEGIN
    CREATE TABLE [avg].[ImportLog] (
        [Id]               INT            IDENTITY (1, 1) NOT NULL,
        [ImportDatum]      DATETIME       CONSTRAINT [DF_avg_ImportLog_ImportDatum] DEFAULT (GETUTCDATE()) NOT NULL,
        [AantalRijen]      INT            NOT NULL,
        [CsvBestand]       NVARCHAR (500) NULL,
        [ImporterendeDoor] NVARCHAR (200) NULL,
        [Duur_ms]          INT            NULL,
        [ClubCode]         NVARCHAR (20)  NOT NULL CONSTRAINT [DF_avg_ImportLog_ClubCode] DEFAULT '',
        CONSTRAINT [PK_avg_ImportLog] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'planner')
    EXEC('CREATE SCHEMA [planner]');
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('planner.HerplanVerzoeken'))
BEGIN
    CREATE TABLE [planner].[HerplanVerzoeken] (
        [Id]                    INT            IDENTITY(1,1) NOT NULL,
        [Wedstrijdcode]         BIGINT         NOT NULL,
        [HuidigeWedstrijd]      NVARCHAR(200)  NOT NULL,
        [HuidigeDatum]          DATE           NOT NULL,
        [HuidigeAanvangsTijd]   TIME           NOT NULL,
        [HuidigeVeldNaam]       NVARCHAR(50)   NULL,
        [GewensteAanvangsTijd]  TIME           NOT NULL,
        [GewenstVeldNummer]     INT            NULL,
        [Status]                NVARCHAR(20)   NOT NULL CONSTRAINT [DF_HerplanVerzoeken_Status] DEFAULT 'Aangevraagd',
        [AangevraagdDoor]       NVARCHAR(200)  NULL,
        [Opmerking]             NVARCHAR(500)  NULL,
        [mta_inserted]          DATETIME       NOT NULL CONSTRAINT [DF_HerplanVerzoeken_Ins] DEFAULT GETUTCDATE(),
        [mta_modified]          DATETIME       NOT NULL CONSTRAINT [DF_HerplanVerzoeken_Mod] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_HerplanVerzoeken] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.Zonsondergang'))
BEGIN
    CREATE TABLE [dbo].[Zonsondergang] (
        [Datum]         DATE  NOT NULL,
        [Zonsondergang] TIME  NOT NULL,
        CONSTRAINT [PK_Zonsondergang] PRIMARY KEY CLUSTERED ([Datum] ASC)
    );
END
GO

-- #595: ClubCode-discriminator op planner.HerplanVerzoeken (multi-club invariant).
-- Zelfde nullable -> backfill -> NOT NULL patroon als planner.GeplandeWedstrijden (#428), en om
-- dezelfde reden in aparte batches: SQL Server bindt kolomnamen bij batch-compilatie, dus DML die
-- de nieuwe kolom noemt moet ná een GO staan (vgl. #564).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.HerplanVerzoeken') AND name = 'ClubCode')
    ALTER TABLE [planner].[HerplanVerzoeken] ADD [ClubCode] NVARCHAR(20) NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.HerplanVerzoeken') AND name = 'ClubCode')
   AND EXISTS (SELECT 1 FROM [planner].[HerplanVerzoeken] WHERE [ClubCode] IS NULL)
    UPDATE [planner].[HerplanVerzoeken]
    SET [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1)
    WHERE [ClubCode] IS NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('planner.HerplanVerzoeken')
             AND name = 'ClubCode' AND is_nullable = 1)
   AND NOT EXISTS (SELECT 1 FROM [planner].[HerplanVerzoeken] WHERE [ClubCode] IS NULL)
    ALTER TABLE [planner].[HerplanVerzoeken] ALTER COLUMN [ClubCode] NVARCHAR(20) NOT NULL;
GO

-- ============================================================
-- #606: indexen op de his.*-tabellen (business key + ClubCode).
--
-- De his-tabellen zijn heaps: elke join/filter op de business key of ClubCode doet een full scan.
-- Er is bovendien geen enkele schema-garantie tegen duplicaten als de MERGE ON-matching ooit
-- misaligneert (type-cast/whitespace) — een stil datakwaliteitsrisico.
--
-- De unieke index wordt alleen aangemaakt als de data dat toestaat. De Sportlink /teams-feed
-- levert aantoonbaar duplicaten (#569, nog open), en een mislukte CREATE UNIQUE INDEX zou de
-- volledige deploy laten falen. Blijven er duplicaten staan, dan komt er een niet-unieke index —
-- de performancewinst is er dan wel, en de uniciteit volgt zodra #569 is opgelost.
-- ============================================================
DECLARE @hisTabellen TABLE ([Tabel] SYSNAME, [Bk] SYSNAME);
INSERT INTO @hisTabellen ([Tabel], [Bk]) VALUES
    ('teams',        'bk_teams'),
    ('matches',      'bk_matches'),
    ('matchdetails', 'bk_WedstrijdCode');

DECLARE @tabel SYSNAME, @bk SYSNAME, @sql NVARCHAR(MAX), @objId INT, @duplicaten INT;
DECLARE hisCur CURSOR LOCAL FAST_FORWARD FOR SELECT [Tabel], [Bk] FROM @hisTabellen;
OPEN hisCur;
FETCH NEXT FROM hisCur INTO @tabel, @bk;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @objId = OBJECT_ID('his.' + @tabel);

    -- Alleen als de tabel én de business-key-kolom bestaan, en er nog geen bk-index is.
    IF @objId IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = @bk)
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = 'UQ_' + @tabel + '_bk')
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = 'IX_' + @tabel + '_bk')
    BEGIN
        -- Duplicaten tellen; bepaalt of de index uniek kan zijn.
        SET @sql = N'SELECT @cnt = COUNT(*) FROM (SELECT ' + QUOTENAME(@bk) +
                   N' FROM [his].' + QUOTENAME(@tabel) +
                   N' GROUP BY ' + QUOTENAME(@bk) + N' HAVING COUNT(*) > 1) d;';
        EXEC sp_executesql @sql, N'@cnt INT OUTPUT', @cnt = @duplicaten OUTPUT;

        IF @duplicaten = 0
        BEGIN
            SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX ' + QUOTENAME('UQ_' + @tabel + '_bk') +
                       N' ON [his].' + QUOTENAME(@tabel) + N' (' + QUOTENAME(@bk) + N');';
            EXEC sp_executesql @sql;
            PRINT 'his.' + @tabel + ': unieke index op ' + @bk + ' aangemaakt.';
        END
        ELSE
        BEGIN
            SET @sql = N'CREATE NONCLUSTERED INDEX ' + QUOTENAME('IX_' + @tabel + '_bk') +
                       N' ON [his].' + QUOTENAME(@tabel) + N' (' + QUOTENAME(@bk) + N');';
            EXEC sp_executesql @sql;
            PRINT 'his.' + @tabel + ': ' + CAST(@duplicaten AS VARCHAR(10)) +
                  ' dubbele business keys gevonden (zie #569) — niet-unieke index aangemaakt.';
        END
    END

    -- Ondersteunende index op ClubCode.
    IF @objId IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = 'ClubCode')
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = 'IX_' + @tabel + '_ClubCode')
    BEGIN
        SET @sql = N'CREATE NONCLUSTERED INDEX ' + QUOTENAME('IX_' + @tabel + '_ClubCode') +
                   N' ON [his].' + QUOTENAME(@tabel) + N' ([ClubCode]);';
        EXEC sp_executesql @sql;
    END

    FETCH NEXT FROM hisCur INTO @tabel, @bk;
END

CLOSE hisCur;
DEALLOCATE hisCur;
GO

-- ============================================================
-- #595 (uitbreiding): dbo.Season en mta.source_target_mapping ontbraken om dezelfde reden.
--
-- Gevonden door de nieuwe schema-drift check in .github/workflows/build.yml. Beide tabellen stonden
-- alleen in het DB-project:
--   - dbo.Season               — sp_UpdateSeasonTable doet alleen INSERT, maakt de tabel niet aan
--   - mta.source_target_mapping — stuurt sp_CreateTargetTableFromSource + sp_MergeStgToHis; zonder
--                                 deze tabel (of zonder rijen) doet de volledige ETL stilzwijgend niets
--
-- De seed-rijen stonden in Database/mta/Tables/source_target_mapping.sql uitsluitend als SQL-comment,
-- dus zelfs een dacpac-publish liet de tabel leeg achter. De drie rijen hieronder zijn exact de
-- configuratie die in productie draait. Alle waarden zijn clubneutraal.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.Season'))
BEGIN
    -- #631: Name is NOT NULL met unique constraint, zodat een verse installatie meteen beschermd
    -- is tegen duplicaatseizoenen. Bestaande installaties worden hierboven opgeruimd en gemigreerd.
    CREATE TABLE [dbo].[Season] (
        [Name]      NCHAR(9) NOT NULL,
        [DateFrom]  DATE     NULL,
        [DateUntil] DATE     NULL,
        CONSTRAINT [UQ_Season_Name] UNIQUE ([Name])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'mta')
    EXEC('CREATE SCHEMA [mta]');
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('mta.source_target_mapping'))
BEGIN
    CREATE TABLE [mta].[source_target_mapping] (
        [Id]            INT IDENTITY(1,1) NOT NULL,
        [source_type]   INT            NULL,
        [source_root]   NVARCHAR(250)  NULL,
        [source_schema] NVARCHAR(10)   NULL,
        [source_entity] NVARCHAR(250)  NULL,
        [source_pk]     NVARCHAR(250)  NULL,
        [target_type]   INT            NULL,
        [target_root]   NVARCHAR(250)  NULL,
        [target_schema] NVARCHAR(10)   NULL,
        [target_entity] NVARCHAR(250)  NULL,
        [target_pk]     NVARCHAR(250)  NULL
    );
END
GO

-- Seed per rij idempotent: bestaande installaties houden hun eigen aanpassingen.
INSERT INTO [mta].[source_target_mapping]
    ([source_type], [source_root], [source_schema], [source_entity], [source_pk],
     [target_type], [target_root], [target_schema], [target_entity], [target_pk])
SELECT v.* FROM (VALUES
    (0, 'SportlinkSqlDb', 'stg', 'teams',        '[teamcode],[lokaleteamcode],[poulecode]', 0, 'SportlinkSqlDb', 'his', 'teams',        'bk_teams NVARCHAR(100)'),
    (0, 'SportlinkSqlDb', 'stg', 'matches',      '[wedstrijdcode]',                         0, 'SportlinkSqlDb', 'his', 'matches',      'bk_matches NVARCHAR(100)'),
    (0, 'SportlinkSqlDb', 'stg', 'matchdetails', '[WedstrijdCode]',                         0, 'SportlinkSqlDb', 'his', 'matchdetails', 'bk_WedstrijdCode INT')
) AS v([source_type], [source_root], [source_schema], [source_entity], [source_pk],
       [target_type], [target_root], [target_schema], [target_entity], [target_pk])
WHERE NOT EXISTS (
    SELECT 1 FROM [mta].[source_target_mapping] m
    WHERE m.[source_schema] = v.[source_schema] AND m.[source_entity] = v.[source_entity]
      AND m.[target_schema] = v.[target_schema] AND m.[target_entity] = v.[target_entity]
);
GO

-- ============================================================
-- #595 (uitbreiding): stored procedures en views uit het DB-project.
--
-- Ook deze objecten stonden alleen in het DB-project en ontbraken dus volledig op een verse deploy.
-- Dat trof de kern van de ETL: Script.PostDeployment1.sql riep dbo.sp_UpdateSeasonTable al aan
-- zonder die procedure ooit aan te maken, en zonder sp_CreateTargetTableFromSource /
-- sp_MergeStgToHis draait de hele Sportlink-pipeline niet.
--
-- Gevonden door de uitgebreide schema-drift check in .github/workflows/build.yml.
--
-- CREATE OR ALTER is idempotent en houdt de definitie gelijk aan de bronbestanden onder
-- Database/. Wijzig een object dus altijd op BEIDE plekken, of genereer dit blok opnieuw.
-- ============================================================

-- Bron: Database/dbo/System Stored Procedures/sp_CreateDateTable.sql
CREATE OR ALTER PROCEDURE [dbo].[sp_CreateDateTable]
	@YearStart as int,
	@YearEnd   as int 
AS

IF  EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DateTable]') AND type in (N'U'))
	DROP TABLE [dbo].[DateTable]

CREATE TABLE dbo.DateTable (
    Date DATE PRIMARY KEY,
    Day INT NOT NULL,
    Month INT NOT NULL,
    Year INT NOT NULL,
    Quarter INT NOT NULL,
    DayOfWeek INT NOT NULL, -- 1 = Monday, 7 = Sunday
    DayName VARCHAR(10) NOT NULL,
    MonthName VARCHAR(15) NOT NULL,
	IsWeekend BIT NOT NULL
);

-- Populate the table with dates
WITH RecursiveDates AS (
    SELECT DATEFROMPARTS(@YearStart,1,1) AS Date -- Starting date
    UNION ALL
    SELECT DATEADD(DAY, 1, Date) 
    FROM RecursiveDates
    WHERE Date < DATEFROMPARTS(@YearEnd,12,31) -- Ending date
)

INSERT INTO dbo.DateTable (Date, Day, Month, Year, Quarter, DayOfWeek, DayName, MonthName, IsWeekend)
SELECT
    d.Date,
    DAY(d.Date) AS Day,
    MONTH(d.Date) AS Month,
    YEAR(d.Date) AS Year,
    DATEPART(QUARTER, d.Date) AS Quarter,
    -- DATEPART(WEEKDAY) is afhankelijk van de sessie-instelling DATEFIRST (default 7 = zondag bij
    -- us_english). Daardoor gaf de oude berekening Monday = 2 in plaats van de gedocumenteerde 1,
    -- en markeerde IsWeekend vrijdag als weekend en zondag NIET als weekend.
    -- DATEDIFF vanaf 1900-01-01 (een maandag) is DATEFIRST-onafhankelijk en dus deterministisch.
    (DATEDIFF(DAY, '19000101', d.Date) % 7) + 1 AS DayOfWeek,
    DATENAME(WEEKDAY, d.Date) AS DayName,
    DATENAME(MONTH, d.Date) AS MonthName,
    CASE WHEN ((DATEDIFF(DAY, '19000101', d.Date) % 7) + 1) IN (6, 7) THEN 1 ELSE 0 END AS IsWeekend
FROM    RecursiveDates d
OPTION (MAXRECURSION 0); -- Allows for recursive CTE to handle larger datasets
GO

-- Bron: Database/dbo/System Stored Procedures/sp_UpdateSeasonTable.sql
CREATE OR ALTER PROCEDURE [dbo].[sp_UpdateSeasonTable]
    @SeasonStartMonth INT
AS
BEGIN
	DECLARE @YearStart INT;
	DECLARE @YearEnd   INT;

	-- No seasons found! Add last two seasons
	IF (SELECT YEAR(MAX(DateUntil)) FROM [dbo].[Season]) IS NULL 
	BEGIN
		INSERT INTO [dbo].[Season]
			(
			[Name],
			[DateFrom],
			[DateUntil]
			)
		VALUES 
			(
			CONCAT(YEAR(GETDATE())-2,'-',YEAR(GETDATE())-1),
			DATEFROMPARTS(YEAR(GETDATE())-2,@SeasonStartMonth,1),
			EOMONTH(DATEFROMPARTS(YEAR(GETDATE())-1,@SeasonStartMonth-1,1))
			),
			(
			CONCAT(YEAR(GETDATE())-1,'-',YEAR(GETDATE())),
			DATEFROMPARTS(YEAR(GETDATE())-1,@SeasonStartMonth,1),
			EOMONTH(DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth-1,1))
			);
	END

	-- Create 2 months before start of a new season a new record in season table
	--
	-- #631: de guard controleerde YEAR(MAX(DateUntil)) <> YEAR(GETDATE())+1. Zodra er een
	-- TOEKOMSTIG seizoen in de tabel staat is MAX(DateUntil) niet meer het huidige seizoen en kan
	-- die vergelijking nooit meer onwaar worden. In productie stond 2027-2028, dus de conditie was
	-- permanent waar en werd 2026-2027 bij ELKE deploy opnieuw ingevoegd (3 identieke rijen).
	-- Nu wordt gecontroleerd of het seizoen dat we gaan maken al bestaat. Dat is idempotent en
	-- werkt ook met toekomstige seizoenen in de tabel.
	DECLARE @NewSeasonName NCHAR(9) = CONCAT(YEAR(GETDATE()),'-',YEAR(GETDATE())+1);

	IF NOT EXISTS (SELECT 1 FROM [dbo].[Season] WHERE [Name] = @NewSeasonName)
		AND GETDATE() >= DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth-2,1)
	BEGIN
		INSERT INTO [dbo].[Season]
			(
			[Name],
			[DateFrom],
			[DateUntil]
			)
		 VALUES
			(
			@NewSeasonName,
			DATEFROMPARTS(YEAR(GETDATE()),@SeasonStartMonth,1),
			EOMONTH(DATEFROMPARTS(YEAR(GETDATE())+1,@SeasonStartMonth-1,1))
			)
	END;

	-- Create a new DateTable based on the new start and enddate in seasons table
	SELECT @YearStart = YEAR(MIN(DateFrom))  FROM [dbo].[Season];
	SELECT @YearEnd   = YEAR(MAX(DateUntil)) FROM [dbo].[Season];
	EXEC [dbo].[sp_CreateDateTable] @YearStart, @YearEnd;
END;
GO

-- Bron: Database/dbo/System Stored Procedures/sp_CreateTargetTableFromSource.sql
CREATE OR ALTER PROCEDURE [dbo].[sp_CreateTargetTableFromSource]
	@SourceSchema NVARCHAR(128),
	@SourceName   NVARCHAR(128),
	@TargetSchema NVARCHAR(128),
	@TargetName   NVARCHAR(128)
AS
BEGIN
	/*
	version | date			| name					| description
	1.0		| 12-01-2025	| Jaap van Beusekom		| Initial setup
	1.1		| 2025			| Jaap van Beusekom		| Fixed target table name using @TargetName instead of @SourceName
	*/

	IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[' + @TargetSchema + '].[' + @TargetName +']') AND type in (N'U'))
	BEGIN
		SET NOCOUNT ON;
		DECLARE @SqlString NVARCHAR(MAX) = 'CREATE TABLE ' + QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName) + ' (';

		-- Fetch the primary key field from the mapping table
		DECLARE @MtaTargetKey NVARCHAR(128);
    
		SELECT @MtaTargetKey = stm.target_pk
		  FROM mta.source_target_mapping stm
		 WHERE stm.source_entity = @SourceName 
		   AND stm.source_schema = @SourceSchema 
		   AND stm.target_entity = @TargetName
		   AND stm.target_schema = @TargetSchema;

		-- Add keyfield if exist at first
		IF @MtaTargetKey IS NOT NULL
			SET @SqlString += '' + @MtaTargetKey + ' NOT NULL,'		

		-- Fetch metadata sourcetable columns
		SELECT @SqlString += 
			QUOTENAME(c.name) + ' ' + 
			t.name + 
			CASE 
			WHEN t.name IN ('varchar', 'nvarchar', 'char', 'nchar') 
			THEN '(' + 
				CASE 
				WHEN c.max_length = -1 THEN 'MAX' 
				WHEN t.name IN ('nvarchar','nchar') THEN CAST(c.max_length / 2 AS VARCHAR) 
				ELSE CAST(c.max_length AS VARCHAR) 
				END + ')'
			ELSE '' 
			END + ' ' +
			CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NOT NULL' END + ', '
		FROM sys.tables st
		INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
		INNER JOIN sys.all_columns c ON c.object_id = st.object_id
		LEFT JOIN sys.types t ON c.user_type_id = t.user_type_id
		WHERE ss.name = @SourceSchema 
		  AND st.name = @SourceName;

		-- Add additional metadata columns
		SET @SqlString += '
			mta_inserted DATETIME NULL,
			mta_modified DATETIME NULL,
			mta_deleted  DATETIME NULL
		);';

		-- Execute this SQL command
		EXEC sp_executesql @SqlString;
		-- Output the generated SQL for verification
		-- PRINT @SqlString;

		/*
			#606: index op de business key en op ClubCode.

			Zonder index doet elke join/filter op de business key of ClubCode een full heap scan, en
			niets in het schema voorkomt duplicaten als de MERGE ON-matching ooit misaligneert.

			De business-key-index is UNIEK omdat de tabel hier net leeg is aangemaakt — dat kan dus
			niet falen. Voor bestaande tabellen gebeurt hetzelfde in Script.PostDeployment1.sql, daar
			wél voorwaardelijk: de Sportlink-feed levert aantoonbaar duplicaten (#569), en een
			mislukte CREATE UNIQUE INDEX zou de hele deploy laten falen.

			@MtaTargetKey heeft de vorm 'kolomnaam DATATYPE' — zelfde parsing als sp_MergeStgToHis.
		*/
		IF @MtaTargetKey IS NOT NULL
		BEGIN
			DECLARE @BkColumn NVARCHAR(128) =
				CASE WHEN CHARINDEX(' ', @MtaTargetKey) > 0
					 THEN LEFT(@MtaTargetKey, CHARINDEX(' ', @MtaTargetKey) - 1)
					 ELSE @MtaTargetKey END;

			DECLARE @IndexSql NVARCHAR(MAX) =
				'CREATE UNIQUE NONCLUSTERED INDEX ' + QUOTENAME('UQ_' + @TargetName + '_bk') +
				' ON ' + QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName) +
				' (' + QUOTENAME(@BkColumn) + ');';
			EXEC sp_executesql @IndexSql;
		END

		-- Ondersteunende index op ClubCode voor his-tabellen die de discriminator hebben.
		IF EXISTS (SELECT 1 FROM sys.columns
				   WHERE object_id = OBJECT_ID(QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName))
					 AND name = 'ClubCode')
		BEGIN
			DECLARE @ClubIndexSql NVARCHAR(MAX) =
				'CREATE NONCLUSTERED INDEX ' + QUOTENAME('IX_' + @TargetName + '_ClubCode') +
				' ON ' + QUOTENAME(@TargetSchema) + '.' + QUOTENAME(@TargetName) +
				' ([ClubCode]);';
			EXEC sp_executesql @ClubIndexSql;
		END
	END
	--ELSE
	--BEGIN
	--	PRINT '[' + @TargetSchema + '].[' + @TargetName +'] already exists'
	--END
END;
GO

-- Bron: Database/dbo/System Stored Procedures/sp_MergeStgToHis.sql
CREATE OR ALTER PROCEDURE [dbo].[sp_MergeStgToHis]
    @SourceSchema nvarchar(128),
    @SourceName   nvarchar(128),
    @TargetSchema nvarchar(128),
    @TargetName   nvarchar(128)
AS
BEGIN
    /*
    version | date       | name              | description
    1.0     | 12-01-2025 | Jaap van Beusekom | Initial setup
    1.1     | 25-01-2025 | Jaap van Beusekom | NULL handling for non-string columns using CAST AS NVARCHAR(MAX)
    1.2     | 2025       | Jaap van Beusekom | Multi-column business key support using CONCAT
    */
    SET NOCOUNT ON;
    -- Create the target table from source structure if it does not yet exist
    EXECUTE [dbo].[sp_CreateTargetTableFromSource] @SourceSchema, @SourceName, @TargetSchema, @TargetName;

    DECLARE @ColName        NVARCHAR(MAX);
    DECLARE @Index          INT = 1;
    DECLARE @SqlString      NVARCHAR(MAX) = '';
    DECLARE @SqlStringTmp   NVARCHAR(MAX) = '';

    SET @SqlString +=
'MERGE [' + @TargetSchema + '].[' + @TargetName + '] AS target
USING [' + @SourceSchema + '].[' + @SourceName + '] AS source
';
    -- Retrieve primary key columns and target key definition from metadata
    DECLARE @SourcePk       VARCHAR(255);
    DECLARE @SourcePkColumns VARCHAR(MAX);
    DECLARE @TargetPk       VARCHAR(255);
    DECLARE @TargetPkFull   VARCHAR(255);

    SELECT @SourcePk     = source_pk
         , @TargetPkFull = target_pk
      FROM mta.source_target_mapping
     WHERE source_entity = @SourceName
       AND source_schema = @SourceSchema
       AND target_schema = @TargetSchema
       AND target_entity = @TargetName;

    -- Extract just the column name from target_pk (format: 'columnname DATATYPE')
    IF @TargetPkFull IS NOT NULL
    BEGIN
        IF CHARINDEX(' ', @TargetPkFull) > 0
            SET @TargetPk = LTRIM(RTRIM(SUBSTRING(@TargetPkFull, 1, CHARINDEX(' ', @TargetPkFull) - 1)));
        ELSE
            SET @TargetPk = @TargetPkFull;
    END
    ELSE
        SET @TargetPk = 'bk_' + @TargetName;

    IF @SourcePk IS NULL
        RETURN;

    -- Build the ON clause using source PK columns, casting to NVARCHAR to support any data type
    DECLARE @ColNames TABLE (ColName NVARCHAR(MAX));
    INSERT INTO @ColNames (ColName)
        SELECT TRIM(value) FROM STRING_SPLIT(@SourcePk, ',');

    WHILE EXISTS (SELECT 1 FROM @ColNames WHERE ColName IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = ColName FROM @ColNames WHERE ColName IS NOT NULL;
        IF @Index = 1
        BEGIN
            SET @SqlString +=
'ON ISNULL(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)), '''') = ISNULL(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SourcePkColumns = 'source.' + @ColName + ' ';
        END
        ELSE
        BEGIN
            SET @SqlString +=
'AND ISNULL(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)), '''') = ISNULL(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SourcePkColumns += ', source.' + @ColName + ' ';
        END
        DELETE FROM @ColNames WHERE ColName = @ColName;
        SET @Index += 1;
    END

    SET @SqlString += '
WHEN MATCHED AND (';

    -- Build all source columns and non-PK source columns
    DECLARE @SourceTableColumns TABLE (TableColumn NVARCHAR(MAX));
    INSERT INTO @SourceTableColumns (TableColumn)
        SELECT '[' + c.name + ']'
        FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        WHERE ss.name = @SourceSchema AND st.name = @SourceName;

    DECLARE @SourceTableColumnsNoPk TABLE (TableColumn NVARCHAR(MAX));
    INSERT INTO @SourceTableColumnsNoPk (TableColumn)
        SELECT '[' + c.name + ']'
        FROM sys.tables st
        INNER JOIN sys.schemas ss ON ss.schema_id = st.schema_id
        INNER JOIN sys.all_columns c ON c.object_id = st.object_id
        WHERE ss.name = @SourceSchema AND st.name = @SourceName
          AND '[' + c.name + ']' NOT IN (SELECT '[' + TRIM(value) + ']' FROM STRING_SPLIT(@SourcePk, ','));

    -- Build the WHEN MATCHED condition and UPDATE SET clause
    SET @Index = 1;
    WHILE EXISTS (SELECT 1 FROM @SourceTableColumnsNoPk WHERE TableColumn IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = TableColumn FROM @SourceTableColumnsNoPk WHERE TableColumn IS NOT NULL;
        IF @Index = 1
        BEGIN
            SET @SqlString  += '
    COALESCE(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)), '''') <> COALESCE(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SqlStringTmp += '
    target.' + @ColName + ' = source.' + @ColName + ',';
        END
        ELSE
        BEGIN
            SET @SqlString  += '
 OR COALESCE(CAST(target.' + @ColName + ' AS NVARCHAR(MAX)), '''') <> COALESCE(CAST(source.' + @ColName + ' AS NVARCHAR(MAX)), '''') ';
            SET @SqlStringTmp += '
    target.' + @ColName + ' = source.' + @ColName + ',';
        END
        DELETE FROM @SourceTableColumnsNoPk WHERE TableColumn = @ColName;
        SET @Index += 1;
    END

    SET @SqlString += ')
THEN UPDATE SET '
        + @SqlStringTmp + '
    target.mta_modified = GETUTCDATE()';

    SET @SqlString += '
WHEN NOT MATCHED BY TARGET THEN ';

    -- Build the INSERT column list and VALUES for new records
    DECLARE @SqlStringTargets NVARCHAR(MAX) = '';
    DECLARE @SqlStringValues  NVARCHAR(MAX) = '';

    WHILE EXISTS (SELECT 1 FROM @SourceTableColumns WHERE TableColumn IS NOT NULL)
    BEGIN
        SELECT TOP 1 @ColName = TableColumn FROM @SourceTableColumns WHERE TableColumn IS NOT NULL;
        SET @SqlStringTargets += ', ' + @ColName;
        SET @SqlStringValues  += ', source.' + @ColName;
        DELETE FROM @SourceTableColumns WHERE TableColumn = @ColName;
    END

    -- CONCAT supports both single and multi-column source keys
    SET @SqlString += '
INSERT (' + @TargetPk + @SqlStringTargets + ', mta_inserted, mta_modified)
VALUES (CONCAT('''', ' + @SourcePkColumns + ')' + @SqlStringValues + ', GETUTCDATE(), GETUTCDATE());';

    EXEC sp_executesql @SqlString;

    PRINT 'Merged ' + @SourceName + ' into ' + @TargetName;
END;
GO

-- Bron: Database/pub/Views/DateTable.sql
CREATE OR ALTER VIEW [pub].[DateTable]
	AS 
SELECT dt.[Date]
      ,dt.[Day]
      ,dt.[Month]
      ,dt.[Year]
      ,dt.[Quarter]
      ,dt.[DayOfWeek]
      ,dt.[DayName]
      ,dt.[MonthName]
      ,dt.[IsWeekend]
	  ,s.Name           As Season
  FROM [dbo].[DateTable] dt
  INNER JOIN [dbo].[Season] s
	ON dt.Date BETWEEN s.DateFrom AND s.DateUntil;
GO

-- Bron: Database/pub/Views/Teams.sql
CREATE OR ALTER VIEW [pub].[Teams]
	AS 
SELECT [bk_teams]			AS [TeamBk]
      ,[teamcode]			AS [TeamCode]
      ,[lokaleteamcode]		AS [TeamCodeLokaal]
      ,[teamnaam]			AS [TeamNaam]
      ,[teamsoort]			AS [TeamSoort]
      ,[geslacht]			AS [Geslacht]
      ,[leeftijdscategorie]	AS [LeeftijdsCategorie]
      ,[competitiesoort]	AS [CompetitieSoort]
      ,[competitienaam]		AS [CompetitieNaam]
      ,[klasse]				AS [CompetitieKlasse]
      ,[poule]				AS [Poule]
      ,[klassepoule]		AS [PouleKlasse]
      ,[poulecode]			AS [PouleCode]
      ,[spelsoort]			AS [SpelSoort]
      ,[speeldag]			AS [Speeldag]
      ,[ClubCode]			AS [ClubCode]
  FROM [his].[teams];
GO

-- Bron: Database/pub/Views/Matches.sql
CREATE OR ALTER VIEW [pub].[Matches]
	AS 
SELECT m.[wedstrijdcode]					AS WedstrijdCode
	,tt.teamnaam							AS VerenigingsTeam
	,md.Categorie							AS CompetitieCategorie
	,m.[competitienaam]						AS CompetitieNaam
	,m.[competitiesoort]					AS CompetitieSoort
	,md.[PouleCode]							AS CompetitiePouleCode
	,m.[wedstrijdnummer]					AS WedstrijdNummer
	,m.[wedstrijd]							AS Wedstrijd
	,md.[WedstrijdType]						AS WestrijdType
	,CAST(m.[datum] AS date)				AS WedstrijdDatum
	,CAST(m.[aanvangstijd] AS time)			AS WedstrijdAanvangsTijd
	,md.[Duration]							AS WestrijdDuur
	,m.[status]								AS WedstrijdStatus
	,md.[VeldNaam]							AS VeldNaam
	,md.[VeldLocatie]						AS VeldLocatie
	,m.[thuisteam]							AS TeamThuis
	,m.[thuisteamid]						AS TeamThuisId
	,m.[uitteam]							AS TeamUit
	,m.[uitteamid]							AS TeamUitId
	,m.[uitslag]							AS Uitslag
	,m.[uitslag-regulier]					AS UitslagRegulier
	,m.[uitslag-nv]							AS UitslagNv
	,m.[uitslag-s]							AS UitslagS
	,md.[ThuisScore]						AS UitslagThuisScore
	,md.[ThuisScoreRegulier]				AS UitslagThuisScoreRegulier
	,md.[ThuisScoreNV]						AS UitslagThuisScoreNv
	,md.[ThuisScoreS]						AS UitslagThuisScoreS
	,md.[UitScore]							AS UitslagUitScore
	,md.[UitScoreRegulier]					AS UitslagUitScoreRegulier
	,md.[UitScoreNV]						AS UitslagUitScoreNv
	,md.[UitScoreS]							AS UitslagUitScoreS
	,m.[verenigingswedstrijd]				AS IsVerenigingsWedstrijd
	,'Thuis'								AS IsThuisUitWedstrijd
	,md.[Opmerkingen]						AS DivOpmerkingen
	,md.[VerenigingScheidsrechterCode]		AS DivOfficialScheidsrechterCode
	,md.[VerenigingScheidsrechter]			AS DivOfficialScheidsrechter
	,md.[OverigeOfficialCode]				AS DivOfficialOverigeCode
	,md.[OverigeOfficial]					AS DivOfficialOverige
	,m.[ClubCode]							AS ClubCode
FROM [his].[matches] m
LEFT JOIN [his].[matchdetails] md ON CAST(md.InternCode AS bigint)=CAST(m.wedstrijdcode AS bigint)
LEFT JOIN [his].[teams] tt ON tt.teamcode = m.thuisteamid AND (LEFT(tt.competitienaam,6) = LEFT(md.competitietype,6) OR md.PouleCode=tt.poulecode)
WHERE tt.teamnaam IS NOT NULL

UNION ALL

SELECT m.[wedstrijdcode]					AS WedstrijdCode
	,tu.teamnaam							AS VerenigingsTeam
	,md.Categorie							AS CompetitieCategorie
	,m.[competitienaam]						AS CompetitieNaam
	,m.[competitiesoort]					AS CompetitieSoort
	,md.[PouleCode]							AS CompetitiePouleCode
	,m.[wedstrijdnummer]					AS WedstrijdNummer
	,m.[wedstrijd]							AS Wedstrijd
	,md.[WedstrijdType]						AS WestrijdType
	,CAST(m.[datum] AS date)				AS WedstrijdDatum
	,CAST(m.[aanvangstijd] AS time)			AS WedstrijdAanvangsTijd
	,md.[Duration]							AS WestrijdDuur
	,m.[status]								AS WedstrijdStatus
	,md.[VeldNaam]							AS VeldNaam
	,md.[VeldLocatie]						AS VeldLocatie
	,m.[thuisteam]							AS TeamThuis
	,m.[thuisteamid]						AS TeamThuisId
	,m.[uitteam]							AS TeamUit
	,m.[uitteamid]							AS TeamUitId
	,m.[uitslag]							AS Uitslag
	,m.[uitslag-regulier]					AS UitslagRegulier
	,m.[uitslag-nv]							AS UitslagNv
	,m.[uitslag-s]							AS UitslagS
	,md.[ThuisScore]						AS UitslagThuisScore
	,md.[ThuisScoreRegulier]				AS UitslagThuisScoreRegulier
	,md.[ThuisScoreNV]						AS UitslagThuisScoreNv
	,md.[ThuisScoreS]						AS UitslagThuisScoreS
	,md.[UitScore]							AS UitslagUitScore
	,md.[UitScoreRegulier]					AS UitslagUitScoreRegulier
	,md.[UitScoreNV]						AS UitslagUitScoreNv
	,md.[UitScoreS]							AS UitslagUitScoreS
	,m.[verenigingswedstrijd]				AS IsVerenigingsWedstrijd
	,'Uit'								AS IsThuisUitWedstrijd
	,md.[Opmerkingen]						AS DivOpmerkingen
	,md.[VerenigingScheidsrechterCode]		AS DivOfficialScheidsrechterCode
	,md.[VerenigingScheidsrechter]			AS DivOfficialScheidsrechter
	,md.[OverigeOfficialCode]				AS DivOfficialOverigeCode
	,md.[OverigeOfficial]					AS DivOfficialOverige
	,m.[ClubCode]							AS ClubCode
  FROM [his].[matches] m
  LEFT JOIN [his].[matchdetails] md ON CAST(md.InternCode AS bigint)=CAST(m.wedstrijdcode AS bigint)
  LEFT JOIN [his].[teams] tu ON tu.teamcode = m.uitteamid   AND (LEFT(tu.competitienaam,6) = LEFT(md.competitietype,6) OR md.PouleCode=tu.poulecode)
  WHERE tu.teamnaam IS NOT NULL

  UNION ALL

  SELECT m.[wedstrijdcode]					AS WedstrijdCode
	,tu.teamnaam							AS VerenigingsTeam
	,md.Categorie							AS CompetitieCategorie
	,m.[competitienaam]						AS CompetitieNaam
	,m.[competitiesoort]					AS CompetitieSoort
	,md.[PouleCode]							AS CompetitiePouleCode
	,m.[wedstrijdnummer]					AS WedstrijdNummer
	,m.[wedstrijd]							AS Wedstrijd
	,md.[WedstrijdType]						AS WestrijdType
	,CAST(m.[datum] AS date)				AS WedstrijdDatum
	,CAST(m.[aanvangstijd] AS time)			AS WedstrijdAanvangsTijd
	,md.[Duration]							AS WestrijdDuur
	,m.[status]								AS WedstrijdStatus
	,md.[VeldNaam]							AS VeldNaam
	,md.[VeldLocatie]						AS VeldLocatie
	,m.[thuisteam]							AS TeamThuis
	,m.[thuisteamid]						AS TeamThuisId
	,m.[uitteam]							AS TeamUit
	,m.[uitteamid]							AS TeamUitId
	,m.[uitslag]							AS Uitslag
	,m.[uitslag-regulier]					AS UitslagRegulier
	,m.[uitslag-nv]							AS UitslagNv
	,m.[uitslag-s]							AS UitslagS
	,md.[ThuisScore]						AS UitslagThuisScore
	,md.[ThuisScoreRegulier]				AS UitslagThuisScoreRegulier
	,md.[ThuisScoreNV]						AS UitslagThuisScoreNv
	,md.[ThuisScoreS]						AS UitslagThuisScoreS
	,md.[UitScore]							AS UitslagUitScore
	,md.[UitScoreRegulier]					AS UitslagUitScoreRegulier
	,md.[UitScoreNV]						AS UitslagUitScoreNv
	,md.[UitScoreS]							AS UitslagUitScoreS
	,m.[verenigingswedstrijd]				AS IsVerenigingsWedstrijd
	,NULL								    AS IsThuisUitWedstrijd
	,md.[Opmerkingen]						AS DivOpmerkingen
	,md.[VerenigingScheidsrechterCode]		AS DivOfficialScheidsrechterCode
	,md.[VerenigingScheidsrechter]			AS DivOfficialScheidsrechter
	,md.[OverigeOfficialCode]				AS DivOfficialOverigeCode
	,md.[OverigeOfficial]					AS DivOfficialOverige
	,m.[ClubCode]							AS ClubCode
  FROM [his].[matches] m
  LEFT JOIN [his].[matchdetails] md ON CAST(md.InternCode AS bigint)=CAST(m.wedstrijdcode AS bigint)
  LEFT JOIN [his].[teams] tt ON tt.teamcode = m.thuisteamid AND (LEFT(tt.competitienaam,6) = LEFT(md.competitietype,6) OR md.PouleCode=tt.poulecode)
  LEFT JOIN [his].[teams] tu ON tu.teamcode = m.uitteamid   AND (LEFT(tu.competitienaam,6) = LEFT(md.competitietype,6) OR md.PouleCode=tu.poulecode)
  WHERE tt.teamnaam IS NULL
	AND tu.teamnaam IS NULL
GO

-- ============================================================
-- #707: AVG-retentieprocedures — CREATE OR ALTER, niet IF NOT EXISTS
--
-- Deze vier procedures stonden eerder verderop in dit script als
-- 'IF NOT EXISTS (... type = ''P'') BEGIN EXEC(N''CREATE PROCEDURE ...'') END'. De deploy-pipeline
-- publiceert geen dacpac — db-migrate runt uitsluitend dit script — en de procedures bestaan al
-- sinds een eerdere release. De guard was dus permanent onwaar en ELKE wijziging aan een van deze
-- vier procedures verdween geruisloos bij de deploy.
--
-- Dat was al ingetreden: de anonimisering van planner.EmailVerwerking.FoutMelding (#420) stond wel
-- in Database/planner/System Stored Procedures/sp_CleanupEmailVerwerking.sql en in de CHANGELOG,
-- maar niet in de gedeployde procedure. Een foutmelding kan een e-mailadres of berichtfragment
-- bevatten, dus productie anonimiseerde een persoonsgegeven niet.
--
-- CREATE OR ALTER mag niet binnen IF/BEGIN staan (moet het eerste statement van de batch zijn),
-- vandaar één eigen batch per procedure — hetzelfde patroon als de #595-sectie hierboven.
--
-- Deze sectie staat bewust NA het aanmaken van de schema's avg en planner: CREATE OR ALTER draait
-- onvoorwaardelijk en faalt op een verse database als het schema nog niet bestaat. De tabellen
-- hoeven nog niet te bestaan (deferred name resolution), maar het schema wel.
--
-- Definities één-op-één gelijk aan de bronbestanden onder
-- Database/{planner,avg}/System Stored Procedures/ — wijzig een procedure dus altijd op BEIDE
-- plekken.
-- ============================================================

-- Bron: Database/planner/System Stored Procedures/sp_CleanupClassificatieCorrectie.sql  (#424)
CREATE OR ALTER PROCEDURE [planner].[sp_CleanupClassificatieCorrectie]
AS
BEGIN
    SET NOCOUNT ON;

    -- AANROEPVOLGORDE: deze procedure loopt vóór sp_CleanupEmailVerwerking, maar de correctheid
    -- hangt daar niet van af. Eerder stond hier dat de volgorde de FK-afhankelijkheid met
    -- planner.EmailVerwerking afdekte — dat was onjuist en misleidend. Deze procedure verwijdert
    -- correctierijen alleen op hun EIGEN leeftijd, en een correctierij kan jonger zijn dan de rij
    -- waarnaar hij verwijst. Een 91 dagen oude e-mailrij met een 51 dagen oude correctie bleef zo
    -- achter en liet de DELETE in sp_CleanupEmailVerwerking structureel op een FK-schending
    -- vastlopen. Het opruimen van die verwijzingen gebeurt daarom in sp_CleanupEmailVerwerking
    -- zelf, met exact dezelfde grens die bepaalt welke ouderrijen verdwijnen.

    -- Beide grenzen eenmalig uit GETUTCDATE(): anders kan een rij tussen de UPDATE en de DELETE
    -- door van venster wisselen.
    DECLARE @AnonimiseerVanaf DATETIME = DATEADD(DAY, -30, GETUTCDATE());
    DECLARE @VerwijderVoor    DATETIME = DATEADD(DAY, -90, GETUTCDATE());

    -- Fase 1: anonimiseer samenvattingen in records 30-90 dagen oud
    UPDATE [planner].[ClassificatieCorrectie]
    SET [OrigineleSamenvatting] = NULL,
        [CorrectieSamenvatting] = NULL,
        [mta_modified]          = GETUTCDATE()
    WHERE [mta_inserted] < @AnonimiseerVanaf
      AND [mta_inserted] >= @VerwijderVoor
      AND ([OrigineleSamenvatting] IS NOT NULL
           OR [CorrectieSamenvatting] IS NOT NULL);

    -- Fase 2: verwijder records ouder dan 90 dagen — de eigen bewaartermijn van deze tabel.
    -- Correctierijen die jonger zijn maar naar een te verwijderen e-mailrij verwijzen, worden
    -- opgeruimd door sp_CleanupEmailVerwerking (fase 2a).
    DELETE FROM [planner].[ClassificatieCorrectie]
    WHERE [mta_inserted] < @VerwijderVoor;
END;
GO

-- Bron: Database/planner/System Stored Procedures/sp_CleanupEmailVerwerking.sql  (#208 / #420)
CREATE OR ALTER PROCEDURE [planner].[sp_CleanupEmailVerwerking]
AS
BEGIN
    SET NOCOUNT ON;

    -- De bewaartermijn van planner.EmailVerwerking staat hier op ÉÉN plek. Beide grenzen worden
    -- eenmalig uit GETUTCDATE() berekend: anders kan een rij tussen twee statements door van
    -- venster wisselen (geanonimiseerd maar niet verwijderd, of omgekeerd).
    DECLARE @AnonimiseerVanaf DATETIME = DATEADD(DAY, -30, GETUTCDATE());
    DECLARE @VerwijderVoor    DATETIME = DATEADD(DAY, -90, GETUTCDATE());

    -- Fase 1: anonimiseer PII in rijen van 30-90 dagen oud
    -- Afzender en Onderwerp zijn NOT NULL → vervangen door placeholder.
    -- Nullbare velden worden op NULL gezet, inclusief FoutMelding (#420): een foutmelding bevat
    -- vaak het e-mailadres of een fragment van het bericht en is dus zelf een persoonsgegeven.
    --
    -- NIET aanraken: [IsBeantwoord] en [VerzendPogingOpUtc] (#718, #716). Dat zijn geen
    -- persoonsgegevens — een boolean en een tijdstip zeggen niets over een persoon — en ze dragen
    -- twee harde grenzen: "wij hebben hierop geantwoord" (replydetectie én duplicaatbescherming) en
    -- "er is een verzendpoging met onbekende uitkomst". Werden die mee geanonimiseerd, dan zou een
    -- reply na 31 dagen niet meer als reply worden herkend en zou een oud bericht een tweede
    -- antwoord kunnen krijgen. Voeg ze dus nooit toe aan de SET-lijst hieronder.
    UPDATE [planner].[EmailVerwerking]
    SET [Afzender]          = '[geanonimiseerd]',
        [Onderwerp]         = '[geanonimiseerd]',
        [VerstuurdNaar]     = NULL,
        [EmailBody]         = NULL,
        [AntwoordEmail]     = NULL,
        [PlannerResponse]   = NULL,
        [GeextraheerdeData] = NULL,
        [FoutMelding]       = NULL,
        [mta_modified]      = GETUTCDATE()
    WHERE [mta_inserted] < @AnonimiseerVanaf
      AND [mta_inserted] >= @VerwijderVoor
      AND ([Afzender] <> '[geanonimiseerd]'
           OR [EmailBody] IS NOT NULL
           OR [AntwoordEmail] IS NOT NULL
           OR [PlannerResponse] IS NOT NULL
           OR [GeextraheerdeData] IS NOT NULL
           OR [FoutMelding] IS NOT NULL);

    -- Fase 2a: verwijder correctierijen die verwijzen naar een rij die hieronder verdwijnt.
    --
    -- planner.ClassificatieCorrectie heeft twee foreign keys naar deze tabel
    -- (FK_..._Origineel en FK_..._Correctie) zonder ON DELETE CASCADE. Cascade is hier ook geen
    -- optie: twee cascadepaden vanuit dezelfde tabel naar dezelfde ouder weigert SQL Server met
    -- Msg 1785 ("may cause cycles or multiple cascade paths").
    --
    -- Een correctierij kan JONGER zijn dan de rij waarnaar hij verwijst — replydetectie kent geen
    -- tijdgrens, dus een reply op dag 40 hoort bij een bericht van dag 0. De eigen 90-dagenregel in
    -- sp_CleanupClassificatieCorrectie ruimt die correctierij dan niet op. Zonder deze DELETE
    -- faalt fase 2b op een FK-schending, gooit de aanroepende Function de fout door en wordt er
    -- vanaf dat moment nooit meer iets verwijderd: elke wekelijkse run klapt op dezelfde rij en de
    -- bewaartermijn van 90 dagen wordt structureel niet gehaald.
    --
    -- Deze opruiming staat bewust in DEZE procedure: dezelfde @VerwijderVoor bepaalt welke
    -- ouderrijen verdwijnen en welke verwijzingen dus mee moeten. De twee kunnen daardoor niet uit
    -- elkaar lopen, en de aanroepvolgorde van de twee cleanup-procedures is niet langer bepalend
    -- voor de correctheid.
    DELETE cc
    FROM [planner].[ClassificatieCorrectie] cc
    WHERE EXISTS (
        SELECT 1
        FROM [planner].[EmailVerwerking] ev
        WHERE ev.[Id] IN (cc.[OrigineleVerwerkingId], cc.[CorrectionVerwerkingId])
          AND ev.[mta_inserted] < @VerwijderVoor
    );

    -- Fase 2b: verwijder rijen ouder dan 90 dagen
    DELETE FROM [planner].[EmailVerwerking]
    WHERE [mta_inserted] < @VerwijderVoor;
END;
GO

-- Bron: Database/avg/System Stored Procedures/sp_CleanupImportLog.sql  (#426)
CREATE OR ALTER PROCEDURE [avg].[sp_CleanupImportLog]
AS
BEGIN
    SET NOCOUNT ON;

    -- Fase 1: anonimiseer PII in records ouder dan 90 dagen
    -- ImporterendeDoor is een persoonsgegeven (Entra display name).
    -- CsvBestand kan herleidbare info bevatten.
    UPDATE [avg].[ImportLog]
    SET [ImporterendeDoor] = NULL,
        [CsvBestand]       = NULL
    WHERE [ImportDatum] < DATEADD(DAY, -90, GETUTCDATE())
      AND ([ImporterendeDoor] IS NOT NULL
           OR [CsvBestand] IS NOT NULL);

    -- Fase 2: verwijder records ouder dan 1 jaar (gelijk aan avg.Teambegeleiding-retentie)
    DELETE FROM [avg].[ImportLog]
    WHERE [ImportDatum] < DATEADD(YEAR, -1, GETUTCDATE());
END;
GO

-- Bron: Database/avg/System Stored Procedures/sp_CleanupTeambegeleiding.sql  (#238)
CREATE OR ALTER PROCEDURE [avg].[sp_CleanupTeambegeleiding]
AS
BEGIN
    SET NOCOUNT ON;

    -- Verwijder rijen ouder dan 1 jaar.
    -- Vangnet voor het geval het importscript (TRUNCATE + herinsert) langere tijd
    -- niet gedraaid heeft. Actieve begeleiders worden bij elke import ververst
    -- en hebben altijd een recente mta_imported.
    DELETE FROM [avg].[Teambegeleiding]
    WHERE [mta_imported] < DATEADD(YEAR, -1, GETUTCDATE());
END;
GO

-- ============================================================
-- #635: AllStars FC demodata (idempotent)
--
-- De AppSettings-rij voor ALLSTARS werd hierboven al aangemaakt, maar de bijbehorende demodata
-- stond uitsluitend in scripts/migrations/002-seed-allstars-fc.sql. Die map wordt handmatig
-- uitgevoerd en is nooit tegen productie gelopen: de democlub bestond wel, maar had geen velden,
-- geen speeltijden en geen teams. De testmodus in de Admin GUI was daardoor een leeg scherm, en
-- clubs die de repo forken kregen geen werkende demo-omgeving.
--
-- Staat bewust aan het EINDE van dit script: alle betrokken tabellen en kolommen zijn dan
-- gegarandeerd aangemaakt door de blokken hierboven.
--
-- AVG: uitsluitend fictieve gegevens conform de vastgelegde uitzondering in CLAUDE.md --
-- voornamen zonder achternaam en het gereserveerde .test-TLD (RFC 2606), dat publiek niet bestaat.
-- Alleen rijen met ClubCode = 'ALLSTARS'; nooit gebruikt voor een echte club.
--
-- Speeldata worden relatief aan de deploydatum berekend. De oorspronkelijke seed had juni/juli 2026
-- hardcoded, waardoor de demo na die periode alleen verleden wedstrijden toonde.
-- ============================================================
DECLARE @DemoClub NVARCHAR(20) = 'ALLSTARS';

-- Alleen seeden als de democlub bestaat -- een fork die hem weghaalt houdt een schone database.
IF EXISTS (SELECT 1 FROM [dbo].[AppSettings] WHERE [ClubCode] = @DemoClub)
BEGIN
    -- Velden: nummers 101-103 vermijden een PK-conflict met de primaire club
    -- (PK_Velden staat op VeldNummer alleen).
    IF NOT EXISTS (SELECT 1 FROM [dbo].[Velden] WHERE [ClubCode] = @DemoClub)
        INSERT INTO [dbo].[Velden] ([VeldNummer], [VeldNaam], [VeldType], [HeeftKunstlicht], [Actief], [ClubCode])
        VALUES (101, 'Kunstgras 1', 'kunstgras',  1, 1, @DemoClub),
               (102, 'Kunstgras 2', 'kunstgras',  1, 1, @DemoClub),
               (103, 'Gras',        'natuurgras', 0, 1, @DemoClub);

    -- VeldBeschikbaarheid: DagVanWeek volgt .NET DayOfWeek (0 = zondag, 6 = zaterdag), dezelfde
    -- conventie als de bestaande rijen van de primaire club.
    -- 08:30-22:00 sluit aan op wat een club in de praktijk aanhoudt en is ruim genoeg voor de
    -- 14 thuiswedstrijden per speeldag; met een krapper venster loopt de planner over zijn
    -- beschikbaarheid heen en eindigt de demo met een onrealistisch schema.
    IF NOT EXISTS (SELECT 1 FROM [dbo].[VeldBeschikbaarheid] WHERE [ClubCode] = @DemoClub)
        INSERT INTO [dbo].[VeldBeschikbaarheid]
            ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang], [ClubCode])
        SELECT v.[VeldNummer], d.[Dag], '08:30', '22:00', 0, @DemoClub
        FROM (VALUES (101), (102), (103)) AS v([VeldNummer])
        CROSS JOIN (VALUES (6), (0)) AS d([Dag]);

    -- Speeltijden: overgenomen van de primaire club in plaats van hardcoded. Dit zijn
    -- KNVB-standaarden, dus zo blijft de demo consistent met wat de club zelf heeft ingesteld
    -- (inclusief de correcties uit #291).
    IF NOT EXISTS (SELECT 1 FROM [dbo].[Speeltijden] WHERE [ClubCode] = @DemoClub)
        INSERT INTO [dbo].[Speeltijden]
            ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust], [ClubCode])
        SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust], @DemoClub
        FROM [dbo].[Speeltijden]
        WHERE [ClubCode] = (SELECT MIN([ClubCode]) FROM [dbo].[AppSettings] WHERE [ClubCode] <> @DemoClub);

    -- his.teams: twee teams per categorie. Set-based gegenereerd zodat de lijst compact blijft;
    -- de leeftijdscategorieen sluiten aan op de sleutels in dbo.Speeltijden.
    IF NOT EXISTS (SELECT 1 FROM [his].[teams] WHERE [ClubCode] = @DemoClub)
        INSERT INTO [his].[teams]
            ([bk_teams], [teamnaam], [teamsoort], [geslacht], [leeftijdscategorie],
             [competitiesoort], [mta_inserted], [mta_modified], [ClubCode])
        SELECT
            CONCAT('ALLSTARS-', c.[Cat], '-', n.[Nr]),
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

    -- his.matches: acht speelronden vanaf de eerstvolgende zaterdag, afwisselend thuis en uit.
    -- De zaterdagberekening is onafhankelijk van SET DATEFIRST: 1900-01-01 was een maandag, dus
    -- DATEDIFF % 7 geeft 0 = maandag en 5 = zaterdag.
    -- Wedstrijdcodes vanaf 9000001 overlappen niet met echte Sportlink-codes.
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
                -- thuis speelt. Alleen op ronde alterneren zou alle teams op dezelfde dag thuis
                -- zetten - onrealistisch en niet in te plannen op drie velden.
                (r.[Ronde] + ROW_NUMBER() OVER (PARTITION BY r.[Ronde] ORDER BY t.[teamnaam])) % 2 AS [Thuis],
                CASE WHEN t.[teamsoort] = 'Senioren' THEN '14:30' ELSE '09:00' END AS [Tijd]
            FROM [his].[teams] t
            CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7), (8)) AS r([Ronde])
            WHERE t.[ClubCode] = @DemoClub
        ) AS x;
    END
END
GO

-- ============================================================
-- v2 — #666: standaard voorkeurstijd per leeftijdscategorie
-- ------------------------------------------------------------
-- De planner gebruikt deze tijd als een team géén eigen rij in dbo.TeamVoorkeurTijden heeft voor de
-- speeldag. Zonder deze kolom viel zo'n team terug op "het eerst beschikbare gat", zonder tijdsdoel.
-- NULL blijft toegestaan en betekent nog steeds: geen streeftijd.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Speeltijden') AND name = 'StandaardVoorkeurTijd')
    ALTER TABLE [dbo].[Speeltijden] ADD [StandaardVoorkeurTijd] TIME NULL;
GO

-- Initiële waarden — uitsluitend voor rijen die nog geen tijd hebben, zodat een beheerder die de
-- tijden zelf heeft aangepast niet overschreven wordt. Per club, voor ALLE clubs in deze database.
-- Bewust géén waarde voor JO6/JO7/MO7 (jongste jeugd) en JO23/MO20/MO23: daarvoor is geen beleid
-- vastgesteld. Die categorieën houden NULL en dus het bestaande gedrag (eerst beschikbare slot).
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Speeltijden') AND name = 'StandaardVoorkeurTijd')
BEGIN
    UPDATE s
    SET s.[StandaardVoorkeurTijd] = d.[Tijd]
    FROM [dbo].[Speeltijden] s
    INNER JOIN (VALUES
        ('JO8',  CAST('09:00' AS TIME)), ('JO9',  CAST('09:00' AS TIME)),
        ('MO8',  CAST('09:00' AS TIME)), ('MO9',  CAST('09:00' AS TIME)),
        ('JO10', CAST('10:00' AS TIME)), ('JO11', CAST('10:00' AS TIME)),
        ('JO12', CAST('10:00' AS TIME)), ('JO13', CAST('10:00' AS TIME)),
        ('MO10', CAST('10:00' AS TIME)), ('MO11', CAST('10:00' AS TIME)),
        ('MO12', CAST('10:00' AS TIME)), ('MO13', CAST('10:00' AS TIME)),
        ('JO14', CAST('11:00' AS TIME)), ('JO15', CAST('11:00' AS TIME)),
        ('MO14', CAST('11:00' AS TIME)), ('MO15', CAST('11:00' AS TIME)),
        ('JO16', CAST('12:00' AS TIME)), ('JO17', CAST('12:00' AS TIME)),
        ('JO18', CAST('12:00' AS TIME)), ('JO19', CAST('12:00' AS TIME)),
        ('MO16', CAST('12:00' AS TIME)), ('MO17', CAST('12:00' AS TIME)),
        ('MO18', CAST('12:00' AS TIME)), ('MO19', CAST('12:00' AS TIME)),
        ('G',    CAST('10:00' AS TIME)),
        ('VR',   CAST('14:30' AS TIME)), ('1-99', CAST('14:30' AS TIME))
    ) AS d([Leeftijd], [Tijd]) ON d.[Leeftijd] = s.[Leeftijd]
    WHERE s.[StandaardVoorkeurTijd] IS NULL;
END
GO

-- #692/#696: canonieke teamidentiteit + aliassen voor de teamnaam-naar-ID vertaallaag.
-- Nog leeg op een verse deploy — vulling vanuit de nachtelijke Sportlink-sync volgt in #696.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.Teams'))
BEGIN
    CREATE TABLE [dbo].[Teams] (
        [TeamId]                 INT IDENTITY(1,1) NOT NULL,
        [ClubCode]               NVARCHAR(20)  NOT NULL,
        [Teamnaam]               NVARCHAR(100) NOT NULL,
        [TeamnaamGenormaliseerd] NVARCHAR(100) NOT NULL,
        [LeeftijdsCategorie]     NVARCHAR(50)  NULL,
        [LeeftijdNummer]         INT NULL,
        [TeamNummer]             INT NULL,
        [BkTeams]                NVARCHAR(100) NULL,
        [IsActief]               BIT NOT NULL CONSTRAINT [DF_Teams_IsActief] DEFAULT 1,
        [mta_inserted]           DATETIME NOT NULL CONSTRAINT [DF_Teams_Inserted] DEFAULT GETUTCDATE(),
        [mta_modified]           DATETIME NOT NULL CONSTRAINT [DF_Teams_Modified] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_Teams] PRIMARY KEY CLUSTERED ([TeamId] ASC),
        CONSTRAINT [UQ_Teams_Club_Teamnaam] UNIQUE ([ClubCode], [Teamnaam]),
        CONSTRAINT [UQ_Teams_Club_Genormaliseerd] UNIQUE ([ClubCode], [TeamnaamGenormaliseerd])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.TeamAliassen'))
BEGIN
    CREATE TABLE [dbo].[TeamAliassen] (
        [Id]                      INT IDENTITY(1,1) NOT NULL,
        [ClubCode]                NVARCHAR(20)  NOT NULL,
        [RuweTekst]               NVARCHAR(200) NOT NULL,
        [RuweTekstGenormaliseerd] NVARCHAR(200) NOT NULL,
        [TeamId]                  INT NOT NULL,
        [Bron]                    NVARCHAR(20)  NOT NULL,
        [Status]                  NVARCHAR(20)  NOT NULL CONSTRAINT [DF_TeamAliassen_Status] DEFAULT 'pending',
        [AantalKeerGebruikt]      INT NOT NULL CONSTRAINT [DF_TeamAliassen_AantalKeerGebruikt] DEFAULT 0,
        [mta_inserted]            DATETIME NOT NULL CONSTRAINT [DF_TeamAliassen_Inserted] DEFAULT GETUTCDATE(),
        [mta_modified]            DATETIME NOT NULL CONSTRAINT [DF_TeamAliassen_Modified] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_TeamAliassen] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [FK_TeamAliassen_Teams] FOREIGN KEY ([TeamId]) REFERENCES [dbo].[Teams]([TeamId]),
        CONSTRAINT [UQ_TeamAliassen_Club_RuweTekst] UNIQUE ([ClubCode], [RuweTekst])
    );
END
GO

-- ============================================================
-- #712: planner.EmailVerwerking.Pogingen — pogingenteller voor de e-mailverwerking.
--
-- Waarom: een bericht waarvan de verwerking faalt (verzendfout, plannerfout, mislukte
-- AI-classificatie) blijft ongelezen in de inbox staan en komt bij elke poll terug. De Graph-query
-- pakt de 10 oudste ongelezen berichten, dus tien structureel falende berichten blokkeren de hele
-- wachtrij en kosten elke poll opnieuw een AI-call. Met deze teller geeft de processor na een vast
-- aantal pogingen op: bericht als gelezen markeren, status Fout, spoor in het email-log.
--
-- DEFAULT 0 en niet 1: bestaande rijen zijn al afgehandeld en mogen niet als "poging gedaan"
-- gelden. Nieuwe rijen worden door de code expliciet met Pogingen = 1 aangemaakt.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.EmailVerwerking') AND name = 'Pogingen')
    ALTER TABLE [planner].[EmailVerwerking] ADD [Pogingen] INT NOT NULL CONSTRAINT [DF_EmailVerwerking_Pogingen] DEFAULT 0;
GO

-- ============================================================
-- #707: dbo.AppSettings.ClubCode mag niet leeg zijn.
--
-- Waarom: LoadSettingsAsync zet een lege kolomwaarde als "" in de settings-cache. Code die alleen op
-- NULL controleerde (?? throw) liet die waarde door, waarna elke query met [ClubCode] = '' een lege
-- set opleverde. Voor de uitsluitingslijst van de e-mailverwerking betekende dat: uitgesloten
-- adressen werden alsnog verwerkt en beantwoord — fail-open op een AVG-maatregel. De C#-kant weigert
-- nu elke lege waarde (RequireClubCode); deze constraint is de tweede laag, ook voor handmatige
-- UPDATE's rechtstreeks op de database.
--
-- De ALTER mag de deploy niet laten falen: een bestaande rij met een lege ClubCode zou een
-- constraint-fout geven en daarmee de hele PostDeployment afbreken. Daarom eerst controleren —
-- schendende rij aanwezig, dan alleen waarschuwen (severity 10 = informatief, geen fout) en de
-- constraint overslaan. Zodra de beheerder de rij heeft gevuld legt de volgende deploy hem alsnog
-- aan. Idempotent: bestaat de constraint al (via het #598-blok hierboven, of via de dacpac uit
-- Database/dbo/Tables/AppSettings.sql), dan doet dit blok niets.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AppSettings_ClubCode')
BEGIN
    IF EXISTS (SELECT 1 FROM [dbo].[AppSettings] WHERE LEN(LTRIM(RTRIM([ClubCode]))) = 0)
        RAISERROR('#707 WAARSCHUWING: dbo.AppSettings bevat een rij met een lege ClubCode. CK_AppSettings_ClubCode is NIET aangemaakt. Vul de ClubCode en voer de deploy opnieuw uit.', 10, 1) WITH NOWAIT;
    ELSE
        ALTER TABLE [dbo].[AppSettings] ADD CONSTRAINT [CK_AppSettings_ClubCode] CHECK (LEN(LTRIM(RTRIM([ClubCode]))) > 0);
END
GO
-- ============================================================
-- #700: dbo.TeamAliassen — uniciteit van de genormaliseerde sleutel naar de RUWE tekst.
--
-- Waarom: de sync koppelt nu elke schrijfwijze die in his.matches/his.teams voorkomt aan zijn
-- canonieke team, zodat het zoeken van een wedstrijd een exacte join op de ruwe naam wordt in plaats
-- van een LIKE-patroon. Meerdere ruwe schrijfwijzen horen dan juist naar dezelfde genormaliseerde
-- sleutel te wijzen ("[club] JO10-1" en "[club] O10-1" zijn hetzelfde team) — uniciteit op die
-- sleutel zou de tweede schrijfwijze weigeren.
--
-- Veilig voor bestaande installaties: de tabel is in de vorige release aangemaakt en wordt pas door
-- deze release gevuld, dus er kan geen bestaande rij zijn die de nieuwe constraint schendt. Mocht er
-- toch data staan met dubbele (ClubCode, RuweTekst), dan slaagt de ALTER niet en blijft de oude
-- constraint staan; dat wordt hieronder expliciet gemeld in plaats van de deploy te laten klappen.
-- ============================================================
IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('dbo.TeamAliassen'))
BEGIN
    IF EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE name = 'UQ_TeamAliassen_Club_Genormaliseerd'
                 AND parent_object_id = OBJECT_ID('dbo.TeamAliassen'))
        ALTER TABLE [dbo].[TeamAliassen] DROP CONSTRAINT [UQ_TeamAliassen_Club_Genormaliseerd];

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
                   WHERE name = 'UQ_TeamAliassen_Club_RuweTekst'
                     AND parent_object_id = OBJECT_ID('dbo.TeamAliassen'))
    BEGIN
        IF EXISTS (SELECT 1 FROM [dbo].[TeamAliassen]
                   GROUP BY [ClubCode], [RuweTekst] HAVING COUNT(*) > 1)
            PRINT 'WAARSCHUWING: dbo.TeamAliassen bevat dubbele (ClubCode, RuweTekst) — UQ_TeamAliassen_Club_RuweTekst NIET aangemaakt. Ruim de duplicaten op en draai dit script opnieuw.';
        ELSE
            ALTER TABLE [dbo].[TeamAliassen]
                ADD CONSTRAINT [UQ_TeamAliassen_Club_RuweTekst] UNIQUE ([ClubCode], [RuweTekst]);
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_TeamAliassen_Club_Genormaliseerd'
                     AND object_id = OBJECT_ID('dbo.TeamAliassen'))
        CREATE NONCLUSTERED INDEX [IX_TeamAliassen_Club_Genormaliseerd]
            ON [dbo].[TeamAliassen] ([ClubCode], [RuweTekstGenormaliseerd])
            INCLUDE ([TeamId], [Status]);
END
GO

-- ============================================================
-- #718: planner.EmailVerwerking.IsBeantwoord — "wij hebben geantwoord" los van het adres.
--
-- Waarom: de AVG-retentie zet VerstuurdNaar na 30 dagen op NULL. Diezelfde kolom was óók het
-- criterium voor de replydetectie ("hoort deze inkomende mail bij een bericht dat wij beantwoord
-- hebben?") en sinds #712 de harde grens tegen een tweede antwoord. Eén kolom met twee betekenissen,
-- waarvan er één per definitie na 30 dagen verdwijnt: een reply op dag 31 — normaal bij een verzoek
-- dat weken vooruit ligt — werd niet meer als reply herkend, dus werd er ook geen leermoment meer
-- vastgelegd. Het zelflerende deel werkte daarmee alleen binnen 30 dagen, en dat stond nergens.
--
-- Backfill: elke rij waarvan we nog kunnen zien dat er geantwoord is, krijgt de vlag. Voor rijen die
-- al geanonimiseerd zijn én geen status 'AntwoordVerstuurd' hebben is dat niet meer te achterhalen;
-- die blijven 0. Dat is de veilige kant op: de statuslijst in EmailIdempotentie blijft een tweede,
-- onafhankelijke grens tegen een dubbel antwoord.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.EmailVerwerking') AND name = 'IsBeantwoord')
BEGIN
    ALTER TABLE [planner].[EmailVerwerking]
        ADD [IsBeantwoord] BIT NOT NULL CONSTRAINT [DF_EmailVerwerking_IsBeantwoord] DEFAULT 0;
END
GO

-- Aparte batch: de kolom moet bestaan vóór de UPDATE gecompileerd wordt.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.EmailVerwerking') AND name = 'IsBeantwoord')
BEGIN
    UPDATE [planner].[EmailVerwerking]
    SET [IsBeantwoord] = 1
    WHERE [IsBeantwoord] = 0
      AND ([VerstuurdNaar] IS NOT NULL OR [Status] = 'AntwoordVerstuurd');
END
GO

-- ============================================================
-- #716: planner.EmailVerwerking.VerzendPogingOpUtc — verzendintentie vóór het versturen.
--
-- Waarom: de volgorde was versturen → vastleggen → als gelezen markeren. De grens die een tweede
-- antwoord moet voorkomen werd dus geschreven NÁDAT de mail al de deur uit was. Wordt de invocatie
-- precies daartussen hard afgebroken (functie-time-out van tien minuten, host-recycle, scale-in),
-- dan is het antwoord verstuurd, is VerstuurdNaar leeg, staat de status niet op definitief en is het
-- bericht nog ongelezen — en stuurt de volgende poll een tweede antwoord.
--
-- Deze kolom wordt gezet vlak vóór de verzendpoging en gewist zodra die aantoonbaar mislukt. Een
-- gevulde waarde bij IsBeantwoord = 0 betekent daarom precies: "verstuurd of misschien verstuurd,
-- uitkomst onbekend". De verwerking gaat dan naar Review in plaats van opnieuw te versturen, zodat
-- de coördinator beslist. De twee bekende varianten (databasefout bij het vastleggen, fout bij het
-- als-gelezen-markeren) waren al gedekt; dit dicht specifiek de harde afbreking.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.EmailVerwerking') AND name = 'VerzendPogingOpUtc')
BEGIN
    ALTER TABLE [planner].[EmailVerwerking] ADD [VerzendPogingOpUtc] DATETIME2 NULL;
END
GO

-- ============================================================
-- #715: één leermoment per (origineel, correctie)-paar.
--
-- Waarom: InsertClassificatieCorrectieAsync was een kale INSERT en de tabel had geen uniciteit op
-- het paar. Sinds de idempotentiefix van #712 wordt een niet-afgeronde verwerking op dezelfde rij
-- hervat, dus draait de correctiedetectie opnieuw en levert hetzelfde paar een tweede rij op — bij
-- drie toegestane pogingen tot drie identieke leermomenten. De beheerder moet die allemaal apart
-- valideren, en meervoudig goedgekeurd weegt hetzelfde voorbeeld onbedoeld zwaarder in de AI-prompt.
--
-- Bestaande duplicaten worden eerst opgeruimd: de oudste rij van elk paar blijft staan (laagste Id),
-- inclusief een eventueel al gezette validatiebeslissing. Zijn er duplicaten waarvan er één is
-- goedgekeurd en een andere niet, dan is "de oudste houden" een keuze — daarom wordt het aantal
-- opgeruimde rijen expliciet gemeld in de deploy-output in plaats van stil te verdwijnen.
-- ============================================================
IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('planner.ClassificatieCorrectie'))
   AND NOT EXISTS (SELECT 1 FROM sys.key_constraints
                   WHERE name = 'UQ_ClassificatieCorrectie_Paar'
                     AND parent_object_id = OBJECT_ID('planner.ClassificatieCorrectie'))
BEGIN
    DECLARE @DubbeleLeermomenten INT = 0;

    ;WITH Gerangschikt AS (
        SELECT [Id],
               ROW_NUMBER() OVER (
                   PARTITION BY [OrigineleVerwerkingId], [CorrectionVerwerkingId]
                   ORDER BY [Id]) AS Rang
        FROM [planner].[ClassificatieCorrectie]
    )
    DELETE FROM Gerangschikt WHERE Rang > 1;

    SET @DubbeleLeermomenten = @@ROWCOUNT;

    IF @DubbeleLeermomenten > 0
        RAISERROR('#715: %d dubbele leermomenten opgeruimd vóór het aanleggen van UQ_ClassificatieCorrectie_Paar (oudste rij per paar behouden).', 10, 1, @DubbeleLeermomenten) WITH NOWAIT;

    ALTER TABLE [planner].[ClassificatieCorrectie]
        ADD CONSTRAINT [UQ_ClassificatieCorrectie_Paar] UNIQUE ([OrigineleVerwerkingId], [CorrectionVerwerkingId]);
END
GO
