using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using SportlinkFunction;
using SportlinkFunction.Integrations.SportlinkClub;
using Xunit;

namespace FunctionApp.Tests.Integrations;

/// <summary>
/// SQL Server cache-witness coverage corresponding to the Postgres integration suite. Requires the
/// disposable SQL test database with the production cache table and synchronized <c>his.matches</c> schema.
/// </summary>
public class SqlSportlinkPublicMatchIdRepositoryIntegrationTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException($"{SqlServerFactAttribute.EnvVar} niet gezet.");

    private static string Club => "ztest" + Guid.NewGuid().ToString("N")[..8];
    private static long MatchCode => Random.Shared.NextInt64(8_000_000, 9_000_000);

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [SqlServerFact]
    public async Task LegacyWritersAndExactOpaqueIdComparison_CannotMakeUnverifiedMappingReadable()
    {
        var club = Club;
        var code = MatchCode;
        var bk = "sportlink-test-" + Guid.NewGuid().ToString("N");
        SystemUtilities.AppSettings.SetForTests("clubCode", club);
        try
        {
            using (var conn = await OpenAsync())
            using (var cmd = new SqlCommand(@"
                INSERT INTO [his].[matches] ([bk_matches], [wedstrijdcode], [wedstrijdnummer], [kaledatum], [ClubCode], [mta_inserted], [mta_modified])
                VALUES (@bk, @code, 7001, N'2026-09-05', @club, GETUTCDATE(), GETUTCDATE())", conn))
            {
                cmd.Parameters.AddWithValue("@bk", bk);
                cmd.Parameters.AddWithValue("@code", code);
                cmd.Parameters.AddWithValue("@club", club);
                await cmd.ExecuteNonQueryAsync();
            }
            using (var conn = await OpenAsync())
            using (var cmd = new SqlCommand(@"
                INSERT INTO [dbo].[SportlinkPublicMatchIdCache] ([Wedstrijdcode], [ClubCode], [PublicMatchId])
                VALUES (@code, @club, N'M-legacy')", conn))
            {
                cmd.Parameters.AddWithValue("@code", code);
                cmd.Parameters.AddWithValue("@club", club);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var conn = await OpenAsync())
                (await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(conn, code, club)).Should().BeNull();
            using (var conn = await OpenAsync())
            {
                (await SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync(conn, new[] { "M-legacy" }, club))
                    .Should().BeEmpty();
                (await SportlinkPublicMatchIdRepository.ZoekWedstrijdenZonderCacheAsync(
                    conn, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), club))
                    .Should().ContainSingle(m => m.Wedstrijdcode == code);
            }

            using (var conn = await OpenAsync())
                await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(conn, code, club, "OpaqueID");
            using (var conn = await OpenAsync())
            {
                (await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(conn, code, club)).Should().Be("OpaqueID");
                (await SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync(conn, new[] { "OpaqueID" }, club))
                    .Should().ContainKey("OpaqueID");
            }
            using (var conn = await OpenAsync())
            using (var cmd = new SqlCommand(@"
                UPDATE [dbo].[SportlinkPublicMatchIdCache] SET [PublicMatchId] = N'OpaqueID'
                WHERE [Wedstrijdcode] = @code AND [ClubCode] = @club", conn))
            {
                cmd.Parameters.AddWithValue("@code", code);
                cmd.Parameters.AddWithValue("@club", club);
                await cmd.ExecuteNonQueryAsync();
            }
            using (var conn = await OpenAsync())
                (await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(conn, code, club)).Should().Be("OpaqueID");

            // SQL Server's common case-insensitive/padded comparison must not treat a different opaque ID as verified.
            using (var conn = await OpenAsync())
            using (var cmd = new SqlCommand(@"
                UPDATE [dbo].[SportlinkPublicMatchIdCache] SET [VerifiedPublicMatchId] = N'opaqueid'
                WHERE [Wedstrijdcode] = @code AND [ClubCode] = @club", conn))
            {
                cmd.Parameters.AddWithValue("@code", code);
                cmd.Parameters.AddWithValue("@club", club);
                await cmd.ExecuteNonQueryAsync();
            }
            using (var conn = await OpenAsync())
            {
                (await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(conn, code, club)).Should().BeNull();
                (await SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync(conn, new[] { "OpaqueID" }, club))
                    .Should().BeEmpty();
            }

            using (var conn = await OpenAsync())
            using (var cmd = new SqlCommand(@"
                UPDATE [dbo].[SportlinkPublicMatchIdCache] SET [VerifiedPublicMatchId] = N'OpaqueID '
                WHERE [Wedstrijdcode] = @code AND [ClubCode] = @club", conn))
            {
                cmd.Parameters.AddWithValue("@code", code);
                cmd.Parameters.AddWithValue("@club", club);
                await cmd.ExecuteNonQueryAsync();
            }
            using (var conn = await OpenAsync())
                (await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(conn, code, club)).Should().BeNull();
        }
        finally
        {
            using var conn = await OpenAsync();
            using var cmd = new SqlCommand(@"
                DELETE FROM [dbo].[SportlinkPublicMatchIdCache] WHERE [ClubCode] = @club;
                DELETE FROM [his].[matches] WHERE [bk_matches] = @bk;", conn);
            cmd.Parameters.AddWithValue("@club", club);
            cmd.Parameters.AddWithValue("@bk", bk);
            await cmd.ExecuteNonQueryAsync();
            SystemUtilities.AppSettings.ResetForTests();
        }
    }

    [SqlServerFact]
    public async Task GelijktijdigeSchrijfacties_GevoerdMetHoldLock_BewarenEenCacheMapping()
    {
        var club = Club;
        var code = MatchCode;
        try
        {
            var writes = Enumerable.Range(0, 6).Select(async i =>
            {
                using var conn = await OpenAsync();
                await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(conn, code, club, $"M-concurrent-{i}");
            });
            await Task.WhenAll(writes);

            using var verify = await OpenAsync();
            using var count = new SqlCommand(@"SELECT COUNT(*) FROM [dbo].[SportlinkPublicMatchIdCache] WHERE [Wedstrijdcode] = @code AND [ClubCode] = @club", verify);
            count.Parameters.AddWithValue("@code", code);
            count.Parameters.AddWithValue("@club", club);
            Convert.ToInt32(await count.ExecuteScalarAsync()).Should().Be(1);
            (await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(verify, code, club)).Should().StartWith("M-concurrent-");
        }
        finally
        {
            using var conn = await OpenAsync();
            using var cmd = new SqlCommand("DELETE FROM [dbo].[SportlinkPublicMatchIdCache] WHERE [ClubCode] = @club", conn);
            cmd.Parameters.AddWithValue("@club", club);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
