using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using BlazorAdmin.Shared;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace BlazorAdmin.Tests;

public class PlanningVeldMutatieTests
{
    [Theory]
    [InlineData(true, false, false, 1)]
    [InlineData(false, false, false, 0)]
    [InlineData(true, true, false, 0)]
    [InlineData(true, false, true, 0)]
    public async Task AlleenEchteGeslaagdeVeldwijziging_VraagtPlanningTeVerversen(bool success, bool dryRun, bool forced, int verwacht)
    {
        var uitkomst = new SportlinkMutatieResultaatDto { IsSuccess = success, IsDryRun = dryRun, IsForcedDryRun = forced };
        var api = new SportlinkApiClient(new AdminApiTransport(new HttpClient(new Handler(uitkomst))
            { BaseAddress = new Uri("https://allstars-fc.test/") }));
        var panel = new SportlinkMatchPanel();
        typeof(SportlinkMatchPanel).GetProperty("Api", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(panel, api);
        var verversingen = 0;
        panel.OnVeldGewijzigd = EventCallback.Factory.Create(new object(), () => verversingen++);
        await (Task)typeof(SportlinkMatchPanel).GetMethod("SaveFieldAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, null)!;
        Assert.Equal(verwacht, verversingen);
    }

    private sealed class Handler(SportlinkMutatieResultaatDto uitkomst) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(uitkomst) });
    }
}
