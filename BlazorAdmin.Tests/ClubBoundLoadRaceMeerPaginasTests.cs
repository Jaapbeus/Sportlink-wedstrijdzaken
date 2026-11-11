using System.Net;
using System.Reflection;
using System.Text.Json;
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

/// <summary>
/// #1872: de overige clubgebonden pagina's passen een te late response van een vorige club niet meer toe
/// (zelfde stijl als <see cref="ClubBoundLoadRaceTests"/>): club A hangt, club B antwoordt direct, daarna
/// komt A alsnog binnen en mag de stand van B niet overschrijven.
/// </summary>
public sealed class ClubBoundLoadRaceMeerPaginasTests
{
    [Fact]
    public async Task Leermomenten_LateClubAResponseCannotReplaceClubBListStatsOrLoadingState()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Leermomenten();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new LerenApiClient(transport));

        var loadA = CallAsync(page, "LaadAsync", "pending");
        Assert.Equal(2, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        Assert.Equal(2, Get<LeermomentenStatsDto>(page, "_stats").Pending);
        Assert.Equal(2, Get<List<LeermomentDto>>(page, "_items").Single().Id);

        handler.ReleaseClubA();
        await loadA;
        Assert.Equal(2, Get<LeermomentenStatsDto>(page, "_stats").Pending);
        Assert.Equal(2, Get<List<LeermomentDto>>(page, "_items").Single().Id);
        Assert.False(Get<bool>(page, "_bezig"));
        Assert.Null(Get<string?>(page, "_fout"));
    }

    [Fact]
    public async Task TeamAliassen_LateClubAResponseCannotReplaceClubBList()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new TeamAliassen();
        Set(page, "ClubSelector", club);
        Set(page, "LerenApi", new LerenApiClient(transport));

        var loadA = CallAsync(page, "LaadAsync", "pending");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        Assert.Equal(2, Get<List<TeamAliasDto>>(page, "_items").Single().Id);

        handler.ReleaseClubA();
        await loadA;
        Assert.Equal(2, Get<List<TeamAliasDto>>(page, "_items").Single().Id);
        Assert.Equal(2, Get<TeamAliassenResponse>(page, "_stats").Pending);
        Assert.False(Get<bool>(page, "_bezig"));
    }

    [Fact]
    public async Task RolFeatureInstellingen_LateClubAResponseCannotReplaceClubBMatrix()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new RolFeatureInstellingen();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new InstellingenApiClient(transport));

        var loadA = CallAsync(page, "LaadAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        AssertMatrixIsClubB(page);

        handler.ReleaseClubA();
        await loadA;
        AssertMatrixIsClubB(page);
        Assert.False(Get<bool>(page, "_laden"));
    }

    [Fact]
    public async Task Thema_LateClubAResponseCannotReplaceClubBThemeAndSaveNeverWritesStaleTheme()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Thema();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new InstellingenApiClient(transport));
        Set(page, "ThemeService", new ThemeService(new InstellingenApiClient(transport), new FakeJsRuntime()));

        var loadA = CallAsync(page, "LoadAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        Assert.Equal("#bbbbbb", Get<ThemeDto>(page, "_theme").Primary);

        handler.ReleaseClubA();
        await loadA;
        Assert.Equal("#bbbbbb", Get<ThemeDto>(page, "_theme").Primary);
        Assert.False(Get<bool>(page, "_loading"));

        await CallAsync(page, "SaveAsync");
        var put = Assert.Single(handler.Puts);
        Assert.Equal("B", put.ClubCode);
        Assert.Contains("#bbbbbb", put.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("#aaaaaa", put.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Thema_SaveIsRefusedWhileThemeOfSelectedClubIsNotLoaded()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Thema();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new InstellingenApiClient(transport));
        Set(page, "ThemeService", new ThemeService(new InstellingenApiClient(transport), new FakeJsRuntime()));

        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "LoadAsync");
        await club.SelectClubAsync("A", "Club A"); // thema in het geheugen hoort nog bij B
        await CallAsync(page, "SaveAsync");

        Assert.Empty(handler.Puts);
        Assert.False(Get<bool>(page, "_saveSuccess"));
    }

    [Fact]
    public async Task SportlinkExtensieInstellingen_LateClubAResponsesCannotReplaceClubBData()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var settingsCache = new ClubInstellingenCache(new InstellingenApiClient(transport), club);
        var page = new SportlinkExtensieInstellingen();
        Set(page, "ClubSelector", club);
        Set(page, "InstellingenCache", settingsCache);
        Set(page, "SportlinkApi", new SportlinkApiClient(transport));

        var loadA = CallAsync(page, "LoadAsync");
        Assert.Equal(4, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        AssertSportlinkExtensieIsClubB(page);

        handler.ReleaseClubA();
        await loadA;
        AssertSportlinkExtensieIsClubB(page);
        Assert.False(Get<bool>(page, "sportlinkHealthLoading"));

        await CallAsync(page, "OpslaanAsync");
        Assert.Equal("B", Assert.Single(handler.Puts).ClubCode);
        settingsCache.Dispose();
    }

    [Fact]
    public async Task Feedback_LateClubAResponseCannotReplaceClubBListOrLoadingState()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Feedback();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new FeedbackApiClient(transport));

        var loadA = CallAsync(page, "LaadLijstAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        Assert.Equal("Onderwerp B", Get<FeedbackLijstDto>(page, "_lijst").Items.Single().Onderwerp);

        handler.ReleaseClubA();
        await loadA;
        Assert.Equal("Onderwerp B", Get<FeedbackLijstDto>(page, "_lijst").Items.Single().Onderwerp);
        Assert.False(Get<bool>(page, "_bezig"));
        Assert.Null(Get<string?>(page, "_fout"));
    }

    [Theory]
    [InlineData(typeof(Planning))]
    [InlineData(typeof(VeldOptimalisatie))]
    public async Task SportlinkExtensionFlag_ReloadsOnClubSwitchAndLateClubAResponseCannotReplaceClubBFlag(Type pageType)
    {
        var (club, handler, transport) = await CreateContextAsync();
        var settingsCache = new ClubInstellingenCache(new InstellingenApiClient(transport), club);
        var page = Activator.CreateInstance(pageType)!;
        Set(page, "ClubSelector", club);
        Set(page, "InstellingenCache", settingsCache);
        Set(page, "PlanningApi", new PlanningApiClient(transport));

        var loadA = CallAsync(page, "LaadSportlinkExtensionStatusAsync");
        Assert.Equal(1, handler.PendingCount);
        Assert.False(Get<bool>(page, "_sportlinkExtensionEnabled"));
        await club.SelectClubAsync("B", "Club B");
        // Via de echte clubwissel-reload: zonder herladen van de vlag blijft de (nog hangende) stand van A staan.
        await CallAsync(page, "OnClubChangedAsync");
        Assert.True(Get<bool>(page, "_sportlinkExtensionEnabled"));

        handler.ReleaseClubA();
        await loadA;
        Assert.True(Get<bool>(page, "_sportlinkExtensionEnabled"));
        settingsCache.Dispose();
    }

    [Fact]
    public async Task Planning_LateClubAVeldbezettingCannotReplaceClubBBezettingOrLoadingState()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Planning();
        Set(page, "ClubSelector", club);
        Set(page, "PlanningApi", new PlanningApiClient(transport));

        var loadA = CallAsync(page, "LoadVeldbezettingAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "LoadVeldbezettingAsync");
        Assert.Equal("Team B", Get<List<VeldbezettingItemDto>>(page, "_veldbezetting").Single().TeamNaam);

        handler.ReleaseClubA();
        await loadA;
        Assert.Equal("Team B", Get<List<VeldbezettingItemDto>>(page, "_veldbezetting").Single().TeamNaam);
        Assert.False(Get<bool>(page, "_veldbezettingBezig"));
        Assert.Null(Get<string?>(page, "_veldbezettingError"));
    }

    [Fact]
    public async Task Thema_ClubSwitchDuringWebsiteExtract_ReleasesButtonAndShowsNoClubAResult()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = await LoadThemaForClubAAsync(club, handler, transport);

        var extractA = CallAsync(page, "ExtractFromWebsiteAsync");
        Assert.Equal(1, handler.PendingCount); // save is klaar, het ophalen van club A hangt
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");

        handler.ReleaseClubA();
        await extractA;
        Assert.False(Get<bool>(page, "_extracting"));
        Assert.Empty(Get<List<string>>(page, "_extractedColors"));
        Assert.Null(Get<string?>(page, "_extractError"));
    }

    [Fact]
    public async Task Thema_ClubSwitchDuringSaveBeforeExtract_SendsNoExtractRequestEvenAfterEarlierSuccessfulSave()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = await LoadThemaForClubAAsync(club, handler, transport);
        await CallAsync(page, "SaveAsync");
        Assert.True(Get<bool>(page, "_saveSuccess"));

        handler.HoldPuts = true;
        var extractA = CallAsync(page, "ExtractFromWebsiteAsync");
        Assert.Equal(1, handler.PendingCount); // de save van club A hangt
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");

        handler.ReleaseClubA();
        await extractA;
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/theme/extract", StringComparison.Ordinal));
        Assert.False(Get<bool>(page, "_extracting"));
        Assert.Null(Get<string?>(page, "_extractError"));
    }

    [Fact]
    public async Task SportlinkExtensieInstellingen_SaveAfterClubSwitchWithoutReloadWritesNothing()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var settingsCache = new ClubInstellingenCache(new InstellingenApiClient(transport), club);
        var page = new SportlinkExtensieInstellingen();
        Set(page, "ClubSelector", club);
        Set(page, "InstellingenCache", settingsCache);
        Set(page, "SportlinkApi", new SportlinkApiClient(transport));

        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "LoadAsync");
        await club.SelectClubAsync("A", "Club A"); // instellingen in het geheugen horen nog bij B
        await CallAsync(page, "OpslaanAsync");

        Assert.Empty(handler.Puts);
        Assert.False(Get<bool>(page, "saving"));
        settingsCache.Dispose();
    }

    [Fact]
    public async Task SportlinkExtensieInstellingen_ClubSwitchDuringSaveDoesNotApplyClubAOutcome()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var settingsCache = new ClubInstellingenCache(new InstellingenApiClient(transport), club);
        var page = new SportlinkExtensieInstellingen();
        Set(page, "ClubSelector", club);
        Set(page, "InstellingenCache", settingsCache);
        Set(page, "SportlinkApi", new SportlinkApiClient(transport));
        var loadA = CallAsync(page, "LoadAsync");
        handler.ReleaseClubA();
        await loadA;

        handler.HoldPuts = true;
        var saveA = CallAsync(page, "OpslaanAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        handler.ReleaseClubA();
        await saveA;

        Assert.Null(Get<string?>(page, "successMessage"));
        Assert.False(Get<bool>(page, "saving"));
        settingsCache.Dispose();
    }

    [Fact]
    public async Task RolFeatureInstellingen_ClubSwitchDuringToggleDoesNotWriteClubAValueIntoClubBMatrix()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new RolFeatureInstellingen();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new InstellingenApiClient(transport));

        handler.HoldPuts = true;
        var zetA = CallAsync(page, "ZetAsync", "user", "alleen-a", true);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        handler.ReleaseClubA();
        await zetA;

        AssertMatrixIsClubB(page);
        Assert.DoesNotContain(("user", "alleen-a"), Get<Dictionary<(string RolNaam, string FeatureKey), bool>>(page, "_instellingen").Keys);
        Assert.Null(Get<string?>(page, "_successMessage"));
    }

    [Fact]
    public async Task Feedback_ClubSwitchDuringPublishDoesNotShowClubAOutcome()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Feedback();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new FeedbackApiClient(transport));

        var publishA = CallAsync(page, "PubliceerAsync", Guid.NewGuid());
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        handler.ReleaseClubA();
        await publishA;

        Assert.Null(Get<string?>(page, "_melding"));
        Assert.Equal("Onderwerp B", Get<FeedbackLijstDto>(page, "_lijst").Items.Single().Onderwerp);
    }

    [Fact]
    public async Task TeamAliassen_ClubSwitchDuringTeamListRebuildDoesNotShowClubAResult()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new TeamAliassen();
        Set(page, "ClubSelector", club);
        Set(page, "LerenApi", new LerenApiClient(transport));
        Set(page, "TeamsApi", new TeamsApiClient(transport));

        var herstelA = CallAsync(page, "HerstelTeamlijstAsync");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        handler.ReleaseClubA();
        await herstelA;

        Assert.Null(Get<TeamHerstelDto?>(page, "_herstelResultaat"));
        Assert.False(Get<bool>(page, "_herstelBezig"));
        Assert.Equal(2, Get<List<TeamAliasDto>>(page, "_items").Single().Id);
    }

    [Fact]
    public async Task TeamAliassen_ClubSwitchDuringValidateDoesNotApplyClubAOutcome()
    {
        var (club, handler, transport) = await CreateContextAsync();
        handler.HoldPuts = true;
        var page = new TeamAliassen();
        Set(page, "ClubSelector", club);
        Set(page, "LerenApi", new LerenApiClient(transport));

        var valideerA = CallAsync(page, "ValideerAsync", 1, "validated");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        var requestsVoorRelease = handler.Requests.Count;
        handler.ReleaseClubA();
        await valideerA;

        Assert.Null(Get<string?>(page, "_melding"));
        Assert.Null(Get<string?>(page, "_fout"));
        Assert.Equal(2, Get<List<TeamAliasDto>>(page, "_items").Single().Id);
        Assert.Empty(Get<HashSet<int>>(page, "_actieBezigId"));
        Assert.Equal(requestsVoorRelease, handler.Requests.Count); // geen vervolg-load na de uitkomst van club A
    }

    [Fact]
    public async Task TeamAliassen_ClubSwitchDuringDeleteKeepsClubBConfirmationAndShowsNoClubAOutcome()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new TeamAliassen();
        Set(page, "ClubSelector", club);
        Set(page, "LerenApi", new LerenApiClient(transport));
        SetField(page, "_verwijderConfirmId", 1);

        var verwijderA = CallAsync(page, "VerwijderAsync", 1);
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        Assert.Null(Get<int?>(page, "_verwijderConfirmId"));
        SetField(page, "_verwijderConfirmId", 2); // beheerder opent de bevestiging voor een alias van club B
        var requestsVoorRelease = handler.Requests.Count;
        handler.ReleaseClubA();
        await verwijderA;

        Assert.Equal(2, Get<int?>(page, "_verwijderConfirmId"));
        Assert.Null(Get<string?>(page, "_melding"));
        Assert.Null(Get<string?>(page, "_fout"));
        Assert.Equal(2, Get<List<TeamAliasDto>>(page, "_items").Single().Id);
        Assert.Empty(Get<HashSet<int>>(page, "_actieBezigId"));
        Assert.Equal(requestsVoorRelease, handler.Requests.Count);
    }

    [Fact]
    public async Task Leermomenten_ClubSwitchDuringDeleteKeepsClubBConfirmationAndStartsNoClubAReload()
    {
        var (club, handler, transport) = await CreateContextAsync();
        var page = new Leermomenten();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new LerenApiClient(transport));
        SetField(page, "_verwijderBevestigId", 1);

        var verwijderA = CallAsync(page, "VerwijderAsync", 1);
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        Assert.Null(Get<int?>(page, "_verwijderBevestigId"));
        SetField(page, "_verwijderBevestigId", 2); // beheerder opent de bevestiging voor een leermoment van club B
        var requestsVoorRelease = handler.Requests.Count;
        handler.ReleaseClubA();
        await verwijderA;

        Assert.Equal(2, Get<int?>(page, "_verwijderBevestigId"));
        Assert.Null(Get<string?>(page, "_fout"));
        Assert.Equal(2, Get<List<LeermomentDto>>(page, "_items").Single().Id);
        Assert.Empty(Get<HashSet<int>>(page, "_actieBezigId"));
        Assert.Equal(requestsVoorRelease, handler.Requests.Count);
    }

    [Fact]
    public async Task Leermomenten_ClubSwitchDuringValidateStartsNoClubAReload()
    {
        var (club, handler, transport) = await CreateContextAsync();
        handler.HoldPuts = true;
        var page = new Leermomenten();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new LerenApiClient(transport));

        var valideerA = CallAsync(page, "ValideerAsync", 1, "validated");
        Assert.Equal(1, handler.PendingCount);
        await club.SelectClubAsync("B", "Club B");
        await CallAsync(page, "OnClubChangedAsync");
        var requestsVoorRelease = handler.Requests.Count;
        handler.ReleaseClubA();
        await valideerA;

        Assert.Null(Get<string?>(page, "_fout"));
        Assert.Equal(2, Get<List<LeermomentDto>>(page, "_items").Single().Id);
        Assert.Empty(Get<HashSet<int>>(page, "_actieBezigId"));
        Assert.Equal(requestsVoorRelease, handler.Requests.Count);
    }

    private static async Task<Thema> LoadThemaForClubAAsync(ClubSelectorService club, Handler handler, AdminApiTransport transport)
    {
        var page = new Thema();
        Set(page, "ClubSelector", club);
        Set(page, "Api", new InstellingenApiClient(transport));
        Set(page, "ThemeService", new ThemeService(new InstellingenApiClient(transport), new FakeJsRuntime()));
        var loadA = CallAsync(page, "LoadAsync");
        handler.ReleaseClubA();
        await loadA;
        Assert.Equal("#aaaaaa", Get<ThemeDto>(page, "_theme").Primary);
        return page;
    }

    private static void AssertMatrixIsClubB(RolFeatureInstellingen page)
    {
        var matrix = Get<Dictionary<(string RolNaam, string FeatureKey), bool>>(page, "_instellingen");
        Assert.True(matrix[("user", "club-b")]);
        Assert.DoesNotContain(("user", "club-a"), matrix.Keys);
    }

    private static void AssertSportlinkExtensieIsClubB(SportlinkExtensieInstellingen page)
    {
        Assert.Equal("B", Get<AppSettingsDto>(page, "settings")!.ClubCode);
        Assert.Equal("rol-B", Get<List<SportlinkExtensieRolDto>>(page, "sportlinkRollen").Single().RolNaam);
        Assert.Equal("acct-B", Get<SportlinkAutoLoginStatusDto>(page, "autoLoginStatus")!.LastError);
        Assert.True(Get<SportlinkExtensieHealthDto>(page, "sportlinkHealth")!.ExtensionEnabled);
    }

    private static async Task<(ClubSelectorService Club, Handler Handler, AdminApiTransport Transport)> CreateContextAsync()
    {
        var club = new ClubSelectorService(new FakeJsRuntime());
        await club.SelectClubAsync("A", "Club A");
        var handler = new Handler();
        var clubHandler = new ClubCodeHeaderHandler(club) { InnerHandler = handler };
        var transport = new AdminApiTransport(new HttpClient(clubHandler) { BaseAddress = new Uri("https://example.test/") });
        return (club, handler, transport);
    }

    private static T Get<T>(object instance, string field) =>
        (T)instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static void Set(object instance, string property, object value)
    {
        // Pagina's roepen StateHasChanged aan; zonder renderer-koppeling gooit Blazor "render handle is not yet assigned".
        if (property == "ClubSelector" && instance is IComponent component && !Renderers.TryGetValue(instance, out _))
        {
            var renderer = new SwallowingRenderer();
            renderer.Koppel(component);
            Renderers.Add(instance, renderer);
        }
        SetProperty(instance, property, value);
    }

    private static void SetField(object instance, string field, object? value) =>
        instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SwallowingRenderer> Renderers = new();

    private sealed class SwallowingRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = new InlineDispatcher();
        public void Koppel(IComponent component) => AssignRootComponentId(component);
        protected override void HandleException(Exception exception) { /* markup-rendering zonder services is niet de bedoeling van deze tests */ }
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    /// <summary>Voert alles inline uit: de tests sturen de pagina's zelf aan vanuit één logische thread.</summary>
    private sealed class InlineDispatcher : Dispatcher
    {
        public override bool CheckAccess() => true;
        public override Task InvokeAsync(Action workItem) { workItem(); return Task.CompletedTask; }
        public override Task InvokeAsync(Func<Task> workItem) => workItem();
        public override Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem) => Task.FromResult(workItem());
        public override Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem) => workItem();
    }

    private static void SetProperty(object instance, string property, object value) =>
        instance.GetType().GetProperty(property, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static Task CallAsync(object instance, string method, params object[] args) =>
        (Task)instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args.Length == 0 ? null : args)!;

    private sealed class Handler : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly List<(string Path, TaskCompletionSource<HttpResponseMessage> Completion)> _pending = [];

        public List<(string ClubCode, string Body)> Puts { get; } = [];
        public List<(HttpMethod Method, string ClubCode, string Path)> Requests { get; } = [];
        public int PendingCount { get { lock (_sync) return _pending.Count; } }

        /// <summary>Laat ook een PUT van club A hangen tot <see cref="ReleaseClubA"/> (clubwissel tijdens opslaan).</summary>
        public bool HoldPuts { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clubCode = request.Headers.GetValues("X-Club-Code").Single();
            var path = request.RequestUri!.AbsolutePath;
            lock (_sync) Requests.Add((request.Method, clubCode, path));
            if (request.Method == HttpMethod.Put)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (_sync) Puts.Add((clubCode, body));
                if (!HoldPuts || clubCode != "A") return Json(new { });
            }

            if (clubCode != "A") return Json(Payload(clubCode, path));
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _pending.Add((path, completion));
            return await completion.Task;
        }

        public void ReleaseClubA()
        {
            (string Path, TaskCompletionSource<HttpResponseMessage> Completion)[] pending;
            lock (_sync) { pending = [.. _pending]; _pending.Clear(); }
            foreach (var (path, completion) in pending) completion.TrySetResult(Json(Payload("A", path)));
        }

        private static HttpResponseMessage Json(object payload) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };

        private static object Payload(string club, string path)
        {
            var id = club == "A" ? 1 : 2;
            return path switch
            {
                var p when p.EndsWith("/leermomenten/stats", StringComparison.Ordinal) => new LeermomentenStatsDto { Pending = id },
                var p when p.EndsWith("/leermomenten", StringComparison.Ordinal)
                    => new LeermomentenResponse { Items = [new LeermomentDto { Id = id }] },
                var p when p.EndsWith("/teamaliassen", StringComparison.Ordinal)
                    => new TeamAliassenResponse { Pending = id, Items = [new TeamAliasDto { Id = id }] },
                var p when p.EndsWith("/rolfeatureinstellingen", StringComparison.Ordinal)
                    => new[] { new RolFeatureInstellingDto { RolNaam = "user", FeatureKey = $"club-{club.ToLowerInvariant()}", Enabled = true } },
                var p when p.EndsWith("/theme/extract", StringComparison.Ordinal)
                    => new ThemeExtractResultDto { Colors = [$"#{club}{club}{club}"] },
                var p when p.EndsWith("/teams/herstel", StringComparison.Ordinal)
                    => new TeamHerstelDto { ClubCode = club },
                var p when p.EndsWith("/publiceer", StringComparison.Ordinal)
                    => new FeedbackPubliceerResponse { IssueNummer = id },
                var p when p.EndsWith("/theme", StringComparison.Ordinal)
                    => new ThemeDto { Primary = club == "A" ? "#aaaaaa" : "#bbbbbb" },
                var p when p.EndsWith("/settings", StringComparison.Ordinal)
                    => new AppSettingsDto { ClubCode = club, SportlinkExtensionEnabled = club == "B" },
                var p when p.EndsWith("/sportlink-extensie/rollen", StringComparison.Ordinal)
                    => new[] { new SportlinkExtensieRolDto { RolNaam = $"rol-{club}" } },
                var p when p.EndsWith("/autologin", StringComparison.Ordinal)
                    => new SportlinkAutoLoginStatusDto { LastError = $"acct-{club}" },
                var p when p.EndsWith("/sportlink-extensie/health", StringComparison.Ordinal)
                    => new SportlinkExtensieHealthDto { ExtensionEnabled = club == "B" },
                var p when p.EndsWith("/feedback", StringComparison.Ordinal)
                    => new FeedbackLijstDto { Totaal = 1, Items = [new FeedbackItemDto { Onderwerp = $"Onderwerp {club}" }] },
                var p when p.EndsWith("/veldbezetting", StringComparison.Ordinal)
                    => new VeldbezettingAntwoordDto { Wedstrijden = [new VeldbezettingItemDto { TeamNaam = $"Team {club}" }] },
                _ => new { }
            };
        }
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new(identifier == "localStorage.getItem" ? (TValue)(object?)"A"! : identifier == "themeHelper.getMode" ? (TValue)(object)"light" : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
