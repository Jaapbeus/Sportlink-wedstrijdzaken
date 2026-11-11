using Microsoft.Data.SqlClient;
using SportlinkFunction.Admin;
using SportlinkFunction.TeamResolution;
using Microsoft.Extensions.Logging.Abstractions;
using FunctionApp.Tests.Feedback;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

public sealed partial class TeamCanonicalisatieAliasStateIntegrationTests
{
    [SqlServerFact]
    public async Task SyncDelete_PersistsRejectionAcrossRefresh_ManualDeleteRemainsPhysical_ClubIsStrict()
    {
        var cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;
        var club = "Z1816" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        try
        {
            await using (var seed = new SqlCommand(@"INSERT INTO [his].[teams] (bk_teams, teamnaam, teamsoort, leeftijdscategorie, [ClubCode]) VALUES (@keya, 'JO12-1', 'lokaal', 'JO12', @club), (@keyb, 'O12-1', 'bond', 'JO12', @club);", connection))
            {
                seed.Parameters.AddWithValue("club", club);
                seed.Parameters.AddWithValue("keya", "fixture-" + Guid.NewGuid().ToString("N"));
                seed.Parameters.AddWithValue("keyb", "fixture-" + Guid.NewGuid().ToString("N"));
                await seed.ExecuteNonQueryAsync();
            }
            await TeamCanonicalisatieService.RefreshAsync(cs, club, NullLogger.Instance);
            var aliasId = Convert.ToInt32(await Scalar("SELECT [Id] FROM [dbo].[TeamAliassen] WHERE [ClubCode] = @club AND [RuweTekst] = 'JO12-1'"));
            Assert.Equal(0, await AdminTeamAliassenRepository.DeleteAsync(aliasId, "OTHERCLUB", cs));
            Assert.Equal("validated", await Scalar($"SELECT [Status] FROM [dbo].[TeamAliassen] WHERE [Id] = {aliasId} AND [ClubCode] = @club"));
            Assert.True(await AdminTeamAliassenRepository.DeleteAsync(aliasId, club, cs) > 0);
            await TeamCanonicalisatieService.RefreshAsync(cs, club, NullLogger.Instance);
            Assert.Equal("rejected", await Scalar($"SELECT [Status] FROM [dbo].[TeamAliassen] WHERE [Id] = {aliasId} AND [ClubCode] = @club"));
            var teamId = Convert.ToInt32(await Scalar("SELECT [TeamId] FROM [dbo].[Teams] WHERE [ClubCode] = @club"));
            var context = await new TeamFixtureContextRepository(cs).GetAsync(club, new DateOnly(2026, 11, 10), [teamId, teamId]);
            Assert.DoesNotContain(context.Spellings, spelling => spelling.Text == "JO12-1");
            Assert.Contains(context.Spellings, spelling => spelling.Text == "O12-1");
            var manualId = Convert.ToInt32(await Scalar("INSERT INTO [dbo].[TeamAliassen] ([ClubCode], [RuweTekst], [RuweTekstGenormaliseerd], [TeamId], [Bron], [Status]) OUTPUT INSERTED.[Id] SELECT @club, 'manual-name', 'MANUAL-NAME', [TeamId], 'CoordinatorCorrectie', 'validated' FROM [dbo].[Teams] WHERE [ClubCode] = @club "));
            Assert.Equal(0, await AdminTeamAliassenRepository.DeleteAsync(manualId, "OTHERCLUB", cs));
            Assert.True(await AdminTeamAliassenRepository.DeleteAsync(manualId, club, cs) > 0);
            Assert.Null(await Scalar($"SELECT [Id] FROM [dbo].[TeamAliassen] WHERE [Id] = {manualId} AND [ClubCode] = @club"));
        }
        finally
        {
            await using var cleanup = new SqlCommand(@"DELETE FROM [dbo].[TeamAliassen] WHERE [ClubCode] = @club;
                DELETE FROM [dbo].[Teams] WHERE [ClubCode] = @club; DELETE FROM [his].[teams] WHERE [ClubCode] = @club;", connection);
            cleanup.Parameters.AddWithValue("club", club);
            await cleanup.ExecuteNonQueryAsync();
        }

        async Task<object?> Scalar(string sql)
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("club", club);
            return await command.ExecuteScalarAsync();
        }
    }
}
