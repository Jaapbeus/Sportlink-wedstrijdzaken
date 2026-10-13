using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Planner.Shared.Infrastructure;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

/// <summary>
/// Tier-bedrading van de clubcode-keuze (#1689, SQL Server-tier): de helper gebruikt de getoetste
/// uitkomst (<see cref="ClubZoekResultaat.CodeVoorVerzoek"/>), nooit de ruwe headerwaarde, en valt
/// alleen bij een onbekende of ontbrekende waarde terug op de primaire club.
/// </summary>
public class EasyAuthHelperClubCodeTests
{
    private const string Primair = "PRIMAIR";

    private static HttpRequest Verzoek(string? clubCode)
    {
        var context = new DefaultHttpContext();
        if (clubCode != null) context.Request.Headers["X-Club-Code"] = clubCode;
        return context.Request;
    }

    [Theory]
    [InlineData("allstars", "ALLSTARS")]   // opgeslagen schrijfwijze, niet de headerwaarde
    [InlineData("ONBEKEND", Primair)]
    [InlineData("AB CD", Primair)]
    [InlineData("", Primair)]
    [InlineData(null, Primair)]
    public void MetLijst_KiestGetoetsteCodeOfPrimaireClub(string? header, string verwacht)
    {
        var clubs = new BekendeClubs(() => ["ALLSTARS"]);

        EasyAuthHelper.GetClubCodeFromRequest(Verzoek(header), clubs, () => Primair).Should().Be(verwacht);
    }

    [Fact]
    public void LijstNietBeschikbaar_GeldigeWaardeGeldtZoalsVoorheen_ZonderPrimaireClubOpTeVragen()
    {
        var clubs = new BekendeClubs(() => throw new InvalidOperationException());

        EasyAuthHelper.GetClubCodeFromRequest(Verzoek("ALLSTARS"), clubs, () => throw new InvalidOperationException("primair"))
            .Should().Be("ALLSTARS");
    }
}
