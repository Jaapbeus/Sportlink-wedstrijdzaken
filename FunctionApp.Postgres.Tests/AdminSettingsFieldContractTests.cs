using AwesomeAssertions;
using Database.Postgres.Tests;
using Npgsql;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Planner;
using Xunit;

namespace FunctionApp.Postgres.Tests;

public class AdminSettingsFieldContractTests
{
    [Fact]
    public void Postgres_AllowsSportlinkDryRunForSettingsUpdates()
        => AdminSettingsFunction.AllowedFields.Should().Contain("SportlinkDryRun");

    [Fact]
    public void Postgres_AllowsClubOptimalisatieAanvangForSettingsUpdates()
        => AdminSettingsFunction.AllowedFields.Should().Contain("OptimalisatieAanvang");

    [PostgresFact]
    public async Task Postgres_OptimalisatieAanvangIsNullableAndClubScoped()
    {
        var clubA = "starta" + Guid.NewGuid().ToString("N")[..8];
        var clubB = "startb" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = new NpgsqlConnection(PostgresTestEnvironment.ConnectionStringOrNull!);
        await connection.OpenAsync();
        try
        {
            await using (var insert = new NpgsqlCommand(@"
                INSERT INTO public.appsettings (clubcode, seasonstartmonth, syncenabled, optimalisatieaanvang)
                VALUES (@a, 7, FALSE, '08:00'), (@b, 7, FALSE, NULL)", connection))
            {
                insert.Parameters.AddWithValue("a", clubA);
                insert.Parameters.AddWithValue("b", clubB);
                await insert.ExecuteNonQueryAsync();
            }

            (await PlannerSettingsRepository.GetOptimalisatieAanvangAsync(
                PostgresTestEnvironment.ConnectionStringOrNull!, clubA)).Should().Be(new TimeOnly(8, 0));
            (await PlannerSettingsRepository.GetOptimalisatieAanvangAsync(
                PostgresTestEnvironment.ConnectionStringOrNull!, clubB)).Should().BeNull();

            await AdminSettingsFunction.ApplyChangesAsync(connection,
                new Dictionary<string, string?> { ["OptimalisatieAanvang"] = null },
                "test", clubA, new DateOnly(2026, 11, 10));
            (await PlannerSettingsRepository.GetOptimalisatieAanvangAsync(
                PostgresTestEnvironment.ConnectionStringOrNull!, clubA)).Should().BeNull();
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(@"
                DELETE FROM public.appsettingsaudit WHERE clubcode IN (@a, @b);
                DELETE FROM public.appsettings WHERE clubcode IN (@a, @b)", connection);
            cleanup.Parameters.AddWithValue("a", clubA);
            cleanup.Parameters.AddWithValue("b", clubB);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Postgres_FetchScheduleRestartFlagUsesStoredValueWhileOtherSettingsPersist()
    {
        var club = "z1819" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = new NpgsqlConnection(
            PostgresTestEnvironment.ConnectionStringOrNull!);
        await connection.OpenAsync();
        const string initial = "0 0 8 * * *";
        const string changed = "0 0 9 * * *";
        try
        {
            await using (var insert = new NpgsqlCommand(@"
                INSERT INTO public.appsettings
                    (clubcode, seasonstartmonth, syncenabled, fetchschedule, bufferminuten)
                VALUES (@club, 7, FALSE, @schedule, 15)", connection))
            {
                insert.Parameters.AddWithValue("@club", club);
                insert.Parameters.AddWithValue("@schedule", initial);
                await insert.ExecuteNonQueryAsync();
            }

            // Invoke the committed persistence path, below the Azure restart boundary.
            foreach (var (schedule, buffer, restart) in new[]
                { (initial, 17, false), (changed, 18, true), (changed, 19, false) })
            {
                var needsRestart = await AdminSettingsFunction.ApplyChangesAsync(connection,
                    new Dictionary<string, string?>
                    {
                        ["FetchSchedule"] = schedule,
                        ["BufferMinuten"] = buffer.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }, "test", club, new DateOnly(2026, 11, 10));
                needsRestart.Should().Be(restart, "only a change from the stored schedule requires restart");
                await using var read = new NpgsqlCommand(@"
                    SELECT fetchschedule, bufferminuten FROM public.appsettings
                    WHERE clubcode = @club", connection);
                read.Parameters.AddWithValue("@club", club);
                await using var reader = await read.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be(schedule);
                reader.GetInt32(1).Should().Be(buffer);
            }
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(@"
                DELETE FROM public.appsettingsaudit WHERE clubcode = @club;
                DELETE FROM public.appsettings WHERE clubcode = @club", connection);
            cleanup.Parameters.AddWithValue("@club", club);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

}
