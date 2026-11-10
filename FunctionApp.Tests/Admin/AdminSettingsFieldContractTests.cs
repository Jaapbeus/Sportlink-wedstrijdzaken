using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

public class AdminSettingsFieldContractTests
{
    [Fact]
    public void SqlServer_AllowsSportlinkDryRunForSettingsUpdates()
        => AdminSettingsFunction.AllowedFields.Should().Contain("SportlinkDryRun");

    [SqlServerFact]
    public async Task SqlServer_FetchScheduleRestartFlagUsesStoredValueWhileOtherSettingsPersist()
    {
        var club = "z1819" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = new SqlConnection(
            Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!);
        await connection.OpenAsync();
        const string initial = "0 0 8 * * *";
        const string changed = "0 0 9 * * *";
        try
        {
            await using (var insert = new SqlCommand(@"
                INSERT INTO [dbo].[AppSettings]
                    ([ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId],
                     [SeasonStartMonth], [SyncEnabled], [FetchSchedule], [BufferMinuten])
                VALUES (N'AllStars FC', @club, N'https://allstars-fc.test', N'test', 7, 0, @schedule, 15)", connection))
            {
                insert.Parameters.AddWithValue("@club", club);
                insert.Parameters.AddWithValue("@schedule", initial);
                await insert.ExecuteNonQueryAsync();
            }

            // Invoke the committed persistence path, below the Azure restart boundary.
            foreach (var (schedule, buffer, restart) in new[]
                { (initial, 17, false), (changed, 18, true), (changed, 19, false) })
            {
                var needsRestart = await AdminSettingsFunction.PasWijzigingenToeAsync(connection,
                    new Dictionary<string, string?>
                    {
                        ["FetchSchedule"] = schedule,
                        ["BufferMinuten"] = buffer.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }, club, "test", new DateOnly(2026, 11, 10));
                needsRestart.Should().Be(restart, "only a change from the stored schedule requires restart");
                await using var read = new SqlCommand(@"
                    SELECT [FetchSchedule], [BufferMinuten] FROM [dbo].[AppSettings]
                    WHERE [ClubCode] = @club", connection);
                read.Parameters.AddWithValue("@club", club);
                await using var reader = await read.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be(schedule);
                reader.GetInt32(1).Should().Be(buffer);
            }
        }
        finally
        {
            await using var cleanup = new SqlCommand(@"
                DELETE FROM [dbo].[AppSettingsAudit] WHERE [ClubCode] = @club;
                DELETE FROM [dbo].[AppSettings] WHERE [ClubCode] = @club", connection);
            cleanup.Parameters.AddWithValue("@club", club);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [SqlServerFact]
    public async Task SqlServer_DryRun_WritesFalseThenTrueAndAuditsBoth()
    {
        var club = "z1819" + Guid.NewGuid().ToString("N")[..8];
        var cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        try
        {
            await using (var insert = new SqlCommand(@"
                INSERT INTO [dbo].[AppSettings]
                    ([ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId], [SeasonStartMonth], [SyncEnabled])
                VALUES (N'AllStars FC', @club, N'https://allstars-fc.test', N'test', 7, 0)", connection))
            {
                insert.Parameters.AddWithValue("@club", club);
                await insert.ExecuteNonQueryAsync();
            }

            foreach (var (requested, expected) in new[] { ("false", false), ("true", true) })
            {
                await AdminSettingsFunction.PasWijzigingenToeAsync(connection,
                    new Dictionary<string, string?> { ["SportlinkDryRun"] = requested },
                    club, "test", new DateOnly(2026, 11, 10));
                await using var read = new SqlCommand(
                    "SELECT [SportlinkDryRun] FROM [dbo].[AppSettings] WHERE [ClubCode] = @club", connection);
                read.Parameters.AddWithValue("@club", club);
                ((bool)(await read.ExecuteScalarAsync())!).Should().Be(expected);
            }

            await using var audit = new SqlCommand(@"
                SELECT COUNT(*) FROM [dbo].[AppSettingsAudit]
                WHERE [ClubCode] = @club AND [Veld] = N'SportlinkDryRun'", connection);
            audit.Parameters.AddWithValue("@club", club);
            ((int)(await audit.ExecuteScalarAsync())!).Should().Be(2);
        }
        finally
        {
            await using var cleanup = new SqlCommand(@"
                DELETE FROM [dbo].[AppSettingsAudit] WHERE [ClubCode] = @club;
                DELETE FROM [dbo].[AppSettings] WHERE [ClubCode] = @club", connection);
            cleanup.Parameters.AddWithValue("@club", club);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
