using System.Net;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Pages;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

public class VeldOptimalisatieToepassenTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.Should().EndWith("/api/planner/auto-plan/toepassen");
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"bijgewerkt\":1,\"mislukt\":0,\"fouten\":[]}")
            };
        }
    }

    [Fact]
    public async Task ApplySendsAllVisibleRowsWithSavedPreviewInputsAndManualLane()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var page = new VeldOptimalisatie();
        var selector = new ClubSelectorService(null!);
        await selector.SelectClubAsync("ALLSTARS");
        Set(page, "ClubSelector", selector);
        Set(page, "PlanningApi", new PlanningApiClient(new AdminApiTransport(http)));
        Set(page, "_datumDt", new DateTime(2026, 9, 12));
        Set(page, "_bufferMinuten", 5);
        Set(page, "_planDatum", "2026-09-05");
        Set(page, "_planBuffer", 25);
        Set(page, "_plan", new AutoPlanResponseDto
        {
            Wedstrijden =
            [
                new() { WedstrijdCode = 1, HuidigeVeld = "veld 101 A1", HuidigeTijd = "09:00",
                    OptimaalVeldNummer = 101, OptimaalVeld = "veld 101 A2", OptimaalTijd = "10:00" },
                new() { WedstrijdCode = 2, HuidigeVeld = null, HuidigeTijd = null,
                    OptimaalVeldNummer = null, OptimaalVeld = null, OptimaalTijd = null }
            ]
        });

        var method = typeof(VeldOptimalisatie).GetMethod("ToepassenAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(page, null)!;

        handler.Body.Should().NotBeNull();
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        root.GetProperty("datum").GetString().Should().Be("2026-09-05");
        root.GetProperty("bufferMinuten").GetInt32().Should().Be(25);
        var rows = root.GetProperty("toewijzingen");
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("veldSubpositie").GetString().Should().Be("A2");
        rows[0].GetProperty("huidigeVeld").GetString().Should().Be("veld 101 A1");
        rows[1].GetProperty("veldNummer").ValueKind.Should().Be(JsonValueKind.Null);
        rows[1].GetProperty("tijd").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static void Set(object instance, string name, object value)
    {
        var type = instance.GetType();
        var property = type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null) { property.SetValue(instance, value); return; }
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(instance, value);
    }
}
