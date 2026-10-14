using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>#1715: de gedeelde regel voor de seizoensstartmaand per club.</summary>
public class SeizoenStartMaandTests
{
    [Fact]
    public void Standaard_IsJuli()
    {
        SeizoenStartMaand.Standaard.Should().Be(7);
        SeizoenStartMaand.Naam(SeizoenStartMaand.Standaard).Should().Be("juli");
    }

    [Fact]
    public void Maandnamen_ZijnTwaalf_EnUniek()
    {
        SeizoenStartMaand.Maandnamen.Should().HaveCount(12).And.OnlyHaveUniqueItems();
        SeizoenStartMaand.Naam(1).Should().Be("januari");
        SeizoenStartMaand.Naam(12).Should().Be("december");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(12, true)]
    [InlineData(0, false)]
    [InlineData(13, false)]
    [InlineData(-1, false)]
    public void IsGeldig_AlleenEenTotTwaalf(int maand, bool verwacht)
        => SeizoenStartMaand.IsGeldig(maand).Should().Be(verwacht);

    [Theory]
    [InlineData("7", true, 7)]
    [InlineData("07", true, 7)]
    [InlineData("12", true, 12)]
    [InlineData("0", false, 0)]
    [InlineData("13", false, 0)]
    [InlineData("juli", false, 0)]
    [InlineData(" 7", false, 0)]
    [InlineData("7.0", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void TryParse_StrengGeheelGetal(string? waarde, bool geldig, int verwacht)
    {
        SeizoenStartMaand.TryParse(waarde, out var maand).Should().Be(geldig);
        if (geldig) maand.Should().Be(verwacht);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData(0, 7)]
    [InlineData(13, 7)]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    public void OfStandaard_ValtTerugOpJuliBijOngeldigeWaarde(int? opgeslagen, int verwacht)
        => SeizoenStartMaand.OfStandaard(opgeslagen).Should().Be(verwacht);
}
