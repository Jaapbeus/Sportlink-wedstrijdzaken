using System.Text.RegularExpressions;
using FluentAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// Regressietests voor de HTML-injectiekwetsbaarheid in de dagplanning-export (#1010).
///
/// <para>
/// De generator interpoleerde wedstrijd-/team-/veldnamen, locatie, footer en suggestieteksten
/// ongeëncodeerd als HTML — alleen een apostrof werd voor één data-attribuut vervangen. Deze
/// tests draaien tegen de VOLLEDIGE gegenereerde HTML-output van
/// <see cref="PlannerHtmlGenerator.GenereerHtml"/> en <see cref="PlannerHtmlGenerator.GenereerEmailHtml"/>
/// — niet tegen een geïsoleerde encode-helper — omdat de kwetsbaarheid zat in hóe de generator de
/// stukken samenvoegt, niet in een los te testen functie.
/// </para>
/// </summary>
public class PlannerHtmlGeneratorTests
{
    private static readonly HtmlInstellingen Instellingen = new(
        Accommodatie: "Sportpark Voorbeeld",
        PlannerAfzenderNaam: "Veldplanner",
        EersteElftalNaam: null,
        ClubCode: null);

    private static readonly List<VeldInfo> Velden = new()
    {
        new VeldInfo { VeldNummer = 1, VeldNaam = "Veld 1", VeldType = "gras" },
    };

    private static BestaandeWedstrijd Wedstrijd(string naam) => new()
    {
        Datum = new DateOnly(2026, 9, 6),
        AanvangsTijd = new TimeOnly(10, 0),
        EindTijd = new TimeOnly(11, 30),
        VeldNummer = 1,
        VeldDeelGebruik = 1.0m,
        Wedstrijd = naam,
        Bron = "Programma",
    };

    private static string Genereer(string wedstrijdNaam, HtmlInstellingen? instellingen = null) =>
        PlannerHtmlGenerator.GenereerHtml(
            new DateOnly(2026, 9, 6),
            new List<BestaandeWedstrijd> { Wedstrijd(wedstrijdNaam) },
            new List<OptimalisatieSuggestie>(),
            Velden,
            "huidig",
            instellingen ?? Instellingen);

    private static int AantalScriptTags(string html) =>
        Regex.Matches(html, "<script", RegexOptions.IgnoreCase).Count;

    [Fact]
    public void ScriptTagInWedstrijdnaam_WordtNooitLetterlijkElement()
    {
        var payload = "<script>evil()</script>";
        var html = Genereer(payload);

        // Alleen het eigen vaste klikscript van de generator mag <script> opleveren.
        AantalScriptTags(html).Should().Be(1);
        html.Should().NotContain(payload);
        html.Should().Contain("&lt;script&gt;evil()&lt;/script&gt;");
    }

    [Fact]
    public void EventHandlerAchtigeInhoud_WordtNietUitgevoerbaar()
    {
        var payload = "\"><img src=x onerror=alert(1)>";
        var html = Genereer(payload);

        html.Should().NotContain("<img");
        AantalScriptTags(html).Should().Be(1);
        // De data-wedstrijd-attribuutwaarde mag niet vroegtijdig sluiten door de aanhalingstekens.
        html.Should().Contain("&quot;&gt;&lt;img src=x onerror=alert(1)&gt;");
    }

    [Fact]
    public void QuotesEnAmpersands_WordenGeencodeerd()
    {
        var html = Genereer("Team A & B \"Test\"");

        html.Should().Contain("Team A &amp; B &quot;Test&quot;");
        html.Should().NotContain("Team A & B \"Test\"");
    }

    [Fact]
    public void GewoneNederlandseNaamMetApostrof_BlijftLeesbaar()
    {
        var html = Genereer("SV d'Ancona 1");

        // WebUtility.HtmlEncode encodeert de apostrof naar &#39; — de browser rendert dat
        // identiek aan een letterlijke apostrof, dus de naam blijft voor een lezer leesbaar.
        // Enige eis: geen dubbele encodering (die zou "&#39;" letterlijk tonen).
        html.Should().Contain("SV d&#39;Ancona 1");
        html.Should().NotContain("SV d&amp;#39;Ancona 1");
        AantalScriptTags(html).Should().Be(1);
    }

    [Fact]
    public void ScriptTagInAccommodatienaam_WordtGeencodeerd()
    {
        var instellingen = Instellingen with { Accommodatie = "<script>alert(2)</script>" };
        var html = Genereer("Team 1", instellingen);

        AantalScriptTags(html).Should().Be(1);
        html.Should().Contain("&lt;script&gt;alert(2)&lt;/script&gt;");
    }

    [Fact]
    public void ScriptTagInPlannerAfzenderNaam_WordtGeencodeerd()
    {
        var instellingen = Instellingen with { PlannerAfzenderNaam = "<script>alert(3)</script>" };
        var html = Genereer("Team 1", instellingen);

        AantalScriptTags(html).Should().Be(1);
        html.Should().Contain("&lt;script&gt;alert(3)&lt;/script&gt;");
    }

    [Fact]
    public void ScriptTagInSuggestieVeldnaam_WordtGeencodeerdInStatusEnDataAttribuut()
    {
        var wedstrijd = Wedstrijd("Team 1");
        var suggestie = new OptimalisatieSuggestie
        {
            Wedstrijd = "Team 1",
            HuidigVeldNummer = 1,
            HuidigVeld = "Veld 1",
            HuidigeTijd = "10:00",
            NieuwVeldNummer = 1,
            NieuwVeld = "<script>alert(4)</script>",
            NieuweTijd = "11:00",
            Reden = "test",
        };

        var html = PlannerHtmlGenerator.GenereerHtml(
            new DateOnly(2026, 9, 6),
            new List<BestaandeWedstrijd> { wedstrijd },
            new List<OptimalisatieSuggestie> { suggestie },
            Velden,
            "huidig",
            Instellingen);

        AantalScriptTags(html).Should().Be(1);
        html.Should().Contain("&lt;script&gt;alert(4)&lt;/script&gt;");
    }

    [Fact]
    public void JavascriptSchemaInBrowserUrl_WordtGeblokkeerd()
    {
        var html = PlannerHtmlGenerator.GenereerEmailHtml(
            new DateOnly(2026, 9, 6),
            new List<BestaandeWedstrijd>(),
            new List<OptimalisatieSuggestie>(),
            Velden,
            "huidig",
            "javascript:alert(1)",
            Instellingen);

        html.Should().NotContain("href='javascript:alert(1)'");
        html.Should().Contain("href='#'");
    }

    [Fact]
    public void HttpsBrowserUrl_BlijftIntactInEmailHtml()
    {
        var html = PlannerHtmlGenerator.GenereerEmailHtml(
            new DateOnly(2026, 9, 6),
            new List<BestaandeWedstrijd>(),
            new List<OptimalisatieSuggestie>(),
            Velden,
            "huidig",
            "https://example.test/planner",
            Instellingen);

        html.Should().Contain("href='https://example.test/planner'");
    }

    [Fact]
    public void ScriptTagInWedstrijdnaam_EmailHtml_WordtGeencodeerd()
    {
        var wedstrijd = Wedstrijd("<script>alert(5)</script>");
        var html = PlannerHtmlGenerator.GenereerEmailHtml(
            new DateOnly(2026, 9, 6),
            new List<BestaandeWedstrijd> { wedstrijd },
            new List<OptimalisatieSuggestie>(),
            Velden,
            "huidig",
            "https://example.test/planner",
            Instellingen);

        html.Should().NotContain("<script>alert(5)</script>");
        html.Should().Contain("&lt;script&gt;alert(5)&lt;/script&gt;");
    }
}
