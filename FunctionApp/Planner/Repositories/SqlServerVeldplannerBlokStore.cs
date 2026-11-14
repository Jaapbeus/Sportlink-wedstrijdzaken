using Microsoft.Data.SqlClient;
using Planner.Shared.Planning;

namespace SportlinkFunction.Planner;

/// <summary>
/// SQL Server-opslag van de veldplanner-blokken (#1915): <c>dbo.SportlinkVeldplannerBlok</c>. Tegenhanger van
/// <c>PostgresVeldplannerBlokStore</c>; de vergelijkingslogica staat in <see cref="VeldplannerBlokSynchronisatie"/>.
/// </summary>
internal sealed class SqlServerVeldplannerBlokStore : IVeldplannerBlokStore
{
    private readonly string _connectionString;

    public SqlServerVeldplannerBlokStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyDictionary<string, BlokOpslagStand>> LeesDagAsync(string clubCode, DateOnly datum, CancellationToken ct = default)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = new SqlCommand(
            "SELECT [PublicMatchId], [RijHash], [ExternalMatchId] FROM [dbo].[SportlinkVeldplannerBlok] WHERE [ClubCode] = @ClubCode AND [Datum] = @Datum",
            connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@Datum", datum.ToDateTime(TimeOnly.MinValue));

        var resultaat = new Dictionary<string, BlokOpslagStand>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            resultaat[reader.GetString(0)] = new BlokOpslagStand(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
        return resultaat;
    }

    public async Task SchrijfAsync(string clubCode, DateOnly datum, IReadOnlyList<VeldplannerBlokRij> rijen,
        IReadOnlyList<string> verwijderSleutels, DateOnly bewaarVanaf, CancellationToken ct = default)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        foreach (var rij in rijen)
        {
            // Een al bekend wedstrijdnummer wordt nooit overschreven met "onbekend".
            using var upsert = new SqlCommand("""
                MERGE [dbo].[SportlinkVeldplannerBlok] WITH (HOLDLOCK) AS doel
                USING (SELECT @ClubCode AS [ClubCode], @PublicMatchId AS [PublicMatchId]) AS bron
                   ON doel.[ClubCode] = bron.[ClubCode] AND doel.[PublicMatchId] = bron.[PublicMatchId]
                WHEN MATCHED THEN UPDATE SET
                    [Datum] = @Datum, [Label] = @Label, [Veld] = @Veld, [StartTijd] = @StartTijd,
                    [Veldafmeting] = @Veldafmeting, [DuurMinuten] = @DuurMinuten,
                    [ExternalMatchId] = COALESCE(@ExternalMatchId, doel.[ExternalMatchId]),
                    [RijHash] = @RijHash, [BijgewerktOp] = GETUTCDATE()
                WHEN NOT MATCHED THEN INSERT
                    ([ClubCode], [PublicMatchId], [Datum], [Label], [Veld], [StartTijd], [Veldafmeting], [DuurMinuten], [ExternalMatchId], [RijHash], [BijgewerktOp])
                    VALUES (@ClubCode, @PublicMatchId, @Datum, @Label, @Veld, @StartTijd, @Veldafmeting, @DuurMinuten, @ExternalMatchId, @RijHash, GETUTCDATE());
                """, connection, transaction);
            upsert.Parameters.AddWithValue("@ClubCode", rij.ClubCode);
            upsert.Parameters.AddWithValue("@PublicMatchId", rij.PublicMatchId);
            upsert.Parameters.AddWithValue("@Datum", rij.Datum.ToDateTime(TimeOnly.MinValue));
            upsert.Parameters.AddWithValue("@Label", rij.Label);
            upsert.Parameters.AddWithValue("@Veld", rij.Veld);
            upsert.Parameters.AddWithValue("@StartTijd", rij.StartTijd);
            upsert.Parameters.AddWithValue("@Veldafmeting", rij.Veldafmeting);
            upsert.Parameters.AddWithValue("@DuurMinuten", rij.DuurMinuten);
            upsert.Parameters.AddWithValue("@ExternalMatchId", (object?)rij.ExternalMatchId ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@RijHash", rij.RijHash);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        foreach (var sleutel in verwijderSleutels)
        {
            using var verwijder = new SqlCommand(
                "DELETE FROM [dbo].[SportlinkVeldplannerBlok] WHERE [ClubCode] = @ClubCode AND [Datum] = @Datum AND [PublicMatchId] = @PublicMatchId",
                connection, transaction);
            verwijder.Parameters.AddWithValue("@ClubCode", clubCode);
            verwijder.Parameters.AddWithValue("@Datum", datum.ToDateTime(TimeOnly.MinValue));
            verwijder.Parameters.AddWithValue("@PublicMatchId", sleutel);
            await verwijder.ExecuteNonQueryAsync(ct);
        }

        using var opruim = new SqlCommand(
            "DELETE FROM [dbo].[SportlinkVeldplannerBlok] WHERE [ClubCode] = @ClubCode AND [Datum] < @Vanaf", connection, transaction);
        opruim.Parameters.AddWithValue("@ClubCode", clubCode);
        opruim.Parameters.AddWithValue("@Vanaf", bewaarVanaf.ToDateTime(TimeOnly.MinValue));
        await opruim.ExecuteNonQueryAsync(ct);

        transaction.Commit();
    }
}
