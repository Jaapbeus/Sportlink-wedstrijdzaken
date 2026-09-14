using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt vast dat <see cref="PostgresAppSettings.LoadSettingsAsync"/> sinds #1141 ook
/// <c>knvbpdfbijlageingeschakeld</c> en <c>knvbstandaardregio</c> in de instellingencache laadt —
/// nodig voor het "verzet zonder datum"-pad (#561) in de echte, mailbox-getriggerde verwerking
/// (<c>BerichtPipeline.BouwVerzetZonderDatumResponseAsync</c> leest deze via
/// <see cref="PostgresAppSettings.GetSetting"/> zodra er geen <c>ClubAppSettingsSnapshot</c> is).
///
/// <para>
/// Beide kolommen bestaan onvoorwaardelijk sinds migratie 003 (<c>knvbpdfbijlageingeschakeld
/// BOOLEAN NOT NULL DEFAULT TRUE</c>, <c>knvbstandaardregio VARCHAR(20) NULL</c>) — geen
/// optionele-kolom-dans zoals bij <c>sportlinkextensionenabled</c>/<c>sportlinkdryrun</c> nodig.
/// </para>
///
/// <para>Zelfde delete-then-insert-precedent op <c>public.appsettings</c> als
/// <see cref="PostgresAppSettingsReloadIntegrationTests"/>; zie <see cref="PostgresSyncFixtureIntegrationTests"/>
/// voor de lokale containeropzet.</para>
/// </summary>
public class PostgresAppSettingsKnvbColumnsIntegrationTests
{
    private const string TestClub = "zknvbcols1141";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task Laadt_KnvbPdfBijlageIngeschakeldEnStandaardRegio()
    {
        await using var conn = await OpenAsync();
        await ZetPrimaireClubAsync(conn, knvbPdfBijlageIngeschakeld: true, knvbStandaardRegio: "West");

        PostgresAppSettings.ResetForTests();
        await PostgresAppSettings.LoadSettingsAsync(NullLogger.Instance);

        PostgresAppSettings.LastLoadFailed.Should().BeFalse();
        PostgresAppSettings.GetSetting("knvbPdfBijlageIngeschakeld").Should().Be("1");
        PostgresAppSettings.GetSetting("knvbStandaardRegio").Should().Be("West");
    }

    [PostgresFact]
    public async Task KnvbPdfBijlageUitgeschakeld_GeeftNul()
    {
        await using var conn = await OpenAsync();
        await ZetPrimaireClubAsync(conn, knvbPdfBijlageIngeschakeld: false, knvbStandaardRegio: "Noord");

        PostgresAppSettings.ResetForTests();
        await PostgresAppSettings.LoadSettingsAsync(NullLogger.Instance);

        PostgresAppSettings.GetSetting("knvbPdfBijlageIngeschakeld").Should().Be("0");
    }

    /// <summary>
    /// Ontbrekende regio (NULL) is het bewuste, gedocumenteerde fallbackpad (#561/#1141): de
    /// aanroeper (<c>BerichtPipeline.BouwVerzetZonderDatumResponseAsync</c>) valt dan terug op het
    /// standaard herplanpad — expliciet gelogd, niet stilzwijgend. Deze test bewijst alleen de
    /// datalaag-kant: <see cref="PostgresAppSettings.GetSetting"/> geeft <c>null</c> terug in plaats
    /// van een geraden of hardcoded regio.
    /// </summary>
    [PostgresFact]
    public async Task OntbrekendeRegio_GeeftNullNooitEenGeradenWaarde()
    {
        await using var conn = await OpenAsync();
        await ZetPrimaireClubAsync(conn, knvbPdfBijlageIngeschakeld: true, knvbStandaardRegio: null);

        PostgresAppSettings.ResetForTests();
        await PostgresAppSettings.LoadSettingsAsync(NullLogger.Instance);

        PostgresAppSettings.GetSetting("knvbStandaardRegio").Should().BeNull();
    }

    // ── hulpjes ────────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ZetPrimaireClubAsync(
        NpgsqlConnection conn, bool knvbPdfBijlageIngeschakeld, string? knvbStandaardRegio)
    {
        await using (var del = new NpgsqlCommand("DELETE FROM public.appsettings", conn))
            await del.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.appsettings (clubcode, syncenabled, knvbpdfbijlageingeschakeld, knvbstandaardregio)
              VALUES (@club, TRUE, @bijlage, @regio)", conn);
        cmd.Parameters.AddWithValue("club", TestClub);
        cmd.Parameters.AddWithValue("bijlage", knvbPdfBijlageIngeschakeld);
        cmd.Parameters.AddWithValue("regio", (object?)knvbStandaardRegio ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
