using Npgsql;
using Planner.Shared.Avg;

namespace Database.Postgres;

/// <summary>
/// Postgres-query's voor de seizoensretentie van de persoonskolommen in de historietabellen (#1698).
/// De regel en de kolomlijsten staan in <see cref="HisPersoonsgegevensRetentie"/>; hier alleen het
/// lezen van de seizoenseinden en de <c>UPDATE</c>. SQL Server-tegenhanger:
/// <c>FunctionApp/Admin/SqlServerHisRetentie.cs</c>.
/// <para>
/// Idempotent: alleen rijen met nog minstens één gevulde persoonskolom worden geraakt, dus een
/// tweede run telt nul. Een historietabel die nog niet bestaat (verse installatie vóór de eerste
/// sync) wordt overgeslagen.
/// </para>
/// <para>
/// Als <see cref="IHisRetentieOpslag"/> opent elke aanroep een eigen verbinding; de dagelijkse
/// AVG-retentierun (<c>CleanupFeedbackFunction</c>) geeft alleen de connectiestring mee.
/// </para>
/// </summary>
public sealed class PostgresHisRetentie : IHisRetentieOpslag
{
    // Gewone constructor in plaats van een primaire: de methode-heuristiek van
    // check-bestandsgrootte.sh leest een klasse met primaire constructor als één lange methode.
    private readonly string _connectionString;

    public PostgresHisRetentie(string connectionString) => _connectionString = connectionString;

    // wedstrijddatum is tekst ("yyyy-MM-ddTHH:mm..."). Welke waarden als datum tellen, beslist
    // HisPersoonsgegevensRetentie.SelecteerTeWissenDatumdelen voor beide tiers (Codex-review ronde 2):
    // de database levert alleen de verschillende eerste tien tekens en wist daarna op binaire
    // gelijkheid (een deterministische collation vergelijkt bytes). Geen cast of regex hier, dus geen
    // tweede definitie die van SQL Server kan afwijken.
    private const string DatumDeel = "left(\"wedstrijddatum\", 10)";

    // #1715: de retentie geldt per club, met het seizoenseinde van die club. Rijen zonder clubstempel
    // horen bij de primaire club (de syncenabled-club met de laagste clubcode, zoals de planner-view);
    // zonder primaire club zijn ze van niemand en blijven ze staan (veilige kant: niets wissen).
    private const string ClubFilter =
        "COALESCE(\"clubcode\", (SELECT clubcode FROM public.appsettings WHERE syncenabled = true ORDER BY clubcode LIMIT 1)) = @clubcode";

    private static readonly string MatchesDatumdelenSql =
        $"SELECT DISTINCT {DatumDeel} FROM his.\"matches\" " +
        $"WHERE \"wedstrijddatum\" IS NOT NULL AND {ClubFilter} AND ({NietLeeg(HisPersoonsgegevensRetentie.MatchesKolommen)});";

    private static readonly string MatchesSql =
        $"UPDATE his.\"matches\" SET {SetLijst(HisPersoonsgegevensRetentie.MatchesKolommen)} " +
        $"WHERE {DatumDeel} = ANY(@datums) AND {ClubFilter} " +
        $"AND ({NietLeeg(HisPersoonsgegevensRetentie.MatchesKolommen)});";

    private static readonly string MatchDetailsSql =
        $"UPDATE his.\"matchdetails\" SET {SetLijst(HisPersoonsgegevensRetentie.MatchDetailsKolommen)} " +
        $"WHERE COALESCE(\"matchdate\", \"matchdatetime\"::date) <= @grens AND {ClubFilter} " +
        $"AND ({NietLeeg(HisPersoonsgegevensRetentie.MatchDetailsKolommen)});";

    public async Task<IReadOnlyList<ClubSeizoenEinde>> LeesSeizoenseindenAsync()
    {
        await using var connection = await OpenAsync();
        return await LeesSeizoenseindenAsync(connection);
    }

    public async Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(string clubCode, DateOnly grens)
    {
        await using var connection = await OpenAsync();
        return await WisTotEnMetAsync(connection, clubCode, grens);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Alle seizoenseinddatums uit <c>public.clubseason</c>, per club (#1715).</summary>
    public static async Task<IReadOnlyList<ClubSeizoenEinde>> LeesSeizoenseindenAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var einden = new List<ClubSeizoenEinde>();
        await using var command = new NpgsqlCommand("SELECT clubcode, dateuntil FROM public.clubseason WHERE dateuntil IS NOT NULL", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            einden.Add(new ClubSeizoenEinde(reader.GetString(0), reader.GetFieldValue<DateOnly>(1)));
        return einden;
    }

    /// <summary>Maakt de persoonskolommen van <paramref name="clubCode"/> leeg voor wedstrijden tot en met
    /// <paramref name="grens"/>. Geeft uitsluitend aantallen terug, nooit inhoud.</summary>
    public static async Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(
        NpgsqlConnection connection, string clubCode, DateOnly grens, CancellationToken ct = default)
    {
        var matches = await AlsTabelBestaatAsync(connection, "his.matches", () => HisPersoonsgegevensRetentie.WisMatchesAsync(
            () => LeesDatumdelenAsync(connection, clubCode, ct),
            datums => VoerUitAsync(connection, MatchesSql, clubCode, "datums", datums.ToArray(), ct),
            grens), ct);
        var matchDetails = await AlsTabelBestaatAsync(connection, "his.matchdetails",
            () => VoerUitAsync(connection, MatchDetailsSql, clubCode, "grens", grens, ct), ct);
        return (matches, matchDetails);
    }

    private static async Task<IReadOnlyList<string?>> LeesDatumdelenAsync(NpgsqlConnection connection, string clubCode, CancellationToken ct)
    {
        var delen = new List<string?>();
        await using var command = new NpgsqlCommand(MatchesDatumdelenSql, connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            delen.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return delen;
    }

    private static async Task<int> AlsTabelBestaatAsync(
        NpgsqlConnection connection, string tabel, Func<Task<int>> stap, CancellationToken ct)
    {
        await using (var bestaat = new NpgsqlCommand("SELECT to_regclass(@tabel) IS NOT NULL", connection))
        {
            bestaat.Parameters.AddWithValue("tabel", tabel);
            if (await bestaat.ExecuteScalarAsync(ct) is not true) return 0;
        }
        return await stap();
    }

    private static async Task<int> VoerUitAsync(
        NpgsqlConnection connection, string sql, string clubCode, string parameter, object waarde, CancellationToken ct)
    {
        await using var update = new NpgsqlCommand(sql, connection);
        update.Parameters.AddWithValue("clubcode", clubCode);
        update.Parameters.AddWithValue(parameter, waarde);
        return await update.ExecuteNonQueryAsync(ct);
    }

    private static string SetLijst(IEnumerable<string> kolommen)
        => string.Join(", ", kolommen.Select(k => $"{PostgresIdentifier.Quote(k)} = NULL"));

    private static string NietLeeg(IEnumerable<string> kolommen)
        => string.Join(" OR ", kolommen.Select(k => $"{PostgresIdentifier.Quote(k)} IS NOT NULL"));
}
