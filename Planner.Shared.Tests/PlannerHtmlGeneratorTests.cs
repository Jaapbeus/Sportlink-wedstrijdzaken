using System.Text.RegularExpressions;
using AwesomeAssertions;
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

    // ── Karakterisatie categorietabel (#1625) ──

    private static BestaandeWedstrijd CategorieWedstrijd(int veld, int uur, int minuut, string naam) => new()
    {
        Datum = new DateOnly(2026, 9, 6),
        AanvangsTijd = new TimeOnly(uur, minuut),
        EindTijd = new TimeOnly(uur, minuut).AddMinutes(75),
        VeldNummer = veld,
        VeldDeelGebruik = 1.0m,
        Wedstrijd = naam,
        Bron = "Programma",
    };

    private static string CategorieTabel(string html)
    {
        var start = html.IndexOf("Per leeftijdscategorie</h3>", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var tabelStart = html.IndexOf("<table", start, StringComparison.Ordinal);
        var tabelEinde = html.IndexOf("</table>", tabelStart, StringComparison.Ordinal) + "</table>".Length;
        return html[tabelStart..tabelEinde].Replace("\r\n", "\n");
    }

    [Fact]
    public void CategorieTabel_SorteertPerLeeftijdsklasse_MetScheidingEnVerplaatsing()
    {
        var velden = new List<VeldInfo>
        {
            new() { VeldNummer = 1, VeldNaam = "Veld 1", VeldType = "gras" },
            new() { VeldNummer = 2, VeldNaam = "Veld 2", VeldType = "kunstgras" },
        };
        var wedstrijden = new List<BestaandeWedstrijd>
        {
            CategorieWedstrijd(1, 9, 0, "Overig-1 - Tegenstander"),
            CategorieWedstrijd(1, 9, 30, "ALLSTARS 2 - Tegenstander"),
            CategorieWedstrijd(2, 9, 30, "ALLSTARS 1 - Tegenstander"),
            CategorieWedstrijd(1, 10, 0, "VR1 - Tegenstander"),
            CategorieWedstrijd(2, 10, 0, "O23-1 - Tegenstander"),
            CategorieWedstrijd(1, 10, 30, "G1 - Tegenstander"),
            CategorieWedstrijd(2, 10, 30, "MO20-1 - Tegenstander"),
            CategorieWedstrijd(1, 11, 0, "MO15-1 - Tegenstander"),
            CategorieWedstrijd(2, 11, 0, "JO19-1 - Tegenstander"),
            CategorieWedstrijd(1, 11, 30, "JO7-1 - Tegenstander"),
            CategorieWedstrijd(2, 11, 30, "JO19-2 - Tegenstander"),
        };
        var suggesties = new List<OptimalisatieSuggestie>
        {
            new()
            {
                Wedstrijd = "JO19-2 - Tegenstander", HuidigVeldNummer = 2, HuidigVeld = "Veld 2", HuidigeTijd = "11:30",
                NieuwVeldNummer = 1, NieuwVeld = "Veld 1", NieuweTijd = "12:30", Reden = "test",
            },
        };
        var instellingen = new HtmlInstellingen("Sportpark Voorbeeld", "Veldplanner", null, "ALLSTARS");

        var html = PlannerHtmlGenerator.GenereerHtml(new DateOnly(2026, 9, 6), wedstrijden, suggesties, velden, "optimaal", instellingen);

        CategorieTabel(html).Should().Be(ExpectedCategorieTabel.Replace("\r\n", "\n"));
    }

    // Golden string van de huidige uitvoer: sortering JO7 < MO15 < JO19 < MO20 < G < O23 < VR < senioren < overig,
    // scheidingslijn bij elke klassewissel, verplaatste wedstrijd (oud + nieuw) en vast eerste elftal.
    private const string ExpectedCategorieTabel = """
<table style='border-collapse:collapse;width:100%;font-size:11px;'>
<tr style='background:#21262d;'><th style='padding:5px 8px;text-align:left;color:#8b949e;'>Veld</th><th style='padding:5px 8px;text-align:left;color:#8b949e;'>Aanvang</th><th style='padding:5px 8px;text-align:left;color:#8b949e;'>Einde</th><th style='padding:5px 8px;text-align:left;color:#8b949e;'>Status</th><th style='padding:5px 8px;text-align:left;color:#8b949e;'>Wedstrijd</th></tr>
<tr style='background:transparent;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 1</td><td style='padding:4px 8px;'>11:30</td><td style='padding:4px 8px;'>12:45</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>JO7-1 - Tegenstander</td></tr>
<tr style='background:transparent;border-top:2px solid #21262d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 1</td><td style='padding:4px 8px;'>11:00</td><td style='padding:4px 8px;'>12:15</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>MO15-1 - Tegenstander</td></tr>
<tr style='background:transparent;border-top:2px solid #21262d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 2</td><td style='padding:4px 8px;'>11:00</td><td style='padding:4px 8px;'>12:15</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>JO19-1 - Tegenstander</td></tr>
<tr style='background:#1a0d00;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 2</td><td style='padding:4px 8px;'>11:30</td><td style='padding:4px 8px;'>12:45</td><td style='padding:4px 8px;color:#d29922;'>⟶ Veld 1 12:30</td><td style='padding:4px 8px;'>JO19-2 - Tegenstander</td></tr>
<tr style='background:#0d1a0d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 1</td><td style='padding:4px 8px;'>12:30</td><td style='padding:4px 8px;'>13:45</td><td style='padding:4px 8px;color:#2ea043;'>← van Veld 2 11:30</td><td style='padding:4px 8px;'>★ JO19-2 - Tegenstander</td></tr>
<tr style='background:transparent;border-top:2px solid #21262d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 2</td><td style='padding:4px 8px;'>10:30</td><td style='padding:4px 8px;'>11:45</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>MO20-1 - Tegenstander</td></tr>
<tr style='background:transparent;border-top:2px solid #21262d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 1</td><td style='padding:4px 8px;'>10:30</td><td style='padding:4px 8px;'>11:45</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>G1 - Tegenstander</td></tr>
<tr style='background:transparent;border-top:2px solid #21262d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 2</td><td style='padding:4px 8px;'>10:00</td><td style='padding:4px 8px;'>11:15</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>O23-1 - Tegenstander</td></tr>
<tr style='background:transparent;border-top:2px solid #21262d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 1</td><td style='padding:4px 8px;'>10:00</td><td style='padding:4px 8px;'>11:15</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>VR1 - Tegenstander</td></tr>
<tr style='background:transparent;border-top:2px solid #21262d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 1</td><td style='padding:4px 8px;'>09:30</td><td style='padding:4px 8px;'>10:45</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>ALLSTARS 2 - Tegenstander</td></tr>
<tr style='background:transparent;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 2</td><td style='padding:4px 8px;'>09:30</td><td style='padding:4px 8px;'>10:45</td><td style='padding:4px 8px;color:#388bfd;'>&#128274; Vast</td><td style='padding:4px 8px;'>ALLSTARS 1 - Tegenstander</td></tr>
<tr style='background:transparent;border-top:2px solid #21262d;border-bottom:1px solid #21262d;'><td style='padding:4px 8px;'>Veld 1</td><td style='padding:4px 8px;'>09:00</td><td style='padding:4px 8px;'>10:15</td><td style='padding:4px 8px;color:#8b949e;'></td><td style='padding:4px 8px;'>Overig-1 - Tegenstander</td></tr>
</table>
""";
}
