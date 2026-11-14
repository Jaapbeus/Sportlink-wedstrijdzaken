using System.Net;
using System.Reflection;
using System.Text;
using BlazorAdmin.Models;
using BlazorAdmin.Pages;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class InstellingenSyncLogTests
{
    [Fact]
    public void PartialFailure_ShowsSafeReasonAndReadableOutcome()
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
    public void PhasePosition_UsesSevenRecordedPipelinePhasesAndFallsBackForGaps()
    {
        var log = new SyncLogboek();
        var tijd = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        var job = new SyncJobDto { Status = "pending" };
        Assert.Equal("Fase 1 van 7 volgt na het ophalen uit de wachtrij.", Call<string>(log, "FaseVoortgang", job, true));

        job.Status = "running";
        job.Phases =
        [
            new() { Phase = "teams", Status = "succeeded", StartedAt = tijd },
            new() { Phase = "programma", Status = "running", StartedAt = tijd.AddSeconds(1) }
        ];
        Assert.Equal("Fase 2 van 7: Programma · 1 fase afgerond.", Call<string>(log, "FaseVoortgang", job, true));

        job.Phases =
        [
            new() { Phase = "configuratie", Status = "succeeded", StartedAt = tijd },
            new() { Phase = "teams", Status = "succeeded", StartedAt = tijd.AddSeconds(1) },
            new() { Phase = "programma", Status = "running", StartedAt = tijd.AddSeconds(2) }
        ];
        Assert.Equal("Fase 2 van 7: Programma · 1 fase afgerond.", Call<string>(log, "FaseVoortgang", job, true));

        job.Phases[2].Status = "succeeded";
        job.Phases.AddRange(
        [
            new() { Phase = "uitslagen", Status = "succeeded", StartedAt = tijd.AddSeconds(2) },
            new() { Phase = "matchdetails", Status = "succeeded", StartedAt = tijd.AddSeconds(3) },
            new() { Phase = "reconciliatie", Status = "succeeded", StartedAt = tijd.AddSeconds(4) },
            new() { Phase = "canonicalisatie", Status = "succeeded", StartedAt = tijd.AddSeconds(5) },
            new() { Phase = "planner-view", Status = "skipped", StartedAt = tijd.AddSeconds(6) }
        ]);
        Assert.Equal("Fase 7 van 7: Plannerweergave · 7 fasen afgerond.", Call<string>(log, "FaseVoortgang", job, true));

        job.Phases =
        [
            new() { Phase = "teams", Status = "succeeded", StartedAt = tijd },
            new() { Phase = "matchdetails", Status = "running", StartedAt = tijd.AddSeconds(1) }
        ];
        Assert.Equal("Fasepositie onbekend; 1 fase afgerond.", Call<string>(log, "FaseVoortgang", job, true));

        job.Phases = [new() { Phase = "configuratie", Status = "failed", StartedAt = tijd }];
        Assert.Equal("Configuratie mislukt; geen pipelinefase gestart.", Call<string>(log, "FaseVoortgang", job, true));
        job.Phases[0].Status = "succeeded";
        Assert.Equal("Nog geen pipelinefase geregistreerd (0 van 7).", Call<string>(log, "FaseVoortgang", job, true));
        Assert.Equal("Fasepositie tijdelijk niet beschikbaar.", Call<string>(log, "FaseVoortgang", job, false));
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
    public async Task Poll_TransientFailuresResetAfterSuccessAndReachCompletedJob()
    {
        var handler = new PollSequenceHandler("fail", "running", "fail", "fail", "completed");
        var page = MaakPagina(handler, out var club, out var cache);
        using var renderer = new TestRenderer();
        renderer.Attach(page);
        await club.SelectClubAsync("AAA", "Club A");
        SetField(page, "_syncPollIntervalMilliseconds", 0);
        SetField(page, "_syncing", true);

        Call(page, "StartSyncPolling", "AAA");
        await Get<Task>(page, "_syncPollTask").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, handler.StatusRequests);
        Assert.False(Get<bool>(page, "_syncStatusUnavailable"));
        Assert.False(Get<bool>(page, "_syncing"));
        Assert.Equal("Synchronisatie voltooid.", Get<string?>(page, "_syncResult"));
        cache.Dispose();
    }

    [Fact]
    public async Task Poll_ThreeFailuresStopAndPreserveLastKnownActiveJob()
    {
        var handler = new PollSequenceHandler("fail", "fail", "fail");
        var page = MaakPagina(handler, out var club, out var cache);
        using var renderer = new TestRenderer();
        renderer.Attach(page);
        await club.SelectClubAsync("AAA", "Club A");
        SetField(page, "_syncPollIntervalMilliseconds", 0);
        var snapshot = new SyncStatusDto { ActiveJob = new SyncJobDto { Status = "running" } };
        SetField(page, "_status", snapshot);
        SetField(page, "_syncing", true);

        Call(page, "StartSyncPolling", "AAA");
        await Get<Task>(page, "_syncPollTask").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, handler.StatusRequests);
        Assert.Same(snapshot, Get<SyncStatusDto>(page, "_status"));
        Assert.True(Get<bool>(page, "_syncStatusUnavailable"));
        Assert.False(Get<bool>(page, "_syncing"));
        Assert.Null(Get<CancellationTokenSource?>(page, "_syncPollCancellation"));
        Assert.Equal("Synchronisatiestatus tijdelijk niet beschikbaar. De laatst bekende stand staat hieronder; probeer opnieuw.",
            Get<string?>(page, "_syncResult"));
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

    [Fact]
    public async Task InitialLoad_FindsActiveJobAndLateOldClubResponseCannotReplaceNewClubStatus()
    {
        var handler = new LatePollHandler();
        var page = MaakPagina(handler, out var club, out var cache);
        await club.SelectClubAsync("AAA", "Club A");

        await CallAsync(page, "LoadAsync");
        Assert.True(Get<bool>(page, "_syncing"));
        Assert.Equal("health-A", Get<SyncStatusDto>(page, "_status")!.Status);
        await handler.LatePollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldPoll = Get<Task>(page, "_syncPollTask");

        await club.SelectClubAsync("BBB", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        Assert.Equal("health-B", Get<SyncStatusDto>(page, "_status")!.Status);
        Assert.False(Get<bool>(page, "_syncing"));

        handler.ReleaseLatePoll();
        await oldPoll.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("health-B", Get<SyncStatusDto>(page, "_status")!.Status);
        Assert.True(handler.LatePollToken.IsCancellationRequested);
        Assert.False(Get<bool>(page, "_syncing"));
        cache.Dispose();
    }

    [Fact]
    public async Task Dispose_WhileTriggerPending_DoesNotLoadStatusOrStartPolling()
    {
        var handler = new LateTriggerHandler();
        var page = MaakPagina(handler, out var club, out var cache);
        await club.SelectClubAsync("AAA", "Club A");

        var trigger = CallAsync(page, "StartSyncAsync", (object?)null);
        await handler.TriggerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.Dispose();
        handler.ReleaseTrigger();
        await trigger.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, handler.StatusRequests);
        Assert.Null(Get<Task?>(page, "_syncPollTask"));
        Assert.Null(Get<SyncStatusDto?>(page, "_status"));
        Assert.False(Get<bool>(page, "_syncing"));
        cache.Dispose();
    }

    [Fact]
    public async Task ClubChange_AtoBtoA_RejectsOldTriggerCompletion()
    {
        var handler = new LateTriggerHandler();
        var page = MaakPagina(handler, out var club, out var cache);
        await club.SelectClubAsync("AAA", "Club A");

        var oldTrigger = CallAsync(page, "StartSyncAsync", (object?)null);
        await handler.TriggerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await club.SelectClubAsync("BBB", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        await club.SelectClubAsync("AAA", "Club A");
        await CallAsync(page, "OnClubChangedAsync");
        Assert.Equal("new-A", Get<SyncStatusDto>(page, "_status")!.Status);
        var statusRequests = handler.StatusRequests;

        handler.ReleaseTrigger();
        await oldTrigger.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(statusRequests, handler.StatusRequests);
        Assert.Equal("new-A", Get<SyncStatusDto>(page, "_status")!.Status);
        Assert.Null(Get<Task?>(page, "_syncPollTask"));
        Assert.False(Get<bool>(page, "_syncing"));
        cache.Dispose();
    }

    [Fact]
    public async Task Dispose_WhileInitialStatusPending_DoesNotApplyLateLoad()
    {
        var handler = new LateInitialStatusHandler();
        var page = MaakPagina(handler, out var club, out var cache);
        await club.SelectClubAsync("AAA", "Club A");

        var load = CallAsync(page, "LoadAsync");
        await handler.StatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.Dispose();
        handler.ReleaseStatus();
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(Get<SyncStatusDto?>(page, "_status"));
        Assert.Null(Get<Task?>(page, "_syncPollTask"));
        cache.Dispose();
    }

    private static Instellingen MaakPagina(HttpMessageHandler handler, out ClubSelectorService club, out ClubInstellingenCache cache)
    {
        club = new ClubSelectorService(new FakeJs());
        var transport = new AdminApiTransport(new HttpClient(handler) { BaseAddress = new Uri("https://allstars-fc.test/") });
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

    private static Task CallAsync(object target, string name, params object?[] args)
        => (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

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

    private sealed class PollSequenceHandler(params string[] steps) : HttpMessageHandler
    {
        private int _statusRequests;
        public int StatusRequests => _statusRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/sync/status", StringComparison.Ordinal))
                return Task.FromResult(Response("{}"));

            var index = Interlocked.Increment(ref _statusRequests) - 1;
            var step = index < steps.Length ? steps[index] : "fail";
            var response = step switch
            {
                "running" => Response("""
                    {"status":"ok","activeJob":{"id":"11111111-1111-1111-1111-111111111111","status":"running","createdAt":"2026-11-14T10:00:00Z","attempt":1,"phases":[]},"jobs":[]}
                    """),
                "completed" => Response("""
                    {"status":"ok","activeJob":null,"lastCompletedJob":{"id":"11111111-1111-1111-1111-111111111111","status":"succeeded","createdAt":"2026-11-14T10:00:00Z","completedAt":"2026-11-14T10:05:00Z","attempt":1,"phases":[]},"jobs":[]}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class TestRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = new InlineDispatcher();
        public void Attach(IComponent component) => AssignRootComponentId(component);
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    private sealed class InlineDispatcher : Dispatcher
    {
        public override bool CheckAccess() => true;
        public override Task InvokeAsync(Action workItem) { workItem(); return Task.CompletedTask; }
        public override Task InvokeAsync(Func<Task> workItem) => workItem();
        public override Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem) => Task.FromResult(workItem());
        public override Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem) => workItem();
    }

    private sealed class LatePollHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _latePollStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLatePoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _syncRequests;
        private CancellationToken _latePollToken;
        public TaskCompletionSource LatePollStarted => _latePollStarted;
        public CancellationToken LatePollToken => _latePollToken;
        public void ReleaseLatePoll() => _releaseLatePoll.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sync/status", StringComparison.Ordinal))
            {
                var call = Interlocked.Increment(ref _syncRequests);
                if (call == 2)
                {
                    _latePollToken = cancellationToken;
                    _latePollStarted.TrySetResult();
                    await _releaseLatePoll.Task;
                    return Response("""
                        {"status":"health-A-late","activeJob":{"id":"11111111-1111-1111-1111-111111111111","status":"running","createdAt":"2026-11-14T10:00:00Z","attempt":1,"phases":[]},"jobs":[],"logAvailable":true}
                        """);
                }

                var status = call == 1
                    ? """
                      {"status":"health-A","activeJob":{"id":"11111111-1111-1111-1111-111111111111","status":"running","createdAt":"2026-11-14T10:00:00Z","attempt":1,"phases":[]},"jobs":[],"logAvailable":true}
                      """
                    : """
                      {"status":"health-B","activeJob":null,"jobs":[],"logAvailable":false}
                      """;
                return Response(status);
            }

            if (path.EndsWith("/uitgesloten-emails", StringComparison.Ordinal)) return Response("[]");
            if (path.EndsWith("/email-log", StringComparison.Ordinal)) return Response("{\"statusCounts\":{},\"items\":[]}");
            if (path.EndsWith("/settings", StringComparison.Ordinal)) return Response("{\"clubCode\":\"test\"}");
            return Response("{}");
        }
    }

    private sealed class LateTriggerHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _triggerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _statusRequests;
        public TaskCompletionSource TriggerStarted => _triggerStarted;
        public int StatusRequests => _statusRequests;
        public void ReleaseTrigger() => _releaseTrigger.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sync/trigger", StringComparison.Ordinal))
            {
                _triggerStarted.TrySetResult();
                await _releaseTrigger.Task;
                return Response("{\"jobId\":\"11111111-1111-1111-1111-111111111111\",\"tijdstip\":\"2026-11-14T10:00:00Z\"}");
            }

            if (path.EndsWith("/sync/status", StringComparison.Ordinal))
                return Response($"{{\"status\":\"{(Interlocked.Increment(ref _statusRequests) == 1 ? "view-B" : "new-A")}\",\"jobs\":[]}}");
            if (path.EndsWith("/uitgesloten-emails", StringComparison.Ordinal)) return Response("[]");
            if (path.EndsWith("/email-log", StringComparison.Ordinal)) return Response("{\"statusCounts\":{},\"items\":[]}");
            if (path.EndsWith("/settings", StringComparison.Ordinal)) return Response("{\"clubCode\":\"ALLSTARS\"}");
            return Response("{}");
        }
    }

    private sealed class LateInitialStatusHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _statusStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StatusStarted => _statusStarted;
        public void ReleaseStatus() => _releaseStatus.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sync/status", StringComparison.Ordinal))
            {
                _statusStarted.TrySetResult();
                await _releaseStatus.Task;
                return Response("{\"status\":\"stale\",\"jobs\":[]}");
            }
            if (path.EndsWith("/uitgesloten-emails", StringComparison.Ordinal)) return Response("[]");
            if (path.EndsWith("/email-log", StringComparison.Ordinal)) return Response("{\"statusCounts\":{},\"items\":[]}");
            if (path.EndsWith("/settings", StringComparison.Ordinal)) return Response("{\"clubCode\":\"ALLSTARS\"}");
            return Response("{}");
        }
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
