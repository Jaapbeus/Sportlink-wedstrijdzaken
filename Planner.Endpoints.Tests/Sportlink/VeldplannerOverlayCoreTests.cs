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
    private static readonly SportlinkVeldplannerBlok Blok = new("A - B", "veld 1", "09:00", 1m, 75, "M-A");

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

    private sealed record Regel(string Wedstrijd, string? Tijd, bool NietInSportlink = false, string? Bron = null, string? PublicMatchId = null, long? Nummer = null);

    private static async Task<IReadOnlyList<Regel>> Samenvoeg(ISportlinkClubClient client, string clubCode, string accommodatie, params Regel[] eigen)
        => (await SamenvoegMetStatus(client, clubCode, accommodatie, eigen)).Wedstrijden;

    private static Task<VeldbezettingAntwoord<Regel>> SamenvoegMetStatus(ISportlinkClubClient client, string clubCode, string accommodatie, params Regel[] eigen)
        => VeldplannerOverlayCore.SamenvoegAsync<Regel>(eigen, clubCode, () => SportlinkTimerStatus.MagDraaien, () => (client, null), "Rol", accommodatie,
            new DateOnly(2030, 5, 4), NullLogger.Instance,
            sleutel: r => new EigenSleutel(r.PublicMatchId, r.Nummer),
            overschrijf: (r, b) => r with { Tijd = b.StartTijd, Bron = "Sportlink" },
            nieuw: b => new Regel(b.Label, b.StartTijd, false, "Sportlink"),
            nietInSportlink: r => r with { NietInSportlink = true },
            sorteerTijd: r => r.Tijd);

    [Fact]
    public async Task SamenvoegAsync_KoppeltEenBekendeWedstrijd_EnToontDeRest()
    {
        var resultaat = await Samenvoeg(Client("F-8").Object, "CLUB", Naam("F-8"), new Regel("A - B", "09:30", PublicMatchId: "M-A"));

        resultaat.Should().ContainSingle().Which.Should().Be(new Regel("A - B", "09:00", false, "Sportlink", "M-A"));
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
        => (await SamenvoegMetStatus(Client("F-16").Object, "CLUB", Naam("F-16"), new Regel("A - B", "09:30", PublicMatchId: "M-A"))).Sportlink.Geraadpleegd.Should().BeTrue();

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

    private const string Sentinel = "sentinel-clientid-123";

    /// <summary>Legt bericht én uitzonderingsobject vast, zodat het beperkte logcontract meetbaar is.</summary>
    private sealed class VastleggendeLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(string Bericht, Exception? Uitzondering)> Regels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Regels.Add((formatter(state, null), exception));
    }

    private static void ControleerLogcontract(VastleggendeLogger logger)
    {
        logger.Regels.Should().NotBeEmpty("de fout moet zichtbaar blijven in het log");
        foreach (var (bericht, uitzondering) in logger.Regels)
        {
            uitzondering.Should().BeNull("alleen het uitzonderingstype hoort in het log (hoofd- en binnenste uitzondering kunnen een URL dragen)");
            bericht.Should().NotContain(Sentinel);
        }
    }

    /// <summary>Codex-review #1917: de timer gebruikt dit pad; een onverwachte fout logt geen uitzonderingsobject.</summary>
    [Fact]
    public async Task HaalBlokkenAsync_OnverwachteFout_LogtGeenUitzonderingsobject()
    {
        var mock = Client("F-20");
        mock.Setup(c => c.GetVeldplannerAsync(It.IsAny<string>(), "F-20", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException($"fout naar https://host/?clientId={Sentinel}", new IOException(Sentinel)));
        var logger = new VastleggendeLogger();

        var resultaat = await VeldplannerOverlayCore.HaalBlokkenAsync(() => SportlinkTimerStatus.MagDraaien, () => (mock.Object, null), "Rol",
            Naam("F-20"), new DateOnly(2030, 7, 6), logger);

        resultaat.Blokken.Should().BeNull();
        ControleerLogcontract(logger);
    }

    /// <summary>Codex-review #1917: een mislukte opslag logt alleen het type, ook met een binnenste uitzondering.</summary>
    [Fact]
    public async Task SamenvoegAsync_OpslagFout_LogtGeenUitzonderingsobject()
    {
        var logger = new VastleggendeLogger();
        var store = new Mock<IVeldplannerBlokStore>();
        store.Setup(s => s.LeesDagAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"db {Sentinel}", new Exception(Sentinel)));

        var resultaat = await VeldplannerOverlayCore.SamenvoegAsync<Regel>([], "CLUB", () => SportlinkTimerStatus.MagDraaien,
            () => (Client("F-21").Object, null), "Rol", Naam("F-21"), new DateOnly(2030, 7, 13), logger,
            sleutel: r => new EigenSleutel(r.PublicMatchId, r.Nummer),
            overschrijf: (r, b) => r with { Tijd = b.StartTijd, Bron = "Sportlink" },
            nieuw: b => new Regel(b.Label, b.StartTijd, false, "Sportlink"),
            nietInSportlink: r => r with { NietInSportlink = true },
            sorteerTijd: r => r.Tijd, store: store.Object);

        resultaat.Wedstrijden.Should().ContainSingle("de Planning blijft werken zonder opslag");
        ControleerLogcontract(logger);
    }

    /// <summary>Een in-geheugen-store die bijhoudt wat de overlay wegschrijft.</summary>
    private sealed class GeheugenStore(bool kapot = false) : IVeldplannerBlokStore
    {
        public Dictionary<string, VeldplannerBlokRij> Rijen { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, BlokOpslagStand>> LeesDagAsync(string clubCode, DateOnly datum, CancellationToken ct = default)
            => kapot ? throw new InvalidOperationException("database weg")
                : Task.FromResult<IReadOnlyDictionary<string, BlokOpslagStand>>(Rijen.Values.Where(r => r.Datum == datum)
                    .ToDictionary(r => r.PublicMatchId, r => new BlokOpslagStand(r.RijHash, r.ExternalMatchId), StringComparer.Ordinal));

        public Task SchrijfAsync(string clubCode, DateOnly datum, IReadOnlyList<VeldplannerBlokRij> rijen,
            IReadOnlyList<string> verwijderSleutels, DateOnly bewaarVanaf, CancellationToken ct = default)
        {
            foreach (var rij in rijen) Rijen[rij.PublicMatchId] = rij;
            foreach (var sleutel in verwijderSleutels) Rijen.Remove(sleutel);
            return Task.CompletedTask;
        }
    }

    private static Task<VeldbezettingAntwoord<Regel>> SamenvoegMetStore(
        ISportlinkClubClient client, IVeldplannerBlokStore store, string accommodatie, params Regel[] eigen)
        => VeldplannerOverlayCore.SamenvoegAsync<Regel>(eigen, "CLUB", () => SportlinkTimerStatus.MagDraaien, () => (client, null), "Rol", accommodatie,
            new DateOnly(2030, 5, 4), NullLogger.Instance,
            sleutel: r => new EigenSleutel(r.PublicMatchId, r.Nummer),
            overschrijf: (r, b) => r with { Tijd = b.StartTijd, Bron = "Sportlink" },
            nieuw: b => new Regel(b.Label, b.StartTijd, false, "Sportlink"),
            nietInSportlink: r => r with { NietInSportlink = true },
            sorteerTijd: r => r.Tijd, store: store);

    /// <summary>#1915: de dataservice noemt een club anders dan de veldplanner; de koppeling loopt via het opgevraagde wedstrijdnummer.</summary>
    [Fact]
    public async Task SamenvoegAsync_KoppeltViaHetOpgevraagdeWedstrijdnummer_ZonderNaam_EnBewaartHetNummer()
    {
        var client = Client("F-20");
        client.Setup(c => c.GetMatchAsync(It.IsAny<string>(), "M-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<SportlinkMatch>(SportlinkClubCallStatus.Ok, new SportlinkMatch { ExternalMatchId = "101066" }, null, 200));
        var store = new GeheugenStore();

        var antwoord = await SamenvoegMetStore(client.Object, store, Naam("F-20"),
            new Regel("Heel Andere Clubnaam - Team", "10:00", Nummer: 101066));

        antwoord.Wedstrijden.Should().ContainSingle("één wedstrijd is één regel, hoe de club ook heet")
            .Which.Bron.Should().Be("Sportlink");
        store.Rijen["M-A"].ExternalMatchId.Should().Be(101066);
    }

    [Fact]
    public async Task SamenvoegAsync_TweedeAanvraag_VraagHetBewaardeNummerNietOpnieuwOp()
    {
        var client = Client("F-21");
        client.Setup(c => c.GetMatchAsync(It.IsAny<string>(), "M-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<SportlinkMatch>(SportlinkClubCallStatus.Ok, new SportlinkMatch { ExternalMatchId = "5" }, null, 200));
        var store = new GeheugenStore();
        var regel = new Regel("X - Y", "10:00", Nummer: 5);

        await SamenvoegMetStore(client.Object, store, Naam("F-21"), regel);
        var tweede = await SamenvoegMetStore(client.Object, store, Naam("F-21"), regel);

        tweede.Wedstrijden.Should().ContainSingle().Which.Bron.Should().Be("Sportlink");
        client.Verify(c => c.GetMatchAsync(It.IsAny<string>(), "M-A", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SamenvoegAsync_StoreKapot_PlanningWerktDoor_MetZichtbareAfwijking()
    {
        var antwoord = await SamenvoegMetStore(Client("F-22").Object, new GeheugenStore(kapot: true), Naam("F-22"),
            new Regel("X - Y", "10:00", Nummer: 5));

        antwoord.Sportlink.Geraadpleegd.Should().BeTrue();
        antwoord.Wedstrijden.Should().HaveCount(2, "zonder wedstrijdnummer koppelt niets: de eigen regel en het blok blijven beide zichtbaar");
        antwoord.Wedstrijden.Should().Contain(r => r.NietInSportlink);
    }

    [Fact]
    public void SorteerSleutel_ZetRegelsZonderTijdAchteraan()
        => VeldplannerOverlayCore.SorteerSleutel(" ").Should().Be("99:99");
}
