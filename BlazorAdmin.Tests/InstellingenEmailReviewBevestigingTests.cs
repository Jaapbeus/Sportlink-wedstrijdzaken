using System.Net;
using System.Reflection;
using System.Text.Json;
using BlazorAdmin.Models;
using BlazorAdmin.Pages;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class InstellingenEmailReviewBevestigingTests
{
    [Fact]
    public async Task FailedSaveRequiresFreshConfirmationBeforeAnotherReviewOffPut()
    {
        var handler = new SettingsHandler();
        var club = new ClubSelectorService(new FakeJsRuntime());
        using var cache = new ClubInstellingenCache(
            new InstellingenApiClient(new AdminApiTransport(new HttpClient(handler)
            { BaseAddress = new Uri("https://allstars-fc.test/") })), club);
        var page = new Instellingen();
        Set(page, "InstellingenCache", cache);
        var settings = new AppSettingsDto
        {
            EmailReviewModusActief = false,
            EmailReviewOntvanger = "trainer@voorbeeld.nl"
        };
        SetField(page, "settings", settings);

        await InvokeAsync(page, "OpslaanAsync");
        Assert.Empty(handler.Requests);
        Assert.True(GetField<bool>(page, "_toonEmailReviewBevestiging"));

        await InvokeAsync(page, "BevestigEmailReviewUitzettenAsync");
        Assert.Single(handler.Requests);
        Assert.True(handler.Requests[0].Confirmation);
        Assert.Equal("0", handler.Requests[0].ReviewMode);
        Assert.NotNull(GetField<string?>(page, "saveErrorMessage"));
        Assert.Equal("trainer@voorbeeld.nl", settings.EmailReviewOntvanger);

        settings.EmailReviewModusActief = true;
        settings.EmailReviewModusActief = false;
        await InvokeAsync(page, "OpslaanAsync");
        Assert.Single(handler.Requests);
        Assert.True(GetField<bool>(page, "_toonEmailReviewBevestiging"));

        await InvokeAsync(page, "BevestigEmailReviewUitzettenAsync");
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(handler.Requests[1].Confirmation);
        Assert.Null(GetField<string?>(page, "saveErrorMessage"));
        Assert.False(GetField<bool>(page, "_emailReviewBevestigd"));
    }

    private static void Set(Instellingen page, string property, object value) =>
        typeof(Instellingen).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, value);

    private static void SetField(Instellingen page, string field, object value) =>
        typeof(Instellingen).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, value);

    private static T GetField<T>(Instellingen page, string field) =>
        (T)typeof(Instellingen).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!;

    private static Task InvokeAsync(Instellingen page, string method) =>
        (Task)typeof(Instellingen).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, null)!;

    private sealed class SettingsHandler : HttpMessageHandler
    {
        public List<(bool Confirmation, string? ReviewMode)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/beheer/settings", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var confirmation = body.RootElement.GetProperty("bevestigReviewUitzetten").GetBoolean();
            var reviewMode = body.RootElement.GetProperty("velden").GetProperty("EmailReviewModusActief").GetString();
            Requests.Add((confirmation, reviewMode));
            return Requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    { Content = new StringContent("{\"error\":\"synthetic failure\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class FakeJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            CancellationToken cancellationToken, object?[]? args) => new(default(TValue)!);
    }
}
