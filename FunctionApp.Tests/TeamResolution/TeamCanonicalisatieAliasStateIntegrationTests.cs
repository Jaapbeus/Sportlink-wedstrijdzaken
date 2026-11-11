using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

public sealed partial class TeamCanonicalisatieAliasStateIntegrationTests
{
    [SqlServerFact]
    public async Task RefreshAsync_RejectedSyncAlias_RemainsRejected()
    {
        var club = "Z1816" + Guid.NewGuid().ToString("N")[..8];
        var sourceKey = $"test-1816-{Guid.NewGuid():N}";
        var cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;
        try
        {
            await using (var connection = new SqlConnection(cs))
            {
                await connection.OpenAsync();
                await using var insert = new SqlCommand(@"
                    INSERT INTO [his].[teams]
                        ([bk_teams], [teamnaam], [teamsoort], [leeftijdscategorie], [ClubCode])
                    VALUES (@sourceKey, N'JO12-1', N'bond', N'JO12', @club);", connection);
                insert.Parameters.AddWithValue("@sourceKey", sourceKey);
                insert.Parameters.AddWithValue("@club", club);
                await insert.ExecuteNonQueryAsync();
            }

            await TeamCanonicalisatieService.RefreshAsync(cs, club, NullLogger.Instance);
            await using (var connection = new SqlConnection(cs))
            {
                await connection.OpenAsync();
                await using var reject = new SqlCommand(
                    "UPDATE [dbo].[TeamAliassen] SET [Status] = N'rejected' WHERE [ClubCode] = @club AND [Bron] = N'Sync';",
                    connection);
                reject.Parameters.AddWithValue("@club", club);
                await reject.ExecuteNonQueryAsync();
            }

            await TeamCanonicalisatieService.RefreshAsync(cs, club, NullLogger.Instance);

            await using var verifyConnection = new SqlConnection(cs);
            await verifyConnection.OpenAsync();
            await using var verify = new SqlCommand(
                "SELECT [Status] FROM [dbo].[TeamAliassen] WHERE [ClubCode] = @club AND [Bron] = N'Sync';",
                verifyConnection);
            verify.Parameters.AddWithValue("@club", club);
            Assert.Equal("rejected", await verify.ExecuteScalarAsync());
        }
        finally
        {
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            await using var cleanup = new SqlCommand(@"
                DELETE FROM [dbo].[TeamAliassen] WHERE [ClubCode] = @club;
                DELETE FROM [dbo].[Teams] WHERE [ClubCode] = @club;
                DELETE FROM [his].[teams] WHERE [ClubCode] = @club;", connection);
            cleanup.Parameters.AddWithValue("@club", club);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
