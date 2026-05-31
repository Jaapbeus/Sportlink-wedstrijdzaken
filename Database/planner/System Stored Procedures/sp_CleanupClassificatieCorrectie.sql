CREATE PROCEDURE [planner].[sp_CleanupClassificatieCorrectie]
AS
BEGIN
    SET NOCOUNT ON;

    -- VEREISTE AANROEPVOLGORDE: deze SP moet worden aangeroepen VÓÓR sp_CleanupEmailVerwerking.
    -- ClassificatieCorrectie heeft FK naar EmailVerwerking — verwijdering van rijen uit
    -- EmailVerwerking faalt als refererende correcties nog bestaan.

    -- Fase 1: anonimiseer samenvattingen in records 30-90 dagen oud
    UPDATE [planner].[ClassificatieCorrectie]
    SET [OrigineleSamenvatting] = NULL,
        [CorrectieSamenvatting] = NULL,
        [mta_modified]          = GETUTCDATE()
    WHERE [mta_inserted] < DATEADD(DAY, -30, GETUTCDATE())
      AND [mta_inserted] >= DATEADD(DAY, -90, GETUTCDATE())
      AND ([OrigineleSamenvatting] IS NOT NULL
           OR [CorrectieSamenvatting] IS NOT NULL);

    -- Fase 2: verwijder records ouder dan 90 dagen
    DELETE FROM [planner].[ClassificatieCorrectie]
    WHERE [mta_inserted] < DATEADD(DAY, -90, GETUTCDATE());
END;
