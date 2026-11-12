using Npgsql;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.TeamResolution;
using Microsoft.Extensions.Logging.Abstractions;
using Database.Postgres.Tests;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Postgres.Tests.TeamResolution;

public sealed class TeamAliasDeleteIntegrationTests
{
    [PostgresFact]
    public async Task SyncDelete_PersistsRejectionAcrossRefresh_ManualDeleteRemainsPhysical_ClubIsStrict()
    {
        var cs = Database.Postgres.Tests.PostgresTestEnvironment.ConnectionStringOrNull!;
        var club = "Z1816" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        try
        {
            await using (var seed = new NpgsqlCommand(@"INSERT INTO his.teams (teamcode, lokaleteamcode, poulecode, teamnaam, teamsoort, leeftijdscategorie, clubcode, mta_inserted, mta_modified) VALUES (@keya, @keya, @keya, 'JO12-1', 'lokaal', 'JO12', @club, NOW(), NOW()), (@keyb, @keyb, @keyb, 'O12-1', 'bond', 'JO12', @club, NOW(), NOW());", connection))
            {
                seed.Parameters.AddWithValue("club", club);
                seed.Parameters.AddWithValue("keya", Random.Shared.NextInt64(1, 100_000_000));
                seed.Parameters.AddWithValue("keyb", Random.Shared.NextInt64(1, 100_000_000));
                await seed.ExecuteNonQueryAsync();
            }
            await TeamCanonicalisatieService.RefreshAsync(cs, club, NullLogger.Instance);
            var aliasId = Convert.ToInt32(await Scalar("SELECT id FROM public.teamaliassen WHERE clubcode = @club AND ruwetekst = 'JO12-1'"));
            Assert.Equal(0, await AdminTeamAliassenRepository.DeleteAsync(aliasId, "OTHERCLUB", cs));
            Assert.Equal("validated", await Scalar($"SELECT status FROM public.teamaliassen WHERE id = {aliasId} AND clubcode = @club"));
            Assert.True(await AdminTeamAliassenRepository.DeleteAsync(aliasId, club, cs) > 0);
            await TeamCanonicalisatieService.RefreshAsync(cs, club, NullLogger.Instance);
            Assert.Equal("rejected", await Scalar($"SELECT status FROM public.teamaliassen WHERE id = {aliasId} AND clubcode = @club"));
            var teamId = Convert.ToInt32(await Scalar("SELECT teamid FROM public.teams WHERE clubcode = @club"));
            var context = await new TeamFixtureContextRepository(cs).GetAsync(club, new DateOnly(2026, 11, 10), [teamId, teamId]);
            Assert.DoesNotContain(context.Spellings, spelling => spelling.Text == "JO12-1");
            Assert.Contains(context.Spellings, spelling => spelling.Text == "O12-1");
            var manualId = Convert.ToInt32(await Scalar("INSERT INTO public.teamaliassen (clubcode, ruwetekst, ruwetekstgenormaliseerd, teamid, bron, status) SELECT @club, 'manual-name', 'MANUAL-NAME', teamid, 'CoordinatorCorrectie', 'validated' FROM public.teams WHERE clubcode = @club RETURNING id"));
            Assert.Equal(0, await AdminTeamAliassenRepository.DeleteAsync(manualId, "OTHERCLUB", cs));
            Assert.True(await AdminTeamAliassenRepository.DeleteAsync(manualId, club, cs) > 0);
            Assert.Null(await Scalar($"SELECT id FROM public.teamaliassen WHERE id = {manualId} AND clubcode = @club"));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(@"DELETE FROM public.teamaliassen WHERE clubcode = @club;
                DELETE FROM public.teams WHERE clubcode = @club; DELETE FROM his.teams WHERE clubcode = @club;", connection);
            cleanup.Parameters.AddWithValue("club", club);
            await cleanup.ExecuteNonQueryAsync();
        }

        async Task<object?> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("club", club);
            return await command.ExecuteScalarAsync();
        }
    }
}
