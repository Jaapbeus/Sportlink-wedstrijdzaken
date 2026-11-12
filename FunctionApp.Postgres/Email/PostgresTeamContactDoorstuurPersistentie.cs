using Npgsql;
using Planner.Shared.Email;

namespace FunctionApp.Postgres.Email;

internal sealed class PostgresTeamContactDoorstuurPersistentie(string connectionString, string clubCode)
    : ITeamContactDoorstuurPersistentie
{
    public async Task<TeamContactDoorstuurClaim> ClaimAsync(int verwerkingId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var claim = new NpgsqlCommand("""
            UPDATE planner.emailverwerking
            SET teamcontactdoorstuurstatus = 'Bezig', mta_modified = NOW()
            WHERE id = @id AND clubcode = @club AND teamcontactdoorstuurstatus = 'NietGestart'
            """, connection);
        claim.Parameters.AddWithValue("id", verwerkingId);
        claim.Parameters.AddWithValue("club", clubCode);
        if (await claim.ExecuteNonQueryAsync() == 1)
            return TeamContactDoorstuurClaim.Geclaimd;

        await using var read = new NpgsqlCommand("""
            SELECT teamcontactdoorstuurstatus FROM planner.emailverwerking
            WHERE id = @id AND clubcode = @club
            """, connection);
        read.Parameters.AddWithValue("id", verwerkingId);
        read.Parameters.AddWithValue("club", clubCode);
        return (await read.ExecuteScalarAsync()) as string == "Geaccepteerd"
            ? TeamContactDoorstuurClaim.AlGeaccepteerd
            : TeamContactDoorstuurClaim.NietBeschikbaar;
    }

    public async Task<bool> MarkeerTerminalAsync(int verwerkingId, TeamContactDoorstuurStatus status)
    {
        if (status is not (TeamContactDoorstuurStatus.Geaccepteerd or TeamContactDoorstuurStatus.NietVerstuurd or TeamContactDoorstuurStatus.Onbekend))
            throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE planner.emailverwerking
            SET teamcontactdoorstuurstatus = @status, mta_modified = NOW()
            WHERE id = @id AND clubcode = @club AND teamcontactdoorstuurstatus = 'Bezig'
            """, connection);
        command.Parameters.AddWithValue("id", verwerkingId);
        command.Parameters.AddWithValue("club", clubCode);
        command.Parameters.AddWithValue("status", status.ToString());
        return await command.ExecuteNonQueryAsync() == 1;
    }
}
