using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// Regelvolgorde van het idempotentiebesluit (#712/#716), sinds #1650 eenmalig in Planner.Shared.
/// De vertaling van de tier-eigen stand staat in de EmailIdempotentieTests van de tiers.
/// </summary>
public class VerwerkingsBesluitRegelsTests
{
    [Fact]
    public void Definitief_GaatVoorAlleAndereRegels()
        => VerwerkingsBesluitRegels.BepaalVoorBestaandeRij(isDefinitief: true, verzendPogingOnbeslist: true, pogingen: 99)
            .Should().Be(VerwerkingsBesluit.OverslaanAlAfgerond);

    [Fact]
    public void OnbeslisteVerzendpoging_GaatVoorDePogingengrens()
        => VerwerkingsBesluitRegels.BepaalVoorBestaandeRij(false, true, VerwerkingsBesluitRegels.MaxPogingen)
            .Should().Be(VerwerkingsBesluit.OnbeslistNaVerzendPoging);

    [Theory]
    [InlineData(0, VerwerkingsBesluit.HerhaalVerwerking)]
    [InlineData(2, VerwerkingsBesluit.HerhaalVerwerking)]
    [InlineData(3, VerwerkingsBesluit.OpgevenNaMaxPogingen)]
    [InlineData(7, VerwerkingsBesluit.OpgevenNaMaxPogingen)]
    public void Pogingengrens_BepaaltHerhalenOfOpgeven(int pogingen, VerwerkingsBesluit verwacht)
        => VerwerkingsBesluitRegels.BepaalVoorBestaandeRij(false, false, pogingen).Should().Be(verwacht);

    [Fact]
    public void MaxPogingen_IsDrie() => VerwerkingsBesluitRegels.MaxPogingen.Should().Be(3);
}
