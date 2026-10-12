using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Infrastructure;
using Planner.Shared.Theming;
using Xunit;

namespace Planner.Shared.Tests.Theming;

/// <summary>
/// Tests voor de verharding van de uitgaande thema-extractie (#1675): host-controle per hop,
/// begrensde antwoordomvang, charset-bewust lezen, regexen zonder backtracking, de eind-URL als
/// basis voor relatieve assets en validatie/normalisatie van favicon-/logo-URL.
/// Uitsluitend <c>example.com</c>-achtige testhosts.
/// </summary>
public class ThemeCoreSsrfVerhardingTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public StubHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Dequeue());
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
    };

    private static HttpResponseMessage Ok(string body = "ok") => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // --- (1) host-controle per hop -----------------------------------------------------------

    [Fact]
    public async Task Redirect_NaarAndereHost_WordtPerHopGeweigerd()
    {
        using var client = new HttpClient(new StubHandler(Redirect("https://ander.example.org/"), Ok()));

        var act = () => SsrfProtection.GetWithBoundedRedirectsAsync(
            client, new Uri("https://club.example.com/"),
            isAllowedHop: u => ThemeCore.IsToegestaneHop(u, "club.example.com"));

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Fact]
    public async Task Redirect_NaarDezelfdeHost_WordtGevolgd()
    {
        using var client = new HttpClient(new StubHandler(Redirect("/nieuw"), Ok()));

        using var response = await SsrfProtection.GetWithBoundedRedirectsAsync(
            client, new Uri("https://club.example.com/"),
            isAllowedHop: u => ThemeCore.IsToegestaneHop(u, "club.example.com"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Redirect_Https_NaarHttp_WordtGeweigerd()
    {
        using var client = new HttpClient(new StubHandler(Redirect("http://club.example.com/"), Ok()));

        var act = () => SsrfProtection.GetWithBoundedRedirectsAsync(client, new Uri("https://club.example.com/"));

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Fact]
    public async Task Redirect_Http_NaarHttps_WordtGevolgd()
    {
        using var client = new HttpClient(new StubHandler(Redirect("https://club.example.com/"), Ok()));

        using var response = await SsrfProtection.GetWithBoundedRedirectsAsync(client, new Uri("http://club.example.com/"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("https://www.club.example.com/", "club.example.com", true)]
    [InlineData("https://club.example.com/", "www.club.example.com", true)]
    [InlineData("https://CLUB.example.com/", "club.example.com", true)]
    [InlineData("https://ander.example.com/", "club.example.com", false)]
    [InlineData("https://club.example.com.ander.example.org/", "club.example.com", false)]
    public void IsToegestaneHop_VergelijktHostZonderWww(string hop, string toegestaan, bool verwacht) =>
        ThemeCore.IsToegestaneHop(new Uri(hop), toegestaan).Should().Be(verwacht);

    [Fact]
    public async Task ExtraheerAsync_RedirectNaarAndereHost_GeeftBestemmingGeblokkeerd()
    {
        using var client = new HttpClient(new StubHandler(Redirect("https://ander.example.org/"), Ok()));

        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client);

        resultaat.Status.Should().Be(ThemeExtractieStatus.BestemmingGeblokkeerd);
    }

    // --- (2) responsgrootte ------------------------------------------------------------------

    [Fact]
    public async Task ReadBoundedStringAsync_BinnenLimiet_GeeftTekst()
    {
        var tekst = await SsrfProtection.ReadBoundedStringAsync(new StringContent("hallo"), maxBytes: 10);

        tekst.Should().Be("hallo");
    }

    [Fact]
    public async Task ReadBoundedStringAsync_BovenLimiet_GooitResponseTooLargeException()
    {
        var act = () => SsrfProtection.ReadBoundedStringAsync(new StringContent(new string('a', 100)), maxBytes: 10);

        await act.Should().ThrowAsync<ResponseTooLargeException>();
    }

    [Fact]
    public async Task ReadBoundedStringAsync_BovenLimietZonderContentLength_GooitResponseTooLargeException()
    {
        using var stream = new MemoryStream(new byte[100]);
        var content = new StreamContent(stream);

        var act = () => SsrfProtection.ReadBoundedStringAsync(content, maxBytes: 10);

        await act.Should().ThrowAsync<ResponseTooLargeException>();
    }

    [Fact]
    public async Task ExtraheerAsync_TeGroteBody_GeeftAntwoordTeGroot()
    {
        var groot = new string('a', SsrfProtection.MaxResponseBytes + 1);
        using var client = new HttpClient(new StubHandler(Ok(groot)));

        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client);

        resultaat.Status.Should().Be(ThemeExtractieStatus.AntwoordTeGroot);
        resultaat.Foutmelding.Should().Be("De website geeft een te groot antwoord.");
    }

    // --- charset-bewust lezen ---------------------------------------------------------------

    private static ByteArrayContent Tekst(byte[] bytes, string? charset)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") { CharSet = charset };
        return content;
    }

    [Fact]
    public async Task ReadBoundedStringAsync_Iso88591_DecodeertNietAsciiTeken()
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes("<title>Caf\u00e9</title>");

        var tekst = await SsrfProtection.ReadBoundedStringAsync(Tekst(bytes, "ISO-8859-1"));

        tekst.Should().Be("<title>Caf\u00e9</title>");
    }

    [Fact]
    public async Task ReadBoundedStringAsync_Windows1252_DecodeertCodepagina()
    {
        var tekst = await SsrfProtection.ReadBoundedStringAsync(Tekst(new byte[] { 0x80 }, "windows-1252"));

        tekst.Should().Be("\u20ac");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("geen-bestaande-charset")]
    public async Task ReadBoundedStringAsync_ZonderOfOnbekendeCharset_ValtTerugOpUtf8(string? charset)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Caf\u00e9");

        var tekst = await SsrfProtection.ReadBoundedStringAsync(Tekst(bytes, charset));

        tekst.Should().Be("Caf\u00e9");
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16LE")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32LE")]
    [InlineData("utf-32BE")]
    public async Task ReadBoundedStringAsync_MetBom_GebruiktBomEnLaatHemWeg(string encodingNaam)
    {
        var encoding = encodingNaam switch
        {
            "utf-8" => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf-16LE" => new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf-16BE" => new System.Text.UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "utf-32LE" => new System.Text.UTF32Encoding(bigEndian: false, byteOrderMark: true),
            _ => (System.Text.Encoding)new System.Text.UTF32Encoding(bigEndian: true, byteOrderMark: true)
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("<title>Caf\u00e9</title>")).ToArray();

        // De header noemt bewust een andere charset: de BOM gaat voor.
        var tekst = await SsrfProtection.ReadBoundedStringAsync(Tekst(bytes, "iso-8859-1"));

        tekst.Should().Be("<title>Caf\u00e9</title>");
    }

    [Fact]
    public async Task ExtraheerAsync_Iso88591Pagina_GeeftAssetUrlMetNietAsciiTeken()
    {
        var html = "<link rel=\"icon\" href=\"/caf\u00e9.png\">";
        var antwoord = new HttpResponseMessage(HttpStatusCode.OK) { Content = Tekst(System.Text.Encoding.Latin1.GetBytes(html), "iso-8859-1") };
        using var client = new HttpClient(new StubHandler(antwoord));

        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client);

        resultaat.FaviconUrl.Should().Be("https://club.example.com/caf\u00e9.png");
    }

    // --- (3) regexen ------------------------------------------------------------------------

    [Fact]
    public void Extractie_OpPathologischeInvoer_BlijftSnel()
    {
        var html = "<link rel=\"icon\" " + new string('a', 200_000) + "<meta " + string.Concat(Enumerable.Repeat("content='", 20_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        ThemeCore.ExtractFaviconUrl(html, new Uri("https://club.example.com/"));
        ThemeCore.ExtractLogoUrl(html, new Uri("https://club.example.com/"));

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Extractie_NormaleHtml_GeeftNogSteedsFaviconEnLogo()
    {
        const string html = "<link rel=\"icon\" href=\"/f.png\"><meta property=\"og:image\" content=\"https://club.example.com/l.png\">";

        ThemeCore.ExtractFaviconUrl(html, new Uri("https://club.example.com/")).Should().Be("https://club.example.com/f.png");
        ThemeCore.ExtractLogoUrl(html, new Uri("https://club.example.com/")).Should().Be("https://club.example.com/l.png");
    }

    // --- (4) favicon-/logo-URL-validatie ----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://club.example.com/favicon.ico")]
    public void ValideerAssetUrl_Geldig_GeeftNull(string? url) =>
        ThemeAssetUrl.ValideerAssetUrl(url, "favicon-URL").Should().BeNull();

    [Theory]
    [InlineData("http://club.example.com/f.ico")]
    [InlineData("/favicon.ico")]
    [InlineData("favicon.ico")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://club.example.com/a\nb")]
    public void ValideerAssetUrl_Ongeldig_GeeftFoutmelding(string url) =>
        ThemeAssetUrl.ValideerAssetUrl(url, "favicon-URL").Should().NotBeNull();

    [Fact]
    public void ValideerAssetUrl_TeLang_GeeftFoutmelding() =>
        ThemeAssetUrl.ValideerAssetUrl("https://club.example.com/" + new string('a', 300), "logo-URL").Should().NotBeNull();

    [Theory]
    [InlineData("http://club.example.com/f.ico?v=2", "https://club.example.com/f.ico?v=2")]
    [InlineData("HTTP://club.example.com/f.ico", "https://club.example.com/f.ico")]
    [InlineData("http://club.example.com:80/f.ico", "https://club.example.com/f.ico")]
    [InlineData("https://club.example.com/f.ico", "https://club.example.com/f.ico")]
    [InlineData("/favicon.ico", "/favicon.ico")]
    [InlineData("javascript:alert(1)", "javascript:alert(1)")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void NaarHttps_ZetAlleenAbsoluteHttpUrlOm(string? invoer, string? verwacht) =>
        ThemeAssetUrl.NaarHttps(invoer).Should().Be(verwacht);

    [Fact]
    public async Task ValideerUpdateAsync_HttpFaviconEnLogo_WordenAlsHttpsOpgeslagen()
    {
        var dto = new ThemeUpdateRequest
        {
            FaviconUrl = "http://club.example.com/f.ico",
            LogoUrl = "http://club.example.com/img/logo.png?v=3"
        };

        var resultaat = await ThemeCore.ValideerUpdateAsync(dto);

        resultaat.Status.Should().Be(ThemeValidatieStatus.Ok);
        dto.FaviconUrl.Should().Be("https://club.example.com/f.ico");
        dto.LogoUrl.Should().Be("https://club.example.com/img/logo.png?v=3");
    }

    [Fact]
    public async Task ValideerUpdateAsync_HttpsAssetMetWitruimte_WordtGetrimdOpgeslagen()
    {
        var dto = new ThemeUpdateRequest { FaviconUrl = "  https://club.example.com/f.ico ", LogoUrl = " http://club.example.com/l.png\t" };

        var resultaat = await ThemeCore.ValideerUpdateAsync(dto);

        resultaat.Status.Should().Be(ThemeValidatieStatus.Ok);
        dto.FaviconUrl.Should().Be("https://club.example.com/f.ico");
        dto.LogoUrl.Should().Be("https://club.example.com/l.png");
    }

    [Theory]
    [InlineData("/img/logo.png")]
    [InlineData("data:image/png;base64,AAAA")]
    public async Task ValideerUpdateAsync_OpgeslagenOnherstelbaarLogo_GeeftBruikbareMelding(string logo)
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(new ThemeUpdateRequest { Primary = "#123456", LogoUrl = logo });

        resultaat.Status.Should().Be(ThemeValidatieStatus.OngeldigeAssetUrl);
        resultaat.Foutmelding.Should().Be(
            "De logo-URL is ongeldig of te lang (maximaal 300 tekens, https). Leeg het veld of vul een geldige https-URL in.");
    }

    [Fact]
    public async Task ValideerUpdateAsync_TeLangeFavicon_GeeftBruikbareMelding()
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(
            new ThemeUpdateRequest { FaviconUrl = "https://club.example.com/" + new string('a', 400) });

        resultaat.Foutmelding.Should().StartWith("De favicon-URL is ongeldig of te lang").And.Contain("Leeg het veld");
    }

    [Fact]
    public async Task ValideerUpdateAsync_KleurwijzigingMetOpgeslagenHttpAsset_Slaagt()
    {
        var dto = new ThemeUpdateRequest { Primary = "#123456", FaviconUrl = "http://club.example.com/f.ico" };

        var resultaat = await ThemeCore.ValideerUpdateAsync(dto);

        resultaat.Status.Should().Be(ThemeValidatieStatus.Ok);
        dto.FaviconUrl.Should().Be("https://club.example.com/f.ico");
    }

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("ftp://club.example.com/f.ico")]
    [InlineData("data:image/png;base64,AAAA")]
    public async Task ValideerUpdateAsync_OngeldigeFavicon_WordtGeweigerd(string favicon)
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(new ThemeUpdateRequest { FaviconUrl = favicon });

        resultaat.Status.Should().Be(ThemeValidatieStatus.OngeldigeAssetUrl);
    }

    [Fact]
    public async Task ValideerUpdateAsync_HttpFaviconDieNaOmzettingTeLangIs_WordtGeweigerd()
    {
        var favicon = "http://club.example.com/" + new string('a', ThemeAssetUrl.MaxAssetUrlLengte - 24);
        favicon.Length.Should().Be(ThemeAssetUrl.MaxAssetUrlLengte);

        var resultaat = await ThemeCore.ValideerUpdateAsync(new ThemeUpdateRequest { FaviconUrl = favicon });

        resultaat.Status.Should().Be(ThemeValidatieStatus.OngeldigeAssetUrl);
    }

    [Fact]
    public async Task ValideerUpdateAsync_OngeldigLogo_WordtGeweigerd()
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(new ThemeUpdateRequest { LogoUrl = "javascript:alert(1)" });

        resultaat.Status.Should().Be(ThemeValidatieStatus.OngeldigeAssetUrl);
    }

    [Fact]
    public async Task ValideerUpdateAsync_GeldigeAssets_ZijnToegestaan()
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(new ThemeUpdateRequest
        {
            FaviconUrl = "https://club.example.com/f.ico",
            LogoUrl = "https://club.example.com/l.png"
        });

        resultaat.Status.Should().Be(ThemeValidatieStatus.Ok);
    }

    [Fact]
    public void Extractie_HttpAssets_WordenAlsHttpsVoorgesteld()
    {
        const string html = "<link rel=\"icon\" href=\"http://club.example.com/f.png\"><meta property=\"og:image\" content=\"http://club.example.com/l.png\">";

        ThemeCore.ExtractFaviconUrl(html, new Uri("https://club.example.com/")).Should().Be("https://club.example.com/f.png");
        ThemeCore.ExtractLogoUrl(html, new Uri("https://club.example.com/")).Should().Be("https://club.example.com/l.png");
    }

    [Fact]
    public void Extractie_HttpBasisZonderIcoon_GeeftHttpsFavicon() =>
        ThemeCore.ExtractFaviconUrl("<html></html>", new Uri("http://club.example.com/"))
            .Should().Be("https://club.example.com/favicon.ico");

    [Fact]
    public void Extractie_NietOpslaanbaarAsset_WordtNietVoorgesteld() =>
        ThemeCore.ExtractLogoUrl("<meta property=\"og:image\" content=\"data:image/png;base64,AAAA\">", new Uri("https://club.example.com/"))
            .Should().BeNull();

    // --- eind-URL als basis voor relatieve assets ---------------------------------------------

    [Fact]
    public async Task GetWithBoundedRedirectsAndFinalUriAsync_GeeftBestemmingVanLaatsteHop()
    {
        using var client = new HttpClient(new StubHandler(Redirect("/nl/"), Ok()));

        var (response, eindUri) = await SsrfProtection.GetWithBoundedRedirectsAndFinalUriAsync(
            client, new Uri("https://club.example.com/"));
        using (response)
        {
            eindUri.Should().Be(new Uri("https://club.example.com/nl/"));
        }
    }

    [Fact]
    public async Task ExtraheerAsync_RedirectHttpsNaarHttps_LostRelatieveFaviconOpTegenEindUrl()
    {
        using var client = new HttpClient(new StubHandler(
            Redirect("https://www.club.example.com/nl/"), Ok("<link rel=\"icon\" href=\"icon.png\">")));

        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client);

        resultaat.Status.Should().Be(ThemeExtractieStatus.Ok);
        resultaat.FaviconUrl.Should().Be("https://www.club.example.com/nl/icon.png");
    }

    [Fact]
    public async Task ExtraheerAsync_MeerdereRedirects_GebruiktEindUrlVanLaatsteHop()
    {
        using var client = new HttpClient(new StubHandler(
            Redirect("https://www.club.example.com/"),
            Redirect("/nl/home/"),
            Ok("<link rel=\"icon\" href=\"../icon.png\">")));

        var resultaat = await ThemeCore.ExtraheerAsync(
            "http://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client);

        resultaat.Status.Should().Be(ThemeExtractieStatus.Ok);
        resultaat.FaviconUrl.Should().Be("https://www.club.example.com/nl/icon.png");
    }

    [Fact]
    public async Task ExtraheerAsync_RedirectHttpNaarHttps_GeeftHttpsFaviconVanEindUrl()
    {
        using var client = new HttpClient(new StubHandler(
            Redirect("https://www.club.example.com/"), Ok("<html></html>")));

        var resultaat = await ThemeCore.ExtraheerAsync(
            "http://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client);

        resultaat.Status.Should().Be(ThemeExtractieStatus.Ok);
        resultaat.FaviconUrl.Should().Be("https://www.club.example.com/favicon.ico");
    }

    // --- begrenzing over de hele extractie ----------------------------------------------------

    private sealed class TraagStream : Stream
    {
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ExtraheerAsync_TraagStromendAntwoord_EindigtBinnenTotaleTijdlimiet()
    {
        var antwoord = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TraagStream()) };
        using var client = new HttpClient(new StubHandler(antwoord));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client, totaleTijdlimiet: TimeSpan.FromMilliseconds(200));

        resultaat.Status.Should().Be(ThemeExtractieStatus.OphalenMislukt);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExtraheerAsync_SnelAntwoord_BinnenTijdlimiet_IsOk()
    {
        using var client = new HttpClient(new StubHandler(Ok("<html></html>")));

        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client, totaleTijdlimiet: TimeSpan.FromSeconds(5));

        resultaat.Status.Should().Be(ThemeExtractieStatus.Ok);
    }

    [Fact]
    public async Task ReadBoundedStringAsync_CumulatiefBudget_GeldtOverMeerdereAntwoorden()
    {
        var budget = new ResponseBudget(10);

        (await SsrfProtection.ReadBoundedStringAsync(new StringContent("123456"), budget: budget)).Should().Be("123456");
        budget.Remaining.Should().Be(4);

        var act = () => SsrfProtection.ReadBoundedStringAsync(new StringContent("123456"), budget: budget);
        await act.Should().ThrowAsync<ResponseTooLargeException>();
    }

    [Fact]
    public async Task ReadBoundedStringAsync_CumulatiefBudget_TweeAntwoordenBinnenBudget_Slagen()
    {
        var budget = new ResponseBudget(10);

        await SsrfProtection.ReadBoundedStringAsync(new StringContent("12345"), budget: budget);
        await SsrfProtection.ReadBoundedStringAsync(new StringContent("12345"), budget: budget);

        budget.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task ReadBoundedStringAsync_UitpakkendeStreamZonderContentLength_WordtOpGelezenBytesBegrensd()
    {
        var gzip = new MemoryStream();
        await using (var z = new System.IO.Compression.GZipStream(gzip, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            await z.WriteAsync(new byte[1_000_000]);
        gzip.Position = 0;
        gzip.Length.Should().BeLessThan(10_000);
        using var uitgepakt = new System.IO.Compression.GZipStream(gzip, System.IO.Compression.CompressionMode.Decompress);
        var content = new StreamContent(uitgepakt); // geen Content-Length, zoals chunked

        var act = () => SsrfProtection.ReadBoundedStringAsync(content, maxBytes: 100_000);

        await act.Should().ThrowAsync<ResponseTooLargeException>();
    }

    [Fact]
    public void CreateHandler_ZetAutomatischeDecompressieEnProxyEnRedirectsUit()
    {
        using var handler = SsrfProtection.CreateHandler();

        handler.AutomaticDecompression.Should().Be(DecompressionMethods.None);
        handler.UseProxy.Should().BeFalse();
        handler.AllowAutoRedirect.Should().BeFalse();
    }
}
