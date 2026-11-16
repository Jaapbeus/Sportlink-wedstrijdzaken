using System.Net;
using System.Reflection;
using System.Text.Json;
using BlazorAdmin.Models;
using BlazorAdmin.Pages;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class VeldOptimalisatieSelectieRaceTests
{
    private sealed class DeferredHandler : HttpMessageHandler
    {
        private readonly List<TaskCompletionSource<HttpResponseMessage>> _responses = [];
        public int PendingCount => _responses.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/api/planner/auto-plan", request.RequestUri!.AbsolutePath);
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _responses.Add(response);
            return response.Task;
        }

        public void Complete(int request, HttpResponseMessage response) => _responses[request].SetResult(response);
    }

    [Fact]
    public async Task MislukteHerlaad_BehoudtBestaandeSelectieEnPlan()
    {
        var (page, handler, selectie, http) = await CreatePageAsync();
        using (http)
        {
            var oudPlan = Plan(1);
            Set(page, "_plan", oudPlan);
            selectie.Selecteer(1);

            var request = AutoPlanAsync(page);
            Assert.Equal(1, handler.PendingCount);
            handler.Complete(0, Json(HttpStatusCode.InternalServerError, "{\"error\":\"synthetic failure\"}"));
            await request;

            Assert.Same(oudPlan, Get<AutoPlanResponseDto?>(page, "_plan"));
            Assert.Equal(1, selectie.WedstrijdCode);
        }
    }

    [Fact]
    public async Task SuccesvolleHerlaad_WistSelectieDieTijdensAanroepIsGemaakt()
    {
        var (page, handler, selectie, http) = await CreatePageAsync();
        using (http)
        {
            Set(page, "_plan", Plan(1));

            var request = AutoPlanAsync(page);
            Assert.Equal(1, handler.PendingCount);
            selectie.Selecteer(1); // Selecteert een bestaande rij uit het oude plan tijdens de aanvraag.
            handler.Complete(0, PlanResponse(2));
            await request;

            Assert.Equal(2, Get<AutoPlanResponseDto>(page, "_plan").Wedstrijden.Single().WedstrijdCode);
            Assert.Null(selectie.WedstrijdCode);
            Assert.False(GetProperty<bool>(page, "IsSelectedOutsideFilter"));
        }
    }

    [Fact]
    public async Task VerouderdeHerlaad_WistGeenSelectieVanNieuwPlan()
    {
        var (page, handler, selectie, http) = await CreatePageAsync();
        using (http)
        {
            Set(page, "_plan", Plan(1));

            var oudereAanvraag = AutoPlanAsync(page);
            var nieuwereAanvraag = AutoPlanAsync(page);
            Assert.Equal(2, handler.PendingCount);

            handler.Complete(1, PlanResponse(2));
            await nieuwereAanvraag;
            selectie.Selecteer(2);

            handler.Complete(0, PlanResponse(3));
            await oudereAanvraag;

            Assert.Equal(2, Get<AutoPlanResponseDto>(page, "_plan").Wedstrijden.Single().WedstrijdCode);
            Assert.Equal(2, selectie.WedstrijdCode);
        }
    }

    private static async Task<(
        VeldOptimalisatie Page, DeferredHandler Handler, GanttSelectieState Selection, HttpClient Http)> CreatePageAsync()
    {
        var handler = new DeferredHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var selector = new ClubSelectorService(null!);
        await selector.SelectClubAsync("ALLSTARS");
        var page = new VeldOptimalisatie();
        Set(page, "ClubSelector", selector);
        Set(page, "PlanningApi", new PlanningApiClient(new AdminApiTransport(http)));
        Set(page, "_datumDt", new DateTime(2026, 11, 21));
        return (page, handler, Get<GanttSelectieState>(page, "_selectie"), http);
    }

    private static Task AutoPlanAsync(VeldOptimalisatie page) =>
        (Task)typeof(VeldOptimalisatie).GetMethod("AutoPlanAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, null)!;

    private static AutoPlanResponseDto Plan(long code) => new()
    {
        Wedstrijden = [new AutoPlanWedstrijdItemDto { WedstrijdCode = code }]
    };

    private static HttpResponseMessage PlanResponse(long code) => Json(
        HttpStatusCode.OK,
        JsonSerializer.Serialize(Plan(code), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body)
    };

    private static T Get<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static T GetProperty<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static void Set(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (property is not null) { property.SetValue(instance, value); return; }
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    }
}
