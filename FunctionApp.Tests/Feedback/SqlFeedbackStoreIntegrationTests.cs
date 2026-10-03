using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using Planner.Shared.Feedback;
using SportlinkFunction.Feedback;
using Xunit;

namespace FunctionApp.Tests.Feedback;

/// <summary>
/// Echte-database-tests voor <see cref="SqlFeedbackStore"/> en <c>avg.sp_CleanupFeedback</c> (#764,
/// #1476). Tegenhanger: <c>FunctionApp.Postgres.Tests/Feedback/FeedbackStoreIntegrationTests.cs</c>.
///
/// <para>
/// Lokaal draaien: start een wegwerp-SQL Server-container, voer het #764-blok van
/// <c>Database/Script.PostDeployment1.sql</c> uit in een lege database en zet
/// <c>SQLSERVER_TEST_CONNECTION_STRING</c> (nooit een wachtwoord in een bestand of log). Zonder die
/// variabele worden de tests overgeslagen.
/// </para>
/// <para>
/// Elke test werkt onder een eigen, unieke clubcode en ruimt zijn rijen op. De retentie-tests
/// bewijzen niet alleen wát er verdwijnt maar ook wát blijft staan.
/// </para>
/// </summary>
public class SqlFeedbackStoreIntegrationTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException($"{SqlServerFactAttribute.EnvVar} niet gezet.");

    private static string NieuweClub() => "ztest" + Guid.NewGuid().ToString("N")[..8];

    private static FeedbackNieuw Rij(string club, string? oid = "oid-1", string type = "Fout",
        string onderwerp = "Onderwerp", string beschrijving = "Beschrijving") =>
        new(Guid.NewGuid(), club, type, onderwerp, "Issuetekst", beschrijving, null, oid, "Jan Melder", "user", "/velden", "3.9.7.0",
            FeedbackStatusWaarden.WachtOpPublicatie);

    private static async Task ExecAsync(string sql, params (string, object?)[] parameters)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string sql, params (string, object?)[] parameters)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task OpruimenAsync(string club)
    {
        await ExecAsync("DELETE FROM [avg].[Feedback] WHERE [ClubCode] = @c", ("@c", club));
        await ExecAsync("DELETE FROM [avg].[FeedbackInzageLog] WHERE [ClubCode] = @c", ("@c", club));
    }

    private static SqlFeedbackStore Store() => new(ConnectionString);

    [SqlServerFact]
    public async Task BewaarEnDetail_RoundTrip_InclusiefTelemetrie_EnClubScope()
    {
        var club = NieuweClub();
        try
        {
            var rij = Rij(club);
            await Store().BewaarAsync(rij, [new FeedbackTelemetrieRegel("console", "Console-fout: x"), new FeedbackTelemetrieRegel("netwerk", "POST /a → 500")]);

            var detail = await Store().GetDetailAsync(club, rij.FeedbackId);

            detail.Should().NotBeNull();
            detail!.Samenvatting.MelderNaam.Should().Be("Jan Melder");
            detail.Samenvatting.AangemaaktUtc.Kind.Should().Be(DateTimeKind.Utc);
            detail.Telemetrie.Select(t => t.Bron).Should().Equal("console", "netwerk");
            (await Store().GetDetailAsync("andere-club", rij.FeedbackId)).Should().BeNull();
        }
        finally { await OpruimenAsync(club); }
    }

    [SqlServerFact]
    public async Task Lijst_FiltertEnEscapedWildcards_EnPagineert()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            await s.BewaarAsync(Rij(club, onderwerp: "Veld laadt niet", beschrijving: "korting 100% fout [x]"), []);
            await s.BewaarAsync(Rij(club, type: "Vraag", onderwerp: "Hoe werkt het", beschrijving: "uitleg"), []);

            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, null, 50, 0))).Totaal.Should().Be(2);
            (await s.LijstAsync(club, new FeedbackFilter("Vraag", null, null, null, null, 50, 0))).Items.Should().ContainSingle();
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, "VELD", 50, 0))).Items.Should().ContainSingle();
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, "100%", 50, 0))).Items.Should().ContainSingle();
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, "%", 50, 0))).Totaal.Should().Be(1, "% is letterlijk");
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, "[x]", 50, 0))).Totaal.Should().Be(1, "[ is letterlijk");
            (await s.LijstAsync(club, new FeedbackFilter(null, null, null, null, null, 1, 1))).Items.Should().ContainSingle();
        }
        finally { await OpruimenAsync(club); }
    }

    [SqlServerFact]
    public async Task ClaimPublicatie_SlaagtMaarEenKeer()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var rij = Rij(club);
            await s.BewaarAsync(rij, []);

            (await s.ClaimPublicatieAsync(club, rij.FeedbackId)).Should().BeTrue();
            (await s.ClaimPublicatieAsync(club, rij.FeedbackId)).Should().BeFalse();

            await s.ZetGepubliceerdAsync(club, rij.FeedbackId, 77, "https://github.com/example/repo/issues/77");
            (await s.GetDetailAsync(club, rij.FeedbackId))!.Samenvatting.IssueNummer.Should().Be(77);

            await s.ZetPublicatieMisluktAsync(club, rij.FeedbackId);   // alleen vanuit 'publiceren': geen effect op gepubliceerd
            (await s.GetDetailAsync(club, rij.FeedbackId))!.Samenvatting.Status.Should().Be(FeedbackStatusWaarden.Gepubliceerd);
        }
        finally { await OpruimenAsync(club); }
    }

    [SqlServerFact]
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
        }
        finally { await OpruimenAsync(club); }
    }

    [SqlServerFact]
    public async Task Inzagelog_LegtActieVast()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var id = Guid.NewGuid();
            await s.LogInzageAsync(new FeedbackInzageNieuw(club, "oid-admin", "Piet Beheerder", "detail", id, null));
            await s.LogInzageAsync(new FeedbackInzageNieuw(club, "oid-admin", "Piet Beheerder", "lijst", null, "type=Fout"));

            var lijst = await s.LijstInzageAsync(club, 10, 0);

            lijst.Totaal.Should().Be(2);
            lijst.Items.Select(i => i.Actie).Should().Equal("lijst", "detail");
            lijst.Items[1].FeedbackId.Should().Be(id);
        }
        finally { await OpruimenAsync(club); }
    }

    [SqlServerFact]
    public async Task Retentie_AnonimiseertAlleenNaTermijn_WistTelemetrieEnInzage_EnLaatTekstStaan()
    {
        var club = NieuweClub();
        try
        {
            var s = Store();
            var nu = DateTime.UtcNow;
            var gesloten25 = Rij(club, oid: "oid-1", beschrijving: "tekst blijft");
            var gesloten23 = Rij(club, oid: "oid-2");
            var openOud = Rij(club, oid: "oid-3");
            var nooitOud = Rij(club, oid: "oid-4");
            var nooitNieuw = Rij(club, oid: "oid-5");
            await s.BewaarAsync(gesloten25, [new FeedbackTelemetrieRegel("console", "oud"), new FeedbackTelemetrieRegel("netwerk", "nieuw")]);
            foreach (var r in new[] { gesloten23, openOud, nooitOud, nooitNieuw }) await s.BewaarAsync(r, []);

            async Task Zet(Guid id, int? nr, DateTime? gesloten, DateTime aangemaakt) => await ExecAsync(
                "UPDATE [avg].[Feedback] SET [IssueNummer]=@nr, [IssueGeslotenOpUtc]=@g, [mta_inserted]=@a WHERE [FeedbackId]=@id",
                ("@nr", nr), ("@g", gesloten), ("@a", aangemaakt), ("@id", id));
            await Zet(gesloten25.FeedbackId, 1, nu.AddMonths(-25), nu.AddMonths(-30));
            await Zet(gesloten23.FeedbackId, 2, nu.AddMonths(-23), nu.AddMonths(-30));
            await Zet(openOud.FeedbackId, 3, null, nu.AddMonths(-40));
            await Zet(nooitOud.FeedbackId, null, null, nu.AddMonths(-25));
            await Zet(nooitNieuw.FeedbackId, null, null, nu.AddMonths(-1));
            await ExecAsync("UPDATE [avg].[FeedbackTelemetrie] SET [mta_inserted]=@t WHERE [FeedbackId]=@id AND [Bron]='console'",
                ("@t", nu.AddDays(-91)), ("@id", gesloten25.FeedbackId));
            await s.LogInzageAsync(new FeedbackInzageNieuw(club, "o", "n", "lijst", null, null));
            await s.LogInzageAsync(new FeedbackInzageNieuw(club, "o", "n", "detail", gesloten25.FeedbackId, null));
            await ExecAsync("UPDATE [avg].[FeedbackInzageLog] SET [mta_inserted]=@t WHERE [ClubCode]=@c AND [Actie]='lijst'",
                ("@t", nu.AddMonths(-25)), ("@c", club));

            var eerste = await s.VoerRetentieUitAsync(nu);
            var tweede = await s.VoerRetentieUitAsync(nu);

            eerste.Geanonimiseerd.Should().BeGreaterThanOrEqualTo(2);
            async Task<(object? Oid, object? Naam)> Lees(Guid id) => (
                await ScalarAsync("SELECT [MelderObjectId] FROM [avg].[Feedback] WHERE [FeedbackId]=@id", ("@id", id)),
                await ScalarAsync("SELECT [MelderNaam] FROM [avg].[Feedback] WHERE [FeedbackId]=@id", ("@id", id)));

            (await Lees(gesloten25.FeedbackId)).Should().Be((DBNull.Value, DBNull.Value));
            (await Lees(nooitOud.FeedbackId)).Should().Be((DBNull.Value, DBNull.Value));
            (await Lees(gesloten23.FeedbackId)).Should().Be(("oid-2", "Jan Melder"));
            (await Lees(openOud.FeedbackId)).Should().Be(("oid-3", "Jan Melder"), "zolang het issue open is blijft de identiteit bewaard");
            (await Lees(nooitNieuw.FeedbackId)).Should().Be(("oid-5", "Jan Melder"));
            (await s.GetDetailAsync(club, gesloten25.FeedbackId))!.Beschrijving.Should().Be("tekst blijft");
            (await s.GetDetailAsync(club, gesloten25.FeedbackId))!.Telemetrie.Select(t => t.Bron).Should().Equal("netwerk");
            (await s.LijstInzageAsync(club, 10, 0)).Items.Select(i => i.Actie).Should().Equal("detail");
            tweede.Geanonimiseerd.Should().Be(0, "een tweede run vindt niets meer te anonimiseren");
        }
        finally { await OpruimenAsync(club); }
    }

    [SqlServerFact]
    public async Task TeControlerenIssues_GeeftNooitGecontroleerdEerst_EnHeropenenWistHetSluitmoment()
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
            lijst.Select(v => v.IssueNummer).Should().Equal(12, 11);

            await s.ZetIssueStatusAsync(a.FeedbackId, null);
            (await ScalarAsync("SELECT [IssueGeslotenOpUtc] FROM [avg].[Feedback] WHERE [FeedbackId]=@id", ("@id", a.FeedbackId)))
                .Should().Be(DBNull.Value);
        }
        finally { await OpruimenAsync(club); }
    }
}
