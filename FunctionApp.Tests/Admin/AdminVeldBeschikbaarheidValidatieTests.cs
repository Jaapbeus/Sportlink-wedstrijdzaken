using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

/// <summary>
/// #957: de API accepteerde stilzwijgend een venster dat eindigt vóór het begint (bijv. verwisselde
/// begin-/eindtijd) — geen foutmelding, de rij werd zo opgeslagen.
/// </summary>
public class AdminVeldBeschikbaarheidValidatieTests
{
    [Fact]
    public void ValideerTijden_EindtijdVoorBegintijd_WordtAfgewezen()
    {
        var result = AdminVeldBeschikbaarheidFunction.ValideerTijden("19:00", "10:00");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ValideerTijden_EindtijdGelijkAanBegintijd_WordtAfgewezen()
    {
        var result = AdminVeldBeschikbaarheidFunction.ValideerTijden("10:00", "10:00");

        result.Should().BeOfType<BadRequestObjectResult>("een venster van nul minuten is geen bruikbare beschikbaarheid");
    }

    [Fact]
    public void ValideerTijden_EindtijdNaBegintijd_WordtGeaccepteerd()
    {
        var result = AdminVeldBeschikbaarheidFunction.ValideerTijden("08:30", "22:00");

        result.Should().BeNull();
    }
}
