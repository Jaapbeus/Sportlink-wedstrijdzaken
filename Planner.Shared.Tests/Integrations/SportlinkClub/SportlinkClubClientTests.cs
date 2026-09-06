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

    private static string DressingRoomsViolationResponse(params string[] codes)
    {
        var items = string.Join(",", codes.Select(c => "{\"code\": \"" + c + "\"}"));
        return "{\"isSuccess\": false, \"entityViolation\": {\"violations\": [" + items + "]}}";
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
        result.Data.Violations.Should().ContainSingle().Which.Should().Be("INVALID_UPDATE_ACTION");
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

    [Fact]
    public async Task UpdateFieldAsync_HappyPath_RetourneertIsSuccessTrue()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchField") == true)
                return JsonResponse(DressingRoomsSuccessResponse());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "12345-1", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateFieldAsync_ZetJuisteBodyInclusiefIsForceUpdate()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        string? capturedBody = null;
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchField") == true)
            {
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(DressingRoomsSuccessResponse());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "12345-1", "1.0", 0, isForceUpdate: false);

        capturedBody.Should().Contain(TestPublicMatchId).And.Contain("12345-1").And.Contain("1.0").And.Contain("\"IsForceUpdate\":false");
    }

    [Fact]
    public async Task UpdateFieldAsync_SportlinkWijstMutatieAf_RetourneertOkMetIsSuccessFalseEnViolations()
    {
        var tokenStore = new FakeSportlinkClubTokenStore(FictieveRefreshToken);
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchField") == true)
                return JsonResponse(DressingRoomsViolationResponse("INVALID_UPDATE_ACTION"));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "12345-1", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.Ok);
        result.Data!.IsSuccess.Should().BeFalse();
        result.Data.Violations.Should().ContainSingle().Which.Should().Be("INVALID_UPDATE_ACTION");
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
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchField") == true)
            {
                putCallCount++;
                return putCallCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : JsonResponse(DressingRoomsSuccessResponse());
            }
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
        var client = MakeClient(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("idm.sportlink.com") == true)
                return JsonResponse(TokenResponse(FictieveAccessToken));
            if (req.RequestUri?.AbsoluteUri.Contains("UpdateMatchField") == true)
                throw new HttpRequestException("Netwerk down");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new SportlinkClubClient(client, tokenStore, NullLogger<SportlinkClubClient>.Instance);

        var result = await sut.UpdateFieldAsync(TestFunctioneleRol, TestPublicMatchId, "12345-1", "1.0", 0, isForceUpdate: false);

        result.Status.Should().Be(SportlinkClubCallStatus.NetwerkFout);
        result.Data.Should().BeNull();
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
