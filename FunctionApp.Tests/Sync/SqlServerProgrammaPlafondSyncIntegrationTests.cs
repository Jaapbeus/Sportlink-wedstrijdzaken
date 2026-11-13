using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// #1915: SQL Server-tegenhanger van <c>PostgresProgrammaPlafondSyncIntegrationTests</c> — de volledige sync tegen een
/// fixtureserver die zich gedraagt als Sportlinks <c>/programma</c> (maximaal 100 rijen, filters thuis/uit). Draait alleen met
/// <c>SQLSERVER_TEST_CONNECTION_STRING</c> én <c>SqlConnectionString</c> (dezelfde waarde; de pipeline leest de tweede bij het
/// opstarten) tegen een wegwerpdatabase met het schema uit <c>Database/Script.PostDeployment1.sql</c>. De datum ligt in de
/// toekomst, omdat de reconciliatie van deze tier altijd vanaf morgen werkt. Testdata synthetisch.
/// </summary>
public class SqlServerProgrammaPlafondSyncIntegrationTests : IAsyncLifetime
{
    private const string ClubCode = "tc-plafond";
    private const long Basis = 91000000;
    private const string Datum = "2030-09-07";

    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException($"{SqlServerFactAttribute.EnvVar} niet gezet.");

    public Task InitializeAsync()
    {
        SystemUtilities.AppSettings.SetForTests("clubCode", ClubCode);
        return Task.CompletedTask;
    }

    /// <summary>Testdata blijft niet achter in de gedeelde testdatabase: andere tests leggen constraints op his.teams.</summary>
    public async Task DisposeAsync()
    {
        SystemUtilities.AppSettings.ResetForTests();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar))) await SchoonAsync();
    }

    private static SportlinkFixtureServer Dataservice(IReadOnlyList<PlafondDataservice.Wed> dag, bool negeertFilters = false)
        => PlafondDataservice.Bouw(Basis, ClubCode, Datum, dag, negeertFilters);

    private static Task RunAsync(SportlinkFixtureServer server) =>
        SportlinkSyncPipeline.RunSyncAsync(
            fromWeekOffset: 0, toWeekOffset: 0, sportlinkApiUrl: server.BaseUrl, sportlinkClientId: "clientId=fixture-test",
            log: NullLogger.Instance);

    [SqlServerFact]
    public async Task DrukkeSpeeldag_MeerDanHonderdWedstrijden_KomtVolledigInHisMatches()
    {
        await SchoonAsync();
        using var server = Dataservice(PlafondDataservice.Dag(Basis, 130));

        await RunAsync(server);

        (await TelAsync("[mta_deleted] IS NULL")).Should().Be(130, "alle wedstrijden van de dag, niet alleen de eerste 100");
        server.Requests.Should().Contain(r => r.StartsWith("/programma") && r.Contains("thuis=JA&uit=NEE"));
        server.Requests.Should().Contain(r => r.StartsWith("/programma") && r.Contains("thuis=NEE&uit=JA"));
    }

    [SqlServerFact]
    public async Task EenVolgendeSync_BlijftSluitend_GeenWedstrijdWordtVerwijderd()
    {
        await SchoonAsync();
        using var server = Dataservice(PlafondDataservice.Dag(Basis, 130));

        await RunAsync(server);
        await RunAsync(server);

        (await TelAsync("[mta_deleted] IS NULL")).Should().Be(130);
        (await TelAsync("[mta_deleted] IS NOT NULL")).Should().Be(0);
    }

    /// <summary>De oorspronkelijke fout: een afgekapte respons mag nooit leiden tot "verwijderd".</summary>
    [SqlServerFact]
    public async Task EenAfgekapteRespons_ZonderWerkendeFilters_VerwijdertNiets()
    {
        await SchoonAsync();
        var dag = PlafondDataservice.Dag(Basis, 130);
        using (var volledig = Dataservice(dag)) await RunAsync(volledig);
        (await TelAsync("[mta_deleted] IS NULL")).Should().Be(130);

        using var afgekapt = Dataservice(dag, negeertFilters: true);
        await RunAsync(afgekapt);

        (await TelAsync("[mta_deleted] IS NOT NULL")).Should().Be(0,
            "de run is onvolledig en reconcilieert niet; de wedstrijden na de 100e blijven staan");
        (await TelAsync("[mta_deleted] IS NULL")).Should().Be(130);
    }

    /// <summary>
    /// De zwaarste week: zowel de thuis- als de uitverzameling heeft meer dan 100 wedstrijden, dus alleen per team (teamcode) is
    /// alles binnen te halen. Zo zag de eerste live proef van #1915 een deel van de weken uitkomen.
    /// </summary>
    [SqlServerFact]
    public async Task ZeerDrukkeWeek_ThuisEnUitBeideBoven100_KomtVolledigBinnenViaDeTeams()
    {
        await SchoonAsync();
        using var server = Dataservice(PlafondDataservice.Dag(Basis, 300));

        await RunAsync(server);

        (await TelAsync("[mta_deleted] IS NULL")).Should().Be(300, "alle wedstrijden, ook als thuis en uit elk al boven het plafond zitten");
        server.Requests.Should().Contain(r => r.StartsWith("/programma") && r.Contains("teamcode="));
    }

    /// <summary>Controle: de reconciliatie is niet uitgezet. Een volledige respons waarin een wedstrijd ontbreekt verwijdert hem wél.</summary>
    [SqlServerFact]
    public async Task EenVolledigeResponsZonderEenWedstrijd_MarkeertDieWedstrijdAlsVerwijderd()
    {
        await SchoonAsync();
        var dag = PlafondDataservice.Dag(Basis, 90);
        using (var eerste = Dataservice(dag)) await RunAsync(eerste);

        using var tweede = Dataservice(dag.Where(w => w.Code != Basis + 7).ToList());
        await RunAsync(tweede);

        (await TelAsync("[mta_deleted] IS NOT NULL")).Should().Be(1);
        (await TelAsync($"[mta_deleted] IS NOT NULL AND [wedstrijdcode] = {Basis + 7}")).Should().Be(1);
    }

    private static async Task<int> TelAsync(string voorwaarde)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM [his].[matches] WHERE [ClubCode] = @club AND {voorwaarde}", conn);
        cmd.Parameters.AddWithValue("@club", ClubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>his.* bestaat op een verse database pas na de eerste merge; elke DELETE is dus voorwaardelijk.</summary>
    private static async Task SchoonAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var opruimen = new SqlCommand(
            "IF OBJECT_ID('[his].[matchdetails]') IS NOT NULL DELETE FROM [his].[matchdetails] WHERE [ClubCode] = @club; " +
            "IF OBJECT_ID('[his].[matches]') IS NOT NULL DELETE FROM [his].[matches] WHERE [ClubCode] = @club; " +
            "IF OBJECT_ID('[his].[teams]') IS NOT NULL DELETE FROM [his].[teams] WHERE [ClubCode] = @club;", conn);
        opruimen.Parameters.AddWithValue("@club", ClubCode);
        await opruimen.ExecuteNonQueryAsync();
    }
}
