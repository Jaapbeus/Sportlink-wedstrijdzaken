using Npgsql;
using Planner.Endpoints.Leren;
using Planner.Shared.Leren;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Admin/Repositories/AdminLeermomentenRepository.cs</c> (#887). Vertaling:
/// <c>[planner].[ClassificatieCorrectie]</c> → <c>planner.classificatiecorrectie</c>,
/// <c>TOP (@Limit)</c> → <c>LIMIT @limit</c>, gequote PascalCase-aliassen (#855),
/// <c>SUM(CASE …)</c> levert in Postgres <c>bigint</c> op (<c>GetInt64</c> i.p.v. <c>GetInt32</c>).
/// Geen <c>DateTime.SpecifyKind</c> nodig — Npgsql geeft <c>TIMESTAMPTZ</c> al terug met
/// <c>Kind=Utc</c>.
/// </summary>
internal static class AdminLeermomentenRepository
{
    internal static async Task<(int count, int limit, List<Dictionary<string, object?>> items)> GetAsync(
        string clubCode, string statusFilter, int limit, string cs)
    {
        var whereExtra = statusFilter switch
        {
            "pending"   => "AND cc.isgevalideerd = FALSE AND cc.isafgewezen = FALSE",
            "validated" => "AND cc.isgevalideerd = TRUE",
            "rejected"  => "AND cc.isafgewezen = TRUE",
            _           => ""
        };
        var sql = $@"SELECT
                    cc.id AS ""Id"", cc.origineleverwerkingid AS ""OrigineleVerwerkingId"",
                    cc.correctionverwerkingid AS ""CorrectionVerwerkingId"",
                    cc.origineelverzoektype AS ""OrigineelVerzoekType"",
                    cc.afgeleidjuisttype AS ""AfgeleidJuistType"",
                    cc.originelesamenvatting AS ""OrigineleSamenvatting"",
                    cc.correctiesamenvatting AS ""CorrectieSamenvatting"",
                    cc.isgevalideerd AS ""IsGevalideerd"", cc.isafgewezen AS ""IsAfgewezen"",
                    cc.herkomst AS ""Herkomst"", cc.aangemaaktdoornaam AS ""AangemaaktDoorNaam"",
                    cc.aangemaaktop AS ""AangemaaktOp"", cc.herkomstverwerkingid AS ""HerkomstVerwerkingId"",
                    cc.mta_inserted, cc.mta_modified
                FROM planner.classificatiecorrectie cc
                WHERE cc.clubcode = @cc {whereExtra}
                ORDER BY cc.mta_inserted DESC
                LIMIT @limit";

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(RepositoryRijMapper.LeesRij(r));
        return (list.Count, limit, list);
    }

    internal static async Task<(int pending, int validated, int rejected)> GetStatsAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                SUM(CASE WHEN isgevalideerd = FALSE AND isafgewezen = FALSE THEN 1 ELSE 0 END),
                SUM(CASE WHEN isgevalideerd = TRUE THEN 1 ELSE 0 END),
                SUM(CASE WHEN isafgewezen = TRUE  THEN 1 ELSE 0 END)
            FROM planner.classificatiecorrectie
            WHERE clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (0, 0, 0);
        return (r.IsDBNull(0) ? 0 : (int)r.GetInt64(0),
                r.IsDBNull(1) ? 0 : (int)r.GetInt64(1),
                r.IsDBNull(2) ? 0 : (int)r.GetInt64(2));
    }

    internal static async Task<int> ValideerAsync(int id, bool isGevalideerd, bool isAfgewezen, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.classificatiecorrectie
            SET isgevalideerd = @isgv, isafgewezen = @isaf, mta_modified = NOW()
            WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("isgv", isGevalideerd);
        cmd.Parameters.AddWithValue("isaf", isAfgewezen);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Een leermoment door een beheerder (#1568 deel C): direct gevalideerd, herkomst <c>Admin</c>, zonder
    /// reply-paar (beide verwerkings-id's NULL). De door de beheerder geredigeerde samenvatting staat in
    /// <c>originelesamenvatting</c> — dat is het veld dat de few-shot-prompt als "Samenvatting" toont.
    /// </summary>
    internal static async Task<int> MaakAdminLeermomentAsync(AdminLeermomentOpdracht o, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO planner.classificatiecorrectie
                (origineleverwerkingid, correctionverwerkingid, origineelverzoektype, afgeleidjuisttype,
                 originelesamenvatting, isgevalideerd, clubcode, herkomst,
                 aangemaaktdoor, aangemaaktdoornaam, aangemaaktop, herkomstverwerkingid)
            VALUES (NULL, NULL, @origineel, @juist, @samenvatting, TRUE, @cc, 'Admin', @door, @naam, NOW(), @verwerking)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("origineel", o.OrigineelType);
        cmd.Parameters.AddWithValue("juist", o.JuistType);
        cmd.Parameters.AddWithValue("samenvatting", o.Samenvatting);
        cmd.Parameters.AddWithValue("cc", o.ClubCode);
        cmd.Parameters.AddWithValue("door", o.Wie.DoorId);
        cmd.Parameters.AddWithValue("naam", (object?)o.Wie.DoorNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("verwerking", (object?)o.HerkomstVerwerkingId ?? DBNull.Value);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Verwijdert uitsluitend een leermoment met herkomst <c>Admin</c> van de eigen club (AVG, #1568).</summary>
    internal static async Task<LeermomentVerwijderUitkomst> VerwijderAdminLeermomentAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using (var del = new NpgsqlCommand(
            "DELETE FROM planner.classificatiecorrectie WHERE id = @id AND clubcode = @cc AND herkomst = 'Admin'", conn))
        {
            del.Parameters.AddWithValue("id", id);
            del.Parameters.AddWithValue("cc", clubCode);
            if (await del.ExecuteNonQueryAsync() > 0) return LeermomentVerwijderUitkomst.Verwijderd;
        }
        await using var bestaat = new NpgsqlCommand(
            "SELECT 1 FROM planner.classificatiecorrectie WHERE id = @id AND clubcode = @cc", conn);
        bestaat.Parameters.AddWithValue("id", id);
        bestaat.Parameters.AddWithValue("cc", clubCode);
        return await bestaat.ExecuteScalarAsync() is null
            ? LeermomentVerwijderUitkomst.NietGevonden : LeermomentVerwijderUitkomst.GeenAdminLeermoment;
    }
}
