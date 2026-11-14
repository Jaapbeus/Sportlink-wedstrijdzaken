using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>#1892: resultaatvelden voor thuis-/uitteam en de terugleeswaarschuwing bij een afwijkend uitteam.</summary>
public class ClubMatchTerugleesTests
{
    private const string Rol = "wedstrijdzaken";
    private const string MatchId = "synthetic-match-id";
    private const string EigenTeamId = "T1000000001";
    private const string TegenstanderId = "T1000000002";

    private static SportlinkClubMatchAanvraag Aanvraag(string uitId = SportlinkClubMatchAanvraag.GeenTeamId, string uit = "FC Voorbeeld JO10-1")
        => new(new DateOnly(2026, 11, 14), new TimeOnly(10, 0), 60, 26111401, "Oefenwedstrijd", "JO10-1", uit,
            EigenTeamId, "110", "SOCCER-VE-AL/SATURDAY", true, "FAC", "FAC-OUTDOOR_FIELD-1", PublicAwayTeamId: uitId);

    private static string Json(string? naam, string? id)
        => $"{{\"Teams\":{{\"Home\":{{\"TeamName\":\"JO10-1\",\"PublicTeamId\":\"{EigenTeamId}\"}},\"Away\":{{\"TeamName\":{(naam == null ? "null" : $"\"{naam}\"")},\"PublicTeamId\":{(id == null ? "null" : $"\"{id}\"")}}}}}}}";

    private static SportlinkMutationResult Succes(bool dryRun = false, bool geforceerd = false, string? id = MatchId)
        => new(true, null, dryRun, geforceerd, id);

    private static Mock<ISportlinkClubClient> Client(string? json, SportlinkClubCallStatus status = SportlinkClubCallStatus.Ok)
    {
        var mock = new Mock<ISportlinkClubClient>(MockBehavior.Strict);
        mock.Setup(c => c.GetMatchRawJsonAsync(Rol, MatchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<string>(status, json, null, 200));
        return mock;
    }

    private static Task<IReadOnlyList<string>> Roep(Mock<ISportlinkClubClient> client, SportlinkMutationResult? r, SportlinkClubMatchAanvraag a)
        => ClubMatchTerugleesCore.VoegTerugleesWaarschuwingToeAsync(client.Object, Rol, r, a, new[] { "bestaand" }, NullLogger.Instance);

    [Fact]
    public void Resultaat_BevatThuisEnUitteam_EnKoppelstatus()
    {
        var vrij = ClubMatchEndpointCore.Resultaat(Succes(), Aanvraag(), "Veld 1", Array.Empty<string>());
        vrij.Thuisteam.Should().Be("JO10-1");
        vrij.Uitteam.Should().Be("FC Voorbeeld JO10-1");
        vrij.UitteamGekoppeld.Should().BeFalse("de sentinel betekent: geen teamkoppeling");

        ClubMatchEndpointCore.Resultaat(Succes(), Aanvraag(TegenstanderId), "Veld 1", Array.Empty<string>())
            .UitteamGekoppeld.Should().BeTrue();
    }

    [Fact]
    public async Task EigenTeamAlsUitteam_GeeftWaarschuwing()
    {
        var w = await Roep(Client(Json("JO10-1", EigenTeamId)), Succes(), Aanvraag());
        w.Should().HaveCount(2).And.Contain("bestaand");
        w[1].Should().Contain("'JO10-1'").And.Contain("FC Voorbeeld JO10-1");
    }

    [Fact]
    public async Task VrijeTegenstander_ZelfdeNaamGenormaliseerd_GeenWaarschuwing()
    {
        var w = await Roep(Client(Json("  fc voorbeeld  jo10-1 ", TegenstanderId)), Succes(), Aanvraag());
        w.Should().Equal("bestaand");
    }

    private static SportlinkClubMatchAanvraag VrijThuisEnVrijUit(string uit = "FC Voorbeeld JO10-1")
        => Aanvraag(uit: uit) with { PublicTeamId = SportlinkClubMatchAanvraag.GeenTeamId };

    [Fact]
    public async Task VrijThuisteamEnVrijeTegenstander_GelijkeNaam_GeenWaarschuwing()
    {
        var json = Json("FC Voorbeeld JO10-1", SportlinkClubMatchAanvraag.GeenTeamId);
        (await Roep(Client(json), Succes(), VrijThuisEnVrijUit())).Should().Equal("bestaand");
    }

    [Fact]
    public async Task VrijThuisteamEnVrijeTegenstander_AfwijkendeNaam_GeeftWaarschuwing()
    {
        var json = Json("Andere Club 1", SportlinkClubMatchAanvraag.GeenTeamId);
        (await Roep(Client(json), Succes(), VrijThuisEnVrijUit())).Should().HaveCount(2);
    }

    [Fact]
    public async Task VrijeTegenstander_AndereNaam_GeeftWaarschuwing()
    {
        var w = await Roep(Client(Json("Andere Club 1", null)), Succes(), Aanvraag());
        w.Should().HaveCount(2);
    }

    [Fact]
    public async Task GekoppeldUitteam_ZelfdeId_GeenWaarschuwing_AnderId_Wel()
    {
        (await Roep(Client(Json("x", TegenstanderId)), Succes(), Aanvraag(TegenstanderId))).Should().Equal("bestaand");
        (await Roep(Client(Json("x", "T1000000003")), Succes(), Aanvraag(TegenstanderId))).Should().HaveCount(2);
    }

    [Fact]
    public async Task GekoppeldUitteam_ZonderIdUitSportlink_GeenValseWaarschuwing()
        => (await Roep(Client(Json("Iets", null)), Succes(), Aanvraag(TegenstanderId))).Should().Equal("bestaand");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DryRunOfGesimuleerd_LeestNietTerug(bool dryRun, bool geforceerd)
    {
        var client = new Mock<ISportlinkClubClient>(MockBehavior.Strict); // geen setup: elke aanroep faalt
        var w = await Roep(client, Succes(dryRun, geforceerd), Aanvraag());
        w.Should().Equal("bestaand");
    }

    [Fact]
    public async Task MislukteAanmaakOfOntbrekendId_LeestNietTerug()
    {
        var client = new Mock<ISportlinkClubClient>(MockBehavior.Strict);
        (await Roep(client, new SportlinkMutationResult(false, new[] { "nee" }, false, false, MatchId), Aanvraag())).Should().Equal("bestaand");
        (await Roep(client, Succes(id: null), Aanvraag())).Should().Equal("bestaand");
        (await Roep(client, null, Aanvraag())).Should().Equal("bestaand");
    }

    [Theory]
    [InlineData("geen json")]
    [InlineData("{}")]
    [InlineData("{\"Teams\":{}}")]
    [InlineData("{\"Teams\":{\"Away\":null}}")]
    public async Task OnbruikbareTeruglezing_GeeftNooitWaarschuwing(string json)
        => (await Roep(Client(json), Succes(), Aanvraag())).Should().Equal("bestaand");

    [Fact]
    public async Task TerugleesFoutOfStatus_BreektNietAf()
    {
        (await Roep(Client(null, SportlinkClubCallStatus.Ok), Succes(), Aanvraag())).Should().Equal("bestaand");
        (await Roep(Client(Json("JO10-1", EigenTeamId), SportlinkClubCallStatus.RolNietGekoppeld), Succes(), Aanvraag())).Should().Equal("bestaand");

        var kapot = new Mock<ISportlinkClubClient>(MockBehavior.Strict);
        kapot.Setup(c => c.GetMatchRawJsonAsync(Rol, MatchId, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("synthetisch"));
        (await Roep(kapot, Succes(), Aanvraag())).Should().Equal("bestaand");
    }
}
