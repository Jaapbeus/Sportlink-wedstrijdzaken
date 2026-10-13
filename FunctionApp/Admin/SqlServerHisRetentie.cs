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
internal sealed class SqlServerHisRetentie : IHisRetentieOpslag
{
    // Gewone constructor in plaats van een primaire: de methode-heuristiek van
    // check-bestandsgrootte.sh leest een klasse met primaire constructor als één lange methode.
    private readonly string _connectionString;

    public SqlServerHisRetentie(string connectionString) => _connectionString = connectionString;

    // Welke wedstrijddatum-waarden als datum tellen, beslist HisPersoonsgegevensRetentie
    // .SelecteerTeWissenDatumdelen voor beide tiers (Codex-review ronde 2). TRY_CONVERT(date, ..., 23)
    // was ruimer dan Postgres (een lege tekst werd 1900-01-01, '2026/06/30' en '20260630' telden als
    // datum, een voorloopspatie gaf een andere datum). Hier alleen lezen en wissen, binair vergeleken:
    // onder de standaard-collation (CI, breedte-ongevoelig) zijn fullwidth-cijfers gelijk aan ASCII.
    private const string DatumDeel = "LEFT([wedstrijddatum], 10) COLLATE Latin1_General_BIN2";

    // #1715: de retentie geldt per club, met het seizoenseinde van die club. Rijen zonder clubstempel
    // horen bij de primaire club (de SyncEnabled-club met de laagste ClubCode, zoals de planner-view);
    // zonder primaire club zijn ze van niemand en blijven ze staan (veilige kant: niets wissen).
    private const string ClubFilter =
        "COALESCE([ClubCode], (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1 ORDER BY [ClubCode])) = @clubcode";

    private static readonly string MatchesDatumdelenSql =
        "IF OBJECT_ID(N'[his].[matches]', N'U') IS NOT NULL " +
        $"SELECT DISTINCT {DatumDeel} FROM [his].[matches] " +
        $"WHERE [wedstrijddatum] IS NOT NULL AND {ClubFilter} AND ({NietLeeg(HisPersoonsgegevensRetentie.MatchesKolommen)});";

    private static readonly string MatchesSql =
        "IF OBJECT_ID(N'[his].[matches]', N'U') IS NOT NULL " +
        $"UPDATE [his].[matches] SET {SetLijst(HisPersoonsgegevensRetentie.MatchesKolommen)} " +
        $"WHERE {DatumDeel} IN (SELECT [value] FROM STRING_SPLIT(@datums, N',')) AND {ClubFilter} " +
        $"AND ({NietLeeg(HisPersoonsgegevensRetentie.MatchesKolommen)});";

    private static readonly string MatchDetailsSql =
        "IF OBJECT_ID(N'[his].[matchdetails]', N'U') IS NOT NULL " +
        $"UPDATE [his].[matchdetails] SET {SetLijst(HisPersoonsgegevensRetentie.MatchDetailsKolommen)} " +
        $"WHERE COALESCE([MatchDate], CAST([MatchDateTime] AS date)) <= @grens AND {ClubFilter} " +
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

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Alle seizoenseinddatums uit <c>dbo.Season</c>, per club (#1715).</summary>
    internal static async Task<IReadOnlyList<ClubSeizoenEinde>> LeesSeizoenseindenAsync(SqlConnection connection)
    {
        var einden = new List<ClubSeizoenEinde>();
        await using var command = new SqlCommand("SELECT [ClubCode], [DateUntil] FROM [dbo].[Season] WHERE [DateUntil] IS NOT NULL", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            einden.Add(new ClubSeizoenEinde(reader.GetString(0), DateOnly.FromDateTime(reader.GetDateTime(1))));
        return einden;
    }

    /// <summary>Maakt de persoonskolommen van <paramref name="clubCode"/> leeg voor wedstrijden tot en met
    /// <paramref name="grens"/>. Geeft uitsluitend aantallen terug, nooit inhoud.</summary>
    internal static async Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(SqlConnection connection, string clubCode, DateOnly grens)
    {
        var matches = await HisPersoonsgegevensRetentie.WisMatchesAsync(
            () => LeesDatumdelenAsync(connection, clubCode),
            // Een geldig datumdeel bevat geen komma, dus STRING_SPLIT kan het niet opknippen.
            datums => VoerUitAsync(connection, MatchesSql, clubCode,
                new SqlParameter("@datums", System.Data.SqlDbType.NVarChar, -1) { Value = string.Join(',', datums) }),
            grens);
        var matchDetails = await VoerUitAsync(connection, MatchDetailsSql, clubCode,
            new SqlParameter("@grens", System.Data.SqlDbType.Date) { Value = grens.ToDateTime(TimeOnly.MinValue) });
        return (matches, matchDetails);
    }

    private static async Task<IReadOnlyList<string?>> LeesDatumdelenAsync(SqlConnection connection, string clubCode)
    {
        var delen = new List<string?>();
        await using var command = new SqlCommand(MatchesDatumdelenSql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@clubcode", clubCode);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            delen.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return delen;
    }

    private static async Task<int> VoerUitAsync(SqlConnection connection, string sql, string clubCode, SqlParameter parameter)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@clubcode", clubCode);
        command.Parameters.Add(parameter);
        // Een overgeslagen IF-tak geeft -1; dat is "niets geraakt".
        return Math.Max(0, await command.ExecuteNonQueryAsync());
    }

    private static string SetLijst(IEnumerable<string> kolommen)
        => string.Join(", ", kolommen.Select(k => $"[{k}] = NULL"));

    private static string NietLeeg(IEnumerable<string> kolommen)
        => string.Join(" OR ", kolommen.Select(k => $"[{k}] IS NOT NULL"));
}
