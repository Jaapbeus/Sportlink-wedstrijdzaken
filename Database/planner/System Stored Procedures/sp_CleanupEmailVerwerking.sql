CREATE PROCEDURE [planner].[sp_CleanupEmailVerwerking]
AS
BEGIN
    SET NOCOUNT ON;

    -- Fase 1: anonimiseer PII in rijen van 30-90 dagen oud
    -- Afzender en Onderwerp zijn NOT NULL → vervangen door placeholder.
    -- Nullbare velden worden op NULL gezet, inclusief FoutMelding (#420).
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
    WHERE [mta_inserted] < DATEADD(DAY, -30, GETUTCDATE())
      AND [mta_inserted] >= DATEADD(DAY, -90, GETUTCDATE())
      AND ([Afzender] <> '[geanonimiseerd]'
           OR [EmailBody] IS NOT NULL
           OR [AntwoordEmail] IS NOT NULL
           OR [PlannerResponse] IS NOT NULL
           OR [GeextraheerdeData] IS NOT NULL
           OR [FoutMelding] IS NOT NULL);

    -- Fase 2: verwijder rijen ouder dan 90 dagen
    DELETE FROM [planner].[EmailVerwerking]
    WHERE [mta_inserted] < DATEADD(DAY, -90, GETUTCDATE());
END;
