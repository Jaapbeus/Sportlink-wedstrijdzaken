using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// Exacte slots (Toepassen, #1802) en de buffer van de optimalisatie. WZ-ADR-014 (#1960): de buffer van de
/// pagina en de teambuffers gelden voor elke wedstrijd; er bestaat geen uitzondering voor Sportlink-wedstrijden.
/// </summary>
public class FieldSchedulerBufferEnExactSlotTests
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
            "JO11-1", out var slot).Should().BeFalse();
        slot.Should().BeNull();
        scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0, 30), 60, 1m, null,
            "JO11-1", out _).Should().BeTrue();
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
            "JO11-1", out var slot).Should().Be(expected);
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
            "JO11-1", out var slot).Should().Be(expected);
        if (expected) slot!.EindTijd.Should().Be(new TimeOnly(17, 0, seconds).AddMinutes(minutes));
    }

    [Fact]
    public void TryOccupyExactSlot_AccepteertExactSlotZonderTijdverschuiving()
    {
        var scheduler = MaakScheduler();

        var geplaatst = scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, 1m, null, "JO11-1", out var slot);

        geplaatst.Should().BeTrue();
        slot!.AanvangsTijd.Should().Be(new TimeOnly(10, 0));
        slot.EindTijd.Should().Be(new TimeOnly(11, 0));
    }

    [Fact]
    public void TryOccupyExactSlot_WeigertConflictZonderEenAlternatiefTeKiezen()
    {
        var scheduler = MaakScheduler();
        scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, 1m, null, "JO11-1", out _).Should().BeTrue();

        var geplaatst = scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, 1m, null, "JO13-1", out var slot);

        geplaatst.Should().BeFalse();
        slot.Should().BeNull();
    }

    [Fact]
    public void TryOccupyExactSlot_QuarterlaneBehoudtHandmatigeKeuze()
    {
        var scheduler = MaakScheduler();

        var geplaatst = scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .25m, "A2", "JO11-1", out var slot);

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

        var geplaatst = scheduler.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, fractie, subpositie, "JO11-1", out var slot);

        geplaatst.Should().BeFalse();
        slot.Should().BeNull();
    }

    [Fact]
    public void TryOccupyExactSlot_HalfVeldAConflictMetKwartA1MaarNietMetA2()
    {
        var conflict = MaakScheduler();
        conflict.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .25m, "A1", "JO13-1", out _).Should().BeTrue();
        conflict.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .5m, "A", "JO11-1", out _).Should().BeFalse();

        var vrij = MaakScheduler();
        vrij.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .25m, "A1", "JO13-1", out _).Should().BeTrue();
        vrij.TryOccupyExactSlot(1, new TimeOnly(10, 0), 60, .25m, "A2", "JO11-1", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 11, 15)]
    [InlineData(10, 11, 25)]
    [InlineData(15, 11, 30)]
    public void PaginabufferGeldtTussenElkeTweeWedstrijden(int buffer, int uur, int minuut)
    {
        var scheduler = new FieldScheduler(
            [new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(9, 0), BeschikbaarTot = new TimeOnly(18, 0) }],
            [new() { VeldNummer = 1, VeldNaam = "veld 1" }], buffer);
        scheduler.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, 75, teamNaam: "JO11-1");

        scheduler.FindAndOccupyNearTime(new TimeOnly(11, 15), 1.0m, 75, teamNaam: "JO13-1")!
            .AanvangsTijd.Should().Be(new TimeOnly(uur, minuut));
    }

    [Fact]
    public void TeambufferVoor_GeldtBovenopDeAlgemeneBuffer()
    {
        var teamBuffers = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase) { ["Heren 1"] = (60, 30) };
        var scheduler = MaakScheduler(teamBuffers);
        scheduler.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, 75, AlgemeneBuffer, "JO13-1");

        scheduler.FindAndOccupyNearTime(new TimeOnly(11, 15), 1.0m, 105, 60, "Heren 1", tolerantieMinuten: 0)!
            .AanvangsTijd.Should().Be(new TimeOnly(12, 15)); // 11:15 + 60 min vrij vóór de wedstrijd
    }

    [Fact]
    public void TeambufferNa_GeldtVoorDeVolgendeWedstrijd()
    {
        var teamBuffers = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase) { ["Heren 1"] = (0, 30) };
        var scheduler = MaakScheduler(teamBuffers);
        scheduler.FindAndOccupyNearTime(new TimeOnly(10, 0), 1.0m, 105, AlgemeneBuffer, "Heren 1");

        scheduler.FindAndOccupyNearTime(new TimeOnly(11, 45), 1.0m, 75, AlgemeneBuffer, "JO13-1", tolerantieMinuten: 0)!
            .AanvangsTijd.Should().Be(new TimeOnly(12, 15)); // 11:45 + 30 min BufferNa
    }
}
