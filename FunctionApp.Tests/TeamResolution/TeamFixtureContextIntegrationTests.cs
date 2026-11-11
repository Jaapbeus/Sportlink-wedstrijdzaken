using Microsoft.Data.SqlClient;
using SportlinkFunction.TeamResolution;
using FunctionApp.Tests.Feedback;
using Planner.Shared.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

public sealed partial class TeamCanonicalisatieAliasStateIntegrationTests
{
    [SqlServerFact]
    public async Task FixtureSourceOwnership_UniqueOwnerThenMirroredOwner_DeletedAndCancelledCannotSupport()
    {
        var cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;
        var club = "Z1816" + Guid.NewGuid().ToString("N")[..8];
        var prefix = "fixture-" + Guid.NewGuid().ToString("N");
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        try
        {
            var first = await Team("JO12-1");
            var second = await Team("MO12-1");
            await Fixture("a", "JO12-1");
            var repository = new TeamFixtureContextRepository(cs);
            var context = await repository.GetAsync(club, new DateOnly(2026, 11, 10), [first, second]);
            Assert.Equal(first, TeamFixtureChoice.Choose(context, first, second, "10:30"));
            Assert.Empty((await repository.GetAsync("OTHERCLUB", new DateOnly(2026, 11, 10), [first, second])).Fixtures);
            Assert.Empty((await repository.GetAsync(club, new DateOnly(2026, 11, 11), [first, second])).Fixtures);
            await Fixture("b", "MO12-1");
            context = await repository.GetAsync(club, new DateOnly(2026, 11, 10), [first, second]);
            Assert.Null(TeamFixtureChoice.Choose(context, first, second, null));
            await Execute("UPDATE [his].[matches] SET mta_deleted = GETUTCDATE() WHERE [ClubCode] = @club AND teamnaam = 'MO12-1'");
            await Fixture("c", "MO12-1", "aFgElAsT");
            context = await repository.GetAsync(club, new DateOnly(2026, 11, 10), [first, second]);
            Assert.Single(context.Fixtures);
            Assert.Equal(first, TeamFixtureChoice.Choose(context, first, second, null));
        }
        finally
        {
            await Execute("DELETE FROM [his].[matches] WHERE [ClubCode] = @club; DELETE FROM [dbo].[TeamAliassen] WHERE [ClubCode] = @club; DELETE FROM [dbo].[Teams] WHERE [ClubCode] = @club");
        }

        async Task<int> Team(string text)
        {
            await using var command = new SqlCommand("INSERT INTO [dbo].[Teams] ([ClubCode], [Teamnaam], [TeamnaamGenormaliseerd]) OUTPUT INSERTED.[TeamId] VALUES (@club, @name, @name)", connection);
            command.Parameters.AddWithValue("club", club);
            command.Parameters.AddWithValue("name", text);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        async Task Fixture(string key, string owner, string status = "Te spelen")
        {
            await using var command = new SqlCommand(@"INSERT INTO [his].[matches]
                (bk_matches, teamnaam, thuisteam, uitteam, aanvangstijd, kaledatum, status, [ClubCode])
                VALUES (@key, @owner, 'JO12-1', 'MO12-1', '10:30', '2026-11-10', @status, @club)", connection);
            command.Parameters.AddWithValue("club", club);
            command.Parameters.AddWithValue("key", prefix + key);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("status", status);
            await command.ExecuteNonQueryAsync();
        }
        async Task Execute(string sql)
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("club", club);
            await command.ExecuteNonQueryAsync();
        }
    }
}
