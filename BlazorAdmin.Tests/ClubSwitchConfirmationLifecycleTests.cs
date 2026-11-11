using System.Net;
using System.Reflection;
using BlazorAdmin.Pages;
using BlazorAdmin.Services;
using BlazorAdmin.Shared;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class ClubSwitchConfirmationLifecycleTests
{
    [Fact]
    public async Task SpeeltijdenClubSwitchCancelsPendingDelete()
    {
        var handler = new RecordingHandler();
        var page = new Speeltijden();
        Set(page, "Api", new TeamsApiClient(Transport(handler)));
        var confirmation = Field<InlineBevestiging<string>>(page, "_deleteConfirmation");
        confirmation.Vraag("JO13");

        await CallAsync(page, "OnClubChangedAsync");
        await CallAsync(page, "DeleteAsync", "JO13");

        Assert.False(confirmation.IsOpenFor("JO13"));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task EmailTemplateClubSwitchCancelsPendingReset()
    {
        var handler = new RecordingHandler();
        var transport = Transport(handler);
        var selector = new ClubSelectorService(new FakeJsRuntime());
        await selector.SelectClubAsync("ALLSTARS");
        var settings = new ClubInstellingenCache(new InstellingenApiClient(transport), selector);
        var page = new EmailTemplates();
        Set(page, "ClubSelector", selector);
        Set(page, "EmailApi", new EmailApiClient(transport));
        Set(page, "InstellingenCache", settings);
        var confirmation = Field<InlineBevestiging<string>>(page, "_resetConfirmation");
        confirmation.Vraag("BeschikbaarheidCheck");

        await CallAsync(page, "OnClubChangedAsync");
        await CallAsync(page, "ResetAsync", "BeschikbaarheidCheck");

        Assert.False(confirmation.IsOpenFor("BeschikbaarheidCheck"));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Post && request.Path.Contains("/reset", StringComparison.Ordinal));
        settings.Dispose();
    }

    [Fact]
    public async Task OefenwedstrijdClubSwitchCancelsPendingCreateConfirmation()
    {
        var handler = new RecordingHandler();
        var page = new WedstrijdAanmakenFormulier();
        var selector = new ClubSelectorService(new FakeJsRuntime());
        await selector.SelectClubAsync("ALLSTARS");
        Set(page, "ClubSelector", selector);
        var transport = Transport(handler);
        Set(page, "SportlinkApi", new SportlinkApiClient(transport));
        Set(page, "TeamsApi", new TeamsApiClient(transport));
        Set(page, "VeldenApi", new VeldenApiClient(transport));
        var confirmation = Field<WedstrijdAanmaakPoort>(page, "_poort");
        Assert.False(confirmation.VraagAan(Array.Empty<string>(), dryRun: false));
        Assert.Equal(WedstrijdAanmaakPoort.Stap.Bevestigen, confirmation.Huidig);

        await CallAsync(page, "OnClubChangedAsync");

        Assert.Equal(WedstrijdAanmaakPoort.Stap.Invoer, confirmation.Huidig);
        Assert.False(confirmation.Bevestig());
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    private static AdminApiTransport Transport(RecordingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static void Set(object instance, string name, object value)
    {
        var type = instance.GetType();
        var property = type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        property.SetValue(instance, value);
    }

    private static async Task CallAsync(object instance, string name, params object[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(instance, args)!;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path));
            var body = path.EndsWith("/settings", StringComparison.Ordinal) ? "{}" : "[]";
            if (path.EndsWith("/dryrun-status", StringComparison.Ordinal)) body = "{\"dryRun\":false}";
            if (path.EndsWith("/club-match/formulier", StringComparison.Ordinal))
                body = "{\"teams\":[],\"ageClasses\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new(identifier == "localStorage.getItem" ? (TValue)(object)"ALLSTARS" : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
            object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
