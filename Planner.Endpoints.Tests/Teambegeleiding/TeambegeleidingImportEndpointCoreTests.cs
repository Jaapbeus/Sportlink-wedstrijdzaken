using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Teambegeleiding;
using Planner.Shared;
using Xunit;

namespace Planner.Endpoints.Tests.Teambegeleiding;

/// <summary>#1461: de gedeelde 400-vertaling die beide tiers gebruiken — de Postgres-tier gaf bij een
/// te lange kolom een 500 omdat hij de lengtevalidatie niet aanriep.</summary>
public class TeambegeleidingImportEndpointCoreTests
{
    private const string Header = "Team;Rol in team;Functie;Voornaam;Familienaam;E-mailadres";

    [Fact]
    public void TeLangeFunctie_Geeft400MetRegelnummer()
    {
        var csv = $"{Header}\nJO13-1;Staf;{new string('F', 151)};Jan;de Vries;trainer@voorbeeld.nl\n";

        var antwoord = TeambegeleidingImportEndpointCore.Weiger(TeambegeleidingCsv.ParseEnValideer(csv));

        var bad = antwoord.Should().BeOfType<BadRequestObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(bad.Value).Should().Contain("Rij 2").And.Contain("Functie");
    }

    [Fact]
    public void OngeldigeCsv_Geeft400()
    {
        TeambegeleidingImportEndpointCore.Weiger(TeambegeleidingCsv.ParseEnValideer("Team\n"))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GeldigeCsv_WordtNietGeweigerd()
    {
        var csv = $"{Header}\nJO13-1;Staf;Coach;Jan;de Vries;trainer@voorbeeld.nl\n";
        TeambegeleidingImportEndpointCore.Weiger(TeambegeleidingCsv.ParseEnValideer(csv)).Should().BeNull();
    }
}
