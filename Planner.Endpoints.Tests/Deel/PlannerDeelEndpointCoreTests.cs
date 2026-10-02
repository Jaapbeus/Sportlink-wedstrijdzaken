using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Deel;
using Planner.Shared.Deel;
using Xunit;

namespace Planner.Endpoints.Tests.Deel;

/// <summary>#1364: de gedeelde ?format=/?tab=/datum-validatie en de HTML/PDF-respons van beide tiers.</summary>
public class PlannerDeelEndpointCoreTests
{
    private static readonly DateOnly Zaterdag = new(2026, 10, 3);

    private static PlannerShareModel Model(params PlannerShareWedstrijd[] regels) =>
        new("Veldbezetting op zaterdag 3 oktober 2026", "ALLSTARS", Zaterdag, regels);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("json")]
    [InlineData("JSON")]
    public void ControleerFormat_LeegOfJson_BlijftJson(string? rauw)
    {
        PlannerDeelEndpointCore.ControleerFormat(rauw, out var format).Should().BeNull();
        format.Should().BeNull();
    }

    [Theory]
    [InlineData("html", "html")]
    [InlineData("PDF", "pdf")]
    [InlineData(" pdf ", "pdf")]
    public void ControleerFormat_HtmlOfPdf_WordtGenormaliseerd(string rauw, string verwacht)
    {
        PlannerDeelEndpointCore.ControleerFormat(rauw, out var format).Should().BeNull();
        format.Should().Be(verwacht);
    }

    [Fact]
    public void ControleerFormat_Onbekend_Geeft400()
    {
        PlannerDeelEndpointCore.ControleerFormat("docx", out _).Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(null, PlanWeergave.Huidig)]
    [InlineData("huidig", PlanWeergave.Huidig)]
    [InlineData("Optimaal", PlanWeergave.Optimaal)]
    public void ControleerTab_GeldigeWaarden(string? rauw, PlanWeergave verwacht)
    {
        PlannerDeelEndpointCore.ControleerTab(rauw, out var weergave).Should().BeNull();
        weergave.Should().Be(verwacht);
    }

    [Fact]
    public void ControleerTab_Onbekend_Geeft400()
    {
        PlannerDeelEndpointCore.ControleerTab("morgen", out _).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ControleerDatum_Geldig()
    {
        PlannerDeelEndpointCore.ControleerDatum("2026-10-03", out var datum).Should().BeNull();
        datum.Should().Be(Zaterdag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("morgen")]
    [InlineData("03-10-2026")]
    [InlineData("2026-13-45")]
    public void ControleerDatum_Ongeldig_Geeft400(string? rauw)
    {
        PlannerDeelEndpointCore.ControleerDatum(rauw, out _).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Maak_Pdf_GeeftPdfBytesMetContentTypeEnBestandsnaam()
    {
        var model = Model(new PlannerShareWedstrijd("09:30", "JO10-1", "Gasten JO10-2", "veld 3 A", "competitie", null));

        var result = PlannerDeelEndpointCore.Maak(model, PlannerDeelEndpointCore.FormatPdf, "veldbezetting");

        var bestand = result.Should().BeOfType<FileContentResult>().Subject;
        bestand.ContentType.Should().Be("application/pdf");
        bestand.FileDownloadName.Should().Be("veldbezetting-2026-10-03.pdf");
        Encoding.ASCII.GetString(bestand.FileContents, 0, 5).Should().Be("%PDF-");
    }

    // #1461: HTML-export vanaf de API-origin → CSP + nosniff op de respons zelf.
    [Fact]
    public async Task Maak_Html_ZetCspEnNosniffHeaders()
    {
        var model = Model(new PlannerShareWedstrijd("09:30", "JO10-1", "Gasten JO10-2", "veld 3 A", "competitie", null));
        var result = PlannerDeelEndpointCore.Maak(model, PlannerDeelEndpointCore.FormatHtml, "veldbezetting");
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        await result.ExecuteResultAsync(new Microsoft.AspNetCore.Mvc.ActionContext { HttpContext = http });

        http.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Be("default-src 'none'; style-src 'unsafe-inline'; sandbox");
        http.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    [Fact]
    public void Maak_Html_GeeftEenHtmlPaginaZonderScript()
    {
        var model = Model(new PlannerShareWedstrijd("09:30", "JO10-1", "Gasten JO10-2", "veld 3 A", "competitie", null));

        var result = PlannerDeelEndpointCore.Maak(model, PlannerDeelEndpointCore.FormatHtml, "veldbezetting");

        var inhoud = result.Should().BeAssignableTo<ContentResult>().Subject;
        inhoud.ContentType.Should().StartWith("text/html");
        inhoud.Content.Should().Contain("JO10-1").And.Contain("Gasten JO10-2").And.NotContain("<script");
    }

    [Fact]
    public void Lees_ZonderFormat_IsJsonEnZonderDatumcontrole()
    {
        PlannerDeelEndpointCore.Lees(null, null, "geen-datum", out var verzoek).Should().BeNull();
        verzoek.Format.Should().BeNull();
        verzoek.VanVeldbezetting(new List<Regel>(), "ALLSTARS").Should().BeNull("zonder format blijft het de JSON-respons");
    }

    [Fact]
    public void Lees_PdfMetOngeldigeDatum_Geeft400()
    {
        PlannerDeelEndpointCore.Lees("pdf", null, "morgen", out _).Should().BeOfType<BadRequestObjectResult>();
        PlannerDeelEndpointCore.Lees("html", null, null, out _).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Lees_OnbekendeTab_Geeft400()
    {
        PlannerDeelEndpointCore.Lees("pdf", "morgen", "2026-10-03", out _).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void VanVeldbezetting_Pdf_GeeftPdfMetBestandsnaam()
    {
        PlannerDeelEndpointCore.Lees("pdf", null, "2026-10-03", out var verzoek).Should().BeNull();

        var result = verzoek.VanVeldbezetting(new List<Regel> { new() }, "ALLSTARS");

        var bestand = result.Should().BeOfType<FileContentResult>().Subject;
        bestand.ContentType.Should().Be("application/pdf");
        bestand.FileDownloadName.Should().Be("veldbezetting-2026-10-03.pdf");
        Encoding.ASCII.GetString(bestand.FileContents, 0, 5).Should().Be("%PDF-");
    }

    [Theory]
    [InlineData("huidig", "huidige-planning-2026-10-03.pdf", "09:00")]
    [InlineData("optimaal", "optimale-planning-2026-10-03.pdf", "08:30")]
    public void VanPlan_KiestDeGevraagdeTab(string tab, string bestandsnaam, string verwachteTijd)
    {
        PlannerDeelEndpointCore.Lees("pdf", tab, "2026-10-03", out var pdf).Should().BeNull();
        PlannerDeelEndpointCore.Lees("html", tab, "2026-10-03", out var html).Should().BeNull();
        var wedstrijden = new List<PlanRegel> { new() };

        pdf.VanPlan(wedstrijden, "ALLSTARS").Should().BeOfType<FileContentResult>()
            .Which.FileDownloadName.Should().Be(bestandsnaam);
        html.VanPlan(wedstrijden, "ALLSTARS").Should().BeAssignableTo<ContentResult>()
            .Which.Content.Should().Contain(verwachteTijd);
    }

    private sealed class Regel : IVeldbezettingRegel
    {
        public string? AanvangsTijd => "09:30";
        public string TeamNaam => "JO10-1";
        public string Wedstrijd => "AllStars JO10-1 - Gasten JO10-2";
        public string? Uitteam => "Gasten JO10-2";
        public string? Veld => "veld 3 A";
        public string? Competitiesoort => "competitie";
    }

    private sealed class PlanRegel : IPlanWedstrijdRegel
    {
        public string TeamNaam => "JO10-1";
        public string Wedstrijd => "AllStars JO10-1 - Gasten JO10-2";
        public string? Competitiesoort => "competitie";
        public string? HuidigeTijd => "09:00";
        public string? HuidigeVeld => "veld 1";
        public string? OptimaalTijd => "08:30";
        public string? OptimaalVeld => "veld 2";
    }
}
