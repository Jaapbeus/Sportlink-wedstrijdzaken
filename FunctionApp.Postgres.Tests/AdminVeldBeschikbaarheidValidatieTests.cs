using FluentAssertions;
using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #957: de API accepteerde stilzwijgend een venster dat eindigt vóór het begint (bijv. verwisselde
/// begin-/eindtijd) — geen foutmelding, de rij werd zo opgeslagen. Zelfde scenario als
/// <c>FunctionApp.Tests/Admin/AdminVeldBeschikbaarheidValidatieTests.cs</c> op de SQL Server-tier.
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
