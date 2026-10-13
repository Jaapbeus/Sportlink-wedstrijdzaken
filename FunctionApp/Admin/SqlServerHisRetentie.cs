using Microsoft.Data.SqlClient;
using Planner.Shared.Avg;

namespace SportlinkFunction.Admin;

/// <summary>
/// SQL Server-query's voor de seizoensretentie van de persoonskolommen in de historietabellen
/// (#1698). Regel en kolomlijsten: <see cref="HisPersoonsgegevensRetentie"/>. Postgres-tegenhanger:
/// <c>Database.Postgres/PostgresHisRetentie.cs</c>. Kolomnamen komen uit de gedeelde lijst
/// (lowercase); de database is case-insensitief (<c>ModelCollation 1033, CI</c>), zoals de
/// bestaande ETL-query's al aannemen.
/// <para>
/// Idempotent (alleen rijen met nog een gevulde persoonskolom) en een nog niet bestaande
/// historietabel wordt overgeslagen: die maakt <c>sp_CreateTargetTableFromSource</c> pas bij de
/// eerste sync aan.
/// </para>
/// <para>
/// Als <see cref="IHisRetentieOpslag"/> opent elke aanroep een eigen verbinding; de dagelijkse
/// AVG-retentierun (<c>CleanupFeedbackFunction</c>) geeft alleen de connectiestring mee.
/// </para>
/// </summary>
internal sealed class SqlServerHisRetentie(string connectionString) : IHisRetentieOpslag
{
    private static readonly string MatchesSql =
        "IF OBJECT_ID(N'[his].[matches]', N'U') IS NOT NULL " +
        $"UPDATE [his].[matches] SET {SetLijst(HisPersoonsgegevensRetentie.MatchesKolommen)} " +
        "WHERE TRY_CONVERT(date, LEFT([wedstrijddatum], 10), 23) <= @grens " +
        $"AND ({NietLeeg(HisPersoonsgegevensRetentie.MatchesKolommen)});";

    private static readonly string MatchDetailsSql =
        "IF OBJECT_ID(N'[his].[matchdetails]', N'U') IS NOT NULL " +
        $"UPDATE [his].[matchdetails] SET {SetLijst(HisPersoonsgegevensRetentie.MatchDetailsKolommen)} " +
        "WHERE COALESCE([MatchDate], CAST([MatchDateTime] AS date)) <= @grens " +
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

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Alle seizoenseinddatums uit <c>dbo.Season</c>.</summary>
    internal static async Task<IReadOnlyList<DateOnly>> LeesSeizoenseindenAsync(SqlConnection connection)
    {
        var einden = new List<DateOnly>();
        await using var command = new SqlCommand("SELECT [DateUntil] FROM [dbo].[Season] WHERE [DateUntil] IS NOT NULL", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            einden.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
        return einden;
    }

    /// <summary>Maakt de persoonskolommen leeg voor wedstrijden tot en met <paramref name="grens"/>.
    /// Geeft uitsluitend aantallen terug, nooit inhoud.</summary>
    internal static async Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(SqlConnection connection, DateOnly grens)
        => (await VoerUitAsync(connection, MatchesSql, grens), await VoerUitAsync(connection, MatchDetailsSql, grens));

    private static async Task<int> VoerUitAsync(SqlConnection connection, string sql, DateOnly grens)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.Add(new SqlParameter("@grens", System.Data.SqlDbType.Date) { Value = grens.ToDateTime(TimeOnly.MinValue) });
        // Een overgeslagen IF-tak geeft -1; dat is "niets geraakt".
        return Math.Max(0, await command.ExecuteNonQueryAsync());
    }

    private static string SetLijst(IEnumerable<string> kolommen)
        => string.Join(", ", kolommen.Select(k => $"[{k}] = NULL"));

    private static string NietLeeg(IEnumerable<string> kolommen)
        => string.Join(" OR ", kolommen.Select(k => $"[{k}] IS NOT NULL"));
}
