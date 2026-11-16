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

            var andereOntvanger = new Dictionary<string, string?>
                { ["EmailReviewOntvanger"] = "trainer@allstars-fc.test" };
            await AdminSettingsFunction.ApplyChangesAsync(connection, andereOntvanger,
                "test", club, new DateOnly(2026, 11, 16));

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
                SELECT veld, oudewaarde, nieuwewaarde FROM public.appsettingsaudit
                WHERE clubcode = @club ORDER BY id", connection);
            audit.Parameters.AddWithValue("club", club);
            await using var reader = await audit.ExecuteReaderAsync();
            var rows = new List<(string Veld, string? Oud, string? Nieuw)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
            rows.Select(row => row.Veld).Should().Equal(
                "EmailReviewOntvanger", "EmailReviewOntvanger", "EmailReviewModusActief", "EmailReviewUitschakelingBevestigd");
            rows[0].Nieuw.Should().Be("[ingesteld]");
            rows[1].Oud.Should().Be("[ingesteld]");
            rows[1].Nieuw.Should().Be("[ingesteld]");
            rows[2].Oud.Should().Be("1");
            rows[2].Nieuw.Should().Be("0");
            rows.SelectMany(row => new[] { row.Oud, row.Nieuw }).Should().NotContain(new[] { "trainer@voorbeeld.nl", "trainer@allstars-fc.test" });
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
