using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Planner.Repositories;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag vast van <see cref="PlannerMatchRepository.FindMatchByOpponentAsync"/> (#1139,
/// deelstuk 1 van #972) — de Postgres-vertaling van het "opponent kan ons team alsnog vinden"-pad
/// dat <c>BerichtPipeline</c> gebruikt als het eigen team niet herkend wordt maar wél een
/// tegenstander genoemd is.
///
/// <para>
/// <b>Aparte klasse van <see cref="PlannerMatchSearchRepositoryIntegrationTests"/>.</b> Die klasse
/// dekt <c>FindMatchAsync</c>/<c>FindMatchByCodeAsync</c>/<c>SavePlannedMatchAsync</c>/
/// <c>SaveHerplanVerzoekAsync</c> en zoekt altijd via de teamresolutielaag
/// (<c>public.teams</c>/<c>public.teamaliassen</c>). <c>FindMatchByOpponentAsync</c> doet expliciet
/// het tegenovergestelde: een vrije-tekst <c>ILIKE</c>-zoekopdracht op de wedstrijdnaam/tegenstander-
/// kolom, zonder teamresolutie — vandaar een eigen club/opstelling zodat geen van beide
/// testklassen de aannames van de ander hoeft te delen.
/// </para>
///
/// <para>
/// <b>Veldnummer 701, niet 101/301/302/401/501/502/601/602 (al bezet door buurklassen).</b>
/// <c>public.velden.veldnummer</c> is een kale PK zonder ClubCode-scope (migratie 001); elke
/// testklasse kiest daarom een eigen, niet-botsende reeks.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class PlannerMatchOpponentLookupIntegrationTests : IDisposable
{
    public void Dispose() => PostgresAppSettings.ResetForTests();

    private const string Club = "opponentlookup";
    private const string AndereClub = "opponentlookup-2";
    private const string Accommodatie = "Sportpark OpponentTest";
    private static readonly DateOnly Zaterdag = new(2026, 9, 5);
    private static readonly DateOnly EenWeekLater = new(2026, 9, 12);

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task GevondenOpDatum_ViaHisMatches()
    {
        await using var conn = await OpstellingAsync();
        await ZetMatchAsync(conn, wedstrijdcode: 9400001, wedstrijd: "EigenTeam JO13-1 - SV Tegenstander",
            datum: Zaterdag, aanvang: "10:00", club: Club);

        var match = await PlannerMatchRepository.FindMatchByOpponentAsync(
            ConnectionString, "SV Tegenstander", Zaterdag, Club);

        match.Should().NotBeNull("de tegenstandernaam komt met een ILIKE-patroon voor in m.wedstrijd, op de gevraagde datum");
        match!.Wedstrijdcode.Should().Be(9400001);
        match.Wedstrijd.Should().Be("EigenTeam JO13-1 - SV Tegenstander");
        match.AanvangsTijd.Should().Be("10:00");
        match.Datum.Should().Be("2026-09-05");
    }

    [PostgresFact]
    public async Task MetDatum_GeenMatchOpDieDatum_MaarWelZonderDatumfilter()
    {
        await using var conn = await OpstellingAsync();
        // De wedstrijd staat gepland op EenWeekLater, niet op Zaterdag.
        await ZetMatchAsync(conn, wedstrijdcode: 9400002, wedstrijd: "EigenTeam JO13-1 - FC Andere Datum",
            datum: EenWeekLater, aanvang: "11:00", club: Club);

        var opZaterdag = await PlannerMatchRepository.FindMatchByOpponentAsync(
            ConnectionString, "FC Andere Datum", Zaterdag, Club);
        opZaterdag.Should().BeNull("op de gevraagde datum staat deze wedstrijd niet gepland");

        var zonderDatum = await PlannerMatchRepository.FindMatchByOpponentAsync(
            ConnectionString, "FC Andere Datum", null, Club);
        zonderDatum.Should().NotBeNull("de tweede stap van BerichtPipeline's zoekvolgorde laat de datumfilter juist weg");
        zonderDatum!.Wedstrijdcode.Should().Be(9400002);
        zonderDatum.Datum.Should().Be("2026-09-12");
    }

    [PostgresFact]
    public async Task NietGevonden_GeeftNullOpBeideTabellen()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;
        // Bewust géén his.matches- en géén planner.geplandewedstrijden-rij voor deze tegenstander.
        var match = await PlannerMatchRepository.FindMatchByOpponentAsync(
            ConnectionString, "Onbekende Tegenstander FC", null, Club);

        match.Should().BeNull();
    }

    [PostgresFact]
    public async Task AndereClub_NietZichtbaar()
    {
        await using var conn = await OpstellingAsync();
        await ZetAndereClubAsync(conn);
        await ZetMatchAsync(conn, wedstrijdcode: 9400003, wedstrijd: "AndereClubTeam - SV Gedeelde Naam",
            datum: Zaterdag, aanvang: "12:00", club: AndereClub);

        var match = await PlannerMatchRepository.FindMatchByOpponentAsync(
            ConnectionString, "SV Gedeelde Naam", Zaterdag, Club);

        match.Should().BeNull("de wedstrijd hoort bij een andere club en mag niet over de ClubCode-grens heen zichtbaar zijn");
    }

    [PostgresFact]
    public async Task ValtTerugOpZelfIngeplandeOefenwedstrijd_AlsHisMatchesNietsOplevert()
    {
        await using var conn = await OpstellingAsync();
        await ZetGeplandeWedstrijdAsync(conn, teamnaam: "EigenTeam JO13-1", tegenstander: "Oefenclub Vriendschappelijk",
            datum: Zaterdag, aanvang: new TimeOnly(15, 0), duurMinuten: 90);

        var match = await PlannerMatchRepository.FindMatchByOpponentAsync(
            ConnectionString, "Oefenclub Vriendschappelijk", Zaterdag, Club);

        match.Should().NotBeNull("his.matches heeft niets, dus de fallback op planner.geplandewedstrijden moet de wedstrijd vinden");
        match!.Wedstrijd.Should().Be("EigenTeam JO13-1 - Oefenclub Vriendschappelijk");
        match.AanvangsTijd.Should().Be("15:00", "de TIME-kolom wordt consistent als HH:mm geformatteerd, net als elders in deze klasse");
        match.EindTijd.Should().Be("16:30");
        match.DuurMinuten.Should().Be(90);
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);

        // PostgresClubScope.Primary/RequireAccommodatieAsync leest de primaire club uit de
        // procesbrede PostgresAppSettings-cache — zelfde reden als de buurklassen.
        PostgresAppSettings.SetForTests("clubCode", Club);

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var sql in new[]
        {
            "DELETE FROM planner.geplandewedstrijden WHERE clubcode = @club OR clubcode = @andereclub",
            "DELETE FROM his.matches WHERE clubcode = @club OR clubcode = @andereclub",
            "DELETE FROM his.teams WHERE clubcode = @club OR clubcode = @andereclub",
            "DELETE FROM public.velden WHERE clubcode = @club OR clubcode = @andereclub",
            "DELETE FROM public.speeltijden WHERE clubcode = @club OR clubcode = @andereclub",
            "DELETE FROM public.appsettings WHERE clubcode = @club OR clubcode = @andereclub",
        })
            await ExecAsync(conn, sql, ("club", Club), ("andereclub", AndereClub));

        await ExecAsync(conn,
            "INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie) VALUES (@club, true, @acc)",
            ("club", Club), ("acc", Accommodatie));
        await ExecAsync(conn,
            "INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, clubcode) VALUES ('JO13', 1.00, 60, @club) ON CONFLICT DO NOTHING",
            ("club", Club));
        // Veldnummer 701 — public.velden.veldnummer is een kale PK zonder ClubCode-scope (migratie
        // 001); nodig voor de FK fk_geplandewedstrijden_velden (migratie 011) die de oefenwedstrijd-
        // fallbacktest hieronder aanspreekt.
        await ExecAsync(conn,
            "INSERT INTO public.velden (veldnummer, veldnaam, actief, clubcode) VALUES (701, 'Veld 1', true, @club)",
            ("club", Club));

        return conn;
    }

    private static async Task ZetAndereClubAsync(NpgsqlConnection conn)
    {
        await ExecAsync(conn,
            "INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie) VALUES (@club, false, @acc)",
            ("club", AndereClub), ("acc", Accommodatie));
    }

    private static async Task ZetMatchAsync(
        NpgsqlConnection conn, long wedstrijdcode, string wedstrijd, DateOnly datum, string aanvang, string club)
    {
        var teamnaam = wedstrijd.Split(" - ", 2)[0];
        await ExecAsync(conn, @"
            INSERT INTO his.matches (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status, clubcode, mta_inserted, mta_modified)
            VALUES (@code, @datum, @aanvang, 'veld 1', @team, @wedstrijd, @acc, 'Te spelen', @club, NOW(), NOW())",
            ("code", wedstrijdcode), ("datum", datum.ToDateTime(TimeOnly.MinValue)), ("aanvang", aanvang),
            ("team", teamnaam), ("wedstrijd", wedstrijd), ("acc", Accommodatie), ("club", club));

        // t.leeftijdscategorie IS NOT NULL/<>''-filter in de LEFT JOIN vereist een his.teams-rij
        // voor dit team — anders blijft de LeeftijdsCategorie/Speeltijden-koppeling weg en gooit
        // MapZoekWedstrijdResponse op "Speelduur niet geconfigureerd".
        await ExecAsync(conn, @"
            INSERT INTO his.teams (teamcode, lokaleteamcode, poulecode, teamnaam, leeftijdscategorie, clubcode, mta_inserted, mta_modified)
            SELECT @code, @code, @code, @team, 'JO13', @club, NOW(), NOW()
            WHERE NOT EXISTS (SELECT 1 FROM his.teams WHERE teamnaam = @team AND clubcode = @club)",
            ("code", wedstrijdcode), ("team", teamnaam), ("club", club));
    }

    private static async Task ZetGeplandeWedstrijdAsync(
        NpgsqlConnection conn, string teamnaam, string tegenstander, DateOnly datum, TimeOnly aanvang, int duurMinuten)
    {
        await ExecAsync(conn, @"
            INSERT INTO planner.geplandewedstrijden
                (datum, aanvangstijd, eindtijd, veldnummer, leeftijdscategorie, teamnaam, tegenstander,
                 wedstrijdduurminuten, status, isvervallen, clubcode)
            VALUES (@datum, @aanvang, @eind, 701, 'JO13', @team, @tegenstander, @duur, 'Te bevestigen', FALSE, @club)",
            ("datum", datum.ToDateTime(TimeOnly.MinValue)), ("aanvang", aanvang.ToTimeSpan()),
            ("eind", aanvang.AddMinutes(duurMinuten).ToTimeSpan()), ("team", teamnaam),
            ("tegenstander", tegenstander), ("duur", duurMinuten), ("club", Club));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }
}
