using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// Vertaling van een Sportlink-mutatierespons naar <see cref="SportlinkMutationResult"/> (#1493:
/// uit PutMutationAsync gehaald; gedrag ongewijzigd). Loopt via DeleteClubMatchAsync, zonder netwerk.
/// </summary>
public class SportlinkMutatieResponsTests
{
    private sealed class FakeTokenStore : ISportlinkClubTokenStore
    {
        public string? LeesRefreshToken(string functioneleRol) => "fictief-refresh-token-voor-test";
        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static async Task<SportlinkClubResponse<SportlinkMutationResult>> Verwijder(HttpStatusCode status, string body)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
                req.RequestUri!.AbsoluteUri.Contains("idm.sportlink.com")
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"access_token":"fictief-access-token","expires_in":3600,"refresh_token":"fictief-nieuw"}""",
                            System.Text.Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        var sut = new SportlinkClubClient(new HttpClient(handler.Object), new FakeTokenStore(),
            NullLogger<SportlinkClubClient>.Instance, isDryRun: () => false);
        return await sut.DeleteClubMatchAsync("test-planner", "M000000001");
    }

    [Fact]
    public async Task Afwijzing420_MetViolations_GeeftSuccesFalseMetOmschrijving()
    {
        var r = await Verwijder((HttpStatusCode)420,
            """{"Error":true,"Status":"420","Message":"x","ViolationCodes":["X"],"Violations":{"X":"Omschrijving"}}""");

        r.Status.Should().Be(SportlinkClubCallStatus.Ok);
        r.Data!.IsSuccess.Should().BeFalse();
        r.Data.Violations.Should().Equal("X: Omschrijving");
        r.HttpStatusCode.Should().Be(420);
    }

    [Fact]
    public async Task Afwijzing_ZonderViolations_GebruiktSportlinkMessage()
    {
        var r = await Verwijder((HttpStatusCode)602, """{"Error":true,"Message":"Reden"}""");

        r.Data!.IsSuccess.Should().BeFalse();
        r.Data.Violations.Should().Equal("Sportlink 602: Reden");
    }

    [Fact]
    public async Task ServerFout5xx_MetJsonBody_IsSportlinkFout()
    {
        var r = await Verwijder(HttpStatusCode.BadGateway, """{"Error":true}""");

        r.Status.Should().Be(SportlinkClubCallStatus.SportlinkFout);
        r.Data.Should().BeNull();
        r.HttpStatusCode.Should().Be(502);
    }

    [Fact]
    public async Task OnleesbareBody_IsSportlinkFout_JsonDeserialisatieFout()
    {
        var r = await Verwijder(HttpStatusCode.OK, "geen json");

        r.Status.Should().Be(SportlinkClubCallStatus.SportlinkFout);
        r.FoutmeldingVoorLog.Should().Be("JSON deserialisatie fout");
    }

    [Fact]
    public async Task LegeBody2xx_BijDelete_IsSucces()
    {
        var r = await Verwijder(HttpStatusCode.NoContent, "");

        r.Status.Should().Be(SportlinkClubCallStatus.Ok);
        r.Data!.IsSuccess.Should().BeTrue();
    }
}
