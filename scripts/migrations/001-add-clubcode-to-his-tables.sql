-- ============================================================
-- Migratie 001 — ClubCode + SyncEnabled infrastructure
-- Issue: #324 — AllStars FC demo-club / multi-club GUI switch
--
-- Uitvoeren op productie-SQL vóór of na deploy.
-- Volledig idempotent — veilig om meerdere keren te draaien.
-- ============================================================

-- ClubCode in his.teams
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('his.teams') AND name = 'ClubCode')
    ALTER TABLE [his].[teams] ADD [ClubCode] NVARCHAR(20) NULL;
GO

-- ClubCode in his.matches
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('his.matches') AND name = 'ClubCode')
    ALTER TABLE [his].[matches] ADD [ClubCode] NVARCHAR(20) NULL;
GO

-- ClubCode in his.matchdetails
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('his.matchdetails') AND name = 'ClubCode')
    ALTER TABLE [his].[matchdetails] ADD [ClubCode] NVARCHAR(20) NULL;
GO

-- SyncEnabled in dbo.AppSettings
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'SyncEnabled')
    ALTER TABLE [dbo].[AppSettings] ADD [SyncEnabled] BIT NOT NULL DEFAULT 1;
GO

-- UNIQUE constraint op ClubCode (één rij per club)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.AppSettings') AND name = 'UQ_AppSettings_ClubCode')
    ALTER TABLE [dbo].[AppSettings] ADD CONSTRAINT [UQ_AppSettings_ClubCode] UNIQUE ([ClubCode]);
GO

-- Bestaande rijen koppelen aan de primaire club
UPDATE [his].[teams]
    SET [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1)
    WHERE [ClubCode] IS NULL;
GO

UPDATE [his].[matches]
    SET [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1)
    WHERE [ClubCode] IS NULL;
GO

UPDATE [his].[matchdetails]
    SET [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1)
    WHERE [ClubCode] IS NULL;
GO
