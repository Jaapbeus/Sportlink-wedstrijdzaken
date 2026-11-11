using System.Net;
using System.Reflection;
using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Pages;
using BlazorAdmin.Services;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class InstellingenUitgeslotenEmailBevestigingTests
{
    [Fact]
    public async Task DeleteRequiresConfirmationAndCancelLeavesAddressUntouched()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)
            { Content = new StringContent("") });
        var (page, selector) = await CreatePageAsync(handler);

        Invoke(page, "VraagVerwijderAdres", 12);
        handler.Requests.Should().BeEmpty();
        Invoke(page, "AnnuleerVerwijderenAdres");
        await InvokeAsync(page, "VerwijderAdresAsync", 12);

        handler.Requests.Should().BeEmpty();
        GetAddresses(page).Should().ContainSingle().Which.Id.Should().Be(12);
        selector.SelectedClubCode.Should().Be("ALLSTARS");
    }

    [Fact]
    public async Task ConfirmedDeleteReloadsOnlyOnSuccessAndFailureKeepsLoadedAddress()
    {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("{\"error\":\"synthetic failure\"}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var (page, _) = await CreatePageAsync(handler);

        Invoke(page, "VraagVerwijderAdres", 12);
        await InvokeAsync(page, "VerwijderAdresAsync", 12);

        handler.Requests.Should().ContainSingle().Which.Should().Be("DELETE api/beheer/uitgesloten-emails/12");
        GetAddresses(page).Should().ContainSingle().Which.Id.Should().Be(12);
        GetField<string?>(page, "uitgeslotenMessage").Should().Be("Fout: synthetic failure");
    }

    [Fact]
    public async Task ConfirmedDeleteCallsApiAndReloadsTheAddressList()
    {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var (page, _) = await CreatePageAsync(handler);

        Invoke(page, "VraagVerwijderAdres", 12);
        await InvokeAsync(page, "VerwijderAdresAsync", 12);

        handler.Requests.Should().BeEquivalentTo(
            "DELETE api/beheer/uitgesloten-emails/12",
            "GET api/beheer/uitgesloten-emails");
        GetAddresses(page).Should().BeEmpty();
    }

    private static async Task<(Instellingen Page, ClubSelectorService Selector)> CreatePageAsync(FakeHandler handler)
    {
        var selector = new ClubSelectorService(new FakeJsRuntime());
        await selector.SelectClubAsync("ALLSTARS");

        var page = new Instellingen();
        typeof(ClubSelectorPageBase).GetProperty("ClubSelector", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, selector);
        typeof(Instellingen).GetProperty("EmailApi", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, new EmailApiClient(new AdminApiTransport(new HttpClient(handler)
            {
                BaseAddress = new Uri("https://example.test/")
            })));
        SetField(page, "uitgeslotenEmails", new List<UitgeslotenEmailAdresDto>
        {
            new() { Id = 12, EmailAdres = "trainer@voorbeeld.nl", Actief = true, ClubCode = "ALLSTARS" }
        });
        return (page, selector);
    }

    private static void Invoke(Instellingen page, string name, params object[] args) =>
        typeof(Instellingen).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);

    private static Task InvokeAsync(Instellingen page, string name, params object[] args) =>
        (Task)typeof(Instellingen).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args)!;

    private static List<UitgeslotenEmailAdresDto> GetAddresses(Instellingen page) =>
        GetField<List<UitgeslotenEmailAdresDto>>(page, "uitgeslotenEmails");

    private static T GetField<T>(Instellingen page, string name) =>
        (T)typeof(Instellingen).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static void SetField(Instellingen page, string name, object value) =>
        typeof(Instellingen).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}");
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            new(default(TValue)!);
    }
}
