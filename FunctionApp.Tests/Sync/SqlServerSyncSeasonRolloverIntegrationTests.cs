using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Sync;
using SportlinkFunction;
using SportlinkFunction.Sync;
using Xunit;

namespace FunctionApp.Tests.Sync;

public sealed class SqlServerSyncSeasonRolloverIntegrationTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

    [SqlServerFact]
    public async Task TrackedStart_LeaseCollision_DoesNotRollSeasonBuildWindowOrCreateFailedJob()
    {
        var clubCode = TestClubCode();
        var today = SqlServerSeasonRollover.VandaagInNederland;
        await using var connection = await OpenAsync();
        await MaakClubAsync(connection, clubCode, startMaand: 1);
        await VoegSeizoenToeAsync(connection, clubCode, today.Year - 2, 1);
        await VoegSeizoenToeAsync(connection, clubCode, today.Year - 1, 1);
        var seasonsBefore = await SeizoennamenAsync(connection, clubCode);
        var windowBuilt = false;
        try
        {
            await using (var lease = await SqlServerSyncStagingLease.TryAcquireAsync())
            {
                lease.Should().NotBeNull();
                var run = () => FetchAndStoreApiData.RunTrackedSyncAsync(
                    () => { windowBuilt = true; return Task.FromResult((0, 0)); },
                    NullLogger.Instance, clubCode);
                await run.Should().ThrowAsync<SyncAlreadyRunningException>();
            }
            windowBuilt.Should().BeFalse();
            (await SeizoennamenAsync(connection, clubCode)).Should().Equal(seasonsBefore);
            (await CountJobsAsync(connection, clubCode)).Should().Be(0);
        }
        finally
        {
            await using var deleteJobs = new SqlCommand(
                "DELETE FROM [dbo].[SyncJobs] WHERE [ClubCode] = @ClubCode", connection);
            deleteJobs.Parameters.AddWithValue("@ClubCode", clubCode);
            await deleteJobs.ExecuteNonQueryAsync();
            await VerwijderClubAsync(connection, clubCode);
        }
    }

    [SqlServerFact]
    public async Task EnsureAsync_Doorrolgrens_IsPerClub_Idempotent_EnBehoudtHistorie()
    {
        var clubCode = TestClubCode();
        var otherClubCode = TestClubCode();
        await using var connection = await OpenAsync();
        await MaakClubAsync(connection, clubCode, startMaand: 9);
        await MaakClubAsync(connection, otherClubCode, startMaand: 7);
        await VoegSeizoenToeAsync(connection, clubCode, 2024, 9);
        await VoegSeizoenToeAsync(connection, clubCode, 2025, 9);
        await VoegSeizoenToeAsync(connection, otherClubCode, 2024, 7);
        await VoegSeizoenToeAsync(connection, otherClubCode, 2025, 7);

        try
        {
            await EnsureMetDatumAsync(connection, clubCode, new DateOnly(2026, 6, 30));

            (await SeizoennamenAsync(connection, clubCode)).Should().NotContain("2026-2027");
            await EnsureMetDatumAsync(connection, otherClubCode, new DateOnly(2026, 6, 30));
            (await SeizoennamenAsync(connection, otherClubCode)).Should().Contain("2026-2027");

            await EnsureMetDatumAsync(connection, clubCode, new DateOnly(2026, 7, 1));
            (await SeizoennamenAsync(connection, clubCode)).Should().Contain("2026-2027");
            (await GrenzenAsync(connection, clubCode, "2026-2027"))
                .Should().Be((new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31)));

            await EnsureMetDatumAsync(connection, clubCode, new DateOnly(2026, 7, 1));
            (await SeizoennamenAsync(connection, clubCode)).Should().Equal("2024-2025", "2025-2026", "2026-2027");
            (await GrenzenAsync(connection, clubCode, "2024-2025"))
                .Should().Be((new DateOnly(2024, 9, 1), new DateOnly(2025, 8, 31)));
        }
        finally
        {
            await VerwijderClubAsync(connection, clubCode);
            await VerwijderClubAsync(connection, otherClubCode);
        }
    }

    [SqlServerFact]
    public async Task EnsureAsync_FebruariStart_BetrektSeizoenVolgendJaarBijDoorrol()
    {
        var clubCode = TestClubCode();
        await using var connection = await OpenAsync();
        await MaakClubAsync(connection, clubCode, startMaand: 2);
        await VoegSeizoenToeAsync(connection, clubCode, 2024, 2);
        await VoegSeizoenToeAsync(connection, clubCode, 2025, 2);

        try
        {
            await EnsureMetDatumAsync(connection, clubCode, new DateOnly(2026, 11, 30));
            (await SeizoennamenAsync(connection, clubCode)).Should().NotContain("2027-2028");

            await EnsureMetDatumAsync(connection, clubCode, new DateOnly(2026, 12, 1));
            (await GrenzenAsync(connection, clubCode, "2027-2028"))
                .Should().Be((new DateOnly(2027, 2, 1), new DateOnly(2028, 1, 31)));
        }
        finally
        {
            await VerwijderClubAsync(connection, clubCode);
        }
    }

    [SqlServerFact]
    public async Task RunSyncWithSeasonWindowAsync_BouwtVensterNaDoorrolEnBehoudtLockEigenaar()
    {
        var clubCode = TestClubCode();
        var vandaag = SqlServerSeasonRollover.VandaagInNederland;
        await using var connection = await OpenAsync();
        await MaakClubAsync(connection, clubCode, startMaand: 1);
        await VoegSeizoenToeAsync(connection, clubCode, vandaag.Year - 2, 1);
        await VoegSeizoenToeAsync(connection, clubCode, vandaag.Year - 1, 1);
        SystemUtilities.AppSettings.SetForTests("clubCode", clubCode);

        try
        {
            var run = () => SportlinkSyncPipeline.RunSyncWithSeasonWindowAsync(
                () => throw new VensterGebouwdNaDoorrolException(), "http://127.0.0.1", "clientId=fixture-test", NullLogger.Instance);

            await run.Should().ThrowAsync<VensterGebouwdNaDoorrolException>();
            (await SeizoennamenAsync(connection, clubCode))
                .Should().Contain($"{vandaag.Year}-{vandaag.Year + 1}");

            var lease = await SqlServerSyncStagingLease.TryAcquireAsync();
            lease.Should().NotBeNull("the staging lease must still be owned/released by one pipeline path");
            await lease!.DisposeAsync();
        }
        finally
        {
            SystemUtilities.AppSettings.ResetForTests();
            await VerwijderClubAsync(connection, clubCode);
        }
    }

    [SqlServerFact]
    public async Task RunSyncWithSeasonWindowAsync_DoorrolfoutBreektAfVoorVensterProviderEnTimestamp()
    {
        var clubCode = TestClubCode();
        var constraintName = $"CK_1818_rollover_failure_{Guid.NewGuid():N}";
        var constraintAdded = false;
        var previousTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var connection = await OpenAsync();
        await MaakClubAsync(connection, clubCode, startMaand: 1);
        SystemUtilities.AppSettings.SetForTests("clubCode", clubCode);

        try
        {
            await using (var timestampCommand = new SqlCommand(
                "UPDATE [dbo].[AppSettings] SET [LastSyncTimestamp] = @Timestamp WHERE [ClubCode] = @ClubCode",
                connection))
            {
                timestampCommand.Parameters.AddWithValue("@Timestamp", previousTimestamp);
                timestampCommand.Parameters.AddWithValue("@ClubCode", clubCode);
                await timestampCommand.ExecuteNonQueryAsync();
            }

            await using (var constraintCommand = new SqlCommand(
                $"ALTER TABLE [dbo].[ClubSeason] ADD CONSTRAINT [{constraintName}] CHECK ([ClubCode] <> N'{clubCode}');",
                connection))
            {
                await constraintCommand.ExecuteNonQueryAsync();
                constraintAdded = true;
            }

            using var server = new SportlinkFixtureServer();
            var windowBuilt = false;
            var run = () => SportlinkSyncPipeline.RunSyncWithSeasonWindowAsync(
                () =>
                {
                    windowBuilt = true;
                    return Task.FromResult((0, 0));
                },
                server.BaseUrl, "clientId=fixture-test", NullLogger.Instance);

            await run.Should().ThrowAsync<SqlException>();
            windowBuilt.Should().BeFalse("the sync must stop before building a window");
            server.Requests.Should().BeEmpty("the provider must not be called after rollover fails");
            (await LeesLastSyncTimestampAsync(connection, clubCode)).Should().Be(previousTimestamp);

            var lease = await SqlServerSyncStagingLease.TryAcquireAsync();
            lease.Should().NotBeNull("the failed rollover must release the staging lease");
            await lease!.DisposeAsync();
        }
        finally
        {
            if (constraintAdded)
            {
                await using var dropConstraint = new SqlCommand(
                    $"ALTER TABLE [dbo].[ClubSeason] DROP CONSTRAINT [{constraintName}];", connection);
                await dropConstraint.ExecuteNonQueryAsync();
            }
            SystemUtilities.AppSettings.ResetForTests();
            await VerwijderClubAsync(connection, clubCode);
        }
    }

    [SqlServerFact]
    public async Task RunSyncWithSeasonWindowAsync_TimeZonesUnavailable_StopsBeforeRolloverWindowProviderAndTimestamp()
    {
        var clubCode = TestClubCode();
        var utc = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var previousTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var connection = await OpenAsync();
        await MaakClubAsync(connection, clubCode, startMaand: 9);
        await VoegSeizoenToeAsync(connection, clubCode, 2024, 9);
        await VoegSeizoenToeAsync(connection, clubCode, 2025, 9);
        SystemUtilities.AppSettings.SetForTests("clubCode", clubCode);
        try
        {
            await using (var command = new SqlCommand(
                "UPDATE [dbo].[AppSettings] SET [LastSyncTimestamp] = @Timestamp WHERE [ClubCode] = @ClubCode", connection))
            {
                command.Parameters.AddWithValue("@Timestamp", previousTimestamp);
                command.Parameters.AddWithValue("@ClubCode", clubCode);
                await command.ExecuteNonQueryAsync();
            }
            using var server = new SportlinkFixtureServer();
            var windowBuilt = false;
            var run = () => SportlinkSyncPipeline.RunSyncWithSeasonWindowAsync(
                () => { windowBuilt = true; throw new VensterGebouwdNaDoorrolException(); },
                server.BaseUrl, "clientId=fixture-test", NullLogger.Instance,
                () => SqlServerSeasonRollover.BepaalVandaag(utc, _ => throw new TimeZoneNotFoundException()));

            await run.Should().ThrowAsync<TimeZoneNotFoundException>();
            windowBuilt.Should().BeFalse();
            server.Requests.Should().BeEmpty();
            (await SeizoennamenAsync(connection, clubCode)).Should().Equal("2024-2025", "2025-2026");
            (await LeesLastSyncTimestampAsync(connection, clubCode)).Should().Be(previousTimestamp);
            var lease = await SqlServerSyncStagingLease.TryAcquireAsync();
            lease.Should().NotBeNull("a failed business clock must release the original global lease");
            await lease!.DisposeAsync();
        }
        finally
        {
            SystemUtilities.AppSettings.ResetForTests();
            await VerwijderClubAsync(connection, clubCode);
        }
    }

    private static string TestClubCode() => "t" + Guid.NewGuid().ToString("N")[..12];

    private static async Task EnsureMetDatumAsync(SqlConnection connection, string clubCode, DateOnly vandaag)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await SqlServerSeasonRollover.EnsureAsync(connection, transaction, clubCode, vandaag);
        await transaction.CommitAsync();
    }

    private static async Task MaakClubAsync(SqlConnection connection, string clubCode, int startMaand)
    {
        await using var command = new SqlCommand(@"
            INSERT INTO [dbo].[AppSettings]
                ([ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId], [SeasonStartMonth], [SyncEnabled])
            VALUES (N'Jan de Vries', @ClubCode, N'https://sportlink.voorbeeld.test', N'test-client', @StartMaand, 0);",
            connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@StartMaand", startMaand);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task VoegSeizoenToeAsync(SqlConnection connection, string clubCode, int jaar, int maand)
    {
        var seizoen = SeizoenKalender.Seizoen(jaar, maand);
        await using var command = new SqlCommand(@"
            INSERT INTO [dbo].[ClubSeason] ([ClubCode], [Name], [DateFrom], [DateUntil])
            VALUES (@ClubCode, @Name, @DateFrom, @DateUntil);", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@Name", seizoen.Naam);
        command.Parameters.Add(new SqlParameter("@DateFrom", System.Data.SqlDbType.Date)
        {
            Value = seizoen.Van.ToDateTime(TimeOnly.MinValue)
        });
        command.Parameters.Add(new SqlParameter("@DateUntil", System.Data.SqlDbType.Date)
        {
            Value = seizoen.Tot.ToDateTime(TimeOnly.MinValue)
        });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> SeizoennamenAsync(SqlConnection connection, string clubCode)
    {
        await using var command = new SqlCommand(
            "SELECT [Name] FROM [dbo].[ClubSeason] WHERE [ClubCode] = @ClubCode ORDER BY [Name]", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        await using var reader = await command.ExecuteReaderAsync();
        var namen = new List<string>();
        while (await reader.ReadAsync()) namen.Add(reader.GetString(0).Trim());
        return namen;
    }

    private static async Task<(DateOnly From, DateOnly Until)> GrenzenAsync(SqlConnection connection, string clubCode, string naam)
    {
        await using var command = new SqlCommand(
            "SELECT [DateFrom], [DateUntil] FROM [dbo].[ClubSeason] WHERE [ClubCode] = @ClubCode AND [Name] = @Name", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@Name", naam);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Season row not found.");
        return (DateOnly.FromDateTime(reader.GetDateTime(0)), DateOnly.FromDateTime(reader.GetDateTime(1)));
    }

    private static async Task<DateTime?> LeesLastSyncTimestampAsync(SqlConnection connection, string clubCode)
    {
        await using var command = new SqlCommand(
            "SELECT [LastSyncTimestamp] FROM [dbo].[AppSettings] WHERE [ClubCode] = @ClubCode", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (DateTime)value;
    }

    private static async Task<int> CountJobsAsync(SqlConnection connection, string clubCode)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[SyncJobs] WHERE [ClubCode] = @ClubCode", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task VerwijderClubAsync(SqlConnection connection, string clubCode)
    {
        await using var command = new SqlCommand(@"
            DELETE FROM [dbo].[ClubSeason] WHERE [ClubCode] = @ClubCode;
            DELETE FROM [dbo].[AppSettings] WHERE [ClubCode] = @ClubCode;", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private sealed class VensterGebouwdNaDoorrolException : Exception { }
}
