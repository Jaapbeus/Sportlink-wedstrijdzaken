-- #1701: bewaartermijn van het inzagelog van teambegeleiding. Postgres-tegenhanger:
-- Database.Postgres/PostgresCleanupProcedures.CleanupTeambegeleidingInzageLogAsync.
-- Inzageregels ouder dan @InzageMaanden (de aanroeper leest de termijn uit
-- Planner.Shared.Teambegeleiding.TeambegeleidingInzageRetentie) worden verwijderd; de tijdgrens wordt
-- uit @NuUtc berekend. Geeft uitsluitend het aantal verwijderde rijen terug.
CREATE PROCEDURE [avg].[sp_CleanupTeambegeleidingInzageLog]
    @NuUtc         DATETIME2,
    @InzageMaanden INT
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM [avg].[TeambegeleidingInzageLog] WHERE [mta_inserted] < DATEADD(MONTH, -@InzageMaanden, @NuUtc);

    SELECT @@ROWCOUNT AS [InzageVerwijderd];
END;
