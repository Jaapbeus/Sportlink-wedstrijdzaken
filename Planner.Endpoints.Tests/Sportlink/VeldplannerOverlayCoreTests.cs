using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

public class VeldplannerOverlayCoreTests
{
    private static readonly SportlinkVeldplannerBlok Blok = new("A - B", "veld 1", "09:00", 1m, 75, null);

    private static Mock<ISportlinkClubClient> Client(string accommodatieId, DateTime? _ = null, SportlinkClubCallStatus status = SportlinkClubCallStatus.Ok)
    {
        var mock = new Mock<ISportlinkClubClient>();
        mock.Setup(c => c.GetClubMatchPickListsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<SportlinkClubMatchPickLists>(SportlinkClubCallStatus.Ok,
                new SportlinkClubMatchPickLists(Array.Empty<SportlinkPickListItem>(),
                    new[] { new SportlinkPickListItem(accommodatieId, Naam(accommodatieId)) }), null, 200));
        mock.Setup(c => c.GetVeldplannerAsync(It.IsAny<string>(), accommodatieId, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>>(
                status, status == SportlinkClubCallStatus.Ok ? new[] { Blok } : null, null, 200));
        return mock;
    }

    // Elke test een eigen accommodatienaam: de facility-cache is bewust statisch en zou anders tussen tests lekken.
    private static string Naam(string id) => "Sportpark " + id;

    private static async Task<IReadOnlyList<SportlinkVeldplannerBlok>?> Haal(
        ISportlinkClubClient? client, SportlinkTimerStatus toegang = SportlinkTimerStatus.MagDraaien, string? accommodatie = null, DateOnly? datum = null, Func<DateTime>? nu = null)
        => (await HaalMetStatus(client, toegang, accommodatie, datum, nu)).Blokken;

    private static Task<VeldplannerOphaalResultaat> HaalMetStatus(
        ISportlinkClubClient? client, SportlinkTimerStatus toegang = SportlinkTimerStatus.MagDraaien, string? accommodatie = null, DateOnly? datum = null, Func<DateTime>? nu = null)
        => VeldplannerOverlayCore.HaalBlokkenAsync(() => toegang, () => (client, null), "Rol", accommodatie ?? "Sportpark F-1",
            datum ?? new DateOnly(2030, 1, 5), NullLogger.Instance, nu);

    [Fact]
    public async Task ZoektFacilityOpAccommodatienaamEnGeeftDeBlokken()
    {
        var blokken = await Haal(Client("F-1").Object, accommodatie: Naam("F-1"), datum: new DateOnly(2030, 2, 2));
        blokken.Should().ContainSingle().Which.Should().Be(Blok);
    }

    [Fact]
    public async Task ExtensieUitOfEgressDicht_GeeftNull_ZonderSportlinkAanroep()
    {
        var mock = Client("F-2");
        var resultaat = await HaalMetStatus(mock.Object, accommodatie: Naam("F-2"), toegang: SportlinkTimerStatus.ExtensieUit);

        resultaat.Blokken.Should().BeNull();
        resultaat.Status.Reden.Should().Be(SportlinkVeldplannerStatus.RedenExtensieUit);
        mock.Verify(c => c.GetVeldplannerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SportlinkFout_GeeftNull_EenTerugvalEnGeenException_MetDeReden()
    {
        var resultaat = await HaalMetStatus(Client("F-3", status: SportlinkClubCallStatus.NetwerkFout).Object, accommodatie: Naam("F-3"), datum: new DateOnly(2030, 3, 2));
        resultaat.Blokken.Should().BeNull();
        resultaat.Status.Should().Be(SportlinkVeldplannerStatus.Terugval(SportlinkVeldplannerStatus.RedenNetwerkFout));
    }

    [Fact]
    public async Task GeslaagdeRaadpleging_HeeftStatusOk_MetTijdstip()
    {
        var nu = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var resultaat = await HaalMetStatus(Client("F-13").Object, accommodatie: Naam("F-13"), datum: new DateOnly(2030, 6, 1), nu: () => nu);
        resultaat.Status.Should().Be(SportlinkVeldplannerStatus.Ok(nu));
    }

    [Fact]
    public async Task OnbekendeAccommodatie_GeeftNull_MetRedenAccommodatieOnbekend()
    {
        var resultaat = await HaalMetStatus(Client("F-4").Object, accommodatie: "Ergens anders");
        resultaat.Blokken.Should().BeNull();
        resultaat.Status.Reden.Should().Be(SportlinkVeldplannerStatus.RedenAccommodatieOnbekend);
    }

    /// <summary>#1774: de echte productie-oorzaak — een verlopen koppeling werd gelogd als "accommodatie niet gevonden".</summary>
    [Fact]
    public async Task MislukteLocatielijst_IsGeenOnbekendeAccommodatie_MaarDeAanroepstatus()
    {
        var mock = new Mock<ISportlinkClubClient>();
        mock.Setup(c => c.GetClubMatchPickListsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<SportlinkClubMatchPickLists>(SportlinkClubCallStatus.HerkoppelingVereist, null, "Refresh token is ongeldig", null));

        var resultaat = await HaalMetStatus(mock.Object, accommodatie: "Sportpark Uniek Veertien");

        resultaat.Blokken.Should().BeNull();
        resultaat.Status.Reden.Should().Be(SportlinkVeldplannerStatus.RedenKoppelingVerlopen);
        resultaat.Status.KoppelingHerstelNodig.Should().BeTrue();
        mock.Verify(c => c.GetVeldplannerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GeenAccommodatieInstelling_GeeftNull()
        => (await HaalMetStatus(Client("F-5").Object, accommodatie: "  ")).Status.Reden.Should().Be(SportlinkVeldplannerStatus.RedenGeenAccommodatie);

    [Fact]
    public async Task UitzonderingInClient_GeeftNull()
    {
        var mock = new Mock<ISportlinkClubClient>();
        mock.Setup(c => c.GetClubMatchPickListsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boem"));
        var resultaat = await HaalMetStatus(mock.Object, accommodatie: "Sportpark Uniek Zes");
        resultaat.Blokken.Should().BeNull();
        resultaat.Status.Reden.Should().Be(SportlinkVeldplannerStatus.RedenOnverwacht);
    }

    [Fact]
    public async Task ClientNietGeconfigureerd_GeeftDieReden()
        => (await HaalMetStatus(null, accommodatie: "Sportpark Uniek Vijftien")).Status.Reden.Should().Be(SportlinkVeldplannerStatus.RedenClientNietGeconfigureerd);

    [Fact]
    public async Task BinnenDeGeldigheid_GaatMaarEenKeerNaarSportlink()
    {
        var mock = Client("F-7");
        var nu = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var datum = new DateOnly(2030, 4, 6);

        await Haal(mock.Object, accommodatie: Naam("F-7"), datum: datum, nu: () => nu);
        await Haal(mock.Object, accommodatie: Naam("F-7"), datum: datum, nu: () => nu.AddSeconds(30));
        await Haal(mock.Object, accommodatie: Naam("F-7"), datum: datum, nu: () => nu.AddSeconds(90));

        mock.Verify(c => c.GetVeldplannerAsync(It.IsAny<string>(), "F-7", datum, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private sealed record Regel(string Wedstrijd, string? Tijd, bool NietInSportlink = false, string? Bron = null);

    private static async Task<IReadOnlyList<Regel>> Samenvoeg(ISportlinkClubClient client, string clubCode, string accommodatie, params Regel[] eigen)
        => (await SamenvoegMetStatus(client, clubCode, accommodatie, eigen)).Wedstrijden;

    private static Task<VeldbezettingAntwoord<Regel>> SamenvoegMetStatus(ISportlinkClubClient client, string clubCode, string accommodatie, params Regel[] eigen)
        => VeldplannerOverlayCore.SamenvoegAsync<Regel>(eigen, clubCode, () => SportlinkTimerStatus.MagDraaien, () => (client, null), "Rol", accommodatie,
            new DateOnly(2030, 5, 4), NullLogger.Instance,
            sleutel: r => (r.Wedstrijd, r.Tijd),
            overschrijf: (r, b) => r with { Tijd = b.StartTijd, Bron = "Sportlink" },
            nieuw: b => new Regel(b.Label, b.StartTijd, false, "Sportlink"),
            nietInSportlink: r => r with { NietInSportlink = true },
            sorteerTijd: r => r.Tijd);

    [Fact]
    public async Task SamenvoegAsync_KoppeltEenBekendeWedstrijd_EnToontDeRest()
    {
        var resultaat = await Samenvoeg(Client("F-8").Object, "CLUB", Naam("F-8"), new Regel("A - B", "09:30"));

        resultaat.Should().ContainSingle().Which.Should().Be(new Regel("A - B", "09:00", false, "Sportlink"));
    }

    [Fact]
    public async Task SamenvoegAsync_ZonderEigenRegels_ToontToch_HetSportlinkBlok()
        => (await Samenvoeg(Client("F-10").Object, "CLUB", Naam("F-10"))).Should().ContainSingle()
            .Which.Bron.Should().Be("Sportlink", "de velden kunnen door een andere club bezet zijn (#1582)");

    [Fact]
    public async Task SamenvoegAsync_EenRegelDieSportlinkNietKent_KrijgtEenMarkering()
        => (await Samenvoeg(Client("F-11").Object, "CLUB", Naam("F-11"), new Regel("Ander - Team", "12:00")))
            .Should().Contain(r => r.Wedstrijd == "Ander - Team" && r.NietInSportlink);

    [Fact]
    public async Task SamenvoegAsync_SportlinkOnbereikbaar_LaatDePlanningOngewijzigd()
    {
        var eigen = new Regel("A - B", "09:30");
        var resultaat = await SamenvoegMetStatus(Client("F-12", status: SportlinkClubCallStatus.NetwerkFout).Object, "CLUB", Naam("F-12"), eigen);
        resultaat.Wedstrijden.Should().ContainSingle().Which.Should().Be(eigen);
        resultaat.Sportlink.Geraadpleegd.Should().BeFalse("de terugval is nooit stil (#1774)");
        resultaat.Sportlink.Reden.Should().Be(SportlinkVeldplannerStatus.RedenNetwerkFout);
    }

    [Fact]
    public async Task SamenvoegAsync_Geslaagd_HeeftStatusGeraadpleegd()
        => (await SamenvoegMetStatus(Client("F-16").Object, "CLUB", Naam("F-16"), new Regel("A - B", "09:30"))).Sportlink.Geraadpleegd.Should().BeTrue();

    [Fact]
    public async Task SamenvoegAsync_Democlub_VraagNooitNaarSportlink()
    {
        var mock = Client("F-9");
        var eigen = new Regel("A - B", "09:30");
        var resultaat = await SamenvoegMetStatus(mock.Object, "allstars", Naam("F-9"), eigen);
        resultaat.Wedstrijden.Should().ContainSingle().Which.Should().Be(eigen);
        resultaat.Sportlink.Reden.Should().Be(SportlinkVeldplannerStatus.RedenDemoclub);
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public void SorteerSleutel_ZetRegelsZonderTijdAchteraan()
        => VeldplannerOverlayCore.SorteerSleutel(" ").Should().Be("99:99");
}
