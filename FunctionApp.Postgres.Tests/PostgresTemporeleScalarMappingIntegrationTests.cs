using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Vangnet voor één specifieke, stille faalvorm: een <c>ExecuteScalar</c>-resultaat dat met
/// <c>is DateTime</c>/<c>is TimeSpan</c> wordt afgetast terwijl Npgsql sinds 10.0 voor
/// <c>DATE</c>- en <c>TIME</c>-kolommen <see cref="DateOnly"/> respectievelijk
/// <see cref="TimeOnly"/> teruggeeft.
///
/// <para>
/// <b>Waarom een eigen testklasse.</b> Bij de upgrade naar Npgsql 10 (#1170) bleken vier van zulke
/// plekken te bestaan. Eén ervan werd gedekt door
/// <c>AvailabilityServiceIntegrationTests.PopulateSunsetTableAsync_EnGetSunsetAsync_RoundTrip</c>
/// en viel dus meteen om; de andere drie hadden geen enkele dekking en gaven geen fout — ze vielen
/// stilzwijgend terug op hun "niet gevonden"-tak. <c>GetSeasonEndWeekOffsetAsync</c> zou dan
/// permanent de standaardwaarde teruggeven en daarmee het synchronisatiebereik verkeerd bepalen,
/// zonder dat er ook maar iets rood werd. Deze klasse dekt juist die drie, zodat de volgende
/// Npgsql-major hier luidruchtig faalt in plaats van in productie stil te vallen.
/// </para>
///
/// <para>
/// Isolatie volgt <see cref="PostgresSeasonHelperKnvbSeizoenIntegrationTests"/>: <c>public.season</c>
/// heeft geen ClubCode-discriminator, dus in plaats van de tabel te legen wordt één rij met een
/// extreem ver-in-de-toekomst <c>dateuntil</c> gezet die <c>MAX(dateuntil)</c> altijd domineert,
/// ongeacht welke echte seizoensrijen aanwezig zijn.
/// </para>
/// </summary>
public class PostgresTemporeleScalarMappingIntegrationTests : IAsyncLifetime
{
    // Max. 9 tekens — public.season.name is VARCHAR(9), zelfde vorm als een echte seizoensnaam.
    private const string DominantSeizoenNaam = "9998/9999";
    private static readonly DateOnly DominantDateFrom = new(9998, 1, 1);
    private static readonly DateOnly DominantDateUntil = new(9999, 6, 30);

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.season WHERE name = @naam", conn);
        cmd.Parameters.AddWithValue("naam", DominantSeizoenNaam);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ZaaiDominantSeizoenAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO public.season (name, datefrom, dateuntil)
            VALUES (@naam, @datefrom, @dateuntil)
            ON CONFLICT (name) DO UPDATE SET datefrom = EXCLUDED.datefrom, dateuntil = EXCLUDED.dateuntil
            """, conn);
        cmd.Parameters.AddWithValue("naam", DominantSeizoenNaam);
        cmd.Parameters.AddWithValue("datefrom", DominantDateFrom.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("dateuntil", DominantDateUntil.ToDateTime(TimeOnly.MinValue));
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task GetSeasonEndDateAsync_LeestDeDateKolom_EnGeeftNietStilNull()
    {
        await ZaaiDominantSeizoenAsync();

        var einde = await PlannerSettingsRepository.GetSeasonEndDateAsync(ConnectionString);

        einde.Should().Be(DominantDateUntil,
            "MAX(dateuntil) is een DATE-kolom; komt hier null uit, dan past de patroonvergelijking "
            + "in de repository niet meer bij het CLR-type dat Npgsql voor DATE teruggeeft");
    }

    [PostgresFact]
    public async Task GetSeasonEndWeekOffsetAsync_BerekentUitDeDateKolom_EnValtNietTerugOpDeStandaard()
    {
        await ZaaiDominantSeizoenAsync();

        var offset = await PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync(NullLogger.Instance);

        var verwacht = (int)Math.Ceiling(
            (DominantDateUntil.ToDateTime(TimeOnly.MinValue) - DateTime.UtcNow.Date).TotalDays / 7.0);
        offset.Should().Be(verwacht,
            "de methode vangt haar eigen fouten af en valt dan terug op een standaardwaarde — "
            + "een verkeerd afgetast scalartype is daardoor onzichtbaar behalve via deze assertie");
    }

    [PostgresFact]
    public async Task GetSeasonStartWeekOffsetAsync_BerekentUitDeDateKolom_EnValtNietTerugOpDeStandaard()
    {
        await ZaaiDominantSeizoenAsync();

        var offset = await PostgresSeasonHelper.GetSeasonStartWeekOffsetAsync(
            DominantDateFrom.Year, NullLogger.Instance);

        var verwacht = (int)Math.Floor(
            (DominantDateFrom.ToDateTime(TimeOnly.MinValue) - DateTime.UtcNow.Date).TotalDays / 7.0);
        offset.Should().Be(verwacht, "zelfde stille terugval als bij GetSeasonEndWeekOffsetAsync");
    }
}
