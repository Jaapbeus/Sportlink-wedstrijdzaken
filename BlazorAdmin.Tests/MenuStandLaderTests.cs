using System.Net;
using System.Text;
using AwesomeAssertions;
using BlazorAdmin.Services;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1647: extensievlag en sleutels uit menu-toegang (ook voor een niet-admin); #1599: late antwoorden na een clubwissel tellen niet.</summary>
public class MenuStandLaderTests
{
    private sealed class FakeJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    /// <summary>Beantwoordt menu-toegang-aanroepen in volgorde van binnenkomst, elk achter een eigen poort.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public int Settings, Menu;
        public bool SettingsGeweigerd = true;
        public readonly List<TaskCompletionSource> Poorten = new();
        public readonly List<string> Antwoorden = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var pad = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (pad == "api/beheer/settings")
            {
                Interlocked.Increment(ref Settings);
                return Antwoord("""{"error":"Forbidden"}""", SettingsGeweigerd ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            }
            if (pad == "api/planner/menu-toegang")
            {
                var index = Interlocked.Increment(ref Menu) - 1;
                if (index < Poorten.Count) await Poorten[index].Task;
                return Antwoord(Antwoorden[index], HttpStatusCode.OK);
            }
            return Antwoord("{}", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Antwoord(string json, HttpStatusCode status) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static async Task<(MenuStandLader Lader, ClubSelectorService Club, Handler Handler)> Maak()
    {
        var handler = new Handler();
        var transport = new AdminApiTransport(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        var club = new ClubSelectorService(new FakeJs());
        await club.SelectClubAsync("AAA", "Club A");
        var cache = new ClubInstellingenCache(new InstellingenApiClient(transport), club);
        return (new MenuStandLader(cache, club), club, handler);
    }

    private static string Json(bool extensie, params string[] sleutels) =>
        $$"""{"featureKeys":[{{string.Join(",", sleutels.Select(s => $"\"{s}\""))}}],"sportlinkExtensieActief":{{(extensie ? "true" : "false")}}}""";

    [Fact]
    public async Task NietAdmin_ExtensieAanEnToegestaneSleutel_WedstrijdAanmakenZichtbaar_ZonderInstellingenRoute()
    {
        var (lader, club, handler) = await Maak();
        handler.Antwoorden.Add(Json(true, MenuKeys.WedstrijdAanmaken, MenuKeys.Planning));

        await lader.LaadAsync();

        var zicht = new MenuZichtbaarheid(false, lader.Sleutels);
        (club.SportlinkExtensionEnabled && zicht.Mag(MenuKeys.WedstrijdAanmaken)).Should().BeTrue();
        zicht.Mag(MenuKeys.Wijzigingsverzoeken).Should().BeFalse();
        handler.Settings.Should().Be(0, "het menu mag de admin-only instellingenroute niet gebruiken");
    }

    [Fact]
    public async Task NietAdmin_ExtensieUit_WedstrijdAanmakenVerborgen()
    {
        var (lader, club, handler) = await Maak();
        handler.Antwoorden.Add(Json(false, MenuKeys.WedstrijdAanmaken));

        await lader.LaadAsync();

        var zicht = new MenuZichtbaarheid(false, lader.Sleutels);
        zicht.Mag(MenuKeys.WedstrijdAanmaken).Should().BeTrue();
        club.SportlinkExtensionEnabled.Should().BeFalse();
        (club.SportlinkExtensionEnabled && zicht.Mag(MenuKeys.WedstrijdAanmaken)).Should().BeFalse();
    }

    [Fact]
    public async Task AntwoordZonderSleutels_ToontNietsEnZetDeExtensieUit()
    {
        var (lader, club, handler) = await Maak();
        club.ZetSportlinkExtensionEnabled(true);
        handler.Antwoorden.Add("{}");

        await lader.LaadAsync();

        lader.Sleutels.Should().BeEmpty();
        club.SportlinkExtensionEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Clubwissel_LateAntwoordVanVorigeClubOverschrijftDeNieuweStandNiet()
    {
        var (lader, club, handler) = await Maak();
        var poortA = new TaskCompletionSource();
        var poortB = new TaskCompletionSource();
        handler.Poorten.AddRange([poortA, poortB]);
        handler.Antwoorden.Add(Json(false, MenuKeys.Planning));        // club A
        handler.Antwoorden.Add(Json(true, MenuKeys.Teambegeleiding));  // club B

        var ladenA = lader.LaadAsync();
        await club.SelectClubAsync("BBB", "Club B");
        var ladenB = lader.LaadAsync();

        poortB.SetResult();
        (await ladenB).Should().BeTrue();
        poortA.SetResult();
        (await ladenA).Should().BeFalse("het antwoord van club A is verouderd");

        lader.Sleutels.Should().Equal(MenuKeys.Teambegeleiding);
        club.SportlinkExtensionEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ZonderGekozenClub_DoetGeenAanroep_EnLaadtZodraDeClubBekendIs()
    {
        var handler = new Handler();
        var transport = new AdminApiTransport(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        var club = new ClubSelectorService(new FakeJs());
        var lader = new MenuStandLader(new ClubInstellingenCache(new InstellingenApiClient(transport), club), club);
        handler.Antwoorden.Add(Json(true, MenuKeys.Planning));

        (await lader.LaadAsync()).Should().BeFalse();
        handler.Menu.Should().Be(0);

        await club.SelectClubAsync("AAA", "Club A");
        (await lader.LaadAsync()).Should().BeTrue();
        handler.Menu.Should().Be(1);
    }

    [Fact]
    public async Task ZelfdeClub_TweeKeerLaden_RoeptDeServerEenKeerAan()
    {
        var (lader, _, handler) = await Maak();
        handler.Antwoorden.Add(Json(true, MenuKeys.Planning));

        await lader.LaadAsync();
        (await lader.LaadAsync()).Should().BeFalse();

        handler.Menu.Should().Be(1);
    }
}
