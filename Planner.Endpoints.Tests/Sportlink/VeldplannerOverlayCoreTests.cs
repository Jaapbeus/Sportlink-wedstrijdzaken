using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
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

    private static Task<IReadOnlyList<SportlinkVeldplannerBlok>?> Haal(
        ISportlinkClubClient? client, IActionResult? toggleFout = null, string? accommodatie = null, DateOnly? datum = null, Func<DateTime>? nu = null)
        => VeldplannerOverlayCore.HaalBlokkenAsync(() => toggleFout, () => (client, null), "Rol", accommodatie ?? "Sportpark F-1",
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
        var blokken = await Haal(mock.Object, accommodatie: Naam("F-2"), toggleFout: new ObjectResult(new { }) { StatusCode = 409 });

        blokken.Should().BeNull();
        mock.Verify(c => c.GetVeldplannerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SportlinkFout_GeeftNull_EenTerugvalEnGeenException()
        => (await Haal(Client("F-3", status: SportlinkClubCallStatus.NetwerkFout).Object, accommodatie: Naam("F-3"), datum: new DateOnly(2030, 3, 2))).Should().BeNull();

    [Fact]
    public async Task OnbekendeAccommodatie_GeeftNull()
        => (await Haal(Client("F-4").Object, accommodatie: "Ergens anders")).Should().BeNull();

    [Fact]
    public async Task GeenAccommodatieInstelling_GeeftNull()
        => (await Haal(Client("F-5").Object, accommodatie: "  ")).Should().BeNull();

    [Fact]
    public async Task UitzonderingInClient_GeeftNull()
    {
        var mock = new Mock<ISportlinkClubClient>();
        mock.Setup(c => c.GetClubMatchPickListsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boem"));
        (await Haal(mock.Object, accommodatie: "Sportpark Uniek Zes")).Should().BeNull();
    }

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

    private static Task<IReadOnlyDictionary<int, SportlinkVeldplannerBlok>> Koppel(ISportlinkClubClient client, string clubCode, string accommodatie)
        => VeldplannerOverlayCore.KoppelAsync(new[] { ("A - B", (string?)"09:00") }, clubCode, () => null,
            () => (client, null), "Rol", accommodatie, new DateOnly(2030, 5, 4), NullLogger.Instance);

    [Fact]
    public async Task KoppelAsync_KoppeltEenBekendeWedstrijd()
        => (await Koppel(Client("F-8").Object, "CLUB", Naam("F-8"))).Should().ContainKey(0);

    [Fact]
    public async Task KoppelAsync_Democlub_VraagNooitNaarSportlink()
    {
        var mock = Client("F-9");
        (await Koppel(mock.Object, "allstars", Naam("F-9"))).Should().BeEmpty();
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public void SorteerSleutel_ZetRegelsZonderTijdAchteraan()
        => VeldplannerOverlayCore.SorteerSleutel(" ").Should().Be("99:99");
}
