using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Theming;
using Xunit;

namespace Planner.Shared.Tests.Theming;

/// <summary>
/// Tests voor de gedeelde thema-kern (#1248). Vóór deze ontdubbeling bestond deze logica twee keer
/// — in <c>FunctionApp/Admin/AdminThemeFunction.cs</c> en
/// <c>FunctionApp.Postgres/Admin/AdminThemeFunction.cs</c> — en was er geen enkele test die de twee
/// kopieën tegen elkaar of tegen een verwachting valideerde. Dat is precies het risico dat het
/// issue beschrijft: een aanscherping in het ene bestand kan in het andere vergeten worden zonder
/// dat CI iets merkt. Deze tests draaien nu tegen de ene implementatie die beide tiers gebruiken.
/// <para>
/// Alle HTML-fragmenten hieronder gebruiken <c>example.com</c> (gereserveerd, RFC 2606) — nooit een
/// echte club of een echt domein.
/// </para>
/// </summary>
public class ThemeCoreTests
{
    private static readonly Uri Basis = new("https://www.example.com/");

    // ---------------------------------------------------------------------------------------
    // ExtractColors
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ExtractColors_SorteertAflopendOpFrequentie()
    {
        var html = "<style>a{color:#112233}b{color:#112233}c{color:#445566}</style>";

        var kleuren = ThemeCore.ExtractColors(html);

        kleuren.Should().Equal("#112233", "#445566");
    }

    [Fact]
    public void ExtractColors_SlaatTeAlgemeneKleurenOver()
    {
        // Wit, zwart en de grijstinten uit _skipColors komen op vrijwel elke site voor en zouden
        // de frequentielijst domineren zonder ooit een merkkleur te zijn.
        var html = "<style>a{color:#ffffff}b{color:#ffffff}c{color:#000000}d{color:#cccccc}e{color:#112233}</style>";

        ThemeCore.ExtractColors(html).Should().Equal("#112233");
    }

    [Fact]
    public void ExtractColors_NormaliseertNaarKleineLettersEnTeltHoofdletterVariantMee()
    {
        var html = "<style>a{color:#AABBCC}b{color:#aabbcc}c{color:#112233}</style>";

        ThemeCore.ExtractColors(html).Should().Equal("#aabbcc", "#112233");
    }

    [Fact]
    public void ExtractColors_GeeftHooguitHetMaximumTerug()
    {
        var html = string.Join("", Enumerable.Range(0x100000, 20).Select(i => $"#{i:x6} "));

        ThemeCore.ExtractColors(html).Should().HaveCount(ThemeCore.MaxGeextraheerdeKleuren);
    }

    [Fact]
    public void ExtractColors_HtmlZonderHexkleuren_GeeftLegeLijst()
    {
        // Geldige uitkomst, geen fout: CMS-sites zetten hun merkkleuren vaak in een extern
        // stylesheet dat hier niet opgehaald wordt.
        ThemeCore.ExtractColors("<html><body><p>geen kleuren</p></body></html>").Should().BeEmpty();
    }

    [Fact]
    public void ExtractColors_DrieletterigeHexcode_WordtNietMeegeteld()
    {
        ThemeCore.ExtractColors("<style>a{color:#abc}</style>").Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // IsValidHexColor
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#112233")]
    [InlineData("#AABBCC")]
    [InlineData(null)]   // optioneel veld → valt terug op de standaardkleur
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidHexColor_GeldigeOfWeggelatenWaarde_IsTrue(string? waarde)
        => ThemeCore.IsValidHexColor(waarde).Should().BeTrue();

    [Theory]
    [InlineData("#abc")]
    [InlineData("112233")]
    [InlineData("#1122334")]
    [InlineData("#gggggg")]
    [InlineData("rood")]
    [InlineData("#112233;background:url(x)")]
    public void IsValidHexColor_OngeldigeWaarde_IsFalse(string waarde)
        => ThemeCore.IsValidHexColor(waarde).Should().BeFalse();

    // ---------------------------------------------------------------------------------------
    // Favicon en logo
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ExtractFaviconUrl_RelLinkAanwezig_WordtAbsoluutGemaakt()
    {
        var html = """<link rel="icon" href="/assets/icon.png">""";

        ThemeCore.ExtractFaviconUrl(html, Basis).Should().Be("https://www.example.com/assets/icon.png");
    }

    [Fact]
    public void ExtractFaviconUrl_HrefVoorRel_WordtOokHerkend()
    {
        var html = """<link href="/omgekeerd.ico" rel="shortcut icon">""";

        ThemeCore.ExtractFaviconUrl(html, Basis).Should().Be("https://www.example.com/omgekeerd.ico");
    }

    [Fact]
    public void ExtractFaviconUrl_GeenLinkTag_ValtTerugOpStandaardpad()
    {
        ThemeCore.ExtractFaviconUrl("<html></html>", Basis).Should().Be("https://www.example.com/favicon.ico");
    }

    [Fact]
    public void ExtractLogoUrl_OgImage_HeeftVoorrangBovenAppleTouchIcon()
    {
        var html = """
            <link rel="apple-touch-icon" href="/touch.png">
            <meta property="og:image" content="https://www.example.com/og.png">
            """;

        ThemeCore.ExtractLogoUrl(html, Basis).Should().Be("https://www.example.com/og.png");
    }

    [Fact]
    public void ExtractLogoUrl_AlleenAppleTouchIcon_WordtGebruikt()
    {
        var html = """<link rel="apple-touch-icon" href="/touch.png">""";

        ThemeCore.ExtractLogoUrl(html, Basis).Should().Be("https://www.example.com/touch.png");
    }

    [Fact]
    public void ExtractLogoUrl_GeenEnkeleBron_GeeftNull()
        => ThemeCore.ExtractLogoUrl("<html></html>", Basis).Should().BeNull();

    // ---------------------------------------------------------------------------------------
    // ResolveUrl
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    public void ResolveUrl_NietHttpOfHttps_GeeftNull(string url)
        => ThemeCore.ResolveUrl(url, Basis).Should().BeNull();

    [Theory]
    [InlineData("/favicon.ico", "https://www.example.com/favicon.ico")]
    [InlineData("/assets/logo.png", "https://www.example.com/assets/logo.png")]
    [InlineData("//cdn.example.com/logo.png", "https://cdn.example.com/logo.png")]
    public void ResolveUrl_RootRelatievePadenLossenOpTegenDeBasis(string invoer, string verwacht)
    {
        // Regressieslot op #1252. Op Unix — en dus op het Linux Consumption Plan waar dit draait —
        // parseert Uri.TryCreate("/favicon.ico", UriKind.Absolute, out _) succesvol, als file:-URI.
        // De oorspronkelijke volgorde nam daardoor altijd de absolute tak, vond schema "file" en gaf
        // null terug: favicon en logo kwamen er in productie nooit uit, zonder foutmelding. Op
        // Windows gaf dezelfde aanroep false en werkte het wél, dus dit faalt alleen op Unix als de
        // oude volgorde terugkeert — precies waar CI draait.
        ThemeCore.ResolveUrl(invoer, Basis).Should().Be(verwacht);
    }

    [Fact]
    public void ResolveUrl_AbsoluteUrlOpAnderDomein_BlijftOngewijzigd()
        => ThemeCore.ResolveUrl("https://cdn.example.com/logo.png", Basis)
            .Should().Be("https://cdn.example.com/logo.png");

    [Fact]
    public void ResolveUrl_ProtocolRelatieveUrl_ErftHetSchemaVanDeBasis()
        => ThemeCore.ResolveUrl("//cdn.example.com/logo.png", Basis)
            .Should().Be("https://cdn.example.com/logo.png");

    // ---------------------------------------------------------------------------------------
    // HostUitWebsiteUrl — de allowlist-sleutel (#422)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void HostUitWebsiteUrl_GeldigeUrl_GeeftHostZonderSchemaOfPad()
        => ThemeCore.HostUitWebsiteUrl("https://www.example.com/pagina?x=1").Should().Be("www.example.com");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("geen-url")]
    [InlineData("/relatief/pad")]
    public void HostUitWebsiteUrl_OnbruikbareWaarde_GeeftNull(string? waarde)
        => ThemeCore.HostUitWebsiteUrl(waarde).Should().BeNull();

    // ---------------------------------------------------------------------------------------
    // ValideerUpdateAsync
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ValideerUpdateAsync_AlleKleurenLeeg_IsGeldig()
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(new ThemeUpdateRequest());

        resultaat.Status.Should().Be(ThemeValidatieStatus.Ok);
        resultaat.Foutmelding.Should().BeNull();
    }

    [Theory]
    [InlineData("Primary", "Ongeldige primary kleur.")]
    [InlineData("Secondary", "Ongeldige secondary kleur.")]
    [InlineData("Accent", "Ongeldige accent kleur.")]
    [InlineData("TextOnPrimary", "Ongeldige textOnPrimary kleur.")]
    public async Task ValideerUpdateAsync_OngeldigeKleur_WordtPerVeldGemeld(string veld, string verwachteMelding)
    {
        var dto = new ThemeUpdateRequest();
        typeof(ThemeUpdateRequest).GetProperty(veld)!.SetValue(dto, "niet-een-kleur");

        var resultaat = await ThemeCore.ValideerUpdateAsync(dto);

        resultaat.Status.Should().Be(ThemeValidatieStatus.OngeldigeKleur);
        resultaat.Foutmelding.Should().Be(verwachteMelding);
    }

    [Fact]
    public async Task ValideerUpdateAsync_OnparsebareWebsiteUrl_WordtGeweigerd()
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(new ThemeUpdateRequest { ClubWebsiteUrl = "geen-url" });

        resultaat.Status.Should().Be(ThemeValidatieStatus.OngeldigeWebsiteUrl);
    }

    [Fact]
    public async Task ValideerUpdateAsync_WebsiteUrlOpEenNietToegestanePoort_WordtGeweigerd()
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(
            new ThemeUpdateRequest { ClubWebsiteUrl = "https://www.example.com:8080/" });

        resultaat.Status.Should().Be(ThemeValidatieStatus.OngeldigeWebsiteUrl);
    }

    [Fact]
    public async Task ValideerUpdateAsync_WebsiteUrlDieNietNaarEenPubliekAdresResolvet_WordtGeweigerd()
    {
        // #1007: deze controle staat bewust al bij het opslaan. Anders kan dezelfde admin die de
        // allowlist beheert hem op een intern adres zetten.
        var resultaat = await ThemeCore.ValideerUpdateAsync(
            new ThemeUpdateRequest { ClubWebsiteUrl = "https://intern.example.com/" },
            resolverOverride: _ => Task.FromResult<System.Net.IPAddress?>(null));

        resultaat.Status.Should().Be(ThemeValidatieStatus.OngeldigeWebsiteUrl);
        resultaat.Foutmelding.Should().Contain("toegestaan publiek adres");
    }

    [Fact]
    public async Task ValideerUpdateAsync_GeldigeKleurenEnWebsiteUrl_IsGeldig()
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(
            new ThemeUpdateRequest
            {
                Primary = "#112233",
                Secondary = "#445566",
                Accent = "#778899",
                TextOnPrimary = "#ffffff",
                ClubWebsiteUrl = "https://www.example.com/"
            },
            resolverOverride: _ => Task.FromResult<System.Net.IPAddress?>(System.Net.IPAddress.Parse("203.0.113.10")));

        resultaat.Status.Should().Be(ThemeValidatieStatus.Ok);
    }

    // ---------------------------------------------------------------------------------------
    // ExtraheerAsync — de bewaakte paden vóór er ook maar iets opgehaald wordt
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ExtraheerAsync_GeenUrl_GeeftOngeldigeUrl(string? url)
    {
        var resultaat = await ThemeCore.ExtraheerAsync(url, () => Task.FromResult<string?>("www.example.com"), NullLogger.Instance);

        resultaat.Status.Should().Be(ThemeExtractieStatus.OngeldigeUrl);
    }

    [Theory]
    [InlineData("ftp://www.example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relatief/pad")]
    public async Task ExtraheerAsync_NietHttpOfHttps_GeeftOngeldigeUrl(string url)
    {
        var resultaat = await ThemeCore.ExtraheerAsync(url, () => Task.FromResult<string?>("www.example.com"), NullLogger.Instance);

        resultaat.Status.Should().Be(ThemeExtractieStatus.OngeldigeUrl);
    }

    [Fact]
    public async Task ExtraheerAsync_OngeldigeUrl_RaadpleegtDeDatabaseNiet()
    {
        // Regressieslot op de volgorde: de vorm van de URL wordt eerst gecontroleerd, zodat een
        // onbruikbare URL geen databaseaanroep kost. Dat was ook de volgorde vóór #1248.
        var aangeroepen = false;

        await ThemeCore.ExtraheerAsync("geen-url", () => { aangeroepen = true; return Task.FromResult<string?>(null); }, NullLogger.Instance);

        aangeroepen.Should().BeFalse();
    }

    [Fact]
    public async Task ExtraheerAsync_GeenClubWebsiteIngesteld_WordtGeblokkeerd()
    {
        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://www.example.com/", () => Task.FromResult<string?>(null), NullLogger.Instance);

        resultaat.Status.Should().Be(ThemeExtractieStatus.HostNietToegestaan);
    }

    [Fact]
    public async Task ExtraheerAsync_AndereHostDanDeAllowlist_WordtGeblokkeerd()
    {
        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://kwaadaardig.example.com/", () => Task.FromResult<string?>("www.example.com"), NullLogger.Instance);

        resultaat.Status.Should().Be(ThemeExtractieStatus.HostNietToegestaan);
        resultaat.Colors.Should().BeNull();
    }

    [Fact]
    public async Task ExtraheerAsync_HostVergelijkingIsHoofdletterongevoelig()
    {
        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://WWW.EXAMPLE.COM/", () => Task.FromResult<string?>("www.example.com"), NullLogger.Instance);

        // Komt voorbij de allowlist en struikelt daarna op het ophalen zelf — niet op de host.
        resultaat.Status.Should().NotBe(ThemeExtractieStatus.HostNietToegestaan);
    }

    // ---------------------------------------------------------------------------------------
    // Responscontract — beide tiers bouwen hun GET-antwoord hiermee
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void BouwResponse_HeeftDeVeldnamenVanHetBestaandeContract()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(ThemeCore.BouwResponse(ThemeCore.Standaard));

        json.Should().Contain("\"primary\"").And.Contain("\"secondary\"").And.Contain("\"accent\"")
            .And.Contain("\"textOnPrimary\"").And.Contain("\"clubWebsiteUrl\"")
            .And.Contain("\"faviconUrl\"").And.Contain("\"logoUrl\"");
    }

    [Fact]
    public void Standaard_BevatGeenClubspecifiekeWaarden()
    {
        ThemeCore.Standaard.ClubWebsiteUrl.Should().BeEmpty();
        ThemeCore.Standaard.FaviconUrl.Should().BeNull();
        ThemeCore.Standaard.LogoUrl.Should().BeNull();
        ThemeCore.Standaard.Primary.Should().Be(ThemeCore.DefaultPrimaryColor);
    }
}
