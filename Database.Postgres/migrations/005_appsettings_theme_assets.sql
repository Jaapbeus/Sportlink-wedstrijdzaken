-- 005_appsettings_theme_assets.sql — public.appsettings.faviconurl/logourl (#339, #887's
-- AdminThemeFunction). SQL Server voegt deze kolommen toe via een idempotente ALTER in
-- Script.PostDeployment1.sql (regel 361+) in plaats van in de Tables/AppSettings.sql-definitie —
-- vandaar dat 003_admin_tables.sql ze nog niet had.

ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS faviconurl VARCHAR(2048) NULL,
    ADD COLUMN IF NOT EXISTS logourl VARCHAR(2048) NULL;
