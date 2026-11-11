using System.Net;
using System.Reflection;
using AwesomeAssertions;
using BlazorAdmin.Pages.TestData;
using BlazorAdmin.Services;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class TestDataWedstrijdenLifecycleTests
{
    [Fact]
    public async Task PreselectedNonDemoClub_FinishesLoadingWithoutRequestsOrClubChange()
    {
        var (selector, handler, transport) = await CreateContextAsync();
        var changes = 0;
        selector.OnChange += () => changes++;
        using var page = CreatePage(selector, transport);

        await page.InitializePageAsync();

        Get<bool>(page, "_loading").Should().BeFalse();
        Get<string?>(page, "_error").Should().BeNull();
        handler.Requests.Should().Be(0);
        changes.Should().Be(0);
        selector.SelectedClubCode.Should().Be("EXAMPLE");
    }

    [Fact]
    public async Task ReturningWithSameSelectedClub_InitializesNewPageWithoutRequests()
    {
        var (selector, handler, transport) = await CreateContextAsync();
        using (var firstVisit = CreatePage(selector, transport))
        {
            await firstVisit.InitializePageAsync();
            Get<bool>(firstVisit, "_loading").Should().BeFalse();
        }
        using var secondVisit = CreatePage(selector, transport);

        await secondVisit.InitializePageAsync();

        Get<bool>(secondVisit, "_loading").Should().BeFalse();
        handler.Requests.Should().Be(0);
    }

    [Fact]
    public async Task NonDemoInitialization_UsesSameResetAsClubChange()
    {
        var (selector, handler, transport) = await CreateContextAsync();
        using var page = CreatePage(selector, transport);
        Get<List<string>>(page, "_teams").Add("ALLSTARS JO15-1");
        Set(page, "_error", "old load error");
        Set(page, "_bevestigVerwijderGefilterde", true);

        await page.InitializePageAsync();

        Get<bool>(page, "_loading").Should().BeFalse();
        Get<List<string>>(page, "_teams").Should().BeEmpty();
        Get<string?>(page, "_error").Should().BeNull();
        Get<bool>(page, "_bevestigVerwijderGefilterde").Should().BeFalse();
        handler.Requests.Should().Be(0);
    }

    private static async Task<(ClubSelectorService, FakeHandler, AdminApiTransport)> CreateContextAsync()
    {
        var selector = new ClubSelectorService(new PresetJsRuntime());
        await selector.SelectClubAsync("EXAMPLE");
        var handler = new FakeHandler();
        var transport = new AdminApiTransport(new HttpClient(handler) { BaseAddress = new Uri("https://allstars-fc.test/") });
        return (selector, handler, transport);
    }

    private static LifecyclePage CreatePage(ClubSelectorService selector, AdminApiTransport transport)
    {
        var page = new LifecyclePage(selector);
        foreach (var (property, value) in new (string, object)[]
        {
            ("InstellingenApi", new InstellingenApiClient(transport)),
            ("TeamsApi", new TeamsApiClient(transport)),
            ("VeldenApi", new VeldenApiClient(transport))
        })
            typeof(Wedstrijden).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
        return page;
    }

    private static T Get<T>(Wedstrijden page, string field) =>
        (T)typeof(Wedstrijden).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static void Set(Wedstrijden page, string field, object value) =>
        typeof(Wedstrijden).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private sealed class LifecyclePage(ClubSelectorService selector) : Wedstrijden
    {
        public async Task InitializePageAsync()
        {
            ClubSelector = selector;
            OnInitialized();
            await OnInitializedAsync();
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }

    private sealed class PresetJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new(identifier == "localStorage.getItem" ? (TValue)(object)"EXAMPLE" : default!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
