CREATE PROCEDURE [planner].[sp_CleanupEmailVerwerking]
AS
BEGIN
    SET NOCOUNT ON;

    -- De bewaartermijn van planner.EmailVerwerking staat hier op ÉÉN plek. Beide grenzen worden
    -- eenmalig uit GETUTCDATE() berekend: anders kan een rij tussen twee statements door van
    -- venster wisselen (geanonimiseerd maar niet verwijderd, of omgekeerd).
    DECLARE @AnonimiseerVanaf DATETIME = DATEADD(DAY, -30, GETUTCDATE());
    DECLARE @VerwijderVoor    DATETIME = DATEADD(DAY, -90, GETUTCDATE());

    -- Fase 1: anonimiseer PII in rijen van 30-90 dagen oud
    -- Afzender en Onderwerp zijn NOT NULL → vervangen door placeholder.
    -- Nullbare velden worden op NULL gezet, inclusief FoutMelding (#420): een foutmelding bevat
    -- vaak het e-mailadres of een fragment van het bericht en is dus zelf een persoonsgegeven.
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
    WHERE [mta_inserted] < @AnonimiseerVanaf
      AND [mta_inserted] >= @VerwijderVoor
      AND ([Afzender] <> '[geanonimiseerd]'
           OR [EmailBody] IS NOT NULL
           OR [AntwoordEmail] IS NOT NULL
           OR [PlannerResponse] IS NOT NULL
           OR [GeextraheerdeData] IS NOT NULL
           OR [FoutMelding] IS NOT NULL);

    -- Fase 2a: verwijder correctierijen die verwijzen naar een rij die hieronder verdwijnt.
    --
    -- planner.ClassificatieCorrectie heeft twee foreign keys naar deze tabel
    -- (FK_..._Origineel en FK_..._Correctie) zonder ON DELETE CASCADE. Cascade is hier ook geen
    -- optie: twee cascadepaden vanuit dezelfde tabel naar dezelfde ouder weigert SQL Server met
    -- Msg 1785 ("may cause cycles or multiple cascade paths").
    --
    -- Een correctierij kan JONGER zijn dan de rij waarnaar hij verwijst — replydetectie kent geen
    -- tijdgrens, dus een reply op dag 40 hoort bij een bericht van dag 0. De eigen 90-dagenregel in
    -- sp_CleanupClassificatieCorrectie ruimt die correctierij dan niet op. Zonder deze DELETE
    -- faalt fase 2b op een FK-schending, gooit de aanroepende Function de fout door en wordt er
    -- vanaf dat moment nooit meer iets verwijderd: elke wekelijkse run klapt op dezelfde rij en de
    -- bewaartermijn van 90 dagen wordt structureel niet gehaald.
    --
    -- Deze opruiming staat bewust in DEZE procedure: dezelfde @VerwijderVoor bepaalt welke
    -- ouderrijen verdwijnen en welke verwijzingen dus mee moeten. De twee kunnen daardoor niet uit
    -- elkaar lopen, en de aanroepvolgorde van de twee cleanup-procedures is niet langer bepalend
    -- voor de correctheid.
    DELETE cc
    FROM [planner].[ClassificatieCorrectie] cc
    WHERE EXISTS (
        SELECT 1
        FROM [planner].[EmailVerwerking] ev
        WHERE ev.[Id] IN (cc.[OrigineleVerwerkingId], cc.[CorrectionVerwerkingId])
          AND ev.[mta_inserted] < @VerwijderVoor
    );

    -- Fase 2b: verwijder rijen ouder dan 90 dagen
    DELETE FROM [planner].[EmailVerwerking]
    WHERE [mta_inserted] < @VerwijderVoor;
END;
