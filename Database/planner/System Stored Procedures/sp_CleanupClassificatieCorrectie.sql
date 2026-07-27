CREATE PROCEDURE [planner].[sp_CleanupClassificatieCorrectie]
AS
BEGIN
    SET NOCOUNT ON;

    -- AANROEPVOLGORDE: deze procedure loopt vóór sp_CleanupEmailVerwerking, maar de correctheid
    -- hangt daar niet van af. Eerder stond hier dat de volgorde de FK-afhankelijkheid met
    -- planner.EmailVerwerking afdekte — dat was onjuist en misleidend. Deze procedure verwijdert
    -- correctierijen alleen op hun EIGEN leeftijd, en een correctierij kan jonger zijn dan de rij
    -- waarnaar hij verwijst. Een 91 dagen oude e-mailrij met een 51 dagen oude correctie bleef zo
    -- achter en liet de DELETE in sp_CleanupEmailVerwerking structureel op een FK-schending
    -- vastlopen. Het opruimen van die verwijzingen gebeurt daarom in sp_CleanupEmailVerwerking
    -- zelf, met exact dezelfde grens die bepaalt welke ouderrijen verdwijnen.

    -- Beide grenzen eenmalig uit GETUTCDATE(): anders kan een rij tussen de UPDATE en de DELETE
    -- door van venster wisselen.
    DECLARE @AnonimiseerVanaf DATETIME = DATEADD(DAY, -30, GETUTCDATE());
    DECLARE @VerwijderVoor    DATETIME = DATEADD(DAY, -90, GETUTCDATE());

    -- Fase 1: anonimiseer samenvattingen in records 30-90 dagen oud
    UPDATE [planner].[ClassificatieCorrectie]
    SET [OrigineleSamenvatting] = NULL,
        [CorrectieSamenvatting] = NULL,
        [mta_modified]          = GETUTCDATE()
    WHERE [mta_inserted] < @AnonimiseerVanaf
      AND [mta_inserted] >= @VerwijderVoor
      AND ([OrigineleSamenvatting] IS NOT NULL
           OR [CorrectieSamenvatting] IS NOT NULL);

    -- Fase 2: verwijder records ouder dan 90 dagen — de eigen bewaartermijn van deze tabel.
    -- Correctierijen die jonger zijn maar naar een te verwijderen e-mailrij verwijzen, worden
    -- opgeruimd door sp_CleanupEmailVerwerking (fase 2a).
    DELETE FROM [planner].[ClassificatieCorrectie]
    WHERE [mta_inserted] < @VerwijderVoor;
END;
