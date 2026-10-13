using System.Data;
using Microsoft.Data.SqlClient;
using Planner.Shared.Teambegeleiding;

namespace SportlinkFunction.Admin;

/// <summary>
/// SQL Server-implementatie van <see cref="ITeambegeleidingInzageStore"/> (#1701). Tegenhanger:
/// <c>FunctionApp.Postgres/Admin/PostgresTeambegeleidingInzageStore.cs</c> — tier-eigen SQL, geen gedeelde
/// providerabstractie. De orkestratie (eerst ophalen, dan vastleggen, fail-closed) staat in
/// <c>Planner.Endpoints/Teambegeleiding/TeambegeleidingInzageEndpointCore</c>.
/// Schema: <c>[avg].[Teambegeleiding]</c> (lezen) en <c>[avg].[TeambegeleidingInzageLog]</c>
/// (Database/avg/Tables + Script.PostDeployment1.sql).
/// </summary>
internal sealed class SqlTeambegeleidingInzageStore(string connectionString) : ITeambegeleidingInzageStore
{
    public async Task<IReadOnlyList<TeambegeleiderContact>> HaalContactgegevensAsync(string clubCode, string team)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            SELECT [Naam], [Teamrol], [Emailadres], [Telefoonnummer], [Functie]
            FROM [avg].[Teambegeleiding]
            WHERE [Team] = @team
              AND [ClubCode] = @ClubCode
            ORDER BY
                CASE WHEN [Teamrol] LIKE '%Trainer%' THEN 1
                     WHEN [Teamrol] LIKE '%Coach%' THEN 2
                     WHEN [Teamrol] LIKE '%Teamleider%' THEN 3
                     ELSE 4 END,
                [Naam]
        ", connection);
        command.Parameters.AddWithValue("@team", team);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        using var reader = await command.ExecuteReaderAsync();
        var contacten = new List<TeambegeleiderContact>();
        while (await reader.ReadAsync())
            contacten.Add(TeambegeleiderContact.LeesVan(reader));
        return contacten;
    }

    public async Task LogInzageAsync(TeambegeleidingInzageNieuw inzage)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            INSERT INTO [avg].[TeambegeleidingInzageLog] ([ClubCode], [InzienDoorObjectId], [InzienDoorNaam], [Team], [AantalRijen])
            VALUES (@ClubCode, @Oid, @Naam, @Team, @Aantal)", connection);
        command.Parameters.Add("@ClubCode", SqlDbType.NVarChar, 20).Value = inzage.ClubCode;
        command.Parameters.Add("@Oid", SqlDbType.NVarChar, TeambegeleidingInzageNieuw.MaxObjectId).Value = (object?)inzage.InzienDoorObjectId ?? DBNull.Value;
        command.Parameters.Add("@Naam", SqlDbType.NVarChar, TeambegeleidingInzageNieuw.MaxNaam).Value = (object?)inzage.InzienDoorNaam ?? DBNull.Value;
        command.Parameters.Add("@Team", SqlDbType.NVarChar, TeambegeleidingInzageNieuw.MaxTeam).Value = inzage.Team;
        command.Parameters.Add("@Aantal", SqlDbType.Int).Value = inzage.AantalRijen;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Bewaartermijn (#1701): verwijdert inzageregels ouder dan
    /// <see cref="TeambegeleidingInzageRetentie.InzageLogMaanden"/> via <c>[avg].[sp_CleanupTeambegeleidingInzageLog]</c>.
    /// Aangeroepen door de maandelijkse timer <c>CleanupTeambegeleiding</c>. Geeft alleen het aantal terug.
    /// </summary>
    internal static async Task<int> VerwijderVerlopenInzageAsync(SqlConnection connection, DateTime nuUtc)
    {
        using var command = new SqlCommand("[avg].[sp_CleanupTeambegeleidingInzageLog]", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 120
        };
        command.Parameters.Add("@NuUtc", SqlDbType.DateTime2).Value = nuUtc;
        command.Parameters.Add("@InzageMaanden", SqlDbType.Int).Value = TeambegeleidingInzageRetentie.InzageLogMaanden;
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }
}
