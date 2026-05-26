-- Migratie 003: FaviconUrl en LogoUrl toevoegen aan dbo.AppSettings
-- Idempotent: veilig meerdere keren uit te voeren.
-- Van toepassing op alle rijen (alle clubs).

IF NOT EXISTS (
    SELECT 1 FROM [sys].[columns]
    WHERE [object_id] = OBJECT_ID('[dbo].[AppSettings]') AND [name] = 'FaviconUrl'
)
BEGIN
    ALTER TABLE [dbo].[AppSettings] ADD [FaviconUrl] NVARCHAR(2048) NULL;
    PRINT 'Kolom FaviconUrl toegevoegd aan dbo.AppSettings';
END
ELSE
    PRINT 'Kolom FaviconUrl bestaat al — overgeslagen';
GO

IF NOT EXISTS (
    SELECT 1 FROM [sys].[columns]
    WHERE [object_id] = OBJECT_ID('[dbo].[AppSettings]') AND [name] = 'LogoUrl'
)
BEGIN
    ALTER TABLE [dbo].[AppSettings] ADD [LogoUrl] NVARCHAR(2048) NULL;
    PRINT 'Kolom LogoUrl toegevoegd aan dbo.AppSettings';
END
ELSE
    PRINT 'Kolom LogoUrl bestaat al — overgeslagen';
GO
