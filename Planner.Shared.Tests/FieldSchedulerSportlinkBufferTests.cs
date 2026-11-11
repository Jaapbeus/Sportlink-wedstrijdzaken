using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// #1559: wedstrijden die al in Sportlink staan zijn leidend en houden geen rekening met de app-buffer.
/// De buffer is per slot: een Sportlink-slot draagt 0 minuten, een app-slot de club-/teambuffer.
/// </summary>
public class FieldSchedulerSportlinkBufferTests
{
    private const int AlgemeneBuffer = 15;

    private static FieldScheduler MaakScheduler(Dictionary<string, (int, int)>? teamBuffers = null)
    {
        var beschikbaarheid = new List<VeldBeschikbaarheidInfo>
        {
            new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(9, 0), BeschikbaarTot = new TimeOnly(18, 0) }
        };
        var velden = new List<VeldInfo> { new() { VeldNummer = 1, VeldNaam = "veld 1" } };
        return new FieldScheduler(beschikbaarheid, velden, AlgemeneBuffer, teamBuffers);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(840)]
    [InlineData(1440)]
    [InlineData(1450)]
    [InlineData(int.MaxValue)]
    public void ExactSlotRejectsNonPositiveAndDayCrossingDuration(int minutes)
    {
        var scheduler = MaakScheduler();
        scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0, 30), minutes, 1m, null,
            "JO11-1", false, out var slot).Should().BeFalse();
        slot.Should().BeNull();
        scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0, 30), 60, 1m, null,
            "JO11-1", false, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(1439, true)]
    [InlineData(1440, false)]
    [InlineData(1450, false)]
    [InlineData(int.MaxValue, false)]
    public void ExactSlotBoundsWholeDayWithoutLosingSeconds(int minutes, bool expected)
    {
        var scheduler = new FieldScheduler(
            [new() { VeldNummer = 1, BeschikbaarVanaf = TimeOnly.MinValue, BeschikbaarTot = TimeOnly.MaxValue }],
            [new() { VeldNummer = 1, VeldNaam = "veld 1" }], 0);
        scheduler.TryOccupyExactSlot(1, new TimeOnly(0, 0, 30), minutes, 1m, null,
            "JO11-1", false, out var slot).Should().Be(expected);
        if (expected) slot!.EindTijd.Should().Be(new TimeOnly(23, 59, 30));
    }

    [Theory]
    [InlineData(0, 60, true)]
    [InlineData(30, 60, false)]
    [InlineData(30, 59, true)]
    public void ExactSlotHonorsClosingBoundaryAndSourceSeconds(int seconds, int minutes, bool expected)
    {
        var scheduler = MaakScheduler();
        scheduler.TryOccupyExactSlot(1, new TimeOnly(17, 0, seconds), minutes, 1m, null,
            "JO11-1", false, out var slot).Should().Be(expected);
        if (expected) slot!.EindTijd.Should().Be(new TimeOnly(17, 0, seconds).AddMinutes(minutes));
    }

    [Fact]
    public void TryOccupyExactSlot_AccepteertExactSlotZonderTijdverschuiving()
    {
        var scheduler = MaakScheduler();

        var geplaatst = scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, 1m, null, "JO11-1", false, out var slot);

        geplaatst.Should().BeTrue();
        slot!.AanvangsTijd.Should().Be(new TimeOnly(10, 0));
        slot.EindTijd.Should().Be(new TimeOnly(11, 0));
    }

    [Fact]
    public void TryOccupyExactSlot_WeigertConflictZonderEenAlternatiefTeKiezen()
    {
        var scheduler = MaakScheduler();
        scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, 1m, null, "JO11-1", false, out _).Should().BeTrue();

        var geplaatst = scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, 1m, null, "JO13-1", false, out var slot);

        geplaatst.Should().BeFalse();
        slot.Should().BeNull();
    }

    [Fact]
    public void TryOccupyExactSlot_QuarterlaneBehoudtHandmatigeKeuze()
    {
        var scheduler = MaakScheduler();

        var geplaatst = scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .25m, "A2", "JO11-1", false, out var slot);

        geplaatst.Should().BeTrue();
        slot!.VeldSubpositie.Should().Be("A2");
    }

    [Theory]
    [InlineData(.25, "A")]
    [InlineData(.5, "A1")]
    [InlineData(.5, "B2")]
    [InlineData(1, "B")]
    public void TryOccupyExactSlot_WeigertOnverenigbareSubpositie(decimal fractie, string subpositie)
    {
        var scheduler = MaakScheduler();

        var geplaatst = scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, fractie, subpositie, "JO11-1", false, out var slot);

        geplaatst.Should().BeFalse();
        slot.Should().BeNull();
    }

    [Fact]
    public void TryOccupyExactSlot_HalfVeldAConflictMetKwartA1MaarNietMetA2()
    {
        var conflict = MaakScheduler();
        conflict.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .25m, "A1", "JO13-1", false, out _).Should().BeTrue();
        conflict.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .5m, "A", "JO11-1", false, out _).Should().BeFalse();

        var vrij = MaakScheduler();
        vrij.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .25m, "A1", "JO13-1", false, out _).Should().BeTrue();
        vrij.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .25m, "A2", "JO11-1", false, out _).Should().BeTrue();
    }

    [Fact]
    public void SportlinkWedstrijd_BezetSpeelduurPlusVijftien_EnVolgendeSportlinkWedstrijdSluitDirectAan()
    {
        var scheduler = MaakScheduler();
        int duur = VeldbezettingDuur.Bepaal(60, 115);
        duur.Should().Be(75);

        var eerste = scheduler.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, duur, 0, "JO11-1", sportlinkLeidend: true);
        var tweede = scheduler.FindAndOccupyNearTime(new TimeOnly(11, 15), 1.0m, duur, 0, "JO13-1", sportlinkLeidend: true);

        eerste.Should().NotBeNull();
        eerste!.EindTijd.Should().Be(new TimeOnly(11, 15));
        tweede.Should().NotBeNull();
        tweede!.AanvangsTijd.Should().Be(new TimeOnly(11, 15)); // geen buffer ertussen
    }

    [Fact]
    public void SportlinkWedstrijd_NegeertTeamBuffer_AppWedstrijdMetDezelfdeTeamregelKrijgtHemWel()
    {
        var teamBuffers = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase) { ["Heren 1"] = (60, 60) };

        var sportlink = MaakScheduler(teamBuffers);
        sportlink.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, 75, 0, "Heren 1", sportlinkLeidend: true);
        var direct = sportlink.FindAndOccupyNearTime(new TimeOnly(11, 15), 1.0m, 75, 0, "Heren 1", tolerantieMinuten: 0, sportlinkLeidend: true);
        direct!.AanvangsTijd.Should().Be(new TimeOnly(11, 15));

        var app = MaakScheduler(teamBuffers);
        app.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, 75, 60, "Heren 1");
        var metBuffer = app.FindAndOccupyNearTime(new TimeOnly(11, 15), 1.0m, 75, 60, "Heren 1", tolerantieMinuten: 0);
        metBuffer!.AanvangsTijd.Should().Be(new TimeOnly(12, 15)); // 11:15 + 60 min teambuffer (valt terug op eerstvolgend slot)
    }

    [Fact]
    public void AppWedstrijdNaastSportlinkWedstrijd_EistAlleenZijnEigenBuffer()
    {
        // De Sportlink-wedstrijd (team met teamregel 60 min) eindigt om 11:15. De app-wedstrijd van een ander
        // team met alleen de algemene buffer (15) mag om 11:30 starten; de teamregel van de Sportlink-wedstrijd telt niet.
        var teamBuffers = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase) { ["Heren 1"] = (60, 60) };
        var scheduler = MaakScheduler(teamBuffers);
        scheduler.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, 75, 0, "Heren 1", sportlinkLeidend: true);

        var app = scheduler.FindAndOccupyNearTime(new TimeOnly(11, 30), 1.0m, 60, AlgemeneBuffer, "JO11-1", tolerantieMinuten: 0);
        app.Should().NotBeNull();
        app!.AanvangsTijd.Should().Be(new TimeOnly(11, 30));

        // Zijn eigen buffer van 15 minuten blijft wél gelden: 11:20 is te vroeg.
        var scheduler2 = MaakScheduler();
        scheduler2.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, 75, 0, "JO13-1", sportlinkLeidend: true);
        scheduler2.FindAndOccupyNearTime(new TimeOnly(11, 20), 1.0m, 60, AlgemeneBuffer, "JO11-1", tolerantieMinuten: 0)!
            .AanvangsTijd.Should().Be(new TimeOnly(11, 30));
    }

    [Fact]
    public void AppWedstrijden_Onderling_BehoudenDeBuffer()
    {
        var scheduler = MaakScheduler();
        scheduler.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, 75, AlgemeneBuffer, "JO11-1");
        scheduler.FindAndOccupyNearTime(new TimeOnly(11, 15), 1.0m, 75, AlgemeneBuffer, "JO13-1", tolerantieMinuten: 0)!
            .AanvangsTijd.Should().Be(new TimeOnly(11, 30)); // 11:15 mag niet: 15 min buffer
    }

    [Fact]
    public void IsSportlinkLeidend_AlleenBijPositieveSpeelduur()
    {
        VeldbezettingDuur.IsSportlinkLeidend(60).Should().BeTrue();
        VeldbezettingDuur.IsSportlinkLeidend(0).Should().BeFalse();
        VeldbezettingDuur.IsSportlinkLeidend(null).Should().BeFalse();
    }
}
