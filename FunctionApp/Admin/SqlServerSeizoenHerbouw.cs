using Microsoft.Data.SqlClient;
using Planner.Shared.Sync;

namespace SportlinkFunction.Admin;

/// <summary>
/// Bouwt de seizoensrijen van één club opnieuw op na een wijziging van <c>SeasonStartMonth</c> in
/// Beheer &gt; Instellingen (#1715). Postgres-tegenhanger:
/// <c>Database.Postgres/PostgresSeasonProcedures.HerbouwSeizoenenVoorClubAsync</c>.
/// <para>
/// De tabel <c>dbo.Season</c> is afgeleide data. De rijen van de club worden in één transactie verwijderd en
/// met de nieuwe maand opnieuw geplaatst (<see cref="SeizoenKalender.Basis"/>); andere clubs blijven staan.
/// Bewust niet via <c>sp_UpdateSeasonTable</c>: die herbouwt ook <c>dbo.DateTable</c> (DROP TABLE), wat de
/// databasegebruiker van de Function App niet hoeft te mogen. De DateTable volgt bij de volgende deploy.
/// </para>
/// </summary>
internal static class SqlServerSeizoenHerbouw
{
    internal static async Task<int> HerbouwAsync(
        SqlConnection connection, string clubCode, int startMaand, DateOnly vandaag)
    {
        await using var transactie = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var verwijder = new SqlCommand("DELETE FROM [dbo].[Season] WHERE [ClubCode] = @ClubCode", connection, transactie))
        {
            verwijder.Parameters.AddWithValue("@ClubCode", clubCode);
            await verwijder.ExecuteNonQueryAsync();
        }

        var rijen = SeizoenKalender.Basis(startMaand, vandaag);
        foreach (var rij in rijen)
        {
            await using var voegToe = new SqlCommand(
                "INSERT INTO [dbo].[Season] ([ClubCode], [Name], [DateFrom], [DateUntil]) VALUES (@ClubCode, @Name, @DateFrom, @DateUntil)",
                connection, transactie);
            voegToe.Parameters.AddWithValue("@ClubCode", clubCode);
            voegToe.Parameters.AddWithValue("@Name", rij.Naam);
            voegToe.Parameters.Add(new SqlParameter("@DateFrom", System.Data.SqlDbType.Date) { Value = rij.Van.ToDateTime(TimeOnly.MinValue) });
            voegToe.Parameters.Add(new SqlParameter("@DateUntil", System.Data.SqlDbType.Date) { Value = rij.Tot.ToDateTime(TimeOnly.MinValue) });
            await voegToe.ExecuteNonQueryAsync();
        }

        await transactie.CommitAsync();
        return rijen.Count;
    }
}
