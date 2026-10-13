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
}
