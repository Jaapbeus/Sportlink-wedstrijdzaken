using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

/// <summary>
/// #1962 (review R1-F1): POST en PUT van de speeltijden-API (beide tiers) controleren helften, helft, rust en het
/// berekende totaal vóór opslag. Extreme invoer loopt niet over naar een totaal van 0 of negatief maar geeft 400;
/// de function keert dan terug vóór de repository-aanroep.
/// </summary>
public class SpeeltijdenValidatieCoreTests
{
    [Theory]
    [InlineData(2, int.MaxValue, 0)]          // vermenigvuldiging zou overlopen
    [InlineData(2, 1_073_741_824, 0)]         // 2 × 2^30 = 2^31
    [InlineData(2, 20, int.MaxValue)]         // optelling zou overlopen
    [InlineData(2, 700, 41)]                  // totaal 1441 > één dag
    [InlineData(1, 20, 5)]                    // rust bij één helft
    [InlineData(3, 20, 0)]                    // drie helften
    public void OngeldigeInvoer_Geeft400_ZonderTotaal(int helften, int helft, int rust)
    {
        var fout = SpeeltijdenValidatieCore.Valideer(helften, helft, rust, out _, out var totaal);

        fout.Should().BeOfType<BadRequestObjectResult>();
        totaal.Should().Be(0);
    }

    [Theory]
    [InlineData(null, 45, 15, 2, 105)]
    [InlineData(1, 20, 0, 1, 20)]
    [InlineData(2, 700, 40, 2, 1440)]
    public void GeldigeInvoer_GeeftHetBerekendeTotaal(int? helften, int helft, int rust, int verwachtAantal, int verwachtTotaal)
    {
        var fout = SpeeltijdenValidatieCore.Valideer(helften, helft, rust, out var aantal, out var totaal);

        fout.Should().BeNull();
        (aantal, totaal).Should().Be((verwachtAantal, verwachtTotaal));
    }
}
