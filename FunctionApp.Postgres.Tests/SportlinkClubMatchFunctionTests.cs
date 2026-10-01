using AwesomeAssertions;
using Planner.Endpoints.Sportlink;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1116: het oefenwedstrijd-formulier stuurt alleen teamnaam/tegenstander/veld; de server leidt de
/// Sportlink-velden af. Deze tests dekken de pure vertaalstappen — de databasekoppeling
/// (<c>SportlinkClubMatchRepository</c>) en de Sportlink-aanroep zelf blijven buiten beeld (die
/// laatste is bovendien code-gelockt, zie <c>SportlinkClubClientTests</c>).
/// </summary>
public class SportlinkClubMatchFunctionTests
{
    private static ClubMatchEndpointCore.OefenwedstrijdAanmakenDto GeldigeInvoer() => new()
    {
        MatchDateTime = new DateTime(2026, 9, 20, 19, 30, 0),
        Duration = 90,
        TeamNaam = "JO10-1",
        Tegenstander = "SV Voorbeeld JO10-2",
        VeldNummer = 3
    };

    [Fact]
    public void Valideer_GeldigeInvoer_GeeftNull()
    {
        ClubMatchEndpointCore.Valideer(GeldigeInvoer()).Should().BeNull();
    }

    [Fact]
    public void Valideer_LegeBody_VerplichtMatchDateTime()
    {
        ClubMatchEndpointCore.Valideer(null).Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Valideer_ZonderTeam_WordtAfgewezen(string? teamNaam)
    {
        var dto = GeldigeInvoer();
        dto.TeamNaam = teamNaam;

        ClubMatchEndpointCore.Valideer(dto).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Valideer_ZonderTegenstander_WordtAfgewezen()
    {
        var dto = GeldigeInvoer();
        dto.Tegenstander = " ";

        ClubMatchEndpointCore.Valideer(dto).Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(241)]
    public void Valideer_OnrealistischeDuur_WordtAfgewezen(int duur)
    {
        var dto = GeldigeInvoer();
        dto.Duration = duur;

        ClubMatchEndpointCore.Valideer(dto).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Valideer_ZonderDuur_ValtTerugOpDefault()
    {
        var dto = GeldigeInvoer();
        dto.Duration = null;

        ClubMatchEndpointCore.Valideer(dto).Should().BeNull("Duration is optioneel; de functie vult 90 in");
    }

    [Fact]
    public void BouwOmschrijving_EigenTekst_WintAltijd()
    {
        ClubMatchEndpointCore.BouwOmschrijving("  Oefenpot met scheids  ", "JO10-1", "SV Voorbeeld")
            .Should().Be("Oefenpot met scheids");
    }

    [Fact]
    public void BouwOmschrijving_ZonderEigenTekst_NoemtTeamEnTegenstander()
    {
        // Sinds #1427 gaat het veld als SubFacilityId mee; het hoeft niet meer in de omschrijving.
        ClubMatchEndpointCore.BouwOmschrijving(null, "JO10-1", " SV Voorbeeld JO10-2 ")
            .Should().Be("Oefenwedstrijd JO10-1 - SV Voorbeeld JO10-2");
    }
}
