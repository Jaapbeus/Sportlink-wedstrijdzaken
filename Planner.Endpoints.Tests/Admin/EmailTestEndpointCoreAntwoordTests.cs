using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Admin;
using Planner.Shared.Email.Trace;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

/// <summary>#1583: het antwoord van de e-mailtester bevat een definitief eindoordeel met de actuele poort-instelling.</summary>
public class EmailTestEndpointCoreAntwoordTests
{
    private static TraceBuilder OnbekendTeam()
        => new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO 13/2", "Onopgelost", 0, null, null)
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"teamOnbekend\":true}");

    private static TraceBuilder HerkendTeam()
        => new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO 13/2", "Alias", 1.0, null, "JO13-2")
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"beschikbaar\":true}");

    private static JsonElement Antwoord(TraceBuilder trace, bool poortActief, bool replyMoetVersturen = true, bool reviewModus = false)
    {
        var result = EmailTestEndpointCore.Antwoord(
            new { }, "BeschikbaarheidCheck", "Vraag om een oefenwedstrijd", "{}", trace,
            new TesterBeleid(poortActief, replyMoetVersturen, "reden", reviewModus), "Onderwerp", "Body");
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public void OnzekerMetPoortAan_GeeftReview_EnLegtDePoortstapVastInDeTrace()
    {
        var json = Antwoord(OnbekendTeam(), poortActief: true);

        var e = json.GetProperty("eindoordeel");
        e.GetProperty("uitkomst").GetString().Should().Be("Review");
        e.GetProperty("titel").GetString().Should().Contain("géén antwoord verstuurd");
        e.GetProperty("zekerheidspoortActief").GetBoolean().Should().BeTrue();
        json.GetProperty("trace").GetProperty("stappen").EnumerateArray()
            .Select(s => s.GetProperty("code").GetString()).Should().Contain(TraceCodes.Zekerheidspoort);
    }

    [Fact]
    public void OnzekerMetPoortUit_GeeftAutomatischVerstuurd_MetWaarschuwing()
    {
        var e = Antwoord(OnbekendTeam(), poortActief: false).GetProperty("eindoordeel");

        e.GetProperty("uitkomst").GetString().Should().Be("AutomatischVerstuurd");
        e.GetProperty("waarschuwing").GetBoolean().Should().BeTrue();
        e.GetProperty("zekerheidspoortActief").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Zeker_GeeftAutomatischVerstuurd_ZonderPoortstap()
    {
        var json = Antwoord(HerkendTeam(), poortActief: true);

        json.GetProperty("eindoordeel").GetProperty("uitkomst").GetString().Should().Be("AutomatischVerstuurd");
        json.GetProperty("trace").GetProperty("stappen").EnumerateArray()
            .Select(s => s.GetProperty("code").GetString()).Should().NotContain(TraceCodes.Zekerheidspoort);
    }

    [Fact]
    public void ReplyBeleidDatZwijgt_GeeftGeenAntwoord_ZonderPoortstap_ZoalsInProductie()
    {
        var json = Antwoord(OnbekendTeam(), poortActief: true, replyMoetVersturen: false);

        json.GetProperty("eindoordeel").GetProperty("uitkomst").GetString().Should().Be("GeenAntwoord");
        json.GetProperty("trace").GetProperty("stappen").EnumerateArray()
            .Select(s => s.GetProperty("code").GetString()).Should().NotContain(TraceCodes.Zekerheidspoort);
    }

    [Fact]
    public void Antwoord_BehoudtDeBestaandeVelden()
    {
        var json = Antwoord(HerkendTeam(), poortActief: true);

        json.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        json.GetProperty("voorbeeldAntwoord").GetProperty("onderwerp").GetString().Should().Be("Onderwerp");
        json.GetProperty("leersuggestie").GetProperty("verzoekType").GetString().Should().Be("BeschikbaarheidCheck");
    }

    private static IEnumerable<string?> Stapcodes(JsonElement json)
        => json.GetProperty("trace").GetProperty("stappen").EnumerateArray().Select(s => s.GetProperty("code").GetString());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReviewModusAan_ZekereTrace_GaatToch_NaarReview_ZonderPoortstap(bool poortActief)
    {
        var json = Antwoord(HerkendTeam(), poortActief, replyMoetVersturen: true, reviewModus: true);

        var e = json.GetProperty("eindoordeel");
        e.GetProperty("uitkomst").GetString().Should().Be("Review");
        e.GetProperty("reviewModusActief").GetBoolean().Should().BeTrue();
        Stapcodes(json).Should().NotContain(TraceCodes.Zekerheidspoort);
    }

    /// <summary>#1608: een onderdrukt antwoord is ook in reviewmodus "Handmatige planning", zoals de productieverwerking.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReviewModusAan_ReplyBeleidZwijgt_IsHandmatigePlanning_ZonderPoortstap(bool poortActief)
    {
        var json = Antwoord(HerkendTeam(), poortActief, replyMoetVersturen: false, reviewModus: true);

        var e = json.GetProperty("eindoordeel");
        e.GetProperty("uitkomst").GetString().Should().Be("GeenAntwoord");
        e.GetProperty("titel").GetString().Should().Contain("Handmatige planning");
        e.GetProperty("reviewModusActief").GetBoolean().Should().BeTrue();
        Stapcodes(json).Should().NotContain(TraceCodes.Zekerheidspoort);
    }

    [Fact]
    public void ReviewModusAan_OnzekereTrace_PoortUit_IsNog_Review_ZonderWaarschuwing()
    {
        var e = Antwoord(OnbekendTeam(), poortActief: false, reviewModus: true).GetProperty("eindoordeel");

        e.GetProperty("uitkomst").GetString().Should().Be("Review");
        e.GetProperty("waarschuwing").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void ReviewModusUit_BlijftHetBestaandeGedrag_EnMeldtDeModus()
    {
        var json = Antwoord(HerkendTeam(), poortActief: true, reviewModus: false);

        json.GetProperty("eindoordeel").GetProperty("uitkomst").GetString().Should().Be("AutomatischVerstuurd");
        json.GetProperty("eindoordeel").GetProperty("reviewModusActief").GetBoolean().Should().BeFalse();
    }
}
