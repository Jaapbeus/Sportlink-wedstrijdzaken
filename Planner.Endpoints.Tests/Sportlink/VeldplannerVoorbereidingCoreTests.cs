using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>
/// #1917: de timer die de veldplanner-blokken en wedstrijdnummers vooraf vastlegt. Fictieve ID's; elke test een eigen
/// facility-ID en eigen datums, want de facility- en blokkencache van de overlay zijn bewust statisch.
/// </summary>
public class VeldplannerVoorbereidingCoreTests
{
    private static string Naam(string id) => "Sportpark " + id;

    private static SportlinkVeldplannerBlok Blok(string id, string start = "10:00")
        => new($"Thuis {id} - Uit {id}", "veld 1 A", start, 0.5m, 75, id);

    /// <summary>Client met een veldplanner per datum; <c>null</c> voor een datum = Sportlink geeft een netwerkfout.</summary>
    private static Mock<ISportlinkClubClient> Client(string facilityId, Func<DateOnly, IReadOnlyList<SportlinkVeldplannerBlok>?> blokkenPerDatum)
    {
        var mock = new Mock<ISportlinkClubClient>();
        mock.Setup(c => c.GetClubMatchPickListsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SportlinkClubResponse<SportlinkClubMatchPickLists>(SportlinkClubCallStatus.Ok,
                new SportlinkClubMatchPickLists(Array.Empty<SportlinkPickListItem>(),
                    new[] { new SportlinkPickListItem(facilityId, Naam(facilityId)) }), null, 200));
        mock.Setup(c => c.GetVeldplannerAsync(It.IsAny<string>(), facilityId, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, DateOnly datum, CancellationToken _) =>
            {
                var blokken = blokkenPerDatum(datum);
                return blokken == null
                    ? new SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>>(SportlinkClubCallStatus.NetwerkFout, null, null, null)
                    : new SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>>(SportlinkClubCallStatus.Ok, blokken, null, 200);
            });
        // Het wedstrijdnummer is het getal achter het laatste koppelteken van het fictieve ID ("M-101" -> 101).
        mock.Setup(c => c.GetMatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string id, CancellationToken _) =>
                new SportlinkClubResponse<SportlinkMatch>(SportlinkClubCallStatus.Ok,
                    new SportlinkMatch { ExternalMatchId = id[(id.LastIndexOf('-') + 1)..] }, null, 200));
        return mock;
    }

    private sealed class GeheugenStore(bool kapot = false) : IVeldplannerBlokStore
    {
        public Dictionary<string, VeldplannerBlokRij> Rijen { get; } = new(StringComparer.Ordinal);
        public int Schrijfaanroepen { get; private set; }

        public Task<IReadOnlyDictionary<string, BlokOpslagStand>> LeesDagAsync(string clubCode, DateOnly datum, CancellationToken ct = default)
            => kapot ? throw new InvalidOperationException("database weg")
                : Task.FromResult<IReadOnlyDictionary<string, BlokOpslagStand>>(Rijen.Values.Where(r => r.Datum == datum)
                    .ToDictionary(r => r.PublicMatchId, r => new BlokOpslagStand(r.RijHash, r.ExternalMatchId), StringComparer.Ordinal));

        public Task SchrijfAsync(string clubCode, DateOnly datum, IReadOnlyList<VeldplannerBlokRij> rijen,
            IReadOnlyList<string> verwijderSleutels, DateOnly bewaarVanaf, CancellationToken ct = default)
        {
            Schrijfaanroepen++;
            foreach (var rij in rijen) Rijen[rij.PublicMatchId] = rij;
            foreach (var sleutel in verwijderSleutels) Rijen.Remove(sleutel);
            return Task.CompletedTask;
        }
    }

    private static Task<VeldplannerVoorbereidingResultaat> Voer(
        ISportlinkClubClient client, IVeldplannerBlokStore store, string facilityId, IReadOnlyList<DateOnly> datums, string club = "CLUB")
        => VeldplannerVoorbereidingCore.VoerUitAsync(client, store, club, "Rol", Naam(facilityId), datums, NullLogger.Instance, new DateOnly(2031, 1, 1));

    [Fact]
    public async Task LegtBlokkenEnNummersVast_VoorElkeDatum()
    {
        var d1 = new DateOnly(2031, 3, 1); var d2 = new DateOnly(2031, 3, 8);
        var client = Client("V-1", d => d == d1 ? new[] { Blok("M-101"), Blok("M-102", "11:00") } : new[] { Blok("M-201") });
        var store = new GeheugenStore();

        var r = await Voer(client.Object, store, "V-1", new[] { d1, d2 });

        (r.Datums, r.Mislukt, r.Toegevoegd, r.NummersOpgevraagd, r.NummersOnbekend).Should().Be((2, 0, 3, 3, 0));
        store.Rijen.Values.Select(x => (x.PublicMatchId, x.ExternalMatchId)).Should().BeEquivalentTo(
            new[] { ("M-101", (long?)101), ("M-102", 102L), ("M-201", 201L) });
    }

    [Fact]
    public async Task EenTweedeRun_ZonderWijzigingen_SchrijftNietsEnVraagGeenNummersOp()
    {
        var d = new DateOnly(2031, 4, 5);
        var client = Client("V-2", _ => new[] { Blok("M-301"), Blok("M-302", "11:00") });
        var store = new GeheugenStore();
        await Voer(client.Object, store, "V-2", new[] { d });
        var schrijfaanroepen = store.Schrijfaanroepen;

        var tweede = await Voer(client.Object, store, "V-2", new[] { d });

        (tweede.Toegevoegd, tweede.Gewijzigd, tweede.NummersOpgevraagd).Should().Be((0, 0, 0));
        store.Schrijfaanroepen.Should().Be(schrijfaanroepen, "alleen verschillen worden geschreven");
        client.Verify(c => c.GetMatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EenMislukteDatum_StoptDeAndereDatumsNiet()
    {
        var kapot = new DateOnly(2031, 5, 3); var goed = new DateOnly(2031, 5, 10);
        var client = Client("V-3", d => d == kapot ? null : new[] { Blok("M-401") });
        var store = new GeheugenStore();

        var r = await Voer(client.Object, store, "V-3", new[] { kapot, goed });

        (r.Mislukt, r.Toegevoegd).Should().Be((1, 1));
        store.Rijen.Keys.Should().Equal("M-401");
    }

    [Fact]
    public async Task EenFoutInDeOpslag_TeltAlsMislukt_ZonderUitzondering()
    {
        var client = Client("V-4", _ => new[] { Blok("M-501") });

        var r = await Voer(client.Object, new GeheugenStore(kapot: true), "V-4", new[] { new DateOnly(2031, 6, 7) });

        (r.Mislukt, r.Toegevoegd).Should().Be((1, 0));
    }

    [Fact]
    public async Task NaDrieOpeenvolgendeMislukteDatums_StoptDeRun()
    {
        var client = Client("V-5", _ => null);
        var datums = Enumerable.Range(0, 6).Select(i => new DateOnly(2031, 7, 5).AddDays(7 * i)).ToList();

        var r = await Voer(client.Object, new GeheugenStore(), "V-5", datums);

        r.Datums.Should().Be(VeldplannerVoorbereidingCore.MaxOpeenvolgendMislukt);
        r.Mislukt.Should().Be(VeldplannerVoorbereidingCore.MaxOpeenvolgendMislukt);
        client.Verify(c => c.GetVeldplannerAsync(It.IsAny<string>(), "V-5", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()),
            Times.Exactly(VeldplannerVoorbereidingCore.MaxOpeenvolgendMislukt));
    }

    [Fact]
    public async Task EenGeslaagdeDatum_ZetDeTellerVoorOpeenvolgendMisluktTerug()
    {
        var mis = new HashSet<DateOnly> { new(2031, 8, 2), new(2031, 8, 9), new(2031, 8, 23), new(2031, 8, 30) };
        var datums = new[] { new DateOnly(2031, 8, 2), new DateOnly(2031, 8, 9), new DateOnly(2031, 8, 16), new DateOnly(2031, 8, 23), new DateOnly(2031, 8, 30) };
        var client = Client("V-6", d => mis.Contains(d) ? null : new[] { Blok("M-601") });

        var r = await Voer(client.Object, new GeheugenStore(), "V-6", datums);

        r.Datums.Should().Be(5, "twee mislukt, één geslaagd, twee mislukt: nooit drie achter elkaar");
        r.Mislukt.Should().Be(4);
    }

    [Fact]
    public async Task Democlub_DoetNiets_EnVraagNooitNaarSportlink()
    {
        var client = Client("V-7", _ => new[] { Blok("M-701") });

        var r = await Voer(client.Object, new GeheugenStore(), "V-7", new[] { new DateOnly(2031, 9, 6) }, club: "allstars");

        r.Should().Be(new VeldplannerVoorbereidingResultaat(0, 0, 0, 0, 0, 0, 0));
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ZonderDatums_DoetNiets()
    {
        var client = Client("V-8", _ => new[] { Blok("M-801") });

        var r = await Voer(client.Object, new GeheugenStore(), "V-8", Array.Empty<DateOnly>());

        r.Datums.Should().Be(0);
        client.VerifyNoOtherCalls();
    }

    // ── De volledige timerstroom (VoerTimerUitAsync) ──

    [Fact]
    public async Task Timer_ZonderClient_DoetNiets_EnEvalueertDeClubcodeNiet()
    {
        var clubcodeGelezen = false;
        var datumsGezocht = false;

        await VeldplannerVoorbereidingCore.VoerTimerUitAsync(null, () => { clubcodeGelezen = true; return "CLUB"; }, "Rol", "Sportpark X",
            (_, _, _) => { datumsGezocht = true; return Task.FromResult<IReadOnlyList<DateOnly>>(Array.Empty<DateOnly>()); },
            new GeheugenStore(), NullLogger.Instance);

        clubcodeGelezen.Should().BeFalse("een uitgeschakelde timer mag niet falen op een ontbrekende instelling");
        datumsGezocht.Should().BeFalse();
    }

    [Fact]
    public async Task Timer_ZoektDatumsVanVandaagTotVandaagPlus28_EnVoertDeRunUit()
    {
        var datum = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3);
        var client = Client("V-9", _ => new[] { Blok("M-901") });
        var store = new GeheugenStore();
        (string Club, DateOnly Van, DateOnly Tot)? gevraagd = null;

        await VeldplannerVoorbereidingCore.VoerTimerUitAsync(client.Object, () => "CLUB", "Rol", Naam("V-9"),
            (club, van, tot) => { gevraagd = (club, van, tot); return Task.FromResult<IReadOnlyList<DateOnly>>(new[] { datum }); },
            store, NullLogger.Instance);

        var vandaag = DateOnly.FromDateTime(DateTime.UtcNow);
        gevraagd.Should().NotBeNull();
        gevraagd!.Value.Club.Should().Be("CLUB");
        (gevraagd.Value.Tot.DayNumber - gevraagd.Value.Van.DayNumber).Should().Be(SportlinkEndpointCore.VeldplannerVoorbereidDagen).And.Be(28);
        gevraagd.Value.Van.Should().BeOnOrBefore(vandaag.AddDays(1)).And.BeOnOrAfter(vandaag.AddDays(-1));
        store.Rijen.Keys.Should().Equal("M-901");
    }

    [Fact]
    public async Task Timer_FoutBijHetZoekenVanDatums_StoptZonderUitzondering_EnZonderSportlinkAanroep()
    {
        var client = Client("V-10", _ => new[] { Blok("M-1001") });

        var act = () => VeldplannerVoorbereidingCore.VoerTimerUitAsync(client.Object, () => "CLUB", "Rol", Naam("V-10"),
            (_, _, _) => throw new InvalidOperationException("database weg"), new GeheugenStore(), NullLogger.Instance);

        await act.Should().NotThrowAsync();
        client.Verify(c => c.GetVeldplannerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Timer_HeeftEenVasteNaamEnTweeRunsPerDag()
    {
        VeldplannerVoorbereidingCore.FunctieNaam.Should().Be("SportlinkVeldplannerVoorbereiding");
        VeldplannerVoorbereidingCore.Schema.Should().Be("0 30 6,18 * * *", "06:30 en 18:30 UTC, buiten de sync (0 0 8-20/2) en de warmup (06:00)");
    }
}
