using Microsoft.Data.SqlClient;
using Planner.Shared.Email;

namespace SportlinkFunction.Email;

internal sealed class SqlTeamContactDoorstuurPersistentie(string connectionString, string clubCode)
    : ITeamContactDoorstuurPersistentie
{
    public async Task<TeamContactDoorstuurClaim> ClaimAsync(int verwerkingId)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var claim = new SqlCommand("""
            UPDATE [planner].[EmailVerwerking]
            SET [TeamContactDoorstuurStatus] = 'Bezig', [mta_modified] = GETUTCDATE()
            WHERE [Id] = @id AND [ClubCode] = @club AND [TeamContactDoorstuurStatus] = 'NietGestart'
            """, connection);
        claim.Parameters.AddWithValue("@id", verwerkingId);
        claim.Parameters.AddWithValue("@club", clubCode);
        if (await claim.ExecuteNonQueryAsync() == 1)
            return TeamContactDoorstuurClaim.Geclaimd;

        using var read = new SqlCommand("""
            SELECT [TeamContactDoorstuurStatus] FROM [planner].[EmailVerwerking]
            WHERE [Id] = @id AND [ClubCode] = @club
            """, connection);
        read.Parameters.AddWithValue("@id", verwerkingId);
        read.Parameters.AddWithValue("@club", clubCode);
        return (await read.ExecuteScalarAsync()) as string == "Geaccepteerd"
            ? TeamContactDoorstuurClaim.AlGeaccepteerd
            : TeamContactDoorstuurClaim.NietBeschikbaar;
    }

    public async Task<bool> MarkeerTerminalAsync(int verwerkingId, TeamContactDoorstuurStatus status)
    {
        if (status is not (TeamContactDoorstuurStatus.Geaccepteerd or TeamContactDoorstuurStatus.NietVerstuurd or TeamContactDoorstuurStatus.Onbekend))
            throw new ArgumentOutOfRangeException(nameof(status));
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand("""
            UPDATE [planner].[EmailVerwerking]
            SET [TeamContactDoorstuurStatus] = @status, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @id AND [ClubCode] = @club AND [TeamContactDoorstuurStatus] = 'Bezig'
            """, connection);
        command.Parameters.AddWithValue("@id", verwerkingId);
        command.Parameters.AddWithValue("@club", clubCode);
        command.Parameters.AddWithValue("@status", status.ToString());
        return await command.ExecuteNonQueryAsync() == 1;
    }
}
