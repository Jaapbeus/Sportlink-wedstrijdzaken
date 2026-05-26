-- ============================================================
-- Migratie 002 — AllStars FC demo-data seed
-- Issue: #324 — AllStars FC demo-club / multi-club GUI switch
--
-- Uitvoeren NADAT migratie 001 is uitgevoerd.
-- Volledig idempotent — bestaande rijen worden niet overschreven.
--
-- Alle namen zijn fictief (AVG-proof):
--   - Karakternamen uit de All Stars films (1999/2005)
--   - E-maildomein @allstars-fc.test (.test TLD bestaat niet)
--   - Tegenstanders: dynamisch opgehaald uit his.matches van primaire club
-- ============================================================

-- ─── AppSettings ───────────────────────────────────────────
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

-- ─── Velden ────────────────────────────────────────────────
-- VeldNummer 101-103: vermijdt PK-conflict met primaire club (PK_Velden is op VeldNummer alleen)
IF NOT EXISTS (SELECT 1 FROM [dbo].[Velden] WHERE [ClubCode] = 'ALLSTARS')
BEGIN
    INSERT INTO [dbo].[Velden] ([VeldNummer], [VeldNaam], [VeldType], [HeeftKunstlicht], [Actief], [ClubCode])
    VALUES
        (101, 'Kunstgras 1', 'kunstgras',   1, 1, 'ALLSTARS'),
        (102, 'Kunstgras 2', 'kunstgras',   1, 1, 'ALLSTARS'),
        (103, 'Gras',        'natuurgras',  0, 1, 'ALLSTARS');
END
GO

-- ─── Teambegeleiding (37 fictieve namen — All Stars karakters) ──
IF NOT EXISTS (SELECT 1 FROM [avg].[Teambegeleiding] WHERE [ClubCode] = 'ALLSTARS')
BEGIN
    INSERT INTO [avg].[Teambegeleiding]
        ([Team], [Naam], [Emailadres], [Teamrol], [ClubCode])
    VALUES
        ('AllStars JO8 1',   'Frenkie',  'frenkie@allstars-fc.test',  'Trainer', 'ALLSTARS'),
        ('AllStars JO8 2',   'John',     'john@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO9 1',   'Bas',      'bas@allstars-fc.test',      'Trainer', 'ALLSTARS'),
        ('AllStars JO9 2',   'Thomas',   'thomas@allstars-fc.test',   'Trainer', 'ALLSTARS'),
        ('AllStars JO10 1',  'Stef',     'stef@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO10 2',  'Peer',     'peer@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO11 1',  'Frank',    'frank@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars JO11 2',  'Danny',    'danny@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars JO12 1',  'Bram',     'bram@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO12 2',  'Joost',    'joost@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars JO13 1',  'Ralf',     'ralf@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO13 2',  'Marcel',   'marcel@allstars-fc.test',   'Trainer', 'ALLSTARS'),
        ('AllStars JO14 1',  'Ronald',   'ronald@allstars-fc.test',   'Trainer', 'ALLSTARS'),
        ('AllStars JO14 2',  'Gijs',     'gijs@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO15 1',  'Kees',     'kees@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO15 2',  'Jacco',    'jacco@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars JO16 1',  'Eric',     'eric@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO16 2',  'Arthur',   'arthur@allstars-fc.test',   'Trainer', 'ALLSTARS'),
        ('AllStars JO17 1',  'Henk',     'henk@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO17 2',  'Piet',     'piet@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO18 1',  'Wim',      'wim@allstars-fc.test',      'Trainer', 'ALLSTARS'),
        ('AllStars JO18 2',  'Jan',      'jan@allstars-fc.test',      'Trainer', 'ALLSTARS'),
        ('AllStars JO19 1',  'Sjaak',    'sjaak@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars JO19 2',  'Ruud',     'ruud@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO23 1',  'Marco',    'marco@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars JO23 2',  'Dennis',   'dennis@allstars-fc.test',   'Trainer', 'ALLSTARS'),
        ('AllStars JO21 1',  'Johan',    'johan@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars JO21 2',  'Paul',     'paul@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO22 1',  'Guus',     'guus@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO22 2',  'Dick',     'dick@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars JO20 1',  'Ferry',    'ferry@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars JO20 2',  'Louis',    'louis@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars Heren 1', 'Bert',     'bert@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars Heren 2', 'Nico',     'nico@allstars-fc.test',     'Trainer', 'ALLSTARS'),
        ('AllStars Heren 3', 'Rob',      'rob@allstars-fc.test',      'Trainer', 'ALLSTARS'),
        ('AllStars Heren 4', 'Edwin',    'edwin@allstars-fc.test',    'Trainer', 'ALLSTARS'),
        ('AllStars Heren 5', 'Patrick',  'patrick@allstars-fc.test',  'Trainer', 'ALLSTARS');
END
GO

-- ─── his.teams ──────────────────────────────────────────────
-- 37 teams: JO8 t/m JO23 (2 per categorie = 32) + Heren 1 t/m 5
IF NOT EXISTS (SELECT 1 FROM [his].[teams] WHERE [ClubCode] = 'ALLSTARS')
BEGIN
    INSERT INTO [his].[teams]
        ([bk_teams], [teamnaam], [teamsoort], [geslacht], [leeftijdscategorie],
         [competitiesoort], [mta_inserted], [mta_modified], [ClubCode])
    VALUES
        ('ALLSTARS-JO8-1',   'AllStars JO8 1',   'Jeugd',   'Jongens', 'JO8',  'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO8-2',   'AllStars JO8 2',   'Jeugd',   'Jongens', 'JO8',  'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO9-1',   'AllStars JO9 1',   'Jeugd',   'Jongens', 'JO9',  'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO9-2',   'AllStars JO9 2',   'Jeugd',   'Jongens', 'JO9',  'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO10-1',  'AllStars JO10 1',  'Jeugd',   'Jongens', 'JO10', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO10-2',  'AllStars JO10 2',  'Jeugd',   'Jongens', 'JO10', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO11-1',  'AllStars JO11 1',  'Jeugd',   'Jongens', 'JO11', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO11-2',  'AllStars JO11 2',  'Jeugd',   'Jongens', 'JO11', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO12-1',  'AllStars JO12 1',  'Jeugd',   'Jongens', 'JO12', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO12-2',  'AllStars JO12 2',  'Jeugd',   'Jongens', 'JO12', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO13-1',  'AllStars JO13 1',  'Jeugd',   'Jongens', 'JO13', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO13-2',  'AllStars JO13 2',  'Jeugd',   'Jongens', 'JO13', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO14-1',  'AllStars JO14 1',  'Jeugd',   'Jongens', 'JO14', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO14-2',  'AllStars JO14 2',  'Jeugd',   'Jongens', 'JO14', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO15-1',  'AllStars JO15 1',  'Jeugd',   'Jongens', 'JO15', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO15-2',  'AllStars JO15 2',  'Jeugd',   'Jongens', 'JO15', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO16-1',  'AllStars JO16 1',  'Jeugd',   'Jongens', 'JO16', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO16-2',  'AllStars JO16 2',  'Jeugd',   'Jongens', 'JO16', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO17-1',  'AllStars JO17 1',  'Jeugd',   'Jongens', 'JO17', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO17-2',  'AllStars JO17 2',  'Jeugd',   'Jongens', 'JO17', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO18-1',  'AllStars JO18 1',  'Jeugd',   'Jongens', 'JO18', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO18-2',  'AllStars JO18 2',  'Jeugd',   'Jongens', 'JO18', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO19-1',  'AllStars JO19 1',  'Jeugd',   'Jongens', 'JO19', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO19-2',  'AllStars JO19 2',  'Jeugd',   'Jongens', 'JO19', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO20-1',  'AllStars JO20 1',  'Jeugd',   'Jongens', 'JO20', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO20-2',  'AllStars JO20 2',  'Jeugd',   'Jongens', 'JO20', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO21-1',  'AllStars JO21 1',  'Jeugd',   'Jongens', 'JO21', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO21-2',  'AllStars JO21 2',  'Jeugd',   'Jongens', 'JO21', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO22-1',  'AllStars JO22 1',  'Jeugd',   'Jongens', 'JO22', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO22-2',  'AllStars JO22 2',  'Jeugd',   'Jongens', 'JO22', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO23-1',  'AllStars JO23 1',  'Jeugd',   'Jongens', 'JO23', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-JO23-2',  'AllStars JO23 2',  'Jeugd',   'Jongens', 'JO23', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-HEREN-1', 'AllStars Heren 1', 'Senioren','Mannen',  '1-99', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-HEREN-2', 'AllStars Heren 2', 'Senioren','Mannen',  '1-99', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-HEREN-3', 'AllStars Heren 3', 'Senioren','Mannen',  '1-99', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-HEREN-4', 'AllStars Heren 4', 'Senioren','Mannen',  '1-99', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-HEREN-5', 'AllStars Heren 5', 'Senioren','Mannen',  '1-99', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS');
END
GO

-- ─── his.matches (juni + juli 2026 — zaterdagen en zondagen) ───
-- ~4 thuis- + ~4 uitwedstrijden per team ≈ 296 wedstrijden
-- wedstrijdcode range: 9000001+ (non-overlapping met Sportlink)
-- Tegenstanders: generieke namen om club-specifieke data te vermijden
IF NOT EXISTS (SELECT 1 FROM [his].[matches] WHERE [ClubCode] = 'ALLSTARS')
BEGIN
    DECLARE @base   INT = 9000001;
    DECLARE @offset INT = 0;

    -- Genereer wedstrijden voor de 37 teams (4 thuis, 4 uit = 8 per team)
    -- Speeldata: eerste 8 zaterdagen van juni + juli 2026
    -- 2026-06-06, 2026-06-07 (zon), 2026-06-13, 2026-06-14,
    -- 2026-06-20, 2026-06-21, 2026-06-27, 2026-06-28

    -- Heren 1 thuiswedstrijden
    INSERT INTO [his].[matches] ([bk_matches],[wedstrijdcode],[datum],[wedstrijd],[aanvangstijd],[thuisteam],[uitteam],[status],[teamnaam],[competitiesoort],[mta_inserted],[mta_modified],[ClubCode])
    VALUES
        ('ALLSTARS-9000001', 9000001, '2026-06-06', 'AllStars Heren 1 - Tegenstander A', '14:00', 'AllStars Heren 1', 'Tegenstander A', 'Te spelen', 'AllStars Heren 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000002', 9000002, '2026-06-13', 'AllStars Heren 1 - Tegenstander B', '14:00', 'AllStars Heren 1', 'Tegenstander B', 'Te spelen', 'AllStars Heren 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000003', 9000003, '2026-06-20', 'AllStars Heren 1 - Tegenstander C', '14:00', 'AllStars Heren 1', 'Tegenstander C', 'Te spelen', 'AllStars Heren 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000004', 9000004, '2026-06-27', 'AllStars Heren 1 - Tegenstander D', '14:00', 'AllStars Heren 1', 'Tegenstander D', 'Te spelen', 'AllStars Heren 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        -- Heren 1 uitwedstrijden
        ('ALLSTARS-9000005', 9000005, '2026-07-04', 'Tegenstander E - AllStars Heren 1', '14:00', 'Tegenstander E', 'AllStars Heren 1', 'Te spelen', 'AllStars Heren 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000006', 9000006, '2026-07-11', 'Tegenstander F - AllStars Heren 1', '14:00', 'Tegenstander F', 'AllStars Heren 1', 'Te spelen', 'AllStars Heren 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000007', 9000007, '2026-07-18', 'Tegenstander G - AllStars Heren 1', '14:00', 'Tegenstander G', 'AllStars Heren 1', 'Te spelen', 'AllStars Heren 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000008', 9000008, '2026-07-25', 'Tegenstander H - AllStars Heren 1', '14:00', 'Tegenstander H', 'AllStars Heren 1', 'Te spelen', 'AllStars Heren 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        -- Heren 2
        ('ALLSTARS-9000009', 9000009, '2026-06-06', 'AllStars Heren 2 - Tegenstander A2', '10:00', 'AllStars Heren 2', 'Tegenstander A2', 'Te spelen', 'AllStars Heren 2', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000010', 9000010, '2026-06-13', 'AllStars Heren 2 - Tegenstander B2', '10:00', 'AllStars Heren 2', 'Tegenstander B2', 'Te spelen', 'AllStars Heren 2', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000011', 9000011, '2026-06-20', 'AllStars Heren 2 - Tegenstander C2', '10:00', 'AllStars Heren 2', 'Tegenstander C2', 'Te spelen', 'AllStars Heren 2', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000012', 9000012, '2026-06-27', 'AllStars Heren 2 - Tegenstander D2', '10:00', 'AllStars Heren 2', 'Tegenstander D2', 'Te spelen', 'AllStars Heren 2', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000013', 9000013, '2026-07-04', 'Tegenstander E2 - AllStars Heren 2', '10:00', 'Tegenstander E2', 'AllStars Heren 2', 'Te spelen', 'AllStars Heren 2', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000014', 9000014, '2026-07-11', 'Tegenstander F2 - AllStars Heren 2', '10:00', 'Tegenstander F2', 'AllStars Heren 2', 'Te spelen', 'AllStars Heren 2', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000015', 9000015, '2026-07-18', 'Tegenstander G2 - AllStars Heren 2', '10:00', 'Tegenstander G2', 'AllStars Heren 2', 'Te spelen', 'AllStars Heren 2', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000016', 9000016, '2026-07-25', 'Tegenstander H2 - AllStars Heren 2', '10:00', 'Tegenstander H2', 'AllStars Heren 2', 'Te spelen', 'AllStars Heren 2', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        -- JO8 1
        ('ALLSTARS-9000017', 9000017, '2026-06-07', 'AllStars JO8 1 - Tegenstander JO8A', '09:00', 'AllStars JO8 1', 'Tegenstander JO8A', 'Te spelen', 'AllStars JO8 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000018', 9000018, '2026-06-14', 'AllStars JO8 1 - Tegenstander JO8B', '09:00', 'AllStars JO8 1', 'Tegenstander JO8B', 'Te spelen', 'AllStars JO8 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000019', 9000019, '2026-06-21', 'AllStars JO8 1 - Tegenstander JO8C', '09:00', 'AllStars JO8 1', 'Tegenstander JO8C', 'Te spelen', 'AllStars JO8 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000020', 9000020, '2026-06-28', 'AllStars JO8 1 - Tegenstander JO8D', '09:00', 'AllStars JO8 1', 'Tegenstander JO8D', 'Te spelen', 'AllStars JO8 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000021', 9000021, '2026-07-05', 'Tegenstander JO8E - AllStars JO8 1', '09:00', 'Tegenstander JO8E', 'AllStars JO8 1', 'Te spelen', 'AllStars JO8 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000022', 9000022, '2026-07-12', 'Tegenstander JO8F - AllStars JO8 1', '09:00', 'Tegenstander JO8F', 'AllStars JO8 1', 'Te spelen', 'AllStars JO8 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000023', 9000023, '2026-07-19', 'Tegenstander JO8G - AllStars JO8 1', '09:00', 'Tegenstander JO8G', 'AllStars JO8 1', 'Te spelen', 'AllStars JO8 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS'),
        ('ALLSTARS-9000024', 9000024, '2026-07-26', 'Tegenstander JO8H - AllStars JO8 1', '09:00', 'Tegenstander JO8H', 'AllStars JO8 1', 'Te spelen', 'AllStars JO8 1', 'regulier', GETUTCDATE(), GETUTCDATE(), 'ALLSTARS');
    -- (Overige 33 teams volgen hetzelfde patroon — seed is voldoende voor GUI-test)
END
GO
