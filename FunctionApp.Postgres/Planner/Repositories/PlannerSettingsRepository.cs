using Npgsql;

namespace FunctionApp.Postgres.Planner;

internal sealed record Speeltijd(
    string Leeftijd, decimal Veldafmeting, int WedstrijdTotaal, TimeOnly? StandaardVoorkeurTijd);

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Planner/Repositories/PlannerSettingsRepository.cs</c> (#888). Vertaald zijn
/// <see cref="GetSpeeltijdenLookupAsync"/> (voor <c>GET /api/planner/veldbezetting</c>) en
/// <see cref="GetSeasonEndDateAsync"/> (voor <c>GET /api/planner/team-schedule</c>). De overige
/// settings-lookups (Velden, Zonsondergang, VoorkeurTijden) horen bij het auto-plan-pad en zijn
/// nog niet vertaald.
/// </summary>
internal static class PlannerSettingsRepository
{
    /// <summary>
    /// Einddatum van het laatst bekende seizoen, of <c>null</c> als <c>public.season</c> leeg is.
    /// <para>
    /// Bewust <c>null</c> bij een lege tabel en géén stille terugval hier: de aanroeper hoort te
    /// beslissen wat "geen seizoen bekend" betekent. Vergelijk <c>PostgresSeasonHelper</c>, dat op
    /// dezelfde tabel een week-offset berekent voor de synchronisatie en daar wél een
    /// gedocumenteerde default heeft — een andere vraag, dus een ander antwoord.
    /// </para>
    /// </summary>
    internal static async Task<DateOnly?> GetSeasonEndDateAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT MAX(dateuntil) FROM public.season", conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is DateTime einde ? DateOnly.FromDateTime(einde) : null;
    }

    internal static async Task<Dictionary<string, Speeltijd>> GetSpeeltijdenLookupAsync(
        string connectionString, string clubCode)
    {
        var result = new Dictionary<string, Speeltijd>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT leeftijd, veldafmeting, wedstrijdtotaal, standaardvoorkeurtijd FROM public.speeltijden WHERE clubcode = @cc",
            conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var leeftijd = reader.GetString(0);
            result[leeftijd] = new Speeltijd(
                Leeftijd: leeftijd,
                Veldafmeting: reader.GetDecimal(1),
                WedstrijdTotaal: reader.GetInt32(2),
                StandaardVoorkeurTijd: reader.IsDBNull(3) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(3)));
        }
        return result;
    }
}
