using FunctionApp.Postgres.Admin;
using Database.Postgres.Tests;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

public sealed class AdminEmailLogStatusCountsIntegrationTests
{
    [PostgresFact]
    public async Task GetStatusCountsAsync_CountsAllMatchingRowsBeyondPageLimitAndHonorsFilters()
    {
        var club = "Z1819" + Guid.NewGuid().ToString("N")[..10];
        var otherClub = club + "X";
        var cs = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION_STRING")!;
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
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            for (var i = 0; i < count; i++)
            {
                await using var command = new NpgsqlCommand(@"
                    INSERT INTO planner.emailverwerking
                        (messageid, afzender, onderwerp, ontvangstdatum, verzoektype, status, clubcode)
                    VALUES (@messageid, @afzender, @onderwerp, @ontvangstdatum, @verzoektype, @status, @clubcode)", connection);
                command.Parameters.AddWithValue("messageid", $"test-1819-{Guid.NewGuid():N}");
                command.Parameters.AddWithValue("afzender", "trainer@voorbeeld.nl");
                command.Parameters.AddWithValue("onderwerp", "synthetic integration row");
                command.Parameters.AddWithValue("ontvangstdatum", received);
                command.Parameters.AddWithValue("verzoektype", "Onbekend");
                command.Parameters.AddWithValue("status", status);
                command.Parameters.AddWithValue("clubcode", clubCode);
                await command.ExecuteNonQueryAsync();
            }
        }

        static async Task DeleteClubAsync(string clubCode, string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("DELETE FROM planner.emailverwerking WHERE clubcode = @clubcode", connection);
            command.Parameters.AddWithValue("clubcode", clubCode);
            await command.ExecuteNonQueryAsync();
        }
    }
}
