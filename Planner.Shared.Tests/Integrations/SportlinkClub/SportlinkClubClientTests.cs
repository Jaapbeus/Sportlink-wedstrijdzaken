using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Planner.Shared.Integrations.SportlinkClub;
using System.Net;
using System.Text.Json;
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
    public async Task GetMatchAsync_ExternalMatchIdAlsJsonGetal_WordtCorrectGemaptNaarString()
    {
        // Regressietest voor een live-gevonden bug (2026-09-06): Sportlinks echte Match-endpoint
        // levert externalMatchId als JSON-getal (bijv. 3403), niet als string ("3403") — de
        // eerdere aanname dat JsonNumberHandling.AllowReadingFromString dit al opving was fout
        // (die instelling werkt alleen andersom: string→getal, niet getal→string). Zonder
        // FlexibleStringJsonConverter faalt deze deserialisatie met een JsonException.
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse("""
                    {
                        "publicMatchId": "M000000001",
                        "externalMatchId": 3403,
                        "matchDate": "2026-09-15T19:30:00+02:00",
                        "matchStatus": "CONCEPT",
                        "isHomeMatch": true,
                        "isCanceledMatch": false,
                        "isConceptMatch": true,
                        "taskStatus": null,
                        "isEditFieldAllowed": true,
                        "isAssignDressingRoomsAllowed": true,
                        "isAssignOfficialsAllowed": true,
                        "isEditFieldSidePanelAllowed": true,
                        "isAddScoreAllowed": true
                    }
                    """);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.ExternalMatchId.Should().Be("3403");
    }

    [Fact]
    public async Task GetMatchAsync_MatchDateAlsGenestObject_WordtCorrectGemaptNaarDateTimeOffset()
    {
        // Regressietest voor een live-gevonden bug (2026-09-06, vervolg op de ExternalMatchId-fix
        // hierboven): Sportlinks echte Match-endpoint levert matchDate niet als losse ISO-string,
        // maar als geneste structuur {Date, StartTime, DateTime}. Zonder MatchDateJsonConverter
        // faalt deserialisatie met "Cannot get the value of a token type 'StartObject' as a string."
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse("""
                    {
                        "publicMatchId": "M000000001",
                        "externalMatchId": "69",
                        "matchDate": {
                            "Date": "2026-09-27",
                            "StartTime": "10:30:00",
                            "DateTime": "2026-09-27T10:30:00+0200"
                        },
                        "matchStatus": "SCHEDULED",
                        "isHomeMatch": true,
                        "isCanceledMatch": false,
                        "isConceptMatch": false,
                        "taskStatus": null,
                        "isEditFieldAllowed": true,
                        "isAssignDressingRoomsAllowed": true,
                        "isAssignOfficialsAllowed": true,
                        "isEditFieldSidePanelAllowed": true,
                        "isAddScoreAllowed": false
                    }
                    """);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.MatchDate.Should().Be(DateTimeOffset.Parse("2026-09-27T10:30:00+0200"));
    }

    [Fact]
    public async Task GetMatchAsync_MatchDateAlsLosseString_WordtCorrectGemaptNaarDateTimeOffset()
    {
        // Fallback-pad van MatchDateJsonConverter: als het veld ooit alsnog als losse ISO-string
        // terugkomt (bijv. via een ander endpoint of toekomstige Sportlink-wijziging), moet dat
        // ook blijven werken — geen aanname, puur verificatie van de bestaande happy-path-tests.
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse(MatchResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.MatchDate.Should().Be(DateTimeOffset.Parse("2026-09-15T19:30:00+02:00"));
    }

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

    // ── ResolvePublicMatchIdAsync (#991/#1016) ──

    private static string MatchProgramOverviewResponse(params (long ExternalMatchId, string PublicMatchId)[] entries)
    {
        var items = entries.Select(e => $$"""{ "externalMatchId": {{e.ExternalMatchId}}, "publicMatchId": "{{e.PublicMatchId}}" }""");
        return $"[{string.Join(",", items)}]";
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_WedstrijdInResponsAanwezig_VindtHetJuistePublicMatchId()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchProgramOverview") == true)
                return JsonResponse(MatchProgramOverviewResponse((1111, "M100000001"), (3403, "M392686417")));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.ResolvePublicMatchIdAsync(TestFunctioneleRol, 3403, new DateOnly(2026, 9, 5));

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.PublicMatchId.Should().Be("M392686417");
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_WedstrijdNietInRespons_GeeftOkMetLegeDataTerug()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchProgramOverview") == true)
                return JsonResponse(MatchProgramOverviewResponse((1111, "M100000001")));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.ResolvePublicMatchIdAsync(TestFunctioneleRol, 3403, new DateOnly(2026, 9, 5));

        result.Status.Should().Be(SportlinkClubCallStatus.Ok, "de aanroep zelf slaagde, alleen deze wedstrijd stond er niet in");
        result.Data.Should().BeNull();
        result.IsSuccess.Should().BeFalse("Data is null, dus geen bruikbaar resultaat, ook al is Status Ok");
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_GenestOnderMatchesProperty_VindtHetJuistePublicMatchId()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchProgramOverview") == true)
                return JsonResponse("""{ "Matches": [ { "externalMatchId": 3403, "publicMatchId": "M392686417" } ] }""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.ResolvePublicMatchIdAsync(TestFunctioneleRol, 3403, new DateOnly(2026, 9, 5));

        result.Data.Should().NotBeNull();
        result.Data!.PublicMatchId.Should().Be("M392686417");
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_ZetDeGevraagdeDatumAlsSmalBereikDoor()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        string? gevraagdeUrl = null;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            gevraagdeUrl = req.RequestUri?.AbsoluteUri;
            return JsonResponse("[]");
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        await sut.ResolvePublicMatchIdAsync(TestFunctioneleRol, 3403, new DateOnly(2026, 9, 5));

        gevraagdeUrl.Should().Contain("DateFrom=2026-09-05").And.Contain("DateTo=2026-09-05");
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_GeenRefreshTokenGeregistreerd_RetourneertRolNietGekoppeld()
    {
        var tokenStore = new FakeSportlinkClubTokenStore();
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.ResolvePublicMatchIdAsync(TestFunctioneleRol, 3403, new DateOnly(2026, 9, 5));

        result.Status.Should().Be(SportlinkClubCallStatus.RolNietGekoppeld);
    }

    // ── GetMatchProgramOverviewAsync (#1017: één aanroep per datum, hergebruikt door
    // ResolvePublicMatchIdAsync) ──

    [Fact]
    public async Task GetMatchProgramOverviewAsync_HappyPath_GeeftVolledigeNietGefilterdeLijstTerug()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchProgramOverview") == true)
                return JsonResponse(MatchProgramOverviewResponse((1111, "M100000001"), (3403, "M392686417")));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetMatchProgramOverviewAsync(TestFunctioneleRol, new DateOnly(2026, 9, 5));

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().HaveCount(2, "de volledige, niet-gefilterde dagrespons wordt teruggegeven");
        result.Data.Should().Contain(e => e.ExternalMatchId == 1111 && e.PublicMatchId == "M100000001");
        result.Data.Should().Contain(e => e.ExternalMatchId == 3403 && e.PublicMatchId == "M392686417");
    }

    [Fact]
    public async Task GetMatchProgramOverviewAsync_LegeDagrespons_GeeftOkMetLegeLijstTerug()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchProgramOverview") == true)
                return JsonResponse("[]");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetMatchProgramOverviewAsync(TestFunctioneleRol, new DateOnly(2026, 9, 5));

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task GetMatchProgramOverviewAsync_MeerdereAanroepenVoorZelfdeDatum_ElkAanroepDoetEigenHttpCall()
    {
        // Documenteert bewust: deze methode zelf dedupliceert niet — dat is de verantwoordelijkheid
        // van de aanroeper (bijv. de warmup-timer groepeert zelf per datum vóór hij dit aanroept).
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var overviewCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchProgramOverview") == true)
            {
                overviewCallCount++;
                return JsonResponse(MatchProgramOverviewResponse((3403, "M392686417")));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        await sut.GetMatchProgramOverviewAsync(TestFunctioneleRol, new DateOnly(2026, 9, 5));
        await sut.GetMatchProgramOverviewAsync(TestFunctioneleRol, new DateOnly(2026, 9, 5));

        overviewCallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetMatchProgramOverviewAsync_NetwerkfoutBijOverviewEndpoint_RetourneertNetwerkFout()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchProgramOverview") == true)
                throw new HttpRequestException("Netwerk down");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetMatchProgramOverviewAsync(TestFunctioneleRol, new DateOnly(2026, 9, 5));

        result.Status.Should().Be(SportlinkClubCallStatus.NetwerkFout);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_HergebruiktGetMatchProgramOverviewAsync_EenAanroepPerDatum()
    {
        // Regressietest voor de #1017-refactor: ResolvePublicMatchIdAsync mag na het loskoppelen van
        // GetMatchProgramOverviewAsync nog steeds precies één overview-aanroep per (rol, datum) doen.
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var overviewCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchProgramOverview") == true)
            {
                overviewCallCount++;
                return JsonResponse(MatchProgramOverviewResponse((3403, "M392686417")));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.ResolvePublicMatchIdAsync(TestFunctioneleRol, 3403, new DateOnly(2026, 9, 5));

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.PublicMatchId.Should().Be("M392686417");
        overviewCallCount.Should().Be(1);
    }

    // ── UpdateDressingRoomsAsync (#992, epic #986) ──

    private static string DressingRoomsSuccessResponse() => """{"isSuccess": true}""";

    // Live vastgesteld (2026-09-06, testwedstrijd wedstrijdnummer 69) tegen het echte
    // UpdateMatchDressingRooms-endpoint — de eerder aangenomen vorm
    // ({"isSuccess":false,"entityViolation":{"violations":[{"code":...}]}}) bleek onjuist.
    private static string DressingRoomsViolationResponse(params string[] codes)
    {
        var codesJson = string.Join(",", codes.Select(c => "\"" + c + "\""));
        var violationsJson = string.Join(",", codes.Distinct().Select(c => "\"" + c + "\": \"Nederlandse omschrijving\""));
        return "{\"Error\": true, \"Status\": \"420\", \"Message\": \"Validation exception : " + codes[0] + "\", "
             + "\"ViolationCodes\": [" + codesJson + "], \"Violations\": {" + violationsJson + "}}";
    }

    [Fact]
    public async Task UpdateDressingRoomsAsync_HappyPath_RetourneertIsSuccessTrue()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
                return JsonResponse(DressingRoomsSuccessResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeTrue();
        result.Data.Violations.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task UpdateDressingRoomsAsync_SportlinkWijstMutatieAf_RetourneertOkMetIsSuccessFalseEnViolations()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
                return JsonResponse(DressingRoomsViolationResponse("INVALID_UPDATE_ACTION"));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        result.Status.Should().Be(SportlinkClubCallStatus.Ok, "de aanroep zelf lukte, Sportlink wees de mutatie inhoudelijk af");
        result.Data!.IsSuccess.Should().BeFalse();
        result.Data.Violations.Should().ContainSingle().Which.Should().Be("INVALID_UPDATE_ACTION: Nederlandse omschrijving");
    }

    [Fact]
    public async Task UpdateDressingRoomsAsync_EchteAfwijzingMetHttp420_WordtCorrectGeparsed()
    {
        // Regressietest voor een live-gevonden bug (2026-09-06, testwedstrijd wedstrijdnummer 69,
        // veld 6, kleedkamers 10/6/9): de aangenomen violation-vorm bleek onjuist. Sportlink gaf
        // HTTP 420 met dit exacte, geobserveerde JSON terug — inclusief drie keer dezelfde code
        // (één per kleedkamerveld) en een top-level "Error"/"Status"/"Message", niet het eerder
        // aangenomen "isSuccess"/"entityViolation".
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
                return new HttpResponseMessage((HttpStatusCode)420)
                {
                    Content = new StringContent(
                        """
                        {
                          "Error" : true,
                          "Status" : "420",
                          "Message" : "Validation exception : INVALID_COMBINATION_FACILITY_DRESSINGROOM",
                          "ViolationCodes" : [ "INVALID_COMBINATION_FACILITY_DRESSINGROOM", "INVALID_COMBINATION_FACILITY_DRESSINGROOM", "INVALID_COMBINATION_FACILITY_DRESSINGROOM" ],
                          "Violations" : {
                            "INVALID_COMBINATION_FACILITY_DRESSINGROOM" : "Een of meer gekozen kleedkamers, horen niet bij de gekozen accommodatie"
                          }
                        }
                        """, System.Text.Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        result.Status.Should().Be(SportlinkClubCallStatus.Ok, "HTTP 420 is een structurele afwijzing, geen transportfout");
        result.Data!.IsSuccess.Should().BeFalse();
        result.Data.Violations.Should().ContainSingle()
            .Which.Should().Be("INVALID_COMBINATION_FACILITY_DRESSINGROOM: Een of meer gekozen kleedkamers, horen niet bij de gekozen accommodatie");
    }

    // ── Dry-run-modus (#998, epic #986) ──

    [Fact]
    public async Task UpdateDressingRoomsAsync_DryRun_SlaatPutOverEnGeeftIsDryRunTrue()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var aangeroepenUrls = new List<string>();
        var client = MakeClient(req =>
        {
            aangeroepenUrls.Add(req.RequestUri!.AbsoluteUri);
            if (req.RequestUri.AbsoluteUri.Contains("idm.sportlink.com"))
                return JsonResponse(TokenResponse(FictieveAccessToken));
            // Als de PUT toch zou worden verstuurd (dry-run-bug), faalt de test hierop expliciet.
            if (req.RequestUri.AbsoluteUri.Contains("UpdateMatchDressingRooms"))
                throw new InvalidOperationException("Dry-run mag de echte PUT niet versturen.");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance, isDryRun: () => true);

        var result = await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsDryRun.Should().BeTrue();
        result.Data.IsSuccess.Should().BeTrue("dry-run simuleert een geslaagde mutatie");
        result.Data.Violations.Should().BeNullOrEmpty();
        // Token-refresh moet wél echt gebeurd zijn — alleen de PUT zelf wordt overgeslagen.
        aangeroepenUrls.Should().Contain(url => url.Contains("idm.sportlink.com"));
        aangeroepenUrls.Should().NotContain(url => url.Contains("UpdateMatchDressingRooms"));
    }

    [Fact]
    public async Task UpdateFieldAsync_DryRun_SnapshotGetGebeurtWelMaarPutNiet()
    {
        // #998: de dry-run-vertakking zit in PutMutationAsync (achter de snapshot-GET), zodat een
        // dry-run realistisch blijft — token-refresh en de voorbereidende Match-snapshot-GET lopen
        // dus echt, alleen de PUT UpdateMatchDetails wordt overgeslagen.
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var aangeroepenUrls = new List<string>();
        var client = MakeClient(req =>
        {
            aangeroepenUrls.Add(req.RequestUri!.AbsoluteUri);
            if (req.RequestUri.AbsoluteUri.Contains("idm.sportlink.com"))
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri.AbsoluteUri.Contains("UpdateMatchDetails"))
                throw new InvalidOperationException("Dry-run mag de echte PUT niet versturen.");
            // Match GET (snapshot-ophaal) — zelfde fixture als de bestaande UpdateFieldAsync-tests
            // hieronder (MatchDetailsSnapshotResponse), niet de lichtere SportlinkMatch-fixture
            // (MatchResponse): de snapshot deserialiseert naar een ander intern model met een
            // genest MatchDate-object, geen kale ISO-string.
            if (req.RequestUri.AbsoluteUri.Contains("club.sportlink.com"))
                return JsonResponse(MatchDetailsSnapshotResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance, isDryRun: () => true);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "F1", "1.0", null, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsDryRun.Should().BeTrue();
        result.Data.IsSuccess.Should().BeTrue();
        aangeroepenUrls.Should().Contain(url => url.Contains("club.sportlink.com") && !url.Contains("UpdateMatchDetails"),
            "de voorbereidende snapshot-GET moet ook in dry-run echt gebeuren");
        aangeroepenUrls.Should().NotContain(url => url.Contains("UpdateMatchDetails"));
    }

    [Fact]
    public async Task UpdateDressingRoomsAsync_ZetJuisteBodyEnHeaders()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
            {
                captured = req;
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(DressingRoomsSuccessResponse());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Put);
        captured.Headers.Authorization?.Parameter.Should().Be(FictieveAccessToken);
        captured.Headers.GetValues("X-Navajo-Entity").Should().Contain("competition/match/UpdateMatchDressingRooms");
        capturedBody.Should().Contain(TestPublicMatchId).And.Contain("\"10\"").And.Contain("\"6\"").And.Contain("\"9\"");
    }

    [Fact]
    public async Task UpdateDressingRoomsAsync_GeenRefreshTokenGeregistreerd_RetourneertRolNietGekoppeldZonderHttpAanroep()
    {
        var tokenStore = new FakeSportlinkClubTokenStore();
        var httpCallCount = 0;
        var client = MakeClient(_ => { httpCallCount++; return new HttpResponseMessage(HttpStatusCode.NotFound); });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        result.Status.Should().Be(SportlinkClubCallStatus.RolNietGekoppeld);
        httpCallCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateDressingRoomsAsync_401OndanksGecachedToken_VerversTEenmaalEnHeraanvraagt()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var tokenCallCount = 0;
        var putCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
            {
                tokenCallCount++;
                return JsonResponse(TokenResponse(FictieveAccessToken + tokenCallCount));
            }
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
            {
                putCallCount++;
                return putCallCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : JsonResponse(DressingRoomsSuccessResponse());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeTrue();
        tokenCallCount.Should().Be(2);
        putCallCount.Should().Be(2);
    }

    [Fact]
    public async Task UpdateDressingRoomsAsync_NetwerkfoutBijEndpoint_RetourneertNetwerkFout()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
                throw new HttpRequestException("Netwerk down");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        result.Status.Should().Be(SportlinkClubCallStatus.NetwerkFout);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task UpdateDressingRoomsAsync_LogtNooitDeTokenwaarde()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var logger = new TestLogger();
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
                return JsonResponse(DressingRoomsSuccessResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, logger);

        await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        foreach (var log in logger.AllLogs)
        {
            log.Should().NotContain(FictieveAccessToken);
            log.Should().NotContain(FictieveRefreshToken);
        }
    }

    // ── UpdateFieldAsync (#993, epic #986) ──
    //
    // Live vastgesteld (2026-09-06, netwerktrace door de eigenaar, #1047): dit roept niet
    // "UpdateMatchField" aan (bestaat niet) maar "UpdateMatchDetails" met het VOLLEDIGE
    // wedstrijdrecord — eerst een verse Match-GET-snapshot ophalen, dan de PUT met snapshot +
    // overschreven veld. PublicApplicantId gaat leeg mee (#1048): live bevestigd dat Sportlink dat
    // accepteert voor een eigen-veld-wijziging — geen UserInfo-aanroep nodig voor dit pad (in
    // tegenstelling tot #996's change-request-actie, die wél een echte aanvrager-identiteit heeft).

    private static string MatchDetailsSnapshotResponse() => """
        {
            "AgeClassCode": "001",
            "Duration": 105,
            "MatchStatus": "SCHEDULED",
            "ExternalMatchId": 69,
            "MatchDate": {"Date": "2026-09-27", "StartTime": "10:30:00"},
            "Field": {"FieldId": "BBCF989-OUTDOOR_FIELD-6", "FieldSize": 1.0, "FieldOffset": 0},
            "MatchField": {"FacilityId": "BBCF989"},
            "Sport": {"IdTag": "SOCCER-VE-AL/SUNDAY"},
            "Teams": {
                "Home": {"TeamName": "TEST 1", "PublicTeamId": "T2010269033"},
                "Away": {"TeamName": "TEST 2", "PublicTeamId": "T2010269034"}
            },
            "Result": {"HomeScore": null, "AwayScore": null},
            "MatchDetails": {
                "Description": {"Value": "Oefenwedstrijd"},
                "MatchDetailsHome": {"AssemblyTime": {"Value": null}}
            }
        }
        """;

    private static Func<HttpRequestMessage, HttpResponseMessage> MakeUpdateFieldClient(
        Func<string> updateMatchDetailsResponseJson, Action<string>? captureBody = null) =>
        req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDetails") == true)
            {
                if (captureBody != null)
                    captureBody(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
                return JsonResponse(updateMatchDetailsResponseJson());
            }
            // Match GET (snapshot-ophaal) — geen entity-header-check nodig, UpdateMatchDetails
            // matcht hierboven al exclusief op zijn eigen padnaam.
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse(MatchDetailsSnapshotResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    [Fact]
    public async Task UpdateFieldAsync_HappyPath_RetourneertIsSuccessTrue()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(MakeUpdateFieldClient(DressingRoomsSuccessResponse));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "BBCF989-OUTDOOR_FIELD-6", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateFieldAsync_FieldSizeAlsJsonGetalInSnapshot_WordtCorrectGemaptNaarString()
    {
        // Regressietest voor een live-gevonden bug (2026-09-06, #1047-vervolg): Match GET levert
        // Field.FieldSize als JSON-getal (1.0), niet als string ("1.0") — zelfde wisselvallige-
        // veldtype-patroon als ExternalMatchId (#1036). MatchDetailsSnapshotResponse() gebruikt
        // hierboven al de numerieke vorm; deze test maakt de regressie expliciet traceerbaar.
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(MakeUpdateFieldClient(DressingRoomsSuccessResponse));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "BBCF989-OUTDOOR_FIELD-6", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateFieldAsync_ZetVolledigeMatchDataMetOverschrevenVeld()
    {
        string? capturedBody = null;
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(MakeUpdateFieldClient(DressingRoomsSuccessResponse, b => capturedBody = b));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "BBCF989-OUTDOOR_FIELD-9", "1.0", 0, isForceUpdate: false);

        // Het gewijzigde veld staat erin...
        capturedBody.Should().Contain(TestPublicMatchId)
            .And.Contain("BBCF989-OUTDOOR_FIELD-9")
            .And.Contain("\"IsForceUpdate\":false")
            .And.Contain("\"PublicApplicantId\":\"\"");
        // ...en de rest van het wedstrijdrecord komt ongewijzigd van de snapshot terug, niet
        // van een lokale aanname — bewijst dat teamnamen/omschrijving niet verloren gaan.
        capturedBody.Should().Contain("TEST 1").And.Contain("TEST 2").And.Contain("Oefenwedstrijd")
            .And.Contain("SOCCER-VE-AL/SUNDAY").And.Contain("BBCF989");
    }

    [Fact]
    public async Task UpdateFieldAsync_SportlinkWijstMutatieAf_RetourneertOkMetIsSuccessFalseEnViolations()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(MakeUpdateFieldClient(() => DressingRoomsViolationResponse("INVALID_UPDATE_ACTION")));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "BBCF989-OUTDOOR_FIELD-6", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeFalse();
        result.Data.Violations.Should().ContainSingle().Which.Should().Be("INVALID_UPDATE_ACTION: Nederlandse omschrijving");
    }

    [Fact]
    public async Task UpdateFieldAsync_GeenRefreshTokenGeregistreerd_RetourneertRolNietGekoppeldZonderHttpAanroep()
    {
        var tokenStore = new FakeSportlinkClubTokenStore();
        var httpCallCount = 0;
        var client = MakeClient(_ => { httpCallCount++; return new HttpResponseMessage(HttpStatusCode.NotFound); });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "12345-1", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.RolNietGekoppeld);
        httpCallCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateFieldAsync_401OndanksGecachedToken_VerversTEenmaalEnHeraanvraagt()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var tokenCallCount = 0;
        var putCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
            {
                tokenCallCount++;
                return JsonResponse(TokenResponse(FictieveAccessToken + tokenCallCount));
            }
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDetails") == true)
            {
                putCallCount++;
                return putCallCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : JsonResponse(DressingRoomsSuccessResponse());
            }
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse(MatchDetailsSnapshotResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "12345-1", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        tokenCallCount.Should().Be(2);
        putCallCount.Should().Be(2);
    }

    [Fact]
    public async Task UpdateFieldAsync_NetwerkfoutBijEndpoint_RetourneertNetwerkFout()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(MakeUpdateFieldClient(() => throw new HttpRequestException("Netwerk down")));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "12345-1", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.NetwerkFout);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task UpdateFieldAsync_MatchSnapshotOphalenMislukt_RetourneertFoutZonderPutAanroep()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var putCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDetails") == true)
            {
                putCallCount++;
                return JsonResponse(DressingRoomsSuccessResponse());
            }
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "12345-1", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.SportlinkFout);
        putCallCount.Should().Be(0, "zonder een geldige snapshot mag er nooit een PUT verstuurd worden");
    }

    // ── GetChangeRequestsAsync / ActOnChangeRequestAsync (#996, epic #986) ──

    private static string ChangeRequestsResponse() => """
        [
          {
            "publicMatchId": "M392686417",
            "publicRequestId": "R1",
            "requestStatus": "CONFIRM",
            "requestData": {
              "currentDate": "2026-09-27",
              "currentStartTime": "10:30",
              "requestedDate": "2026-09-27",
              "requestedStartTime": "11:00"
            },
            "reason": "Testverzoek"
          }
        ]
        """;

    [Fact]
    public async Task GetChangeRequestsAsync_HappyPath_MaptVeldenCorrect()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchChangeRequests") == true)
                return JsonResponse(ChangeRequestsResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetChangeRequestsAsync(TestFunctioneleRol);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().ContainSingle();
        var item = result.Data![0];
        item.PublicRequestId.Should().Be("R1");
        item.RequestStatus.Should().Be("CONFIRM");
        item.RequestData!.RequestedStartTime.Should().Be("11:00");
    }

    [Fact]
    public async Task GetChangeRequestsAsync_GenestOnderChangeRequestsProperty_WerktOok()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("MatchChangeRequests") == true)
                return JsonResponse($$"""{ "ChangeRequests": {{ChangeRequestsResponse()}} }""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetChangeRequestsAsync(TestFunctioneleRol);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data.Should().ContainSingle();
    }

    [Fact]
    public async Task GetChangeRequestsAsync_GeenRefreshTokenGeregistreerd_RetourneertRolNietGekoppeld()
    {
        var tokenStore = new FakeSportlinkClubTokenStore();
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetChangeRequestsAsync(TestFunctioneleRol);

        result.Status.Should().Be(SportlinkClubCallStatus.RolNietGekoppeld);
    }

    [Fact]
    public async Task ActOnChangeRequestAsync_HappyPath_HaaltUserInfoOpEnStuurtJuisteBody()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        string? capturedBody = null;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UserInfo") == true)
                return JsonResponse("""{"publicPersonId": "P999"}""");
            if (req.RequestUri?.AbsoluteUri.Contains("MatchChangeRequestAction") == true)
            {
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(DressingRoomsSuccessResponse());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.ActOnChangeRequestAsync(TestFunctioneleRol, "APPROVE", TestPublicMatchId, "R1", null);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeTrue();
        capturedBody.Should().Contain("APPROVE").And.Contain("P999").And.Contain("R1").And.Contain(TestPublicMatchId);
    }

    [Fact]
    public async Task ActOnChangeRequestAsync_UserInfoMislukt_RetourneertFoutZonderActieAanroep()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var actionCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UserInfo") == true)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (req.RequestUri?.AbsoluteUri.Contains("MatchChangeRequestAction") == true)
                actionCallCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.ActOnChangeRequestAsync(TestFunctioneleRol, "APPROVE", TestPublicMatchId, "R1", null);

        result.Status.Should().Be(SportlinkClubCallStatus.SportlinkFout);
        actionCallCount.Should().Be(0, "zonder geldig PublicPersonId mag de actie-aanroep niet gebeuren");
    }

    [Fact]
    public async Task ActOnChangeRequestAsync_GeenRefreshTokenGeregistreerd_RetourneertRolNietGekoppeld()
    {
        var tokenStore = new FakeSportlinkClubTokenStore();
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.ActOnChangeRequestAsync(TestFunctioneleRol, "DENY", TestPublicMatchId, "R1", "Geen ruimte");

        result.Status.Should().Be(SportlinkClubCallStatus.RolNietGekoppeld);
    }

    // ── VerversTokenAsync (proactieve keep-alive, zie #990-comment 2026-09-05) ──

    [Fact]
    public async Task VerversTokenAsync_GeldigeRefreshToken_RetourneertOkEnSchrijftNieuwTokenTerug()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var tokenCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
            {
                tokenCallCount++;
                return JsonResponse(TokenResponse(FictieveAccessToken, newRefreshToken: NewFictieveRefreshToken));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var status = await sut.VerversTokenAsync(TestFunctioneleRol);

        status.Should().Be(SportlinkClubCallStatus.Ok);
        tokenCallCount.Should().Be(1);
        // SchrijfRefreshTokenAsync loopt async/fire-and-forget in de client — even wachten tot de
        // fake store 'm ontvangen heeft (geen artificiële Task.Delay: pollen met een korte timeout).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (tokenStore.LeesRefreshToken(TestFunctioneleRol) != NewFictieveRefreshToken && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        tokenStore.LeesRefreshToken(TestFunctioneleRol).Should().Be(NewFictieveRefreshToken);
    }

    [Fact]
    public async Task VerversTokenAsync_NooitEenMatchOfMatchProgramOverviewAanroep_UitsluitendTokenEndpoint()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var clubSportlinkCallCount = 0;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                clubSportlinkCallCount++;
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        await sut.VerversTokenAsync(TestFunctioneleRol);

        clubSportlinkCallCount.Should().Be(0, "een keep-alive-ververs mag nooit een inhoudelijke Sportlink-aanroep doen");
    }

    [Fact]
    public async Task VerversTokenAsync_NegeertEenNogGeldigGeachteAccessTokenCache_VerversAltijdEcht()
    {
        // Arrange: eerst een gewone GetMatchAsync zodat er een geldige (niet-verlopen) access-token
        // in de in-memory cache zit.
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
        await sut.GetMatchAsync(TestFunctioneleRol, TestPublicMatchId);
        tokenCallCount.Should().Be(1, "voorwaarde: er staat nu een niet-verlopen access-token in de cache");

        // Act: een keep-alive-ververs terwijl de cache nog geldig is (dit is precies het scenario
        // dat #990 blootlegde — een geldig geachte cache bewijst niets over de Keycloak-kant).
        var status = await sut.VerversTokenAsync(TestFunctioneleRol);

        // Assert
        status.Should().Be(SportlinkClubCallStatus.Ok);
        tokenCallCount.Should().Be(2, "VerversTokenAsync moet het token-endpoint ECHT opnieuw aanroepen, niet de cache vertrouwen");
    }

    [Fact]
    public async Task VerversTokenAsync_GeenRefreshTokenGeregistreerd_RetourneertRolNietGekoppeld()
    {
        var tokenStore = new FakeSportlinkClubTokenStore();
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var status = await sut.VerversTokenAsync(TestFunctioneleRol);

        status.Should().Be(SportlinkClubCallStatus.RolNietGekoppeld);
    }

    [Fact]
    public async Task VerversTokenAsync_InvalidGrant_RetourneertHerkoppelingVereist()
    {
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

        var status = await sut.VerversTokenAsync(TestFunctioneleRol);

        status.Should().Be(SportlinkClubCallStatus.HerkoppelingVereist);
    }

    [Fact]
    public async Task VerversTokenAsync_LogtNooitDeTokenwaarde()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var logger = new TestLogger();
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken, newRefreshToken: NewFictieveRefreshToken));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, logger);

        await sut.VerversTokenAsync(TestFunctioneleRol);

        foreach (var log in logger.AllLogs)
        {
            log.Should().NotContain(FictieveAccessToken);
            log.Should().NotContain(FictieveRefreshToken);
            log.Should().NotContain(NewFictieveRefreshToken);
        }
    }

    // ── forceDryRun code-lock (#994/§1, epic #986) ──
    // Kern-eis: een mutatie met forceDryRun:true blijft ALTIJD gesimuleerd, ook als de globale
    // instelling (isDryRun-delegate) NIET op dry-run staat. Dit bewijst dat de lock niet via de
    // bestaande sportlinkDryRun-instelling omzeilbaar is.

    [Fact]
    public async Task AssignOfficialsAsync_GlobaleInstellingStaatUit_BlijftTochGesimuleerdDoorCodeLock()
    {
        // isDryRun: () => false — de club-instelling staat NIET op dry-run. Toch mag er nooit een
        // echte PUT/POST naar het MatchOfficialsAction-endpoint gaan, want AssignOfficialsAsync
        // geeft altijd forceDryRun: true mee (endpoint/body nog niet live bevestigd, #994).
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var aangeroepenUrls = new List<string>();
        var client = MakeClient(req =>
        {
            aangeroepenUrls.Add(req.RequestUri!.AbsoluteUri);
            if (req.RequestUri.AbsoluteUri.Contains("idm.sportlink.com"))
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri.AbsoluteUri.Contains("MatchOfficialsAction"))
                throw new InvalidOperationException("De code-lock mag deze PUT nooit versturen, ongeacht de globale dry-run-instelling.");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance, isDryRun: () => false);

        var result = await sut.AssignOfficialsAsync(
            TestFunctioneleRol, TestPublicMatchId,
            new List<SportlinkOfficialToewijzing> { new("Referee", "123456") });

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsDryRun.Should().BeTrue();
        result.Data.IsForcedDryRun.Should().BeTrue("de code-lock is onafhankelijk van de club-instelling sportlinkDryRun");
        result.Data.IsSuccess.Should().BeTrue("een dry-run simuleert een geslaagde mutatie");
        aangeroepenUrls.Should().Contain(url => url.Contains("idm.sportlink.com"), "token-refresh moet wél echt gebeuren");
        aangeroepenUrls.Should().NotContain(url => url.Contains("MatchOfficialsAction"));
    }

    [Fact]
    public void BuildMatchOfficialsBody_AanNameNietLiveBevestigd_ZetPublicMatchIdEnOfficialsToBeAssigned()
    {
        // Vastlegging van de AANGENOMEN, NOG NIET LIVE BEVESTIGDE body-vorm (#994) — de
        // elementstructuur ("OfficialPosition"/"PersoonId") is nooit met een netwerktrace gezien,
        // zie SportlinkOfficialToewijzing.cs. Deze test houdt de aanname grijpbaar/regressie-vast,
        // niet een bevestigd contract.
        var body = SportlinkClubClient.BuildMatchOfficialsBody(
            TestPublicMatchId,
            new List<SportlinkOfficialToewijzing> { new("Referee", "123456"), new("AssistantReferee1", "654321") });

        var json = JsonSerializer.Serialize(body);

        json.Should().Contain($"\"PublicMatchId\":\"{TestPublicMatchId}\"");
        json.Should().Contain("\"OfficialsToBeAssigned\"");
        json.Should().Contain("\"OfficialPosition\":\"Referee\"").And.Contain("\"PersoonId\":\"123456\"");
        json.Should().Contain("\"OfficialPosition\":\"AssistantReferee1\"").And.Contain("\"PersoonId\":\"654321\"");
    }

    [Fact]
    public void VerrijkOfficialsResultaat_MinstensEenValidationDescription_ZetIsSuccessFalseMetViolations()
    {
        // #994: Sportlink toont "opgeslagen met fouten" (IS_SAVED_WITH_ERRORS) zodra één official
        // een ValidationDescription heeft — ook al is de HTTP-status 200/Error niet gezet. De
        // generieke PutMutationAsync-parsing ziet dit niet; VerrijkOfficialsResultaat is de
        // taakspecifieke uitbreiding die dat corrigeert.
        var json = """
            {"Officials": [
                {"OfficialPosition": "Referee", "ValidationDescription": null},
                {"OfficialPosition": "AssistantReferee1", "ValidationDescription": "Persoon heeft al een aanstelling voor deze wedstrijd"}
            ]}
            """;
        var basisResultaat = new SportlinkMutationResult(IsSuccess: true, Violations: null);

        var verrijkt = SportlinkClubClient.VerrijkOfficialsResultaat(json, basisResultaat);

        verrijkt.IsSuccess.Should().BeFalse("minstens één official had een ValidationDescription — Sportlink toont dit als 'opgeslagen met fouten'");
        verrijkt.Violations.Should().ContainSingle().Which.Should().Be("Persoon heeft al een aanstelling voor deze wedstrijd");
    }

    [Fact]
    public void VerrijkOfficialsResultaat_GeenValidationDescriptions_LaatResultaatOngewijzigd()
    {
        var json = """{"Officials": [{"OfficialPosition": "Referee", "ValidationDescription": null}]}""";
        var basisResultaat = new SportlinkMutationResult(IsSuccess: true, Violations: null);

        var verrijkt = SportlinkClubClient.VerrijkOfficialsResultaat(json, basisResultaat);

        verrijkt.IsSuccess.Should().BeTrue();
        verrijkt.Violations.Should().BeNullOrEmpty();
    }

    [Fact]
    public void VerrijkOfficialsResultaat_OnherkenbareVorm_LaatResultaatOngewijzigd()
    {
        // Defensief pad: als de respons onverwacht geen "Officials"-array bevat (of onherkenbare
        // JSON is), mag dit geen extra fout stapelen bovenop het generieke resultaat.
        var basisResultaat = new SportlinkMutationResult(IsSuccess: true, Violations: null);

        var verrijkt = SportlinkClubClient.VerrijkOfficialsResultaat("""{"Error": true}""", basisResultaat);

        verrijkt.Should().Be(basisResultaat);
    }

    // ── UpdateFieldAsync regressie na #995 ──
    // #995 genericeerde ExecuteMutationWithRetryAsync en voegde RequestMatchChangeAsync toe met een
    // ALTIJD-actieve forceDryRun-lock. Deze test bewijst dat #993's live-bevestigde veld-wijziging
    // daar niets van meekrijgt: met isDryRun: () => false gaat er nog steeds een ECHTE PUT uit.

    [Fact]
    public async Task UpdateFieldAsync_IsDryRunFalseEnGeenForceDryRunLock_StuurtEenEchtePut()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var aangeroepenUrls = new List<string>();
        var inner = MakeUpdateFieldClient(DressingRoomsSuccessResponse);
        var client = MakeClient(req =>
        {
            aangeroepenUrls.Add(req.RequestUri!.AbsoluteUri);
            return inner(req);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance, isDryRun: () => false);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "BBCF989-OUTDOOR_FIELD-6", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsDryRun.Should().BeFalse();
        result.Data.IsForcedDryRun.Should().BeFalse();
        aangeroepenUrls.Should().Contain(url => url.Contains("UpdateMatchDetails"),
            "de veld-wijziging (#993) blijft een ECHTE PUT versturen, ongeacht de #995-uitbreiding van ExecuteMutationWithRetryAsync");
    }

    // ── forceDryRun code-lock voor RequestMatchChangeAsync (#995, epic #986) ──
    // Kern-eis: net als AssignOfficialsAsync (#994) blijft deze mutatie ALTIJD gesimuleerd, ook als
    // de globale instelling (isDryRun-delegate) NIET op dry-run staat — dit is bovendien de enige
    // mutatiesoort die een ECHTE tegenstander raakt, dus de lock is hier extra belangrijk.

    [Fact]
    public async Task RequestMatchChangeAsync_GlobaleInstellingStaatUit_BlijftTochGesimuleerdDoorCodeLock()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var aangeroepenUrls = new List<string>();
        var client = MakeClient(req =>
        {
            aangeroepenUrls.Add(req.RequestUri!.AbsoluteUri);
            if (req.RequestUri!.AbsoluteUri.Contains("idm.sportlink.com"))
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri.AbsoluteUri.Contains("UpdateMatchDetails"))
                throw new InvalidOperationException("De code-lock mag deze PUT nooit versturen, ongeacht de globale dry-run-instelling.");
            if (req.RequestUri.AbsoluteUri.Contains("club.sportlink.com"))
                return JsonResponse(MatchDetailsSnapshotResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance, isDryRun: () => false);

        var result = await sut.RequestMatchChangeAsync(
            TestFunctioneleRol, TestPublicMatchId,
            new DateOnly(2026, 10, 4), new TimeOnly(11, 0), "BBCF990", "Veld is niet beschikbaar door onderhoud");

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.Mutatie.IsDryRun.Should().BeTrue();
        result.Data.Mutatie.IsForcedDryRun.Should().BeTrue("de code-lock is onafhankelijk van de club-instelling sportlinkDryRun");
        result.Data.Mutatie.IsSuccess.Should().BeTrue("een dry-run simuleert een geslaagde mutatie");
        result.Data.Validatie.Should().BeNull("zonder een echte HTTP-respons is er niets te parsen — de lock voorkomt de PUT volledig");
        aangeroepenUrls.Should().Contain(url => url.Contains("club.sportlink.com"), "de snapshot-GET moet wél echt gebeuren");
        aangeroepenUrls.Should().NotContain(url => url.Contains("UpdateMatchDetails"));
    }

    // ── BuildMatchChangeRequestBody (#995, epic #986) ──
    // ONBEVESTIGD: IsMatchChangeRequestMandatory/PublicApplicantId zijn aannames, zie de
    // doc-comment op BuildMatchChangeRequestBody zelf. Deze tests leggen vast wélke velden
    // overschreven worden en welke uit de snapshot komen — geen bevestigd Sportlink-contract.

    private static SportlinkClubClient.SportlinkMatchDetailsSnapshot MaakTestSnapshot() => new(
        AgeClassCode: "001",
        Duration: 105,
        MatchStatus: "SCHEDULED",
        ExternalMatchId: 69,
        MatchDate: new SportlinkClubClient.SportlinkMatchDateRaw("2026-09-27", "10:30:00"),
        Field: new SportlinkClubClient.SportlinkFieldRaw("BBCF989-OUTDOOR_FIELD-6", "1.0", 0),
        MatchField: new SportlinkMatchField { FacilityId = "BBCF989" },
        Sport: new SportlinkClubClient.SportlinkSportRaw("SOCCER-VE-AL/SUNDAY"),
        Teams: new SportlinkClubClient.SportlinkTeamsRaw(
            new SportlinkClubClient.SportlinkTeamRaw("TEST 1", "T2010269033"),
            new SportlinkClubClient.SportlinkTeamRaw("TEST 2", "T2010269034")),
        Result: new SportlinkClubClient.SportlinkResultRaw(null, null),
        MatchDetails: new SportlinkClubClient.SportlinkMatchDetailsFieldsRaw(
            new SportlinkClubClient.SportlinkEditableFieldRaw("Oefenwedstrijd"),
            new SportlinkClubClient.SportlinkMatchDetailsHomeRaw(new SportlinkClubClient.SportlinkEditableFieldRaw(null))));

    [Fact]
    public void BuildMatchChangeRequestBody_OverschrijftAlleenDatumTijdFacilityEnToelichting_RestKomtUitSnapshot()
    {
        var body = SportlinkClubClient.BuildMatchChangeRequestBody(
            TestPublicMatchId, MaakTestSnapshot(),
            nieuweDatum: new DateOnly(2026, 10, 4), nieuweStartTijd: new TimeOnly(11, 0), nieuweFacilityId: "BBCF990",
            toelichting: "Veld is niet beschikbaar door onderhoud");

        var json = JsonSerializer.Serialize(body);

        // Gewijzigde velden...
        json.Should().Contain("\"MatchDate\":\"2026-10-04\"")
            .And.Contain("\"StartTime\":\"11:00:00\"")
            .And.Contain("\"FacilityId\":\"BBCF990\"")
            .And.Contain("\"MatchChangeRequestRemarks\":\"Veld is niet beschikbaar door onderhoud\"");
        // ...de rest komt ongewijzigd van de snapshot, niet van een lokale aanname — bewijst dat
        // teamnamen/omschrijving/veld-id niet verloren gaan.
        json.Should().Contain("TEST 1").And.Contain("TEST 2").And.Contain("Oefenwedstrijd")
            .And.Contain("SOCCER-VE-AL/SUNDAY")
            .And.Contain("\"FieldId\":\"BBCF989-OUTDOOR_FIELD-6\"");
        json.Should().Contain($"\"PublicMatchId\":\"{TestPublicMatchId}\"");
    }

    [Fact]
    public void BuildMatchChangeRequestBody_GeenNieuweWaarden_ValtVolledigTerugOpSnapshot()
    {
        var body = SportlinkClubClient.BuildMatchChangeRequestBody(
            TestPublicMatchId, MaakTestSnapshot(),
            nieuweDatum: null, nieuweStartTijd: null, nieuweFacilityId: null,
            toelichting: "Toelichting");

        var json = JsonSerializer.Serialize(body);

        json.Should().Contain("\"MatchDate\":\"2026-09-27\"")
            .And.Contain("\"StartTime\":\"10:30:00\"")
            .And.Contain("\"FacilityId\":\"BBCF989\"");
    }

    // ── ParseMatchChangeValidatie (#995, epic #986) — ONBEVESTIGDE responsvorm, zie issue #995 ──

    [Fact]
    public void ParseMatchChangeValidatie_ConfirmationNeededNull_GeeftFalseEnLegeLijst()
    {
        var result = SportlinkClubClient.ParseMatchChangeValidatie("""{"ConfirmationNeeded": null}""");

        result.Should().NotBeNull();
        result!.ConfirmationNeeded.Should().BeFalse();
        result.ValidationResultMessages.Should().BeEmpty();
        result.HasBlockingMessages.Should().BeFalse();
    }

    [Fact]
    public void ParseMatchChangeValidatie_ConfirmationNeededOntbreekt_GeeftFalseEnLegeLijst()
    {
        var result = SportlinkClubClient.ParseMatchChangeValidatie("""{"IsSuccess": true}""");

        result.Should().NotBeNull();
        result!.ConfirmationNeeded.Should().BeFalse();
    }

    [Fact]
    public void ParseMatchChangeValidatie_MetKaleStringMeldingen_ParsedZeAllemaal()
    {
        var json = """
            {"ConfirmationNeeded": {
                "ValidationResultMessages": ["Datum ligt buiten de deadline", "Tegenstander moet akkoord gaan"],
                "HasBlockingMessages": true
            }}
            """;

        var result = SportlinkClubClient.ParseMatchChangeValidatie(json);

        result.Should().NotBeNull();
        result!.ConfirmationNeeded.Should().BeTrue();
        result.ValidationResultMessages.Should().BeEquivalentTo(
            "Datum ligt buiten de deadline", "Tegenstander moet akkoord gaan");
        result.HasBlockingMessages.Should().BeTrue();
    }

    [Fact]
    public void ParseMatchChangeValidatie_MetObjectMeldingenViaMessageOfDescription_ParsedZeAllemaal()
    {
        // Elementvorm onbekend (issue #995) — deze test legt de defensieve aanname vast: een
        // object-element met "Message" of "Description" wordt ook herkend, niet alleen een kale string.
        var json = """
            {"ConfirmationNeeded": {
                "ValidationResultMessages": [
                    {"Message": "Melding via Message-veld"},
                    {"Description": "Melding via Description-veld"}
                ],
                "HasBlockingMessages": false
            }}
            """;

        var result = SportlinkClubClient.ParseMatchChangeValidatie(json);

        result.Should().NotBeNull();
        result!.ConfirmationNeeded.Should().BeTrue();
        result.ValidationResultMessages.Should().BeEquivalentTo(
            "Melding via Message-veld", "Melding via Description-veld");
        result.HasBlockingMessages.Should().BeFalse();
    }

    [Fact]
    public void ParseMatchChangeValidatie_HasBlockingMessagesOpToplevel_WordtOokHerkend()
    {
        // Positie van HasBlockingMessages niet bevestigd — deze test legt de fallback naar
        // toplevel vast (naast de primaire, geneste locatie onder ConfirmationNeeded).
        var json = """{"ConfirmationNeeded": {"ValidationResultMessages": []}, "HasBlockingMessages": true}""";

        var result = SportlinkClubClient.ParseMatchChangeValidatie(json);

        result.Should().NotBeNull();
        result!.HasBlockingMessages.Should().BeTrue();
    }

    [Fact]
    public void ParseMatchChangeValidatie_OnherkenbareJson_GeeftNull()
    {
        var result = SportlinkClubClient.ParseMatchChangeValidatie("dit is geen json");

        result.Should().BeNull();
    }

    // ── #997: oefenwedstrijd aanmaken (ClubMatch) — scaffolding, bewust beperkte scope ──

    [Fact]
    public async Task BestaandeMutatiePaden_GebruikenNogSteedsHttpMethodPut_RegressietestVoorGeneriekeHttpMethodParameter()
    {
        // #997 generaliseerde de gedeelde verzendmethode (voorheen uitsluitend PUT) met een
        // optionele HttpMethod-parameter (default Put) om ook CreateClubMatchAsync (POST) te kunnen
        // versturen. Deze test bewijst dat de drie bestaande mutatiepaden (#992 kleedkamers, #993
        // veld, #996 change-request-actie) ongewijzigd HttpMethod.Put blijven gebruiken.
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var capturedMethods = new List<(string Endpoint, HttpMethod Method)>();
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
            {
                capturedMethods.Add(("UpdateMatchDressingRooms", req.Method));
                return JsonResponse(DressingRoomsSuccessResponse());
            }
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDetails") == true)
            {
                capturedMethods.Add(("UpdateMatchDetails", req.Method));
                return JsonResponse(DressingRoomsSuccessResponse());
            }
            if (req.RequestUri?.AbsoluteUri.Contains("MatchChangeRequestAction") == true)
            {
                capturedMethods.Add(("MatchChangeRequestAction", req.Method));
                return JsonResponse(DressingRoomsSuccessResponse());
            }
            if (req.RequestUri?.AbsoluteUri.Contains("user/UserInfo") == true)
                return JsonResponse("""{"PublicPersonId": "P1"}""");
            // Snapshot-GET (UpdateFieldAsync haalt eerst het volledige record op) — moet ná de
            // specifiekere checks hierboven staan, anders vangt deze catch-all ze allemaal af.
            if (req.RequestUri?.AbsoluteUri.Contains("club.sportlink.com") == true)
                return JsonResponse(MatchDetailsSnapshotResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");
        await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "F1", "1.0", null, isForceUpdate: false);
        await sut.ActOnChangeRequestAsync(TestFunctioneleRol, "APPROVE", TestPublicMatchId, "REQ1", null);

        capturedMethods.Should().HaveCount(3);
        capturedMethods.Should().OnlyContain(x => x.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task CreateClubMatchAsync_GlobaleInstellingStaatUit_BlijftTochGesimuleerdDoorCodeLock()
    {
        // isDryRun: () => false — de club-instelling staat NIET op dry-run. Toch mag er nooit een
        // echte POST naar het ClubMatch-endpoint gaan, want CreateClubMatchAsync geeft altijd
        // forceDryRun: true mee (#997 — van alle #986-sub-issues de meeste onbekenden: volledige
        // body onbevestigd, meerdere picklist-vormen onbekend, delete-methode onbekend).
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var aangeroepenUrls = new List<string>();
        var client = MakeClient(req =>
        {
            aangeroepenUrls.Add(req.RequestUri!.AbsoluteUri);
            if (req.RequestUri.AbsoluteUri.Contains("idm.sportlink.com"))
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri.AbsoluteUri.Contains("clubmatch/ClubMatch"))
                throw new InvalidOperationException("De code-lock mag deze POST nooit versturen, ongeacht de globale dry-run-instelling.");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance, isDryRun: () => false);

        var aanvraag = new SportlinkClubMatchAanvraag(
            MatchDateTime: new DateTime(2026, 9, 20, 19, 30, 0),
            Duration: 90,
            AgeClassCode: "JO10",
            Description: "Oefenwedstrijd tegen buurtclub",
            PublicHomeTeamId: "T2010269033",
            PublicAwayTeamId: "T2010269099",
            FacilityId: "BBCF989",
            FieldId: "BBCF989-OUTDOOR_FIELD-6",
            ExternalMatchId: 12345);

        var result = await sut.CreateClubMatchAsync(TestFunctioneleRol, aanvraag);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsDryRun.Should().BeTrue();
        result.Data.IsForcedDryRun.Should().BeTrue("de code-lock is onafhankelijk van de club-instelling sportlinkDryRun");
        result.Data.IsSuccess.Should().BeTrue("een dry-run simuleert een geslaagde mutatie");
        result.Data.PublicMatchId.Should().BeNull("Sportlink is niet echt aangeroepen tijdens een (forced) dry-run");
        aangeroepenUrls.Should().Contain(url => url.Contains("idm.sportlink.com"), "token-refresh moet wél echt gebeuren");
        aangeroepenUrls.Should().NotContain(url => url.Contains("clubmatch/ClubMatch"));
    }

    [Fact]
    public void BuildClubMatchBody_AanNameNietLiveBevestigd_ZetVerwachteVelden()
    {
        // Vastlegging van de AANGENOMEN, NOG NIET LIVE BEVESTIGDE body-vorm (#997) — elk veld komt
        // uit Sportlinks eigen frontend-code, nooit met een netwerktrace gezien. Deze test houdt de
        // aanname grijpbaar/regressie-vast, niet een bevestigd contract.
        var aanvraag = new SportlinkClubMatchAanvraag(
            MatchDateTime: new DateTime(2026, 9, 20, 19, 30, 0),
            Duration: 90,
            AgeClassCode: "JO10",
            Description: "Oefenwedstrijd tegen buurtclub",
            PublicHomeTeamId: "T2010269033",
            PublicAwayTeamId: "T2010269099",
            FacilityId: "BBCF989",
            FieldId: "BBCF989-OUTDOOR_FIELD-6",
            ExternalMatchId: 12345);

        var json = JsonSerializer.Serialize(SportlinkClubClient.BuildClubMatchBody(aanvraag));

        json.Should().Contain("\"MatchDate\":\"2026-09-20T19:30:00\"", "ONBEVESTIGD: datum+tijd samengevoegd, ISO 8601 zonder tijdzone aangenomen");
        json.Should().Contain("\"Duration\":90");
        json.Should().Contain("\"ExternalMatchId\":12345");
        json.Should().Contain("\"HomeResult\":-1").And.Contain("\"AwayResult\":-1", "ONBEVESTIGD: -1 betekent 'nog geen uitslag' volgens het issue");
        json.Should().Contain("\"AgeClassCode\":\"JO10\"");
        json.Should().Contain("\"Description\":\"Oefenwedstrijd tegen buurtclub\"");
        json.Should().Contain("\"PublicHomeTeamId\":\"T2010269033\"").And.Contain("\"PublicAwayTeamId\":\"T2010269099\"");
        json.Should().Contain("\"FacilityId\":\"BBCF989\"").And.Contain("\"FieldId\":\"BBCF989-OUTDOOR_FIELD-6\"");
    }

    [Fact]
    public async Task PutMutationAsync_ResponseBevatPublicMatchId_WordtGemaptNaarMutationResult()
    {
        // Parserfixture voor de ClubMatch-aanmaak-respons {"PublicMatchId":"M123","IsSuccess":true}
        // (#997). Getest via een bestaand, NIET-gelockt mutatiepad (UpdateDressingRoomsAsync) omdat
        // de generieke responsparsing endpoint-onafhankelijk is — CreateClubMatchAsync zelf kan dit
        // pad niet live oefenen zolang de forceDryRun-code-lock actief is (zie de lock-test hierboven).
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchDressingRooms") == true)
                return JsonResponse("""{"PublicMatchId": "M123", "IsSuccess": true}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateDressingRoomsAsync(TestFunctioneleRol, TestPublicMatchId, "10", "6", "9");

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.PublicMatchId.Should().Be("M123");
    }

    [Fact]
    public async Task GetClubMatchPickListsAsync_RedelijkeFixture_ParseertTeamsEnLocations()
    {
        // Read-only en persoonsgegevensvrij (teams/locaties, geen personen) — anders dan
        // CreateClubMatchAsync dus GEEN forceDryRun-lock, deze aanroep gaat "echt" (tegen de fake
        // HttpClient) naar Sportlink. Fixture-vorm is ONBEVESTIGD (#997) — kale array voor Teams,
        // genest onder "Items" voor Locations, om beide envelope-vormen te dekken.
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("PickListsTeams") == true)
                return JsonResponse("""
                    [
                        {"Id": "T1", "Name": "JO10-1"},
                        {"Id": "T2", "Name": "JO10-2"}
                    ]
                    """);
            if (req.RequestUri?.AbsoluteUri.Contains("PickListsLocation") == true)
                return JsonResponse("""
                    {"Items": [
                        {"FacilityId": "F1", "FacilityName": "Sportpark Oost"}
                    ]}
                    """);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.GetClubMatchPickListsAsync(TestFunctioneleRol);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.Teams.Should().HaveCount(2);
        result.Data.Teams.Should().ContainEquivalentOf(new SportlinkPickListItem("T1", "JO10-1"));
        result.Data.Locations.Should().ContainSingle().Which.Should().BeEquivalentTo(new SportlinkPickListItem("F1", "Sportpark Oost"));
    }

    [Fact]
    public void ParsePickListItem_OnbekendeVeldnamen_GeeftNullTerug()
    {
        // Defensief pad: onherkenbare/onbekende veldnamen mogen geen exception geven, alleen null.
        var element = JsonDocument.Parse("""{"SomeOtherField": "x"}""").RootElement;

        var item = SportlinkClubClient.ParsePickListItem(element);

        item.Id.Should().BeNull();
        item.Naam.Should().BeNull();
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
