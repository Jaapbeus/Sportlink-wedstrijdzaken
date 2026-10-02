using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>#1437: opmaak van het wedstrijdnummer (YYMMDD + volgnummer) en de vertaling van de veldafmeting.</summary>
public class ClubMatchWedstrijdNummerTests
{
    [Theory]
    [InlineData(2026, 10, 2, 1, 26100201L)]
    [InlineData(2026, 10, 2, 2, 26100202L)]
    [InlineData(2026, 1, 5, 99, 26010599L)]
    [InlineData(2031, 12, 31, 10, 31123110L)]
    public void Formatteer_MaaktYyMmDdPlusTweecijferigVolgnummer(int jaar, int maand, int dag, int volgnummer, long verwacht)
    {
        ClubMatchWedstrijdNummer.Formatteer(new DateOnly(jaar, maand, dag), volgnummer).Should().Be(verwacht);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-1)]
    public void Formatteer_VolgnummerBuitenBereik_GooitException(int volgnummer)
    {
        var act = () => ClubMatchWedstrijdNummer.Formatteer(new DateOnly(2026, 10, 2), volgnummer);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TeVeelOpEenDagMelding_IsNederlandsEnNoemtDeDatum()
    {
        ClubMatchWedstrijdNummer.TeVeelOpEenDagMelding(new DateOnly(2026, 10, 2))
            .Should().Contain("02-10-2026").And.Contain("99");
    }

    [Theory]
    [InlineData("1.00", "1.0")]
    [InlineData("0.50", "0.5")]
    [InlineData("0.25", "0.25")]
    [InlineData("0.125", "0.125")]
    [InlineData("0.75", null)]
    public void VanAfmeting_VertaaltSpeeltijdenAfmetingNaarVelddeel(string afmeting, string? verwacht)
    {
        ClubMatchVelddeel.VanAfmeting(decimal.Parse(afmeting, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(verwacht);
    }

    [Fact]
    public void VanAfmeting_Onbekend_IsNull() => ClubMatchVelddeel.VanAfmeting(null).Should().BeNull();

    [Fact]
    public void IsGeldig_AccepteertAlleenDeVierWaarden()
    {
        ClubMatchVelddeel.Waarden.Should().OnlyContain(w => ClubMatchVelddeel.IsGeldig(w));
        ClubMatchVelddeel.IsGeldig("2.0").Should().BeFalse();
        ClubMatchVelddeel.IsGeldig(null).Should().BeFalse();
    }
}
