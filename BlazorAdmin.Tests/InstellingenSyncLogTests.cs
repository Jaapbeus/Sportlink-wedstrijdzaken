using System.Net;
using System.Reflection;
using System.Text;
using BlazorAdmin.Models;
using BlazorAdmin.Pages;
using BlazorAdmin.Services;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class InstellingenSyncLogTests
{
    [Fact]
    public async Task PartialFailure_ShowsSafeReasonAndReadableOutcome()
    {
        var page = new Instellingen();
        var job = new SyncJobDto { Status = "partial" };
        var phase = new SyncLogEntryDto { Phase = "matchdetails", Status = "partial", Failed = 1, ReasonCode = "partial_failure" };

        Call(page, "FinishSync", job);

        Assert.Equal("Synchronisatie gedeeltelijk voltooid. Controleer de fasen met fouten.", Get<string?>(page, "_syncResult"));
        Assert.Equal("Deels mislukt", Call<string>(page, "SyncStatusTekst", phase.Status));
        Assert.Contains("mislukt 1", Call<string>(page, "SyncFaseResultaat", phase));
        Assert.Contains("Een deel van de gegevens kon niet worden verwerkt.", Call<string>(page, "SyncFaseResultaat", phase));
        Assert.DoesNotContain("partial_failure", Call<string>(page, "SyncFaseResultaat", phase));
    }

    [Fact]
    public async Task Dispose_CancelsInFlightStatusPoll()
    {
        var handler = new BlockingHandler();
        var page = MaakPagina(handler, out var club, out var cache);
        await club.SelectClubAsync("AAA", "Club A");
        SetField(page, "_syncPollIntervalMilliseconds", 0);

        Call(page, "StartSyncPolling", "AAA");
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var poll = Get<Task>(page, "_syncPollTask");

        page.Dispose();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handler.RequestToken.IsCancellationRequested);
        Assert.Null(Get<CancellationTokenSource?>(page, "_syncPollCancellation"));
        cache.Dispose();
    }

    [Fact]
    public async Task ClubChange_CancelsPreviousClubPollBeforeLoadingNewClub()
    {
        var handler = new BlockingHandler();
        var page = MaakPagina(handler, out var club, out var cache);
        await club.SelectClubAsync("AAA", "Club A");
        SetField(page, "_syncPollIntervalMilliseconds", 0);

        Call(page, "StartSyncPolling", "AAA");
        var poll = Get<Task>(page, "_syncPollTask");
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await club.SelectClubAsync("BBB", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handler.RequestToken.IsCancellationRequested);
        Assert.False(Get<bool>(page, "_syncing"));
        cache.Dispose();
    }

    private static Instellingen MaakPagina(HttpMessageHandler handler, out ClubSelectorService club, out ClubInstellingenCache cache)
    {
        club = new ClubSelectorService(new FakeJs());
        var transport = new AdminApiTransport(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });
        var api = new InstellingenApiClient(transport);
        cache = new ClubInstellingenCache(api, club);
        var page = new Instellingen();
        Set(page, "ClubSelector", club);
        Set(page, "InstellingenApi", api);
        Set(page, "InstellingenCache", cache);
        Set(page, "EmailApi", new EmailApiClient(transport));
        return page;
    }

    private static void Set(object target, string name, object? value)
        => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void SetField(object target, string name, object? value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Get<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Call(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static Task CallAsync(object target, string name)
        => (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null)!;

    private static T Call<T>(object target, string name, params object?[] args)
        => (T)target.GetType().GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

    private sealed class FakeJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => new(default(TValue)!);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _requestToken;
        private int _requests;
        public TaskCompletionSource Started => _started;
        public CancellationToken RequestToken => _requestToken;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) > 1) return Response("{}");
            _requestToken = cancellationToken;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response("{}");
        }
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
