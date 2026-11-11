using Microsoft.Data.SqlClient;
using FunctionApp.Tests.Feedback;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

public sealed class AdminEmailLogStatusCountsIntegrationTests
{
    [SqlServerFact]
    public async Task GetStatusCountsAsync_CountsAllMatchingRowsBeyondPageLimitAndHonorsFilters()
    {
        var club = "Z1819" + Guid.NewGuid().ToString("N")[..10];
        var otherClub = club + "X";
        var cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;
        var now = DateTime.UtcNow;
        try
        {
            await InsertRowsAsync(club, "Fout", now, 201);
            await InsertRowsAsync(club, "Fout", now.AddHours(-25), 1);
            await InsertRowsAsync(club, "Review", now, 1);
            await InsertRowsAsync(otherClub, "Fout", now, 1);

            var counts = await AdminEmailLogRepository.GetStatusCountsAsync(club, now.AddHours(-24), null, null, cs);
            var filtered = await AdminEmailLogRepository.GetStatusCountsAsync(club, now.AddHours(-24), null, "Fout", cs);
            var items = await AdminEmailLogRepository.GetAsync(club, now.AddHours(-24), null, null, 200, cs);

            Assert.Equal(201, counts["Fout"]);
            Assert.Equal(1, counts["Review"]);
            Assert.Equal(201, filtered["Fout"]);
            Assert.Single(filtered);
            Assert.Equal(200, items.Count);
        }
        finally
        {
            await DeleteClubAsync(club, cs);
            await DeleteClubAsync(otherClub, cs);
        }

        async Task InsertRowsAsync(string clubCode, string status, DateTime received, int count)
        {
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            for (var i = 0; i < count; i++)
            {
                await using var command = new SqlCommand(@"
                    INSERT INTO [planner].[EmailVerwerking]
                        ([MessageId], [Afzender], [Onderwerp], [OntvangstDatum], [VerzoekType], [Status], [ClubCode])
                    VALUES (@MessageId, @Afzender, @Onderwerp, @OntvangstDatum, @VerzoekType, @Status, @ClubCode)", connection);
                command.Parameters.AddWithValue("@MessageId", $"test-1819-{Guid.NewGuid():N}");
                command.Parameters.AddWithValue("@Afzender", "trainer@voorbeeld.nl");
                command.Parameters.AddWithValue("@Onderwerp", "synthetic integration row");
                command.Parameters.AddWithValue("@OntvangstDatum", received);
                command.Parameters.AddWithValue("@VerzoekType", "Onbekend");
                command.Parameters.AddWithValue("@Status", status);
                command.Parameters.AddWithValue("@ClubCode", clubCode);
                await command.ExecuteNonQueryAsync();
            }
        }

        static async Task DeleteClubAsync(string clubCode, string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("DELETE FROM [planner].[EmailVerwerking] WHERE [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            await command.ExecuteNonQueryAsync();
        }
    }
}
