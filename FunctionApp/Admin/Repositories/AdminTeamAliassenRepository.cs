using Microsoft.Data.SqlClient;
using Planner.Shared.Leren;

namespace SportlinkFunction.Admin;

/// <summary>
/// Data-access voor <c>dbo.TeamAliassen</c> (#701). Alle queries zijn geparameteriseerd en
/// altijd gescoped op ClubCode — een alias van club A mag nooit zichtbaar of muteerbaar zijn
/// vanuit club B.
/// </summary>
internal static class AdminTeamAliassenRepository
{
    internal static async Task<(int count, int limit, List<Dictionary<string, object?>> items)> GetAsync(
        string clubCode, string? statusFilter, int limit, string cs)
    {
        // Statusfilter uitsluitend als parameter — nooit in de SQL-string interpoleren.
        var heeftStatusFilter = !string.IsNullOrWhiteSpace(statusFilter);
        var sql = $@"SELECT TOP (@Limit)
                    ta.[Id], ta.[RuweTekst], ta.[RuweTekstGenormaliseerd],
                    ta.[TeamId], t.[Teamnaam], t.[LeeftijdsCategorie],
                    ta.[Bron], ta.[Status], ta.[AantalKeerGebruikt],
                    ta.[mta_inserted], ta.[mta_modified],
                    ta.[AangemaaktDoorNaam], ta.[AangemaaktOp], ta.[HerkomstVerwerkingId], ta.[Reden],
                    ta.[BeoordeeldDoorNaam], ta.[BeoordeeldOp]
                FROM [dbo].[TeamAliassen] ta
                LEFT JOIN [dbo].[Teams] t
                    ON t.[TeamId] = ta.[TeamId] AND t.[ClubCode] = ta.[ClubCode]
                WHERE ta.[ClubCode] = @Cc
                  {(heeftStatusFilter ? "AND ta.[Status] = @Status" : "")}
                ORDER BY CASE WHEN ta.[Status] = 'pending' THEN 0 ELSE 1 END,
                         ta.[mta_inserted] DESC";

        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Limit", limit);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        if (heeftStatusFilter)
            cmd.Parameters.AddWithValue("@Status", statusFilter!);

        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            list.Add(new()
            {
                ["id"]                      = r.GetInt32(r.GetOrdinal("Id")),
                ["ruweTekst"]               = r.GetString(r.GetOrdinal("RuweTekst")),
                ["ruweTekstGenormaliseerd"] = r.GetString(r.GetOrdinal("RuweTekstGenormaliseerd")),
                ["teamId"]                  = r.GetInt32(r.GetOrdinal("TeamId")),
                ["teamnaam"]                = Nullable(r, "Teamnaam"),
                ["leeftijdsCategorie"]      = Nullable(r, "LeeftijdsCategorie"),
                ["bron"]                    = r.GetString(r.GetOrdinal("Bron")),
                ["status"]                  = r.GetString(r.GetOrdinal("Status")),
                ["aantalKeerGebruikt"]      = r.GetInt32(r.GetOrdinal("AantalKeerGebruikt")),
                ["mtaInserted"]             = Utc(r, "mta_inserted"),
                ["mtaModified"]             = Utc(r, "mta_modified"),
                ["aangemaaktDoorNaam"]      = Nullable(r, "AangemaaktDoorNaam"),
                ["aangemaaktOp"]            = Utc(r, "AangemaaktOp"),
                ["herkomstVerwerkingId"]    = NullableInt(r, "HerkomstVerwerkingId"),
                ["reden"]                   = Nullable(r, "Reden"),
                ["beoordeeldDoorNaam"]      = Nullable(r, "BeoordeeldDoorNaam"),
                ["beoordeeldOp"]            = Utc(r, "BeoordeeldOp"),
            });
        }
        return (list.Count, limit, list);
    }

    internal static async Task<(int pending, int validated, int rejected)> GetStatsAsync(string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            SELECT
                SUM(CASE WHEN [Status] = 'pending'   THEN 1 ELSE 0 END),
                SUM(CASE WHEN [Status] = 'validated' THEN 1 ELSE 0 END),
                SUM(CASE WHEN [Status] = 'rejected'  THEN 1 ELSE 0 END)
            FROM [dbo].[TeamAliassen]
            WHERE [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (0, 0, 0);
        return (r.IsDBNull(0) ? 0 : r.GetInt32(0),
                r.IsDBNull(1) ? 0 : r.GetInt32(1),
                r.IsDBNull(2) ? 0 : r.GetInt32(2));
    }

    /// <summary>
    /// Zet de status van één alias en legt vast wie (object-ID + naammomentopname) en wanneer (UTC) dat deed
    /// (#1568 deel C). Retourneert het aantal geraakte rijen (0 = niet gevonden).
    /// </summary>
    internal static async Task<int> ZetStatusAsync(int id, string status, string clubCode, LerenAanroeper wie, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[TeamAliassen]
            SET [Status] = @Status, [mta_modified] = GETUTCDATE(),
                [BeoordeeldDoor] = @Door, [BeoordeeldDoorNaam] = @Naam, [BeoordeeldOp] = GETUTCDATE()
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id",     id);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@Door",   wie.DoorId);
        cmd.Parameters.AddWithValue("@Naam",   (object?)wie.DoorNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Cc",     clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[TeamAliassen] WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static string? Nullable(SqlDataReader r, string kolom)
    {
        var i = r.GetOrdinal(kolom);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static int? NullableInt(SqlDataReader r, string kolom)
    {
        var i = r.GetOrdinal(kolom);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    /// <summary>DB slaat UTC op; markeer expliciet als UTC zodat de JSON een Z-suffix krijgt.</summary>
    private static DateTime? Utc(SqlDataReader r, string kolom)
    {
        var i = r.GetOrdinal(kolom);
        return r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
    }
}
