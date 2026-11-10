using AwesomeAssertions;
using Planner.Shared;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

public class PlannerSharedAdapterTests
{
    // The response DTO is tier-specific, so this comparison remains in FunctionApp.Tests.
    [Theory]
    [InlineData("2026-09-07")] // maandag
    [InlineData("2026-09-08")] // dinsdag
    [InlineData("2026-09-09")] // woensdag
    [InlineData("2026-09-10")] // donderdag
    public void Waarschuwing_Doordeweeks_BevatGeenVasteVeldnummers(string datum)
    {
        var date = DateOnly.Parse(datum);

        var response = new CheckAvailabilityResponse();
        PlannerShared.AddWeekdayWarning(response.Waarschuwingen, date);

        var lijst = new List<string>();
        PlannerShared.AddWeekdayWarning(lijst, date);

        response.Waarschuwingen.Should().HaveCount(1);
        lijst.Should().HaveCount(1);
        // Beide overloads geven exact dezelfde, clubneutrale tekst (#576)
        lijst[0].Should().Be(response.Waarschuwingen[0]);

        foreach (var tekst in lijst)
        {
            tekst.Should().NotMatchRegex(@"veld\s*\d",
                "waarschuwing mag geen vaste veldnummers aannemen — velden komen uit dbo.VeldBeschikbaarheid");
            tekst.Should().Contain("veldbeschikbaarheid");
        }
    }
}
