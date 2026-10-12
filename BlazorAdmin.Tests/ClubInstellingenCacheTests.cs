using System.Net;
using System.Text;
using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1599: zelfde club = 1 instellingen-aanroep; andere club of invalidatie = nieuwe aanroep. #1647: menu-toegang.</summary>
public class ClubInstellingenCacheTests
{
    private sealed class FakeJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class TelHandler : HttpMessageHandler
    {
        public int Settings, MenuToegang, Put;
        public HttpStatusCode SettingsStatus = HttpStatusCode.OK;
        public TaskCompletionSource? Poort;
        public bool ExtensieAan;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var pad = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (pad == "api/beheer/settings" && request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref Settings);
                if (Poort is not null) await Poort.Task;
                return Json($$"""{"clubName":"Club","sportlinkExtensionEnabled":{{(ExtensieAan ? "true" : "false")}}}""", SettingsStatus);
            }
            if (pad == "api/beheer/settings") { Put++; return Json("{}", HttpStatusCode.OK); }
            if (pad == "api/planner/menu-toegang") { MenuToegang++; return Json("""{"featureKeys":["menu.planning"]}""", HttpStatusCode.OK); }
            return Json("{}", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeTijd : TimeProvider
    {
        public DateTimeOffset Nu = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Nu;
    }

    private static async Task<(ClubInstellingenCache Cache, ClubSelectorService Club, TelHandler Handler, FakeTijd Tijd)> Maak(string club = "AAA")
    {
        var handler = new TelHandler();
        var transport = new AdminApiTransport(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        var clubService = new ClubSelectorService(new FakeJs());
        await clubService.SelectClubAsync(club, "Club");
        var tijd = new FakeTijd();
        return (new ClubInstellingenCache(new InstellingenApiClient(transport), clubService, tijd), clubService, handler, tijd);
    }

    [Fact]
    public async Task ZelfdeClub_TweeLezers_GevenEenAanroep()
    {
        var (cache, _, handler, _) = await Maak();

        var menu = await cache.GetSettingsAsync();
        var pagina = await cache.GetSettingsAsync();

        menu.Success.Should().BeTrue();
        pagina.Data!.ClubName.Should().Be("Club");
        handler.Settings.Should().Be(1);
    }

    [Fact]
    public async Task GelijktijdigeLezers_DelenDeLopendeAanroep()
    {
        var (cache, _, handler, _) = await Maak();
        handler.Poort = new TaskCompletionSource();

        var menu = cache.GetSettingsAsync();
        var pagina = cache.GetSettingsAsync();
        handler.Poort.SetResult();
        await Task.WhenAll(menu, pagina);

        handler.Settings.Should().Be(1);
    }

    [Fact]
    public async Task AndereClub_LaadtOpnieuw()
    {
        var (cache, club, handler, _) = await Maak();
        await cache.GetSettingsAsync();

        await club.SelectClubAsync("BBB", "Andere club");
        await cache.GetSettingsAsync();
        await cache.GetSettingsAsync();

        handler.Settings.Should().Be(2);
    }

    [Fact]
    public async Task OpslaanViaDeCache_MaaktDeStandOngeldig()
    {
        var (cache, _, handler, _) = await Maak();
        await cache.GetSettingsAsync();

        var opslaan = await cache.UpdateSettingsAsync(new SettingsUpdateDto());
        handler.ExtensieAan = true;
        var daarna = await cache.GetSettingsAsync();

        opslaan.Success.Should().BeTrue();
        handler.Put.Should().Be(1);
        handler.Settings.Should().Be(2);
        daarna.Data!.SportlinkExtensionEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Wis_MaaktDeStandOngeldig()
    {
        var (cache, _, handler, _) = await Maak();
        await cache.GetSettingsAsync();

        cache.Wis();
        await cache.GetSettingsAsync();

        handler.Settings.Should().Be(2);
    }

    [Fact]
    public async Task MislukteAanroep_WordtNietBewaard()
    {
        var (cache, _, handler, _) = await Maak();
        handler.SettingsStatus = HttpStatusCode.InternalServerError;

        var fout = await cache.GetSettingsAsync();
        handler.SettingsStatus = HttpStatusCode.OK;
        var goed = await cache.GetSettingsAsync();

        fout.Success.Should().BeFalse();
        goed.Success.Should().BeTrue();
        handler.Settings.Should().Be(2);
    }

    [Fact]
    public async Task Antwoord_IsEenKopie_ZodatEenPaginaDeGedeeldeStandNietVervuilt()
    {
        var (cache, _, _, _) = await Maak();

        var eerste = await cache.GetSettingsAsync();
        eerste.Data!.ClubName = "Gewijzigd in een formulier";
        var tweede = await cache.GetSettingsAsync();

        tweede.Data!.ClubName.Should().Be("Club");
    }

    [Fact]
    public async Task VerouderdeStand_WordtNaMaxLeeftijdOpnieuwOpgehaald()
    {
        var (cache, _, handler, tijd) = await Maak();
        await cache.GetSettingsAsync();

        tijd.Nu += ClubInstellingenCache.MaxLeeftijd + TimeSpan.FromSeconds(1);
        await cache.GetSettingsAsync();

        handler.Settings.Should().Be(2);
    }

    [Fact]
    public async Task VersLezen_DeeltEenNetOpgehaaldAntwoord_MaarVerverstDaarna()
    {
        var (cache, _, handler, tijd) = await Maak();
        await cache.GetSettingsAsync();            // menu
        await cache.GetSettingsAsync(vers: true);  // pagina direct erna: gedeeld

        tijd.Nu += ClubInstellingenCache.VersVenster + TimeSpan.FromSeconds(1);
        await cache.GetSettingsAsync(vers: true);  // later: echt vers

        handler.Settings.Should().Be(2);
    }

    [Fact]
    public async Task MenuToegang_WordtPerClubEenKeerOpgehaald()
    {
        var (cache, club, handler, _) = await Maak();

        var a = await cache.GetMenuToegangAsync();
        await cache.GetMenuToegangAsync();
        await club.SelectClubAsync("BBB", "Andere club");
        await cache.GetMenuToegangAsync();

        a.Data!.FeatureKeys.Should().Equal("menu.planning");
        handler.MenuToegang.Should().Be(2);
    }
}
