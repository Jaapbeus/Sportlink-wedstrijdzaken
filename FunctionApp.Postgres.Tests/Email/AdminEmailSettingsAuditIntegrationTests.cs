using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Admin;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

public sealed class AdminEmailSettingsAuditIntegrationTests
{
    [PostgresFact]
    public async Task EmailPut_AuditAlleenWerkelijkeVeldenEnBevestiging()
    {
        var club = "mailaudit" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = new NpgsqlConnection(PostgresTestEnvironment.ConnectionStringOrNull!);
        await connection.OpenAsync();
        try
        {
            await using (var insert = new NpgsqlCommand(@"
                INSERT INTO public.appsettings
                    (clubcode, seasonstartmonth, syncenabled, emailverwerkingingeschakeld,
                     emailreviewmodusactief, emailinstellingengeimporteerd)
                VALUES (@club, 7, FALSE, FALSE, TRUE, TRUE)", connection))
            {
                insert.Parameters.AddWithValue("club", club);
                await insert.ExecuteNonQueryAsync();
            }

            (await EmailSettingsAdminSupport.EmailKolommenBeschikbaarAsync(connection)).Should().BeTrue();
            var alleenOntvanger = new Dictionary<string, string?>
                { ["EmailReviewOntvanger"] = "trainer@voorbeeld.nl" };
            await AdminSettingsFunction.ApplyChangesAsync(connection, alleenOntvanger,
                "test", club, new DateOnly(2026, 11, 16));
            alleenOntvanger.Keys.Should().ContainSingle().Which.Should().Be("EmailReviewOntvanger");

            var ongewijzigd = new Dictionary<string, string?>
                { ["EmailVerwerkingIngeschakeld"] = "false" };
            await AdminSettingsFunction.ApplyChangesAsync(connection, ongewijzigd,
                "test", club, new DateOnly(2026, 11, 16));
            ongewijzigd.Should().BeEmpty();

            var bevestiging = new Dictionary<string, string?>
                { ["EmailReviewModusActief"] = "false" };
            await AdminSettingsFunction.ApplyChangesAsync(connection, bevestiging,
                "test", club, new DateOnly(2026, 11, 16), bevestigReviewUitzetten: true);

            await using var audit = new NpgsqlCommand(@"
                SELECT veld, nieuwewaarde FROM public.appsettingsaudit
                WHERE clubcode = @club ORDER BY id", connection);
            audit.Parameters.AddWithValue("club", club);
            await using var reader = await audit.ExecuteReaderAsync();
            var rows = new List<(string Veld, string? Waarde)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            rows.Select(row => row.Veld).Should().Equal(
                "EmailReviewOntvanger", "EmailReviewModusActief", "EmailReviewUitschakelingBevestigd");
            rows[0].Waarde.Should().Be("[ingesteld]");
            rows.Select(row => row.Waarde).Should().NotContain("trainer@voorbeeld.nl");
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(@"
                DELETE FROM public.appsettingsaudit WHERE clubcode = @club;
                DELETE FROM public.appsettings WHERE clubcode = @club", connection);
            cleanup.Parameters.AddWithValue("club", club);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
