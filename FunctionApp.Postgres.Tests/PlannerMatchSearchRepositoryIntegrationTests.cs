using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Planner.Repositories;
using Npgsql;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag van <see cref="PlannerMatchRepository"/>'s vier nieuw vertaalde methoden vast
/// (#888 vervolg): <c>FindMatchAsync</c>, <c>FindMatchByCodeAsync</c>, <c>SavePlannedMatchAsync</c>
/// en <c>SaveHerplanVerzoekAsync</c> — samen genoeg om <c>ZoekWedstrijd</c>, <c>BevestigWedstrijd</c>
/// en <c>HerplanBevestig</c> te wireren.
///
/// <para>
/// <b>Aparte klasse van <see cref="PlannerMatchRepositoryIntegrationTests"/>.</b> Die klasse dekt
/// <c>MarkeerVervallenGeplandeWedstrijdenAsync</c> (#890) en gebruikt een eigen club/opstelling met
/// <see cref="IDisposable"/>-gebaseerde opruiming van de statische <c>PostgresAppSettings</c>-cache.
/// Een andere methode, andere club, andere opstelling — samenvoegen in één klasse zou de bestaande,
/// al bewezen dekking onnodig verstoren.
/// </para>
///
/// <para>
/// <b>Teamresolutie loopt via <c>public.teams</c>/<c>public.teamaliassen</c>, niet <c>his.teams</c>.</b>
/// <c>TeamSchrijfwijzenAsync</c> (private, hergebruikt van #700/#820) herleidt de gevraagde teamnaam
/// eerst tot een canoniek team; zonder een rij in <c>public.teams</c> levert dat altijd een lege
/// schrijfwijzenlijst op en dus <c>null</c> — dat is precies wat <c>FindMatchAsync_OnbekendTeam_GeeftNull</c>
/// hieronder bewijst, en waarom elke positieve test <c>public.teams</c> zelf seedt (de bredere
/// zelftest-harness kan dit niet, vandaar issue #931 — deze integratietest wel).
/// </para>
///
/// <para>
/// <b>Veldnummers 301/302, niet 1/2.</b> <c>public.velden.veldnummer</c> is een kale PK zonder
/// ClubCode-scope (migratie 001) — elke testklasse die tegelijk tegen dezelfde database draait
/// moet dus een eigen, niet-botsende reeks kiezen. Zelfde patroon als
/// <c>006_allstars_demodata.sql</c> (101-103) en <c>PlannerAvailabilityRepositoryIntegrationTests</c>
/// (1-2, met een andere clubcode dan hier).
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class PlannerMatchSearchRepositoryIntegrationTests
{
    private const string Club = "testclub-matchsearch";
    private static readonly DateOnly Zaterdag = new(2026, 9, 5);
    private const string Team = "T-matchsearch JO13-1";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task FindMatchAsync_VindtWedstrijdViaTeamAliasEnBerekentEindtijd()
    {
        await using var conn = await OpstellingAsync();
        var teamId = await ZetTeamAsync(conn, Team);
        await ZetAliasAsync(conn, teamId, "T-matchsearch O13-1"); // KNVB-schrijfwijze, geen J
        await ZetMatchAsync(conn, wedstrijdcode: 9300001, teamnaam: "T-matchsearch O13-1", aanvang: "10:00", status: "Te spelen");

        var match = await PlannerMatchRepository.FindMatchAsync(ConnectionString, Team, Zaterdag, Club);

        match.Should().NotBeNull();
        match!.Wedstrijdcode.Should().Be(9300001);
        match.AanvangsTijd.Should().Be("10:00");
        match.EindTijd.Should().Be("11:00", "Speeltijd JO13 levert 60 minuten wedstrijdduur");
        match.VeldNaam.Should().Be("veld 1", "FindMatchAsync geeft de RUWE Sportlink-veldstring terug, geen geresolveerd veldnummer");
        match.LeeftijdsCategorie.Should().Be("Onder 13");
    }

    [PostgresFact]
    public async Task FindMatchAsync_OnbekendTeam_GeeftNull()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;
        // Bewust GEEN public.teams-rij voor dit team — TeamSchrijfwijzenAsync moet dan een lege
        // lijst opleveren en FindMatchAsync mag niet op een LIKE-patroon terugvallen.
        var match = await PlannerMatchRepository.FindMatchAsync(ConnectionString, "Onbekend Team X", Zaterdag, Club);

        match.Should().BeNull();
    }

    [PostgresFact]
    public async Task FindMatchAsync_GeenSpeeltijdGeconfigureerd_Throws()
    {
        await using var conn = await OpstellingAsync();
        await ZetTeamAsync(conn, Team, leeftijd: "Onder 99"); // geen public.speeltijden-rij hiervoor
        await ZetMatchAsync(conn, wedstrijdcode: 9300002, teamnaam: Team, aanvang: "10:00", status: "Te spelen", leeftijd: "Onder 99");

        var act = () => PlannerMatchRepository.FindMatchAsync(ConnectionString, Team, Zaterdag, Club);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Speelduur niet geconfigureerd*");
    }

    [PostgresFact]
    public async Task FindMatchByCodeAsync_VindtOngeachtStatus()
    {
        await using var conn = await OpstellingAsync();
        await ZetTeamAsync(conn, Team);
        // 'Afgelast' — FindMatchAsync zou dit filteren, FindMatchByCodeAsync bewust niet: het
        // herplanpad moet een bekende wedstrijd ook nog kunnen vinden als de status is gewijzigd.
        await ZetMatchAsync(conn, wedstrijdcode: 9300003, teamnaam: Team, aanvang: "14:00", status: "Afgelast");

        var match = await PlannerMatchRepository.FindMatchByCodeAsync(ConnectionString, 9300003, Club);

        match.Should().NotBeNull();
        match!.AanvangsTijd.Should().Be("14:00");
    }

    [PostgresFact]
    public async Task FindMatchByCodeAsync_OnbekendeCode_GeeftNull()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;
        var match = await PlannerMatchRepository.FindMatchByCodeAsync(ConnectionString, 9999999, Club);
        match.Should().BeNull();
    }

    [PostgresFact]
    public async Task SavePlannedMatchAsync_SlaatAlleKolommenOp()
    {
        await using var conn = await OpstellingAsync();

        var id = await PlannerMatchRepository.SavePlannedMatchAsync(
            ConnectionString,
            Zaterdag, new TimeOnly(15, 0), new TimeOnly(16, 45), veldNummer: 301,
            veldDeelGebruik: 1.00m, leeftijdsCategorie: "JO13", teamNaam: Team,
            tegenstander: "Oefenteam", wedstrijdDuurMinuten: 105, aangevraagdDoor: "Jan de Vries",
            clubCode: Club);

        id.Should().BeGreaterThan(0);

        await using var cmd = new NpgsqlCommand(@"
            SELECT aanvangstijd, eindtijd, veldnummer, velddeelgebruik, leeftijdscategorie,
                   teamnaam, tegenstander, wedstrijdduurminuten, status, aangevraagddoor, clubcode
            FROM planner.geplandewedstrijden WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetTimeSpan(0).Should().Be(new TimeSpan(15, 0, 0));
        reader.GetTimeSpan(1).Should().Be(new TimeSpan(16, 45, 0));
        reader.GetInt32(2).Should().Be(301);
        reader.GetDecimal(3).Should().Be(1.00m);
        reader.GetString(4).Should().Be("JO13");
        reader.GetString(5).Should().Be(Team);
        reader.GetString(6).Should().Be("Oefenteam");
        reader.GetInt32(7).Should().Be(105);
        reader.GetString(8).Should().Be("Te bevestigen", "elke nieuwe geplande wedstrijd start met deze status");
        reader.GetString(9).Should().Be("Jan de Vries");
        reader.GetString(10).Should().Be(Club);
    }

    [PostgresFact]
    public async Task SaveHerplanVerzoekAsync_SlaatVerzoekOpMetClubCode()
    {
        await using var conn = await OpstellingAsync();

        var id = await PlannerMatchRepository.SaveHerplanVerzoekAsync(
            ConnectionString,
            wedstrijdcode: 9300004, huidigeWedstrijd: $"{Team} - Tegenstander",
            huidigeDatum: Zaterdag, huidigeAanvangsTijd: new TimeOnly(10, 0), huidigeVeldNaam: "veld 1",
            gewensteAanvangsTijd: new TimeOnly(12, 0), gewenstVeldNummer: 2,
            aangevraagdDoor: "Jan de Vries", opmerking: "Graag later ivm bezetting",
            clubCode: Club);

        id.Should().BeGreaterThan(0);

        await using var cmd = new NpgsqlCommand(@"
            SELECT wedstrijdcode, huidigewedstrijd, gewensteaanvangstijd, gewenstveldnummer,
                   status, opmerking, clubcode
            FROM planner.herplanverzoeken WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(9300004);
        reader.GetString(1).Should().Be($"{Team} - Tegenstander");
        reader.GetTimeSpan(2).Should().Be(new TimeSpan(12, 0, 0));
        reader.GetInt32(3).Should().Be(2);
        reader.GetString(4).Should().Be("Aangevraagd");
        reader.GetString(5).Should().Be("Graag later ivm bezetting");
        // De regressie die deze test bewaakt: het SQL Server-origineel miste ClubCode volledig in
        // de INSERT terwijl de kolom NOT NULL is zonder DEFAULT (gevonden tijdens deze vertaling,
        // apart gefixt in FunctionApp/Planner/Repositories/PlannerMatchRepository.cs). Zonder deze
        // parameter zou de INSERT hier hard falen in plaats van stilzwijgend NULL op te leveren.
        reader.GetString(6).Should().Be(Club);
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);

        // PostgresClubScope.AddHisParams (his.*-NULL-tolerantie) leest de primaire club uit de
        // procesbrede PostgresAppSettings-cache, niet uit deze test se eigen public.appsettings-rij
        // — die cache wordt normaal ooit gevuld door PostgresAppSettings.LoadSettingsAsync bij
        // functionapp-opstart, wat hier nooit gebeurt. SetForTests is precies hiervoor bedoeld.
        PostgresAppSettings.SetForTests("clubCode", Club);

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var sql in new[]
        {
            "DELETE FROM planner.herplanverzoeken WHERE clubcode = @club",
            "DELETE FROM planner.geplandewedstrijden WHERE clubcode = @club",
            "DELETE FROM his.matches WHERE clubcode = @club",
            // his.teams heeft een business-key-uniqueconstraint op een hardcoded teamcode (zie
            // ZetMatchAsync) — zonder opruimen tussen tests botst de tweede testmethode die een
            // ánder team met hetzelfde teamcode probeert in te voegen op die constraint.
            "DELETE FROM his.teams WHERE clubcode = @club",
            "DELETE FROM public.teamaliassen WHERE clubcode = @club",
            "DELETE FROM public.teams WHERE clubcode = @club",
            "DELETE FROM public.velden WHERE clubcode = @club",
            "DELETE FROM public.speeltijden WHERE clubcode = @club",
            "DELETE FROM public.appsettings WHERE clubcode = @club",
        })
            await ExecAsync(conn, sql, ("club", Club));

        await ExecAsync(conn,
            "INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie, sportlinkapiurl, sportlinkclientid) VALUES (@club, true, 'Sportpark Testclub', 'x', 'x')",
            ("club", Club));
        // Veldnummers 301/302 — zie de klasse-doc-comment over de kale PK op public.velden.veldnummer.
        await ExecAsync(conn, @"
            INSERT INTO public.velden (veldnummer, veldnaam, actief, clubcode, veldtype, heeftkunstlicht)
            VALUES (301, 'Veld 1', true, @club, 'kunstgras', true), (302, 'Veld 2', true, @club, 'natuurgras', false)",
            ("club", Club));
        await ExecAsync(conn,
            "INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, clubcode) VALUES ('JO13', 1.00, 60, @club) ON CONFLICT DO NOTHING",
            ("club", Club));

        return conn;
    }

    private static async Task<int> ZetTeamAsync(NpgsqlConnection conn, string teamnaam, string? leeftijd = null)
    {
        var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(teamnaam, Club);
        var id = (int)(await ScalarAsync(conn, @"
            INSERT INTO public.teams (clubcode, teamnaam, teamnaamgenormaliseerd, leeftijdscategorie, isactief)
            VALUES (@club, @naam, @sleutel, @leeftijd, true) RETURNING teamid",
            ("club", Club), ("naam", teamnaam), ("sleutel", sleutel), ("leeftijd", (object?)leeftijd ?? DBNull.Value)))!;
        return id;
    }

    private static Task ZetAliasAsync(NpgsqlConnection conn, int teamId, string ruweTekst)
    {
        var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruweTekst, Club);
        return ExecAsync(conn, @"
            INSERT INTO public.teamaliassen (clubcode, ruwetekst, ruwetekstgenormaliseerd, teamid, bron, status)
            VALUES (@club, @ruw, @sleutel, @teamid, 'handmatig', 'validated')",
            ("club", Club), ("ruw", ruweTekst), ("sleutel", sleutel), ("teamid", teamId));
    }

    private static async Task ZetMatchAsync(
        NpgsqlConnection conn, long wedstrijdcode, string teamnaam, string aanvang, string status,
        string leeftijd = "Onder 13")
    {
        await ExecAsync(conn, @"
            INSERT INTO his.matches (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status, clubcode, mta_inserted, mta_modified)
            VALUES (@code, '2026-09-05', @aanvang, 'veld 1', @team, @team || ' - Tegenstander', 'Sportpark Testclub', @status, @club, NOW(), NOW())",
            ("code", wedstrijdcode), ("aanvang", aanvang), ("team", teamnaam), ("status", status), ("club", Club));
        // t.[leeftijdscategorie] IS NOT NULL/<>'' filter in FindMatchAsync's LEFT JOIN vereist een
        // his.teams-rij voor dit team — anders blijft de LeeftijdsCategorie/Speeltijden-koppeling weg.
        // Businesskey (teamcode, lokaleteamcode, poulecode) is GEEN ClubCode-gescoped uniqueindex
        // (UQ_his_teams_bk is een gegenereerde kolom over alleen die drie velden, zie
        // Database.Postgres/PostgresSchemaGenerator.cs) — 393 vermijdt de botsing met de teamcodes
        // die andere testklassen gebruiken, xunit draait testklassen parallel.
        await ExecAsync(conn, @"
            INSERT INTO his.teams (teamcode, lokaleteamcode, poulecode, teamnaam, leeftijdscategorie, clubcode, mta_inserted, mta_modified)
            SELECT 393, 393, 393, @team, @leeftijd, @club, NOW(), NOW()
            WHERE NOT EXISTS (SELECT 1 FROM his.teams WHERE teamnaam = @team AND clubcode = @club)",
            ("team", teamnaam), ("leeftijd", leeftijd), ("club", Club));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        return await cmd.ExecuteScalarAsync();
    }
}
