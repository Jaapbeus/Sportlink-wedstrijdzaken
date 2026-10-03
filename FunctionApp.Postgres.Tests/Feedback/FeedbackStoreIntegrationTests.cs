using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Feedback;
using Npgsql;
using Planner.Shared.Feedback;
using Xunit;

namespace FunctionApp.Postgres.Tests.Feedback;

/// <summary>
/// Echte-database-tests voor <see cref="PostgresFeedbackStore"/> en de bewaartermijn-SQL (#764,
/// #1476). Tegenhanger: <c>FunctionApp.Tests/Feedback/SqlFeedbackStoreIntegrationTests.cs</c>.
///
/// <para>
/// Elke test werkt onder een eigen, unieke clubcode en ruimt zijn rijen op. De retentie-tests
/// bewijzen niet alleen wát er verdwijnt maar ook wát blijft staan: een retentie die te veel wist
/// (de meldingstekst) of te weinig (de identiteit) is in beide richtingen een fout.
/// </para>
/// </summary>
public class FeedbackStoreIntegrationTests
{
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    private static string NieuweClub() => "ztest" + Guid.NewGuid().ToString("N")[..8];

    private static FeedbackNieuw Rij(string club, Guid? id = null, string? oid = "oid-1", string type = "Fout",
        string onderwerp = "Onderwerp", string beschrijving = "Beschrijving", string status = FeedbackStatusWaarden.WachtOpPublicatie) =>
        new(id ?? Guid.NewGuid(), club, type, onderwerp, "Issuetekst", beschrijving, null, oid, "Jan de Vries", "user", "/velden", "3.9.7.0", status);

    private static async Task ExecAsync(string sql, params (string, object?)[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task OpruimenAsync(string club)
    {
        await ExecAsync("DELETE FROM avg.feedback WHERE clubcode = @c", ("c", club));
        await ExecAsync("DELETE FROM avg.feedbackinzagelog WHERE clubcode = @c", ("c", club));
    }

    private static PostgresFeedbackStore Store() => new(ConnectionString);

    [PostgresFact]
    public async Task BewaarEnDetail_RoundTrip_InclusiefTelemetrie()
    {
        var club = NieuweClub();
        try
        {
            var rij = Rij(club);
            await Store().BewaarAsync(rij, [new FeedbackTelemetrieRegel("console", "Console-fout: x"), new FeedbackTelemetrieRegel("netwerk", "POST /a → 500")]);

            var detail = await Store().GetDetailAsync(club, rij.FeedbackId);

            detail.Should().NotBeNull();
            detail!.Samenvatting.MelderNaam.Should().Be("Jan de Vries");
            detail.Samenvatting.Status.Should().Be(FeedbackStatusWaarden.WachtOpPublicatie);
            detail.Samenvatting.AangemaaktUtc.Kind.Should().Be(DateTimeKind.Utc);
            detail.IssueBody.Should().Be("Issuetekst");
            detail.Telemetrie.Select(t => t.Bron).Should().Equal("console", "netwerk");
            (await Store().GetDetailAsync("andere-club", rij.FeedbackId)).Should().BeNull("de clubcode scoped elke lees");
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task Lijst_FiltertOpTypeStatusEnZoektekst_EnEscapedWildcards()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            await s.BewaarAsync(Rij(club, onderwerp: "Veld laadt niet", beschrijving: "korting 100% fout"), []);
            await s.BewaarAsync(Rij(club, type: "Vraag", onderwerp: "Hoe werkt het", beschrijving: "uitleg"), []);

            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, null, 50, 0))).Totaal.Should().Be(2);
            (await s.LijstAsync(club, new FeedbackFilter("Vraag", null, null, null, null, 50, 0))).Items.Should().ContainSingle();
            (await s.LijstAsync(club, new FeedbackFilter(null, FeedbackStatusWaarden.Gepubliceerd, null, null, null, 50, 0))).Totaal.Should().Be(0);
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, "VELD", 50, 0))).Items.Should().ContainSingle("zoeken is hoofdletterongevoelig");
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, "100%", 50, 0))).Items.Should().ContainSingle();
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, "%", 50, 0))).Totaal.Should().Be(1, "een % in de zoektekst is letterlijk, geen wildcard");
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, null, 1, 1))).Items.Should().ContainSingle("paginering");
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task ClaimPublicatie_SlaagtMaarEenKeer_EnZetGepubliceerdBewaartHetIssue()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var rij = Rij(club);
            await s.BewaarAsync(rij, []);

            (await s.ClaimPublicatieAsync(club, rij.FeedbackId)).Should().BeTrue();
            (await s.ClaimPublicatieAsync(club, rij.FeedbackId)).Should().BeFalse("een tweede klik mag geen tweede issue maken");

            await s.ZetGepubliceerdAsync(club, rij.FeedbackId, 77, "https://github.com/example/repo/issues/77");
            var detail = (await s.GetDetailAsync(club, rij.FeedbackId))!;
            (detail.Samenvatting.Status, detail.Samenvatting.IssueNummer).Should().Be((FeedbackStatusWaarden.Gepubliceerd, (int?)77));
            (await s.ClaimPublicatieAsync(club, rij.FeedbackId)).Should().BeFalse("een gepubliceerde melding is niet meer te claimen");
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task PublicatieMislukt_IsOpnieuwTeClaimen()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var rij = Rij(club);
            await s.BewaarAsync(rij, []);
            await s.ClaimPublicatieAsync(club, rij.FeedbackId);

            await s.ZetPublicatieMisluktAsync(club, rij.FeedbackId);

            (await s.ClaimPublicatieAsync(club, rij.FeedbackId)).Should().BeTrue();
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task TelRecenteMeldingen_TeltPerMelderEnVoorDeHeleClub()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            await s.BewaarAsync(Rij(club, oid: "oid-a"), []);
            await s.BewaarAsync(Rij(club, oid: "oid-a"), []);
            await s.BewaarAsync(Rij(club, oid: "oid-b"), []);
            var sinds = DateTime.UtcNow.AddMinutes(-10);

            (await s.TelRecenteMeldingenAsync(club, "oid-a", sinds)).Should().Be(2);
            (await s.TelRecenteMeldingenAsync(club, null, sinds)).Should().Be(3);
            (await s.TelRecenteMeldingenAsync(club, "oid-a", DateTime.UtcNow.AddMinutes(1))).Should().Be(0);
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task Inzagelog_LegtActieVast_ZonderMeldingsinhoud()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var id = Guid.NewGuid();
            await s.LogInzageAsync(new FeedbackInzageNieuw(club, "oid-admin", "Testbeheerder", "detail", id, null));
            await s.LogInzageAsync(new FeedbackInzageNieuw(club, "oid-admin", "Testbeheerder", "lijst", null, "type=Fout"));

            var lijst = await s.LijstInzageAsync(club, 10, 0);

            lijst.Totaal.Should().Be(2);
            lijst.Items.Select(i => i.Actie).Should().Equal("lijst", "detail");
            lijst.Items.Should().OnlyContain(i => i.InzienDoorNaam == "Testbeheerder");
            lijst.Items[1].FeedbackId.Should().Be(id);
        }
        finally { await OpruimenAsync(club); }
    }

    // ── Bewaartermijn ──────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Retentie_AnonimiseertAlleenNaTermijn_EnLaatDeMeldingstekstStaan()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var nu = DateTime.UtcNow;
            var gesloten25 = Rij(club, oid: "oid-1", beschrijving: "tekst blijft");
            var gesloten23 = Rij(club, oid: "oid-2");
            var openOud = Rij(club, oid: "oid-3");
            var nooitGepubliceerdOud = Rij(club, oid: "oid-4");
            var nooitGepubliceerdNieuw = Rij(club, oid: "oid-5");
            foreach (var r in new[] { gesloten25, gesloten23, openOud, nooitGepubliceerdOud, nooitGepubliceerdNieuw })
                await s.BewaarAsync(r, []);

            async Task Zet(Guid id, int? nr, DateTime? gesloten, DateTime aangemaakt) => await ExecAsync(
                "UPDATE avg.feedback SET issuenummer=@nr, issuegeslotenoputc=@g, mta_inserted=@a WHERE feedbackid=@id",
                ("nr", nr), ("g", gesloten), ("a", aangemaakt), ("id", id));
            await Zet(gesloten25.FeedbackId, 1, nu.AddMonths(-25), nu.AddMonths(-30));
            await Zet(gesloten23.FeedbackId, 2, nu.AddMonths(-23), nu.AddMonths(-30));
            await Zet(openOud.FeedbackId, 3, null, nu.AddMonths(-40));
            await Zet(nooitGepubliceerdOud.FeedbackId, null, null, nu.AddMonths(-25));
            await Zet(nooitGepubliceerdNieuw.FeedbackId, null, null, nu.AddMonths(-1));

            var resultaat = await s.VoerRetentieUitAsync(nu);

            resultaat.Geanonimiseerd.Should().BeGreaterThanOrEqualTo(2);
            async Task<(string? Oid, string? Naam, bool Anon)> Lees(Guid id)
            {
                var d = (await s.GetDetailAsync(club, id))!;
                await using var conn = new NpgsqlConnection(ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT melderobjectid FROM avg.feedback WHERE feedbackid=@id", conn);
                cmd.Parameters.AddWithValue("id", id);
                var oid = await cmd.ExecuteScalarAsync();
                return (oid is DBNull or null ? null : (string)oid, d.Samenvatting.MelderNaam, d.Samenvatting.IsGeanonimiseerd);
            }

            (await Lees(gesloten25.FeedbackId)).Should().Be((null, null, true), "25 maanden na sluiting: identiteit weg");
            (await Lees(nooitGepubliceerdOud.FeedbackId)).Should().Be((null, null, true), "nooit gepubliceerd telt vanaf aanmaak");
            (await Lees(gesloten23.FeedbackId)).Should().Be(("oid-2", "Jan de Vries", false), "23 maanden is binnen de termijn");
            (await Lees(openOud.FeedbackId)).Should().Be(("oid-3", "Jan de Vries", false), "zolang het issue open is blijft de identiteit bewaard");
            (await Lees(nooitGepubliceerdNieuw.FeedbackId)).Should().Be(("oid-5", "Jan de Vries", false));

            var tekst = (await s.GetDetailAsync(club, gesloten25.FeedbackId))!;
            tekst.Beschrijving.Should().Be("tekst blijft", "de meldingstekst zelf blijft onbeperkt bewaard");
            tekst.Samenvatting.Onderwerp.Should().Be("Onderwerp");
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task Retentie_WistTelemetrieNa90Dagen_EnInzageNa24Maanden_EnIsIdempotent()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var nu = DateTime.UtcNow;
            var rij = Rij(club);
            await s.BewaarAsync(rij, [new FeedbackTelemetrieRegel("console", "oud"), new FeedbackTelemetrieRegel("netwerk", "nieuw")]);
            await ExecAsync("UPDATE avg.feedbacktelemetrie SET mta_inserted=@t WHERE feedbackid=@id AND bron='console'",
                ("t", nu.AddDays(-91)), ("id", rij.FeedbackId));
            await s.LogInzageAsync(new FeedbackInzageNieuw(club, "o", "n", "lijst", null, null));
            await s.LogInzageAsync(new FeedbackInzageNieuw(club, "o", "n", "detail", rij.FeedbackId, null));
            await ExecAsync("UPDATE avg.feedbackinzagelog SET mta_inserted=@t WHERE clubcode=@c AND actie='lijst'",
                ("t", nu.AddMonths(-25)), ("c", club));

            var eerste = await s.VoerRetentieUitAsync(nu);
            var tweede = await s.VoerRetentieUitAsync(nu);

            eerste.TelemetrieVerwijderd.Should().BeGreaterThanOrEqualTo(1);
            eerste.InzageVerwijderd.Should().BeGreaterThanOrEqualTo(1);
            (await s.GetDetailAsync(club, rij.FeedbackId))!.Telemetrie.Select(t => t.Bron).Should().Equal("netwerk");
            (await s.LijstInzageAsync(club, 10, 0)).Items.Select(i => i.Actie).Should().Equal("detail");
            tweede.Should().Be(new FeedbackRetentieResultaat(0, 0, 0), "een tweede run vindt niets meer");
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task TeControlerenIssues_GeeftOudstGecontroleerdEerst_EnHeropenenWistHetSluitmoment()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var a = Rij(club);
            var b = Rij(club);
            await s.BewaarAsync(a, []);
            await s.BewaarAsync(b, []);
            await s.ZetGepubliceerdAsync(club, a.FeedbackId, 11, "https://x/11");
            await s.ZetGepubliceerdAsync(club, b.FeedbackId, 12, "https://x/12");
            await s.ZetIssueStatusAsync(a.FeedbackId, DateTime.UtcNow.AddMonths(-1));

            var lijst = (await s.TeControlerenIssuesAsync(500)).Where(v => v.IssueNummer is 11 or 12).ToList();

            lijst.Select(v => v.IssueNummer).Should().Equal(12, 11);   // nooit gecontroleerd (b) vóór gecontroleerd (a)

            await s.ZetIssueStatusAsync(a.FeedbackId, null);   // heropend
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT issuegeslotenoputc FROM avg.feedback WHERE feedbackid=@id", conn);
            cmd.Parameters.AddWithValue("id", a.FeedbackId);
            (await cmd.ExecuteScalarAsync()).Should().Be(DBNull.Value);
        }
        finally { await OpruimenAsync(club); }
    }
}
