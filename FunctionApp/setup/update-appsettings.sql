-- Update AppSettings with Sportlink API Configuration
-- Run this after the complete-database-setup.sql
-- IMPORTANT: Replace 'YOUR_CLIENT_ID_HERE' with your actual Sportlink client ID

USE SportlinkSqlDb;
GO

-- Update the AppSettings table with the correct API URL
UPDATE [dbo].[AppSettings]
SET 
    [sportlinkApiUrl] = 'https://data.sportlink.com/poule',
    [sportlinkClientId] = 'YOUR_CLIENT_ID_HERE',  -- REPLACE THIS!
    [ModifiedDate] = GETDATE()
WHERE [Id] = 1;
GO

-- Verify the settings
SELECT 
    [Id],
    [sportlinkApiUrl],
    [sportlinkClientId],
    [CreatedDate],
    [ModifiedDate]
FROM [dbo].[AppSettings];
GO

PRINT '';
PRINT '========================================';
PRINT 'AppSettings Updated!';
PRINT '========================================';
PRINT 'REMINDER: Make sure to replace YOUR_CLIENT_ID_HERE with your actual client ID';
PRINT '';
