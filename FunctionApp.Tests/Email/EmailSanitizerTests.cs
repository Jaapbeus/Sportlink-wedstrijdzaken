using FluentAssertions;
using SportlinkFunction;
using Xunit;
using Planner.Shared;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Tests voor EmailSanitizer.SanitizeFoutMelding — regressietest voor #420/#463.
/// </summary>
public class EmailSanitizerTests
{
    [Fact]
    public void SanitizeFoutMelding_EmailInMessage_WordtGemaskeerd()
    {
        var result = EmailSanitizer.SanitizeFoutMelding("Fout voor user@example.com");
        result.Should().NotContain("user@example.com");
        result.Should().Contain("[e-mail]");
    }

    [Fact]
    public void SanitizeFoutMelding_MeerdereEmailsInMessage_WordenAlleMaskeerd()
    {
        var result = EmailSanitizer.SanitizeFoutMelding("Van a@x.nl naar b@y.nl: fout");
        result.Should().NotContain("a@x.nl");
        result.Should().NotContain("b@y.nl");
        result.Should().Contain("[e-mail]");
    }

    [Fact]
    public void SanitizeFoutMelding_GeenEmail_OngewijzigdTeruggegeven()
    {
        var result = EmailSanitizer.SanitizeFoutMelding("Gewone foutmelding zonder e-mailadres");
        result.Should().Be("Gewone foutmelding zonder e-mailadres");
    }

    [Fact]
    public void SanitizeFoutMelding_LangeBoodschap_WordtAfgekapt()
    {
        var lang = new string('x', 300);
        var result = EmailSanitizer.SanitizeFoutMelding(lang);
        result.Length.Should().BeLessOrEqualTo(203); // 200 tekens + "…"
        result.Should().EndWith("…");
    }

    [Fact]
    public void SanitizeFoutMelding_KorteBoodschap_WordtNietAfgekapt()
    {
        var kort = "Korte fout";
        var result = EmailSanitizer.SanitizeFoutMelding(kort);
        result.Should().Be(kort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeFoutMelding_LegOfNull_GeeftOnbekendefout(string? input)
    {
        var result = EmailSanitizer.SanitizeFoutMelding(input!);
        result.Should().Be("Onbekende fout");
    }
}

/// <summary>
/// Regressietests voor de HTML-sanitisatie in beide richtingen:
/// - <see cref="EmailSanitizer.StripHtml"/> op inkomende berichttekst,
/// - <see cref="EmailSanitizer.BouwVeiligeHtmlBody"/> op tekst die als HTML-mail uitgaat.
///
/// Aanleiding: dubbel-geëncodeerde markup overleefde het strippen (tags werden verwijderd vóórdat
/// de entiteiten werden gedecodeerd), waarna de doorgestuurde teambegeleiding-mail als
/// <c>BodyType.Html</c> werd verstuurd. Een externe afzender kon zo een klikbare phishing-link of
/// tracking-pixel in een mail plaatsen die van de club lijkt te komen.
/// </summary>
public class EmailHtmlSanitisatieTests
{
    // ---------- StripHtml: gelaagde encodering ----------

    [Fact]
    public void StripHtml_EnkelGeencodeerdeAnchor_TagVerdwijnt()
    {
        var result = EmailSanitizer.StripHtml(
            "<p>Hoi</p><a href=\"https://phishing.example\">Bekijk wedstrijdschema</a>");

        result.Should().NotContain("<a");
        result.Should().NotContain("href");
        result.Should().NotContain("phishing.example");
        result.Should().Contain("Bekijk wedstrijdschema");
    }

    [Fact]
    public void StripHtml_DubbelGeencodeerdeAnchor_WordtAlsnogGestript()
    {
        // Letterlijke tekst in de HTML-body: &amp;lt;a href=...&amp;gt;
        // Ronde 1 ziet geen tag; de entiteit-decodering maakte er daarna echte markup van.
        var result = EmailSanitizer.StripHtml(
            "&amp;lt;a href=\"https://phishing.example\"&amp;gt;Bekijk wedstrijdschema&amp;lt;/a&amp;gt;");

        result.Should().NotContain("<a");
        result.Should().NotContain("href");
        result.Should().NotContain("phishing.example");
        result.Should().Contain("Bekijk wedstrijdschema");
    }

    [Fact]
    public void StripHtml_DrievoudigGeencodeerdeAnchor_WordtAlsnogGestript()
    {
        var result = EmailSanitizer.StripHtml(
            "&amp;amp;lt;a href=\"https://phishing.example\"&amp;amp;gt;Klik hier&amp;amp;lt;/a&amp;amp;gt;");

        result.Should().NotContain("<a");
        result.Should().NotContain("href");
        result.Should().NotContain("phishing.example");
        result.Should().Contain("Klik hier");
    }

    [Fact]
    public void StripHtml_DubbelGeencodeerdeImg_TrackingPixelVerdwijnt()
    {
        var result = EmailSanitizer.StripHtml(
            "Tekst &amp;lt;img src=\"https://tracker.example/p.gif\" width=\"1\"&amp;gt; einde");

        result.Should().NotContain("<img");
        result.Should().NotContain("tracker.example");
        result.Should().Contain("Tekst");
        result.Should().Contain("einde");
    }

    [Fact]
    public void StripHtml_ScriptBlok_InhoudVerdwijntOok()
    {
        var result = EmailSanitizer.StripHtml(
            "Voor <script>alert('xss')</script> na");

        result.Should().NotContain("<script");
        result.Should().NotContain("alert(");
        result.Should().Contain("Voor");
        result.Should().Contain("na");
    }

    [Fact]
    public void StripHtml_DubbelGeencodeerdScriptBlok_InhoudVerdwijntOok()
    {
        var result = EmailSanitizer.StripHtml(
            "Voor &amp;lt;script&amp;gt;alert('xss')&amp;lt;/script&amp;gt; na");

        result.Should().NotContain("<script");
        result.Should().NotContain("alert(");
        result.Should().Contain("Voor");
        result.Should().Contain("na");
    }

    [Fact]
    public void StripHtml_StyleBlok_CssVerdwijnt()
    {
        var result = EmailSanitizer.StripHtml(
            "<style>body{background:url('https://tracker.example/x.png')}</style>Bericht");

        result.Should().NotContain("<style");
        result.Should().NotContain("tracker.example");
        result.Should().NotContain("background");
        result.Should().Contain("Bericht");
    }

    [Fact]
    public void StripHtml_DubbelGeencodeerdStyleBlok_CssVerdwijnt()
    {
        var result = EmailSanitizer.StripHtml(
            "&amp;lt;style&amp;gt;body{background:red}&amp;lt;/style&amp;gt;Bericht");

        result.Should().NotContain("<style");
        result.Should().NotContain("background");
        result.Should().Contain("Bericht");
    }

    [Fact]
    public void StripHtml_NumeriekeEntiteiten_WordenOokGedecodeerdEnGestript()
    {
        // &#60; / &#62; zag de oude implementatie helemaal niet.
        var result = EmailSanitizer.StripHtml("&#60;a href=\"https://phishing.example\"&#62;Klik&#60;/a&#62;");

        result.Should().NotContain("<a");
        result.Should().NotContain("phishing.example");
        result.Should().Contain("Klik");
    }

    [Fact]
    public void StripHtml_GewoneTekstMetAmpersand_BlijftLeesbaar()
    {
        var result = EmailSanitizer.StripHtml("<p>Jan &amp; Piet komen om 10:00</p>");

        result.Should().Be("Jan & Piet komen om 10:00");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void StripHtml_LeegOfNull_GeeftLegeString(string? input)
    {
        EmailSanitizer.StripHtml(input!).Should().Be("");
    }

    // ---------- BouwVeiligeHtmlBody: uitgaande mail ----------

    [Fact]
    public void BouwVeiligeHtmlBody_PlatteTekstMetAnchor_WordtGeescaped()
    {
        var result = EmailSanitizer.BouwVeiligeHtmlBody(
            "Vraag van iemand\n\n---\n<a href=\"https://phishing.example\">Bekijk wedstrijdschema</a>\n---");

        // Geen klikbare link: de markup staat als leestekst in de mail.
        result.Should().NotContain("<a href");
        result.Should().Contain("&lt;a href");
        result.Should().Contain("Bekijk wedstrijdschema");
    }

    [Fact]
    public void BouwVeiligeHtmlBody_PlatteTekst_RegeleindesWordenBr()
    {
        var result = EmailSanitizer.BouwVeiligeHtmlBody("Regel 1\nRegel 2");

        result.Should().Be("Regel 1<br />Regel 2");
    }

    [Fact]
    public void BouwVeiligeHtmlBody_PlatteTekstMetCrLf_RegeleindesWordenEenmaalBr()
    {
        var result = EmailSanitizer.BouwVeiligeHtmlBody("Regel 1\r\nRegel 2");

        result.Should().Be("Regel 1<br />Regel 2");
    }

    [Fact]
    public void BouwVeiligeHtmlBody_EigenTemplateHtml_BlijftExactOngewijzigd()
    {
        // Exact de vorm die AdminTeambegeleidingFunction opbouwt: attribuutloze opmaaktags en
        // gebruikerstekst die daar al is ge-escaped. Deze opmaak mag niet veranderen.
        var eigenHtml =
            "<p>Er is een vraag binnengekomen over de begeleiding van <strong>JO13-4</strong>.</p>\n"
            + "<p><strong>Vraagsteller:</strong> Jan de Vries</p>\n"
            + "<hr />\n"
            + "<p>Eerste regel<br />Tweede regel</p>\n"
            + "<hr />\n"
            + "<p><em>U kunt direct antwoorden op dit bericht.</em></p>";

        EmailSanitizer.BouwVeiligeHtmlBody(eigenHtml).Should().Be(eigenHtml);
    }

    [Fact]
    public void BouwVeiligeHtmlBody_EigenHtmlMetIngeslopenAnchor_LinkVerdwijnt()
    {
        var result = EmailSanitizer.BouwVeiligeHtmlBody(
            "<p>Vraag over <strong>JO13-4</strong></p><p><a href=\"https://phishing.example\">Bekijk wedstrijdschema</a></p>");

        result.Should().NotContain("<a");
        result.Should().NotContain("phishing.example");
        result.Should().Contain("<strong>JO13-4</strong>");
        result.Should().Contain("Bekijk wedstrijdschema");
    }

    [Fact]
    public void BouwVeiligeHtmlBody_EigenHtmlMetImgEnScript_WordenVerwijderd()
    {
        var result = EmailSanitizer.BouwVeiligeHtmlBody(
            "<p>Hoi</p><img src=\"https://tracker.example/p.gif\"><script>alert(1)</script><style>p{color:red}</style>");

        result.Should().NotContain("<img");
        result.Should().NotContain("tracker.example");
        result.Should().NotContain("<script");
        result.Should().NotContain("alert(");
        result.Should().NotContain("<style");
        result.Should().NotContain("color:red");
        result.Should().Contain("<p>Hoi</p>");
    }

    [Fact]
    public void BouwVeiligeHtmlBody_TagMetAttribuut_ValtBuitenAllowlist()
    {
        var result = EmailSanitizer.BouwVeiligeHtmlBody(
            "<p onclick=\"steal()\">Tekst</p><p>Normaal</p>");

        result.Should().NotContain("onclick");
        result.Should().Contain("Tekst");
        result.Should().Contain("<p>Normaal</p>");
    }

    [Fact]
    public void BouwVeiligeHtmlBody_AlGeescapedeGebruikerstekst_BlijftGeescaped()
    {
        // Entiteiten mogen nooit gedecodeerd worden: dat zou van al veilige tekst weer markup maken.
        var result = EmailSanitizer.BouwVeiligeHtmlBody("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>");

        result.Should().Contain("&lt;script&gt;");
        result.Should().NotContain("<script>");
    }

    [Fact]
    public void StripHtmlDanBouwVeiligeHtmlBody_DubbelGeencodeerdePhishingLink_NooitKlikbaar()
    {
        // Volledige keten zoals in productie: Graph-body → StripHtml → doorgestuurde HTML-mail.
        var inkomend =
            "&amp;lt;a href=\"https://phishing.example\"&amp;gt;Bekijk wedstrijdschema&amp;lt;/a&amp;gt;";

        var opgeslagenBody = EmailSanitizer.StripHtml(inkomend);
        var uitgaandeHtml = EmailSanitizer.BouwVeiligeHtmlBody(
            $"Er is een vraag binnengekomen.\n\n---\n{opgeslagenBody}\n---");

        uitgaandeHtml.Should().NotContain("<a");
        uitgaandeHtml.Should().NotContain("href");
        uitgaandeHtml.Should().NotContain("phishing.example");
        uitgaandeHtml.Should().Contain("Bekijk wedstrijdschema");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BouwVeiligeHtmlBody_LeegOfNull_GeeftLegeString(string? input)
    {
        EmailSanitizer.BouwVeiligeHtmlBody(input!).Should().Be("");
    }
}
