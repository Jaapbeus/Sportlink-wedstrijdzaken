using System.Text;
using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Golden-test (#1630): elke publieke client-methode wordt aangeroepen tegen een opnemende handler en de
/// vastgelegde (methode, relatief pad, body) moet exact overeenkomen met de lijst van vóór de opsplitsing
/// van <c>AdminApiClient</c>. Zo raakt geen endpointpad kwijt of verkeerd gekoppeld bij de domeinclients.
/// </summary>
public class AdminApiPadenTests
{
    private sealed class OpnameHandler : HttpMessageHandler
    {
        public List<string> Regels { get; } = new();
        public string? Huidig { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Regels.Add($"{Huidig} | {request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')} | {body}");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [Fact]
    public async Task AlleClientMethodes_RoepenExactDezelfdeEndpointsAanAlsVoorDeOpsplitsing()
    {
        var handler = new OpnameHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var c = AdminApiPadenAanroepen.Maak(http);

        foreach (var (naam, aanroep) in AdminApiPadenAanroepen.Alle(c))
        {
            handler.Huidig = naam;
            await aanroep();
        }

        var werkelijk = handler.Regels;
        var uitvoer = Environment.GetEnvironmentVariable("ADMINAPI_GOLDEN_UIT");
        if (!string.IsNullOrEmpty(uitvoer))
            File.WriteAllLines(uitvoer, werkelijk);

        werkelijk.Should().Equal(AdminApiPadenGolden.Regels);
    }
}
