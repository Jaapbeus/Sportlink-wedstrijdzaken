using Database.Postgres;
using AwesomeAssertions;
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
        new TeambegeleidingRow("Testclub JO13-1", "Onder 13", "Trainer", "Jan de Vries", "trainer@voorbeeld.nl", "onbekend"),
        new TeambegeleidingRow("Testclub JO13-1", "Onder 13", "Leider", "Piet de Jong", "leider@voorbeeld.nl", null),
    ];

    private async Task SeedAppSettingsAsync(NpgsqlConnection connection, string clubCode, bool syncEnabled)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO public.appsettings (clubcode, syncenabled) VALUES (@c, @s)", connection);
        cmd.Parameters.AddWithValue("c", clubCode);
        cmd.Parameters.AddWithValue("s", syncEnabled);
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task ImportAsync_MeerdereClubs_DeleteScopeRaaktAndereClubsNiet()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Bestaande rij van een ANDERE club moet intact blijven na een import voor "testclub".
        await TeambegeleidingImporter.ImportAsync(connection, "andereclub", Fixture, null, "test", CancellationToken.None);
        await TeambegeleidingImporter.ImportAsync(connection, "testclub", Fixture, "fixture.csv", "test", CancellationToken.None);

        await using var countAndere = new NpgsqlCommand(
            "SELECT COUNT(*) FROM avg.teambegeleiding WHERE clubcode = 'andereclub'", connection);
        var aantalAndere = (long)(await countAndere.ExecuteScalarAsync())!;
        aantalAndere.Should().Be(2, "de import voor 'testclub' mag rijen van 'andereclub' niet raken");

        await using var countVrc = new NpgsqlCommand(
            "SELECT COUNT(*) FROM avg.teambegeleiding WHERE clubcode = 'testclub'", connection);
        var aantalVrc = (long)(await countVrc.ExecuteScalarAsync())!;
        aantalVrc.Should().Be(2);
    }

    /// <summary>
    /// Regressietest voor #1132 (bevinding 7 uit #1107): zonder een club-scoped serialisatie kon
    /// Read Committed twee overlappende imports voor dezelfde club allebei laten committen, met
    /// de vereniging van beide batches als resultaat (in plaats van één complete vervanging).
    /// <see cref="TeambegeleidingImporter.ImportAsync"/> neemt nu vóór de DELETE een
    /// <c>pg_advisory_xact_lock</c> op een per-club sleutel, zodat de tweede aanroep wacht tot de
    /// eerste commit of rollbackt en daarna diens rijen ziet.
    /// <para>
    /// Geen deterministische barrière (zoals de #1107-reviewer met een test-only trigger deed) —
    /// in plaats daarvan vijf herhalingen binnen dezelfde testrun, elk met een asserptie die het
    /// eindresultaat exact gelijk eist aan één van de twee ingediende batches. Dat dekt zowel
    /// "geen vereniging" als "geen gedeeltelijke mix" af, en een vlakke race zou bij minstens één
    /// van de vijf pogingen zichtbaar worden.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task ImportAsync_TweeGelijktijdigeImportsZelfdeClub_EindresultaatIsPreciesÉénBatchNooitDeVereniging()
    {
        for (int poging = 0; poging < 5; poging++)
        {
            var batchA = new[]
            {
                new TeambegeleidingRow("Testclub JO13-1", "Onder 13", "Trainer", $"Batch A Trainer {poging}", $"batch-a-{poging}@voorbeeld.nl", null),
            };
            var batchB = new[]
            {
                new TeambegeleidingRow("Testclub JO15-1", "Onder 15", "Trainer", $"Batch B Trainer {poging}", $"batch-b-1-{poging}@voorbeeld.nl", null),
                new TeambegeleidingRow("Testclub JO15-2", "Onder 15", "Leider",  $"Batch B Leider {poging}",  $"batch-b-2-{poging}@voorbeeld.nl", null),
            };

            await using var connectionA = new NpgsqlConnection(ConnectionString);
            await connectionA.OpenAsync();
            await using var connectionB = new NpgsqlConnection(ConnectionString);
            await connectionB.OpenAsync();

            var importA = TeambegeleidingImporter.ImportAsync(connectionA, "testclub", batchA, null, "import-a", CancellationToken.None);
            var importB = TeambegeleidingImporter.ImportAsync(connectionB, "testclub", batchB, null, "import-b", CancellationToken.None);
            await Task.WhenAll(importA, importB);

            await using var verifyConnection = new NpgsqlConnection(ConnectionString);
            await verifyConnection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT naam FROM avg.teambegeleiding WHERE clubcode = 'testclub' ORDER BY naam", verifyConnection);
            await using var reader = await cmd.ExecuteReaderAsync();
            var namen = new List<string>();
            while (await reader.ReadAsync())
                namen.Add(reader.GetString(0));

            var verwachtA = batchA.Select(r => r.Naam!).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var verwachtB = batchB.Select(r => r.Naam!).OrderBy(n => n, StringComparer.Ordinal).ToList();

            var isExactBatchA = namen.SequenceEqual(verwachtA);
            var isExactBatchB = namen.SequenceEqual(verwachtB);

            (isExactBatchA || isExactBatchB).Should().BeTrue(
                $"poging {poging}: resultaat moet exact batch A [{string.Join(", ", verwachtA)}] " +
                $"of batch B [{string.Join(", ", verwachtB)}] zijn, nooit een mix — was [{string.Join(", ", namen)}]");
        }
    }

    [PostgresFact]
    public async Task ImportAsync_TweedeImportZelfdeClub_VervangtOudeRijenVolledig()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await TeambegeleidingImporter.ImportAsync(connection, "testclub", Fixture, null, "test", CancellationToken.None);
        var enkeleRij = new[] { Fixture[0] };
        await TeambegeleidingImporter.ImportAsync(connection, "testclub", enkeleRij, null, "test", CancellationToken.None);

        await using var count = new NpgsqlCommand(
            "SELECT COUNT(*) FROM avg.teambegeleiding WHERE clubcode = 'testclub'", connection);
        var aantal = (long)(await count.ExecuteScalarAsync())!;
        aantal.Should().Be(1, "de tweede import (delete-vóór-insert) moet de eerste volledig vervangen, niet aanvullen");
    }

    [PostgresFact]
    public async Task ImportAsync_SchrijftAuditrijNaarImportLog()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        var result = await TeambegeleidingImporter.ImportAsync(
            connection, "testclub", Fixture, "fixture.csv", "test-runner", CancellationToken.None);

        result.AantalRijen.Should().Be(2);

        await using var cmd = new NpgsqlCommand(
            "SELECT aantalrijen, csvbestand, importerendedoor, clubcode FROM avg.importlog WHERE clubcode = 'testclub'",
            connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("er moet precies één auditrij zijn geschreven");
        reader.GetInt32(0).Should().Be(2);
        reader.GetString(1).Should().Be("fixture.csv");
        reader.GetString(2).Should().Be("test-runner");
    }

    [PostgresFact]
    public async Task ResolveClubCodeAsync_SelecteertAlleenSyncEnabledClub()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // AllStars (demo) staat expliciet UIT voor sync — de echte club staat AAN.
        await SeedAppSettingsAsync(connection, "allstars", syncEnabled: false);
        await SeedAppSettingsAsync(connection, "testclub", syncEnabled: true);

        var resolved = await TeambegeleidingImporter.ResolveClubCodeAsync(connection, null, CancellationToken.None);

        resolved.Should().Be("testclub", "de democlub (syncenabled=false) mag nooit impliciet als doelclub voor échte persoonsgegevens gekozen worden");
    }

    [PostgresFact]
    public async Task ResolveClubCodeAsync_GeenActieveClub_GooitExceptie()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAppSettingsAsync(connection, "allstars", syncEnabled: false);

        var act = async () => await TeambegeleidingImporter.ResolveClubCodeAsync(connection, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [PostgresTheory]
    [InlineData(89, false)]
    [InlineData(91, true)]
    public async Task GetOudsteImportLeeftijdInDagenAsync_StalenessGrensOp90Dagen(int dagenOud, bool verwachtStale)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var insertVrc = new NpgsqlCommand("""
            INSERT INTO avg.teambegeleiding (naam, clubcode, mta_imported)
            VALUES ('Jan de Vries', 'testclub', NOW() - (@dagen || ' days')::interval)
            """, connection))
        {
            insertVrc.Parameters.AddWithValue("dagen", dagenOud);
            await insertVrc.ExecuteNonQueryAsync();
        }

        // Andere club heeft een veel oudere rij — mag de scoping voor 'testclub' niet beïnvloeden.
        await using (var insertAndere = new NpgsqlCommand("""
            INSERT INTO avg.teambegeleiding (naam, clubcode, mta_imported)
            VALUES ('Piet de Jong', 'andereclub', NOW() - INTERVAL '365 days')
            """, connection))
        {
            await insertAndere.ExecuteNonQueryAsync();
        }

        var leeftijd = await TeambegeleidingImporter.GetOudsteImportLeeftijdInDagenAsync(connection, "testclub", CancellationToken.None);

        leeftijd.Should().NotBeNull();
        (leeftijd!.Value > 90).Should().Be(verwachtStale);
    }
}
