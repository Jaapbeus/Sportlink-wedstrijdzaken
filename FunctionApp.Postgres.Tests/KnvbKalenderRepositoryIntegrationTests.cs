using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.Planner.Repositories;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag vast van <see cref="KnvbKalenderRepository.GetVrijeZaterdagenAsync"/> (#1141) —
/// de Postgres-vertaling van <c>FunctionApp/Planner/KnvbKalenderRepository.cs</c> die
/// <c>BerichtPipeline</c>'s "verzet zonder datum"-pad (#561) van vrije-zaterdagen-suggesties
/// voorziet vanuit <c>public.knvbkalenderdag</c> (migratie 019).
///
/// <para>
/// Gebruikt een fictief seizoen ("2099/2100") dat niet in de echte KNVB-seed (migratie 019, 423
/// rijen voor 2025/2026 en 2026/2027) voorkomt — zo blijven deze tests onafhankelijk van die data
/// en van elkaar, ondanks dat de tabel geen ClubCode-discriminator heeft (landelijke KNVB-data,
/// net als het SQL Server-origineel).
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class KnvbKalenderRepositoryIntegrationTests : IAsyncLifetime
{
    private const string Seizoen = "2099/2100";
    private const string Regio = "West";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    public async Task InitializeAsync() => await OpruimenAsync();
    public async Task DisposeAsync() => await OpruimenAsync();

    private static async Task OpruimenAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.knvbkalenderdag WHERE seizoen = @seizoen", conn);
        cmd.Parameters.AddWithValue("seizoen", Seizoen);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ZetDagAsync(string regio, DateOnly datum, string dagtype)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO public.knvbkalenderdag
                (seizoen, regio, datum, dagtype, heeftsenioren, heeftjeugd, heeftmeiden, pupillentoernooi)
            VALUES (@seizoen, @regio, @datum, @dagtype, TRUE, TRUE, FALSE, FALSE)
            ON CONFLICT (seizoen, regio, datum) DO NOTHING
            """, conn);
        cmd.Parameters.AddWithValue("seizoen", Seizoen);
        cmd.Parameters.AddWithValue("regio", regio);
        cmd.Parameters.AddWithValue("datum", datum.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("dagtype", dagtype);
        await cmd.ExecuteNonQueryAsync();
    }

    // Drie opeenvolgende zaterdagen ver in het fictieve seizoen 2099/2100 (geverifieerd: 5, 12 en
    // 19 september 2099 vallen alle drie op een zaterdag).
    private static readonly DateOnly Zaterdag1 = new(2099, 9, 5);
    private static readonly DateOnly Zaterdag2 = new(2099, 9, 12);
    private static readonly DateOnly Zaterdag3 = new(2099, 9, 19);

    [PostgresFact]
    public async Task FiltertOpDagtype_AlleenCompetitieBekerInhaal()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");
        await ZetDagAsync(Regio, Zaterdag2, "Vrij");
        await ZetDagAsync(Regio, Zaterdag3, "NC");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            ConnectionString, Regio, Seizoen, Zaterdag1, Zaterdag3, new HashSet<DateOnly>(), maxAantal: 5);

        resultaat.Should().ContainSingle().Which.Should().Be(Zaterdag1,
            "alleen Competitie/Beker/Inhaal tellen als 'vrije zaterdag'-kandidaat — Vrij/NC niet");
    }

    [PostgresFact]
    public async Task SluitReedsBezetteDatumUit()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");
        await ZetDagAsync(Regio, Zaterdag2, "Beker");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            ConnectionString, Regio, Seizoen, Zaterdag1, Zaterdag2,
            new HashSet<DateOnly> { Zaterdag1 }, maxAantal: 5);

        resultaat.Should().ContainSingle().Which.Should().Be(Zaterdag2,
            "een datum waarop ons team al een wedstrijd heeft, is geen vrije zaterdag");
    }

    [PostgresFact]
    public async Task BegrensdMaxAantal()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");
        await ZetDagAsync(Regio, Zaterdag2, "Competitie");
        await ZetDagAsync(Regio, Zaterdag3, "Competitie");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            ConnectionString, Regio, Seizoen, Zaterdag1, Zaterdag3, new HashSet<DateOnly>(), maxAantal: 2);

        resultaat.Should().HaveCount(2);
        resultaat.Should().BeInAscendingOrder();
    }

    [PostgresFact]
    public async Task OnbekendeRegio_GeeftLegeLijst()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            ConnectionString, "OnbekendeRegio", Seizoen, Zaterdag1, Zaterdag1, new HashSet<DateOnly>(), maxAantal: 5);

        resultaat.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task LegeRegioOfSeizoen_GeeftLegeLijstZonderQuery()
    {
        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            ConnectionString, "", Seizoen, Zaterdag1, Zaterdag1, new HashSet<DateOnly>(), maxAantal: 5);

        resultaat.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task MaxAantalNulOfNegatief_GeeftLegeLijst()
    {
        await ZetDagAsync(Regio, Zaterdag1, "Competitie");

        var resultaat = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            ConnectionString, Regio, Seizoen, Zaterdag1, Zaterdag1, new HashSet<DateOnly>(), maxAantal: 0);

        resultaat.Should().BeEmpty();
    }
}
