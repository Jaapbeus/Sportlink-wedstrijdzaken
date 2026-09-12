using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Regressietest voor #1077: <see cref="PostgresStagingRepository.MergeUitslagenAsync"/> noemde
/// <c>@clubcode</c> in zijn INSERT maar bond die parameter nooit.
///
/// <para>
/// <b>Waarom de bestaande dekking dit acht dagen liet lopen.</b>
/// <see cref="PostgresSyncFixtureIntegrationTests"/> draait het volledige synchronisatiepad, maar
/// zijn fixture levert uitslagen voor wedstrijden die de programma-fetch al in <c>stg.matches</c>
/// heeft gezet. De UPDATE raakt dan een rij, de methode doet <c>continue</c>, en het INSERT-pad —
/// het enige pad waarin <c>@clubcode</c> voorkomt — wordt nooit uitgevoerd. In productie is dat
/// pad juist de regel: voor voorbije weken staat er nog niets in staging, dus daar viel elke run
/// door naar de INSERT en faalde.
/// </para>
///
/// <para>
/// Deze test dwingt dat pad rechtstreeks af: een verse <c>stg.matches</c>, een wedstrijd die daar
/// niet in staat, en een datum in het verleden (toekomstige wedstrijden slaat de methode bewust
/// over). Zonder de fix faalt hij met
/// <c>42703: column "clubcode" does not exist</c> — de misleidende melding die ontstaat doordat
/// PostgreSQL <c>@</c> als prefix-operator leest zodra de placeholder ongebonden blijft.
/// </para>
/// </summary>
public class UitslagenMergeIntegrationTests
{
    private const string ClubCode = "testclub-uitslagen";
    private const long Wedstrijdcode = 90000777;

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException("POSTGRES_TEST_CONNECTION_STRING ontbreekt.");

    [PostgresFact]
    public async Task MergeUitslagenAsync_VoegtInAlsDeWedstrijdNogNietInStagingStaat()
    {
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.RecreateStgTableAsync(KnownEntities.Matches);

        var match = GespeeldeWedstrijd();

        var verwerkt = await PostgresStagingRepository.MergeUitslagenAsync(
            ConnectionString, [match], ClubCode, NullLogger.Instance);

        verwerkt.Should().Be(1, "de wedstrijd stond nog niet in staging, dus het INSERT-pad hoort te lopen");

        var (clubcode, uitslag) = await LeesRijAsync();
        clubcode.Should().Be(ClubCode, "de clubcode-parameter moet daadwerkelijk gebonden zijn (#1077)");
        uitslag.Should().Be("3-1");
    }

    /// <summary>
    /// Tegenhanger: staat de rij er al, dan werkt de UPDATE hem bij en blijft de clubcode staan.
    /// Zonder deze tweede meting zou de eerste niet onderscheiden of de INSERT of de UPDATE liep.
    /// </summary>
    [PostgresFact]
    public async Task MergeUitslagenAsync_WerktBestaandeRijBijZonderClubcodeTeVerliezen()
    {
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.RecreateStgTableAsync(KnownEntities.Matches);

        var match = GespeeldeWedstrijd();
        await PostgresStagingRepository.MergeUitslagenAsync(
            ConnectionString, [match], ClubCode, NullLogger.Instance);

        match.uitslag = "4-2";
        var verwerkt = await PostgresStagingRepository.MergeUitslagenAsync(
            ConnectionString, [match], ClubCode, NullLogger.Instance);

        verwerkt.Should().Be(1);
        var (clubcode, uitslag) = await LeesRijAsync();
        clubcode.Should().Be(ClubCode);
        uitslag.Should().Be("4-2");
    }

    private static Match GespeeldeWedstrijd() => new()
    {
        // Ruim in het verleden: MergeUitslagenAsync slaat toekomstige wedstrijden bewust over,
        // zodat /uitslagen nooit een nog te spelen wedstrijd kan "voorspellen".
        wedstrijddatum = "2020-01-04T14:30:00.0000000",
        wedstrijdcode = Wedstrijdcode,
        wedstrijdnummer = Wedstrijdcode,
        datum = "04-01-2020",
        wedstrijd = "Testclub 1 - Bezoekers 1",
        thuisteam = "Testclub 1",
        uitteam = "Bezoekers 1",
        status = "Uitslag",
        uitslag = "3-1",
        competitienaam = "Testcompetitie",
    };

    private static async Task<(string? Clubcode, string? Uitslag)> LeesRijAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT clubcode, uitslag FROM stg.matches WHERE wedstrijdcode = @code", conn);
        cmd.Parameters.AddWithValue("code", Wedstrijdcode);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
