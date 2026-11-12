using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.Planner;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1911: de speeltijden-lookup van de Planning las <c>standaardvoorkeurtijd</c> (Postgres <c>time</c>) met een harde
/// cast naar <see cref="TimeSpan"/>, terwijl Npgsql 10 een <see cref="TimeOnly"/> levert. Alleen een test tegen de
/// echte driver bewijst de typemapping; daarom een integratietest met een rij mét voorkeurstijd.
/// </summary>
public class PlannerSpeeltijdenLookupIntegrationTests
{
    private const string ClubCode = "tijd1911test";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    [PostgresFact]
    public async Task GetSpeeltijdenLookupAsync_RijMetVoorkeurstijd_LeestTimeKolomZonderCastFout()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Voer(conn, "DELETE FROM public.speeltijden WHERE clubcode = @cc");
        await Voer(conn,
            @"INSERT INTO public.speeltijden
                (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, clubcode, standaardvoorkeurtijd)
              VALUES ('JO9', 0.5, 60, 30, 10, @cc, '10:30'), ('JO11', 0.5, 70, 35, 10, @cc, NULL)");

        var lookup = await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(ConnectionString, ClubCode);

        lookup["JO9"].StandaardVoorkeurTijd.Should().Be(new TimeOnly(10, 30));
        lookup["JO11"].StandaardVoorkeurTijd.Should().BeNull();

        await Voer(conn, "DELETE FROM public.speeltijden WHERE clubcode = @cc");
    }

    [PostgresFact]
    public async Task ProbeSpeeltijdenLeesPadAsync_RijMetVoorkeurstijd_GeeftTrue()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Voer(conn, "DELETE FROM public.speeltijden WHERE clubcode = @cc");
        await Voer(conn,
            @"INSERT INTO public.speeltijden
                (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, clubcode, standaardvoorkeurtijd)
              VALUES ('JO9', 0.5, 60, 30, 10, @cc, '10:30')");

        (await PlannerSettingsRepository.ProbeSpeeltijdenLeesPadAsync(ConnectionString, CancellationToken.None))
            .Should().BeTrue("de leesproef leest een rij mét voorkeurstijd door de echte typemapping");

        await Voer(conn, "DELETE FROM public.speeltijden WHERE clubcode = @cc");
    }

    [Fact]
    public async Task ProbeSpeeltijdenLeesPadAsync_OnbereikbareDatabase_GeeftFoutDoorAanHealth()
    {
        // Health vangt dit en meldt readPathsOk=false (degraded); de probe zelf slikt nooit een fout in.
        var act = () => PlannerSettingsRepository.ProbeSpeeltijdenLeesPadAsync(
            "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2", CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    private static async Task Voer(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("cc", ClubCode);
        await cmd.ExecuteNonQueryAsync();
    }
}
