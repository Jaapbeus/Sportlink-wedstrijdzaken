using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>
/// Orkestratie van <c>DELETE /api/sportlink/club-match/{publicMatchId}</c> (#1440). Bewijst dat de
/// guard vóór de verwijderaanroep zit, dat élke poging (ook een geblokkeerde) in de audit komt met het
/// PublicMatchId, en dat een geblokkeerde poging de client nooit laat verwijderen. Alle waarden fictief.
/// </summary>
public class ClubMatchVerwijderCoreTests
{
    private const string Id = "M000000001";

    private sealed class Audit
    {
        public List<SportlinkMutationAuditEntry> Pogingen { get; } = new();
        public List<(long AuditId, string Resultaat, string? Samenvatting)> Voltooid { get; } = new();

        public ClubMatchVerwijderCore.VerwijderAudit Contract() => new(
            "ALLSTARS", "tester",
            e => { Pogingen.Add(e); return Task.FromResult(42L); },
            (id, r, s) => { Voltooid.Add((id, r, s)); return Task.CompletedTask; });
    }

    private static Mock<ISportlinkClubClient> Client(SportlinkMatch? match, SportlinkClubCallStatus matchStatus = SportlinkClubCallStatus.Ok)
    {
        var mock = new Mock<ISportlinkClubClient>(MockBehavior.Strict);
        mock.Setup(c => c.GetMatchAsync(It.IsAny<string>(), Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<SportlinkMatch>(matchStatus, match, null, 200));
        mock.Setup(c => c.DeleteClubMatchAsync(It.IsAny<string>(), Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.Ok, new SportlinkMutationResult(true, null, IsDryRun: true, IsForcedDryRun: true), null, 200));
        return mock;
    }

    private static SportlinkMatch Clubwedstrijd(bool? isKernelMatch = false) => new()
    {
        PublicMatchId = Id, ExternalMatchId = "26100201", IsHomeMatch = true, IsKernelMatch = isKernelMatch
    };

    private static Task<IActionResult> Voer(Mock<ISportlinkClubClient> client, Audit audit, string? id = Id, IActionResult? toggleFout = null)
        => ClubMatchVerwijderCore.VerwijderAsync(id, () => toggleFout, () => (client.Object, null), "Wedstrijdzaken", audit.Contract());

    [Fact]
    public async Task Clubwedstrijd_WordtVerwijderd_EnPogingStaatInDeAuditMetPublicMatchId()
    {
        var client = Client(Clubwedstrijd());
        var audit = new Audit();

        var result = await Voer(client, audit);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<ClubMatchVerwijderCore.ClubMatchVerwijderResultaat>().Subject;
        ok.PublicMatchId.Should().Be(Id);
        ok.IsForcedDryRun.Should().BeTrue();
        client.Verify(c => c.DeleteClubMatchAsync("Wedstrijdzaken", Id, It.IsAny<CancellationToken>()), Times.Once);
        var poging = audit.Pogingen.Should().ContainSingle().Subject;
        poging.PublicMatchId.Should().Be(Id);
        poging.Actie.Should().Be(ClubMatchVerwijderCore.AuditActie);
        poging.ClubCode.Should().Be("ALLSTARS");
        poging.WaardeVoor.Should().Contain("\"IsKernelMatch\":false").And.Contain("26100201");
        audit.Voltooid.Should().ContainSingle().Which.Should().Be((42L, "DryRunLocked", (string?)null), "de code-lock maakt dit een gedwongen simulatie; Voltooid hoort bij dezelfde auditrij");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task GeenClubwedstrijdOfOnbekend_Geeft409_EnVerwijdertNooit(bool? isKernelMatch)
    {
        var client = Client(Clubwedstrijd(isKernelMatch));
        var audit = new Audit();

        var result = await Voer(client, audit);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        client.Verify(c => c.DeleteClubMatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Pogingen.Should().ContainSingle("ook een geblokkeerde poging hoort in de audit");
        audit.Voltooid.Should().ContainSingle().Which.Resultaat.Should().Be("Geblokkeerd");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("M1;DROP")]
    [InlineData("../M1")]
    public async Task OngeldigPublicMatchId_Geeft400_ZonderSportlinkAanroep(string? id)
    {
        var client = new Mock<ISportlinkClubClient>(MockBehavior.Strict);
        var audit = new Audit();

        var result = await ClubMatchVerwijderCore.VerwijderAsync(id, () => null, () => (client.Object, null), "Wedstrijdzaken", audit.Contract());

        result.Should().BeOfType<BadRequestObjectResult>();
        audit.Pogingen.Should().BeEmpty();
    }

    [Fact]
    public async Task ToggleOfEgressDicht_GeeftDieFoutTerug_ZonderSportlinkAanroep()
    {
        var client = new Mock<ISportlinkClubClient>(MockBehavior.Strict);
        var audit = new Audit();
        var dicht = new ObjectResult(new { error = "uit" }) { StatusCode = 409 };

        var result = await Voer(client, audit, toggleFout: dicht);

        result.Should().BeSameAs(dicht);
        audit.Pogingen.Should().BeEmpty();
    }

    [Fact]
    public async Task OnbekendeWedstrijd_Geeft404_ZonderVerwijderaanroep()
    {
        var client = Client(match: null);
        var audit = new Audit();

        var result = await Voer(client, audit);

        result.Should().BeOfType<NotFoundObjectResult>();
        client.Verify(c => c.DeleteClubMatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void BouwWaardeVoor_BevatGeenTeamnamenOfOfficials()
    {
        var match = Clubwedstrijd() with
        {
            MatchOfficials = new List<SportlinkMatchOfficial> { new() { OfficialPosition = "Referee", RelatieCode = "ABC123" } }
        };

        var json = ClubMatchVerwijderCore.BouwWaardeVoor(match);

        json.Should().NotContain("ABC123").And.NotContain("Official");
    }
}
