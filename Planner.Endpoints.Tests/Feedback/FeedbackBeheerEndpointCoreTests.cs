using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Feedback;
using Planner.Shared.Feedback;
using Xunit;

namespace Planner.Endpoints.Tests.Feedback;

/// <summary>Beheeroverzicht (#764, #1478): filters, inzagelog en de publicatieknop voor wachtende meldingen.</summary>
public class FeedbackBeheerEndpointCoreTests
{
    private const string ClubCode = "ALLSTARS";
    private static readonly FeedbackAanroeper Beheerder = new("00000000-0000-0000-0000-0000000000a1", "Testbeheerder", true);
    private static readonly Guid Id = Guid.Parse("3f2a9c1b-1111-2222-3333-444455556666");

    private static FeedbackDetail MaakDetail(string status = FeedbackStatusWaarden.WachtOpPublicatie, string body = "Schone issuetekst.") => new(
        new FeedbackSamenvatting(Id, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), "Fout", "Veldenpagina laadt niet",
            "Jan de Vries", false, "user", "/velden", null, null, status),
        "Eigen woorden van de melder", null, body, "3.9.7.0", []);

    private static Func<string, string, string[], Task<(int, string)>> GitHub(out List<string> aanroepen, bool faalt = false)
    {
        var lijst = new List<string>();
        aanroepen = lijst;
        return (titel, body, labels) =>
        {
            lijst.Add(titel);
            if (faalt) throw new InvalidOperationException("HTTP 500");
            return Task.FromResult((77, "https://github.com/example/repo/issues/77"));
        };
    }

    // ── Lijst ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lijst_LogtDeInzage_ZonderDeZoektekst()
    {
        var store = new FeedbackStoreFake();

        var result = await FeedbackBeheerEndpointCore.LijstAsync(ClubCode, Beheerder, store,
            "Fout", "wacht-op-publicatie", "2026-09-01", null, "jan de vries", "25", "0");

        result.Should().BeOfType<OkObjectResult>();
        var log = store.Inzage.Should().ContainSingle().Subject;
        log.Actie.Should().Be("lijst");
        log.InzienDoorObjectId.Should().Be(Beheerder.ObjectId);
        log.InzienDoorNaam.Should().Be("Testbeheerder");
        log.Filter.Should().Contain("type=Fout").And.Contain("zoekterm=ja").And.NotContain("jan de vries");
        store.LaatsteFilter!.Limit.Should().Be(25);
    }

    [Theory]
    [InlineData("Onbekend", null, null)]
    [InlineData(null, "bestaat-niet", null)]
    [InlineData(null, null, "morgen")]
    public async Task Lijst_OngeldigFilter_Geeft400ZonderInzagelog(string? type, string? status, string? vanaf)
    {
        var store = new FeedbackStoreFake();

        var result = await FeedbackBeheerEndpointCore.LijstAsync(ClubCode, Beheerder, store, type, status, vanaf, null, null, null, null);

        result.Should().BeOfType<BadRequestObjectResult>();
        store.Inzage.Should().BeEmpty();
    }

    [Fact]
    public async Task Lijst_BegrenstLimitEnTotIsInclusief()
    {
        var store = new FeedbackStoreFake();

        await FeedbackBeheerEndpointCore.LijstAsync(ClubCode, Beheerder, store, null, null, null, "2026-10-03", null, "99999", "-5");

        store.LaatsteFilter!.Limit.Should().Be(FeedbackEndpointCore.MaxLimit);
        store.LaatsteFilter.Offset.Should().Be(0);
        store.LaatsteFilter.TotUtc.Should().Be(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), "tot en met 3 oktober");
    }

    // ── Detail ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_LogtDeInzageMetHetMeldingId()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };

        var result = await FeedbackBeheerEndpointCore.DetailAsync(ClubCode, Beheerder, store, Id);

        result.Should().BeOfType<OkObjectResult>();
        var log = store.Inzage.Should().ContainSingle().Subject;
        (log.Actie, log.FeedbackId).Should().Be(("detail", (Guid?)Id));
    }

    [Fact]
    public async Task Detail_OnbekendeMelding_Geeft404ZonderInzagelog()
    {
        var store = new FeedbackStoreFake();

        var result = await FeedbackBeheerEndpointCore.DetailAsync(ClubCode, Beheerder, store, Id);

        result.Should().BeOfType<NotFoundObjectResult>();
        store.Inzage.Should().BeEmpty();
    }

    // ── Publiceren ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publiceer_WachtendeMelding_MaaktIssueEnLogtDeActie()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<OkObjectResult>();
        aanroepen.Should().ContainSingle();
        store.Gepubliceerd[Id].Nummer.Should().Be(77);
        store.Inzage.Should().ContainSingle(i => i.Actie == "publiceer");
    }

    [Fact]
    public async Task Publiceer_AlGepubliceerd_Geeft409ZonderGitHubAanroep()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail(FeedbackStatusWaarden.Gepubliceerd) };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<ConflictObjectResult>();
        aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task Publiceer_ClaimMislukt_Geeft409OmEenDubbelIssueTeVoorkomen()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail(), ClaimSlaagt = false };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<ConflictObjectResult>();
        aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task Publiceer_TekstMetPii_WordtAlsnogGeblokkeerd()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail(body: "Mail trainer@voorbeeld.nl voor details.") };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task Publiceer_GitHubFaalt_Geeft502EnZetStatusOpMislukt()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out _, faalt: true), NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        store.Statussen[Id].Should().Be(FeedbackStatusWaarden.GitHubMislukt);
    }

    [Fact]
    public async Task Publiceer_ZonderGitHubConfiguratie_Geeft503()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, null, NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
    }
}
