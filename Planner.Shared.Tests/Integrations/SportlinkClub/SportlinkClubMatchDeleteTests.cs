using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// Verwijderen van een clubwedstrijd (#1440, <c>DELETE competition/match/clubmatch/ClubMatchDelete</c>).
/// Het contract komt uit Sportlinks publieke frontend-bundle en is NOOIT live gezien. Daarom staat
/// <c>ClubMatchDeleteLiveBevestigd</c> op <c>false</c>: het pad is altijd een simulatie, ook als de
/// club dry-run uit heeft staan. Deze tests bewijzen dat hard — een agent zet die vlag nooit om
/// (docs/SPORTLINK-WEB-EXTENSION.md §4.4); doet een mens dat na een live trace, dan moet deze test
/// bewust mee veranderen.
/// </summary>
public class SportlinkClubMatchDeleteTests
{
    private const string Rol = "test-planner";
    private const string PublicMatchId = "M000000001";

    private sealed class FakeTokenStore : ISportlinkClubTokenStore
    {
        private string? _token = "fictief-refresh-token-voor-test";
        public string? LeesRefreshToken(string functioneleRol) => _token;
        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
        {
            _token = nieuwRefreshToken;
            return Task.CompletedTask;
        }
    }

    private static (SportlinkClubClient Sut, List<HttpRequestMessage> Verzoeken) Maak(bool clubDryRun)
    {
        var verzoeken = new List<HttpRequestMessage>();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                verzoeken.Add(req);
                if (req.RequestUri!.AbsoluteUri.Contains("idm.sportlink.com"))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"access_token":"fictief-access-token","expires_in":3600,"refresh_token":"fictief-nieuw"}""",
                            System.Text.Encoding.UTF8, "application/json")
                    };
                if (req.RequestUri.AbsoluteUri.Contains("ClubMatchDelete"))
                    throw new InvalidOperationException("Zolang ClubMatchDeleteLiveBevestigd=false mag er nooit een echte DELETE naar Sportlink.");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
        var sut = new SportlinkClubClient(new HttpClient(handler.Object), new FakeTokenStore(),
            NullLogger<SportlinkClubClient>.Instance, isDryRun: () => clubDryRun);
        return (sut, verzoeken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteClubMatchAsync_ZolangNietLiveBevestigd_StuurtNooitEenDelete(bool clubDryRun)
    {
        var (sut, verzoeken) = Maak(clubDryRun);

        var result = await sut.DeleteClubMatchAsync(Rol, PublicMatchId);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsDryRun.Should().BeTrue();
        result.Data.IsForcedDryRun.Should().BeTrue(
            "de code-lock ClubMatchDeleteLiveBevestigd=false wint van de club-instelling sportlinkDryRun");
        verzoeken.Should().NotContain(r => r.RequestUri!.AbsoluteUri.Contains("ClubMatchDelete"));
        verzoeken.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public void ClubMatchDeleteLiveBevestigd_StaatOpFalse()
    {
        // Vangnet naast de gedragstest hierboven: een stille omzetting van de vlag faalt hier met naam.
        var veld = typeof(SportlinkClubClient).GetField("ClubMatchDeleteLiveBevestigd",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        veld.Should().NotBeNull("de vlag moet grep-baar bij naam bestaan, zelfde patroon als ClubMatchLiveBevestigd");
        veld!.IsLiteral.Should().BeTrue();
        ((bool)veld.GetRawConstantValue()!).Should().BeFalse(
            "alleen de eigenaar zet dit om, na een live trace, in een aparte PR (docs/SPORTLINK-WEB-EXTENSION.md §4.4)");
    }

    [Fact]
    public void BouwClubMatchDeleteUrl_ZetPublicMatchIdAlsQueryParameter()
    {
        // Bundle: deleteMatch → {method:`DELETE`, params:{PublicMatchId}, url:`competition/match/clubmatch/ClubMatchDelete`}.
        SportlinkClubClient.BouwClubMatchDeleteUrl("M12 3&x")
            .Should().Be("https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/ClubMatchDelete?PublicMatchId=M12%203%26x");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "", true)]
    [InlineData(HttpStatusCode.OK, "   ", true)]
    [InlineData(HttpStatusCode.NoContent, "", true)]
    [InlineData(HttpStatusCode.OK, "{}", false)]
    [InlineData((HttpStatusCode)420, "", false)]
    [InlineData(HttpStatusCode.InternalServerError, "", false)]
    public void IsLegeSuccesRespons_AlleenEen2xxZonderBodyIsZonderMeerGeslaagd(HttpStatusCode status, string body, bool verwacht)
    {
        // De bundle leest de succesbody niet; een lege 2xx mag dus geen "onherkenbare respons"-fout geven.
        SportlinkClubClient.IsLegeSuccesRespons(status, body).Should().Be(verwacht);
    }
}
