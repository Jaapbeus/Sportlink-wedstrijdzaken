using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Planner.Repositories;
using Npgsql;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag van <see cref="PlannerAvailabilityRepository"/> en
/// <see cref="TeamRulesRepository"/> vast (#888).
///
/// <para>
/// <b>De kern van deze klasse is niet "komt er data terug" maar "wordt de veldstring correct
/// geresolveerd".</b> <c>planner.alle_wedstrijden_op_veld_ruw</c> levert voor
/// gesynchroniseerde wedstrijden bewust de RUWE Sportlink-veldstring terug (<c>veld_ruw</c>), geen
/// veldnummer — de repository moet die zelf resolveren via
/// <see cref="Planner.Shared.PlannerShared.VindVeldNummer"/> (#819's architectuurbeslissing, zie
/// <see cref="Database.Postgres.PostgresPlannerViewGenerator"/>). Een porteerfout hier laat een
/// wedstrijd stil uit de bezetting vallen — geen foutmelding, gewoon een leeg antwoord dat leest
/// als "veld is vrij" terwijl het bezet is.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class PlannerAvailabilityRepositoryIntegrationTests
{
    private const string Club = "testclub-avail";
    private static readonly DateOnly Zaterdag = new(2026, 9, 5);

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task GetFieldOccupationsAsync_ResolveertRuweVeldstringNaarVeldnummer()
    {
        await using var conn = await OpstellingAsync();
        await ZetCompetitieWedstrijdAsync(conn, wedstrijdcode: 9200001, veld: "veld 1", aanvang: "10:00");

        var bezetting = await PlannerAvailabilityRepository.GetFieldOccupationsAsync(ConnectionString, Zaterdag, Club);

        bezetting.Should().ContainSingle(b => b.Bron == "Competitie")
            .Which.Should().Match<BestaandeWedstrijd>(b =>
                b.VeldNummer == 1 && b.Wedstrijdcode == 9200001 && b.AanvangsTijd == new TimeOnly(10, 0));
    }

    [PostgresFact]
    public async Task GetFieldOccupationsAsync_OnresolveerbareVeldstring_ValtStilWeg()
    {
        await using var conn = await OpstellingAsync();
        // Geen enkel veld in public.velden heet zo — de SQL Server-tegenhanger filtert dit met
        // WHERE v.VeldNummer IS NOT NULL; hier gebeurt dat filter in C# ná resolutie.
        await ZetCompetitieWedstrijdAsync(conn, wedstrijdcode: 9200002, veld: "onbestaand veld X", aanvang: "10:00");

        var bezetting = await PlannerAvailabilityRepository.GetFieldOccupationsAsync(ConnectionString, Zaterdag, Club);

        bezetting.Should().BeEmpty("een onresolveerbare veldstring mag niet als 'veld 0' of als crash verschijnen");
    }

    [PostgresFact]
    public async Task GetFieldOccupationsAsync_CombineertCompetitiePlannerEnTraining()
    {
        await using var conn = await OpstellingAsync();
        await ZetCompetitieWedstrijdAsync(conn, wedstrijdcode: 9200003, veld: "veld 1", aanvang: "10:00");
        await ZetGeplandeWedstrijdAsync(conn, veldnummer: 2, aanvang: "11:00");
        await ZetTrainingAsync(conn, veldnummer: 1, van: "18:00", tot: "19:00");

        var bezetting = await PlannerAvailabilityRepository.GetFieldOccupationsAsync(ConnectionString, Zaterdag, Club);

        bezetting.Select(b => b.Bron).Should().BeEquivalentTo(new[] { "Competitie", "Planner", "Training" });
    }

    [PostgresFact]
    public async Task GetFieldOccupationsExcludingAsync_SluitAlleenExacteWedstrijdcodeUit()
    {
        await using var conn = await OpstellingAsync();
        await ZetCompetitieWedstrijdAsync(conn, wedstrijdcode: 9200004, veld: "veld 1", aanvang: "10:00");
        await ZetCompetitieWedstrijdAsync(conn, wedstrijdcode: 39200004, veld: "veld 2", aanvang: "12:00");

        var bezetting = await PlannerAvailabilityRepository.GetFieldOccupationsExcludingAsync(
            ConnectionString, Zaterdag, excludeWedstrijdcode: 9200004, Club);

        bezetting.Should().ContainSingle(b => b.Wedstrijdcode == 39200004,
            "9200004 moet weg, maar 39200004 (bevat '9200004' als tekst) moet blijven — geen contains-match op de code");
    }

    [PostgresFact]
    public async Task GetAvailableFieldsAsync_RespecteertActieveVeldperiode()
    {
        await using var conn = await OpstellingAsync();
        // Democlub-precedent (#581): een actieve periode maakt het standaardregime (periodeid IS
        // NULL) ONZICHTBAAR — géén samenvoeging. Zaterdag 05-09 valt binnen deze periode.
        await ExecAsync(conn, @"
            INSERT INTO public.veldperiode (naam, datumvan, datumtot, actief, clubcode)
            VALUES ('Zomerstop', '2026-09-01', '2026-09-10', true, @club)", ("club", Club));
        var periodeId = (int)(await ScalarAsync(conn, "SELECT id FROM public.veldperiode WHERE clubcode = @club", ("club", Club)))!;
        await ExecAsync(conn, @"
            INSERT INTO public.veldbeschikbaarheid (veldnummer, dagvanweek, beschikbaarvanaf, beschikbaartot, gebruikzonsondergang, clubcode, periodeid)
            VALUES (1, 6, '09:00', '12:00', false, @club, @periode)", ("club", Club), ("periode", periodeId));

        var beschikbaar = await PlannerAvailabilityRepository.GetAvailableFieldsAsync(ConnectionString, Zaterdag, Club);

        beschikbaar.Should().ContainSingle()
            .Which.Should().Match<VeldBeschikbaarheidInfo>(v =>
                v.VeldNummer == 1 && v.BeschikbaarVanaf == new TimeOnly(9, 0) && v.BeschikbaarTot == new TimeOnly(12, 0));
    }

    [PostgresFact]
    public async Task TeamRules_BuffersEnVoorkeurveldWordenCorrectSamengevat()
    {
        await using var conn = await OpstellingAsync();
        const string team = "T-avail JO13-1";
        await ExecAsync(conn, @"
            INSERT INTO public.teamregels (teamnaam, regeltype, waardeminuten, actief, clubcode)
            VALUES (@team, 'BufferVoor', 20, true, @club), (@team, 'BufferNa', 25, true, @club)",
            ("team", team), ("club", Club));
        await ExecAsync(conn, @"
            INSERT INTO public.teamregels (teamnaam, regeltype, waardeveldnummer, prioriteit, actief, clubcode)
            VALUES (@team, 'VoorkeurVeld', 1, 1, true, @club)", ("team", team), ("club", Club));

        var buffers = await TeamRulesRepository.GetAllTeamBuffersAsync(ConnectionString, Club);
        var voorkeur = await TeamRulesRepository.GetAllTeamVoorkeurVeldenAsync(ConnectionString, Club);
        var perTeam = await TeamRulesRepository.GetTeamRulesForTeamsAsync(ConnectionString, new[] { team, "Onbekend Team" }, Club);

        buffers[team].Should().Be((20, 25));
        voorkeur[team].VeldNummer.Should().Be(1);
        perTeam[team].Should().HaveCount(3);
        perTeam["Onbekend Team"].Should().BeEmpty("een team zonder regels krijgt een lege lijst, geen KeyNotFoundException");
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);

        await using (var setupConn = new NpgsqlConnection(ConnectionString))
        {
            await setupConn.OpenAsync();
            await using var view = new NpgsqlCommand(PostgresPlannerViewGenerator.CreateView, setupConn);
            await view.ExecuteNonQueryAsync();
        }

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var sql in new[]
        {
            "DELETE FROM public.teamregels WHERE clubcode = @club",
            "DELETE FROM public.veldtraining WHERE clubcode = @club",
            "DELETE FROM planner.geplandewedstrijden WHERE clubcode = @club",
            "DELETE FROM his.matches WHERE clubcode = @club",
            "DELETE FROM his.teams WHERE clubcode = @club",
            "DELETE FROM public.veldbeschikbaarheid WHERE clubcode = @club",
            "DELETE FROM public.veldperiode WHERE clubcode = @club",
            "DELETE FROM public.velden WHERE clubcode = @club",
            "DELETE FROM public.speeltijden WHERE clubcode = @club",
            "DELETE FROM public.appsettings WHERE clubcode = @club",
        })
            await ExecAsync(conn, sql, ("club", Club));

        await ExecAsync(conn,
            "INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie, sportlinkapiurl, sportlinkclientid) VALUES (@club, true, 'Sportpark Testclub', 'x', 'x')",
            ("club", Club));
        await ExecAsync(conn, @"
            INSERT INTO public.velden (veldnummer, veldnaam, actief, clubcode, veldtype, heeftkunstlicht)
            VALUES (1, 'Veld 1', true, @club, 'kunstgras', true), (2, 'Veld 2', true, @club, 'natuurgras', false)",
            ("club", Club));
        await ExecAsync(conn, @"
            INSERT INTO public.veldbeschikbaarheid (veldnummer, dagvanweek, beschikbaarvanaf, beschikbaartot, gebruikzonsondergang, clubcode, periodeid)
            VALUES (1, 6, '08:00', '20:00', false, @club, NULL), (2, 6, '08:00', '20:00', false, @club, NULL)",
            ("club", Club));
        await ExecAsync(conn,
            "INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, clubcode) VALUES ('JO13', 1.00, 60, @club) ON CONFLICT DO NOTHING",
            ("club", Club));
        await ExecAsync(conn, @"
            INSERT INTO his.teams (teamcode, lokaleteamcode, poulecode, teamnaam, leeftijdscategorie, clubcode, mta_inserted, mta_modified)
            VALUES (1, 1, 1, 'T-avail JO13-1', 'Onder 13', @club, NOW(), NOW())",
            ("club", Club));

        return conn;
    }

    private static Task ZetCompetitieWedstrijdAsync(NpgsqlConnection conn, long wedstrijdcode, string veld, string aanvang) => ExecAsync(conn, @"
        INSERT INTO his.matches (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status, clubcode, mta_inserted, mta_modified)
        VALUES (@code, '2026-09-05', @aanvang, @veld, 'T-avail JO13-1', 'T-avail JO13-1 - Tegenstander', 'Sportpark Testclub', 'Te spelen', @club, NOW(), NOW())",
        ("code", wedstrijdcode), ("veld", veld), ("aanvang", aanvang), ("club", Club));

    private static Task ZetGeplandeWedstrijdAsync(NpgsqlConnection conn, int veldnummer, string aanvang) => ExecAsync(conn, @"
        INSERT INTO planner.geplandewedstrijden (datum, aanvangstijd, eindtijd, veldnummer, teamnaam, tegenstander, status, isvervallen, clubcode)
        VALUES ('2026-09-05', @aanvang::time, '12:00', @veld, 'T-avail JO14-1', 'Oefenteam', 'Te bevestigen', false, @club)",
        ("veld", veldnummer), ("aanvang", aanvang), ("club", Club));

    private static Task ZetTrainingAsync(NpgsqlConnection conn, int veldnummer, string van, string tot) => ExecAsync(conn, @"
        INSERT INTO public.veldtraining (veldnummer, dagvanweek, vantijd, tottijd, omschrijving, actief, clubcode)
        VALUES (@veld, 6, @van::time, @tot::time, 'Training', true, @club)",
        ("veld", veldnummer), ("van", van), ("tot", tot), ("club", Club));

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
