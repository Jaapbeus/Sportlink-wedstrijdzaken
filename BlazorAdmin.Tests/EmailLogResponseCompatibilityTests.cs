using BlazorAdmin.Models;
using BlazorAdmin.Services;
using System.Text.Json;
using Xunit;

namespace BlazorAdmin.Tests;

public sealed class EmailLogResponseCompatibilityTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    [Fact]
    public void OudApiAntwoord_ZonderStatusCounts_BlijfHerkenbaarAlsNietBeschikbaar()
    {
        var response = JsonSerializer.Deserialize<EmailLogResponse>("""{"count":0,"limit":200,"items":[]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(response);
        Assert.Null(response.StatusCounts);
    }

    [Fact]
    public async Task UtcMiddernachtAlsTot_BlijfExactTimestamp()
    {
        var handler = new CaptureHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new EmailApiClient(new AdminApiTransport(http));

        await api.GetEmailLogAsync(tot: new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("tot=2026-11-09T00%3A00%3A00.0000000Z", handler.Uri!.Query);
    }
}
