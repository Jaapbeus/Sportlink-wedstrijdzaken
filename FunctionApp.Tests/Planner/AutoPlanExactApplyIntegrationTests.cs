using AwesomeAssertions;
using System.Data;
using System.Diagnostics;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared;
using SportlinkFunction;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>Exact-plan apply against the disposable fresh SQL Server CI database.</summary>
public class AutoPlanExactApplyIntegrationTests
{
    private const string Club = "ALLSTARS";
    private const string Date = "2026-09-05";
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException("SQL Server integration connection is missing.");

    [SqlServerFact]
    public async Task ExactQuarterLaneAndUnassignedRowArePersistedAsShown()
    {
        await SetupAsync();
        await AddMatchAsync(9802001, "ALLSTARS JO13-1");
        await AddMatchAsync(9802002, "ALLSTARS JO15-1");
        try
        {
            var result = await ApplyAsync(Choice(9802001, 101, "A2", "10:00"), Choice(9802002));
            result.Bijgewerkt.Should().Be(1);
            (await FieldAsync(9802001)).Should().Be("Kunstgras 1 A2");
            (await FieldAsync(9802002)).Should().BeNull();
        }
        finally { await CleanupAsync(); }
    }

    [SqlServerFact]
    public async Task ExactApplyRetainsSourceSecondsWhenDisplayedMinuteIsUnchanged()
    {
        await SetupAsync();
        await AddMatchAsync(9802006, "ALLSTARS JO13-1", "Kunstgras 1 A2", "10:00:30");
        try
        {
            var unchanged = await ApplyAsync(Choice(9802006, 101, "A2", "10:00",
                "Kunstgras 1 A2", "10:00:30"));
            unchanged.Bijgewerkt.Should().Be(0);
            (await TimeAsync(9802006)).Should().Be("10:00:30");

            var laneChange = await ApplyAsync(Choice(9802006, 101, "A1", "10:00",
                "Kunstgras 1 A2", "10:00:30"));
            laneChange.Bijgewerkt.Should().Be(1);
            (await FieldAsync(9802006)).Should().Be("Kunstgras 1 A1");
            (await TimeAsync(9802006)).Should().Be("10:00:30");
        }
        finally { await CleanupAsync(); }
    }

    [SqlServerFact]
    public async Task ExactApply_UnknownUnassignedRowDoesNotBlockValidAssignment()
    {
        await SetupAsync();
        await AddMatchAsync(9802010, "ALLSTARS JO99-1", "Kunstgras 1 A1", "10:00");
        await AddMatchAsync(9802011, "ALLSTARS JO13-1");
        try
        {
            var response = await ApplyAsync(
                Choice(9802010, oldField: "Kunstgras 1 A1", oldTime: "10:00"),
                Choice(9802011, field: 101, lane: "B1", time: "10:00"));

            response.Bijgewerkt.Should().Be(1);
            (await FieldAsync(9802010)).Should().Be("Kunstgras 1 A1");
            (await FieldAsync(9802011)).Should().Be("Kunstgras 1 B1");
        }
        finally { await CleanupAsync(); }
    }

    [SqlServerFact]
    public async Task ExactApply_TargetLaneFractionOverridesTeamDefault()
    {
        await SetupAsync();
        await AddMatchAsync(9802012, "ALLSTARS JO13-1", "Kunstgras 1 A1", "09:00");
        try
        {
            var response = await ApplyAsync(Choice(9802012, field: 101, lane: "A", time: "10:00",
                oldField: "Kunstgras 1 A1", oldTime: "09:00"));

            response.Bijgewerkt.Should().Be(1);
            (await FieldAsync(9802012)).Should().Be("Kunstgras 1 A");
        }
        finally { await CleanupAsync(); }
    }

    [SqlServerFact]
    public async Task PlannerPreview_QuarterTeamCanApplyASelectedHalfLane()
    {
        await SetupAsync();
        await AddMatchAsync(9802013, "ALLSTARS JO13-1");
        try
        {
            var preview = await AutoPlanService.AutoPlanAsync(
                new AutoPlanRequest { Datum = Date }, Club, NullLogger.Instance);
            var planned = preview.Wedstrijden.Single(w => w.WedstrijdCode == 9802013);
            planned.OptimaalVeldNummer.Should().NotBeNull();
            planned.OptimaalTijd.Should().NotBeNullOrWhiteSpace();

            var apply = await ApplyAsync(Choice(9802013, planned.OptimaalVeldNummer, "A",
                planned.OptimaalTijd, planned.HuidigeVeld, planned.HuidigeTijd));

            apply.Bijgewerkt.Should().Be(1);
            (await FieldAsync(9802013)).Should().EndWith(" A");
        }
        finally { await CleanupAsync(); }
    }

    [SqlServerFact]
    public async Task PlannerPreview_HalfTeamCanApplyASelectedQuarterLane()
    {
        await SetupAsync();
        await AddMatchAsync(9802014, "ALLSTARS JO15-1");
        try
        {
            var preview = await AutoPlanService.AutoPlanAsync(
                new AutoPlanRequest { Datum = Date }, Club, NullLogger.Instance);
            var planned = preview.Wedstrijden.Single(w => w.WedstrijdCode == 9802014);
            planned.OptimaalVeldNummer.Should().NotBeNull();
            planned.OptimaalTijd.Should().NotBeNullOrWhiteSpace();

            var apply = await ApplyAsync(Choice(9802014, planned.OptimaalVeldNummer, "A1",
                planned.OptimaalTijd, planned.HuidigeVeld, planned.HuidigeTijd));

            apply.Bijgewerkt.Should().Be(1);
            (await FieldAsync(9802014)).Should().EndWith(" A1");
        }
        finally { await CleanupAsync(); }
    }

    [SqlServerFact]
    public async Task StaleLaneDuplicateCrossClubAndInvalidFractionRejectWithoutWrites()
    {
        await SetupAsync();
        await AddMatchAsync(9802003, "ALLSTARS JO13-1", "Kunstgras 1 A1", "09:00");
        await AddMatchAsync(9802099, "demo", club: "DEMO2");
        try
        {
            await ((Func<Task>)(() => ApplyAsync(Choice(9802003, 101, "A2", "10:00", "Kunstgras 1 A2", "09:00"))))
                .Should().ThrowAsync<AutoPlanApplyConflictException>();
            await ((Func<Task>)(() => ApplyAsync(Choice(9802003, 101, "A2", "10:00", "Kunstgras 1 A1", "09:00"),
                Choice(9802003, 101, "B1", "10:00", "Kunstgras 1 A1", "09:00"))))
                .Should().ThrowAsync<AutoPlanApplyConflictException>();
            await ((Func<Task>)(() => ApplyAsync(Choice(9802099))))
                .Should().ThrowAsync<AutoPlanApplyConflictException>();
            await ((Func<Task>)(() => ApplyAsync(Choice(9802003, 101, "X", "10:00", "Kunstgras 1 A1", "09:00"))))
                .Should().ThrowAsync<AutoPlanApplyConflictException>();
            (await FieldAsync(9802003)).Should().Be("Kunstgras 1 A1");
        }
        finally { await CleanupAsync(); }
    }

    [SqlServerFact]
    public async Task HalfQuarterConflictAndSecondWriteFailureRollbackFirst()
    {
        await SetupAsync();
        await AddMatchAsync(9802004, "ALLSTARS JO13-1");
        await AddMatchAsync(9802005, "ALLSTARS JO15-1");
        try
        {
            await ((Func<Task>)(() => ApplyAsync(Choice(9802004, 101, "A1", "10:00"),
                Choice(9802005, 101, "A", "10:00"))))
                .Should().ThrowAsync<AutoPlanApplyConflictException>();

            await ExecAsync("""
                EXEC('CREATE TRIGGER [test_1802_second] ON [his].[matches] AFTER UPDATE AS
                BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE [wedstrijdcode] = 9802005)
                THROW 51000, ''synthetic second write failure'', 1; END')
                """);
            await ((Func<Task>)(() => ApplyAsync(Choice(9802004, 101, "A1", "10:00"),
                Choice(9802005, 102, "A", "12:00"))))
                .Should().ThrowAsync<SqlException>();
            (await FieldAsync(9802004)).Should().BeNull();
            (await FieldAsync(9802005)).Should().BeNull();
        }
        finally
        {
            await ExecAsync("IF OBJECT_ID('[his].[test_1802_second]', 'TR') IS NOT NULL DROP TRIGGER [his].[test_1802_second]");
            await CleanupAsync();
        }
    }

    [SqlServerFact]
    public async Task ExactApply_MapsSqlServerDeadlockErrorToSafeConflict()
    {
        const long firstId = 9802020;
        const long secondId = 9802021;
        const string signalTable = "[dbo].[test_1802_deadlock_signal]";
        const string trigger = "[his].[test_1802_deadlock]";
        await SetupAsync();
        await AddMatchAsync(firstId, "ALLSTARS JO13-1");
        await AddMatchAsync(secondId, "ALLSTARS JO15-1");
        await ExecAsync($"IF OBJECT_ID('{trigger}', 'TR') IS NOT NULL DROP TRIGGER {trigger}");
        await ExecAsync($"IF OBJECT_ID('{signalTable}', 'U') IS NOT NULL DROP TABLE {signalTable}");
        await ExecAsync($"CREATE TABLE {signalTable} ([Id] int IDENTITY(1,1) NOT NULL PRIMARY KEY)");
        Task<AutoPlanToepassenResponse>? apply = null;
        SqlConnection? blocker = null;
        SqlTransaction? blockerTransaction = null;
        SqlCommand? blockerCommand = null;
        Task<int>? blockerUpdate = null;
        try
        {
            await ExecAsync($"""
                CREATE TRIGGER {trigger} ON [his].[matches] AFTER UPDATE AS
                BEGIN
                    IF EXISTS (SELECT 1 FROM inserted WHERE [wedstrijdcode] = {firstId})
                    BEGIN
                        INSERT INTO {signalTable} DEFAULT VALUES;
                        DECLARE @lockResult int;
                        EXEC @lockResult = sys.sp_getapplock
                            @Resource = N'test_1802_deadlock', @LockMode = 'Shared',
                            @LockOwner = 'Transaction', @LockTimeout = -1;
                    END
                END
                """);

            blocker = new SqlConnection(ConnectionString);
            await blocker.OpenAsync();
            blockerTransaction = blocker.BeginTransaction(IsolationLevel.ReadCommitted);
            using (var reserveLock = new SqlCommand("""
                SET DEADLOCK_PRIORITY HIGH;
                DECLARE @lockResult int;
                EXEC @lockResult = sys.sp_getapplock
                    @Resource = N'test_1802_deadlock', @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction', @LockTimeout = 0;
                IF @lockResult < 0 THROW 51000, 'Could not reserve deadlock test lock', 1;
                """, blocker, blockerTransaction))
                await reserveLock.ExecuteNonQueryAsync();

            apply = ApplyAsync(Choice(firstId, 101, "A1", "10:00"),
                Choice(secondId, 102, "A1", "12:00"));
            await WaitForSignalAsync(signalTable);

            blockerCommand = new SqlCommand(
                "UPDATE [his].[matches] SET [veld] = [veld] WHERE [wedstrijdcode] = @id",
                blocker, blockerTransaction);
            blockerCommand.Parameters.AddWithValue("@id", firstId);
            blockerUpdate = blockerCommand.ExecuteNonQueryAsync();
            // #1888: the victim is fixed by DEADLOCK_PRIORITY (HIGH blocker vs NORMAL apply), but which
            // client task completes first after the victim's rollback is scheduler timing. Never race
            // the two tasks: await each one with a bound and assert its own outcome.
            await ((Func<Task>)(() => apply.WaitAsync(TimeSpan.FromSeconds(30)))).Should()
                .ThrowAsync<AutoPlanApplyConflictException>(
                    "the exact-apply transaction is the lower-priority deadlock victim");
            await blockerUpdate.WaitAsync(TimeSpan.FromSeconds(30));

            await blockerTransaction.CommitAsync();
            (await FieldAsync(firstId)).Should().BeNull("the chosen victim's plan must roll back");
            (await FieldAsync(secondId)).Should().BeNull();
        }
        finally
        {
            if (blockerUpdate is { IsCompleted: false })
            {
                try { blockerCommand?.Cancel(); }
                catch { /* The rollback below still releases the lock. */ }
            }
            if (blockerTransaction != null)
            {
                try { await blockerTransaction.RollbackAsync(); }
                catch { /* The transaction may already have committed or been selected as victim. */ }
            }
            if (blockerUpdate != null)
            {
                try { await blockerUpdate.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* Observe completion after releasing the transaction. */ }
            }
            blockerCommand?.Dispose();
            if (blockerTransaction != null) await blockerTransaction.DisposeAsync();
            if (blocker != null) await blocker.DisposeAsync();

            bool applySettled = true;
            if (apply != null)
            {
                try { await apply.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch { applySettled = apply.IsCompleted; }
            }
            if (applySettled)
            {
                await ExecAsync($"IF OBJECT_ID('{trigger}', 'TR') IS NOT NULL DROP TRIGGER {trigger}");
                await ExecAsync($"IF OBJECT_ID('{signalTable}', 'U') IS NOT NULL DROP TABLE {signalTable}");
                await CleanupAsync();
            }
        }
    }

    private static async Task WaitForSignalAsync(string signalTable)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            var count = await ScalarAsync($"SELECT COUNT_BIG(*) FROM {signalTable} WITH (READUNCOMMITTED)");
            if (Convert.ToInt64(count) > 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The exact-apply trigger did not signal before the deadlock test timeout.");
    }

    private static async Task SetupAsync()
    {
        SystemUtilities.AppSettings.SetForTests("clubCode", Club);
        SystemUtilities.AppSettings.SetForTests("plannerAfzenderNaam", "testplanner");
        await CleanupAsync();
        await ExecAsync("""
            UPDATE [dbo].[Velden] SET [VeldNaam] = 'Kunstgras 1', [Actief] = 1 WHERE [VeldNummer] = 101;
            UPDATE [dbo].[Velden] SET [VeldNaam] = 'Kunstgras 2', [Actief] = 1 WHERE [VeldNummer] = 102;
            DELETE FROM [dbo].[Speeltijden] WHERE [ClubCode] = 'ALLSTARS' AND [Leeftijd] IN ('JO13','JO15');
            INSERT INTO [dbo].[Speeltijden]
                ([Leeftijd],[Veldafmeting],[WedstrijdTotaal],[WedstrijdHelft],[WedstrijdRust],[ClubCode])
            VALUES ('JO13',0.25,60,30,0,'ALLSTARS'), ('JO15',0.5,60,30,0,'ALLSTARS');
            """);
    }

    private static async Task AddMatchAsync(long id, string team, string? field = null,
        string? time = null, string club = Club)
    {
        await ExecAsync("""
            INSERT INTO [his].[matches]
                ([bk_matches],[wedstrijdcode],[kaledatum],[teamnaam],[veld],[aanvangstijd],[status],[ClubCode])
            VALUES (@bk,@id,@date,@team,@field,@time,'Te spelen',@club)
            """, ("@bk", $"test-{id}"), ("@id", id), ("@date", Date), ("@team", team),
            ("@field", field), ("@time", time), ("@club", club));
    }

    private static AutoPlanToewijzing Choice(long id, int? field = null, string? lane = null,
        string? time = null, string? oldField = null, string? oldTime = null) => new()
    {
        WedstrijdCode = id, VeldNummer = field, VeldSubpositie = lane, Tijd = time,
        HuidigeVeld = oldField, HuidigeTijd = oldTime
    };

    private static Task<AutoPlanToepassenResponse> ApplyAsync(params AutoPlanToewijzing[] choices) =>
        AutoPlanService.AutoPlanToepassenAsync(new AutoPlanToepassenRequest
        { Datum = Date, Toewijzingen = choices.ToList() }, Club, NullLogger.Instance);

    private static async Task<string?> FieldAsync(long id)
    {
        var value = await ScalarAsync("SELECT [veld] FROM [his].[matches] WHERE [wedstrijdcode] = @id", ("@id", id));
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task<string?> TimeAsync(long id)
    {
        var value = await ScalarAsync("SELECT [aanvangstijd] FROM [his].[matches] WHERE [wedstrijdcode] = @id", ("@id", id));
        return value is null or DBNull ? null : (string)value;
    }

    private static Task CleanupAsync() => ExecAsync("DELETE FROM [his].[matches] WHERE [wedstrijdcode] BETWEEN 9802000 AND 9802099");

    private static async Task ExecAsync(string sql, params (string Key, object? Value)[] parameters)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string sql, params (string Key, object? Value)[] parameters)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }
}
