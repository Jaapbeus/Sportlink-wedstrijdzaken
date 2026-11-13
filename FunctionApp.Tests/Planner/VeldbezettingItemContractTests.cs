using System.Text.Json;
using AwesomeAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #1915: de koppelsleutels naar de Sportlink-veldplanner (<c>Wedstrijdnummer</c>, <c>PublicMatchId</c>) zijn alleen intern. Ze
/// staan niet in het API-antwoord van de Planning, zodat <c>openapi.yaml</c> en de Blazor-client ongewijzigd blijven.
/// </summary>
public class VeldbezettingItemContractTests
{
    [Fact]
    public void KoppelsleutelsStaanNietInHetApiAntwoord()
    {
        var item = new VeldbezettingItem { WedstrijdCode = 1, Wedstrijd = "A - B", Wedstrijdnummer = 101066, PublicMatchId = "M-NIET-TONEN" };

        var json = JsonSerializer.Serialize(item);

        json.Should().NotContainEquivalentOf("Wedstrijdnummer");
        json.Should().NotContainEquivalentOf("PublicMatchId");
        json.Should().NotContain("M-NIET-TONEN");
        json.Should().Contain("\"WedstrijdCode\":1", "de bestaande velden blijven");
    }
}
