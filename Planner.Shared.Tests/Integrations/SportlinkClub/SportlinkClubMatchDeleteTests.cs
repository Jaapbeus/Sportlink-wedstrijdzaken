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
/// Sinds #1458 staat <c>ClubMatchDeleteLiveBevestigd</c> op <c>true</c> (eigenaarsbesluit na live
/// trace): de aanroep volgt de club-instelling <c>sportlinkDryRun</c>. Deze tests bewijzen dat dry-run
/// nog steeds niets verstuurt, en dat de live respons <c>{PublicMatchId, IsSuccess}</c> als succes
/// wordt gelezen.
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

    private const string LiveSuccesBody = """{"PublicMatchId":"M000000001","IsSuccess":true}""";

    private static (SportlinkClubClient Sut, List<HttpRequestMessage> Verzoeken) Maak(bool clubDryRun, string deleteBody = LiveSuccesBody)
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
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(deleteBody, System.Text.Encoding.UTF8, "application/json")
                    };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
        var sut = new SportlinkClubClient(new HttpClient(handler.Object), new FakeTokenStore(),
            NullLogger<SportlinkClubClient>.Instance, isDryRun: () => clubDryRun);
        return (sut, verzoeken);
    }

    [Fact]
    public async Task DeleteClubMatchAsync_ClubDryRunAan_StuurtGeenDelete_MaarIsGeenGedwongenSimulatie()
    {
        var (sut, verzoeken) = Maak(clubDryRun: true);

        var result = await sut.DeleteClubMatchAsync(Rol, PublicMatchId);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsDryRun.Should().BeTrue();
        result.Data.IsForcedDryRun.Should().BeFalse("de code-lock is opgeheven (#1458); alleen de club-instelling bepaalt dry-run");
        verzoeken.Should().NotContain(r => r.RequestUri!.AbsoluteUri.Contains("ClubMatchDelete"));
    }

    [Fact]
    public async Task DeleteClubMatchAsync_ClubDryRunUit_StuurtEchteDelete_EnLeestLiveResponsvorm()
    {
        var (sut, verzoeken) = Maak(clubDryRun: false);

        var result = await sut.DeleteClubMatchAsync(Rol, PublicMatchId);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeTrue();
        result.Data.IsDryRun.Should().BeFalse();
        result.Data.IsForcedDryRun.Should().BeFalse();
        result.Data.PublicMatchId.Should().Be(PublicMatchId, "de live respons is {PublicMatchId, IsSuccess}, niet leeg");
        var delete = verzoeken.Should().ContainSingle(r => r.RequestUri!.AbsoluteUri.Contains("ClubMatchDelete")).Subject;
        delete.Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public void ClubMatchDeleteLiveBevestigd_StaatOpTrue()
    {
        // Vangnet: een stille omzetting van de vlag faalt hier met naam.
        var veld = typeof(SportlinkClubClient).GetField("ClubMatchDeleteLiveBevestigd",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        veld.Should().NotBeNull("de vlag moet grep-baar bij naam bestaan, zelfde patroon als ClubMatchLiveBevestigd");
        veld!.IsLiteral.Should().BeTrue();
        ((bool)veld.GetRawConstantValue()!).Should().BeTrue(
            "eigenaarsbesluit 03-10-2026 (#1458): live; omzetten naar false is een bewuste, reviewbare wijziging");
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
