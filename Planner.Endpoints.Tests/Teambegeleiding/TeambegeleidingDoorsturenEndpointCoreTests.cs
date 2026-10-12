using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Teambegeleiding;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Endpoints.Tests.Teambegeleiding;

/// <summary>#1671: toegangs- en gebruiksgrens van het doorstuur-endpoint, gedeeld door beide tiers.</summary>
public class TeambegeleidingDoorsturenEndpointCoreTests
{
    private static GebruikerLimiet NieuweLimiet(int max = 3) => new(max, TimeSpan.FromMinutes(10));

    [Fact]
    public void User_MetVrijOntvangersveld_Geeft403()
    {
        var antwoord = TeambegeleidingDoorsturenEndpointCore.Controleer(false, "trainer@voorbeeld.nl", "gebruiker-a", NieuweLimiet());

        (antwoord as ObjectResult)!.StatusCode.Should().Be(403);
    }

    [Fact]
    public void User_ZonderOntvangersveld_Passeert()
    {
        TeambegeleidingDoorsturenEndpointCore.Controleer(false, null, "gebruiker-a", NieuweLimiet()).Should().BeNull();
        TeambegeleidingDoorsturenEndpointCore.Controleer(false, "  ", "gebruiker-a", NieuweLimiet()).Should().BeNull();
    }

    [Fact]
    public void Admin_MetVrijOntvangersveld_Passeert()
    {
        TeambegeleidingDoorsturenEndpointCore.Controleer(true, "trainer@voorbeeld.nl", "gebruiker-a", NieuweLimiet()).Should().BeNull();
    }

    [Fact]
    public void AfgewezenAanroep_TeltNietMeeInDeLimiet()
    {
        var limiet = NieuweLimiet(1);

        TeambegeleidingDoorsturenEndpointCore.Controleer(false, "trainer@voorbeeld.nl", "gebruiker-a", limiet);

        TeambegeleidingDoorsturenEndpointCore.Controleer(false, null, "gebruiker-a", limiet).Should().BeNull();
    }

    [Fact]
    public void BovenDeLimiet_Geeft429()
    {
        var limiet = NieuweLimiet(2);
        TeambegeleidingDoorsturenEndpointCore.Controleer(true, null, "gebruiker-a", limiet).Should().BeNull();
        TeambegeleidingDoorsturenEndpointCore.Controleer(true, null, "gebruiker-a", limiet).Should().BeNull();

        var antwoord = TeambegeleidingDoorsturenEndpointCore.Controleer(true, null, "gebruiker-a", limiet);

        (antwoord as ObjectResult)!.StatusCode.Should().Be(429);
    }
}
