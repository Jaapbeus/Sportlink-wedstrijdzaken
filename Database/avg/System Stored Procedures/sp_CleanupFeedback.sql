-- #764/#1476: bewaartermijnen van de feedbackmeldingen. Postgres-tegenhanger:
-- Database.Postgres/PostgresCleanupProcedures.CleanupFeedbackAsync.
--  * Identiteit (MelderObjectId + MelderNaam) -> NULL, 24 maanden na sluiting van het GitHub-issue
--    (IssueGeslotenOpUtc). Een nooit gepubliceerde melding (IssueNummer IS NULL) telt vanaf aanmaak.
--  * Technische context (avg.FeedbackTelemetrie): 90 dagen.
--  * Inzagelog (avg.FeedbackInzageLog): 24 maanden.
-- De meldingstekst zelf blijft bewaard. De termijnen zijn parameters; de aanroeper leest ze uit
-- Planner.Shared.Feedback.FeedbackRetentie. Geeft uitsluitend aantallen terug.
CREATE PROCEDURE [avg].[sp_CleanupFeedback]
    @NuUtc               DATETIME2,
    @IdentiteitMaanden   INT,
    @TelemetrieDagen     INT,
    @InzageMaanden       INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Geanonimiseerd INT, @Telemetrie INT, @Inzage INT;
    DECLARE @Grens DATETIME2 = DATEADD(MONTH, -@IdentiteitMaanden, @NuUtc);

    UPDATE [avg].[Feedback]
    SET [MelderObjectId]      = NULL,
        [MelderNaam]          = NULL,
        [IsGeanonimiseerd]    = 1,
        [GeanonimiseerdOpUtc] = @NuUtc,
        [mta_modified]        = @NuUtc
    WHERE [IsGeanonimiseerd] = 0
      AND (([IssueGeslotenOpUtc] IS NOT NULL AND [IssueGeslotenOpUtc] < @Grens)
           OR ([IssueNummer] IS NULL AND [mta_inserted] < @Grens));
    SET @Geanonimiseerd = @@ROWCOUNT;

    DELETE FROM [avg].[FeedbackTelemetrie] WHERE [mta_inserted] < DATEADD(DAY, -@TelemetrieDagen, @NuUtc);
    SET @Telemetrie = @@ROWCOUNT;

    DELETE FROM [avg].[FeedbackInzageLog] WHERE [mta_inserted] < DATEADD(MONTH, -@InzageMaanden, @NuUtc);
    SET @Inzage = @@ROWCOUNT;

    SELECT @Geanonimiseerd AS [Geanonimiseerd], @Telemetrie AS [TelemetrieVerwijderd], @Inzage AS [InzageVerwijderd];
END;
