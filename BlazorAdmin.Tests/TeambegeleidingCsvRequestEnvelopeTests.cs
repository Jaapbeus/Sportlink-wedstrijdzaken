using BlazorAdmin.Services;
using AwesomeAssertions;
using System.Net;
using Xunit;

namespace BlazorAdmin.Tests;

public class TeambegeleidingCsvRequestEnvelopeTests
{
    [Fact]
    public async Task TeamsApiClient_SerializesExistingFiveMiBCsvWithinDerivedJsonEnvelope()
    {
        const int csvFileMaxBytes = 5 * 1024 * 1024;
        const int auditFilenameMaxCharacters = 500;
        const int wrapperOverheadBytes = 64;
        byte[]? serializedBody = null;
        using var client = new HttpClient(new CaptureHandler(async request =>
        {
            serializedBody = await request.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        })) { BaseAddress = new Uri("http://localhost/") };
        var teams = new TeamsApiClient(new AdminApiTransport(client));

        await teams.ImporteerTeambegeleidingAsync(
            new string('\0', csvFileMaxBytes), new string('\0', auditFilenameMaxCharacters));

        serializedBody.Should().NotBeNull();
        serializedBody!.Length.Should().BeLessThanOrEqualTo(
            (6 * csvFileMaxBytes) + (6 * auditFilenameMaxCharacters) + wrapperOverheadBytes);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
