using Database.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Integratietests voor <see cref="TeambegeleidingImporter"/> (#824) — zelfde draaiwijze als
/// <see cref="PostgresMergeOrchestratorIntegrationTests"/> (zie die klasse-doc-comment voor de
/// wegwerpcontainer-instructies).
/// <para>
/// <b>AVG/GDPR:</b> alle testdata hieronder is fictief, conform CLAUDE.md's goedgekeurde
/// uitzonderingen ("Jan de Vries", "trainer@voorbeeld.nl", <c>.test</c>-domeinen) — nooit een echte
/// naam, e-mailadres of telefoonnummer.
/// </para>
/// </summary>
public class TeambegeleidingImporterIntegrationTests : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "POSTGRES_TEST_CONNECTION_STRING";
    private string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionStringEnvVar} niet gezet — zie PostgresMergeOrchestratorIntegrationTests.");

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand("""
            DROP TABLE IF EXISTS avg.teambegeleiding;
            DROP TABLE IF EXISTS avg.importlog;
            DROP TABLE IF EXISTS public.appsettings;
            """, connection);
        await drop.ExecuteNonQueryAsync();

        await using var schema = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS avg;", connection);
        await schema.ExecuteNonQueryAsync();

        await using var appsettings = new NpgsqlCommand(PostgresPlannerSupportSchema.BaselineSql, connection);
        await appsettings.ExecuteNonQueryAsync();

        await using var avgTables = new NpgsqlCommand("""
            CREATE TABLE avg.teambegeleiding (
                id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                team VARCHAR(100) NULL,
                leeftijdscategorieteam VARCHAR(50) NULL,
                teamrol VARCHAR(100) NULL,
                naam VARCHAR(300) NULL,
                emailadres VARCHAR(200) NULL,
                telefoonnummer VARCHAR(50) NULL,
                mta_imported TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                clubcode VARCHAR(20) NOT NULL DEFAULT ''
            );
            CREATE TABLE avg.importlog (
                id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                importdatum TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                aantalrijen INTEGER NOT NULL,
                csvbestand VARCHAR(500) NULL,
                importerendedoor VARCHAR(200) NULL,
                duur_ms INTEGER NULL,
                clubcode VARCHAR(20) NOT NULL DEFAULT ''
            );
            """, connection);
        await avgTables.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly IReadOnlyList<TeambegeleidingRow> Fixture =
    [
        new TeambegeleidingRow("VRC JO13-1", "Onder 13", "Trainer", "Jan de Vries", "trainer@voorbeeld.nl", "0600000001"),
        new TeambegeleidingRow("VRC JO13-1", "Onder 13", "Leider", "Piet de Jong", "leider@voorbeeld.nl", null),
    ];

    private async Task SeedAppSettingsAsync(NpgsqlConnection connection, string clubCode, bool syncEnabled)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO public.appsettings (clubcode, syncenabled) VALUES (@c, @s)", connection);
        cmd.Parameters.AddWithValue("c", clubCode);
        cmd.Parameters.AddWithValue("s", syncEnabled);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task ImportAsync_MeerdereClubs_DeleteScopeRaaktAndereClubsNiet()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Bestaande rij van een ANDERE club moet intact blijven na een import voor "vrc".
        await TeambegeleidingImporter.ImportAsync(connection, "andereclub", Fixture, null, "test", CancellationToken.None);
        await TeambegeleidingImporter.ImportAsync(connection, "vrc", Fixture, "fixture.csv", "test", CancellationToken.None);

        await using var countAndere = new NpgsqlCommand(
            "SELECT COUNT(*) FROM avg.teambegeleiding WHERE clubcode = 'andereclub'", connection);
        var aantalAndere = (long)(await countAndere.ExecuteScalarAsync())!;
        aantalAndere.Should().Be(2, "de import voor 'vrc' mag rijen van 'andereclub' niet raken");

        await using var countVrc = new NpgsqlCommand(
            "SELECT COUNT(*) FROM avg.teambegeleiding WHERE clubcode = 'vrc'", connection);
        var aantalVrc = (long)(await countVrc.ExecuteScalarAsync())!;
        aantalVrc.Should().Be(2);
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task ImportAsync_TweedeImportZelfdeClub_VervangtOudeRijenVolledig()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await TeambegeleidingImporter.ImportAsync(connection, "vrc", Fixture, null, "test", CancellationToken.None);
        var enkeleRij = new[] { Fixture[0] };
        await TeambegeleidingImporter.ImportAsync(connection, "vrc", enkeleRij, null, "test", CancellationToken.None);

        await using var count = new NpgsqlCommand(
            "SELECT COUNT(*) FROM avg.teambegeleiding WHERE clubcode = 'vrc'", connection);
        var aantal = (long)(await count.ExecuteScalarAsync())!;
        aantal.Should().Be(1, "de tweede import (delete-vóór-insert) moet de eerste volledig vervangen, niet aanvullen");
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task ImportAsync_SchrijftAuditrijNaarImportLog()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        var result = await TeambegeleidingImporter.ImportAsync(
            connection, "vrc", Fixture, "fixture.csv", "test-runner", CancellationToken.None);

        result.AantalRijen.Should().Be(2);

        await using var cmd = new NpgsqlCommand(
            "SELECT aantalrijen, csvbestand, importerendedoor, clubcode FROM avg.importlog WHERE clubcode = 'vrc'",
            connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("er moet precies één auditrij zijn geschreven");
        reader.GetInt32(0).Should().Be(2);
        reader.GetString(1).Should().Be("fixture.csv");
        reader.GetString(2).Should().Be("test-runner");
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task ResolveClubCodeAsync_SelecteertAlleenSyncEnabledClub()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // AllStars (demo) staat expliciet UIT voor sync — de echte club staat AAN.
        await SeedAppSettingsAsync(connection, "allstars", syncEnabled: false);
        await SeedAppSettingsAsync(connection, "vrc", syncEnabled: true);

        var resolved = await TeambegeleidingImporter.ResolveClubCodeAsync(connection, null, CancellationToken.None);

        resolved.Should().Be("vrc", "de democlub (syncenabled=false) mag nooit impliciet als doelclub voor échte persoonsgegevens gekozen worden");
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task ResolveClubCodeAsync_GeenActieveClub_GooitExceptie()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAppSettingsAsync(connection, "allstars", syncEnabled: false);

        var act = async () => await TeambegeleidingImporter.ResolveClubCodeAsync(connection, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory(Skip = "Vereist lokale Postgres-instantie (zie PostgresMergeOrchestratorIntegrationTests) — lokaal uitvoeren tegen een wegwerpcontainer")]
    [InlineData(89, false)]
    [InlineData(91, true)]
    public async Task GetOudsteImportLeeftijdInDagenAsync_StalenessGrensOp90Dagen(int dagenOud, bool verwachtStale)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var insertVrc = new NpgsqlCommand("""
            INSERT INTO avg.teambegeleiding (naam, clubcode, mta_imported)
            VALUES ('Jan de Vries', 'vrc', NOW() - (@dagen || ' days')::interval)
            """, connection))
        {
            insertVrc.Parameters.AddWithValue("dagen", dagenOud);
            await insertVrc.ExecuteNonQueryAsync();
        }

        // Andere club heeft een veel oudere rij — mag de scoping voor 'vrc' niet beïnvloeden.
        await using (var insertAndere = new NpgsqlCommand("""
            INSERT INTO avg.teambegeleiding (naam, clubcode, mta_imported)
            VALUES ('Piet de Jong', 'andereclub', NOW() - INTERVAL '365 days')
            """, connection))
        {
            await insertAndere.ExecuteNonQueryAsync();
        }

        var leeftijd = await TeambegeleidingImporter.GetOudsteImportLeeftijdInDagenAsync(connection, "vrc", CancellationToken.None);

        leeftijd.Should().NotBeNull();
        (leeftijd!.Value > 90).Should().Be(verwachtStale);
    }
}
