using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Planner.Shared.Integrations.SportlinkClub;
using System.Net;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// Tests voor SportlinkClubClient. Alle HTTP-aanroepen gaan tegen gemockte HttpMessageHandler.
/// Geen echte Sportlink-tokens of endpoints.
/// </summary>
public class SportlinkClubClientTests
{
    private const string TestFunctioneleRol = "test-planner";
    private const string TestPublicMatchId = "M000000001";
    private const string FictieveAccessToken = "fictief-access-token-voor-test";
    private const string FictieveRefreshToken = "fictief-refresh-token-voor-test";
    private const string NewFictieveRefreshToken = "nieuw-fictief-refresh-token-voor-test";

    /// <summary>
    /// Fake token store voor testen (in-memory).
    /// </summary>
    private sealed class FakeSportlinkClubTokenStore : ISportlinkClubTokenStore
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

        public FakeSportlinkClubTokenStore(string? initialToken = null)
        {
            if (initialToken != null)
                _tokens[TestFunctioneleRol] = initialToken;
        }

        public string? LeesRefreshToken(string functioneleRol) =>
            _tokens.TryGetValue(functioneleRol, out var token) ? token : null;

        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
        {
            _tokens[functioneleRol] = nieuwRefreshToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Helper: bouw gemockte HttpClient met Custom HttpMessageHandler.
    /// </summary>
    private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => respond(req));
        return new HttpClient(handler.Object);
    }

    /// <summary>
    /// Helper: JSON-response.
    /// </summary>
    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    /// <summary>
    /// Helper: token endpoint response.
    /// </summary>
    private static string TokenResponse(string accessToken, int expiresIn = 3600, string? newRefreshToken = null) =>
        $$"""
        {
            "access_token": "{{accessToken}}",
            "expires_in": {{expiresIn}},
            "refresh_token": "{{newRefreshToken ?? FictieveRefreshToken}}",
            "token_type": "Bearer"
        }
        """;

    /// <summary>
    /// Helper: match endpoint response.
    /// </summary>
    private static string MatchResponse(
        string publicMatchId = TestPublicMatchId,
        string matchStatus = "CONCEPT",
        bool isHomeMatch = true,
        bool isEditFieldAllowed = true,
        bool isAddScoreAllowed = true) =>
        $$"""
        {
            "publicMatchId": "{{publicMatchId}}",
            "externalMatchId": "123456",
            "matchDate": "2026-09-15T19:30:00+02:00",
            "matchStatus": "{{matchStatus}}",
            "isHomeMatch": {{isHomeMatch.ToString().ToLower()}},
            "isCanceledMatch": false,
            "isConceptMatch": true,
            "taskStatus": null,
            "isEditFieldAllowed": {{isEditFieldAllowed.ToString().ToLower()}},
            "isAssignDressingRoomsAllowed": true,
            "isAssignOfficialsAllowed": true,
            "isEditFieldSidePanelAllowed": true,
            "isAddScoreAllowed": {{isAddScoreAllowed.ToString().ToLower()}}
        }
        """;

    [Fact]
    public async Task GetMatchAsync_GeldigTokenEnHappyPath_RetourneertGemapteMatch()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var callCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
            {
                callCount++;
                return JsonResponse(TokenResponse(FictieveAccessToken));
            }
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
            {
                callCount++;
                return JsonResponse(MatchResponse());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act
        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.PublicMatchId.Should().Be(TestPublicMatchId);
        result.Data.IsHomeMatch.Should().BeTrue();
        result.Data.IsEditFieldAllowed.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        callCount.Should().Be(2, "een aanroep naar token endpoint, één naar match endpoint");
    }

    [Fact]
    public async Task GetMatchAsync_GeenRefreshTokenGeregistreerdVoorRol_RetourneertRolNietGekoppeld()
    {
        // Arrange: lege token store, geen token voor de rol
        var tokenStore = new FakeSportlinkClubTokenStore();
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act
        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        result.Status.Should().Be(SportlinkClubCallStatus.RolNietGekoppeld);
        result.Data.Should().BeNull();
        result.FoutmeldingVoorLog.Should().Contain(TestFunctioneleRol);
        result.HttpStatusCode.Should().BeNull();
    }

    [Fact]
    public async Task GetMatchAsync_RefreshGeeftInvalidGrant_RetourneertHerkoppelingVereist()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error": "invalid_grant"}""")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act
        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        result.Status.Should().Be(SportlinkClubCallStatus.HerkoppelingVereist);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetMatchAsync_TweedeVerzoekBinnenGeldigheidsduur_HergebruiktGecachedAccessTokenZonderNieuweTokenCall()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var tokenCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
            {
                tokenCallCount++;
                return JsonResponse(TokenResponse(FictieveAccessToken, expiresIn: 3600));
            }
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse(MatchResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act: twee aanroepen met dezelfde rol
        var result1 = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);
        var result2 = await sut.GetMatchAsync(TestFunctioneleRol, "M000000002");

        // Assert
        result1.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result2.Status.Should().Be(SportlinkClubCallStatus.Ok);
        tokenCallCount.Should().Be(1, "token endpoint mag maar één keer aangeroepen worden (gecached)");
    }

    [Fact]
    public async Task GetMatchAsync_MatchEndpointGeeft401OndanksGecachedToken_VerversTEenmaalEnHeraanvraagt()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var tokenCallCount = 0;
        var matchCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
            {
                tokenCallCount++;
                return JsonResponse(TokenResponse(FictieveAccessToken + tokenCallCount, expiresIn: 3600));
            }
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
            {
                matchCallCount++;
                // Eerste aanroep: 401, tweede: succes
                return matchCallCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : JsonResponse(MatchResponse());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act
        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().NotBeNull();
        tokenCallCount.Should().Be(2, "token moet opnieuw vernieuwd worden na 401");
        matchCallCount.Should().Be(2, "match endpoint moet twee keer aangeroepen worden");
    }

    [Fact]
    public async Task GetMatchAsync_MatchEndpointBlijftNaHerhaaldeRefresh401Geven_RetourneertHerkoppelingVereist()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken, expiresIn: 3600));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized); // Altijd 401
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act
        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        result.Status.Should().Be(SportlinkClubCallStatus.HerkoppelingVereist);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetMatchAsync_NetwerkfoutBijMatchEndpoint_RetourneertNetwerkFoutMetVeiligeMelding()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                throw new HttpRequestException("Netwerk down");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act
        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        result.Status.Should().Be(SportlinkClubCallStatus.NetwerkFout);
        result.Data.Should().BeNull();
        result.FoutmeldingVoorLog.Should().NotContain("fictief"); // Nooit tokens in logs
    }

    [Fact]
    public async Task GetMatchAsync_OnverwachteJsonvormVanMatchEndpoint_RetourneertSportlinkFoutZonderException()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse("{ broken JSON ]"); // Echt ongeldig JSON
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act + Assert: geen exception
        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        result.Status.Should().Be(SportlinkClubCallStatus.SportlinkFout);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetMatchAsync_MatchEndpointGeeftNietSuccesStatuscode_RetourneertSportlinkFoutMetStatuscode()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act
        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        result.Status.Should().Be(SportlinkClubCallStatus.SportlinkFout);
        result.Data.Should().BeNull();
        result.HttpStatusCode.Should().Be((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetMatchAsync_ZetVerplichteNavajoHeadersEnBearerToken_OpElkVerzoek()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        HttpRequestMessage? capturedRequest = null;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                capturedRequest = req;
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse(MatchResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        // Act
        await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Authorization?.Scheme.Should().Be("Bearer");
        capturedRequest.Headers.Authorization?.Parameter.Should().Be(FictieveAccessToken);
        capturedRequest.Headers.GetValues("X-Navajo-Entity").Should().Contain("competition/match/Match");
        capturedRequest.Headers.GetValues("X-Navajo-Instance").Should().Contain("KNVB");
        capturedRequest.Headers.GetValues("X-Navajo-Locale").Should().Contain("nl");
    }

    [Fact]
    public async Task GetMatchAsync_LogtNooitDeTokenwaarde()
    {
        // Arrange
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var logger = new TestLogger();
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse(MatchResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, logger);

        // Act
        await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        // Assert
        var allLogs = logger.AllLogs;
        foreach (var log in allLogs)
        {
            log.Should().NotContain("fictief", "geen fictieve tokens in logs");
            log.Should().NotContain(FictieveAccessToken);
            log.Should().NotContain(FictieveRefreshToken);
        }
    }

    [Fact]
    public async Task SchrijfRefreshTokenAsync_ZonderAzureManagementEnvVars_DoetNiksEnBelltNooitDefaultAzureCredential()
    {
        // Arrange: geen env vars (lokale omgeving)
        var oldSub = Environment.GetEnvironmentVariable("AzureSubscriptionId");
        var oldRg = Environment.GetEnvironmentVariable("AzureResourceGroupName");
        var oldFunc = Environment.GetEnvironmentVariable("AzureFunctionAppName");

        Environment.SetEnvironmentVariable("AzureSubscriptionId", null);
        Environment.SetEnvironmentVariable("AzureResourceGroupName", null);
        Environment.SetEnvironmentVariable("AzureFunctionAppName", null);

        try
        {
            var tokenStore = new SportlinkClubAppSettingsTokenStore(
                NullLogger<SportlinkClubAppSettingsTokenStore>.Instance);
            var httpCallCount = 0;
            var client = MakeClient(_ =>
            {
                httpCallCount++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            // Act
            await tokenStore.SchrijfRefreshTokenAsync("test-rol", "nieuw-token");

            // Assert
            httpCallCount.Should().Be(0, "geen HTTP-aanroepen zonder Azure env vars");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AzureSubscriptionId", oldSub);
            Environment.SetEnvironmentVariable("AzureResourceGroupName", oldRg);
            Environment.SetEnvironmentVariable("AzureFunctionAppName", oldFunc);
        }
    }

    [Fact]
    public async Task LeesRefreshToken_LeestUitOmgevingsvariabeleMetRolSpecifiekeNaam()
    {
        // Arrange: unieke rol per test om env-var-conflicten te voorkomen
        var uniqueRol = $"test-rol-{Guid.NewGuid()}";
        var expectedToken = $"token-{Guid.NewGuid()}";
        Environment.SetEnvironmentVariable($"SportlinkClubRefreshToken__{uniqueRol}", expectedToken);

        try
        {
            var tokenStore = new SportlinkClubAppSettingsTokenStore(
                NullLogger<SportlinkClubAppSettingsTokenStore>.Instance);

            // Act
            var result = tokenStore.LeesRefreshToken(uniqueRol);

            // Assert
            result.Should().Be(expectedToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"SportlinkClubRefreshToken__{uniqueRol}", null);
        }
    }
}

/// <summary>
/// Test-logger helper voor LogtNooitDeTokenwaarde-test.
/// </summary>
internal sealed class TestLogger : Microsoft.Extensions.Logging.ILogger<SportlinkClubClient>
{
    public List<string> AllLogs { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        AllLogs.Add(msg);
    }
}
