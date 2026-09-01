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
    /// Eén speeltijd-rij opzoeken — dunne wrapper om <see cref="GetSpeeltijdenLookupAsync"/> heen
    /// i.p.v. een eigen query: zelfde resultaat, geen tweede SQL-implementatie van dezelfde lookup.
    /// </summary>
    internal static async Task<Speeltijd?> GetSpeeltijdAsync(
        string connectionString, string leeftijdsCategorie, string clubCode)
    {
        var lookup = await GetSpeeltijdenLookupAsync(connectionString, clubCode);
        return lookup.TryGetValue(leeftijdsCategorie, out var speeltijd) ? speeltijd : null;
    }

    /// <summary>
    /// Postgres-vertaling van het SQL Server-origineel (issue 888 vervolg, §41) — leest
    /// <c>public.zonsondergang</c>, de tegenhanger van <c>dbo.Zonsondergang</c> (migratie 011).
    /// </summary>
    internal static async Task<TimeOnly?> GetSunsetAsync(string connectionString, DateOnly date)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT zonsondergang FROM public.zonsondergang WHERE datum = @datum", conn);
        cmd.Parameters.AddWithValue("datum", date.ToDateTime(TimeOnly.MinValue).Date);
        var result = await cmd.ExecuteScalarAsync();
        return result is TimeSpan ts ? TimeOnly.FromTimeSpan(ts) : null;
    }

    /// <summary>
    /// Zonsondergang opzoeken (met terugval op <see cref="PostgresSunsetCalculator"/>) en meteen
    /// <c>BeschikbaarTot</c> van elk veld met <c>GebruikZonsondergang</c> afknijpen tot die tijd —
    /// gedeelde stap van <c>CheckAvailabilityAsync</c>, <c>CheckDoordeweeksBeschikbaarAsync</c> en
    /// <c>RescheduleService.CheckRescheduleAvailabilityAsync</c>.
    /// </summary>
    internal static async Task<TimeOnly?> ResolveEnPasZonsondergangToeAsync(
        string connectionString, DateOnly date, List<VeldBeschikbaarheidInfo> availableFields)
    {
        TimeOnly? sunset = await GetSunsetAsync(connectionString, date);
        sunset ??= PostgresSunsetCalculator.GetSunset(date);
        foreach (var field in availableFields)
            if (field.GebruikZonsondergang && sunset.HasValue && sunset.Value < field.BeschikbaarTot)
                field.BeschikbaarTot = sunset.Value;
        return sunset;
    }

    /// <inheritdoc cref="GetSunsetAsync"/>
    internal static async Task PopulateSunsetTableAsync(string connectionString, DateOnly from, DateOnly to)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var sunset = PostgresSunsetCalculator.GetSunset(date);
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO public.zonsondergang (datum, zonsondergang)
                VALUES (@datum, @sunset)
                ON CONFLICT (datum) DO UPDATE SET zonsondergang = EXCLUDED.zonsondergang
            ", conn);
            cmd.Parameters.AddWithValue("datum", date.ToDateTime(TimeOnly.MinValue).Date);
            cmd.Parameters.AddWithValue("sunset", sunset.ToTimeSpan());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Voorkeurstijden per team voor één speeldag (issue 888 vervolg, §42) — laag 1 van AutoPlan's
    /// planningsrangorde (#666). Postgres-vertaling van het gelijknamige SQL Server-origineel.
    /// <para>
    /// De sleutelvergelijking is <see cref="StringComparer.OrdinalIgnoreCase"/>, net als het
    /// origineel: de teamnaam komt uit een handmatig ingevulde beheertabel en hoeft qua kast niet
    /// exact overeen te komen met de naam in de wedstrijdbron.
    /// </para>
    /// </summary>
    internal static async Task<Dictionary<string, List<(TimeOnly Tijd, int Prioriteit)>>> GetVoorkeurTijdenLookupAsync(
        string connectionString, int dagVanWeek, string clubCode)
    {
        var result = new Dictionary<string, List<(TimeOnly, int)>>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT teamnaam, voorkeurtijd, prioriteit
            FROM public.teamvoorkeurtijden
            WHERE dagvanweek = @dag AND actief = true AND clubcode = @clubcode
            ORDER BY teamnaam, prioriteit
            """, conn);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var team = reader.GetString(0);
            var tijd = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1));
            var prio = reader.GetInt32(2);
            if (!result.TryGetValue(team, out var lijst))
            {
                lijst = new List<(TimeOnly, int)>();
                result[team] = lijst;
            }
            lijst.Add((tijd, prio));
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
