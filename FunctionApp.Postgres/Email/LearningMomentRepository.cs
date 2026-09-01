using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/LearningMomentRepository.cs</c> (#889) —
/// data-access voor <c>planner.classificatiecorrectie</c> (leermomenten).
/// Vertaling: <c>IF NOT EXISTS ... INSERT</c> blijft hetzelfde patroon (geen <c>ON CONFLICT</c>
/// nodig — er is geen unique constraint op het paar in de Postgres-migratie, dezelfde
/// niet-constraint-afhankelijke guard als het origineel), <c>TOP 20</c> → <c>LIMIT 20</c>.
/// </summary>
internal static class LearningMomentRepository
{
    private const int MaxSamenvattingLength = 500;
    private const int MaxLeermomentVoorbeelden = 20;

    internal static async Task InsertClassificatieCorrectieAsync(
        string connectionString,
        int origineleVerwerkingId, int correctionVerwerkingId,
        string origineelType, string? afgeleidType,
        string? originaleSamenvatting, string? correctieSamenvatting,
        string clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO planner.classificatiecorrectie
                (origineleverwerkingid, correctionverwerkingid, origineelverzoektype,
                 afgeleidjuisttype, originelesamenvatting, correctiesamenvatting, clubcode)
            SELECT @origineleid, @correctionid, @origineeltype, @afgeleidtype, @originelesamenvatting, @correctiesamenvatting, @clubcode
            WHERE NOT EXISTS (
                SELECT 1 FROM planner.classificatiecorrectie
                WHERE origineleverwerkingid = @origineleid AND correctionverwerkingid = @correctionid)", conn);
        cmd.Parameters.AddWithValue("origineleid", origineleVerwerkingId);
        cmd.Parameters.AddWithValue("correctionid", correctionVerwerkingId);
        cmd.Parameters.AddWithValue("origineeltype", origineelType);
        cmd.Parameters.AddWithValue("afgeleidtype", (object?)afgeleidType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("originelesamenvatting", (object?)Truncate(originaleSamenvatting, MaxSamenvattingLength) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("correctiesamenvatting", (object?)Truncate(correctieSamenvatting, MaxSamenvattingLength) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<List<ClassificatieCorrectieVoorbeeld>> HaalVoorbeeldenOpAsync(
        string connectionString, string clubCode, ILogger log)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                SELECT origineelverzoektype, afgeleidjuisttype,
                       originelesamenvatting, correctiesamenvatting
                FROM planner.classificatiecorrectie
                WHERE isgevalideerd = TRUE AND isafgewezen = FALSE AND clubcode = @clubcode
                ORDER BY mta_modified DESC
                LIMIT {MaxLeermomentVoorbeelden}", conn);
            cmd.Parameters.AddWithValue("clubcode", clubCode);

            var list = new List<ClassificatieCorrectieVoorbeeld>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                if (r.IsDBNull(1)) continue;
                list.Add(new ClassificatieCorrectieVoorbeeld(
                    OrigineelType: r.GetString(0),
                    JuistType: r.GetString(1),
                    OrigineleSamenvatting: r.IsDBNull(2) ? "" : r.GetString(2),
                    CorrectieSamenvatting: r.IsDBNull(3) ? "" : r.GetString(3)));
            }
            return list;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Leermomenten konden niet worden geladen — classificatie zonder few-shots");
            return new List<ClassificatieCorrectieVoorbeeld>();
        }
    }

    private static string? Truncate(string? value, int max) =>
        value == null ? null : (value.Length > max ? value[..max] : value);
}
