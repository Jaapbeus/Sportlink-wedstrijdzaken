using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/AdminEmailLogRepository.cs</c>
/// (#887). Vertaling: <c>[planner].[EmailVerwerking]</c> → <c>planner.emailverwerking</c>,
/// <c>TOP (@Limit)</c> → <c>LIMIT @limit</c>, gequote PascalCase-aliassen (#855). Geen
/// <c>DateTime.SpecifyKind</c> nodig — Npgsql geeft <c>TIMESTAMPTZ</c> al terug met <c>Kind=Utc</c>.
/// AVG-maskering van de afzender is ongewijzigd.
/// </summary>
internal static class AdminEmailLogRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAsync(
        string clubCode, DateTime? vanaf, DateTime? tot, string? statusFilter, int limit, string cs)
    {
        var sql = @"SELECT id AS ""Id"", messageid AS ""MessageId"", conversationid AS ""ConversationId"",
                           afzender AS ""Afzender"", onderwerp AS ""Onderwerp"",
                           ontvangstdatum AS ""OntvangstDatum"", verzoektype AS ""VerzoekType"",
                           status AS ""Status"", verstuurdnaar AS ""VerstuurdNaar"",
                           mta_inserted, mta_modified
                    FROM planner.emailverwerking
                    WHERE clubcode = @cc";
        if (vanaf.HasValue) sql += " AND ontvangstdatum >= @vanaf";
        if (tot.HasValue)   sql += " AND ontvangstdatum < @tot";
        if (!string.IsNullOrWhiteSpace(statusFilter)) sql += " AND status = @status";
        sql += " ORDER BY ontvangstdatum DESC LIMIT @limit";

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("cc", clubCode);
        if (vanaf.HasValue) cmd.Parameters.AddWithValue("vanaf", vanaf.Value);
        if (tot.HasValue)   cmd.Parameters.AddWithValue("tot", tot.Value);
        if (!string.IsNullOrWhiteSpace(statusFilter))
            cmd.Parameters.AddWithValue("status", statusFilter);

        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);

            if (row.TryGetValue("Afzender", out var afz) && afz is string email)
            {
                var at = email.IndexOf('@');
                row["Afzender"] = at > 0 ? "***" + email[at..] : "***";
            }
            list.Add(row);
        }
        return list;
    }
}
