using AwesomeAssertions;
using BlazorAdmin.Models;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>Badge-tekst op /teambegeleiding (#1360): "Teamrol - Functie", of alleen Teamrol.</summary>
public class TeambegeleidingItemTests
{
    [Fact]
    public void RolLabel_MetFunctie_ToontTeamrolEnFunctie()
    {
        var item = new TeambegeleidingItem { Teamrol = "Technische staf", Functie = "Trainer/coach" };

        item.RolLabel.Should().Be("Technische staf - Trainer/coach");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RolLabel_ZonderFunctie_ToontAlleenTeamrolZonderKoppelteken(string? functie)
    {
        var item = new TeambegeleidingItem { Teamrol = "Technische staf", Functie = functie };

        item.RolLabel.Should().Be("Technische staf");
    }
}
