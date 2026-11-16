using AwesomeAssertions;
using Xunit;

namespace Planner.Shared.Tests;

public class AppSettingsNormalisatieTests
{
    [Theory]
    [InlineData("OptimalisatieAanvang", null, null)]
    [InlineData("OptimalisatieAanvang", "", null)]
    [InlineData("optimalisatieaanvang", "  ", null)]
    [InlineData("OPTIMALISATIEAANVANG", "\t", null)]
    [InlineData("OptimalisatieAanvang", "08:00", "08:00")]
    [InlineData("OptimalisatieAanvang", "ongeldig", "ongeldig")]
    [InlineData("Accommodatie", "", "")]
    [InlineData("Accommodatie", "  ", "  ")]
    [InlineData("Accommodatie", null, null)]
    public void AlleenLegeOptimalisatieAanvangWordtNull(string veld, string? waarde, string? verwacht)
    {
        AppSettingsNormalisatie.NormaliseerWaarde(veld, waarde).Should().Be(verwacht);
    }
}
