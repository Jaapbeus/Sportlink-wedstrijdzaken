using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Infrastructure;
using Planner.Shared.Theming;
using Xunit;

namespace Planner.Shared.Tests.Theming;

/// <summary>
/// Tests voor de verharding van de uitgaande thema-extractie (#1675): host-controle per hop,
/// begrensde antwoordomvang, regexen zonder backtracking en validatie van favicon-/logo-URL.
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
    public async Task ReadBoundedStringAsync_BovenLimiet_GooitSsrfBlockedException()
    {
        var act = () => SsrfProtection.ReadBoundedStringAsync(new StringContent(new string('a', 100)), maxBytes: 10);

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Fact]
    public async Task ReadBoundedStringAsync_BovenLimietZonderContentLength_GooitSsrfBlockedException()
    {
        using var stream = new MemoryStream(new byte[100]);
        var content = new StreamContent(stream);

        var act = () => SsrfProtection.ReadBoundedStringAsync(content, maxBytes: 10);

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Fact]
    public async Task ExtraheerAsync_TeGroteBody_GeeftBestemmingGeblokkeerd()
    {
        var groot = new string('a', SsrfProtection.MaxResponseBytes + 1);
        using var client = new HttpClient(new StubHandler(Ok(groot)));

        var resultaat = await ThemeCore.ExtraheerAsync(
            "https://club.example.com/", () => Task.FromResult<string?>("club.example.com"),
            NullLogger.Instance, client);

        resultaat.Status.Should().Be(ThemeExtractieStatus.BestemmingGeblokkeerd);
    }

    [Fact]
    public void CreateHttpClient_BeperktBufferGrootte()
    {
        using var client = SsrfProtection.CreateHttpClient();

        client.MaxResponseContentBufferSize.Should().Be(SsrfProtection.MaxResponseBytes);
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
        ThemeCore.ValideerAssetUrl(url, "favicon-URL").Should().BeNull();

    [Theory]
    [InlineData("http://club.example.com/f.ico")]
    [InlineData("/favicon.ico")]
    [InlineData("favicon.ico")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://club.example.com/a\nb")]
    public void ValideerAssetUrl_Ongeldig_GeeftFoutmelding(string url) =>
        ThemeCore.ValideerAssetUrl(url, "favicon-URL").Should().NotBeNull();

    [Fact]
    public void ValideerAssetUrl_TeLang_GeeftFoutmelding() =>
        ThemeCore.ValideerAssetUrl("https://club.example.com/" + new string('a', 300), "logo-URL").Should().NotBeNull();

    [Fact]
    public async Task ValideerUpdateAsync_OngeldigeFavicon_WordtGeweigerd()
    {
        var resultaat = await ThemeCore.ValideerUpdateAsync(new ThemeUpdateRequest { FaviconUrl = "http://club.example.com/f.ico" });

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
    public void Extractie_HttpAssets_WordenNietVoorgesteld()
    {
        const string html = "<link rel=\"icon\" href=\"http://club.example.com/f.png\"><meta property=\"og:image\" content=\"http://club.example.com/l.png\">";

        ThemeCore.ExtractFaviconUrl(html, new Uri("https://club.example.com/")).Should().BeNull();
        ThemeCore.ExtractLogoUrl(html, new Uri("https://club.example.com/")).Should().BeNull();
    }

    [Fact]
    public void Extractie_HttpBasisZonderIcoon_GeeftGeenHttpFavicon() =>
        ThemeCore.ExtractFaviconUrl("<html></html>", new Uri("http://club.example.com/")).Should().BeNull();
}
