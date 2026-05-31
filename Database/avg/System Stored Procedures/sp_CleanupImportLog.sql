CREATE PROCEDURE [avg].[sp_CleanupImportLog]
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
