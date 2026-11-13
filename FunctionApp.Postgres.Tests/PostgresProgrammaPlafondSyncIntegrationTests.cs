using AwesomeAssertions;
using Database.Postgres;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Sync;
using FunctionApp.Tests.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1915: de volledige sync (<see cref="PostgresSyncPipeline.RunSyncAsync"/>) tegen een fixtureserver die zich gedraagt als
/// Sportlinks <c>/programma</c>: maximaal 100 rijen per aanroep, oplopend in tijd, en de filters <c>thuis</c>/<c>uit</c>.
/// Bewijst dat een drukke speeldag volledig in <c>his.matches</c> komt en dat een afgekapte respons nooit tot
/// "verwijderd" leidt — de oorzaak van de verdwenen wedstrijden. Testdata synthetisch (eigen testclub).
/// </summary>
public class PostgresProgrammaPlafondSyncIntegrationTests : IAsyncLifetime
{
    private const string ClubCode = "tc-plafond";
    private const long Basis = 91000000;

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    private const string Datum = "2026-09-05";

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Testdata blijft niet achter in de gedeelde testdatabase: andere tests leggen constraints op his.teams.</summary>
    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(PostgresTestEnvironment.ConnectionStringOrNull)) await SchoonAsync();
    }

    private static SportlinkFixtureServer Dataservice(IReadOnlyList<PlafondDataservice.Wed> dag, bool negeertFilters = false)
        => PlafondDataservice.Bouw(Basis, ClubCode, Datum, dag, negeertFilters);

    private static Task RunAsync(SportlinkFixtureServer server) =>
        PostgresSyncPipeline.RunSyncAsync(
            fromWeekOffset: 0, toWeekOffset: 0, sportlinkApiUrl: server.BaseUrl, sportlinkClientId: "clientId=fixture-test",
            clubCode: ClubCode, connectionString: ConnectionString, log: NullLogger.Instance,
            reconciliatieOndergrens: new DateOnly(2026, 9, 1));

    [PostgresFact]
    public async Task DrukkeSpeeldag_MeerDanHonderdWedstrijden_KomtVolledigInHisMatches()
    {
        await SchoonAsync();
        var dag = PlafondDataservice.Dag(Basis, 130);
        using var server = Dataservice(dag);

        await RunAsync(server);

        (await TelAsync("mta_deleted IS NULL")).Should().Be(130, "alle wedstrijden van de dag, niet alleen de eerste 100");
        server.Requests.Should().Contain(r => r.StartsWith("/programma") && r.Contains("thuis=JA&uit=NEE"));
        server.Requests.Should().Contain(r => r.StartsWith("/programma") && r.Contains("thuis=NEE&uit=JA"));
    }

    [PostgresFact]
    public async Task EenVolgendeSync_BlijftSluitend_GeenWedstrijdWordtVerwijderd()
    {
        await SchoonAsync();
        var dag = PlafondDataservice.Dag(Basis, 130);
        using var server = Dataservice(dag);

        await RunAsync(server);
        await RunAsync(server);

        (await TelAsync("mta_deleted IS NULL")).Should().Be(130);
        (await TelAsync("mta_deleted IS NOT NULL")).Should().Be(0);
    }

    /// <summary>De oorspronkelijke fout: een afgekapte respons mag nooit leiden tot "verwijderd".</summary>
    [PostgresFact]
    public async Task EenAfgekapteRespons_ZonderWerkendeFilters_VerwijdertNiets()
    {
        await SchoonAsync();
        var dag = PlafondDataservice.Dag(Basis, 130);
        using (var volledig = Dataservice(dag)) await RunAsync(volledig);
        (await TelAsync("mta_deleted IS NULL")).Should().Be(130);

        using var afgekapt = Dataservice(dag, negeertFilters: true);
        await RunAsync(afgekapt);

        (await TelAsync("mta_deleted IS NOT NULL")).Should().Be(0,
            "de run is onvolledig en reconcilieert niet; de wedstrijden na de 100e blijven staan");
        (await TelAsync("mta_deleted IS NULL")).Should().Be(130);
    }

    /// <summary>
    /// De zwaarste week: zowel de thuis- als de uitverzameling heeft meer dan 100 wedstrijden, dus alleen per team (teamcode) is
    /// alles binnen te halen. Zo zag de eerste live proef van #1915 een deel van de weken uitkomen.
    /// </summary>
    [PostgresFact]
    public async Task ZeerDrukkeWeek_ThuisEnUitBeideBoven100_KomtVolledigBinnenViaDeTeams()
    {
        await SchoonAsync();
        using var server = Dataservice(PlafondDataservice.Dag(Basis, 300));

        await RunAsync(server);

        (await TelAsync("mta_deleted IS NULL")).Should().Be(300, "alle wedstrijden, ook als thuis en uit elk al boven het plafond zitten");
        server.Requests.Should().Contain(r => r.StartsWith("/programma") && r.Contains("teamcode="));
    }

    /// <summary>Controle: de reconciliatie is niet uitgezet. Een volledige respons waarin een wedstrijd ontbreekt verwijdert hem wél.</summary>
    [PostgresFact]
    public async Task EenVolledigeResponsZonderEenWedstrijd_MarkeertDieWedstrijdAlsVerwijderd()
    {
        await SchoonAsync();
        var dag = PlafondDataservice.Dag(Basis, 90);
        using (var eerste = Dataservice(dag)) await RunAsync(eerste);

        using var tweede = Dataservice(dag.Where(w => w.Code != Basis + 7).ToList());
        await RunAsync(tweede);

        (await TelAsync("mta_deleted IS NOT NULL")).Should().Be(1);
        (await TelAsync($"mta_deleted IS NOT NULL AND wedstrijdcode = {Basis + 7}")).Should().Be(1);
    }

    /// <summary>
    /// De eigen regels van de Planning dragen de koppelsleutels: het bondswedstrijdnummer uit de sync, en een
    /// <c>PublicMatchId</c> alleen als de cache het geverifieerd heeft (<c>publicmatchid = verifiedpublicmatchid</c>). Een
    /// ongeverifieerd of afwijkend ID telt niet mee — de verouderde cache veroorzaakt geen verkeerde koppeling.
    /// </summary>
    [PostgresFact]
    public async Task EigenRegels_DragenHetWedstrijdnummer_EnAlleenHetGeverifieerdePublicMatchId()
    {
        await SchoonAsync();
        using (var server = Dataservice(PlafondDataservice.Dag(Basis, 6))) await RunAsync(server);
        await ExecAsync("INSERT INTO public.appsettings (clubcode, accommodatie) VALUES (@club, 'Sportpark Oost')");
        await ExecAsync("INSERT INTO public.sportlinkpublicmatchidcache (wedstrijdcode, clubcode, publicmatchid, verifiedpublicmatchid) VALUES " +
                        $"({Basis + 1}, @club, 'P1', 'P1'), ({Basis + 2}, @club, 'P2', NULL), ({Basis + 3}, @club, 'P3', 'p3')");

        var regels = await AllstarsTestDataRepository.GetAllMatchesForDatumAsync(ConnectionString, new DateOnly(2026, 9, 5), ClubCode);

        regels.Should().HaveCount(6);
        regels.Select(r => r.Wedstrijdnummer).Should().OnlyContain(n => n >= 500001 && n <= 500006);
        regels.Single(r => r.WedstrijdCode == Basis + 1).PublicMatchId.Should().Be("P1");
        regels.Single(r => r.WedstrijdCode == Basis + 2).PublicMatchId.Should().BeNull("de cache is niet geverifieerd");
        regels.Single(r => r.WedstrijdCode == Basis + 3).PublicMatchId.Should().BeNull("het geverifieerde ID wijkt af");
        regels.Single(r => r.WedstrijdCode == Basis + 4).PublicMatchId.Should().BeNull("geen cache-regel");
    }

    private static async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("club", ClubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> TelAsync(string voorwaarde)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM his.matches WHERE clubcode = @club AND {voorwaarde}", conn);
        cmd.Parameters.AddWithValue("club", ClubCode);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task SchoonAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(
            ConnectionString, KnownEntities.Teams, KnownEntities.Matches, KnownEntities.MatchDetails);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var opruimen = new NpgsqlCommand(
            "DELETE FROM his.matchdetails WHERE clubcode = @club; DELETE FROM his.matches WHERE clubcode = @club; " +
            "DELETE FROM his.teams WHERE clubcode = @club; DELETE FROM public.teamaliassen WHERE clubcode = @club; " +
            "DELETE FROM public.sportlinkpublicmatchidcache WHERE clubcode = @club; DELETE FROM public.appsettings WHERE clubcode = @club; " +
            "DELETE FROM public.teams WHERE clubcode = @club;", conn);
        opruimen.Parameters.AddWithValue("club", ClubCode);
        await opruimen.ExecuteNonQueryAsync();
    }
}
