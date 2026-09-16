using Database.Postgres;
using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Sync;
using FunctionApp.Tests.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Postgres-tegenhanger van <c>FunctionApp.Tests/Sync/SportlinkFixtureSyncIntegrationTests.cs</c>
/// (#867) — het eerste acceptatiecriterium van issue 890: *"een
/// <c>SportlinkFixtureSyncIntegrationTests</c>-equivalent draait tegen de Postgres-tier en levert
/// dezelfde uitkomst (identieke rijaantallen, geen dubbele <c>mta_modified</c>-updates bij een
/// herhaalde run)"*.
///
/// <para>
/// <b>Waarom dit als blijvende test bestaat en niet opnieuw als wegwerpharnas.</b> Zowel #905
/// (deel 1) als #912 (deel 2) verifieerden dit pad met een console-project dat na afloop werd
/// weggegooid — reproduceerbaar op het moment zelf, maar niets bewaakte daarna nog tegen regressie.
/// Deze klasse is dezelfde meting, alleen blijvend en env-gestuurd (#866).
/// </para>
///
/// <para>
/// <b>Lokaal draaien</b> tegen een wegwerpcontainer — dezelfde opzet als de CI-job
/// <c>fresh-db-postgres</c> in <c>.github/workflows/build.yml</c>:
/// </para>
/// <code>
/// docker run -d --name pgfixture -e POSTGRES_PASSWORD=devonly -e POSTGRES_DB=sportlink -p 55432:5432 postgres:16
/// $env:POSTGRES_CONNECTION_STRING = "Host=localhost;Port=55432;Database=sportlink;Username=postgres;Password=devonly"
/// dotnet run --project Database.Postgres.Cli
/// $env:POSTGRES_TEST_CONNECTION_STRING = $env:POSTGRES_CONNECTION_STRING
/// dotnet test FunctionApp.Postgres.Tests
/// docker rm -f pgfixture
/// </code>
///
/// <para>
/// <b>"Geen enkele externe dienst geraakt" — bewezen, niet aangenomen (#867).</b>
/// <see cref="PostgresSyncPipeline.RunSyncAsync"/> krijgt <c>sportlinkApiUrl</c> uitsluitend als
/// parameter; deze test geeft daar het adres van <see cref="SportlinkFixtureServer"/> aan mee en
/// controleert achteraf welke paden die server daadwerkelijk binnenkreeg.
/// </para>
///
/// <para>
/// <b>Eén structureel verschil met de SQL Server-tegenhanger, bewust:</b> die leest <c>clubCode</c>
/// uit een procesbrede statische cache en moet die dus via <c>SetForTests</c> zetten en opruimen.
/// <see cref="PostgresSyncPipeline.RunSyncAsync"/> neemt <c>clubCode</c> en
/// <c>connectionString</c> als expliciete parameters — geen globale toestand, dus ook niets om op
/// te ruimen.
/// </para>
/// </summary>
public class PostgresSyncFixtureIntegrationTests
{
    private const string ClubCode = "testclub-sync";
    private const long Wedstrijdcode = 90000001;

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task RunSyncAsync_TegenFixtureserver_IsIdempotentEnRaaktUitsluitendDeFixture()
    {
        await SchoonAsync();

        using var fixtureServer = SportlinkFixtures.BuildServer(Wedstrijdcode, ClubCode);

        // Kleine week-range: minimaliseert het aantal fixture-aanroepen zonder de aard van de test te
        // veranderen (elke weekoffset krijgt toch hetzelfde canned antwoord).
        await RunAsync(fixtureServer);

        // Bewijs 1: uitsluitend de fixture is geraakt, en wel op de verwachte endpoints.
        fixtureServer.Requests.Should().Contain(r => r.StartsWith("/teams"));
        fixtureServer.Requests.Should().Contain(r => r.StartsWith("/programma"));
        fixtureServer.Requests.Should().Contain(r => r.StartsWith("/uitslagen"));
        fixtureServer.Requests.Should().Contain(
            r => r.StartsWith("/wedstrijd-informatie") && r.Contains(Wedstrijdcode.ToString()));

        // Bewijs 2: het team, de wedstrijd en de matchdetails staan daadwerkelijk in his.*.
        var eersteModified = await ScalarAsync<DateTime?>(
            "SELECT mta_modified FROM his.matches WHERE wedstrijdcode = @code AND clubcode = @club");
        eersteModified.Should().NotBeNull("de wedstrijd uit /programma moet na de eerste sync in his.matches staan");

        (await CountAsync("SELECT count(*) FROM his.teams WHERE clubcode = @club"))
            .Should().Be(1, "het ene team uit /teams moet na de eerste sync in his.teams staan");
        (await CountAsync("SELECT count(*) FROM his.matchdetails WHERE wedstrijdcode = @code AND clubcode = @club"))
            .Should().Be(1, "de matchdetails uit /wedstrijd-informatie moeten na de eerste sync in his.matchdetails staan");

        // Bewijs 3 (het kernacceptatiecriterium): tweemaal draaien tegen identieke fixture-data levert
        // geen duplicaten op en verandert mta_modified niet voor ongewijzigde rijen.
        await Task.Delay(50); // zorg dat NOW() aantoonbaar vooruit kán gaan tussen de twee runs
        fixtureServer.Requests.Clear();

        await RunAsync(fixtureServer);

        (await ScalarAsync<DateTime?>(
                "SELECT mta_modified FROM his.matches WHERE wedstrijdcode = @code AND clubcode = @club"))
            .Should().Be(eersteModified,
                "identieke brondata mag mta_modified niet bijwerken — PostgresUpsertGenerator werkt, net als "
                + "sp_MergeStgToHis op de SQL Server-tier, alleen bij daadwerkelijk gewijzigde kolommen bij");

        (await CountAsync("SELECT count(*) FROM his.teams WHERE clubcode = @club"))
            .Should().Be(1, "een tweede run met identieke data mag geen duplicaatrij toevoegen");
        (await CountAsync("SELECT count(*) FROM his.matches WHERE wedstrijdcode = @code AND clubcode = @club"))
            .Should().Be(1, "een tweede run met identieke data mag geen duplicaatrij toevoegen");
        (await CountAsync("SELECT count(*) FROM his.matchdetails WHERE wedstrijdcode = @code AND clubcode = @club"))
            .Should().Be(1, "een tweede run met identieke data mag geen duplicaatrij toevoegen");
    }

    /// <summary>
    /// De sync roept aan het eind <c>TeamCanonicalisatieService</c> aan (#889/§28) — best-effort,
    /// dus een fout daar is stil. Deze test bewijst dat die stap in dit pad écht draait en niet
    /// stilzwijgend faalt: na de sync moet het team uit de fixture ook als canoniek team én als
    /// gevalideerde alias in <c>public.teams</c>/<c>public.teamaliassen</c> staan.
    /// <para>
    /// Zonder deze assertie zou een gebroken canonicalisatie onzichtbaar zijn — precies het soort
    /// stille regressie dat de try/catch eromheen mogelijk maakt.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task RunSyncAsync_VultOokDeCanoniekeTeamlijst()
    {
        await SchoonAsync();

        using var fixtureServer = SportlinkFixtures.BuildServer(Wedstrijdcode, ClubCode);
        await RunAsync(fixtureServer);

        var teamnaam = await ScalarAsync<string?>(
            "SELECT teamnaam FROM his.teams WHERE clubcode = @club LIMIT 1");
        teamnaam.Should().NotBeNullOrWhiteSpace("de fixture levert een team in his.teams");

        (await CountAsync("SELECT count(*) FROM public.teams WHERE clubcode = @club AND isactief = TRUE"))
            .Should().BeGreaterThan(0,
                "de best-effort teamcanonicalisatie aan het eind van de sync moet public.teams gevuld hebben; "
                + "is deze nul, dan is die stap stilzwijgend gefaald (hij zit in een try/catch)");

        (await CountAsync(
                "SELECT count(*) FROM public.teamaliassen WHERE clubcode = @club AND status = 'validated'"))
            .Should().BeGreaterThan(0,
                "elke bronschrijfwijze hoort als gevalideerde Sync-alias vastgelegd te worden (#700)");
    }

    /// <summary>
    /// Bewijst dat de sync de plannerview aanmaakt (#861).
    ///
    /// <para>
    /// <b>De bug die deze test bewaakt.</b> <c>planner.alle_wedstrijden_op_veld_ruw</c> werd door
    /// géén migratie en géén applicatiecode aangemaakt — alleen door de testsuites zelf. Op een
    /// verse installatie ontbrak de view dus volledig, en elk endpoint dat eruit leest
    /// (veldbezetting, check-availability, doordeweeks-beschikbaar, herplan-check, auto-plan) faalde
    /// met <c>42P01: relation does not exist</c>. Empirisch bevestigd op een verse database vóór de
    /// fix: <c>to_regclass</c> gaf <c>NULL</c>.
    /// </para>
    ///
    /// <para>
    /// De view wordt hier eerst expliciet <b>gedropt</b>. Zonder die stap zou de assertie ook slagen
    /// op een view die een andere testklasse toevallig had aangemaakt — dan bewijst hij niets over
    /// de pipeline.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task RunSyncAsync_MaaktDePlannerviewAan()
    {
        await SchoonAsync();

        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP VIEW IF EXISTS {PostgresPlannerViewGenerator.ViewName}", conn);
            await drop.ExecuteNonQueryAsync();
        }

        (await BestaatDeViewAsync())
            .Should().BeFalse("de view moet vóór de sync echt weg zijn, anders bewijst deze test niets");

        using var fixtureServer = SportlinkFixtures.BuildServer(Wedstrijdcode, ClubCode);
        await RunAsync(fixtureServer);

        (await BestaatDeViewAsync())
            .Should().BeTrue(
                "de sync hoort de plannerview aan te maken; zonder view faalt de halve plannerlaag met 42P01");

        // En hij moet ook echt bevraagbaar zijn — een view die bestaat maar niet te lezen is,
        // helpt de plannerlaag niets.
        (await CountAsync(
                $"SELECT count(*) FROM {PostgresPlannerViewGenerator.ViewName} WHERE clubcode = @club"))
            .Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// #1193, het kernacceptatiecriterium: een wedstrijd die op een volgende sync niet meer in de
    /// Sportlink-respons zit, moet daarna ook niet meer in de planner-resultaten verschijnen.
    /// <para>
    /// Simuleert dit door de fixture na de eerste (succesvolle) sync om te zetten naar een
    /// <c>/programma</c>/<c>/uitslagen</c>-antwoord dat een ANDERE wedstrijd op dezelfde datum
    /// bevat, maar niet meer <see cref="Wedstrijdcode"/> — exact zoals een club die één wedstrijd in
    /// Sportlink annuleert terwijl de rest van het weekend gewoon doorgaat. Bewust niet volledig
    /// leeg: de reconciliatiestap leidt het datumvenster af uit de MIN/MAX-datum die déze sync-run
    /// in <c>stg</c> laadt (<see cref="Database.Postgres.PostgresMergeOrchestrator.ReconcileWindowedAsync"/>),
    /// en die afleiding is met opzet onmogelijk wanneer <c>stg</c> voor de club volledig leeg is —
    /// zie de xml-doc van die methode. Deze test bewijst dus het gangbare pad (een club heeft
    /// zelden een compleet wedstrijdloos venster); de lege-stg-situatie zelf is los gedekt door
    /// <c>Database.Postgres.Tests</c>.
    /// </para>
    /// <para>
    /// De wedstrijd moet na de tweede sync (a) in <c>his.matches</c> blijven bestaan met
    /// <c>mta_deleted</c> gezet (audit-trail, geen hard delete) en (b) niet meer voldoen aan
    /// <see cref="PostgresClubScope.HisFilter"/>, het predicaat dat vrijwel elke lezende
    /// <c>PlannerMatchRepository</c>-query gebruikt — dus ook niet meer terugkomen in enig
    /// planner-resultaat.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task RunSyncAsync_WedstrijdVerdwijntUitTweedeFixtureRespons_WordtGereconciliëerdEnVerdwijntUitPlannerresultaten()
    {
        await SchoonAsync();

        using var fixtureServer = SportlinkFixtures.BuildServer(Wedstrijdcode, ClubCode);
        await RunAsync(fixtureServer);

        (await IsZichtbaarViaHisFilterAsync()).Should().BeTrue(
            "na de eerste sync moet de wedstrijd via het planner-predicaat zichtbaar zijn");
        (await ScalarAsync<DateTime?>(
                "SELECT mta_deleted FROM his.matches WHERE wedstrijdcode = @code AND clubcode = @club"))
            .Should().BeNull("nog niet gereconcilieerd — de wedstrijd stond in beide sync-runs");

        // Sportlink levert deze ene wedstrijd niet meer, maar wel nog een andere op dezelfde datum
        // (zie klasse-doc-comment hierboven voor waarom dit venstertechnisch nodig is).
        const long andereWedstrijdcode = Wedstrijdcode + 1;
        fixtureServer.RespondWithJson("/programma", AndereWedstrijdJson(andereWedstrijdcode));
        fixtureServer.RespondWithJson("/uitslagen", "[]");
        await RunAsync(fixtureServer);

        (await ScalarAsync<DateTime?>(
                "SELECT mta_deleted FROM his.matches WHERE wedstrijdcode = @code AND clubcode = @club"))
            .Should().NotBeNull(
                "de reconciliatiestap moet de wedstrijd als verdwenen markeren zodra hij niet meer in de sync zit");

        (await IsZichtbaarViaHisFilterAsync()).Should().BeFalse(
            "een zacht-verwijderde wedstrijd hoort niet meer via het planner-predicaat gevonden te worden");

        // Audit-trail, geen hard delete: de rij zelf blijft gewoon bestaan.
        (await CountAsync("SELECT count(*) FROM his.matches WHERE wedstrijdcode = @code AND clubcode = @club"))
            .Should().Be(1, "his is een audit-trail — de rij mag nooit fysiek verwijderd worden");
    }

    /// <summary>
    /// Minimale <c>/programma</c>-respons met precies één wedstrijd, op dezelfde datum als de
    /// standaardfixture (2026-09-05) maar met een andere <c>wedstrijdcode</c> en team — voor het
    /// "één wedstrijd verdwijnt, de rest van het weekend niet"-scenario hierboven.
    /// </summary>
    private static string AndereWedstrijdJson(long wedstrijdcode) => $$"""
        [
          {
            "wedstrijddatum": "2026-09-05T12:00:00+0200",
            "wedstrijdcode": {{wedstrijdcode}},
            "wedstrijdnummer": 19781,
            "teamnaam": "{{ClubCode}} JO13-2",
            "thuisteam": "{{ClubCode}} JO13-2",
            "uitteam": "Andere Tegenstander",
            "teamvolgorde": 1,
            "competitiesoort": "regulier",
            "kaledatum": "2026-09-05 00:00:00.00",
            "datum": "05 sep.",
            "aanvangstijd": "12:00",
            "wedstrijd": "{{ClubCode}} JO13-2 - Andere Tegenstander",
            "status": "Te spelen",
            "accommodatie": "Sportpark Oost",
            "veld": "veld 4",
            "locatie": "Veld",
            "plaats": "TESTSTAD",
            "meer": "wedstrijd-informatie?wedstrijdcode={{wedstrijdcode}}"
          }
        ]
        """;

    /// <summary>Bevraagt <c>his.matches</c> met exact hetzelfde predicaat als
    /// <see cref="PostgresClubScope.HisFilter"/> — de plek waar vrijwel elke
    /// <c>PlannerMatchRepository</c>-query zijn club-/zacht-verwijderd-filtering vandaan haalt.
    /// <para>
    /// Zet <c>@primaireclubcode</c>/<c>@clubcode</c> hier bewust rechtstreeks in plaats van via
    /// <see cref="PostgresClubScope.AddHisParams"/>: die laatste leest de primaire club via
    /// <see cref="PostgresClubScope.Primary"/> uit een procesbrede cache
    /// (<c>PostgresAppSettings</c>) die alleen gevuld is binnen de draaiende Function-host, niet in
    /// deze losstaande testrun. Voor deze query maakt de waarde toch niet uit: de fixture-rij heeft
    /// altijd een niet-NULL <c>clubcode</c>, dus <c>COALESCE(m.clubcode, @primaireclubcode)</c> kiest
    /// hier sowieso nooit de fallback.
    /// </para>
    /// </summary>
    private static async Task<bool> IsZichtbaarViaHisFilterAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"
            SELECT count(*) FROM his.matches m
            WHERE m.wedstrijdcode = @code AND {PostgresClubScope.HisFilter("m")}
        ", conn);
        cmd.Parameters.AddWithValue("code", Wedstrijdcode);
        cmd.Parameters.AddWithValue("clubcode", ClubCode);
        cmd.Parameters.AddWithValue("primaireclubcode", ClubCode);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static async Task RunAsync(SportlinkFixtureServer fixtureServer) =>
        await PostgresSyncPipeline.RunSyncAsync(
            fromWeekOffset: 0, toWeekOffset: 0,
            sportlinkApiUrl: fixtureServer.BaseUrl,
            sportlinkClientId: "clientId=fixture-test",
            clubCode: ClubCode,
            connectionString: ConnectionString,
            log: NullLogger.Instance);

    /// <summary>
    /// Schone lei voor deze club. <c>his.*</c> blijft tussen runs staan (alleen <c>stg.*</c> wordt
    /// hermaakt), en de tabellen bestaan op een verse database pas ná de eerste
    /// <see cref="PostgresMergeOrchestrator.EnsureHisTableAsync"/> — vandaar
    /// <c>DELETE ... WHERE to_regclass(...) IS NOT NULL</c> als voorwaardelijke variant van de
    /// SQL Server-tegenhanger se <c>IF OBJECT_ID(...) IS NOT NULL</c>. Alles is hard gescoped op
    /// deze testclub, zodat de demodata die de CI-job vlak hiervoor asserteert onaangeroerd blijft.
    /// </summary>
    private static async Task SchoonAsync()
    {
        // Eerst de vorm, dan de inhoud: een andere testsuite kan his.* in een afwijkende vorm hebben
        // achtergelaten — zie HisTabelVorm voor de gemeten aanleiding.
        await HisTabelVorm.ZorgVoorProductievormAsync(
            ConnectionString, KnownEntities.Teams, KnownEntities.Matches, KnownEntities.MatchDetails);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var opruimen = new NpgsqlCommand(
            "DELETE FROM his.matchdetails WHERE clubcode = @club; " +
            "DELETE FROM his.matches WHERE clubcode = @club; " +
            "DELETE FROM his.teams WHERE clubcode = @club; " +
            "DELETE FROM public.teamaliassen WHERE clubcode = @club; " +
            "DELETE FROM public.teams WHERE clubcode = @club;", conn);
        opruimen.Parameters.AddWithValue("club", ClubCode);
        await opruimen.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string sql) => await ScalarAsync<long>(sql);

    /// <summary>
    /// <c>to_regclass</c> geeft <c>NULL</c> als het object niet bestaat. De <c>IS NOT NULL</c> zit
    /// bewust in SQL en niet in C#: dan komt er altijd een bool terug en hoeft de client het
    /// <c>regclass</c>-OID-type niet te mappen.
    /// </summary>
    private static async Task<bool> BestaatDeViewAsync() =>
        await ScalarAsync<bool>(
            $"SELECT to_regclass('{PostgresPlannerViewGenerator.ViewName}') IS NOT NULL");

    private static async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("club", ClubCode);
        cmd.Parameters.AddWithValue("code", Wedstrijdcode);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}
