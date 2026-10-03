using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Planner.Endpoints.Deel;
using Planner.Shared.Deel;
using Xunit;

namespace Planner.Endpoints.Tests.Deel;

/// <summary>#1460: validatie en renderer-equivalentie van <c>POST planner/auto-plan/deel</c>.</summary>
public class PlannerDeelPlanEndpointCoreTests
{
    private const string Club = "ALLSTARS";

    private static string Body(object[]? regels = null, string datum = "2026-10-03", string tab = "optimaal", string? clubCode = null) =>
        JsonConvert.SerializeObject(new
        {
            datum, tab, clubCode,
            wedstrijden = regels ?? [new { teamNaam = "AllStars JO10 1", wedstrijd = "AllStars JO10 1 - Tegenstander 1", competitiesoort = "Competitie", tijd = "09:30", veld = "Veld 2 A" }],
        });

    private static void IsBadRequest(IActionResult r, string bevat) =>
        r.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!.Should().Contain(bevat);

    [Fact]
    public void Geldig_Html_RenderEquivalentAanBestaandePad()
    {
        var verwacht = PlannerDeelEndpointCore.Maak(
            PlannerShareModelBuilder.VanPlan(
                [new PlanRegel("AllStars JO10 1", "AllStars JO10 1 - Tegenstander 1", "Competitie", null, null, "09:30", "Veld 2 A")],
                new DateOnly(2026, 10, 3), Club, PlanWeergave.Optimaal),
            "html", "optimale-planning");

        var result = PlannerDeelPlanEndpointCore.Verwerk(Body(), "html", Club);

        result.Should().BeAssignableTo<ContentResult>().Subject.Content
            .Should().Be(((ContentResult)verwacht).Content);
    }

    [Fact]
    public void Geldig_Pdf_GeeftPdf()
    {
        var result = PlannerDeelPlanEndpointCore.Verwerk(Body(), "pdf", Club);
        var f = result.Should().BeOfType<FileContentResult>().Subject;
        Encoding.ASCII.GetString(f.FileContents, 0, 5).Should().Be("%PDF-");
        f.FileDownloadName.Should().Be("optimale-planning-2026-10-03.pdf");
    }

    [Fact]
    public void LegeLijst_IsGeldig() =>
        PlannerDeelPlanEndpointCore.Verwerk(Body([]), "html", Club).Should().BeAssignableTo<ContentResult>();

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    [InlineData("docx")]
    public void OngeldigFormat_400(string? format) =>
        IsBadRequest(PlannerDeelPlanEndpointCore.Verwerk(Body(), format, Club), "format");

    [Fact]
    public void TeveelRegels_400()
    {
        var regels = Enumerable.Range(0, PlannerDeelPlanEndpointCore.MaxRegels + 1)
            .Select(i => (object)new { teamNaam = "T" + i, wedstrijd = "A - B", tijd = "10:00" }).ToArray();
        IsBadRequest(PlannerDeelPlanEndpointCore.Verwerk(Body(regels), "html", Club), "Maximaal");
    }

    [Fact]
    public void PreciesMaxRegels_Ok()
    {
        var regels = Enumerable.Range(0, PlannerDeelPlanEndpointCore.MaxRegels)
            .Select(i => (object)new { teamNaam = "T" + i, wedstrijd = "A - B", tijd = "10:00" }).ToArray();
        PlannerDeelPlanEndpointCore.Verwerk(Body(regels), "html", Club).Should().BeAssignableTo<ContentResult>();
    }

    [Fact]
    public void AndereClub_403() =>
        PlannerDeelPlanEndpointCore.Verwerk(Body(clubCode: "ANDERS"), "html", Club)
            .Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(403);

    [Fact]
    public void ZelfdeClubInAnderHoofdlettergebruik_Ok() =>
        PlannerDeelPlanEndpointCore.Verwerk(Body(clubCode: "allstars"), "html", Club).Should().BeAssignableTo<ContentResult>();

    [Theory]
    [InlineData("25:00")]
    [InlineData("9:30")]
    [InlineData("abc")]
    [InlineData("10:60")]
    public void OngeldigeTijd_400(string tijd) =>
        IsBadRequest(PlannerDeelPlanEndpointCore.Verwerk(
            Body([new { teamNaam = "T", wedstrijd = "A - B", tijd }]), "html", Club), "tijd");

    [Theory]
    [InlineData("morgen")]
    [InlineData("2026-13-01")]
    [InlineData("1999-01-01")]
    [InlineData("2101-01-01")]
    public void OngeldigeDatum_400(string datum) =>
        IsBadRequest(PlannerDeelPlanEndpointCore.Verwerk(Body(datum: datum), "html", Club), "datum");

    [Fact]
    public void ZonderTeamnaam_400() =>
        IsBadRequest(PlannerDeelPlanEndpointCore.Verwerk(Body([new { teamNaam = " ", wedstrijd = "A - B" }]), "html", Club), "teamNaam");

    [Fact]
    public void TeLangeTekst_400() =>
        IsBadRequest(PlannerDeelPlanEndpointCore.Verwerk(
            Body([new { teamNaam = new string('x', PlannerDeelPlanEndpointCore.MaxTekstLengte + 1), wedstrijd = "A - B" }]), "html", Club), "te lang");

    [Fact]
    public void Stuurtekens_400() =>
        IsBadRequest(PlannerDeelPlanEndpointCore.Verwerk(Body([new { teamNaam = "T\u0000", wedstrijd = "A - B" }]), "html", Club), "ongeldige tekens");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{kapot")]
    [InlineData("null")]
    public void OngeldigeBody_400(string body) =>
        PlannerDeelPlanEndpointCore.Verwerk(body, "html", Club).Should().BeOfType<BadRequestObjectResult>();

    [Fact]
    public void ZonderWedstrijden_400() =>
        PlannerDeelPlanEndpointCore.Verwerk("{\"datum\":\"2026-10-03\"}", "html", Club).Should().BeOfType<BadRequestObjectResult>();

    [Fact]
    public void TeGroteBody_413() =>
        PlannerDeelPlanEndpointCore.Verwerk(new string(' ', PlannerDeelPlanEndpointCore.MaxBodyBytes + 1), "html", Club)
            .Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(413);

    [Fact]
    public void HtmlInWaarde_WordtGeencodeerd()
    {
        var html = ((ContentResult)PlannerDeelPlanEndpointCore.Verwerk(
            Body([new { teamNaam = "<script>x</script>", wedstrijd = "A - B" }]), "html", Club)).Content!;
        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
    }

    private sealed record PlanRegel(string TeamNaam, string Wedstrijd, string? Competitiesoort,
        string? HuidigeTijd, string? HuidigeVeld, string? OptimaalTijd, string? OptimaalVeld) : IPlanWedstrijdRegel;
}
