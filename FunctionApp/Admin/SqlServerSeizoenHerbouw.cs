using Microsoft.Data.SqlClient;
using Planner.Shared.Sync;

namespace SportlinkFunction.Admin;

/// <summary>
/// Bouwt de seizoensrijen van één club opnieuw op na een wijziging van <c>SeasonStartMonth</c> in
/// Beheer &gt; Instellingen (#1715). Postgres-tegenhanger:
/// <c>Database.Postgres/PostgresSeasonProcedures.HerbouwSeizoenenVoorClubAsync</c>.
/// <para>
/// De tabel <c>dbo.ClubSeason</c> is afgeleide data. Binnen de transactie van de instellingenwijziging (met de
/// auditregel) wordt de startmaand van de club onder lock gelezen, worden de rijen van de club verwijderd en met
/// die maand opnieuw geplaatst (<see cref="SeizoenKalender.Basis"/>); andere clubs blijven staan. Zo slagen
/// configuratie, audit en kalender samen of draaien ze samen terug (Codex-review #1743 ronde 1).
/// Bewust niet via <c>sp_UpdateClubSeasonTable</c>: die herbouwt ook <c>dbo.DateTable</c> (DROP TABLE), wat de
/// databasegebruiker van de Function App niet hoeft te mogen. De DateTable volgt bij de volgende deploy.
/// </para>
/// </summary>
internal static class SqlServerSeizoenHerbouw
{
    /// <returns>Het aantal geplaatste seizoensrijen; 0 als de club geen instellingenrij heeft.</returns>
    internal static async Task<int> HerbouwAsync(
        SqlConnection connection, SqlTransaction transactie, string clubCode, DateOnly vandaag)
    {
        int? opgeslagen;
        await using (var lees = new SqlCommand(
            "SELECT [SeasonStartMonth] FROM [dbo].[AppSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [ClubCode] = @ClubCode",
            connection, transactie))
        {
            lees.Parameters.AddWithValue("@ClubCode", clubCode);
            var waarde = await lees.ExecuteScalarAsync();
            if (waarde is null)
                return 0; // geen instellingenrij: niets af te leiden, bestaande rijen blijven staan
            opgeslagen = waarde is DBNull ? null : Convert.ToInt32(waarde, System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var verwijder = new SqlCommand("DELETE FROM [dbo].[ClubSeason] WHERE [ClubCode] = @ClubCode", connection, transactie))
        {
            verwijder.Parameters.AddWithValue("@ClubCode", clubCode);
            await verwijder.ExecuteNonQueryAsync();
        }

        var rijen = SeizoenKalender.Basis(SeizoenStartMaand.OfStandaard(opgeslagen), vandaag);
        foreach (var rij in rijen)
        {
            await using var voegToe = new SqlCommand(
                "INSERT INTO [dbo].[ClubSeason] ([ClubCode], [Name], [DateFrom], [DateUntil]) VALUES (@ClubCode, @Name, @DateFrom, @DateUntil)",
                connection, transactie);
            voegToe.Parameters.AddWithValue("@ClubCode", clubCode);
            voegToe.Parameters.AddWithValue("@Name", rij.Naam);
            voegToe.Parameters.Add(new SqlParameter("@DateFrom", System.Data.SqlDbType.Date) { Value = rij.Van.ToDateTime(TimeOnly.MinValue) });
            voegToe.Parameters.Add(new SqlParameter("@DateUntil", System.Data.SqlDbType.Date) { Value = rij.Tot.ToDateTime(TimeOnly.MinValue) });
            await voegToe.ExecuteNonQueryAsync();
        }

        return rijen.Count;
    }
}
