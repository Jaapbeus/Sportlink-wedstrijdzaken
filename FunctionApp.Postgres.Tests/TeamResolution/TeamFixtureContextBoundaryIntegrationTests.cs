using Npgsql;
using Database.Postgres.Tests;
using FunctionApp.Postgres.TeamResolution;
using Planner.Shared.TeamResolution;
using Xunit;

namespace FunctionApp.Postgres.Tests.TeamResolution;

public sealed partial class TeamFixtureContextIntegrationTests
{
    [PostgresFact]
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

    [PostgresFact]
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

    [PostgresFact]
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

    [PostgresFact]
    public async Task NativeFixturePredicate_HasValidNonUniqueExpressionIndexAfterRepeatedEnsure()
    {
        var cs = PostgresTestEnvironment.ConnectionStringOrNull!;
        var orchestrator = new Database.Postgres.PostgresMergeOrchestrator(cs);
        await orchestrator.EnsureHisTableAsync(Database.Postgres.KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(Database.Postgres.KnownEntities.Matches);
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"SELECT i.indisunique, i.indisvalid,
            pg_get_expr(i.indexprs, i.indrelid), pg_get_expr(i.indpred, i.indrelid)
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'his' AND c.relname = 'ix_matches_club_status_upper_live'", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Contains("upper", reader.GetString(2));
        Assert.Contains("status", reader.GetString(2));
        Assert.Contains("mta_deleted IS NULL", reader.GetString(3));
        Assert.Contains("AFGELAST", reader.GetString(3));
        Assert.False(await reader.ReadAsync());
    }

    private sealed class FixtureBoundaryRows : IAsyncDisposable
    {
        private readonly string _cs = PostgresTestEnvironment.ConnectionStringOrNull!;
        private readonly string _club = "Z1816R2" + Guid.NewGuid().ToString("N")[..8];
        private NpgsqlConnection _connection = default!;
        public int First { get; private set; }
        public int Second { get; private set; }

        public async Task InitializeAsync()
        {
            _connection = new NpgsqlConnection(_cs);
            await _connection.OpenAsync();
            First = await TeamAsync("JO12-1");
            Second = await TeamAsync("MO12-1");
        }

        public Task<TeamFixtureContext> ContextAsync() =>
            new TeamFixtureContextRepository(_cs).GetAsync(_club, new DateOnly(2026, 11, 10), [First, Second]);

        public async Task<int> NativeVisibleCountAsync()
        {
            await using var command = new NpgsqlCommand(@"SELECT COUNT(*) FROM his.matches WHERE clubcode = @club AND mta_deleted IS NULL
                AND kaledatum::date = '2026-11-10' AND UPPER(status) <> 'AFGELAST' ", _connection);
            command.Parameters.AddWithValue("club", _club);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task InsertAsync(string? home = "JO12-1", string? away = "MO12-1", string? status = "Te spelen", string time = "10:30")
        {
            await using var command = new NpgsqlCommand(@"INSERT INTO his.matches (wedstrijdcode, teamnaam, thuisteam, uitteam, aanvangstijd, kaledatum, status, clubcode, mta_inserted, mta_modified)
                VALUES (@key, 'JO12-1', @home, @away, @time, '2026-11-10', @status, @club, NOW(), NOW())", _connection);
            command.Parameters.AddWithValue("club", _club);
            command.Parameters.AddWithValue("key", Random.Shared.NextInt64(1, 100_000_000));
            command.Parameters.AddWithValue("home", (object?)home ?? DBNull.Value);
            command.Parameters.AddWithValue("away", (object?)away ?? DBNull.Value);
            command.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
            command.Parameters.AddWithValue("time", time);
            await command.ExecuteNonQueryAsync();
        }

        private async Task<int> TeamAsync(string name)
        {
            await using var command = new NpgsqlCommand("INSERT INTO public.teams (clubcode, teamnaam, teamnaamgenormaliseerd) VALUES (@club, @name, @name) RETURNING teamid", _connection);
            command.Parameters.AddWithValue("club", _club);
            command.Parameters.AddWithValue("name", name);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public Task ClearAsync() => ExecuteAsync("DELETE FROM his.matches WHERE clubcode = @club");
        private async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection);
            command.Parameters.AddWithValue("club", _club);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection is null) return;
            try { await ExecuteAsync("DELETE FROM his.matches WHERE clubcode = @club; DELETE FROM public.teamaliassen WHERE clubcode = @club; DELETE FROM public.teams WHERE clubcode = @club"); }
            finally { await _connection.DisposeAsync(); }
        }
    }
}
