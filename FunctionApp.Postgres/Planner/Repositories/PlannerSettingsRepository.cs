using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Planner/Repositories/PlannerSettingsRepository.cs</c> (#888). Vertaald zijn
/// <see cref="GetSpeeltijdenLookupAsync"/> (voor <c>GET /api/planner/veldbezetting</c>),
/// <see cref="GetSeasonEndDateAsync"/> (voor <c>GET /api/planner/team-schedule</c>) en, sinds de
/// verhuizing van de scheduling-engine naar <c>Planner.Shared</c> (#888, §38),
/// <see cref="GetVeldenAsync"/>.
/// <para>
/// <b>Gebruikt <see cref="Planner.Shared.Speeltijd"/>, geen eigen lokaal record.</b> Deze klasse
/// had eerder een eigen <c>internal sealed record Speeltijd</c> met exact dezelfde vorm — een
/// duplicaat dat overbodig werd zodra de gedeelde <see cref="Planner.Shared.Speeltijd"/>-klasse er
/// al was (#888). Verwijderd in plaats van naast elkaar te laten bestaan.
/// </para>
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
            result[leeftijd] = new Speeltijd
            {
                Leeftijd = leeftijd,
                Veldafmeting = reader.GetDecimal(1),
                WedstrijdTotaal = reader.GetInt32(2),
                StandaardVoorkeurTijd = reader.IsDBNull(3) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(3))
            };
        }
        return result;
    }

    /// <summary>
    /// Actieve velden van een club, met veldtype/kunstlicht — de invoer die de scheduling-engine
    /// (<see cref="Planner.Shared.FieldScheduler"/>) nodig heeft voor de kunstgras-voorkeursvolgorde
    /// en waarop <see cref="Planner.Shared.VeldTypeClassificatie"/> werkt.
    /// </summary>
    internal static async Task<List<VeldInfo>> GetVeldenAsync(string connectionString, string clubCode)
    {
        var result = new List<VeldInfo>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT veldnummer, veldnaam, veldtype, heeftkunstlicht FROM public.velden WHERE clubcode = @cc AND actief = true ORDER BY veldnummer",
            conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new VeldInfo
            {
                VeldNummer = reader.GetInt32(0),
                VeldNaam = reader.GetString(1),
                VeldType = reader.IsDBNull(2) ? "kunstgras" : reader.GetString(2),
                HeeftKunstlicht = reader.GetBoolean(3)
            });
        return result;
    }
}
