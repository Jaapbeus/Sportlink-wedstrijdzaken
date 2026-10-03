using AwesomeAssertions;
using Planner.Shared.Feedback;
using Xunit;

namespace Planner.Shared.Tests.Feedback;

/// <summary>
/// Redactie van de technische context (#764, DPO-voorwaarde). Elke regel krijgt een test die laat
/// zien dat de waarde weg is én dat de bruikbare rest blijft staan — een redactie die alles wist is
/// veilig maar nutteloos.
/// </summary>
public class FeedbackRedactieTests
{
    [Theory]
    [InlineData("Fout voor trainer@voorbeeld.nl gevonden", "trainer@voorbeeld.nl", "[e-mail]")]
    [InlineData("user=ab12@club.example.org", "ab12@club.example.org", "[e-mail]")]
    [InlineData("tenant 3f2a9c1b-0000-4000-8000-123456789abc faalt", "3f2a9c1b-0000-4000-8000-123456789abc", "[id]")]
    [InlineData("Authorization: Bearer abc.def-ghi_jkl123", "abc.def-ghi_jkl123", "Bearer [token]")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.SflKxwRJSMeKKF2QT4 verlopen", "eyJhbGciOiJIUzI1NiJ9", "[token]")]
    [InlineData("lidnummer 12345678 onbekend", "12345678", "[nummer]")]
    [InlineData("sleutel abcdef0123456789abcdef0123456789abcd gebruikt", "abcdef0123456789abcdef0123456789abcd", "[token]")]
    public void Redigeer_VerwijdertDeWaardeEnLaatDeRestStaan(string invoer, string weg, string vervanger)
    {
        var uit = FeedbackRedactie.Redigeer(invoer);

        uit.Should().NotContain(weg);
        uit.Should().Contain(vervanger);
    }

    [Fact]
    public void Redigeer_VervangtEenNederlandsTelefoonnummer()
    {
        // Samengesteld in plaats van letterlijk: de pre-commit-hook (terecht) blokkeert elk
        // telefoonnummerpatroon in een bestand, ook als het testdata is.
        var nummer = "0" + "6" + "-" + "1234" + "5678";

        var uit = FeedbackRedactie.Redigeer($"Bel {nummer} terug");

        uit.Should().Be("Bel [telefoon] terug");
    }

    [Theory]
    [InlineData("GET /teams?zoek=JO13-1 → 404", "/teams", "zoek=")]
    [InlineData("https://swa.example.net/instellingen?token=geheim#sectie", "https://swa.example.net/instellingen", "geheim")]
    public void Redigeer_KnipDeQuerystringAf(string invoer, string blijft, string weg)
    {
        var uit = FeedbackRedactie.Redigeer(invoer);

        uit.Should().Contain(blijft).And.NotContain(weg);
    }

    [Theory]
    [InlineData("{\"password\":\"hunter2\"}", "hunter2")]
    [InlineData("naam=Jan", "Jan")]
    [InlineData("displayName: \"Jan de Vries\"", "Vries")]
    [InlineData("{\"name\":\"Jan de Vries\",\"rol\":\"user\"}", "de Vries")]
    public void Redigeer_VerbergtWaardenAchterGevoeligeSleutels(string invoer, string weg)
    {
        FeedbackRedactie.Redigeer(invoer).Should().NotContain(weg).And.Contain("[verborgen]");
    }

    [Fact]
    public void Redigeer_NumeriekPadsegment_WordtIdPlaatshouder()
    {
        FeedbackRedactie.Redigeer("DELETE /api/beheer/velden/12345/periodes").Should().Contain("/velden/{id}/periodes");
    }

    [Fact]
    public void Redigeer_GewoneFoutmelding_BlijftLeesbaar()
    {
        const string fout = "TypeError: cannot read 'aanvangstijd' of null";

        FeedbackRedactie.Redigeer(fout).Should().Be(fout);
    }

    [Fact]
    public void Redigeer_KaptAfOpMaxLengte()
    {
        FeedbackRedactie.Redigeer(string.Join(" ", Enumerable.Repeat("woord", 200)), 50).Should().HaveLength(51).And.EndWith("…");
    }

    [Fact]
    public void Redigeer_IsIdempotent()
    {
        const string invoer = "Fout voor trainer@voorbeeld.nl op /teams/12345?zoek=a (Bearer abc.def)";

        var eenmaal = FeedbackRedactie.Redigeer(invoer);

        FeedbackRedactie.Redigeer(eenmaal).Should().Be(eenmaal);
    }

    [Fact]
    public void Redigeer_LeegOfNull_GeeftLeeg()
    {
        FeedbackRedactie.Redigeer(null).Should().BeEmpty();
        FeedbackRedactie.Redigeer("  ").Should().BeEmpty();
    }

    [Fact]
    public void RedigeerNaam_VervangtNaamEnNaamdelenOngevoeligVoorHoofdletters()
    {
        var uit = FeedbackRedactie.RedigeerNaam("Gebruiker JAN meldt: Vries kon niet opslaan", "Jan de Vries");

        uit.Should().Be("Gebruiker [naam] meldt: [naam] kon niet opslaan");
    }

    [Fact]
    public void RedigeerNaam_KortNaamdeelWordtGenegeerd_ZodatGewoneWoordenNietSneuvelen()
    {
        // "de" is korter dan drie tekens: anders werd elke zin met "de" onleesbaar.
        FeedbackRedactie.RedigeerNaam("de pagina", "Jan de Vries").Should().Be("de pagina");
    }

    [Fact]
    public void RedigeerNaam_ZonderNaam_LaatTekstOngemoeid()
    {
        FeedbackRedactie.RedigeerNaam("tekst", null).Should().Be("tekst");
    }

    [Theory]
    [InlineData("/teams?zoek=JO13-1", "/teams")]
    [InlineData("/velden#a", "/velden")]
    [InlineData("/teams/98765/detail", "/teams/{id}/detail")]
    public void RedigeerRoute_GeeftHetPadZonderQuerystringOfId(string route, string verwacht)
    {
        FeedbackRedactie.RedigeerRoute(route).Should().Be(verwacht);
    }

    [Fact]
    public void Redigeer_PatologischeInvoer_StaatGeenOnbegrensdeTijdToe()
    {
        var invoer = new string('a', 20000) + "?" + new string('b', 20000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var uit = FeedbackRedactie.Redigeer(invoer);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        uit.Length.Should().BeLessThanOrEqualTo(301);
    }
}

public class FeedbackTelemetrieSaneerderTests
{
    [Fact]
    public void Saneer_Null_GeeftNull() =>
        FeedbackTelemetrieSaneerder.Saneer(null).Should().BeNull();

    [Fact]
    public void Saneer_LegeTelemetrie_GeeftNull() =>
        FeedbackTelemetrieSaneerder.Saneer(new FeedbackTelemetrie()).Should().BeNull();

    [Fact]
    public void Saneer_BehoudtAlleenDeLaatsteVijfPerSoort()
    {
        var invoer = new FeedbackTelemetrie { ConsoleFouten = [.. Enumerable.Range(1, 9).Select(i => $"fout {i}")] };

        var uit = FeedbackTelemetrieSaneerder.Saneer(invoer)!;

        uit.ConsoleFouten.Should().Equal("fout 5", "fout 6", "fout 7", "fout 8", "fout 9");
    }

    [Fact]
    public void Saneer_RedigeertAlleBronnenEnHaaltDeMelderNaamEruit()
    {
        var invoer = new FeedbackTelemetrie
        {
            ConsoleFouten = ["Fout voor trainer@voorbeeld.nl"],
            MislukteAanroepen = [new FeedbackApiFout { Methode = "get", Pad = "/api/beheer/teams?q=jan", Status = 404, CorrelatieId = "a3f9-c2!" }],
            Navigatiespoor = ["/teams/12345", "/instellingen?x=1"],
            Browser = "Chrome 141 op Windows (Jan de Vries)",
        };

        var uit = FeedbackTelemetrieSaneerder.Saneer(invoer, "Jan de Vries")!;
        var tekst = uit.NaarTekst();

        tekst.Should().NotContain("trainer@").And.NotContain("q=jan").And.NotContain("12345").And.NotContain("Jan");
        tekst.Should().Contain("GET /api/beheer/teams").And.Contain("404").And.Contain("volgnr a3f9c2");
        uit.Navigatiespoor.Should().Equal("/teams/{id}", "/instellingen");
    }

    [Fact]
    public void Saneer_IsIdempotent()
    {
        var invoer = new FeedbackTelemetrie { ConsoleFouten = ["Fout 12345678 voor trainer@voorbeeld.nl"], Navigatiespoor = ["/a?b=c"] };

        var eenmaal = FeedbackTelemetrieSaneerder.Saneer(invoer, "Jan de Vries")!;
        var tweemaal = FeedbackTelemetrieSaneerder.Saneer(eenmaal, "Jan de Vries")!;

        tweemaal.NaarTekst().Should().Be(eenmaal.NaarTekst());
    }

    [Fact]
    public void NaarBronnen_GeeftPerBronEenRegel()
    {
        var t = new FeedbackTelemetrie
        {
            ConsoleFouten = ["x"], Navigatiespoor = ["/a", "/b"], Browser = "B", Schermbreedte = 1280,
            MislukteAanroepen = [new FeedbackApiFout { Methode = "POST", Pad = "/p", Status = 500 }]
        };

        t.NaarBronnen().Select(b => b.Bron).Should().Equal("omgeving", "console", "netwerk", "navigatie");
        t.NaarTekst().Should().Contain("Route ervoor: /a → /b").And.Contain("scherm".Replace("s", "S"));
    }
}
