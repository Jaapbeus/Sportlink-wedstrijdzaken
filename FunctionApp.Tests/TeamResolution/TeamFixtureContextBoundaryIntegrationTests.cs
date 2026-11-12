using Microsoft.Data.SqlClient;
using FunctionApp.Tests.Feedback;
using SportlinkFunction.TeamResolution;
using Planner.Shared.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

public sealed partial class TeamCanonicalisatieAliasStateIntegrationTests
{
    [SqlServerFact]
    public async Task IncompleteOwnedRow_BlocksSelectionBeforeHomeAwayAndTimeFiltering()
    {
        await using var rows = new FixtureBoundaryRows();
        await rows.InitializeAsync();
        foreach (var missingHome in new[] { true, false })
        {
            await rows.ClearAsync();
            await rows.InsertAsync();
            await rows.InsertAsync(home: missingHome ? null : "JO12-1", away: missingHome ? "MO12-1" : null, time: "09:00");
            Assert.Equal(2, await rows.NativeVisibleCountAsync());
            Assert.Null(TeamFixtureChoice.Choose(await rows.ContextAsync(), rows.First, rows.Second, "10:30"));
        }
    }

    [SqlServerFact]
    public async Task CancelledStatusVisibility_AgreesWithNativePlannerPredicate()
    {
        await using var rows = new FixtureBoundaryRows();
        await rows.InitializeAsync();
        foreach (var status in new string?[] { "aFgElAsT", " AFGELAST", "AFGELAST ", null, "Te spelen" })
        {
            await rows.ClearAsync();
            await rows.InsertAsync();
            await rows.InsertAsync(status: status);
            var visible = await rows.NativeVisibleCountAsync();
            var context = await rows.ContextAsync();
            Assert.Equal(visible, context.Fixtures.Count);
            Assert.Equal(visible == 1 ? rows.First : (int?)null,
                TeamFixtureChoice.Choose(context, rows.First, rows.Second, "10:30"));
        }
    }

    [SqlServerFact]
    public async Task SourceTimeWithSeconds_MatchesMinuteRequestWithoutDiscardingNonzeroSeconds()
    {
        await using var rows = new FixtureBoundaryRows();
        await rows.InitializeAsync();
        await rows.InsertAsync(time: "10:30:00");
        Assert.Equal(rows.First, TeamFixtureChoice.Choose(await rows.ContextAsync(), rows.First, rows.Second, "10:30"));
        await rows.ClearAsync();
        await rows.InsertAsync(time: "10:30:01");
        Assert.Null(TeamFixtureChoice.Choose(await rows.ContextAsync(), rows.First, rows.Second, "10:30"));
    }

    private sealed class FixtureBoundaryRows : IAsyncDisposable
    {
        private readonly string _cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;
        private readonly string _club = "Z1816R2" + Guid.NewGuid().ToString("N")[..8];
        private SqlConnection _connection = default!;
        public int First { get; private set; }
        public int Second { get; private set; }

        public async Task InitializeAsync()
        {
            _connection = new SqlConnection(_cs);
            await _connection.OpenAsync();
            First = await TeamAsync("JO12-1");
            Second = await TeamAsync("MO12-1");
        }

        public Task<TeamFixtureContext> ContextAsync() =>
            new TeamFixtureContextRepository(_cs).GetAsync(_club, new DateOnly(2026, 11, 10), [First, Second]);

        public async Task<int> NativeVisibleCountAsync()
        {
            await using var command = new SqlCommand(@"SELECT COUNT(*) FROM [his].[matches] WHERE [ClubCode] = @club AND mta_deleted IS NULL
                AND CAST(kaledatum AS date) = '2026-11-10' AND status <> 'Afgelast' ", _connection);
            command.Parameters.AddWithValue("club", _club);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task InsertAsync(string? home = "JO12-1", string? away = "MO12-1", string? status = "Te spelen", string time = "10:30")
        {
            await using var command = new SqlCommand(@"INSERT INTO [his].[matches] (bk_matches, teamnaam, thuisteam, uitteam, aanvangstijd, kaledatum, status, [ClubCode])
                VALUES (@key, 'JO12-1', @home, @away, @time, '2026-11-10', @status, @club)", _connection);
            command.Parameters.AddWithValue("club", _club);
            command.Parameters.AddWithValue("key", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("home", (object?)home ?? DBNull.Value);
            command.Parameters.AddWithValue("away", (object?)away ?? DBNull.Value);
            command.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
            command.Parameters.AddWithValue("time", time);
            await command.ExecuteNonQueryAsync();
        }

        private async Task<int> TeamAsync(string name)
        {
            await using var command = new SqlCommand("INSERT INTO [dbo].[Teams] ([ClubCode], [Teamnaam], [TeamnaamGenormaliseerd]) OUTPUT INSERTED.[TeamId] VALUES (@club, @name, @name)", _connection);
            command.Parameters.AddWithValue("club", _club);
            command.Parameters.AddWithValue("name", name);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public Task ClearAsync() => ExecuteAsync("DELETE FROM [his].[matches] WHERE [ClubCode] = @club");
        private async Task ExecuteAsync(string sql)
        {
            await using var command = new SqlCommand(sql, _connection);
            command.Parameters.AddWithValue("club", _club);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection is null) return;
            try { await ExecuteAsync("DELETE FROM [his].[matches] WHERE [ClubCode] = @club; DELETE FROM [dbo].[TeamAliassen] WHERE [ClubCode] = @club; DELETE FROM [dbo].[Teams] WHERE [ClubCode] = @club"); }
            finally { await _connection.DisposeAsync(); }
        }
    }
}
