using Npgsql;
using FunctionApp.Postgres.TeamResolution;
using Database.Postgres.Tests;
using Planner.Shared.TeamResolution;
using Xunit;

namespace FunctionApp.Postgres.Tests.TeamResolution;

public sealed partial class TeamFixtureContextIntegrationTests
{
    [PostgresFact]
    public async Task FixtureSourceOwnership_UniqueOwnerThenMirroredOwner_DeletedAndCancelledCannotSupport()
    {
        var cs = PostgresTestEnvironment.ConnectionStringOrNull!;
        var club = "Z1816" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = new NpgsqlConnection(cs);
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
            await Execute("UPDATE his.matches SET mta_deleted = NOW() WHERE clubcode = @club AND teamnaam = 'MO12-1'");
            await Fixture("c", "MO12-1", "aFgElAsT");
            context = await repository.GetAsync(club, new DateOnly(2026, 11, 10), [first, second]);
            Assert.Single(context.Fixtures);
            Assert.Equal(first, TeamFixtureChoice.Choose(context, first, second, null));
        }
        finally
        {
            await Execute("DELETE FROM his.matches WHERE clubcode = @club; DELETE FROM public.teamaliassen WHERE clubcode = @club; DELETE FROM public.teams WHERE clubcode = @club");
        }

        async Task<int> Team(string text)
        {
            await using var command = new NpgsqlCommand("INSERT INTO public.teams (clubcode, teamnaam, teamnaamgenormaliseerd) VALUES (@club, @name, @name) RETURNING teamid", connection);
            command.Parameters.AddWithValue("club", club);
            command.Parameters.AddWithValue("name", text);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        async Task Fixture(string key, string owner, string status = "Te spelen")
        {
            await using var command = new NpgsqlCommand(@"INSERT INTO his.matches
                (wedstrijdcode, teamnaam, thuisteam, uitteam, aanvangstijd, kaledatum, status, clubcode, mta_inserted, mta_modified)
                VALUES (@key, @owner, 'JO12-1', 'MO12-1', '10:30', '2026-11-10', @status, @club, NOW(), NOW())", connection);
            command.Parameters.AddWithValue("club", club);
            command.Parameters.AddWithValue("key", Random.Shared.NextInt64(1, 100_000_000));
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("status", status);
            await command.ExecuteNonQueryAsync();
        }
        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("club", club);
            await command.ExecuteNonQueryAsync();
        }
    }
}
