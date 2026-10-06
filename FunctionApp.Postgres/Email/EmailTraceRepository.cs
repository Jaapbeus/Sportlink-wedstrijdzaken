using Npgsql;
using NpgsqlTypes;
using Planner.Shared.Email.Trace;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailTraceRepository.cs</c> — data-access voor
/// <c>planner.emailtrace</c> (#1568, deel B). Zelfde stijl als <see cref="LearningMomentRepository"/>.
/// Het record is PII-arm (zie <see cref="EmailTraceRecord"/>). Geen foreign key naar
/// <c>planner.emailverwerking</c>: de trace overleeft de cleanup van de verwerking, dus de status
/// komt uit een LEFT JOIN en is <c>null</c> als de verwerking al is opgeruimd.
/// </summary>
internal static class EmailTraceRepository
{
    /// <summary>Idempotent: een retry van dezelfde verwerking vervangt de eerdere trace.</summary>
    internal static async Task UpsertAsync(string connectionString, EmailTraceRecord record)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO planner.emailtrace
                (verwerkingid, clubcode, verzoektype, zekerheid, sjabloonsleutel, tracejson, appversie)
            VALUES (@verwerkingid, @clubcode, @verzoektype, @zekerheid, @sjabloon, @tracejson, @appversie)
            ON CONFLICT (verwerkingid) DO UPDATE SET
                clubcode = EXCLUDED.clubcode,
                aangemaakt = NOW(),
                verzoektype = EXCLUDED.verzoektype,
                zekerheid = EXCLUDED.zekerheid,
                sjabloonsleutel = EXCLUDED.sjabloonsleutel,
                tracejson = EXCLUDED.tracejson,
                appversie = EXCLUDED.appversie", conn);
        cmd.Parameters.AddWithValue("verwerkingid", record.VerwerkingId);
        cmd.Parameters.AddWithValue("clubcode", record.ClubCode);
        cmd.Parameters.AddWithValue("verzoektype", record.VerzoekType);
        cmd.Parameters.AddWithValue("zekerheid", record.Zekerheid);
        cmd.Parameters.AddWithValue("sjabloon", (object?)record.SjabloonSleutel ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("tracejson", NpgsqlDbType.Jsonb) { Value = record.TraceJson });
        cmd.Parameters.AddWithValue("appversie", record.AppVersie);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Leest de trace van één verwerking van deze club; <c>null</c> als er geen is.</summary>
    internal static async Task<EmailTraceAntwoord?> HaalOpAsync(string connectionString, string clubCode, int verwerkingId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT t.verzoektype, v.status, v.ontvangstdatum, t.aangemaakt, t.zekerheid,
                   t.sjabloonsleutel, t.appversie, t.tracejson::text
            FROM planner.emailtrace t
            LEFT JOIN planner.emailverwerking v ON v.id = t.verwerkingid AND v.clubcode = t.clubcode
            WHERE t.verwerkingid = @verwerkingid AND t.clubcode = @clubcode", conn);
        cmd.Parameters.AddWithValue("verwerkingid", verwerkingId);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return EmailTraceAntwoord.VanRij(verwerkingId, r);
    }
}
