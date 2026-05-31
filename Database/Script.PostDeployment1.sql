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
IF NOT EXISTS (SELECT 1 FROM [dbo].[AppSettings])
BEGIN
	INSERT INTO [dbo].[AppSettings]
		([ClubName]
		,[SportlinkApiUrl]
		,[SportlinkClientId]
		,[SeasonStartMonth]
		,[FetchSchedule])
	VALUES
		('Uw clubnaam'
		,'https://data.sportlink.com'
		,'APIKEY'
		,7
		,'0 0 4 * * *')
END
GO

-- Speeltijden: insert static reference data once
IF NOT EXISTS (SELECT 1 FROM [dbo].[Speeltijden])
BEGIN
    INSERT INTO [dbo].[Speeltijden] ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust])
    VALUES
        ('JO7',  0.25, 50,  20, 10),
        ('JO8',  0.25, 50,  20, 10),
        ('JO9',  0.25, 50,  20, 10),
        ('JO10', 0.25, 65,  25, 15),
        ('JO11', 0.50, 75,  30, 15),
        ('JO12', 0.50, 75,  30, 15),
        ('JO13', 1.00, 75,  30, 15),
        ('JO14', 1.00, 85,  35, 15),
        ('JO15', 1.00, 85,  35, 15),
        ('JO16', 1.00, 95,  40, 15),
        ('JO17', 1.00, 95,  40, 15),
        ('JO18', 1.00, 105, 45, 15),
        ('JO19', 1.00, 105, 45, 15),
        ('JO23', 1.00, 105, 45, 15),
        ('MO13', 1.00, 75,  30, 15),
        ('MO15', 1.00, 85,  35, 15),
        ('MO17', 1.00, 95,  40, 15),
        ('MO19', 1.00, 105, 45, 15),
        ('MO20', 1.00, 105, 45, 15),
        ('VR',   1.00, 115, 45, 15),
        ('G',    0.50, 75,  30, 15),
        ('1-99', 1.00, 115, 45, 15)
END
GO

-- Velden: field definitions (ClubCode via DEFAULT 'VRC' — single-club initiële seed; bij multi-club handmatig per club inserten)
IF NOT EXISTS (SELECT 1 FROM [dbo].[Velden])
BEGIN
    INSERT INTO [dbo].[Velden] ([VeldNummer], [VeldNaam], [VeldType], [HeeftKunstlicht], [Actief])
    VALUES
        (1, 'veld 1', 'kunstgras', 1, 1),
        (2, 'veld 2', 'kunstgras', 1, 1),
        (3, 'veld 3', 'kunstgras', 1, 1),
        (4, 'veld 4', 'kunstgras', 1, 1),
        (5, 'veld 5', 'natuurgras', 0, 1),
        (6, 'veld 6', 'natuurgras', 0, 0)  -- niet functioneel
END
GO

-- VeldBeschikbaarheid: field availability per day-of-week (ClubCode via DEFAULT 'VRC' — single-club initiële seed)
-- DagVanWeek: 1=Monday, 2=Tuesday, ..., 6=Saturday, 7=Sunday
IF NOT EXISTS (SELECT 1 FROM [dbo].[VeldBeschikbaarheid])
BEGIN
    -- Monday-Thursday (1-4): only veld 5, until sunset
    INSERT INTO [dbo].[VeldBeschikbaarheid] ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang])
    VALUES
        (5, 1, '18:00', '22:00', 1),
        (5, 2, '18:00', '22:00', 1),
        (5, 3, '18:00', '22:00', 1),
        (5, 4, '18:00', '22:00', 1)
    -- Friday (5): no rows = no matches
    -- Sunday (7): no rows = no matches

    -- Saturday (6): all fields
    INSERT INTO [dbo].[VeldBeschikbaarheid] ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang])
    VALUES
        (1, 6, '08:30', '22:00', 0),
        (2, 6, '08:30', '22:00', 0),
        (3, 6, '08:30', '22:00', 0),
        (4, 6, '08:30', '22:00', 0),
        (5, 6, '08:30', '17:00', 0)
END
GO

-- TeamRegels: team-specific scheduling exceptions (ClubCode via DEFAULT 'VRC' — single-club initiële seed)
IF NOT EXISTS (SELECT 1 FROM [dbo].[TeamRegels])
BEGIN
    INSERT INTO [dbo].[TeamRegels] ([TeamNaam], [RegelType], [WaardeMinuten], [Prioriteit], [Actief], [Opmerking])
    VALUES
        ('VRC 1', 'BufferVoor', 60, 10, 1, '1 uur voor de wedstrijd geen andere wedstrijden op hetzelfde veld'),
        ('VRC 1', 'BufferNa',   30, 10, 1, '30 min na de wedstrijd geen andere wedstrijden op hetzelfde veld')
END
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

-- Update the Season and datetable
DECLARE @SeasonStartMonth INT = (SELECT [SeasonStartMonth] FROM [dbo].[AppSettings])
EXEC [dbo].[sp_UpdateSeasonTable] @SeasonStartMonth;
GO
-- ============================================================
-- #30: Multi-club fundament — ClubCode + Accommodatie
-- DEFAULT 'VRC' is uitsluitend voor migratie-backwards-compat (bestaande rijen krijgen hier een waarde).
-- Alle nieuwe inserts geven ClubCode altijd expliciet mee vanuit AppSettings.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'ClubCode')
    ALTER TABLE [dbo].[AppSettings] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_AppSettings_ClubCode] DEFAULT 'VRC'; -- migratie-backwards-compat
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'Accommodatie')
    ALTER TABLE [dbo].[AppSettings] ADD [Accommodatie] NVARCHAR(200) NULL; -- geen default — in te stellen via Beheer → Instellingen per club
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Velden') AND name = 'ClubCode')
    ALTER TABLE [dbo].[Velden] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_Velden_ClubCode] DEFAULT 'VRC'; -- migratie-backwards-compat
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Speeltijden') AND name = 'ClubCode')
    ALTER TABLE [dbo].[Speeltijden] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_Speeltijden_ClubCode] DEFAULT 'VRC'; -- migratie-backwards-compat
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TeamRegels') AND name = 'ClubCode')
    ALTER TABLE [dbo].[TeamRegels] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_TeamRegels_ClubCode] DEFAULT 'VRC'; -- migratie-backwards-compat
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.VeldBeschikbaarheid') AND name = 'ClubCode')
    ALTER TABLE [dbo].[VeldBeschikbaarheid] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_VeldBeschikbaarheid_ClubCode] DEFAULT 'VRC'; -- migratie-backwards-compat
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
        [ClubCode]      NVARCHAR(20) NOT NULL CONSTRAINT [DF_AppSettingsAudit_ClubCode] DEFAULT 'VRC',
        CONSTRAINT [PK_AppSettingsAudit] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END
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
        [ClubCode]      NVARCHAR(20) NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_ClubCode] DEFAULT 'VRC',
        [mta_inserted]  DATETIME2 NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Inserted] DEFAULT GETUTCDATE(),
        [mta_modified]  DATETIME2 NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Modified] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_TeamVoorkeurTijden] PRIMARY KEY CLUSTERED ([Id] ASC)
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
        [ClubCode]     NVARCHAR(20) NOT NULL CONSTRAINT [DF_UitgeslotenEmailAdressen_ClubCode]  DEFAULT 'VRC',
        [mta_inserted] DATETIME2 NOT NULL CONSTRAINT [DF_UitgeslotenEmailAdressen_Inserted] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_UitgeslotenEmailAdressen] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [UQ_UitgeslotenEmailAdressen_Adres] UNIQUE ([EmailAdres], [ClubCode])
    );
END
GO

-- v2 — #119: ClubCode toevoegen aan planner.EmailVerwerking (multi-club isolatie email-log)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('planner.EmailVerwerking') AND name = 'ClubCode')
    ALTER TABLE [planner].[EmailVerwerking] ADD [ClubCode] NVARCHAR(20) NOT NULL CONSTRAINT [DF_EmailVerwerking_ClubCode] DEFAULT 'VRC';
GO

-- ============================================================
-- #208: AVG-retentie — planner.sp_CleanupEmailVerwerking aanmaken
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('planner.sp_CleanupEmailVerwerking') AND type = 'P')
BEGIN
    EXEC(N'
CREATE PROCEDURE [planner].[sp_CleanupEmailVerwerking]
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [planner].[EmailVerwerking]
    SET [Afzender]          = ''[geanonimiseerd]'',
        [Onderwerp]         = ''[geanonimiseerd]'',
        [VerstuurdNaar]     = NULL,
        [EmailBody]         = NULL,
        [AntwoordEmail]     = NULL,
        [PlannerResponse]   = NULL,
        [GeextraheerdeData] = NULL,
        [mta_modified]      = GETUTCDATE()
    WHERE [mta_inserted] < DATEADD(DAY, -30, GETUTCDATE())
      AND [mta_inserted] >= DATEADD(DAY, -90, GETUTCDATE())
      AND ([Afzender] <> ''[geanonimiseerd]''
           OR [EmailBody] IS NOT NULL
           OR [AntwoordEmail] IS NOT NULL
           OR [PlannerResponse] IS NOT NULL
           OR [GeextraheerdeData] IS NOT NULL);
    DELETE FROM [planner].[EmailVerwerking]
    WHERE [mta_inserted] < DATEADD(DAY, -90, GETUTCDATE());
END;
    ');
END
GO

-- #242: Verwijder club-specifieke DEFAULT 'VRC' uit EmailVerwerking.ClubCode
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

-- #238: avg.sp_CleanupTeambegeleiding — AVG-vangnet: verwijder rijen ouder dan 1 jaar
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('avg.sp_CleanupTeambegeleiding') AND type = 'P')
BEGIN
    EXEC('
CREATE PROCEDURE [avg].[sp_CleanupTeambegeleiding]
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM [avg].[Teambegeleiding]
    WHERE [mta_imported] < DATEADD(YEAR, -1, GETUTCDATE());
END');
END
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
        [ClubCode]      NVARCHAR(20) NOT NULL CONSTRAINT [DF_EmailTemplateInstellingen_ClubCode] DEFAULT 'VRC',
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
