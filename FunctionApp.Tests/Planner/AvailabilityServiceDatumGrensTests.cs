using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #1730: de datum van een beschikbaarheidsaanvraag gaat door dezelfde strikte grens als een
/// modeldatum (<c>Planner.Shared.Email.ModelDatum</c>). Deze takken keren terug vóór enige
/// databasetoegang en draaien daarom als unit test.
/// </summary>
public class AvailabilityServiceDatumGrensTests
{
    [Theory]
    [InlineData("2026-4-18")]      // lenig wél leesbaar, strikt niet
    [InlineData("18-04-2026")]
    [InlineData("volgende week")]
    [InlineData("")]
    public async Task CheckAvailabilityAsync_NietStrikteDatum_GeeftOngeldigeDatumZonderDatabase(string datum)
    {
        var request = new CheckAvailabilityRequest { Datum = datum, LeeftijdsCategorie = "JO13" };

        var response = await AvailabilityService.CheckAvailabilityAsync(request, NullLogger.Instance, "ALLSTARS");

        response.Beschikbaar.Should().BeFalse();
        response.Reden.Should().Be("Ongeldige datum.");
    }

    [Fact]
    public async Task CheckAvailabilityAsync_StrikteDatumInHetVerleden_GeeftToekomstReden()
    {
        var request = new CheckAvailabilityRequest { Datum = "2020-01-04", LeeftijdsCategorie = "JO13" };

        var response = await AvailabilityService.CheckAvailabilityAsync(request, NullLogger.Instance, "ALLSTARS");

        response.Reden.Should().Contain("2020-01-04").And.Contain("in de toekomst");
    }
}
