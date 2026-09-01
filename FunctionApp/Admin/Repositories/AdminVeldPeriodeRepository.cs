using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

/// <summary>
/// Repository voor dbo.VeldPeriode — herbruikbare regimes (bijv. "Zomerstop", "Competitie") waar
/// dbo.VeldBeschikbaarheid-rijen naar kunnen verwijzen (#581). Een club zonder periodes heeft hier
/// simpelweg geen rijen; dat verandert niets aan het bestaande gedrag.
/// </summary>
internal static class AdminVeldPeriodeRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAlleAsync(string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            SELECT [Id], [Naam],
                   CONVERT(VARCHAR(10), [DatumVan], 23) AS [DatumVan],
                   CONVERT(VARCHAR(10), [DatumTot], 23) AS [DatumTot],
                   [Actief]
            FROM [dbo].[VeldPeriode]
            WHERE [ClubCode] = @Cc
            ORDER BY [DatumVan]", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(AdminRepositoryHelpers.LeesAlleKolommen(r));
        return list;
    }

    internal static async Task<bool> BestaatAsync(int id, string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM [dbo].[VeldPeriode] WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    /// <summary>
    /// Weigert overlappende periodes voor dezelfde club (#581, open vraag 1 uit het issue): er mag
    /// nooit meer dan één periode tegelijk actief zijn, dus wordt overlap al bij het opslaan
    /// geblokkeerd in plaats van pas bij het plannen een voorrangsregel te moeten toepassen.
    /// <paramref name="uitgesloten"/> is de eigen Id bij een update (anders overlapt een periode
    /// altijd met zichzelf).
    /// </summary>
    internal static async Task<bool> OverlaptMetAndereAsync(
        DateOnly datumVan, DateOnly datumTot, string clubCode, string cs, int? uitgesloten = null)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            SELECT COUNT(1) FROM [dbo].[VeldPeriode]
            WHERE [ClubCode] = @Cc AND [Actief] = 1
              AND (@Uitgesloten IS NULL OR [Id] <> @Uitgesloten)
              AND [DatumVan] <= @DatumTot AND [DatumTot] >= @DatumVan", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        cmd.Parameters.AddWithValue("@Uitgesloten", (object?)uitgesloten ?? DBNull.Value);
        cmd.Parameters.Add("@DatumVan", System.Data.SqlDbType.Date).Value = datumVan.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@DatumTot", System.Data.SqlDbType.Date).Value = datumTot.ToDateTime(TimeOnly.MinValue);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task<int> InsertAsync(
        string naam, DateOnly datumVan, DateOnly datumTot, bool actief, string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[VeldPeriode] ([Naam], [DatumVan], [DatumTot], [Actief], [ClubCode])
            OUTPUT INSERTED.[Id]
            VALUES (@Naam, @Van, @Tot, @Act, @Cc)", conn);
        cmd.Parameters.AddWithValue("@Naam", naam);
        cmd.Parameters.Add("@Van", System.Data.SqlDbType.Date).Value = datumVan.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@Tot", System.Data.SqlDbType.Date).Value = datumTot.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.AddWithValue("@Act", actief);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> UpdateAsync(
        int id, string naam, DateOnly datumVan, DateOnly datumTot, bool actief, string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[VeldPeriode]
            SET [Naam] = @Naam, [DatumVan] = @Van, [DatumTot] = @Tot, [Actief] = @Act
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        cmd.Parameters.AddWithValue("@Naam", naam);
        cmd.Parameters.Add("@Van", System.Data.SqlDbType.Date).Value = datumVan.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@Tot", System.Data.SqlDbType.Date).Value = datumTot.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.AddWithValue("@Act", actief);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Verwijdert de periode niet als er nog VeldBeschikbaarheid-rijen naar verwijzen — anders
    /// verliezen die rijen stilzwijgend hun periode-scoping en gelden ze weer als standaardregime.
    /// De beheerder moet die rijen eerst zelf loskoppelen of verwijderen.
    /// </summary>
    internal static async Task<bool> InGebruikAsync(int id, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM [dbo].[VeldBeschikbaarheid] WHERE [PeriodeId] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[VeldPeriode] WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
