using System.Globalization;
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
public sealed class PostgresHisRetentie(string connectionString) : IHisRetentieOpslag
{
    // wedstrijddatum is tekst ("yyyy-MM-ddTHH:mm..."). Bewust een tekstvergelijking na een
    // vormcontrole in plaats van een cast: Postgres garandeert geen evaluatievolgorde binnen een
    // AND, dus een cast kon op een afwijkende waarde alsnog falen. Op het ISO-formaat is de
    // tekstvolgorde gelijk aan de datumvolgorde.
    private static readonly string MatchesSql =
        $"UPDATE his.\"matches\" SET {SetLijst(HisPersoonsgegevensRetentie.MatchesKolommen)} " +
        "WHERE \"wedstrijddatum\" ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}' " +
        "AND left(\"wedstrijddatum\", 10) <= @grens " +
        $"AND ({NietLeeg(HisPersoonsgegevensRetentie.MatchesKolommen)});";

    private static readonly string MatchDetailsSql =
        $"UPDATE his.\"matchdetails\" SET {SetLijst(HisPersoonsgegevensRetentie.MatchDetailsKolommen)} " +
        "WHERE COALESCE(\"matchdate\", \"matchdatetime\"::date) <= @grens " +
        $"AND ({NietLeeg(HisPersoonsgegevensRetentie.MatchDetailsKolommen)});";

    public async Task<IReadOnlyList<DateOnly>> LeesSeizoenseindenAsync()
    {
        await using var connection = await OpenAsync();
        return await LeesSeizoenseindenAsync(connection);
    }

    public async Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(DateOnly grens)
    {
        await using var connection = await OpenAsync();
        return await WisTotEnMetAsync(connection, grens);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Alle seizoenseinddatums uit <c>public.season</c>.</summary>
    public static async Task<IReadOnlyList<DateOnly>> LeesSeizoenseindenAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var einden = new List<DateOnly>();
        await using var command = new NpgsqlCommand("SELECT dateuntil FROM public.season WHERE dateuntil IS NOT NULL", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            einden.Add(reader.GetFieldValue<DateOnly>(0));
        return einden;
    }

    /// <summary>Maakt de persoonskolommen leeg voor wedstrijden tot en met <paramref name="grens"/>.
    /// Geeft uitsluitend aantallen terug, nooit inhoud.</summary>
    public static async Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(
        NpgsqlConnection connection, DateOnly grens, CancellationToken ct = default)
    {
        var matches = await VoerUitAlsTabelBestaatAsync(connection, "his.matches", MatchesSql,
            grens.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
        var matchDetails = await VoerUitAlsTabelBestaatAsync(connection, "his.matchdetails", MatchDetailsSql, grens, ct);
        return (matches, matchDetails);
    }

    private static async Task<int> VoerUitAlsTabelBestaatAsync(
        NpgsqlConnection connection, string tabel, string sql, object grens, CancellationToken ct)
    {
        await using (var bestaat = new NpgsqlCommand("SELECT to_regclass(@tabel) IS NOT NULL", connection))
        {
            bestaat.Parameters.AddWithValue("tabel", tabel);
            if (await bestaat.ExecuteScalarAsync(ct) is not true) return 0;
        }

        await using var update = new NpgsqlCommand(sql, connection);
        update.Parameters.AddWithValue("grens", grens);
        return await update.ExecuteNonQueryAsync(ct);
    }

    private static string SetLijst(IEnumerable<string> kolommen)
        => string.Join(", ", kolommen.Select(k => $"{PostgresIdentifier.Quote(k)} = NULL"));

    private static string NietLeeg(IEnumerable<string> kolommen)
        => string.Join(" OR ", kolommen.Select(k => $"{PostgresIdentifier.Quote(k)} IS NOT NULL"));
}
