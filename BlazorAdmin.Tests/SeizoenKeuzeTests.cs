using AwesomeAssertions;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

public class SeizoenKeuzeTests
{
    [Theory]
    [InlineData(2026, 10, 2026)]
    [InlineData(2026, 7, 2026)]
    [InlineData(2026, 6, 2025)]
    public void ResetOpties_BeginnenBijHetHuidigeSeizoen_ZonderToekomst(int jaar, int maand, int verwachtHuidig)
    {
        var opties = SeizoenKeuze.ResetOpties(new DateTime(jaar, maand, 15));
        opties.Should().Equal(verwachtHuidig, verwachtHuidig - 1, verwachtHuidig - 2, verwachtHuidig - 3, verwachtHuidig - 4);
        opties.Should().NotContain(verwachtHuidig + 1);
    }
}
