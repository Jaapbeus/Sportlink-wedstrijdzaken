using Npgsql;
using Planner.Shared.Teambegeleiding;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-implementatie van <see cref="ITeambegeleidingInzageStore"/> (#1701). Tegenhanger:
/// <c>FunctionApp/Admin/SqlTeambegeleidingInzageStore.cs</c> — tier-eigen SQL, geen gedeelde
/// providerabstractie. De orkestratie (eerst ophalen, dan vastleggen, fail-closed) staat in
/// <c>Planner.Endpoints/Teambegeleiding/TeambegeleidingInzageEndpointCore</c>.
/// Schema: <c>avg.teambegeleiding</c> (lezen) en <c>avg.teambegeleidinginzagelog</c> (migratie 042).
/// </summary>
internal sealed class PostgresTeambegeleidingInzageStore(string connectionString) : ITeambegeleidingInzageStore
{
    public async Task<IReadOnlyList<TeambegeleiderContact>> HaalContactgegevensAsync(string clubCode, string team)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($@"
            SELECT naam, teamrol, emailadres, telefoonnummer, functie
            FROM avg.teambegeleiding
            WHERE team = @team
              AND clubcode = @clubcode
            ORDER BY {AdminTeambegeleidingFunction.RolVoorkeurCase}, naam
        ", connection);
        command.Parameters.AddWithValue("team", team);
        command.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await command.ExecuteReaderAsync();
        var contacten = new List<TeambegeleiderContact>();
        while (await reader.ReadAsync())
            contacten.Add(TeambegeleiderContact.LeesVan(reader));
        return contacten;
    }

    public async Task LogInzageAsync(TeambegeleidingInzageNieuw inzage)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            INSERT INTO avg.teambegeleidinginzagelog (clubcode, inziendoorobjectid, inziendoornaam, team, aantalrijen)
            VALUES (@clubcode, @oid, @naam, @team, @aantal)", connection);
        command.Parameters.AddWithValue("clubcode", inzage.ClubCode);
        command.Parameters.AddWithValue("oid", (object?)inzage.InzienDoorObjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("naam", (object?)inzage.InzienDoorNaam ?? DBNull.Value);
        command.Parameters.AddWithValue("team", inzage.Team);
        command.Parameters.AddWithValue("aantal", inzage.AantalRijen);
        await command.ExecuteNonQueryAsync();
    }
}
