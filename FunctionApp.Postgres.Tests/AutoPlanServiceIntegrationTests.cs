using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Planner;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag van <see cref="AutoPlanService.AutoPlanAsync"/> en
/// <see cref="AutoPlanService.AutoPlanToepassenAsync"/> vast (issue 888 vervolg, §42) — de laatste
/// twee planner-endpoints van deze tier.
///
/// <para>
/// <b>Draait op de democlub (ALLSTARS).</b> Dat is geen willekeurige keuze: <c>AutoPlanToepassen</c>
/// weigert per definitie elke andere club (het schrijft naar <c>his.matches</c> en mag echte,
/// gesynchroniseerde clubdata nooit aanraken), en de ALLSTARS-tak van <c>AutoPlanAsync</c> gebruikt
/// bovendien <see cref="AllstarsTestDataRepository.GetAllstarsVeldenAsync"/> plus de
/// leeftijd-uit-teamnaam-afleiding — precies de twee stukken die op deze tier nieuw zijn.
/// </para>
///
/// <para>
/// <b>Veldnummers 101/102.</b> <c>GetAllstarsVeldenAsync</c> filtert op <c>veldnummer &gt;= 100</c>
/// — dezelfde conventie als <c>006_allstars_demodata.sql</c>. Een testveld onder 100 zou door die
/// query genegeerd worden en de test zou dan "geen velden" meten in plaats van het echte gedrag.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class AutoPlanServiceIntegrationTests : IDisposable
{
    public void Dispose() => PostgresAppSettings.ResetForTests();

    private const string Club = "ALLSTARS";
    private static readonly DateOnly Zaterdag = new(2026, 9, 5);

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task AutoPlanAsync_PlantElkeWedstrijdInEnLevertBeideHtmlWeergaven()
    {
        await using var conn = await OpstellingAsync();
        await ZetWedstrijdAsync(conn, 9500001, "ALLSTARS JO13-1", aanvang: null, veld: null);
        await ZetWedstrijdAsync(conn, 9500002, "ALLSTARS JO15-1", aanvang: null, veld: null);

        var response = await AutoPlanService.AutoPlanAsync(
            ConnectionString, new AutoPlanRequest { Datum = Zaterdag.ToString("yyyy-MM-dd") }, Club, NullLogger.Instance);

        response.TotaalWedstrijden.Should().Be(2);
        response.NietInplanbaar.Should().Be(0, "beide teams hebben een speeltijd en er zijn twee vrije velden");
        response.Wedstrijden.Should().OnlyContain(w => w.OptimaalTijd != null && w.OptimaalVeldNummer != null);
        // Zonder huidige tijd/veld is elke wedstrijd een nieuw slot.
        response.Wedstrijden.Should().OnlyContain(w => w.Status == "nieuw-slot");
        response.TeWijzigen.Should().Be(2);
        response.GeschatteEindTijd.Should().NotBeNull();

        // De HTML komt uit de nu gedeelde PlannerHtmlGenerator (§42) — beide panelen moeten echte
        // opbouw bevatten, niet de "geen wedstrijden"-tekst.
        response.OptimaleHtml.Should().Contain("planner-grid");
        response.HuidigeHtml.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task AutoPlanAsync_ZonderSpeeltijdVoorDeCategorie_LevertOnbekendTeam()
    {
        await using var conn = await OpstellingAsync();
        // JO99 heeft geen rij in public.speeltijden — de planner kan geen duur bepalen.
        await ZetWedstrijdAsync(conn, 9500003, "ALLSTARS JO99-1", aanvang: null, veld: null);

        var response = await AutoPlanService.AutoPlanAsync(
            ConnectionString, new AutoPlanRequest { Datum = Zaterdag.ToString("yyyy-MM-dd") }, Club, NullLogger.Instance);

        response.Wedstrijden.Should().ContainSingle()
            .Which.Status.Should().Be("onbekend-team");
    }

    [PostgresFact]
    public async Task AutoPlanAsync_OngeldigeDatum_LevertLegeResponseZonderTeCrashen()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;

        var response = await AutoPlanService.AutoPlanAsync(
            ConnectionString, new AutoPlanRequest { Datum = "geen-datum" }, Club, NullLogger.Instance);

        response.Datum.Should().Be("geen-datum");
        response.TotaalWedstrijden.Should().Be(0);
    }

    /// <summary>
    /// Legt het HUIDIGE gedrag vast bij twee wedstrijden van hetzelfde team op één dag: beide
    /// worden ingepland, elk op een eigen veld.
    ///
    /// <para>
    /// <b>Let op — dit legt ook een bekende beperking vast, geen wenselijk gedrag.</b>
    /// <see cref="FieldScheduler"/> gebruikt de teamnaam uitsluitend om teambuffers op te zoeken en
    /// om het slot te stempelen; hij houdt niet bij dat een team al ergens anders speelt. Twee
    /// wedstrijden van hetzelfde team kunnen daardoor op dezelfde tijd op twee velden landen. Dat
    /// geldt voor beide tiers (de engine is sinds §38 gedeeld) en is dus geen porteerfout — zie het
    /// aparte issue dat hierover is aangemaakt. Deze test faalt bewust zodra dat gedrag verandert,
    /// zodat de fix niet stilzwijgend langs deze suite glipt.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task AutoPlanAsync_ZelfdeTeamTweeKeerOpEenDag_PlantBeideOpEigenTijdstip()
    {
        // #939: FieldScheduler hield bezetting alleen per veld bij, niet per team. Twee wedstrijden
        // van hetzelfde team konden daardoor allebei op hetzelfde tijdstip belanden, op twee
        // verschillende velden — een team kan niet op twee velden tegelijk spelen. De unieke-
        // veldnummer-assertie hieronder alleen bewees dat niet: die stond al vóór de fix groen.
        await using var conn = await OpstellingAsync();
        await ZetWedstrijdAsync(conn, 9500004, "ALLSTARS JO13-1", aanvang: null, veld: null);
        await ZetWedstrijdAsync(conn, 9500005, "ALLSTARS JO13-1", aanvang: null, veld: null,
            wedstrijdNaam: "ALLSTARS JO13-1 - Andere tegenstander");

        var response = await AutoPlanService.AutoPlanAsync(
            ConnectionString, new AutoPlanRequest { Datum = Zaterdag.ToString("yyyy-MM-dd") }, Club, NullLogger.Instance);

        response.TotaalWedstrijden.Should().Be(2);
        response.NietInplanbaar.Should().Be(0);
        response.Wedstrijden.Select(w => w.OptimaalVeldNummer).Should().OnlyHaveUniqueItems(
            "twee gelijktijdige wedstrijden kunnen niet op hetzelfde veld staan");
        response.Wedstrijden.Select(w => w.OptimaalTijd).Should().OnlyHaveUniqueItems(
            "hetzelfde team kan niet op twee velden tegelijk spelen, ook al zijn er genoeg vrije velden");
    }

    [PostgresFact]
    public async Task AutoPlanToepassenAsync_SchrijftDeOptimaleTijdEnVeldTerug()
    {
        await using var conn = await OpstellingAsync();
        await ZetWedstrijdAsync(conn, 9500006, "ALLSTARS JO13-1", aanvang: "18:00", veld: "Kunstgras 1");

        var response = await AutoPlanService.AutoPlanToepassenAsync(
            ConnectionString, new AutoPlanToepassenRequest { Datum = Zaterdag.ToString("yyyy-MM-dd") }, Club, NullLogger.Instance);

        response.Bijgewerkt.Should().BeGreaterThan(0, string.Join(" | ", response.Fouten));
        response.Mislukt.Should().Be(0);

        await using var cmd = new NpgsqlCommand(
            "SELECT aanvangstijd, veld FROM his.matches WHERE wedstrijdcode = 9500006", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().NotBe("18:00", "de planner plant de vroegste vrije tijd, niet 18:00");
    }

    [PostgresFact]
    public async Task AutoPlanToepassenAsync_NietDemoclub_Weigert()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;

        var act = () => AutoPlanService.AutoPlanToepassenAsync(
            ConnectionString, new AutoPlanToepassenRequest { Datum = Zaterdag.ToString("yyyy-MM-dd") },
            "echte-club", NullLogger.Instance);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*alleen beschikbaar in testmodus*");
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);
        PostgresAppSettings.SetForTests("clubCode", Club);
        // De gedeelde HTML-generator eist deze instelling (§42) — zonder deze waarde gooit
        // BouwHtmlInstellingen, en dat zou elke test hier op dezelfde melding laten stranden.
        PostgresAppSettings.SetForTests("plannerAfzenderNaam", "Testplanner");
        PostgresAppSettings.SetForTests("accommodatie", "Sportpark Testclub");

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var sql in new[]
        {
            "DELETE FROM his.matches WHERE clubcode = @club",
            "DELETE FROM his.teams WHERE clubcode = @club",
            "DELETE FROM public.teamvoorkeurtijden WHERE clubcode = @club",
            "DELETE FROM public.teamregels WHERE clubcode = @club",
            // FK-volgorde: migratie 006 seedt voor déze clubcode (ALLSTARS) al veldbeschikbaarheid
            // en trainingen op de velden 101-103. Zonder ze eerst te verwijderen faalt de DELETE op
            // public.velden met veldbeschikbaarheid_veldnummer_fkey.
            "DELETE FROM planner.geplandewedstrijden WHERE clubcode = @club",
            "DELETE FROM public.veldbeschikbaarheid WHERE clubcode = @club",
            "DELETE FROM public.veldtraining WHERE clubcode = @club",
            "DELETE FROM public.veldperiode WHERE clubcode = @club",
            "DELETE FROM public.velden WHERE clubcode = @club",
            "DELETE FROM public.speeltijden WHERE clubcode = @club",
            "DELETE FROM public.appsettings WHERE clubcode = @club",
        })
            await ExecAsync(conn, sql, ("club", Club));

        await ExecAsync(conn, @"
            INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie, sportlinkapiurl, sportlinkclientid)
            VALUES (@club, false, 'Sportpark Testclub', 'x', 'x')", ("club", Club));
        // Veldnummers >= 100 — zie de klasse-doc-comment.
        await ExecAsync(conn, @"
            INSERT INTO public.velden (veldnummer, veldnaam, actief, clubcode, veldtype, heeftkunstlicht)
            VALUES (101, 'Kunstgras 1', true, @club, 'kunstgras', true),
                   (102, 'Kunstgras 2', true, @club, 'kunstgras', true)", ("club", Club));
        await ExecAsync(conn, @"
            INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, clubcode)
            VALUES ('JO13', 1.00, 60, @club), ('JO15', 1.00, 70, @club)
            ON CONFLICT DO NOTHING", ("club", Club));

        return conn;
    }

    private static async Task ZetWedstrijdAsync(
        NpgsqlConnection conn, long wedstrijdcode, string teamnaam,
        string? aanvang, string? veld, string? wedstrijdNaam = null)
    {
        await ExecAsync(conn, @"
            INSERT INTO his.matches
                (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status,
                 clubcode, mta_inserted, mta_modified)
            VALUES (@code, '2026-09-05', @aanvang, @veld, @team, @wedstrijd, 'Sportpark Testclub', 'Te spelen',
                    @club, NOW(), NOW())",
            ("code", wedstrijdcode),
            ("aanvang", (object?)aanvang ?? DBNull.Value),
            ("veld", (object?)veld ?? DBNull.Value),
            ("team", teamnaam),
            ("wedstrijd", (object?)wedstrijdNaam ?? $"{teamnaam} - Tegenstander"),
            ("club", Club));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }
}
