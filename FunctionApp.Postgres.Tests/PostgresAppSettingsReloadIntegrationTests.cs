using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Regressietest voor #1135 (bevinding 10 in de Codex-review #1107): <see cref="PostgresAppSettings.LoadSettingsAsync"/>
/// liet een instelling die in de database naar NULL werd gezet de oude, cached waarde teruggeven
/// totdat het proces herstartte. <c>ReadAsync</c> wees vóór de fix alleen niet-NULL kolommen toe aan
/// het bestaande, gedeelde dictionary — er was geen enkele stap die een sleutel ooit verwijderde.
///
/// <para>
/// Deze test reproduceert dat met <c>accommodatie</c> (een nullable instelling): eerst een niet-NULL
/// waarde laden, controleren dat <see cref="PostgresAppSettings.GetSetting"/> die teruggeeft, dan de
/// kolom naar NULL zetten en opnieuw laden. Na de fix moet <c>GetSetting("accommodatie")</c> direct
/// <c>null</c> teruggeven en moet <see cref="PostgresAppSettings.LastLoadFailed"/> <c>false</c>
/// blijven — een geslaagde load die velden wist is geen laadfout.
/// </para>
///
/// <para>Zie <see cref="AppSettingsAuditCleanupIntegrationTests"/> voor hetzelfde
/// delete-then-insert-precedent op <c>public.appsettings</c> en <see cref="PostgresSyncFixtureIntegrationTests"/>
/// voor de lokale containeropzet.</para>
/// </summary>
public class PostgresAppSettingsReloadIntegrationTests
{
    private const string TestClub = "zreloadtest1135";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task Reload_AccommodatieNaarNullGezet_GeeftDirectNullTerugEnLastLoadFailedBlijftFalse()
    {
        await using var conn = await OpenAsync();
        await ZetPrimaireClubAsync(conn, accommodatie: "Sportpark De Testweide");

        PostgresAppSettings.ResetForTests();
        await PostgresAppSettings.LoadSettingsAsync(NullLogger.Instance);

        PostgresAppSettings.LastLoadFailed.Should().BeFalse("de eerste load is geslaagd");
        PostgresAppSettings.GetSetting("accommodatie").Should().Be("Sportpark De Testweide",
            "de eerste load moet de niet-NULL waarde in de cache zetten");

        await ZetAccommodatieAsync(conn, null);
        await PostgresAppSettings.LoadSettingsAsync(NullLogger.Instance);

        PostgresAppSettings.LastLoadFailed.Should().BeFalse(
            "een instelling die naar NULL is gezet, is geen laadfout — de load zelf slaagt gewoon");
        PostgresAppSettings.GetSetting("accommodatie").Should().BeNullOrEmpty(
            "de herlaad moet de oude waarde direct loslaten in plaats van te blijven teruggeven tot een procesherstart (#1135)");
        // Een andere, niet-gewijzigde kolom in dezelfde rij bewijst dat de volledige snapshot nog
        // klopt — dit is geen bijwerking die per ongeluk ook andere instellingen wist.
        PostgresAppSettings.GetSetting("clubCode").Should().Be(TestClub);
    }

    // ── hulpjes ────────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// Zelfde delete-then-insert-precedent als <see cref="AppSettingsAuditCleanupIntegrationTests"/>:
    /// <c>public.appsettings</c> volledig legen en één rij met <c>syncenabled = true</c> terugzetten,
    /// zodat <c>ReadAsync</c>'s <c>WHERE syncenabled = true ORDER BY clubcode LIMIT 1</c> ondubbelzinnig
    /// deze testrij oppikt, ongeacht wat andere (parallelle-uitgeschakelde) tests achterlaten.
    /// </summary>
    private static async Task ZetPrimaireClubAsync(NpgsqlConnection conn, string accommodatie)
    {
        await using (var del = new NpgsqlCommand("DELETE FROM public.appsettings", conn))
            await del.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie)
              VALUES (@club, TRUE, @accommodatie)", conn);
        cmd.Parameters.AddWithValue("club", TestClub);
        cmd.Parameters.AddWithValue("accommodatie", accommodatie);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ZetAccommodatieAsync(NpgsqlConnection conn, string? accommodatie)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE public.appsettings SET accommodatie = @accommodatie WHERE clubcode = @club", conn);
        cmd.Parameters.AddWithValue("club", TestClub);
        cmd.Parameters.AddWithValue("accommodatie", (object?)accommodatie ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
