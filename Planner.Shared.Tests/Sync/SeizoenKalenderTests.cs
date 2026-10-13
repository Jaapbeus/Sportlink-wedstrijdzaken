using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>#1715: de seizoenskalender per club uit de eigen startmaand.</summary>
public class SeizoenKalenderTests
{
    [Fact]
    public void Seizoen_Juli_LooptTotDeDagVoorHetVolgendeSeizoen()
    {
        var s = SeizoenKalender.Seizoen(2026, 7);

        s.Naam.Should().Be("2026-2027");
        s.Van.Should().Be(new DateOnly(2026, 7, 1));
        s.Tot.Should().Be(new DateOnly(2027, 6, 30));
    }

    [Fact]
    public void Seizoen_Januari_EindigtOpEenDecember_ZonderOngeldigeMaand()
    {
        var s = SeizoenKalender.Seizoen(2026, 1);

        s.Van.Should().Be(new DateOnly(2026, 1, 1));
        s.Tot.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void Seizoen_September_EindigtOpEenendertigAugustus()
        => SeizoenKalender.Seizoen(2026, 9).Tot.Should().Be(new DateOnly(2027, 8, 31));

    [Fact]
    public void Seizoen_OngeldigeMaand_ValtTerugOpJuli()
        => SeizoenKalender.Seizoen(2026, 13).Van.Should().Be(new DateOnly(2026, 7, 1));

    [Fact]
    public void TweeClubsMetVerschillendeMaand_KrijgenElkEenEigenSeizoenseinde()
    {
        var vandaag = new DateOnly(2026, 10, 13);

        var juli = SeizoenKalender.Basis(7, vandaag).Max(r => r.Tot);
        var september = SeizoenKalender.Basis(9, vandaag).Max(r => r.Tot);

        juli.Should().Be(new DateOnly(2027, 6, 30));
        september.Should().Be(new DateOnly(2027, 8, 31));
    }

    [Fact]
    public void Basis_JanuariClub_HeeftHetSeizoenVanDitJaarAltijd()
    {
        // De doorroldatum van een januari-club ligt in november van het jaar ervoor: dit jaar is hij al voorbij.
        SeizoenKalender.Basis(1, new DateOnly(2026, 1, 1)).Select(r => r.Naam).Should().Contain("2026-2027");
        SeizoenKalender.Basis(1, new DateOnly(2026, 12, 31)).Select(r => r.Naam).Should().Contain("2026-2027");
    }

    [Fact]
    public void DoorrolDatum_IsTweeMaandenVoorDeStart_OokBijJanuariEnFebruari()
    {
        SeizoenKalender.DoorrolDatum(2026, 7).Should().Be(new DateOnly(2026, 5, 1));
        SeizoenKalender.DoorrolDatum(2026, 2).Should().Be(new DateOnly(2025, 12, 1));
        SeizoenKalender.DoorrolDatum(2026, 1).Should().Be(new DateOnly(2025, 11, 1));
    }

    [Fact]
    public void Basis_VoorDeDoorrol_HeeftTweeAfgerondeSeizoenen()
    {
        var rijen = SeizoenKalender.Basis(7, new DateOnly(2026, 4, 30));

        rijen.Select(r => r.Naam).Should().Equal("2024-2025", "2025-2026");
    }

    [Fact]
    public void Basis_OpDeDoorrolDatum_VoegtHetNieuweSeizoenToe()
    {
        var rijen = SeizoenKalender.Basis(7, new DateOnly(2026, 5, 1));

        rijen.Select(r => r.Naam).Should().Equal("2024-2025", "2025-2026", "2026-2027");
    }

    // ── Codex-review #1743 ronde 1 (P2): januari/februari ook vóór de jaarwisseling doorrollen ────────

    [Theory]
    [InlineData(1, 2026, 10, 31, false)] // januari-start: één dag vóór de drempel (1 november)
    [InlineData(1, 2026, 11, 1, true)]   // januari-start: op de drempel
    [InlineData(2, 2026, 11, 30, false)] // februari-start: één dag vóór de drempel (1 december)
    [InlineData(2, 2026, 12, 1, true)]   // februari-start: op de drempel
    [InlineData(7, 2026, 12, 31, false)] // juli-start: het seizoen van volgend jaar komt pas op 1 mei 2027
    public void Basis_SeizoenVanVolgendJaar_BestaatVanafTweeMaandenVoorDeStart_OokVoorDeJaarwisseling(
        int startMaand, int jaar, int maand, int dag, bool verwacht)
    {
        var namen = SeizoenKalender.Basis(startMaand, new DateOnly(jaar, maand, dag)).Select(r => r.Naam);

        if (verwacht)
            namen.Should().Contain("2027-2028");
        else
            namen.Should().NotContain("2027-2028");
    }

    [Fact]
    public void DoorTeRollenStartjaren_JanuariOpEenNovember_BevatDitEnVolgendJaar()
        => SeizoenKalender.DoorTeRollenStartjaren(1, new DateOnly(2026, 11, 1)).Should().Equal(2026, 2027);

    [Fact]
    public void DoorTeRollenStartjaren_JuliVoorEenMei_IsLeeg()
        => SeizoenKalender.DoorTeRollenStartjaren(7, new DateOnly(2026, 4, 30)).Should().BeEmpty();

    /// <summary>
    /// Voor elke startmaand en elke dag over twee jaarwisselingen: het seizoen waarin de datum over twee
    /// maanden valt, bestaat al (het sync-venster loopt twee maanden vooruit), en er bestaat geen seizoen dat
    /// pas later begint (de doorrol komt niet te vroeg). Dit omvat het bestaande juligedrag (1 mei).
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)]
    public void Basis_ElkeStartmaand_ElkeDag_HeeftPreciesTotTweeMaandenVooruitEenSeizoen(int startMaand)
    {
        for (var vandaag = new DateOnly(2025, 10, 1); vandaag <= new DateOnly(2027, 3, 31); vandaag = vandaag.AddDays(1))
        {
            var rijen = SeizoenKalender.Basis(startMaand, vandaag);
            var overTweeMaanden = vandaag.AddMonths(2);

            rijen.Should().Contain(r => r.Van <= overTweeMaanden && overTweeMaanden <= r.Tot,
                $"startmaand {startMaand}, {vandaag:yyyy-MM-dd}: het seizoen van {overTweeMaanden:yyyy-MM-dd} hoort te bestaan");
            rijen.Should().NotContain(r => r.Van > overTweeMaanden,
                $"startmaand {startMaand}, {vandaag:yyyy-MM-dd}: geen seizoen dat pas na {overTweeMaanden:yyyy-MM-dd} begint");
        }
    }
}
