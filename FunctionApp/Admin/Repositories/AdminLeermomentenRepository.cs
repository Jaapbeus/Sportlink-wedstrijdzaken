using Microsoft.Data.SqlClient;
using Planner.Endpoints.Leren;
using Planner.Shared.Leren;

namespace SportlinkFunction.Admin;

internal static class AdminLeermomentenRepository
{
    internal static async Task<(int count, int limit, List<Dictionary<string, object?>> items)> GetAsync(
        string clubCode, string statusFilter, int limit, string cs)
    {
        var whereExtra = statusFilter switch
        {
            "pending"   => "AND [IsGevalideerd] = 0 AND [IsAfgewezen] = 0",
            "validated" => "AND [IsGevalideerd] = 1",
            "rejected"  => "AND [IsAfgewezen] = 1",
            _           => ""
        };
        var sql = $@"SELECT TOP (@Limit)
                    cc.[Id], cc.[OrigineleVerwerkingId], cc.[CorrectionVerwerkingId],
                    cc.[OrigineelVerzoekType], cc.[AfgeleidJuistType],
                    cc.[OrigineleSamenvatting], cc.[CorrectieSamenvatting],
                    cc.[IsGevalideerd], cc.[IsAfgewezen],
                    cc.[Herkomst], cc.[AangemaaktDoorNaam], cc.[AangemaaktOp], cc.[HerkomstVerwerkingId],
                    cc.[mta_inserted], cc.[mta_modified]
                FROM [planner].[ClassificatieCorrectie] cc
                WHERE cc.[ClubCode] = @Cc {whereExtra}
                ORDER BY cc.[mta_inserted] DESC";

        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Limit", limit);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(AdminRepositoryHelpers.LeesAlleKolommenMetUtcDatums(r));
        return (list.Count, limit, list);
    }

    internal static async Task<(int pending, int validated, int rejected)> GetStatsAsync(string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            SELECT
                SUM(CASE WHEN [IsGevalideerd] = 0 AND [IsAfgewezen] = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN [IsGevalideerd] = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN [IsAfgewezen] = 1  THEN 1 ELSE 0 END)
            FROM [planner].[ClassificatieCorrectie]
            WHERE [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (0, 0, 0);
        return (r.IsDBNull(0) ? 0 : r.GetInt32(0),
                r.IsDBNull(1) ? 0 : r.GetInt32(1),
                r.IsDBNull(2) ? 0 : r.GetInt32(2));
    }

    internal static async Task<int> ValideerAsync(int id, bool isGevalideerd, bool isAfgewezen, string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[ClassificatieCorrectie]
            SET [IsGevalideerd] = @IsGv, [IsAfgewezen] = @IsAf, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id",  id);
        cmd.Parameters.AddWithValue("@IsGv", isGevalideerd);
        cmd.Parameters.AddWithValue("@IsAf", isAfgewezen);
        cmd.Parameters.AddWithValue("@Cc",   clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Een leermoment door een beheerder (#1568 deel C): direct gevalideerd, herkomst <c>Admin</c>, zonder
    /// reply-paar (beide verwerkings-id's NULL). De door de beheerder geredigeerde samenvatting staat in
    /// <c>OrigineleSamenvatting</c> — dat is het veld dat de few-shot-prompt als "Samenvatting" toont.
    /// </summary>
    internal static async Task<int> MaakAdminLeermomentAsync(AdminLeermomentOpdracht o, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            INSERT INTO [planner].[ClassificatieCorrectie]
                ([OrigineleVerwerkingId], [CorrectionVerwerkingId], [OrigineelVerzoekType], [AfgeleidJuistType],
                 [OrigineleSamenvatting], [IsGevalideerd], [ClubCode], [Herkomst],
                 [AangemaaktDoor], [AangemaaktDoorNaam], [AangemaaktOp], [HerkomstVerwerkingId])
            OUTPUT INSERTED.[Id]
            VALUES (NULL, NULL, @Origineel, @Juist, @Samenvatting, 1, @Cc, N'Admin', @Door, @Naam, GETUTCDATE(),
                    (SELECT [Id] FROM [planner].[EmailVerwerking] WHERE [Id] = @Verwerking AND [ClubCode] = @Cc))", conn);
        cmd.Parameters.AddWithValue("@Origineel", o.OrigineelType);
        cmd.Parameters.AddWithValue("@Juist", o.JuistType);
        cmd.Parameters.AddWithValue("@Samenvatting", o.Samenvatting);
        cmd.Parameters.AddWithValue("@Cc", o.ClubCode);
        cmd.Parameters.AddWithValue("@Door", o.Wie.DoorId);
        cmd.Parameters.AddWithValue("@Naam", (object?)o.Wie.DoorNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Verwerking", (object?)o.HerkomstVerwerkingId ?? DBNull.Value);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Verwijdert uitsluitend een leermoment met herkomst <c>Admin</c> van de eigen club (AVG, #1568).</summary>
    internal static async Task<LeermomentVerwijderUitkomst> VerwijderAdminLeermomentAsync(int id, string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using (var del = new SqlCommand(
            "DELETE FROM [planner].[ClassificatieCorrectie] WHERE [Id] = @Id AND [ClubCode] = @Cc AND [Herkomst] = N'Admin'", conn))
        {
            del.Parameters.AddWithValue("@Id", id);
            del.Parameters.AddWithValue("@Cc", clubCode);
            if (await del.ExecuteNonQueryAsync() > 0) return LeermomentVerwijderUitkomst.Verwijderd;
        }
        using var bestaat = new SqlCommand(
            "SELECT 1 FROM [planner].[ClassificatieCorrectie] WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        bestaat.Parameters.AddWithValue("@Id", id);
        bestaat.Parameters.AddWithValue("@Cc", clubCode);
        return await bestaat.ExecuteScalarAsync() is null
            ? LeermomentVerwijderUitkomst.NietGevonden : LeermomentVerwijderUitkomst.GeenAdminLeermoment;
    }
}
