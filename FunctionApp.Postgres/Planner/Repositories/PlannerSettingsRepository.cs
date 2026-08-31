using Npgsql;

namespace FunctionApp.Postgres.Planner;

internal sealed record Speeltijd(
    string Leeftijd, decimal Veldafmeting, int WedstrijdTotaal, TimeOnly? StandaardVoorkeurTijd);

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Planner/Repositories/PlannerSettingsRepository.cs</c> (#888). Alleen
/// <see cref="GetSpeeltijdenLookupAsync"/> is vertaald — nodig voor het
/// <c>GET /api/planner/veldbezetting</c>-endpoint. De overige settings-lookups (Velden,
/// Zonsondergang, Seizoen, VoorkeurTijden) horen bij het auto-plan-pad en zijn nog niet vertaald.
/// </summary>
internal static class PlannerSettingsRepository
{
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
