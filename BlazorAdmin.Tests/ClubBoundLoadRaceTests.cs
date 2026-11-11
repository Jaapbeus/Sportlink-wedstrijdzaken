using System.Net;
using System.Reflection;
using System.Text.Json;
using BlazorAdmin.Models;
using BlazorAdmin.Pages;
using BlazorAdmin.Services;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class ClubBoundLoadRaceTests
{
    [Fact]
    public async Task EmailTemplates_ClubBLoadWinsOverLateClubAResponses_IncludingFootnoteAndLoadingState()
    {
        var (club, handler, transport) = await CreateContextAsync(failClubASettingsAndTemplates: true);
        var settingsCache = new ClubInstellingenCache(new InstellingenApiClient(transport), club);
        var page = new EmailTemplates();
        Set(page, "ClubSelector", club);
        Set(page, "EmailApi", new EmailApiClient(transport));
        Set(page, "InstellingenCache", settingsCache);

        var loadA = CallAsync(page, "LoadAsync");
        Assert.Equal(2, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");

        Assert.Equal("B", Get<List<TemplateDto>>(page, "templates").Single().ClubCode);
        Assert.Equal("foot-B", Get<string>(page, "voetnoot"));
        Assert.Null(Get<string?>(page, "templatesLoadError"));
        Assert.Null(Get<string?>(page, "voetnootLoadError"));
        Assert.False(Get<bool>(page, "loading"));

        handler.ReleaseClubA();
        await loadA;

        Assert.Equal("B", Get<List<TemplateDto>>(page, "templates").Single().ClubCode);
        Assert.Equal("foot-B", Get<string>(page, "voetnoot"));
        Assert.Null(Get<string?>(page, "templatesLoadError"));
        Assert.Null(Get<string?>(page, "voetnootLoadError"));
        Assert.False(Get<bool>(page, "loading"));
        Assert.Equal(new[] { "A", "B" }, handler.Requests.Where(r => r.Path.EndsWith("/templates", StringComparison.Ordinal))
            .Select(r => r.ClubCode).OrderBy(c => c).ToArray());
        settingsCache.Dispose();
    }

    [Fact]
    public async Task Instellingen_ClubBLoadWinsOverLateClubAResponsesAcrossAllDisplayedData()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var settingsCache = new ClubInstellingenCache(new InstellingenApiClient(transport), club);
        var page = new Instellingen();
        Set(page, "ClubSelector", club);
        Set(page, "EmailApi", new EmailApiClient(transport));
        Set(page, "InstellingenApi", new InstellingenApiClient(transport));
        Set(page, "InstellingenCache", settingsCache);

        var loadA = CallAsync(page, "LoadAsync");
        Assert.Equal(4, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");

        AssertInstellingenBelongsTo(page, "B");
        handler.ReleaseClubA();
        await loadA;
        AssertInstellingenBelongsTo(page, "B");
        settingsCache.Dispose();
    }

    [Fact]
    public async Task Instellingen_TransitionToAllstarsDiscardsPendingRealClubLoadAndClearsClubData()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var settingsCache = new ClubInstellingenCache(new InstellingenApiClient(transport), club);
        var page = new Instellingen();
        Set(page, "ClubSelector", club);
        Set(page, "EmailApi", new EmailApiClient(transport));
        Set(page, "InstellingenApi", new InstellingenApiClient(transport));
        Set(page, "InstellingenCache", settingsCache);

        var loadA = CallAsync(page, "LoadAsync");
        Assert.Equal(4, handler.PendingCount);
        await club.SelectClubAsync("ALLSTARS", "AllStars FC");
        await CallAsync(page, "OnClubChangedAsync");

        Assert.Null(Get<AppSettingsDto?>(page, "settings"));
        Assert.Null(Get<SyncStatusDto?>(page, "_status"));
        Assert.Null(Get<EmailLogResponse?>(page, "_emailLog"));
        Assert.Null(Get<EmailLogResponse?>(page, "_reviewLog"));
        Assert.Empty(Get<List<UitgeslotenEmailAdresDto>>(page, "uitgeslotenEmails"));
        Assert.False(Get<bool>(page, "_syncLoading"));

        handler.ReleaseClubA();
        await loadA;

        Assert.Null(Get<AppSettingsDto?>(page, "settings"));
        Assert.Null(Get<SyncStatusDto?>(page, "_status"));
        Assert.Null(Get<EmailLogResponse?>(page, "_emailLog"));
        Assert.Null(Get<EmailLogResponse?>(page, "_reviewLog"));
        Assert.Empty(Get<List<UitgeslotenEmailAdresDto>>(page, "uitgeslotenEmails"));
        Assert.False(Get<bool>(page, "_syncLoading"));
        settingsCache.Dispose();
    }

    [Fact]
    public async Task Instellingen_ExcludedEmailsLateClubAResponseCannotReplaceClubB()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Instellingen();
        Set(page, "ClubSelector", club);
        Set(page, "EmailApi", new EmailApiClient(transport));

        var loadA = CallAsync(page, "LaadUitgeslotenEmailsAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "LaadUitgeslotenEmailsAsync");
        Assert.Equal("B", Get<List<UitgeslotenEmailAdresDto>>(page, "uitgeslotenEmails").Single().ClubCode);

        handler.ReleaseClubA();
        await loadA;
        Assert.Equal("B", Get<List<UitgeslotenEmailAdresDto>>(page, "uitgeslotenEmails").Single().ClubCode);
        Assert.Equal(new[] { "A", "B" }, handler.Requests.Where(r => r.Path.EndsWith("/uitgesloten-emails", StringComparison.Ordinal))
            .Select(r => r.ClubCode).OrderBy(c => c).ToArray());
    }

    [Fact]
    public async Task Velden_LateClubAResponseCannotReplaceAnyClubBListOrLoadingState()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Velden();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new VeldenApiClient(transport));

        var loadA = CallAsync(page, "LoadAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        AssertVeldenBelongTo(page, "B");

        handler.ReleaseClubA();
        await loadA;
        AssertVeldenBelongTo(page, "B");
        Assert.Equal(new[] { "A", "B" }, handler.Requests.Where(r => r.Path.EndsWith("/velden", StringComparison.Ordinal))
            .Select(r => r.ClubCode).OrderBy(c => c).ToArray());
    }

    [Fact]
    public async Task Voorkeurstijden_LateClubAResponseCannotReplaceAnyClubBListOrLoadingState()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Voorkeurstijden();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new TeamsApiClient(transport));

        var loadA = CallAsync(page, "LoadAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        AssertVoorkeurstijdenBelongTo(page, "B");

        handler.ReleaseClubA();
        await loadA;
        AssertVoorkeurstijdenBelongTo(page, "B");
        Assert.Equal(new[] { "A", "B" }, handler.Requests.Where(r => r.Path.EndsWith("/teams", StringComparison.Ordinal))
            .Select(r => r.ClubCode).OrderBy(c => c).ToArray());
    }

    [Theory]
    [InlineData(typeof(Planning))]
    [InlineData(typeof(VeldOptimalisatie))]
    public async Task PdfExportStatus_LateClubAResponseCannotReplaceClubBFlag(Type pageType)
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = Activator.CreateInstance(pageType)!;
        Set(page, "ClubSelector", club);
        Set(page, "PlanningApi", new PlanningApiClient(transport));

        var loadA = CallAsync(page, "LaadPdfExportStatusAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "LaadPdfExportStatusAsync");
        Assert.True(Get<bool>(page, "_pdfExportIngeschakeld"));

        handler.ReleaseClubA();
        await loadA;
        Assert.True(Get<bool>(page, "_pdfExportIngeschakeld"));
        Assert.Equal(new[] { "A", "B" }, handler.Requests.Where(r => r.Path.EndsWith("/pdf-export", StringComparison.Ordinal))
            .Select(r => r.ClubCode).OrderBy(c => c).ToArray());
    }

    private static async Task<(ClubSelectorService Club, DeferredResponseHandler Handler, AdminApiTransport Transport)> CreateContextAsync(
        bool failClubASettingsAndTemplates = false)
    {
        var club = new ClubSelectorService(new FakeJsRuntime());
        await club.SelectClubAsync("A", "Club A");
        var handler = new DeferredResponseHandler(failClubASettingsAndTemplates);
        var clubHandler = new ClubCodeHeaderHandler(club) { InnerHandler = handler };
        var transport = new AdminApiTransport(new HttpClient(clubHandler) { BaseAddress = new Uri("https://example.test/") });
        return (club, handler, transport);
    }

    private static void AssertInstellingenBelongsTo(Instellingen page, string clubCode)
    {
        Assert.Equal(clubCode, Get<AppSettingsDto>(page, "settings")!.ClubCode);
        Assert.Equal($"sync-{clubCode}", Get<SyncStatusDto>(page, "_status")!.Status);
        Assert.Equal(1, Get<EmailLogResponse>(page, "_emailLog")!.StatusCounts![clubCode]);
        Assert.Equal(2, Get<EmailLogResponse>(page, "_reviewLog")!.StatusCounts!["Review"]);
        Assert.Equal(clubCode, Get<List<UitgeslotenEmailAdresDto>>(page, "uitgeslotenEmails").Single().ClubCode);
        Assert.False(Get<bool>(page, "_syncLoading"));
    }

    private static void AssertVeldenBelongTo(Velden page, string clubCode)
    {
        Assert.Equal(clubCode, Get<List<VeldDto>>(page, "velden").Single().VeldNaam);
        Assert.Equal(clubCode, Get<List<VeldPeriodeDto>>(page, "periodes").Single().Naam);
        Assert.Equal(clubCode, Get<List<VeldBeschikbaarheidDto>>(page, "beschikbaarheid").Single().VeldNaam);
        Assert.Equal(clubCode, Get<List<VeldTrainingDto>>(page, "training").Single().VeldNaam);
        Assert.False(Get<bool>(page, "loading"));
    }

    private static void AssertVoorkeurstijdenBelongTo(Voorkeurstijden page, string clubCode)
    {
        Assert.Equal($"Team {clubCode}", Get<List<string>>(page, "teams").Single());
        Assert.Equal($"Team {clubCode}", Get<List<VoorkeurTijdDto>>(page, "items").Single().TeamNaam);
        Assert.Equal(clubCode, Get<List<TeamRegelDto>>(page, "teamRegels").Single().ClubCode);
        Assert.False(Get<bool>(page, "loading"));
    }

    private static T Get<T>(object instance, string field) =>
        (T)instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static void Set(object instance, string property, object value) =>
        instance.GetType().GetProperty(property, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static Task CallAsync(object instance, string method) =>
        (Task)instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null)!;

    private sealed class DeferredResponseHandler(bool failClubASettingsAndTemplates) : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly List<PendingResponse> _pending = [];

        public List<(string ClubCode, string Path)> Requests { get; } = [];
        public int PendingCount { get { lock (_sync) return _pending.Count; } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clubCode = request.Headers.GetValues("X-Club-Code").Single();
            var path = request.RequestUri!.AbsolutePath;
            lock (_sync) Requests.Add((clubCode, path));

            if (clubCode == "A")
            {
                var pending = new PendingResponse(clubCode, path);
                lock (_sync) _pending.Add(pending);
                return pending.Completion.Task;
            }

            return Task.FromResult(CreateResponse(clubCode, path, request.RequestUri.Query));
        }

        public void ReleaseClubA()
        {
            PendingResponse[] pending;
            lock (_sync)
            {
                pending = _pending.Where(p => p.ClubCode == "A").ToArray();
                _pending.RemoveAll(p => p.ClubCode == "A");
            }
            foreach (var item in pending)
                item.Completion.TrySetResult(CreateResponse(item.ClubCode, item.Path, string.Empty));
        }

        private HttpResponseMessage CreateResponse(string clubCode, string path, string query = "")
        {
            if (failClubASettingsAndTemplates && clubCode == "A"
                && (path.EndsWith("/templates", StringComparison.Ordinal) || path.EndsWith("/settings", StringComparison.Ordinal)))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };

            object payload = path switch
            {
                var p when p.EndsWith("/templates", StringComparison.Ordinal) => new[] { new TemplateDto { TemplateKey = clubCode, ClubCode = clubCode } },
                var p when p.EndsWith("/settings", StringComparison.Ordinal) => new AppSettingsDto { ClubCode = clubCode, EmailVoetnoot = $"foot-{clubCode}", SeasonStartMonth = 7 },
                var p when p.EndsWith("/sync/status", StringComparison.Ordinal) => new SyncStatusDto { Status = $"sync-{clubCode}" },
                var p when p.EndsWith("/email-log", StringComparison.Ordinal) && query.Contains("status=Review", StringComparison.Ordinal)
                    => new EmailLogResponse { Count = 2, StatusCounts = new Dictionary<string, int> { ["Review"] = 2 } },
                var p when p.EndsWith("/email-log", StringComparison.Ordinal)
                    => new EmailLogResponse { Count = 1, StatusCounts = new Dictionary<string, int> { [clubCode] = 1 } },
                var p when p.EndsWith("/uitgesloten-emails", StringComparison.Ordinal)
                    => new[] { new UitgeslotenEmailAdresDto { Id = clubCode == "A" ? 1 : 2, ClubCode = clubCode } },
                var p when p.EndsWith("/velden", StringComparison.Ordinal)
                    => new[] { new VeldDto { VeldNummer = clubCode == "A" ? 1 : 2, VeldNaam = clubCode } },
                var p when p.EndsWith("/veldperiodes", StringComparison.Ordinal)
                    => new[] { new VeldPeriodeDto { Id = clubCode == "A" ? 1 : 2, Naam = clubCode } },
                var p when p.EndsWith("/veldbeschikbaarheid", StringComparison.Ordinal)
                    => new[] { new VeldBeschikbaarheidDto { Id = clubCode == "A" ? 1 : 2, VeldNaam = clubCode } },
                var p when p.EndsWith("/veldtraining", StringComparison.Ordinal)
                    => new[] { new VeldTrainingDto { Id = clubCode == "A" ? 1 : 2, VeldNaam = clubCode } },
                var p when p.EndsWith("/teams", StringComparison.Ordinal) => new[] { $"Team {clubCode}" },
                var p when p.EndsWith("/voorkeurstijden", StringComparison.Ordinal)
                    => new[] { new VoorkeurTijdDto { Id = clubCode == "A" ? 1 : 2, TeamNaam = $"Team {clubCode}" } },
                var p when p.EndsWith("/teamregels", StringComparison.Ordinal)
                    => new[] { new TeamRegelDto { Id = clubCode == "A" ? 1 : 2, TeamNaam = $"Team {clubCode}", ClubCode = clubCode } },
                var p when p.EndsWith("/pdf-export", StringComparison.Ordinal)
                    => new PdfExportStatusDto { PdfExportIngeschakeld = clubCode == "B" },
                _ => new { }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload))
            };
        }

        private sealed class PendingResponse(string clubCode, string path)
        {
            public string ClubCode { get; } = clubCode;
            public string Path { get; } = path;
            public TaskCompletionSource<HttpResponseMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new(identifier == "localStorage.getItem" ? (TValue)(object?)"A"! : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
