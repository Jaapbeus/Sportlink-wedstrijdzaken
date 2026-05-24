CREATE PROCEDURE [avg].[sp_CleanupTeambegeleiding]
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
